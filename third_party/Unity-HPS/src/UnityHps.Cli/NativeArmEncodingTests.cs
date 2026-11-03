using System.Text.Json;
using UnityHps;

internal static class NativeArmEncodingTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (bool upperChest in new[] { false, true })
        {
            string name = upperChest ? "55" : "53";
            Rig profile = OfflineRigFactory.LoadProfile(upperChest);
            Rig poseRig = OfflineRigFactory.Transfer(new FbxDocument { bones = profile.bindBones }, profile,
                HumanoidResponseTransport.AnatomicalTargets);
            string before = JsonSerializer.Serialize(poseRig, HpsJsonContext.Default.Rig);
            Rig response = OfflineRigFactory.ForHumanoidEncoding(poseRig);
            check(!ReferenceEquals(response, poseRig) && before == JsonSerializer.Serialize(poseRig, HpsJsonContext.Default.Rig),
                $"native arm encoding {name} preserves the pose-transport rig");
            check(response.bones.Length == poseRig.bones.Length && response.bones.Zip(poseRig.bones).All(pair =>
                    pair.First.parent == pair.Second.parent && pair.First.humanBone == pair.Second.humanBone &&
                    pair.First.position.SequenceEqual(pair.Second.position)) && ReferenceEquals(response.bindBones, poseRig.bindBones),
                $"native arm encoding {name} preserves bone ordering, offsets and bindings");
            check(response.groups.Count(g => g.armResponseAxes.Length != 0) == 6,
                $"native arm encoding {name} identifies the six analytic response groups");

            var solver = new HumanoidSolver(response, rangePolicy: HumanoidRangePolicy.Tangent);
            var curveSolver = new HumanoidSolver(response, null, HumanoidRangePolicy.Tangent, true);
            var curveInput = new float[response.muscles.Length];
            foreach (int channel in new[] { 41, 43, 50, 52 }) curveInput[channel] = 2.1f;
            var curveSeed = curveSolver.CreateFitSeed(curveInput);
            check(new[] { 41, 43, 50, 52 }.All(channel => curveSeed[channel] < 2 && curveInput[channel] == 2.1f) &&
                  solver.CreateFitSeed(curveInput).SequenceEqual(curveInput),
                $"curve twist bounds {name} preserve the default decoder and caller-owned input");
            var bones = response.bones.Select((b, i) => (b.humanBone, i)).Where(p => p.humanBone >= 0)
                .ToDictionary(p => p.humanBone, p => p.i);
            var first = new float[response.muscles.Length];
            first[48] = .2f; first[51] = .5f; first[52] = 1.999f; first[53] = .3f; first[54] = -.2f;
            var second = (float[])first.Clone(); second[52] = 2.001f;
            SkeletonPose a = solver.Decode(first), b = solver.Decode(second);
            // Native controls show a local half-turn at this cut, while the
            // complete wrist direction advances by only the physical 0.18 deg.
            check(Math3.Angle(a.LocalRotations[bones[16]], b.LocalRotations[bones[16]]) > 170 &&
                  Math3.Angle(a.WorldRotations[bones[18]], b.WorldRotations[bones[18]]) < .3f && second[52] == 2.001f,
                $"native arm encoding {name} preserves wrist direction across the distributed twist cut");

            Rig restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(response, HpsJsonContext.Default.Rig),
                HpsJsonContext.Default.Rig)!;
            SkeletonPose decoded = new HumanoidSolver(restored, rangePolicy: HumanoidRangePolicy.Tangent).Decode(second);
            check(response.bones.Select((_, i) => Math3.Angle(decoded.LocalRotations[i], b.LocalRotations[i])).Max() < .001f,
                $"native arm encoding {name} survives rig serialization");
        }
        Rig measured = Demo.CreateRig(); measured.precision = "unity-native";
        check(ReferenceEquals(measured, OfflineRigFactory.ForHumanoidEncoding(measured)),
            "native arm encoding retains an explicitly calibrated response rig");
    }
}
