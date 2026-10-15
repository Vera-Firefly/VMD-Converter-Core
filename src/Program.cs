using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;
using System.Diagnostics;

namespace VeraFirefly.VmdConverter.Core.Executable;

internal sealed class PoseJob
{
    public string Vmd { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string? ExpressionVmd { get; set; }
    public string? MorphMap { get; set; }
}

internal sealed class PoseJobManifest
{
    public List<PoseJob> Jobs { get; set; } = new();
}

internal sealed class EvaluationWorker
{
    public readonly PmxMotionEvaluator Evaluator;
    public readonly PmxPose ReusablePose;

    public EvaluationWorker(PmxModel model, VmdMotion motion)
    {
        Evaluator = new PmxMotionEvaluator(model, motion);
        ReusablePose = Evaluator.CreatePose();
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            {
                Console.WriteLine(Usage);
                return 0;
            }
            Dictionary<string, List<string>> options = ParseArguments(args);
            bool nlu = options.ContainsKey("--nlu");
            bool animWriter = options.ContainsKey("--anim-w");
            string pmxPath = RequireSingle(options, "--pmx");
            float sampleRate = options.TryGetValue("--sample-rate", out List<string>? rates)
                ? float.Parse(rates.Single(), CultureInfo.InvariantCulture) : 30f;
            int threads = options.TryGetValue("--threads", out List<string>? counts)
                ? int.Parse(counts.Single(), CultureInfo.InvariantCulture)
                : Math.Max(1, Environment.ProcessorCount - 1);
            if (sampleRate <= 0f || sampleRate > 240f || !float.IsFinite(sampleRate))
                throw new ArgumentOutOfRangeException("--sample-rate");
            if (threads < 1 || threads > 256) throw new ArgumentOutOfRangeException("--threads");
            bool rigOnly = options.ContainsKey("--rig-only");
            if (rigOnly && !animWriter) throw new ArgumentException("--rig-only requires --anim-w (HPS is probed only in that mode).");
            List<PoseJob> jobs = rigOnly ? new() : BuildJobs(options);
            if (Get(options,"--expression-vmd") is string expression) {
                if(jobs.Count!=1)throw new ArgumentException("--expression-vmd is single-job only; use expressionVmd in --jobs.");
                jobs[0].ExpressionVmd=expression;
            }
            if(Get(options,"--morph-map") is string map) foreach(var job in jobs)job.MorphMap=map;
            if (!rigOnly && jobs.Count == 0) throw new ArgumentException("At least one VMD job is required.");
            // This gate is intentional: the regular Unity/NLU path never loads HPS.
            IVmdHumanoidBackend? hps = animWriter ? HpsBackendLoader.Find(Get(options,"--hps-dir")) : null;
            if (!animWriter && options.Keys.Any(k => k is "--fbx" or "--hps-rig" or "--rig-output" or "--hps-dir" or "--meta" or "--bone-map" or "--profile" or "--passes" or "--curve-error" or "--target-leg-ik" or "--position-scale" or "--coordinate" or "--bake-morphs" or "--morph-map" or "--rotation-error" or "--position-error"))
                throw new ArgumentException("Offline HPS options require --anim-w. Normal conversion uses --rig and the original solver.");

            string? rigPath = options.TryGetValue("--rig", out List<string>? rigs)
                ? Path.GetFullPath(rigs.Single()) : null;
            string? originalRigJson=rigPath==null?null:File.ReadAllText(rigPath);
            VmdTargetRigData? targetRig = rigPath == null ? null :
                JsonSerializer.Deserialize<VmdTargetRigData>(originalRigJson!,
                    new JsonSerializerOptions { IncludeFields = true });
            if (rigPath != null && (targetRig == null ||
                MathF.Abs(targetRig.sampleRate - sampleRate) > 0.0001f))
                throw new InvalidDataException("Invalid target rig or sample rate.");
            VmdExternalOutputKind kind = Get(options,"--output-kind")?.ToLowerInvariant() switch
            {
                null => targetRig?.outputKind ?? VmdExternalOutputKind.HumanoidIk,
                "humanoid-ik" => VmdExternalOutputKind.HumanoidIk,
                "target-humanoid" => VmdExternalOutputKind.TargetCalibratedHumanoid,
                "target-fk" => VmdExternalOutputKind.TargetFk,
                _ => throw new ArgumentException("Unknown --output-kind value.")
            };
            if (targetRig != null) targetRig.outputKind = kind;
            if (hps == null && targetRig == null && (nlu || animWriter || options.ContainsKey("--output-kind")))
                throw new ArgumentException("The original solver requires --rig for curve/anim output. Install Unity-HPS beside the EXE for offline FBX/PMX rig generation.");
            if (nlu && kind == VmdExternalOutputKind.TargetFk)
                throw new ArgumentException("--nlu is for Humanoid Root/Muscle curves, not target-fk.");
            byte[]? rigHash = rigPath == null ? null : PmxPreparedPoseFile.HashFile(rigPath);

            pmxPath = Path.GetFullPath(pmxPath);
            PmxModel model = PmxParser.Parse(pmxPath);
            model.ValidateHierarchy();
            model.GetRestLocalPositions();
            model.GetTransformOrder();
            byte[] pmxHash = PmxPreparedPoseFile.HashFile(pmxPath);
            IVmdHumanoidSession? hpsSession = null;
            if (hps != null)
            {
                var settings = new VmdHumanoidBackendOptions {
                    FbxPath=Get(options,"--fbx"), HpsRigPath=Get(options,"--hps-rig"), MetaPath=Get(options,"--meta"),
                    BoneMapPath=Get(options,"--bone-map"), ProfilePath=Get(options,"--profile"),
                    PositionScale=Number(options,"--position-scale",targetRig?.positionScale??0.08f),
                    CoordinateConversion=Integer(options,"--coordinate",targetRig?.coordinateConversion??2),
                    SampleRate=sampleRate, OutputKind=kind, Passes=Integer(options,"--passes",2),
                    CurveError=Number(options,"--curve-error",0.00001f), RotationErrorDegrees=Number(options,"--rotation-error",0.01f), PositionError=Number(options,"--position-error",0.00005f), BakeMorphs=Get(options,"--bake-morphs") is string morphs ? bool.Parse(morphs) : targetRig?.bakeMorphs??true,
                    TargetLegIk=Get(options,"--target-leg-ik") is string leg ? bool.Parse(leg) : null };
                if (!float.IsFinite(settings.PositionScale) || settings.PositionScale <= 0 || settings.CoordinateConversion is <0 or >3 || settings.Passes is <1 or >20 || !float.IsFinite(settings.CurveError) || settings.CurveError < 0 || !float.IsFinite(settings.RotationErrorDegrees) || settings.RotationErrorDegrees<0 || !float.IsFinite(settings.PositionError) || settings.PositionError<0)
                    throw new ArgumentException("Invalid offline scale, coordinate, passes or curve error.");
                hpsSession=hps.Create(model,settings,targetRig);
                targetRig=hpsSession.AnimationRig;
                rigHash ??= hpsSession.RigIdentity;
                Console.Error.WriteLine($"Humanoid backend: {hps.Name}; precision={hpsSession.Precision}");
                if (Get(options,"--rig-output") is string export) hpsSession.SaveRig(export);
            }
            if (rigOnly)
            {
                if(hpsSession==null || !options.ContainsKey("--rig-output")) throw new ArgumentException("--rig-only requires Unity-HPS and --rig-output.");
                return 0;
            }
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var job in jobs)
            {
                string path=Path.GetFullPath(job.Output);
                if(!destinations.Add(path)) throw new ArgumentException("Duplicate batch output: "+path);
                if(string.Equals(path,Path.GetFullPath(job.Vmd),StringComparison.OrdinalIgnoreCase) || string.Equals(path,pmxPath,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Output must not overwrite an input file: "+path);
                if(animWriter != string.Equals(Path.GetExtension(path),".anim",StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--anim-w jobs require .anim output; normal jobs must not use .anim.");
            }
            Console.Error.WriteLine($"PMX: {model.Bones.Count} bones; {jobs.Count} VMD job(s)");
            int jobThreads=Integer(options,"--job-threads", Math.Min(2,Math.Min(jobs.Count,threads)));
            if(jobThreads<1||jobThreads>32||jobThreads>threads)throw new ArgumentException("--job-threads must be 1..min(32,--threads).");
            int memoryMb=Integer(options,"--memory-mb",256);
            if(memoryMb<32||memoryMb>8192)throw new ArgumentException("--memory-mb must be 32..8192.");
            var budget=new PoseMemoryBudget((long)memoryMb*1024*1024);
            var framePool=new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default,threads);
            int frameThreads=Math.Max(1,threads/Math.Min(jobThreads,jobs.Count));
            Console.Error.WriteLine($"Parallel jobs={jobThreads}; shared frame pool={threads}; pose budget={memoryMb} MiB (not a process RSS limit)");
            Parallel.For(0,jobs.Count,new ParallelOptions {MaxDegreeOfParallelism=jobThreads},index=>
            {
                // Legacy solvers resolve and mutate bone bindings. Never share
                // those mutable bindings between concurrent VMD jobs.
                VmdTargetRigData? jobRig=hpsSession==null&&targetRig!=null&&jobs.Count>1
                    ? JsonSerializer.Deserialize<VmdTargetRigData>(originalRigJson!,new JsonSerializerOptions {IncludeFields=true})
                    : targetRig;
                if(jobRig!=null)jobRig.outputKind=kind;
                var timer=Stopwatch.StartNew();
                PoseJob job = jobs[index];
                string vmdPath = Path.GetFullPath(job.Vmd);
                string outputPath = Path.GetFullPath(job.Output);
                if (animWriter && !string.Equals(Path.GetExtension(outputPath),
                        ".anim", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--anim-w requires an .anim output path: " + outputPath);
                if (!animWriter && string.Equals(Path.GetExtension(outputPath),
                        ".anim", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(".anim output requires --anim-w: " + outputPath);
                VmdMotion motion = VmdParser.Parse(vmdPath);
                if (!string.IsNullOrEmpty(job.ExpressionVmd) &&
                    !string.Equals(Path.GetFullPath(job.ExpressionVmd), vmdPath,
                        StringComparison.OrdinalIgnoreCase))
                    motion.MergeExpressionMorphs(
                        VmdParser.Parse(Path.GetFullPath(job.ExpressionVmd)));
                motion.PrepareForConcurrentSampling();
                uint maximumFrame = motion.MaximumFrame;
                float duration = maximumFrame / 30f;
                int sampleCount = Math.Max(1, checked((int)MathF.Ceiling(duration * sampleRate)) + 1);
                if ((long)sampleCount * model.Bones.Count > 5000000)
                    throw new InvalidDataException("This motion exceeds the pose cache size limit.");
                using var reservation=budget.Reserve((long)sampleCount * (model.Bones.Count*64L+(hpsSession?.EstimatedBytesPerSample??4096)));
                var samples = new PmxPose[sampleCount];
                int completed = 0;
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = threads, TaskScheduler=framePool.ConcurrentScheduler };
                Parallel.For(0, sampleCount, parallel,
                    () => new EvaluationWorker(model, motion),
                    (sample, _, worker) =>
                    {
                        float vmdFrame = sample == sampleCount - 1 ? maximumFrame :
                            MathF.Abs(sampleRate - 30f) <= 1e-6f
                                ? MathF.Min(maximumFrame, sample)
                                : MathF.Min(maximumFrame, sample / sampleRate * 30f);
                        PmxPose evaluated = worker.Evaluator.Evaluate(vmdFrame,
                            worker.ReusablePose);
                        var snapshot = new PmxPose(model.Bones.Count, true);
                        Array.Copy(evaluated.WorldPositions, snapshot.WorldPositions,
                            model.Bones.Count);
                        Array.Copy(evaluated.WorldRotations, snapshot.WorldRotations,
                            model.Bones.Count);
                        Array.Copy(evaluated.LocalRotations, snapshot.LocalRotations,
                            model.Bones.Count);
                        samples[sample] = snapshot;
                        Interlocked.Increment(ref completed);
                        return worker;
                    }, _ => { });
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                string partial = outputPath + ".partial";
                try
                {
                    using (var stream = File.Create(partial))
                    {
                        if (jobRig == null)
                            PmxPreparedPoseFile.Write(stream, pmxHash,
                                PmxPreparedPoseFile.HashFile(vmdPath), sampleRate,
                                duration, maximumFrame, samples);
                        else
                        {
                            VmdTargetCurveData curves = hpsSession != null ? hpsSession.Solve(motion,samples,hpsSession.UsesSharedFramePool?threads:frameThreads,framePool.ConcurrentScheduler) : jobRig.outputKind ==
                                VmdExternalOutputKind.TargetFk
                                ? VmdTargetFkCurveSolver.Solve(model, motion,
                                    samples, jobRig, frameThreads)
                                : nlu
                                    ? VmdHumanoidCurveSolver.SolveNlu(model, motion,
                                        samples, jobRig, frameThreads)
                                    : VmdHumanoidCurveSolver.Solve(model, motion,
                                        samples, jobRig, frameThreads);
                            if (animWriter) ApplyMorphMap(curves,job.MorphMap);
                            if (animWriter)
                                VmdAnimYamlWriter.Write(stream,
                                    Path.GetFileNameWithoutExtension(outputPath), curves,
                                    jobRig);
                            else
                                VmdTargetCurveFile.Write(stream, pmxHash,
                                    VmdTargetCurveFile.HashMotionSources(
                                        vmdPath, job.ExpressionVmd!), rigHash!, curves);
                        }
                    }
                    File.Move(partial, outputPath, true);
                }
                finally
                {
                    if (File.Exists(partial)) File.Delete(partial);
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    vmd = vmdPath,
                    output = outputPath,
                    sampleCount,
                    boneCount = model.Bones.Count,
                    bytes = new FileInfo(outputPath).Length,
                    elapsedMs=timer.ElapsedMilliseconds,
                    backend=hpsSession==null?"VMDCC-original":"Unity-HPS",
                    format = animWriter ? "unity-anim-debug" : jobRig == null ? "pose" :
                        (nlu ? "nlu-" : string.Empty) +
                        jobRig.outputKind.ToString() + "-curves"
                }));
                Console.Error.WriteLine($"[{index + 1}/{jobs.Count}] {Path.GetFileName(vmdPath)}: {completed} samples");
            });
            framePool.Complete();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void ApplyMorphMap(VmdTargetCurveData curves,string? file)
    {
        if(string.IsNullOrEmpty(file))return;
        using var json=JsonDocument.Parse(File.ReadAllText(file));
        var mappings=new Dictionary<string,(string name,string path)>(StringComparer.Ordinal);
        foreach(var item in json.RootElement.GetProperty("mappings").EnumerateArray())
        {
            string source=item.GetProperty("source").GetString()??"",name=item.GetProperty("name").GetString()??"";
            string path=item.TryGetProperty("path",out var p)?p.GetString()??"Body":"Body";
            if(source.Length>0&&name.Length>0)mappings[source]=(name,path.Length==0?"Body":path);
        }
        foreach(var curve in curves.Curves)
            if(curve.BindingKind==1&&curve.Property.StartsWith("blendShape.",StringComparison.Ordinal)&&mappings.TryGetValue(curve.Property[11..],out var target))
            {curve.Property="blendShape."+target.name;curve.Path=target.path;}
        var duplicate=curves.Curves.GroupBy(c=>(c.BindingKind,c.Path,c.Property)).FirstOrDefault(g=>g.Count()>1);
        if(duplicate!=null)throw new InvalidDataException("Morph mapping produces a duplicate output binding: "+duplicate.Key);
    }

    private static List<PoseJob> BuildJobs(Dictionary<string, List<string>> options)
    {
        if (options.TryGetValue("--jobs", out List<string>? manifests))
        {
            string json = File.ReadAllText(manifests.Single());
            PoseJobManifest? manifest = JsonSerializer.Deserialize<PoseJobManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return manifest?.Jobs ?? throw new InvalidDataException("The job manifest is empty.");
        }
        if(!options.TryGetValue("--vmd",out var inputs)||inputs.Count==0) throw new ArgumentException("Specify --vmd or --jobs.");
        if(Get(options,"--output-dir") is string directory)
        {
            if(options.ContainsKey("--output")) throw new ArgumentException("Use --output or --output-dir, not both.");
            string extension=options.ContainsKey("--anim-w")?".anim":options.ContainsKey("--rig")?".vcccurves":".vccpose";
            return inputs.Select(v=>new PoseJob {Vmd=v,Output=Path.Combine(directory,Path.GetFileNameWithoutExtension(v)+extension)}).ToList();
        }
        if(inputs.Count!=1) throw new ArgumentException("Repeated --vmd requires --output-dir (or use --jobs).");
        return new List<PoseJob> { new() { Vmd = inputs[0], Output = RequireSingle(options,"--output") } };
    }

    private static string? Get(Dictionary<string,List<string>> options,string name)=>options.ContainsKey(name)?RequireSingle(options,name):null;
    private static int Integer(Dictionary<string,List<string>> options,string name,int fallback)=>Get(options,name) is string value?int.Parse(value,CultureInfo.InvariantCulture):fallback;
    private static float Number(Dictionary<string,List<string>> options,string name,float fallback)=>Get(options,name) is string value?float.Parse(value,CultureInfo.InvariantCulture):fallback;

    private static string RequireSingle(Dictionary<string, List<string>> options, string name)
    {
        if (!options.TryGetValue(name, out List<string>? values) || values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0]))
            throw new ArgumentException("Specify exactly one " + name + " value.");
        return values[0];
    }

    private static Dictionary<string, List<string>> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (args.Length == 0)
            throw new ArgumentException(Usage);
        string[] flags=["--nlu","--anim-w","--rig-only"];
        string[] valuedOptions=["--pmx","--vmd","--output","--output-dir","--jobs","--rig","--output-kind","--sample-rate","--threads","--fbx","--hps-rig","--hps-dir","--meta","--bone-map","--profile","--rig-output","--position-scale","--coordinate","--passes","--curve-error","--target-leg-ik","--job-threads","--memory-mb","--bake-morphs","--expression-vmd","--morph-map","--rotation-error","--position-error"];
        for (int index = 0; index < args.Length;)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown argument: " + args[index]);
            if (flags.Contains(args[index],StringComparer.OrdinalIgnoreCase))
            {
                string flag = args[index];
                if (options.ContainsKey(flag))
                    throw new ArgumentException(flag + " may be specified only once.");
                options[flag] = new List<string>();
                index++;
                continue;
            }
            if(!valuedOptions.Contains(args[index],StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unknown option: "+args[index]);
            if (index + 1 >= args.Length || args[index+1].StartsWith("--",StringComparison.Ordinal))
                throw new ArgumentException("Missing value for " + args[index]);
            if (!options.TryGetValue(args[index], out List<string>? values))
                options[args[index]] = values = new List<string>();
            values.Add(args[index + 1]);
            index += 2;
        }
        foreach(var pair in options)
            if(pair.Key!="--vmd"&&pair.Value.Count>1) throw new ArgumentException("Repeated option: "+pair.Key);
        if(options.ContainsKey("--jobs")&&(options.ContainsKey("--vmd")||options.ContainsKey("--output")||options.ContainsKey("--output-dir"))) throw new ArgumentException("--jobs cannot be combined with --vmd/--output/--output-dir.");
        return options;
    }

    private const string Usage = """
        VMD Converter Core - PMX/VMD pose and curve worker (no Unity runtime)
        Usage:
          VmdConverterCore --pmx model.pmx --vmd motion.vmd --output motion.vccpose
          VmdConverterCore --pmx model.pmx --jobs jobs.json [options]
          VmdConverterCore --pmx model.pmx --vmd a.vmd --vmd b.vmd --output-dir out [options]
          VmdConverterCore --pmx model.pmx --anim-w --vmd dance.vmd --output dance.anim [--fbx avatar.fbx]
          VmdConverterCore --pmx model.pmx --anim-w --rig-only --rig-output pmx-rig.json [--fbx avatar.fbx --output-kind target-humanoid]
        Options:
          --pmx <file>           PMX model / IK source (required)
          --vmd <file>           Repeat for multiple motions; use --output-dir
          --output <file>        Single output (.vccpose, .vcccurves, or debug .anim)
          --output-dir <dir>     One file per VMD; duplicate basenames are rejected
          --jobs <file>          JSON {"jobs":[{"vmd":"...","output":"...","expressionVmd":"..."}]}
          --rig <file>           Legacy VMDCC target rig (required for original curve solver)
          --output-kind <kind>   humanoid-ik (default), target-humanoid, target-fk
          --sample-rate <fps>    Default 30; range (0,240]
          --threads <count>      Total worker budget, 1..256
          --job-threads <count>  Concurrent VMD files, default min(2,jobs,threads); 1..32
          --memory-mb <MiB>      Estimated pose-cache budget, default 256 (not total RSS)
          --expression-vmd <f>  Single-job facial VMD; batch: expressionVmd in manifest
          --nlu                 Original standalone Root/Muscle DLL pipeline
          --anim-w              EXE-only debug .anim writer. ONLY this flag enables HPS lookup.
                                If HPS exists it handles curves; otherwise original solver + --rig.
        Offline options (only with --anim-w and Unity-HPS installed):
          --hps-dir <dir>        Adapter + SDK directory (default EXE directory)
          --bake-morphs <bool>   Default true
          --morph-map <file>     {"mappings":[{"source":"smile","name":"Happy","path":"Body"}]}
                                Batch jobs may set morphMap separately
          --hps-rig <file>       Reuse HPS rig; calibrated rigs give better accuracy
          --fbx <file>           Target skeleton for target-humanoid / target-fk
                                No FBX: build canonical source rig from PMX
          --meta <file>         FBX Unity importer metadata (adjacent .meta used automatically)
          --bone-map <file>     JSON mapping human bone names to FBX node names
          --profile <file>      Optional portable HPS profile override
          --position-scale <n>  PMX metres/unit; default 0.08
          --coordinate <n>      0=flip Z, 1=flip X, 2=rotate Y180 (default), 3=identity
          --passes <n>          HPS fitting passes, 1..20; default 2
          --rotation-error <n>  FK quaternion reduction tolerance, degrees; default 0.01
          --position-error <n>  FK position reduction tolerance, metres; default 0.00005
          --curve-error <n>     Muscle curve reduction tolerance, default 0.00001; 0=all keys
          --target-leg-ik <bool> Target leg IK; default true for target-humanoid
          --rig-output <file>   Save generated/reused HPS rig JSON
          --rig-only            Generate rig then exit (also requires --anim-w)
          --help, -h            Show help
        """;
}
