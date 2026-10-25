using System.Numerics;
using UnityHps;

internal static class ArmContextTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (bool left in new[] { false, true })
        {
            Rig rig = MakeRig(left);
            int upper = left ? 13 : 14;
            var channels = new HashSet<int> { upper, upper + 2, upper + 4 };
            var legacy = new HumanoidSolver(rig, channels);
            var actual = new HumanoidSolver(rig, channels) { FitDecodedArmContext = true };
            var decoder = new HumanoidSolver(rig);
            var target = decoder.Decode(new float[7]);
            // Change the requested parent frame while preserving every arm
            // world pose. Its shoulder channels are intentionally not fitted.
            Vector3 axis = Vector3.Normalize(target.LocalPositions[3]);
            Quaternion offset = Quaternion.CreateFromAxisAngle(axis, .4f);
            target.LocalRotations[2] *= offset;
            target.LocalRotations[3] = Quaternion.Inverse(offset) * target.LocalRotations[3];
            target.UpdateWorld();
            PoseFrame frame = target.ToFrame(0);
            float[] savedPositions = (float[])frame.positions.Clone(), savedRotations = (float[])frame.rotations.Clone();
            var oldFit = legacy.Solve(frame, 3, new float[7]);
            var newFit = actual.Solve(frame, 3, new float[7]);
            var oldPose = decoder.Decode(oldFit.humanPose.muscles, oldFit.humanPose);
            var newPose = decoder.Decode(newFit.humanPose.muscles, newFit.humanPose);
            float oldError = Vector3.Distance(oldPose.WorldPositions[5], target.WorldPositions[5]);
            float error = Vector3.Distance(newPose.WorldPositions[5], target.WorldPositions[5]);
            check(oldError > .005f && error < .00001f && Math3.Angle(newPose.WorldRotations[5], target.WorldRotations[5]) < .001f,
                $"decoded arm context preserves wrist under an unfitted target shoulder: left={left}, old={oldError * 1000:F3} mm, new={error * 1000:F6} mm");
            check(newFit.humanPose.muscles.All(m => Math.Abs(m) < 1e-6) &&
                frame.positions.SequenceEqual(savedPositions) && frame.rotations.SequenceEqual(savedRotations),
                "arm context does not invent a compensating twist or mutate the target");
            Parallel.For(0, 16, _ =>
            {
                var fit = actual.Solve(frame, 2, new float[7]);
                if (fit.humanPose.muscles.Any(m => Math.Abs(m) > 1e-6))
                    throw new Exception("Shared arm context solver leaked frame state.");
            });
            check(true, "decoded arm context is frame local under concurrent solving");
        }
        foreach (bool reverse in new[] { false, true })
        {
            Rig rig = OfflineRigFactory.LoadProfile(false);
            if (reverse) rig.groups = rig.groups.Reverse().ToArray();
            var solver = new HumanoidSolver(rig) { FitDecodedArmContext = true, ReconstructDecodedRoot = true };
            var known = new float[rig.muscles.Length];
            known[0] = .22f; known[3] = .12f; known[46] = .16f;
            known[48] = -.3f; known[49] = .2f; known[50] = .1f;
            known[51] = -.3f; known[52] = .1f; known[53] = .1f; known[54] = -.2f;
            var target = solver.Decode(known);
            var seed = (float[])known.Clone(); seed[0] = seed[3] = seed[46] = 0;
            var fitted = solver.Solve(target.ToFrame(), passes: 1, initialMuscles: seed);
            var pose = solver.Decode(fitted.humanPose.muscles, fitted.humanPose);
            int hand = Array.FindIndex(rig.bones, b => b.humanBone == 18);
            float error = Vector3.Distance(pose.WorldPositions[hand], target.WorldPositions[hand]);
            check(error < .0002f,
                $"one-pass arm fitting uses the fitted torso/shoulder even with reversed group order: reversed={reverse}, error={error * 1000:F6} mm");
        }
    }

    private static Rig MakeRig(bool left)
    {
        int h = left ? 13 : 14;
        var rig = new Rig
        {
            hips = 0,
            bones = [
                new Bone { name = "Hips", humanBone = 0 },
                new Bone { name = "Chest", parent = 0, humanBone = 8, position = [0, .7f, 0] },
                new Bone { name = "Shoulder", parent = 1, humanBone = h - 2, position = [.18f, .3f, 0] },
                new Bone { name = "Upper", parent = 2, humanBone = h, position = [.05f, .01f, 0] },
                new Bone { name = "Fore", parent = 3, humanBone = h + 2, position = [.25f, 0, 0], rotation = Math3.A(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f)) },
                new Bone { name = "Hand", parent = 4, humanBone = h + 4, position = [.22f, 0, 0] }],
            muscles = Enumerable.Range(0, 7).Select(i => new Muscle { name = "m" + i,
                humanBone = i < 3 ? h : i < 5 ? h + 2 : h + 4, dof = i is 2 or 4 ? 0 : 1 }).ToArray()
        };
        MuscleGroup Make(int[] muscles, int[] bones, Func<float[], Quaternion[]> rotations)
        {
            var samples = new List<LatticeSample>();
            for (int k = 0; k < (int)Math.Pow(5, muscles.Length); k++)
            {
                int n = k; var values = new float[muscles.Length];
                for (int d = 0; d < values.Length; d++) { values[d] = n % 5 * .5f - 1; n /= 5; }
                samples.Add(new LatticeSample { muscles = values,
                    positions = bones.SelectMany(b => rig.bones[b].position).ToArray(),
                    rotations = rotations(values).SelectMany(Math3.A).ToArray() });
            }
            return new MuscleGroup { muscles = muscles, bones = bones, resolution = 5, samples = samples.ToArray() };
        }
        var rest = Math3.Q(rig.bones[4].rotation);
        rig.groups = [
            Make([0, 1, 2], [3, 4], m => [Quaternion.CreateFromYawPitchRoll(m[0] * .5f, m[2] * .4f, m[1] * .6f), rest * Quaternion.CreateFromAxisAngle(Vector3.UnitX, m[2] * .15f)]),
            Make([3, 4], [4, 5], m => [rest * Quaternion.CreateFromYawPitchRoll(0, m[1] * .4f, m[0] * .6f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, m[1] * .1f)]),
            Make([5, 6], [5], m => [Quaternion.CreateFromYawPitchRoll(m[0] * .4f, 0, m[1] * .5f)])];
        return rig;
    }
}
