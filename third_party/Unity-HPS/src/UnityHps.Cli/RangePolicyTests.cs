using System.Numerics;
using UnityHps;

internal static class RangePolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig rig = Demo.CreateRig();
        var tangent = new HumanoidSolver(rig, rangePolicy: HumanoidRangePolicy.Tangent);
        var values = new[] { 1.2f, 0f, 0f };
        var pose = tangent.Decode(values);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f * .6f);
        check(Math3.Angle(pose.LocalRotations[1], expected) < .02f && values[0] == 1.2f &&
            tangent.ExtrapolatedGroupEvaluations > 0, "explicit tangent policy extrapolates a known axis without changing inputs");
        bool rejected = false;
        try { tangent.Decode([4.01f, 0, 0]); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "tangent policy retains a finite absolute evaluation bound");
        float[] estimate = [4.01f, -.25f, .1f];
        float[] seed = tangent.CreateFitSeed(estimate);
        check(seed.SequenceEqual(new[] { 4f, -.25f, .1f }) && estimate[0] == 4.01f,
            "optimizer seed uses solver bounds without mutating estimates or valid channels");
        var fitted = tangent.Solve(pose.ToFrame(), passes: 3, initialMuscles: seed);
        check(Math.Abs(fitted.humanPose.muscles[0] - values[0]) < .001f,
            "bounded optimizer seed still fits the original target instead of clipping the output");
        rejected = false;
        try { tangent.Solve(pose.ToFrame(), initialMuscles: estimate); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "explicit seed preparation does not weaken Solve range validation");
        rejected = false;
        try { tangent.CreateFitSeed([float.NaN, 0, 0]); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "optimizer seed preparation rejects nonfinite estimates");
        Rig profile = OfflineRigFactory.LoadProfile(false);
        var decoder = new HumanoidSolver(profile, rangePolicy: HumanoidRangePolicy.Tangent);
        var shoulder=new float[profile.muscles.Length];shoulder[38]=4.05617762f;
        check(float.IsFinite(decoder.Decode(shoulder).LocalRotations[profile.hips].W) && shoulder[38]>4,
            "tangent shoulder accepts a finite 60.84-degree projected pose without clipping");
        bool shoulderRejected=false;shoulder[38]=12.01f;
        try{decoder.Decode(shoulder);}catch(InvalidDataException){shoulderRejected=true;}
        check(shoulderRejected,"extended shoulder evaluation retains its physical-angle bound");
        var a = new float[profile.muscles.Length]; var b = new float[a.Length];
        a[50] = -.1f; b[50] = 3.9f;
        var pa = decoder.Decode(a); var pb = decoder.Decode(b);
        check(Enumerable.Range(0, profile.bones.Length).Max(i => Math3.Angle(pa.LocalRotations[i], pb.LocalRotations[i])) < .03f,
            "tangent arm evaluation respects the total-twist phase period");
    }
}
