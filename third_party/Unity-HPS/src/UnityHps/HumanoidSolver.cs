using System.Diagnostics;
using System.Numerics;

namespace UnityHps;

/// <summary>
/// Standalone calibrated HumanPose approximation, not Unity's native solver.
/// Nonlinear quaternion lattice interpolation replaces signed-axis log addition.
/// Calibration is read-only and shared across workers; fitting state is per frame.
/// </summary>
public sealed class HumanoidSolver
{
    private readonly Rig rig;
    private readonly SkeletonPose neutral;
    private readonly Dictionary<MuscleGroup, float[]> negativeRanges = new();
    private readonly bool[] reverseComposition;
    private readonly MuscleGroup[][] components;
    private readonly Dictionary<MuscleGroup, int[]> rotationBones = new();
    private readonly Dictionary<MuscleGroup, (Quaternion[] rotations, Vector3[] positions)> latticeCache = new();
    public HumanoidSolver(Rig rig)
    {
        Validate(rig);
        this.rig = rig;
        neutral = new SkeletonPose(rig);
        reverseComposition = Enumerable.Range(0, rig.bones.Length).Select(rig.reverseCompositionBones.Contains).ToArray();
        foreach (var group in rig.groups)
        {
            int boneCount = group.bones.Length;
            var cachedRotations = new Quaternion[group.samples.Length * boneCount];
            var cachedPositions = new Vector3[cachedRotations.Length];
            for (int b = 0; b < boneCount; b++)
            {
                Quaternion anchor = Math3.Q(group.samples[(group.samples.Length - 1) / 2].rotations, b * 4);
                for (int s = 0; s < group.samples.Length; s++)
                {
                    Quaternion q = Math3.Q(group.samples[s].rotations, b * 4);
                    cachedRotations[s * boneCount + b] = Quaternion.Dot(q, anchor) < 0 ? -q : q;
                    cachedPositions[s * boneCount + b] = Math3.V(group.samples[s].positions, b * 3);
                }
            }
            latticeCache.Add(group, (cachedRotations, cachedPositions));
            var ratios = new float[group.muscles.Length];
            int center = (group.samples.Length - 1) / 2, stride = 1;
            for (int d = 0; d < ratios.Length; d++)
            {
                float positive = 0, negative = 0;
                for (int i = 0; i < group.bones.Length; i++)
                {
                    if (group.bones[i] == rig.hips) continue;
                    Quaternion q = Math3.Q(group.samples[center].rotations, i * 4);
                    positive = Math.Max(positive, Math3.Angle(q, Math3.Q(group.samples[center + stride * (group.resolution / 2)].rotations, i * 4)));
                    negative = Math.Max(negative, Math3.Angle(q, Math3.Q(group.samples[center - stride * (group.resolution / 2)].rotations, i * 4)));
                }
                ratios[d] = positive > 1e-4f && negative > 1e-4f ? negative / positive : 1;
                stride *= group.resolution;
            }
            negativeRanges.Add(group, ratios);
            rotationBones.Add(group, group.bones.Where(b => b != rig.hips && rig.bones[b].parent >= 0 &&
                group.samples.Any(s => Math3.Angle(Math3.Q(rig.bones[b].rotation), Math3.Q(s.rotations, Array.IndexOf(group.bones, b) * 4)) > 0.001f)).ToArray());
        }
        var remaining = new HashSet<MuscleGroup>(rig.groups);
        var blocks = new List<MuscleGroup[]>();
        while (remaining.Count > 0)
        {
            var block = new HashSet<MuscleGroup> { remaining.First() }; remaining.ExceptWith(block);
            bool changed;
            do
            {
                var affected = new HashSet<int>(block.SelectMany(g => rotationBones[g]));
                var neighbors = remaining.Where(g => rotationBones[g].Any(affected.Contains)).ToArray();
                changed = neighbors.Length > 0; block.UnionWith(neighbors); remaining.ExceptWith(neighbors);
            } while (changed);
            blocks.Add(rig.groups.Where(block.Contains).ToArray());
        }
        components = blocks.ToArray();
    }

    public static void Validate(Rig rig)
    {
        if (rig.version != 1 || rig.bones.Length == 0 || rig.hips < 0 || rig.hips >= rig.bones.Length ||
            !float.IsFinite(rig.humanScale) || rig.humanScale <= 0)
            throw new InvalidDataException("Unsupported rig schema, missing Hips, or invalid humanScale.");
        for (int i = 0; i < rig.bones.Length; i++)
        {
            if (rig.bones[i].parent < -1 || rig.bones[i].parent >= i)
                throw new InvalidDataException("Rig bones must be parent-before-child with valid indices.");
            if (rig.bones[i].position.Length != 3 || rig.bones[i].rotation.Length != 4)
                throw new InvalidDataException("Invalid rest transform.");
        }
        if (rig.bodyPositionJacobian.Length != 0 && rig.bodyPositionJacobian.Length != rig.bones.Length * 9)
            throw new InvalidDataException("Invalid bodyPositionJacobian length.");
        if (rig.bodyPositionWeights.Length != 0 && rig.bodyPositionWeights.Length != rig.bones.Length)
            throw new InvalidDataException("Invalid bodyPositionWeights length.");
        foreach (var group in rig.groups)
        {
            int n = group.muscles.Length, size = group.resolution;
            if (n < 1 || n > 3 || size < 3 || size > 33 || (size & 1) == 0 ||
                group.muscles.Distinct().Count() != n ||
                group.muscles.Any(m => m < 0 || m >= rig.muscles.Length) ||
                group.bones.Distinct().Count() != group.bones.Length ||
                group.bones.Any(b => b < 0 || b >= rig.bones.Length) ||
                group.samples.Length != (int)Math.Pow(size, n))
                throw new InvalidDataException("Invalid muscle lattice.");
            foreach (var sample in group.samples)
                if (sample.positions.Length != group.bones.Length * 3 || sample.rotations.Length != group.bones.Length * 4)
                    throw new InvalidDataException("Invalid lattice sample shape.");
        }
    }

    /// <summary>Evaluate the calibrated neutral-body pose for muscle values in [-1,1].</summary>
    public SkeletonPose Decode(float[] muscles, HumanPose? root = null)
    {
        if (muscles.Length != rig.muscles.Length || muscles.Any(m => !float.IsFinite(m) || m < -1 || m > 1))
            throw new InvalidDataException("Muscles must match the rig and lie within calibrated [-1,1].");
        var pose = new SkeletonPose(rig);
        foreach (var group in rig.groups)
        {
            var values = group.muscles.Select(i => muscles[i]).ToArray();
            Interpolate(group, values, out var rotations, out var positions);
            for (int j = 0; j < group.bones.Length; j++)
            {
                int b = group.bones[j];
                Quaternion delta = Quaternion.Inverse(neutral.LocalRotations[b]) * rotations[j];
                pose.LocalRotations[b] = Quaternion.Normalize(reverseComposition[b]
                    ? neutral.LocalRotations[b] * delta * Quaternion.Inverse(neutral.LocalRotations[b]) * pose.LocalRotations[b]
                    : pose.LocalRotations[b] * delta);
                pose.LocalPositions[b] += positions[j] - neutral.LocalPositions[b];
            }
        }
        pose.UpdateWorld();
        if (root != null || rig.bodyOrientationMode == "landmarks")
        {
            Quaternion currentRotation = rig.bodyOrientationMode == "landmarks"
                ? Quaternion.Normalize(BodyOrientation(pose) * Quaternion.Inverse(BodyOrientation(neutral)) * Math3.Q(rig.bodyRotation))
                : Math3.Q(rig.bodyRotation);
            Quaternion rotation = Math3.Q(root?.bodyRotation ?? rig.bodyRotation) * Quaternion.Inverse(currentRotation);
            Vector3 oldCenter = Math3.V(rig.bodyPosition) * rig.humanScale;
            if (rig.bodyPositionWeights.Length > 0)
            {
                oldCenter = Vector3.Zero;
                for (int b = 0; b < rig.bones.Length; b++) oldCenter += pose.WorldPositions[b] * rig.bodyPositionWeights[b];
            }
            Vector3 newCenter = Math3.V(root?.bodyPosition ?? rig.bodyPosition) * rig.humanScale;
            // HumanPose moves Hips inside an unchanged Animator root. Applying
            // this to the scene root would produce different local transforms.
            int h = rig.hips, parent = rig.bones[h].parent;
            Vector3 hipsWorld = newCenter + Math3.Rotate(rotation, pose.WorldPositions[h] - oldCenter);
            Quaternion hipsRotation = Quaternion.Normalize(rotation * pose.WorldRotations[h]);
            pose.LocalPositions[h] = parent < 0 ? hipsWorld : Math3.Rotate(Quaternion.Inverse(pose.WorldRotations[parent]), hipsWorld - pose.WorldPositions[parent]);
            pose.LocalRotations[h] = parent < 0 ? hipsRotation : Quaternion.Normalize(Quaternion.Inverse(pose.WorldRotations[parent]) * hipsRotation);
            pose.UpdateWorld();
        }
        return pose;
    }

    public SolveResult Solve(PoseFrame frame, int passes = 2)
    {
        if (!float.IsFinite(frame.time) || frame.time < 0) throw new InvalidDataException("Invalid frame time.");
        var desired = new SkeletonPose(rig, frame);
        float[] ikErrors = frame.goals.Select(g => TwoBoneIk.Solve(desired, g)).ToArray();
        var muscles = new float[rig.muscles.Length];
        // Coordinate LM over complete multi-axis quaternion responses. Cross-bone
        // twist distribution is included through each group's observed bones.
        for (int pass = 0; pass < passes; pass++)
            foreach (var component in components)
                FitGroup(desired, muscles, component);
        SkeletonPose decoded = Decode(muscles);
        Quaternion rootDelta = Quaternion.Normalize(desired.WorldRotations[rig.hips] *
                                                    Quaternion.Inverse(decoded.WorldRotations[rig.hips]));
        Quaternion bodyRotation = Quaternion.Normalize(rootDelta * Math3.Q(rig.bodyRotation));
        if (rig.bodyOrientationMode == "landmarks")
            bodyRotation = EstimateBodyRotation(desired);
        Vector3 bodyPosition;
        if (rig.bodyPositionWeights.Length != 0)
        {
            double x = 0, y = 0, z = 0;
            for (int b = 0; b < rig.bones.Length; b++)
            {
                double weight = rig.bodyPositionWeights[b]; Vector3 p = desired.WorldPositions[b];
                x += weight * p.X; y += weight * p.Y; z += weight * p.Z;
            }
            bodyPosition = new Vector3((float)(x / rig.humanScale), (float)(y / rig.humanScale), (float)(z / rig.humanScale));
        }
        else if (rig.bodyPositionJacobian.Length != 0)
        {
            // GetHumanPose's measured world-position response: do not use Hips
            // local axes or assume RootT is simply Hips/humanScale.
            double x = rig.bodyPosition[0], y = rig.bodyPosition[1], z = rig.bodyPosition[2];
            for (int b = 0; b < rig.bones.Length; b++)
            {
                Vector3 d = desired.WorldPositions[b] - neutral.WorldPositions[b];
                int o = b * 9; var j = rig.bodyPositionJacobian;
                x += (double)j[o] * d.X + (double)j[o + 1] * d.Y + (double)j[o + 2] * d.Z;
                y += (double)j[o + 3] * d.X + (double)j[o + 4] * d.Y + (double)j[o + 5] * d.Z;
                z += (double)j[o + 6] * d.X + (double)j[o + 7] * d.Y + (double)j[o + 8] * d.Z;
            }
            bodyPosition = new Vector3((float)x, (float)y, (float)z);
        }
        else
        {
            Vector3 center = Math3.V(rig.bodyPosition) * rig.humanScale;
            bodyPosition = (desired.WorldPositions[rig.hips] -
                Math3.Rotate(rootDelta, decoded.WorldPositions[rig.hips] - center)) / rig.humanScale;
        }
        var human = new HumanPose { bodyPosition = Math3.A(bodyPosition), bodyRotation = Math3.A(bodyRotation), muscles = muscles };
        float maxError = 0;
        for (int b = 0; b < rig.bones.Length; b++)
        {
            if (b == rig.hips || rig.bones[b].humanBone < 0) continue;
            maxError = Math.Max(maxError, Math3.Angle(decoded.LocalRotations[b], desired.LocalRotations[b]));
        }
        PoseFrame solved = desired.ToFrame(frame.time);
        return new SolveResult { time = frame.time, humanPose = human, maxFitErrorDegrees = maxError,
            ikPositionErrors = ikErrors, positions = solved.positions, rotations = solved.rotations };
    }

    public ResultBatch SolveBatch(PoseBatch input, int threads = 0, int passes = 2, TaskScheduler? scheduler = null)
    {
        if (threads < 0 || passes < 1 || passes > 20) throw new ArgumentOutOfRangeException("Invalid threads/passes.");
        if (input.version != 1 || input.frames.Length == 0) throw new InvalidDataException("No input frames or unsupported schema.");
        for (int i = 1; i < input.frames.Length; i++)
            if (input.frames[i].time < input.frames[i - 1].time) throw new InvalidDataException("Frame times must be sorted.");
        var watch = Stopwatch.StartNew();
        var output = new SolveResult[input.frames.Length];
        Parallel.For(0, output.Length, new ParallelOptions { MaxDegreeOfParallelism = threads == 0 ? Environment.ProcessorCount : threads,
                TaskScheduler = scheduler ?? TaskScheduler.Default },
            i => output[i] = Solve(input.frames[i], passes));
        // Independent frame jobs preserve quaternion hemisphere continuity here.
        for (int i = 1; i < output.Length; i++)
            if (Quaternion.Dot(Math3.Q(output[i - 1].humanPose.bodyRotation), Math3.Q(output[i].humanPose.bodyRotation)) < 0)
                output[i].humanPose.bodyRotation = Math3.A(-Math3.Q(output[i].humanPose.bodyRotation));
        return new ResultBatch { elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, frames = output };
    }

    public AccuracyReport Compare(PoseBatch input, ResultBatch actual)
    {
        if (input.frames.Length != actual.frames.Length) throw new InvalidDataException("Reference frame count differs.");
        var report = new AccuracyReport(); double squares = 0;
        for (int i = 0; i < input.frames.Length; i++)
        {
            HumanPose? expected = input.frames[i].reference;
            if (expected == null) continue;
            HumanPose value = actual.frames[i].humanPose;
            if (expected.muscles.Length != rig.muscles.Length) throw new InvalidDataException("Reference muscle count differs.");
            float distance = Vector3.Distance(Math3.V(value.bodyPosition), Math3.V(expected.bodyPosition)) * rig.humanScale;
            report.maxRootPositionMeters = Math.Max(report.maxRootPositionMeters, distance);
            report.maxRootRotationDegrees = Math.Max(report.maxRootRotationDegrees, Math3.Angle(Math3.Q(value.bodyRotation), Math3.Q(expected.bodyRotation)));
            for (int m = 0; m < value.muscles.Length; m++)
                report.maxMuscleError = Math.Max(report.maxMuscleError, Math.Abs(value.muscles[m] - expected.muscles[m]));
            report.maxBoneFitDegrees = Math.Max(report.maxBoneFitDegrees, actual.frames[i].maxFitErrorDegrees);
            squares += distance * distance; report.frameCount++;
        }
        if (report.frameCount == 0) throw new InvalidDataException("No Unity reference HumanPose values in input.");
        report.rmsRootPositionMeters = (float)Math.Sqrt(squares / report.frameCount);
        return report;
    }

    private void FitGroup(SkeletonPose desired, float[] muscles, MuscleGroup[] block)
    {
        int[] channels = block.SelectMany(g => g.muscles).Distinct().ToArray();
        int[] observed = block.SelectMany(g => rotationBones[g]).Distinct().ToArray();
        if (observed.Length == 0) return;
        // Solve interacting twist channels simultaneously instead of cycling
        // upper/lower limb groups toward one another for many passes.
        float[] Residual()
        {
            var predictions = observed.Select(b => neutral.LocalRotations[b]).ToArray();
            foreach (var group in block)
            {
                Interpolate(group, group.muscles.Select(m => muscles[m]).ToArray(), out var qs, out _);
                for (int i = 0; i < observed.Length; i++)
                {
                    int b = observed[i], index = Array.IndexOf(group.bones, b); if (index < 0) continue;
                    Quaternion delta = Quaternion.Inverse(neutral.LocalRotations[b]) * qs[index];
                    predictions[i] = reverseComposition[b]
                        ? neutral.LocalRotations[b] * delta * Quaternion.Inverse(neutral.LocalRotations[b]) * predictions[i]
                        : predictions[i] * delta;
                }
            }
            var result = new float[observed.Length * 3];
            for (int i = 0; i < observed.Length; i++)
            {
                int b = observed[i];
                Math3.Put(result, i * 3, Math3.Log(Quaternion.Inverse(predictions[i]) * desired.LocalRotations[b]));
            }
            return result;
        }
        int dim = channels.Length;
        float damping = 1e-5f;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            float[] residual = Residual();
            double cost = residual.Sum(v => (double)v * v);
            if (cost < 1e-14) break;
            var jac = new float[dim][];
            for (int d = 0; d < dim; d++)
            {
                int m = channels[d]; float original = muscles[m];
                float shifted = original < 0.999f ? Math.Min(1, original + 0.001f) : Math.Max(-1, original - 0.001f);
                muscles[m] = shifted;
                float[] shiftedResidual = Residual();
                muscles[m] = original;
                jac[d] = new float[residual.Length];
                for (int r = 0; r < residual.Length; r++) jac[d][r] = (shiftedResidual[r] - residual[r]) / (shifted - original);
            }
            var matrix = new double[dim, dim]; var right = new double[dim];
            for (int a = 0; a < dim; a++)
            {
                for (int r = 0; r < residual.Length; r++) right[a] -= jac[a][r] * residual[r];
                for (int b = 0; b < dim; b++)
                {
                    for (int r = 0; r < residual.Length; r++) matrix[a, b] += jac[a][r] * jac[b][r];
                    if (a == b) matrix[a, b] += damping;
                }
            }
            double[] step = SolveLinear(matrix, right);
            float[] originals = channels.Select(m => muscles[m]).ToArray();
            for (int d = 0; d < dim; d++) muscles[channels[d]] = Math.Clamp(originals[d] + (float)Math.Clamp(step[d], -0.5, 0.5), -1, 1);
            double nextCost = Residual().Sum(v => (double)v * v);
            if (nextCost < cost)
            {
                damping = Math.Max(1e-8f, damping * 0.25f);
                if (cost - nextCost < 1e-13) break;
            }
            else
            {
                for (int d = 0; d < dim; d++) muscles[channels[d]] = originals[d];
                damping *= 10;
                if (damping > 100) break;
            }
        }
    }

    private static Quaternion BodyOrientation(SkeletonPose pose)
    {
        Vector3 Find(int human)
        {
            int index = Array.FindIndex(pose.Rig.bones, b => b.humanBone == human);
            if (index < 0) throw new InvalidDataException("Body landmarks require upper legs and upper arms.");
            return pose.WorldPositions[index];
        }
        Vector3 leftHip = Find(1), rightHip = Find(2), leftShoulder = Find(13), rightShoulder = Find(14);
        Vector3 up = (leftShoulder + rightShoulder - leftHip - rightHip) * 0.5f;
        Vector3 right = rightShoulder - leftShoulder + rightHip - leftHip;
        Vector3 forward = Vector3.Cross(right, up);
        if (up.LengthSquared() < 1e-12f || forward.LengthSquared() < 1e-12f)
            throw new InvalidDataException("Degenerate body landmarks.");
        return Math3.LookRotation(forward, up);
    }

    internal Quaternion EstimateBodyRotation(SkeletonPose pose) => Quaternion.Normalize(
        BodyOrientation(pose) * Quaternion.Inverse(BodyOrientation(neutral)) * Math3.Q(rig.bodyRotation));

    internal static double[] SolveLinear(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-15) return new double[n];
            if (pivot != col)
            {
                for (int j = col; j < n; j++) (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            double div = a[col, col];
            for (int j = col; j < n; j++) a[col, j] /= div;
            b[col] /= div;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double factor = a[r, col];
                for (int j = col; j < n; j++) a[r, j] -= factor * a[col, j];
                b[r] -= factor * b[col];
            }
        }
        return b;
    }

    private void Interpolate(MuscleGroup group, float[] values, out Quaternion[] rotations, out Vector3[] positions)
    {
        var (indices, weights) = Stencil(group, values);
        int count = group.bones.Length;
        var cache = latticeCache[group];
        rotations = new Quaternion[count]; positions = new Vector3[count];
        for (int b = 0; b < count; b++)
        {
            Quaternion sum = new(0, 0, 0, 0); Vector3 position = Vector3.Zero;
            for (int i = 0; i < indices.Length; i++)
            {
                int slot = indices[i] * count + b;
                sum += cache.rotations[slot] * weights[i];
                position += cache.positions[slot] * weights[i];
            }
            rotations[b] = Quaternion.Normalize(sum); positions[b] = position;
        }
    }

    private (int[], float[]) Stencil(MuscleGroup group, float[] values)
    {
        int dim = group.muscles.Length, size = group.resolution;
        int width = Math.Min(size, 5);
        var low = new int[dim]; var weights = new float[dim][];
        float[] negativeRange = negativeRanges[group];
        for (int d = 0; d < dim; d++)
        {
            float coordinate = (Math.Clamp(values[d], -1, 1) + 1) * 0.5f * (size - 1);
            low[d] = Math.Clamp((int)coordinate - width / 2, 0, size - width);
            weights[d] = new float[width];
            // Muscle zero is not the midpoint of asymmetric anatomical limits.
            // Interpolate in signed angular coordinates to remove that kink.
            float Physical(float muscle) => muscle < 0 ? muscle * negativeRange[d] : muscle;
            float physical = Physical(Math.Clamp(values[d], -1, 1));
            for (int i = 0; i < width; i++)
            {
                float w = 1;
                float node = Physical((low[d] + i) * 2f / (size - 1) - 1);
                for (int j = 0; j < width; j++)
                {
                    if (i == j) continue;
                    float other = Physical((low[d] + j) * 2f / (size - 1) - 1);
                    w *= (physical - other) / (node - other);
                }
                weights[d][i] = w;
            }
        }
        // Fourth-order tensor interpolation of quaternion components, aligned to
        // the neutral hemisphere. It resolves swing coupling missed by SLERP of
        // the eight enclosing lattice corners.
        int corners = (int)Math.Pow(width, dim);
        var indices = new int[corners]; var tensorWeights = new float[corners];
        for (int corner = 0; corner < corners; corner++)
        {
                int index = 0, stride = 1, cursor = corner; float weight = 1;
                for (int d = 0; d < dim; d++)
                { int node = cursor % width; cursor /= width; index += (low[d] + node) * stride; stride *= size; weight *= weights[d][node]; }
                indices[corner] = index; tensorWeights[corner] = weight;
        }
        return (indices, tensorWeights);
    }
}
