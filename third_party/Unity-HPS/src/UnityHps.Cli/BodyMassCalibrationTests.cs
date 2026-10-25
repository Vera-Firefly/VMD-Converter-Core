using System.Numerics;
using UnityHps;

internal static class BodyMassCalibrationTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig rig = Demo.CreateRig();
        float[] known = [.6f, .1f, .3f];
        var neutral = new SkeletonPose(rig);
        Vector3 anchor = Vector3.Zero;
        for (int b = 0; b < 3; b++) anchor += neutral.WorldPositions[b] * known[b];
        rig.bodyPosition = Math3.A(anchor);
        MuscleGroup original = rig.groups[0];
        var samples = new LatticeSample[original.samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            var pose = new SkeletonPose(rig);
            pose.LocalRotations[1] = Math3.Q(original.samples[i].rotations);
            pose.UpdateWorld();
            Vector3 center = Vector3.Zero;
            for (int b = 0; b < 3; b++) center += pose.WorldPositions[b] * known[b];
            pose.LocalPositions[0] += anchor - center;
            samples[i] = new LatticeSample
            {
                positions = Enumerable.Range(0, 3).SelectMany(b => Math3.A(pose.LocalPositions[b])).ToArray(),
                rotations = Enumerable.Range(0, 3).SelectMany(b => Math3.A(pose.LocalRotations[b])).ToArray()
            };
        }
        rig.groups = [new MuscleGroup { muscles = [0, 1, 2], bones = [0, 1, 2], resolution = 5, samples = samples }];
        Rig prior = Demo.CreateRig();
        prior.bodyPositionWeights = new float[prior.bones.Length];
        // Signed coefficients can occur in a regression prior. They are not
        // physical fitted masses, and must not remove Hips from the fit.
        prior.bodyPositionWeights[0] = -.01f;
        prior.bodyPositionWeights[1] = .4f;
        prior.bodyPositionWeights[2] = .4f;
        float[] before = (float[])samples[0].positions.Clone();
        BodyMassCalibrationResult result = HumanoidBodyMassCalibration.Fit(rig, prior);
        check(Enumerable.Range(0, 3).All(b => Math.Abs(result.weights[b] - known[b]) < 1e-5) &&
            result.maxValidationErrorMetres < 1e-6 && result.validationPoses > 0,
            "body mass calibration recovers known masses from independent compensated poses");
        check(result.weights.All(w => float.IsFinite(w) && w >= 0) && Math.Abs(result.weights.Sum() - 1) < 1e-6 &&
            samples[0].positions.SequenceEqual(before) && rig.bodyPositionWeights.Length == 0,
            "body mass calibration preserves physical constraints and input ownership");
        bool rejected = false;
        try { HumanoidBodyMassCalibration.Fit(Demo.CreateRig(), prior); }
        catch (InvalidDataException error) { rejected = error.Message.Contains("Hips compensation"); }
        check(rejected, "body mass calibration rejects profiles without native compensation");
    }
}
