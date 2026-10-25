using System.Numerics;
using UnityHps;

internal static class FkBranchTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig rig = OfflineRigFactory.LoadProfile(false);
        var rest = new SkeletonPose(rig);
        int upper = Array.FindIndex(rig.bones, b => b.humanBone == 14);
        int elbow = Array.FindIndex(rig.bones, b => b.humanBone == 16);
        int wrist = Array.FindIndex(rig.bones, b => b.humanBone == 18);
        var changed = new SkeletonPose(rig);
        Quaternion oldElbow = changed.WorldRotations[elbow], oldWrist = changed.WorldRotations[wrist];
        Vector3 axis = Vector3.Normalize(changed.WorldPositions[elbow] - changed.WorldPositions[upper]);
        changed.SetWorldRotation(upper, Quaternion.Normalize(new Quaternion(axis, 0) * changed.WorldRotations[upper]));
        changed.SetWorldRotation(elbow, oldElbow);
        changed.SetWorldRotation(wrist, oldWrist);
        PoseFrame[] input = [rest.ToFrame(0), changed.ToFrame(1f / 30)];
        float[] original = (float[])input[1].rotations.Clone();
        PoseFrame[] raw = [rest.ToFrame(0), rest.ToFrame(1f / 30)];
        var output = FkBranchContinuation.Process(rig, rig, raw, input);
        var solved = new SkeletonPose(rig, output[1]);
        check(input[1].rotations.SequenceEqual(original) && !ReferenceEquals(input, output), "FK branch continuation preserves caller-owned poses");
        check(Vector3.Distance(solved.WorldPositions[elbow], changed.WorldPositions[elbow]) < 1e-6f &&
              Vector3.Distance(solved.WorldPositions[wrist], changed.WorldPositions[wrist]) < 1e-6f &&
              Math3.Angle(solved.WorldRotations[wrist], oldWrist) < .001f &&
              Math3.Angle(solved.LocalRotations[upper], rest.LocalRotations[upper]) < .001f,
              "equivalent FK half-turn selection preserves endpoints and wrist orientation");
        bool rejected = false;
        try { FkBranchContinuation.Process(rig, rig, raw, [rest.ToFrame(0), rest.ToFrame(0)]); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "FK branch continuation rejects mismatched time sequences");
        raw[1].time = float.NaN;
        rejected = false;
        try { FkBranchContinuation.Process(rig, rig, raw, input); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "FK branch continuation rejects non-finite source times");
    }
}
