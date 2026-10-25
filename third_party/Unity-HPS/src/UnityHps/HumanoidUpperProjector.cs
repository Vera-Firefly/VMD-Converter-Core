using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using UnityHps;

namespace UnityHps;

/// <summary>Calibrated anatomical transport and upper-limb muscle projection. Inputs and model data remain owned by their callers.</summary>
public sealed class HumanoidUpperProjector
{
    readonly UpperModel model;
    readonly UpperProjection baseline;
    readonly Dictionary<int, int> bones;
    readonly Dictionary<int, Joint> joints;
    readonly Dictionary<int, Q> pre, post;
    readonly double[][][] residual = new double[2][][];
    readonly double[] ranges = new double[5];
    readonly int count;
    readonly SourcePoseAdapter adapter;

    public HumanoidUpperProjector(Rig rig, Rig? bindPose = null)
    {
        var shared = Shared.Value;
        model = shared.model; residual = shared.residual; ranges = shared.ranges; count = shared.count;
        baseline = new UpperProjection(model);
        adapter = new SourcePoseAdapter(rig, bindPose ?? new Rig { bones = rig.bindBones.Length == 0 ? rig.bones : rig.bindBones }, new Rig { bones = model.ReferenceRig.bindBones });
        bones = model.ReferenceRig.bones.Select((b, i) => (b, i)).Where(x => x.b.humanBone >= 0).ToDictionary(x => x.b.humanBone, x => x.i);
        joints = model.Joints.ToDictionary(j => j.HumanBone);
        pre = model.Joints.ToDictionary(j => j.HumanBone, j => Q.Read(j.Pre));
        post = model.Joints.ToDictionary(j => j.HumanBone, j => Q.Read(j.Post));
        var slots = rig.muscles.Select((m, i) => (m, i)).ToDictionary(x => x.m.unityIndex, x => x.i);
        outputSlots = Enumerable.Range(37, 18).Select(c => slots.TryGetValue(c, out int i) ? i : throw new InvalidDataException("Missing upper muscle channel " + c)).ToArray();
        outputLimits = outputSlots.Select(i => rig.muscles[i]).ToArray();
        muscleCount = rig.muscles.Length;
    }

    readonly int[] outputSlots;
    readonly Muscle[] outputLimits;
    readonly int muscleCount;
    static readonly Lazy<(UpperModel model, double[][][] residual, double[] ranges, int count)> Shared = new(Load);
    static Stream Resource(string name)
    {
        var input = typeof(HumanoidUpperProjector).Assembly.GetManifestResourceStream("UnityHps.Upper." + name)
            ?? throw new InvalidOperationException("Missing upper response resource: " + name);
        return new GZipStream(input, CompressionMode.Decompress);
    }
    static (UpperModel, double[][][], double[], int) Load()
    {
        using var input = Resource("response.json.gz");
        var model = JsonSerializer.Deserialize(input, UpperProjectionJsonContext.Default.UpperModel)
            ?? throw new InvalidDataException("Empty upper response model");
        using var reader = new BinaryReader(Resource("residual.bin.gz"));
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported upper residual model");
        int count = reader.ReadInt32();
        if (count < 22 || count > 100000) throw new InvalidDataException("Invalid upper neighbor count");
        var ranges = Enumerable.Range(0, 5).Select(_ => reader.ReadDouble()).ToArray();
        if (ranges.Any(x => !double.IsFinite(x) || x <= 0)) throw new InvalidDataException("Invalid upper parameter range");
        var residual = new double[2][][];
        for (int side = 0; side < 2; side++)
        {
            int rows = reader.ReadInt32();
            if (rows < count || rows > 1000000) throw new InvalidDataException("Invalid upper response table size");
            residual[side] = new double[rows][];
            for (int i = 0; i < rows; i++)
            {
                residual[side][i] = Enumerable.Range(0, 16).Select(_ => reader.ReadDouble()).ToArray();
                if (residual[side][i].Any(x => !double.IsFinite(x))) throw new InvalidDataException("Nonfinite upper response");
            }
        }
        return (model, residual, ranges, count);
    }

    /// <summary>Return a new muscle array with channels 37..54 projected; all other channels keep their initial values.</summary>
    public float[] Project(PoseFrame frame, float[] initialMuscles)
    {
        if (initialMuscles.Length != muscleCount || initialMuscles.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Initial muscle array does not match the rig");
        var output = ProjectUpper(frame);
        var result = (float[])initialMuscles.Clone();
        for (int i = 0; i < 18; i++)
        {
            var reference = model.ReferenceRig.muscles[37 + i];
            double angle = output[i] * (output[i] < 0 ? -reference.minimum : reference.maximum);
            double limit = angle < 0 ? -outputLimits[i].minimum : outputLimits[i].maximum;
            if (!(limit > 0)) throw new InvalidDataException("Upper muscle limits must straddle zero");
            result[outputSlots[i]] = (float)(angle / limit);
        }
        return result;
    }

    Q Frame(Q q, int h) => pre[h].Inverse * q * post[h];
    static double Wrap(double v) => v - 4 * Math.Floor((v + 2) / 4);
    double Muscle(double angle, int channel) => angle / (angle < 0 ? -model.ReferenceRig.muscles[channel].minimum : model.ReferenceRig.muscles[channel].maximum);
    double Angle(Q q, int channel, int h)
    {
        var m = model.ReferenceRig.muscles[channel];
        double v = m.dof == 1 ? q.Y : q.Z;
        return Muscle(2 * Math.Atan2(v, q.W) * 180 / Math.PI * joints[h].LimitSign[m.dof], channel);
    }
    Q Swing(double[] v, int c1, int c2, int h)
    {
        double[] t = new double[3];
        foreach (int c in new[] { c1, c2 })
        {
            var m = model.ReferenceRig.muscles[c];
            double angle = v[c - 37] * (v[c - 37] < 0 ? -m.minimum : m.maximum) * joints[h].LimitSign[m.dof];
            t[m.dof] = Math.Tan(angle * Math.PI / 360);
        }
        return new Q(0, t[1], t[2], 1).Normalized;
    }
    double[] Encode(double[] v, int side)
    {
        int o = 9 * side;
        double a = v[4 + o] * Math.PI / 2, b = v[6 + o] * Math.PI / 2;
        Q u = Swing(v, 39 + o, 40 + o, 13 + side), h = Swing(v, 44 + o, 45 + o, 17 + side);
        return [Math.Cos(a), Math.Sin(a), Math.Cos(b), Math.Sin(b), v[5 + o], u.Y, u.Z, u.W, h.Y, h.Z, h.W];
    }
    double[] Decode(double[] y, int side)
    {
        int o = 9 * side;
        var v = new double[18];
        v[4 + o] = Math.Atan2(y[1], y[0]) * 2 / Math.PI;
        v[6 + o] = Math.Atan2(y[3], y[2]) * 2 / Math.PI;
        v[5 + o] = y[4];
        var u = new Q(0, y[5], y[6], y[7]);
        var h = new Q(0, y[8], y[9], y[10]);
        foreach (int c in new[] { 39 + o, 40 + o }) v[c - 37] = Angle(u, c, 13 + side);
        foreach (int c in new[] { 44 + o, 45 + o }) v[c - 37] = Angle(h, c, 17 + side);
        return v;
    }

    private double[] ProjectUpper(PoseFrame frame)
    {
        double[] raw = adapter.Map(frame);
        var output = baseline.Solve(raw);
        for (int side = 0; side < 2; side++)
        {
            int s = 11 + side, u = 13 + side, f = 15 + side, h = 17 + side, o = 9 * side;
            Q Read(int human) => Q.Read(raw.AsSpan(bones[human] * 4, 4));
            Q ts = Frame(Read(s), s).Twist, uh = Frame(Read(u), u), hh = Frame(Read(h), h), th = hh.Twist;
            Q folded = (Read(f) * pre[h] * th * pre[h].Inverse).Normalized;
            Q fh = Frame(folded, f), su = uh.Swing, sf = fh.Swing, tu = uh.Twist, tf = fh.Twist;
            double a = 2 * Math.Atan2(ts.X, ts.W);
            a -= 2 * Math.PI * Math.Floor((a + Math.PI) / (2 * Math.PI));
            double[] context = [a, 2 * Math.Atan2(su.Y, su.W), 2 * Math.Atan2(su.Z, su.W), 2 * Math.Atan2(sf.Y, sf.W), 2 * Math.Atan2(sf.Z, sf.W)];
            for (int i = 0; i < 5; i++) context[i] *= 180 / Math.PI;
            var normalized = new double[18];
            Array.Copy(baseline.PredictContext(side, context), 0, normalized, o, 9);
            double[] y = Encode(normalized, side), query = context.Select((v, i) => v / ranges[i]).ToArray();
            double[] correction = Fit(query, side);
            for (int i = 0; i < y.Length; i++) y[i] += correction[i];
            double[] native = Decode(y, side);
            double ownU = 2 * Math.Atan2(tu.X, tu.W) * joints[u].LimitSign[0] * 2 / Math.PI;
            double ownF = 2 * Math.Atan2(tf.X, tf.W) * joints[f].LimitSign[0] * 2 / Math.PI;
            for (int i = 2; i <= 6; i++) output[o + i] = native[o + i];
            output[o + 4] = Wrap(native[o + 4] + ownU);
            Q gh = Swing(native, 44 + o, 45 + o, h);
            double phi = -ownF * Math.PI / 2 / joints[f].LimitSign[0];
            gh = new Q(0, gh.Y * Math.Cos(phi) - gh.Z * Math.Sin(phi), gh.Y * Math.Sin(phi) + gh.Z * Math.Cos(phi), gh.W).Normalized;
            Q compound = gh * (th.Inverse * hh), extra = compound.Twist, final = extra.Inverse * compound;
            double handTwist = 2 * Math.Atan2(extra.X, extra.W) * joints[f].LimitSign[0] * 2 / Math.PI;
            output[o + 6] = Wrap(native[o + 6] + ownF + handTwist);
            foreach (int c in new[] { 44 + o, 45 + o }) output[c - 37] = Angle(final, c, h);
        }
        if (output.Any(x => !double.IsFinite(x))) throw new InvalidDataException("Upper projection produced a non-finite muscle.");
        return output;
    }

    double[] Fit(double[] query, int side)
    {
        var points = residual[side];
        var nearest = new PriorityQueue<(int Index, double Distance), double>();
        for (int i = 0; i < points.Length; i++)
        {
            double d = 0;
            for (int j = 0; j < 5; j++) { double v = points[i][j] - query[j]; d += v * v; }
            if (nearest.Count < count) nearest.Enqueue((i, d), -d);
            else if (d < nearest.Peek().Distance) { nearest.Dequeue(); nearest.Enqueue((i, d), -d); }
        }
        var selected = nearest.UnorderedItems.Select(x => x.Element).ToArray();
        double bandwidth = Math.Max(selected.Max(x => x.Distance), 1e-12);
        var designs = new double[selected.Length][];
        var distanceWeights = new double[selected.Length];
        var beta = new double[21, 11];
        for (int j = 0; j < 11; j++)
        {
            var values = selected.Select(r => points[r.Index][j + 5]).Order().ToArray();
            beta[0, j] = (values[(values.Length - 1) / 2] + values[values.Length / 2]) * .5;
        }
        for (int n = 0; n < selected.Length; n++)
        {
            var row = selected[n];
            double[] p = points[row.Index], design = new double[21];
            design[0] = 1;
            for (int j = 0; j < 5; j++) design[j + 1] = p[j] - query[j];
            int k = 6;
            for (int i = 0; i < 5; i++) for (int j = i; j < 5; j++) design[k++] = design[i + 1] * design[j + 1];
            designs[n] = design; distanceWeights[n] = Math.Exp(-row.Distance / bandwidth);
        }
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var matrix = new double[21, 32];
            for (int n = 0; n < selected.Length; n++)
            {
                var design = designs[n]; var p = points[selected[n].Index]; double error = 0;
                for (int j = 0; j < 11; j++)
                {
                    double prediction = 0; for (int i = 0; i < 21; i++) prediction += design[i] * beta[i, j];
                    double e = prediction - p[j + 5]; error += e * e;
                }
                double w = distanceWeights[n] * Math.Min(1, .005 / Math.Max(Math.Sqrt(error), 1e-12));
                for (int i = 0; i < 21; i++)
                {
                    for (int j = 0; j < 21; j++) matrix[i, j] += w * design[i] * design[j];
                    for (int j = 0; j < 11; j++) matrix[i, j + 21] += w * design[i] * p[j + 5];
                }
            }
            for (int i = 0; i < 21; i++) matrix[i, i] += i == 0 ? 1e-10 : i < 6 ? 1e-8 : 1e-7;
            for (int c = 0; c < 21; c++)
            {
                int pivot = c;
                for (int i = c + 1; i < 21; i++) if (Math.Abs(matrix[i, c]) > Math.Abs(matrix[pivot, c])) pivot = i;
                if (Math.Abs(matrix[pivot, c]) < 1e-20) throw new InvalidDataException("Singular robust fit.");
                if (pivot != c) for (int j = c; j < 32; j++) (matrix[c, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[c, j]);
                double divisor = matrix[c, c]; for (int j = c; j < 32; j++) matrix[c, j] /= divisor;
                for (int i = 0; i < 21; i++) if (i != c) { double v = matrix[i, c]; for (int j = c; j < 32; j++) matrix[i, j] -= v * matrix[c, j]; }
            }
            for (int i = 0; i < 21; i++) for (int j = 0; j < 11; j++) beta[i, j] = matrix[i, j + 21];
        }
        return Enumerable.Range(0, 11).Select(j => beta[0, j]).ToArray();
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpperModel))]
internal partial class UpperProjectionJsonContext : JsonSerializerContext { }
