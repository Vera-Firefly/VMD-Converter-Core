using UnityHps;
namespace UnityHps;

sealed class Joint { public int HumanBone { get; set; } public double[] Pre { get; set; } = []; public double[] Post { get; set; } = []; public double[] LimitSign { get; set; } = []; }
sealed class UpperModel { public int Version { get; set; } public Rig ReferenceRig { get; set; } = new(); public int Neighbors { get; set; } public Joint[] Joints { get; set; } = []; public double[][][] Points { get; set; } = []; }
// Calibrated upper-limb response model used by HumanoidUpperProjector.
sealed class UpperProjection
{
    readonly UpperModel model; readonly Dictionary<int, int> bones; readonly Dictionary<int, Joint> joints; readonly Dictionary<int, Q> pre, post;
    public UpperProjection(UpperModel value)
    {
        if (value.Version != 1 || value.Neighbors < 6 || value.Points.Length != 2 || value.Points.Any(p => p.Length <= value.Neighbors || p.Any(r => r.Length != 12 || r.Any(x => !double.IsFinite(x))))) throw new ArgumentException("Invalid model");
        model = value; bones = value.ReferenceRig.bones.Select((b, i) => (b, i)).Where(x => x.b.humanBone >= 0).ToDictionary(x => x.b.humanBone, x => x.i);
        joints = value.Joints.ToDictionary(x => x.HumanBone); pre = value.Joints.ToDictionary(x => x.HumanBone, x => Q.Read(x.Pre)); post = value.Joints.ToDictionary(x => x.HumanBone, x => Q.Read(x.Post));
        foreach (int h in Enumerable.Range(11, 8)) if (!bones.ContainsKey(h) || !joints.ContainsKey(h)) throw new ArgumentException("Missing joint");
    }
    Q Frame(Q q, int h) => pre[h].Inverse * q * post[h];
    static double Wrap(double x) => x - 4 * Math.Floor((x + 2) / 4);
    void Channels(double[] output, Q q, int h, params int[] channels)
    {
        double[] xyz = [q.X, q.Y, q.Z]; foreach (int c in channels) { var m = model.ReferenceRig.muscles[c]; double angle = 2 * Math.Atan2(xyz[m.dof], q.W) * 180 / Math.PI * joints[h].LimitSign[m.dof]; output[c - 37] = angle / (angle < 0 ? -m.minimum : m.maximum); }
    }
    public double[] PredictContext(int side, double[] c)
    {
        var rotations = model.ReferenceRig.bones.SelectMany(b => b.rotation.Select(v => (double)v)).ToArray();
        double rad = Math.PI / 180; Q[] human = [new Q(Math.Sin(c[0] * rad / 2), 0, 0, Math.Cos(c[0] * rad / 2)), new Q(0, Math.Tan(c[1] * rad / 2), Math.Tan(c[2] * rad / 2), 1).Normalized, new Q(0, Math.Tan(c[3] * rad / 2), Math.Tan(c[4] * rad / 2), 1).Normalized, new Q(0, 0, 0, 1)];
        for (int j = 0; j < 4; j++) { int h = 11 + 2 * j + side; Q q = pre[h] * human[j] * post[h].Inverse; int i = bones[h] * 4; rotations[i] = q.X; rotations[i + 1] = q.Y; rotations[i + 2] = q.Z; rotations[i + 3] = q.W; }
        return Solve(rotations).Skip(9 * side).Take(9).ToArray();
    }
    public double[] Solve(double[] rotations)
    {
        if (rotations.Length != model.ReferenceRig.bones.Length * 4) throw new ArgumentException("Wrong bone count"); var output = new double[18];
        for (int side = 0; side < 2; side++)
        {
            int s = 11 + side, u = 13 + side, f = 15 + side, h = 17 + side, o = 9 * side;
            Q qs = Q.Read(rotations.AsSpan(bones[s] * 4, 4)), qu = Q.Read(rotations.AsSpan(bones[u] * 4, 4)), qf = Q.Read(rotations.AsSpan(bones[f] * 4, 4)), qh = Q.Read(rotations.AsSpan(bones[h] * 4, 4));
            Q ss = Frame(qs, s).Swing, projected = pre[s] * ss * post[s].Inverse, corrected = projected.Inverse * qs * qu;
            Q own = Frame(qu, u).Twist, effective = Frame(corrected, u), difference = effective.Twist * own.Inverse;
            Q transported = post[u] * difference * post[u].Inverse, hh = Frame(qh, h), th = hh.Twist;
            var (a, b, t, uc, hc) = Query(transported * qf * pre[h] * th * pre[h].Inverse, side);
            output[41 + o - 37] = Wrap(2 * Math.Atan2(own.X, own.W) * joints[u].LimitSign[0] * 2 / Math.PI + a); output[42 + o - 37] = t; output[43 + o - 37] = b;
            Q combined = hc * (th.Inverse * hh);
            Channels(output, ss, s, 37 + o, 38 + o); Channels(output, (effective.Swing * uc).Swing, u, 39 + o, 40 + o); Channels(output, combined.Twist.Inverse * combined, h, 44 + o, 45 + o);
        }
        return output;
    }
    (double, double, double, Q, Q) Query(Q folded, int side)
    {
        int h = 15 + side; Q human = Frame(folded.Normalized, h), twist = human.Twist; double phase = 2 * Math.Atan2(twist.X, twist.W) * joints[h].LimitSign[0];
        Q axis = human * new Q(1, 0, 0, 0) * human.Inverse; double[] p = [axis.X, axis.Y, axis.Z]; var nearest = new PriorityQueue<(int Index, double Distance), double>(); var points = model.Points[side];
        for (int i = 0; i < points.Length; i++) { var row = points[i]; double d = Math.Max(0, 1 - row[0] * p[0] - row[1] * p[1] - row[2] * p[2]); if (nearest.Count < model.Neighbors) nearest.Enqueue((i, d), -d); else if (d < nearest.Peek().Distance) { nearest.Dequeue(); nearest.Enqueue((i, d), -d); } }
        var selected = nearest.UnorderedItems.Select(x => x.Element).ToArray(); double bandwidth = Math.Max(selected.Max(x => x.Distance), 1e-12);
        var anchor = new double[3]; anchor[Enumerable.Range(0, 3).MinBy(i => Math.Abs(p[i]))] = 1; var a = Cross(p, anchor); double length = Math.Sqrt(a.Sum(x => x * x)); for (int i = 0; i < 3; i++) a[i] /= length; var b = Cross(p, a); var system = new double[6, 15];
        foreach (var row in selected) { var point = points[row.Index]; double x = 0, y = 0; for (int j = 0; j < 3; j++) { x += (point[j] - p[j]) * a[j]; y += (point[j] - p[j]) * b[j]; } double[] design = [1, x, y, x * x, x * y, y * y]; double w = Math.Exp(-row.Distance / bandwidth); for (int i = 0; i < 6; i++) { for (int j = 0; j < 6; j++) system[i, j] += w * design[i] * design[j]; for (int j = 0; j < 9; j++) system[i, j + 6] += w * design[i] * point[j + 3]; } }
        for (int i = 0; i < 6; i++) system[i, i] += i == 0 ? 1e-10 : 1e-8;
        for (int c = 0; c < 6; c++) { int pivot = c; for (int i = c + 1; i < 6; i++) if (Math.Abs(system[i, c]) > Math.Abs(system[pivot, c])) pivot = i; if (Math.Abs(system[pivot, c]) < 1e-20) throw new InvalidDataException("Singular fit"); if (pivot != c) for (int j = c; j < 15; j++) (system[c, j], system[pivot, j]) = (system[pivot, j], system[c, j]); double divisor = system[c, c]; for (int j = c; j < 15; j++) system[c, j] /= divisor; for (int i = 0; i < 6; i++) if (i != c) { double v = system[i, c]; for (int j = c; j < 15; j++) system[i, j] -= v * system[c, j]; } }
        double angle = -phase / joints[h].LimitSign[0], cos = Math.Cos(angle), sin = Math.Sin(angle), hy = system[0, 13] * cos - system[0, 14] * sin, hz = system[0, 13] * sin + system[0, 14] * cos, beta = phase + Math.Atan2(system[0, 9], system[0, 8]);
        return (Math.Atan2(system[0, 7], system[0, 6]) * 2 / Math.PI, Math.Atan2(Math.Sin(beta), Math.Cos(beta)) * 2 / Math.PI, system[0, 10], new Q(0, system[0, 11], system[0, 12], 1).Normalized, new Q(0, hy, hz, 1).Normalized);
    }
    static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
}
readonly record struct Q(double X, double Y, double Z, double W)
{
    public static Q Read(ReadOnlySpan<double> q) { if (q.Length != 4) throw new ArgumentException("Wrong quaternion size"); foreach (double v in q) if (!double.IsFinite(v)) throw new ArgumentException("Nonfinite quaternion"); return new Q(q[0], q[1], q[2], q[3]).Normalized; }
    public Q Normalized { get { double n = Math.Sqrt(X * X + Y * Y + Z * Z + W * W); if (n < 1e-12) throw new ArgumentException("Zero quaternion"); return new(X / n, Y / n, Z / n, W / n); } }
    public Q Inverse => new(-X, -Y, -Z, W);
    public Q Twist { get { double n = Math.Sqrt(X * X + W * W); if (n < 1e-12) return new Q(0, 0, 0, 1); return new(X / n, 0, 0, W / n); } }
    public Q Swing => this * Twist.Inverse;
    public static Q operator *(Q a, Q b) => new(a.W * b.X + b.W * a.X + a.Y * b.Z - a.Z * b.Y, a.W * b.Y + b.W * a.Y + a.Z * b.X - a.X * b.Z, a.W * b.Z + b.W * a.Z + a.X * b.Y - a.Y * b.X, a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
}
