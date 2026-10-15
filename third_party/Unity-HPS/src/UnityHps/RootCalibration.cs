using System.Numerics;

namespace UnityHps;

public static class RootCalibration
{
    /// <summary>
    /// Fit shared scalar body mass weights from independent Unity pose samples.
    /// Translation probes alone miss Unity's reconstruction from joint rotations.
    /// The reference set used for reporting must be distinct from this training set.
    /// </summary>
    public static void Fit(Rig rig, PoseBatch training)
    {
        HumanoidSolver.Validate(rig);
        int[] bones = Enumerable.Range(0, rig.bones.Length).Where(b => rig.bones[b].humanBone >= 0).ToArray();
        int n = bones.Length;
        if (training.frames.Length < n * 2) throw new InvalidDataException("Root calibration needs at least twice the mapped bone count in training frames.");
        var matrix = new double[n, n]; var right = new double[n];
        foreach (var frame in training.frames)
        {
            HumanPose target = frame.reference ?? throw new InvalidDataException("Root training requires Unity references.");
            var pose = new SkeletonPose(rig, frame);
            Vector3 center = Math3.V(target.bodyPosition) * rig.humanScale;
            for (int a = 0; a < n; a++)
            {
                Vector3 pa = pose.WorldPositions[bones[a]];
                right[a] += (double)pa.X * center.X + (double)pa.Y * center.Y + (double)pa.Z * center.Z;
                for (int b = 0; b < n; b++)
                {
                    Vector3 pb = pose.WorldPositions[bones[b]];
                    matrix[a, b] += (double)pa.X * pb.X + (double)pa.Y * pb.Y + (double)pa.Z * pb.Z;
                }
            }
        }
        // Translation covariance: weights sum to one. Mild ridge regularizes
        // collinear finger/limb points; no reference poses are embedded at runtime.
        for (int a = 0; a < n; a++)
        {
            right[a] += training.frames.Length;
            for (int b = 0; b < n; b++) matrix[a, b] += training.frames.Length;
            matrix[a, a] += 1e-8;
        }
        double[] weights = HumanoidSolver.SolveLinear(matrix, right);
        rig.bodyPositionWeights = new float[rig.bones.Length];
        for (int b = 0; b < n; b++) rig.bodyPositionWeights[bones[b]] = (float)weights[b];
        rig.bodyOrientationMode = "landmarks";
        FitComposition(rig, training);
    }

    private static void FitComposition(Rig rig, PoseBatch training)
    {
        rig.reverseCompositionBones = [];
        var forward = new HumanoidSolver(rig);
        rig.reverseCompositionBones = Enumerable.Range(0, rig.bones.Length).ToArray();
        var reverse = new HumanoidSolver(rig);
        var forwardCost = new double[rig.bones.Length]; var reverseCost = new double[rig.bones.Length];
        foreach (PoseFrame frame in training.frames.Take(128))
        {
            float[] muscles = (frame.inputHumanPose ?? frame.reference!).muscles;
            SkeletonPose a = forward.Decode(muscles), b = reverse.Decode(muscles);
            for (int i = 0; i < rig.bones.Length; i++)
            {
                Quaternion target = Math3.Q(frame.rotations, i * 4);
                forwardCost[i] += Math.Pow(Math3.Angle(a.LocalRotations[i], target), 2);
                reverseCost[i] += Math.Pow(Math3.Angle(b.LocalRotations[i], target), 2);
            }
        }
        rig.reverseCompositionBones = Enumerable.Range(0, rig.bones.Length).Where(i => reverseCost[i] + 1e-8 < forwardCost[i]).ToArray();
    }

}
