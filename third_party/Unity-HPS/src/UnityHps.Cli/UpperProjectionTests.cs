using UnityHps;

internal static class UpperProjectionTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig rig = OfflineRigFactory.LoadProfile(false);
        var projector = new HumanoidUpperProjector(rig);
        PoseFrame frame = new HumanoidSolver(rig).Decode(new float[rig.muscles.Length]).ToFrame();
        float[] seed = Enumerable.Repeat(.01f, rig.muscles.Length).ToArray();
        float[] result = projector.Project(frame, seed);
        check(result.All(float.IsFinite) && !ReferenceEquals(result, seed) && seed.All(x => x == .01f) &&
            Enumerable.Range(0, seed.Length).Where(i => rig.muscles[i].unityIndex is < 37 or > 54).All(i => result[i] == seed[i]),
            "embedded upper projector owns output and preserves non-upper channels");
        Parallel.For(0, 8, _ =>
        {
            if (!projector.Project(frame, seed).SequenceEqual(result))
                throw new Exception("Shared upper response model changed under parallel calls.");
        });
        check(true, "embedded upper response is deterministic under parallel projection");
    }
}
