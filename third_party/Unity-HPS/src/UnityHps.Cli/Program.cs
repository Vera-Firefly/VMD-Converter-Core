using System.Globalization;
using UnityHps;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine("""
        UnityHps - standalone calibrated HumanPose conversion (no Unity runtime)
        Commands:
          solve    --rig rig.json --input poses.json --output result.json [--threads 0] [--passes 2]
          decode   --rig rig.json --input human-poses.json --output poses.json
          continue-fk --rig target-rig.json --input target-fk.json --source-rig source-rig.json
                      --source raw-source-fk.json --output continuous-fk.json
          validate --rig rig.json --input reference.json --output report.json [--threads 0] [--passes 2]
                   [--max-root-mm 0.1] [--max-root-deg 0.1] [--max-muscle 0.001]
          inspect  --rig rig.json
          fbx-info --fbx model.fbx --output skeleton.json [--meta model.fbx.meta] [--bone-map map.json]
          rig      --fbx model.fbx --output rig.json [--meta file] [--bone-map map.json] [--scale 1]
          profile  --rig synthetic-calibrated.json --output profile.json.gz [--mass-rig noncoplanar-calibrated.json]
          calibrate --rig rig.json --input training.json --output calibrated-rig.json
          demo     --output directory
          self-test
        solve: local bone poses -> RootT/RootQ/muscles; optional limb goals run before encoding.
        decode: RootT/RootQ/muscles -> local bone poses using an exported calibration lattice.
        continue-fk: optional source-guided forearm roll continuity on decoded FK transforms.
        validate: compare against Unity GetHumanPose references; exit 2 when limits are exceeded.
        Frames use metres, xyzw unit quaternions, and the exported bone ordering.
        rig: offline FBX -> approximate default Humanoid rig; adjacent .fbx.meta is optional.
        Avatar-specific Unity calibration is optional and improves accuracy.
        solve/decode: --range-policy strict|tangent (default strict).
        solve/validate: --leg-geometry true|false --arm-context true|false --decoded-root true|false.
        Unity calibration export runs in the Editor; solve/decode require no Unity runtime.
        """);
        return 0;
    }
    string command = args[0];
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Options must be unique --name value pairs.");
    }
    string Need(string name) => options.TryGetValue(name, out string? value) ? value : throw new ArgumentException("Missing " + name);
    int Integer(string name, int fallback) => options.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
    float Number(string name, float fallback) => options.TryGetValue(name, out var value) ? float.Parse(value, CultureInfo.InvariantCulture) : fallback;
    string[] allowed = command switch
    {
        "self-test" => [], "demo" => ["--output"], "inspect" => ["--rig"],
        "solve" => ["--rig", "--input", "--output", "--threads", "--passes", "--range-policy", "--leg-geometry", "--arm-context", "--decoded-root"],
        "decode" => ["--rig", "--input", "--output", "--range-policy"],
        "calibrate" => ["--rig", "--input", "--output"],
        "continue-fk" => ["--rig", "--input", "--source-rig", "--source", "--output"],
        "fbx-info" => ["--fbx", "--output", "--meta", "--bone-map", "--ignore-meta"],
        "rig" => ["--fbx", "--output", "--meta", "--bone-map", "--ignore-meta", "--scale", "--profile"],
        "profile" => ["--rig", "--output", "--mass-rig"],
        "validate" => ["--rig", "--input", "--output", "--threads", "--passes", "--max-root-mm", "--max-root-deg", "--max-muscle", "--range-policy", "--leg-geometry", "--arm-context", "--decoded-root"],
        _ => throw new ArgumentException("Unknown command: " + command)
    };
    foreach (string key in options.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Unknown option: " + key);
    if (command == "self-test") { SelfTests.Run(); return 0; }
    if (command == "profile")
    {
        Rig portable=HpsJson.ReadRig(Need("--rig"));
        portable.source="Unity default responses on an authored synthetic skeleton (no target Avatar data)";
        portable.precision="portable-default-profile"; portable.bodyPositionJacobian=[];
        var compact=new List<MuscleGroup>();
        foreach(var group in portable.groups)
        {
            int[] slots=Enumerable.Range(0,group.bones.Length).Where(i=>group.bones[i]!=portable.hips&&group.samples.Any(s=>Math3.Angle(Math3.Q(s.rotations,i*4),Math3.Q(portable.bones[group.bones[i]].rotation))>0.001f)).ToArray();
            if(slots.Length==0) continue;
            compact.Add(new MuscleGroup {muscles=group.muscles,bones=slots.Select(i=>group.bones[i]).ToArray(),resolution=group.resolution,
                muscleRanges=group.muscleRanges,
                samples=group.samples.Select(s=>new LatticeSample {rotations=slots.SelectMany(i=>s.rotations.Skip(i*4).Take(4)).ToArray(),positions=slots.SelectMany(i=>portable.bones[group.bones[i]].position).ToArray()}).ToArray()});
        }
        portable.groups=compact.ToArray();
        if(options.TryGetValue("--mass-rig",out string? massPath))
        {
            Rig mass=HpsJson.ReadRig(massPath);
            if(mass.bodyPositionWeights.Length!=mass.bones.Length)throw new InvalidDataException("Mass rig needs fitted weights.");
            var weights=mass.bones.Select((b,i)=>(b,i)).Where(p=>p.b.humanBone>=0).ToDictionary(p=>p.b.humanBone,p=>mass.bodyPositionWeights[p.i]);
            for(int b=0;b<portable.bones.Length;b++)
                if(portable.bones[b].humanBone is 0 or 1 or 2 or 7)portable.bodyPositionWeights[b]=weights[portable.bones[b].humanBone];
            double sum=portable.bodyPositionWeights.Sum(w=>(double)w);
            if(!double.IsFinite(sum)||Math.Abs(sum)<1e-9)throw new InvalidDataException("Invalid mass sum.");
            for(int b=0;b<portable.bodyPositionWeights.Length;b++)portable.bodyPositionWeights[b]=(float)(portable.bodyPositionWeights[b]/sum);
        }
        using var output=File.Create(Need("--output"));
        using var gzip=new System.IO.Compression.GZipStream(output,System.IO.Compression.CompressionLevel.SmallestSize);
        System.Text.Json.JsonSerializer.Serialize(gzip,portable,HpsJsonContext.Default.Rig); return 0;
    }
    if (command == "rig")
    {
        Rig imported=OfflineRigFactory.FromFbx(Need("--fbx"),new FbxImportOptions {MetaPath=options.GetValueOrDefault("--meta"),BoneMapPath=options.GetValueOrDefault("--bone-map"),IgnoreMeta=options.GetValueOrDefault("--ignore-meta")=="true",Scale=Number("--scale",1)},options.GetValueOrDefault("--profile"));
        HpsJson.Write(Need("--output"),imported);
        Console.WriteLine($"Offline FBX rig: {imported.bones.Length} bones, humanScale={imported.humanScale:G9}; precision={imported.precision}."); return 0;
    }
    if (command == "fbx-info")
    {
        var document = FbxReader.Read(Need("--fbx"), new FbxImportOptions { MetaPath = options.GetValueOrDefault("--meta"), BoneMapPath = options.GetValueOrDefault("--bone-map"), IgnoreMeta = options.GetValueOrDefault("--ignore-meta") == "true" });
        File.WriteAllText(Need("--output"), System.Text.Json.JsonSerializer.Serialize(document, HpsJsonContext.Default.FbxDocument));
        Console.WriteLine($"FBX: {document.bones.Length} nodes; {document.bones.Count(b => b.humanBone >= 0)} human mappings; {document.morphs.Length} morphs; meta={document.usedUnityMeta}."); return 0;
    }
    if (command == "demo")
    {
        string directory = Need("--output"); Rig demoRig = Demo.CreateRig();
        HpsJson.Write(Path.Combine(directory, "rig.json"), demoRig);
        HpsJson.Write(Path.Combine(directory, "poses.json"), Demo.CreatePoses(demoRig));
        Console.WriteLine("Synthetic demo written to " + Path.GetFullPath(directory)); return 0;
    }
    Rig rig = HpsJson.ReadRig(Need("--rig"));
    if (command == "continue-fk")
    {
        PoseBatch source = HpsJson.ReadPoses(Need("--source")), target = HpsJson.ReadPoses(Need("--input"));
        if (source.version != 1 || target.version != 1 || target.frames.Length == 0 || source.frames.Length != target.frames.Length)
            throw new InvalidDataException("Source and target FK batches need equal, nonzero frame counts and schema version 1.");
        var continuation = new FkRollContinuation(HpsJson.ReadRig(Need("--source-rig")), rig);
        var frames = target.frames.Select((f, i) => continuation.Process(source.frames[i], f)).ToArray();
        HpsJson.Write(Need("--output"), new PoseBatch { frames = frames });
        Console.WriteLine($"FK continuity: {frames.Length} frames; {continuation.AlteredForearmFrames} forearm-frame branch changes.");
        return 0;
    }
    var policy = options.GetValueOrDefault("--range-policy", "strict") switch { "strict" => HumanoidRangePolicy.Strict, "tangent" => HumanoidRangePolicy.Tangent, _ => throw new ArgumentException("Invalid --range-policy") };
    bool Flag(string name) => options.TryGetValue(name, out string? v) && bool.Parse(v);
    var solver = new HumanoidSolver(rig, rangePolicy: policy) { FitLegGeometry = Flag("--leg-geometry"), FitDecodedArmContext = Flag("--arm-context"), ReconstructDecodedRoot = Flag("--decoded-root") };
    if (command == "inspect")
    {
        Console.WriteLine($"{rig.source}\nBones={rig.bones.Length}, muscles={rig.muscles.Length}, groups={rig.groups.Length}, humanScale={rig.humanScale:G9}");
        Console.WriteLine($"RootT: {(rig.bodyPositionWeights.Length > 0 ? "calibrated mass weights" : rig.bodyPositionJacobian.Length > 0 ? "translation probes only (not fully calibrated)" : "Hips reconstruction fallback")}; RootQ: {rig.bodyOrientationMode}"); return 0;
    }
    if (command == "calibrate")
    {
        RootCalibration.Fit(rig, HpsJson.ReadPoses(Need("--input")));
        HpsJson.Write(Need("--output"), rig);
        Console.WriteLine("Body mass weights fitted. Validate against an independent reference set before use."); return 0;
    }
    if (command == "decode")
    {
        HumanPoseBatch input = HpsJson.ReadHumanPoses(Need("--input"));
        if (input.version != 1 || input.frames.Length == 0) throw new InvalidDataException("No frames or invalid schema.");
        var frames = input.frames.Select(f => solver.Decode(f.humanPose.muscles, f.humanPose).ToFrame(f.time)).ToArray();
        HpsJson.Write(Need("--output"), new PoseBatch { frames = frames }); return 0;
    }
    PoseBatch poses = HpsJson.ReadPoses(Need("--input"));
    ResultBatch result = solver.SolveBatch(poses, Integer("--threads", 0), Integer("--passes", 2));
    if (command == "validate")
    {
        AccuracyReport report = solver.Compare(poses, result);
        HpsJson.Write(Need("--output"), report);
        Console.WriteLine(FormattableString.Invariant($"{report.frameCount} reference frames: RootT max {report.maxRootPositionMeters * 1000:G6} mm, RMS {report.rmsRootPositionMeters * 1000:G6} mm; RootQ max {report.maxRootRotationDegrees:G6} deg; muscle max {report.maxMuscleError:G6}; bone fit max {report.maxBoneFitDegrees:G6} deg."));
        float rootLimit = Number("--max-root-mm", 0.1f), rotationLimit = Number("--max-root-deg", 0.1f), muscleLimit = Number("--max-muscle", 0.001f);
        if (!float.IsFinite(rootLimit) || !float.IsFinite(rotationLimit) || !float.IsFinite(muscleLimit) || rootLimit < 0 || rotationLimit < 0 || muscleLimit < 0)
            throw new ArgumentException("Accuracy limits must be finite and non-negative.");
        bool passed = report.maxRootPositionMeters * 1000 <= rootLimit && report.maxRootRotationDegrees <= rotationLimit && report.maxMuscleError <= muscleLimit;
        Console.WriteLine(passed ? "PASS: all requested accuracy limits." : "FAIL: requested accuracy limits exceeded. See report; this is an approximation.");
        return passed ? 0 : 2;
    }
    HpsJson.Write(Need("--output"), result);
    Console.WriteLine(FormattableString.Invariant($"{result.frames.Length} frames, {result.elapsedMilliseconds:F1} ms solve time. Written to {Path.GetFullPath(Need("--output"))}"));
    return 0;
}
catch (Exception exception)
{
    if (Environment.GetEnvironmentVariable("UNITYHPS_TRACE") == "1") Console.Error.WriteLine(exception);
    Console.Error.WriteLine(exception.GetBaseException().Message); return 1;
}
