using System.Numerics;

namespace UnityHps;

public sealed class BodyMassCalibrationResult
{
    public float[] weights { get; init; } = [];
    public int trainingPoses { get; init; }
    public int validationPoses { get; init; }
    public double maxTrainingErrorMetres { get; init; }
    public double maxValidationErrorMetres { get; init; }
}

/// <summary>Recover a body mass model from fixed-RootT native calibration samples.</summary>
public static class HumanoidBodyMassCalibration
{
    /// <summary>
    /// Fit nonnegative, unit-total masses without modifying either input rig.
    /// Samples must record native compensating Hips translations at the rig's
    /// bodyPosition. A transferred FBX profile without those samples is rejected.
    /// Positive prior coefficients determine eligible mass-bearing bones and
    /// resolve weak axes; signed regression coefficients are not fitted masses.
    /// </summary>
    public static BodyMassCalibrationResult Fit(Rig rig, Rig prior,
        int seed = 20261024, double maxErrorMetres = .00005)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(prior);
        if (!double.IsFinite(maxErrorMetres) || maxErrorMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxErrorMetres));
        HumanoidSolver.Validate(rig);
        if (prior.bodyPositionWeights.Length != prior.bones.Length || prior.bones.Length == 0)
            throw new InvalidDataException("The prior must contain body masses for all its bones.");
        var target = rig.bones.Select((b, i) => (b, i)).Where(x => x.b.humanBone >= 0)
            .ToDictionary(x => x.b.humanBone, x => x.i);
        var mapped = new double[rig.bones.Length];
        for (int i = 0; i < prior.bones.Length; i++)
        {
            double weight = prior.bodyPositionWeights[i];
            if (!double.IsFinite(weight))
                throw new InvalidDataException("Prior body coefficients must be finite.");
            if (weight <= 1e-6) continue;
            int source = i;
            while (source >= 0 && !target.ContainsKey(prior.bones[source].humanBone))
            {
                int parent = prior.bones[source].parent;
                if (parent < -1 || parent >= source)
                    throw new InvalidDataException("Invalid prior hierarchy.");
                source = parent;
            }
            if (source < 0) throw new InvalidDataException("A mass-bearing prior bone cannot be mapped.");
            mapped[target[prior.bones[source].humanBone]] += weight;
        }
        double total = mapped.Sum();
        if (total <= 0) throw new InvalidDataException("The prior has no positive body coefficients.");
        for (int i = 0; i < mapped.Length; i++) mapped[i] /= total;
        int[] selected = Enumerable.Range(0, mapped.Length).Where(i => mapped[i] > 1e-6 || i == rig.hips).ToArray();
        var examples = new List<(Vector3[] points, bool train)>();
        var pose = new SkeletonPose(rig);
        Vector3 neutralHips = pose.WorldPositions[rig.hips];
        float compensation = 0;
        var random = new Random(seed);
        foreach (MuscleGroup group in rig.groups)
        {
            foreach (LatticeSample sample in group.samples)
            {
                for (int j = 0; j < group.bones.Length; j++)
                {
                    int b = group.bones[j];
                    pose.LocalPositions[b] = Math3.V(sample.positions, j * 3);
                    pose.LocalRotations[b] = Math3.Q(sample.rotations, j * 4);
                }
                pose.UpdateWorld();
                compensation = Math.Max(compensation, Vector3.Distance(pose.WorldPositions[rig.hips], neutralHips));
                examples.Add((selected.Select(b => pose.WorldPositions[b]).ToArray(), random.NextDouble() < .7));
            }
            foreach (int b in group.bones)
            {
                pose.LocalPositions[b] = Math3.V(rig.bones[b].position);
                pose.LocalRotations[b] = Math3.Q(rig.bones[b].rotation);
            }
        }
        if (compensation < 1e-5f)
            throw new InvalidDataException("Calibration samples do not contain native Hips compensation; a transferred profile cannot calibrate body masses.");
        int training = examples.Count(e => e.train), validation = examples.Count - training;
        if (training < selected.Length || validation < 3)
            throw new InvalidDataException("Insufficient independent calibration samples.");
        int dim = selected.Length, rows = training * 3 + dim + 1;
        var columns = Enumerable.Range(0, dim).Select(_ => new double[rows]).ToArray();
        var values = new double[rows];
        Vector3 anchor = Math3.V(rig.bodyPosition) * rig.humanScale;
        int row = 0;
        foreach (var example in examples.Where(e => e.train))
        {
            for (int d = 0; d < 3; d++, row++)
            {
                values[row] = anchor[d];
                for (int c = 0; c < dim; c++) columns[c][row] = example.points[c][d];
            }
        }
        for (int c = 0; c < dim; c++)
        {
            columns[c][row + c] = 1e-4;
            values[row + c] = mapped[selected[c]] * 1e-4;
            columns[c][rows - 1] = 1e3;
        }
        values[rows - 1] = 1e3;
        double[] fitted = Nonnegative(columns, values);
        if (Math.Abs(fitted.Sum() - 1) > 1e-6)
            throw new InvalidDataException("The fitted model does not preserve unit total mass.");
        var weights = new float[rig.bones.Length];
        for (int c = 0; c < dim; c++) weights[selected[c]] = (float)fitted[c];
        double trainError = 0, validationError = 0;
        foreach (var example in examples)
        {
            double x = 0, y = 0, z = 0;
            for (int c = 0; c < dim; c++)
            {
                double w = weights[selected[c]];
                x += w * example.points[c].X; y += w * example.points[c].Y; z += w * example.points[c].Z;
            }
            double error = Math.Sqrt(Math.Pow(x - anchor.X, 2) + Math.Pow(y - anchor.Y, 2) + Math.Pow(z - anchor.Z, 2));
            if (example.train) trainError = Math.Max(trainError, error);
            else validationError = Math.Max(validationError, error);
        }
        if (Math.Max(trainError, validationError) > maxErrorMetres)
            throw new InvalidDataException($"Body mass calibration exceeds its error bound: training={trainError:G6} m, held-out={validationError:G6} m.");
        return new BodyMassCalibrationResult { weights = weights, trainingPoses = training, validationPoses = validation,
            maxTrainingErrorMetres = trainError, maxValidationErrorMetres = validationError };
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0; for (int i = 0; i < a.Length; i++) sum += a[i] * b[i]; return sum;
    }

    private static double[] Nonnegative(double[][] columns, double[] values)
    {
        int dim = columns.Length; var active = new bool[dim]; var x = new double[dim];
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var residual = (double[])values.Clone();
            for (int c = 0; c < dim; c++)
                if (x[c] != 0) for (int r = 0; r < residual.Length; r++) residual[r] -= columns[c][r] * x[c];
            int best = -1; double gradient = 1e-9;
            for (int c = 0; c < dim; c++) if (!active[c])
            {
                double g = Dot(columns[c], residual);
                if (g > gradient) { gradient = g; best = c; }
            }
            if (best < 0) return x;
            active[best] = true;
            bool settled = false;
            for (int inner = 0; inner < 300; inner++)
            {
                double[] z = LeastSquares(columns, values, active);
                if (Enumerable.Range(0, dim).All(c => !active[c] || z[c] > 0))
                { x = z; settled = true; break; }
                double alpha = 1;
                for (int c = 0; c < dim; c++) if (active[c] && z[c] <= 0)
                    alpha = Math.Min(alpha, x[c] / Math.Max(x[c] - z[c], 1e-30));
                for (int c = 0; c < dim; c++)
                {
                    x[c] += alpha * (z[c] - x[c]);
                    if (active[c] && x[c] < 1e-12) { x[c] = 0; active[c] = false; }
                }
            }
            if (!settled) throw new InvalidDataException("Body mass fitting did not converge.");
        }
        throw new InvalidDataException("Body mass fitting did not converge.");
    }

    private static double[] LeastSquares(double[][] columns, double[] values, bool[] active)
    {
        int[] ids = Enumerable.Range(0, active.Length).Where(c => active[c]).ToArray();
        var q = new double[ids.Length][]; var r = new double[ids.Length, ids.Length];
        var rhs = new double[ids.Length];
        for (int j = 0; j < ids.Length; j++)
        {
            q[j] = (double[])columns[ids[j]].Clone();
            // Reorthogonalization protects weak mass directions beside the
            // unit-total constraint; normal equations lose that precision.
            for (int pass = 0; pass < 2; pass++) for (int k = 0; k < j; k++)
            {
                double projection = Dot(q[k], q[j]); r[k, j] += projection;
                for (int i = 0; i < values.Length; i++) q[j][i] -= projection * q[k][i];
            }
            double length = Math.Sqrt(Dot(q[j], q[j]));
            if (!double.IsFinite(length) || length < 1e-15)
                throw new InvalidDataException("Degenerate body mass calibration.");
            r[j, j] = length;
            for (int i = 0; i < q[j].Length; i++) q[j][i] /= length;
            rhs[j] = Dot(q[j], values);
        }
        var result = new double[active.Length];
        for (int j = ids.Length - 1; j >= 0; j--)
        {
            double value = rhs[j];
            for (int k = j + 1; k < ids.Length; k++) value -= r[j, k] * result[ids[k]];
            result[ids[j]] = value / r[j, j];
        }
        return result;
    }
}
