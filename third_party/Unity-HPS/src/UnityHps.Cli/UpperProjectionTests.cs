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

        var decoder = new HumanoidSolver(OfflineRigFactory.ForHumanoidEncoding(rig), rangePolicy: HumanoidRangePolicy.Tangent);
        PoseFrame[] frames = Enumerable.Range(0, 9).Select(i => {
            var muscles = new float[rig.muscles.Length];
            muscles[41] = 1.8f + i * .05f;
            muscles[43] = -1.8f - i * .05f;
            muscles[39] = .2f; muscles[42] = .4f; muscles[44] = .15f;
            return decoder.Decode(muscles).ToFrame(i / 30f);
        }).ToArray();
        string before = System.Text.Json.JsonSerializer.Serialize(frames, HpsJsonContext.Default.PoseFrameArray);
        var seeds = frames.Select(_ => (float[])seed.Clone()).ToArray();
        var serial = projector.ProjectSourceBatch(frames, seeds, 1);
        var parallel = projector.ProjectSourceBatch(frames, seeds, 4);
        check(serial.Zip(parallel).All(p => p.First.SequenceEqual(p.Second)),
            "upper batch branch selection is deterministic across frame workers at twist cuts");
        Parallel.For(0, 3, _ => {
            var concurrent = projector.ProjectSourceBatch(frames, seeds, 1);
            if (!serial.Zip(concurrent).All(p => p.First.SequenceEqual(p.Second)))
                throw new Exception("Upper sequence history leaked between clips.");
        });
        check(true, "upper batch branch history is isolated across concurrent clips");
        check(before == System.Text.Json.JsonSerializer.Serialize(frames, HpsJsonContext.Default.PoseFrameArray) &&
              seeds.All(v => v.SequenceEqual(seed)) && serial.SelectMany(v => v).All(float.IsFinite) &&
              serial.All(v => Enumerable.Range(0, v.Length).Where(i => rig.muscles[i].unityIndex is <37 or >54).All(i => v[i] == seed[i])),
            "upper branch batches preserve source frames, initial arrays and non-upper channels");
        var replay = projector.ProjectSourceBatch(frames, seeds, 1);
        check(serial.Zip(replay).All(p => p.First.SequenceEqual(p.Second)),
            "upper branch sequence resets on subsequent calls");
        PoseFrame[] ClockFrames(int rate) => Enumerable.Range(0, rate / 15 + 1).Select(i => {
            float time = i / (float)rate;
            var muscles = new float[rig.muscles.Length];
            muscles[39] = .2f + time; muscles[41] = 1.8f + time;
            muscles[42] = .4f; muscles[43] = -1.8f - time; muscles[44] = .15f;
            return decoder.Decode(muscles).ToFrame(time);
        }).ToArray();
        var sixtyFrames = ClockFrames(60); var fineFrames = ClockFrames(120);
        var sixty = projector.ProjectSourceBatch(sixtyFrames, sixtyFrames.Select(_ => (float[])seed.Clone()).ToArray(), 1);
        var fine = projector.ProjectSourceBatch(fineFrames, fineFrames.Select(_ => (float[])seed.Clone()).ToArray(), 4);
        check(sixty.Select((value, i) => value.SequenceEqual(fine[2 * i])).All(equal => equal),
            "source projection uses the same branch at shared 60/120 Hz sample times");
        foreach(float invalid in new[]{float.NaN,-1f,frames[0].time}){
            float old=frames[1].time;frames[1].time=invalid;bool rejected=false;
            try{projector.ProjectSourceBatch(frames,seeds,1);}catch(InvalidDataException){rejected=true;}finally{frames[1].time=old;}
            check(rejected,"upper lookahead rejects invalid or unordered sample time: "+invalid);
        }
    }
}
