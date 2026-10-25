using System.Numerics;
using UnityHps;

internal static class RootReconstructionTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (bool weighted in new[] { false, true })
        foreach (string orientation in new[] { "hips", "landmarks" })
        {
            Rig rig = MakeRig(weighted, orientation);
            var solver = new HumanoidSolver(rig);
            Parallel.For(0, 32, i =>
            {
                var known = new HumanPose
                {
                    muscles = [MathF.Sin(i) * .6f, MathF.Cos(i) * .5f, MathF.Sin(i * .3f) * .7f],
                    bodyPosition = [MathF.Sin(i * .7f) * .3f, .8f + i * .01f, MathF.Cos(i * .2f) * .4f],
                    bodyRotation = Math3.A(Quaternion.CreateFromYawPitchRoll(i * .29f, MathF.Sin(i) * .3f, MathF.Cos(i) * .2f))
                };
                PoseFrame frame = solver.Decode(known.muscles, known).ToFrame(i / 30f);
                float[] positions = (float[])frame.positions.Clone(), rotations = (float[])frame.rotations.Clone();
                HumanPose actual = solver.ReconstructRoot(frame, known.muscles);
                if (Vector3.Distance(Math3.V(actual.bodyPosition), Math3.V(known.bodyPosition)) > 2e-6f ||
                    Math3.Angle(Math3.Q(actual.bodyRotation), Math3.Q(known.bodyRotation)) > .001f)
                    throw new Exception("Failed to recover known RootT/RootQ.");
                if (ReferenceEquals(actual.muscles, known.muscles) || !actual.muscles.SequenceEqual(known.muscles) ||
                    !frame.positions.SequenceEqual(positions) || !frame.rotations.SequenceEqual(rotations))
                    throw new Exception("Root reconstruction changed an input or retained its muscle array.");
            });
            check(true, $"root reconstruction recovers known transforms with nested parents: {orientation}, weighted={weighted}; parallel input ownership");

            var batchSolver = new HumanoidSolver(rig, new HashSet<int>()) { ReconstructDecodedRoot = true };
            PoseBatch input = Demo.CreatePoses(rig, 8);
            float[][] seeds = input.frames.Select(f => f.reference!.muscles).ToArray();
            ResultBatch result = batchSolver.SolveBatch(input, threads: 4, initialMuscles: seeds);
            check(result.frames.Select((f, i) =>
                Vector3.Distance(Math3.V(f.humanPose.bodyPosition), Math3.V(input.frames[i].reference!.bodyPosition)) < 2e-6f &&
                Math3.Angle(Math3.Q(f.humanPose.bodyRotation), Math3.Q(input.frames[i].reference!.bodyRotation)) < .001f &&
                f.humanPose.muscles.SequenceEqual(seeds[i])).All(valid => valid),
                "batch root reconstruction reuses decoded poses with fixed muscles");

            var target = solver.Decode([.2f, -.3f, .1f]).ToFrame(0);
            target.goals = [new IkGoal { root = 5, middle = 6, tip = 7 }];
            bool rejected = false;
            try { solver.ReconstructRoot(target, [.2f, -.3f, .1f]); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "root reconstruction rejects unresolved IK goals");
        }
    }

    private static Rig MakeRig(bool weighted, string orientation)
    {
        Rig rig = Demo.CreateRig();
        var prefix = new[]
        {
            new Bone { name = "Animator" },
            new Bone { name = "Armature", parent = 0, position = [.04f, .1f, -.03f],
                rotation = Math3.A(Quaternion.CreateFromYawPitchRoll(.31f, -.17f, .09f)) }
        };
        foreach (Bone bone in rig.bones) bone.parent = bone.parent < 0 ? 1 : bone.parent + 2;
        rig.bones = prefix.Concat(rig.bones).ToArray(); rig.hips += 2;
        foreach (MuscleGroup group in rig.groups) group.bones = group.bones.Select(i => i + 2).ToArray();
        rig.humanScale = .78f; rig.bodyPosition = [.03f, .9f, -.06f];
        rig.bodyRotation = Math3.A(Quaternion.CreateFromYawPitchRoll(-.23f, .11f, .04f));
        rig.bodyOrientationMode = orientation;
        if (weighted)
        {
            rig.bodyPositionWeights = new float[rig.bones.Length];
            rig.bodyPositionWeights[rig.hips] = .4f;
            rig.bodyPositionWeights[rig.hips + 1] = .3f;
            rig.bodyPositionWeights[rig.hips + 2] = .3f;
        }
        return rig;
    }
}
