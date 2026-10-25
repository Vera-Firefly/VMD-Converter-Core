using System.Numerics;
using UnityHps;

internal static class LegGeometryTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig rig = OfflineRigFactory.LoadProfile(false);
        foreach (bool left in new[] { true, false })
        {
            int human = left ? 1 : 2;
            var active = new HashSet<int> { human, human + 2, human + 4 };
            var decoder = new HumanoidSolver(rig);
            var oldSolver = new HumanoidSolver(rig, active);
            var solver = new HumanoidSolver(rig, active) { FitLegGeometry = true, FitDecodedArmContext = true, ReconstructDecodedRoot = true };
            var seed = new float[rig.muscles.Length];
            for (int i = 0; i < seed.Length; i++)
            {
                Muscle m = rig.muscles[i];
                if (m.humanBone == human) seed[i] = m.dof == 0 ? .03f : .2f;
                if (m.humanBone == human + 2) seed[i] = m.dof == 0 ? .06f : -.4f;
                if (m.humanBone == human + 4) seed[i] = .08f;
            }
            var desired = decoder.Decode(seed);
            int upper = Array.FindIndex(rig.bones, b => b.humanBone == human);
            int knee = Array.FindIndex(rig.bones, b => b.humanBone == human + 2);
            int foot = Array.FindIndex(rig.bones, b => b.humanBone == human + 4);
            Vector3 beforeKnee = desired.WorldPositions[knee], beforeFoot = desired.WorldPositions[foot];
            Quaternion beforeKneeRotation = desired.WorldRotations[knee], beforeFootRotation = desired.WorldRotations[foot];
            desired.LocalRotations[upper] *= Quaternion.CreateFromAxisAngle(Vector3.Normalize(desired.LocalPositions[knee]), 1.1f);
            desired.UpdateWorld(); desired.SetWorldRotation(knee, beforeKneeRotation);
            check(Vector3.Distance(beforeKnee, desired.WorldPositions[knee]) < 1e-6f &&
                Vector3.Distance(beforeFoot, desired.WorldPositions[foot]) < 1e-6f &&
                Math3.Angle(beforeFootRotation, desired.WorldRotations[foot]) < .001f,
                "leg roll fixture preserves knee, foot position and foot orientation");
            PoseFrame frame = desired.ToFrame();
            var legacy = oldSolver.Solve(frame, 3, seed);
            var fitted = solver.Solve(frame, 3, seed);
            var pose = decoder.Decode(fitted.humanPose.muscles, fitted.humanPose);
            check(legacy.humanPose.muscles.Zip(seed).Any(p => Math.Abs(p.First - p.Second) > .1f) &&
                fitted.humanPose.muscles.Zip(seed).All(p => Math.Abs(p.First - p.Second) < 1e-5f) &&
                Vector3.Distance(pose.WorldPositions[foot], beforeFoot) < 1e-5f,
                $"leg geometry retains an exact seeded endpoint instead of chasing redistributed roll: left={left}");

            var frames = new List<PoseFrame>();
            var seeds = new List<float[]>();
            for (int i = 0; i < 33; i++)
            {
                var values = (float[])seed.Clone();
                for (int m = 0; m < values.Length; m++)
                {
                    if (rig.muscles[m].humanBone == human + 2 && rig.muscles[m].dof != 0)
                        values[m] = -.4f + 1.38f * i / 32;
                    if (rig.muscles[m].humanBone is >= 11 and <= 18) values[m] = .03f;
                }
                var target = decoder.Decode(values);
                Quaternion kneeWorld = target.WorldRotations[knee];
                target.LocalRotations[upper] *= Quaternion.CreateFromAxisAngle(
                    Vector3.Normalize(target.LocalPositions[knee]), MathF.Sin(i * .4f) * 1.1f);
                target.UpdateWorld(); target.SetWorldRotation(knee, kneeWorld);
                PoseFrame input = target.ToFrame(i / 30f);
                if ((i & 1) != 0)
                    for (int q = 0; q < input.rotations.Length; q++) input.rotations[q] = -input.rotations[q];
                frames.Add(input); seeds.Add(values);
            }
            var batchSolver = new HumanoidSolver(rig, active)
            {
                FitLegGeometry = true, FitDecodedArmContext = true,
                AnchorLimbTwistToInitial = true, ReconstructDecodedRoot = true
            };
            var batch = new PoseBatch { frames = frames.ToArray() };
            var parallel = batchSolver.SolveBatch(batch, 4, 3, initialMuscles: seeds.ToArray());
            var serial = batchSolver.SolveBatch(batch, 1, 3, initialMuscles: seeds.ToArray());
            check(parallel.frames.Select((f, i) => f.humanPose.muscles.Zip(seeds[i]).All(p => Math.Abs(p.First - p.Second) < 1e-5f)).All(x => x),
                $"leg geometry preserves seeded bend/roll and inactive arms across bent-to-straight poses and quaternion signs: left={left}");
            check(parallel.frames.Zip(serial.frames).All(p => p.First.humanPose.muscles.SequenceEqual(p.Second.humanPose.muscles)),
                $"leg bend/roll sequence is deterministic across frame worker counts: left={left}");
        }
    }
}
