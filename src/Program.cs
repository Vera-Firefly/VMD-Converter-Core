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
        Evaluator = new PmxMotionEvaluator(model, motion, new PmxEvaluationOptions());
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
            if (Get(options,"--descriptor-output") is string descriptorOutput)
            {
                if (options.ContainsKey("--vmd") || options.ContainsKey("--jobs") ||
                    options.ContainsKey("--output") || options.ContainsKey("--output-dir"))
                    throw new ArgumentException("--descriptor-output is a standalone FBX operation; omit VMD jobs.");
                string fbx = Path.GetFullPath(RequireSingle(options,"--fbx"));
                string output = Path.GetFullPath(descriptorOutput);
                if (!string.Equals(Path.GetExtension(output),".json",StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(output,fbx,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--descriptor-output must be a new .json path.");
                IVmdDescriptorBackend backend = HpsBackendLoader.Require(Get(options,"--hps-dir")) as IVmdDescriptorBackend
                    ?? throw new InvalidOperationException("Descriptor generation requires the Unity-HPS adapter beside the EXE.");
                backend.ExportDescriptor(fbx,Get(options,"--meta"),Get(options,"--bone-map"),
                    Get(options,"--profile"),output);
                Console.WriteLine(JsonSerializer.Serialize(new { descriptor = output, fbx }));
                return 0;
            }
            bool nlu = options.ContainsKey("--nlu");
            bool animWriter = options.ContainsKey("--anim-w");
            VmdAnimationMode animationMode = Get(options,"--anim-mode")?.ToLowerInvariant() switch
            {
                null => VmdAnimationMode.Legacy,
                "transform" => VmdAnimationMode.Transform,
                "humanoid" => VmdAnimationMode.Humanoid,
                _ => throw new ArgumentException("--anim-mode must be transform or humanoid.")
            };
            if (animationMode != VmdAnimationMode.Legacy && !animWriter)
                throw new ArgumentException("--anim-mode requires --anim-w.");
            if (animationMode == VmdAnimationMode.Transform && nlu)
                throw new ArgumentException("--anim-mode transform cannot be combined with --nlu.");
            if (options.ContainsKey("--coverage-report") && animationMode != VmdAnimationMode.Transform)
                throw new ArgumentException("--coverage-report requires --anim-mode transform.");
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
            if (rigOnly && !animWriter) throw new ArgumentException("--rig-only requires --anim-w.");
            List<PoseJob> jobs = rigOnly ? new() : BuildJobs(options);
            if (Get(options,"--expression-vmd") is string expression) {
                if(jobs.Count!=1)throw new ArgumentException("--expression-vmd is single-job only; use expressionVmd in --jobs.");
                jobs[0].ExpressionVmd=expression;
            }
            if(Get(options,"--morph-map") is string map) foreach(var job in jobs)job.MorphMap=map;
            if (!rigOnly && jobs.Count == 0) throw new ArgumentException("At least one VMD job is required.");
            string? rigPath = Get(options,"--rig") is string rigArgument ? Path.GetFullPath(rigArgument) : null;
            bool curveOutput=animWriter || rigPath!=null;
            if(nlu&&!curveOutput)throw new ArgumentException("--nlu requires --rig, or use --anim-w with --fbx/--hps-rig.");
            if(!curveOutput && options.Keys.Any(k=>k is "--output-kind" or "--fbx" or "--hps-rig" or "--descriptor" or "--hps-dir" or "--range-policy" or "--leg-fit" or "--duration"))
                throw new ArgumentException("Animation options require --anim-w or an exported Unity --rig. Raw PMX pose preprocessing does not emit animation curves.");
            IVmdHumanoidBackend? hps=curveOutput?HpsBackendLoader.Require(Get(options,"--hps-dir")):null;
            if(animWriter&&rigPath!=null)throw new ArgumentException("Use --fbx or --hps-rig with --anim-w; --rig is the Unity bridge curve contract.");
            string? originalRigJson=rigPath==null?null:File.ReadAllText(rigPath);
            VmdTargetRigData? targetRig = rigPath == null ? null :
                JsonSerializer.Deserialize<VmdTargetRigData>(originalRigJson!,
                    new JsonSerializerOptions { IncludeFields = true });
            if (rigPath != null && (targetRig == null ||
                MathF.Abs(targetRig.sampleRate - sampleRate) > 0.0001f))
                throw new InvalidDataException("Invalid target rig or sample rate.");
            VmdExternalOutputKind kind=Get(options,"--output-kind") is string requestedKind
                ?VmdOutputModes.Parse(requestedKind):targetRig?.outputKind??VmdExternalOutputKind.HumanoidIk;
            if (targetRig != null) targetRig.outputKind = kind;
            if (animWriter)
            {
                bool hasFbx=options.ContainsKey("--fbx"),hasHpsRig=options.ContainsKey("--hps-rig");
                bool hasDescriptor=options.ContainsKey("--descriptor");
                if (animationMode == VmdAnimationMode.Transform && !hasFbx && !hasDescriptor)
                    throw new ArgumentException("--anim-mode transform requires --fbx or --descriptor.");
                if (animationMode == VmdAnimationMode.Transform && options.ContainsKey("--rig-output"))
                    throw new ArgumentException("--rig-output exports a Humanoid rig and is unavailable in transform mode.");
                if (hasFbx && hasHpsRig)
                    throw new ArgumentException("Use either --fbx or --hps-rig, not both; the rig calibration source must be unambiguous.");
                if (hasDescriptor && hasHpsRig)
                    throw new ArgumentException("Use either --descriptor or --hps-rig, not both.");
                if (hasDescriptor && !hasFbx && options.ContainsKey("--meta"))
                    throw new ArgumentException("--meta requires --fbx; the descriptor already contains importer rest data.");
                if (animationMode == VmdAnimationMode.Humanoid && VmdOutputModes.IsFk(kind))
                    throw new ArgumentException("--anim-mode humanoid cannot use --output-kind target-fk.");
                if (animationMode != VmdAnimationMode.Transform && kind != VmdExternalOutputKind.HumanoidIk && !hasFbx && !hasHpsRig && !hasDescriptor)
                    throw new ArgumentException("Target Humanoid/FK output requires --fbx, --hps-rig or --descriptor.");

            }
            if(animationMode==VmdAnimationMode.Transform && kind!=VmdExternalOutputKind.TargetFk)
                throw new ArgumentException("--anim-mode transform supports target-fk only. Use the default mode for calibrated arm/leg/all output.");
            if ((options.ContainsKey("--meta") || options.ContainsKey("--bone-map")) && !options.ContainsKey("--fbx"))
                throw new ArgumentException("--meta and --bone-map require --fbx.");
            if ((options.ContainsKey("--calibrate") || options.ContainsKey("--residual")) && animationMode != VmdAnimationMode.Transform)
                throw new ArgumentException("--calibrate and --residual apply only to --anim-mode transform.");
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
                    DescriptorPath=Get(options,"--descriptor"), AnimationMode=animationMode,
                    PositionScale=Number(options,"--position-scale",targetRig?.positionScale??0.08f),
                    CoordinateConversion=Integer(options,"--coordinate",targetRig?.coordinateConversion??2),
                    SampleRate=sampleRate, OutputKind=kind, Passes=Integer(options,"--passes",3),
                    RangePolicy=Get(options,"--range-policy")??"tangent", LegFit=Get(options,"--leg-fit")??"auto",
                    FkContinuity=Get(options,"--fk-continuity") is string continuity?bool.Parse(continuity):true,
                    DurationSeconds=Get(options,"--duration") is string durationValue?float.Parse(durationValue,CultureInfo.InvariantCulture):null,
                    DiagnosticsDirectory=Get(options,"--diagnostics-dir"),
                    CurveError=Number(options,"--curve-error",0.00001f), RotationErrorDegrees=Number(options,"--rotation-error",0.01f), PositionError=Number(options,"--position-error",0.00005f), BakeMorphs=Get(options,"--bake-morphs") is string morphs ? bool.Parse(morphs) : targetRig?.bakeMorphs??true,
                    TargetLegIk=Get(options,"--target-leg-ik") is string leg ? bool.Parse(leg) : null,
                    StructuralBones=Get(options,"--structural-bones") is string structural && bool.Parse(structural),
                    CalibrationPath=Get(options,"--calibrate"), ResidualPath=Get(options,"--residual") };
                if (Get(options,"--calibrate") is string calibrationFile && !File.Exists(calibrationFile))
                    throw new ArgumentException($"--calibrate file not found: {calibrationFile}");
                if (Get(options,"--residual") is string residualFile && !File.Exists(residualFile))
                    throw new ArgumentException($"--residual file not found: {residualFile}");
                if (!float.IsFinite(settings.PositionScale) || settings.PositionScale <= 0 || settings.CoordinateConversion is <0 or >3 || settings.Passes is <1 or >20 || !float.IsFinite(settings.CurveError) || settings.CurveError < 0 || !float.IsFinite(settings.RotationErrorDegrees) || settings.RotationErrorDegrees<0 || !float.IsFinite(settings.PositionError) || settings.PositionError<0)
                    throw new ArgumentException("Invalid offline scale, coordinate, passes or curve error.");
                if(settings.DurationSeconds is float limit&&(!float.IsFinite(limit)||limit<=0))throw new ArgumentException("--duration must be positive and finite.");
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
                float duration = Math.Min(maximumFrame / 30f,Get(options,"--duration") is string requestedDuration?float.Parse(requestedDuration,CultureInfo.InvariantCulture):float.PositiveInfinity);
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
                        float vmdFrame = sample == sampleCount - 1 ? duration*30f :
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
                VmdTargetCurveData? curvesForCoverage = null;
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
                            VmdTargetCurveData curves=hpsSession!.Solve(motion,samples,threads,framePool.ConcurrentScheduler);
                            TrimCurves(curves,duration);
                            curvesForCoverage = curves;
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
                    if (Get(options,"--coverage-report") is string coveragePath && hpsSession is IVmdCoverageSession coverage)
                    {
                        string report = jobs.Count == 1 ? Path.GetFullPath(coveragePath) :
                            Path.Combine(Path.GetFullPath(coveragePath),Path.GetFileNameWithoutExtension(outputPath)+".coverage.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                        File.WriteAllText(report,coverage.CoverageJson(motion,curvesForCoverage!),new UTF8Encoding(false));
                    }
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
                    backend=hpsSession==null?"PMX-preprocessing":"Unity-HPS",
                    format = animWriter ? "unity-anim" : jobRig == null ? "pose" :
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

    private static void TrimCurves(VmdTargetCurveData data,float duration)
    {
        foreach(var curve in data.Curves)
        {
            if(curve.Times[^1]<=duration)continue;
            int end=Array.BinarySearch(curve.Times,duration);
            if(end>=0){curve.Times=curve.Times[..(end+1)];curve.Values=curve.Values[..(end+1)];continue;}
            end=~end;
            float value=end==0?curve.Values[0]:curve.Values[end-1]+(curve.Values[end]-curve.Values[end-1])*
                (duration-curve.Times[end-1])/(curve.Times[end]-curve.Times[end-1]);
            curve.Times=curve.Times[..end].Append(duration).ToArray();curve.Values=curve.Values[..end].Append(value).ToArray();
        }
    }

    private static Dictionary<string, List<string>> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (args.Length == 0)
            throw new ArgumentException(Usage);
        string[] flags=["--nlu","--anim-w","--rig-only"];
        string[] valuedOptions=["--pmx","--vmd","--output","--output-dir","--jobs","--rig","--output-kind","--sample-rate","--threads","--fbx","--hps-rig","--hps-dir","--meta","--bone-map","--profile","--rig-output","--position-scale","--coordinate","--passes","--curve-error","--target-leg-ik","--job-threads","--memory-mb","--bake-morphs","--expression-vmd","--morph-map","--rotation-error","--position-error","--anim-mode","--descriptor","--descriptor-output","--coverage-report","--structural-bones","--calibrate","--residual","--range-policy","--leg-fit","--fk-continuity","--duration","--diagnostics-dir"];
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
        VMD Converter Core - PMX/VMD preprocessing and HPS animation conversion
        Usage:
          VmdConverterCore --pmx model.pmx --vmd dance.vmd --output dance.vccpose
          VmdConverterCore --anim-w --pmx model.pmx --vmd dance.vmd --output dance.anim [--fbx avatar.fbx]
          VmdConverterCore --anim-w --pmx model.pmx --vmd a.vmd --vmd b.vmd --output-dir out [options]
          VmdConverterCore --anim-w --pmx model.pmx --jobs jobs.json [options]
          VmdConverterCore --fbx avatar.fbx --descriptor-output avatar.json
        Input/output:
          --pmx file             Source skeleton, PMX constraints and IK
          --vmd file             Repeat for multiple motions
          --output file          Single-job output
          --output-dir dir       Batch output; duplicate basenames rejected
          --jobs file            {"jobs":[{"vmd":"...","output":"...","expressionVmd":"...","morphMap":"..."}]}
          --anim-w               Write Unity YAML .anim offline; Unity-HPS is REQUIRED
          --rig file             Unity-exported legacy binding JSON -> .vcccurves (requires HPS)
          --nlu                  Compatibility alias for HPS curve output; no separate solver
          --output-kind mode     humanoid-ik (default)
                                target-humanoid, target-humanoid-all, target-humanoid-arm, target-humanoid-leg
                                target-fk, target-fk-all, target-fk-arm, target-fk-leg
                                Normal target modes retain the leg-region policy.
        Rig selection:
          --fbx file             Explicit FBX target; adjacent .meta used automatically
          --hps-rig file         Explicit HPS rig; mutually exclusive with --fbx
          --descriptor file      Saved FBX descriptor (or matching --fbx)
                                Explicit targets always participate; invalid targets fail.
                                humanoid-ik uses PMX source IK; an explicit rig retargets the whole pose without target limb reconstruction.
                                All target-* modes require a target.
          --meta file            Override FBX importer metadata
          --bone-map file        Human-bone to FBX node mapping JSON
          --profile file         Portable HPS response profile override
          --hps-dir dir          HPS adapter/SDK folder; default executable folder
          --rig-output file      Save selected/generated HPS rig
          --rig-only             Generate rig and exit; requires --anim-w
          --descriptor-output f  Export FBX descriptor and exit; no VMD jobs
        Sampling and fitting:
          --sample-rate fps      Default 30; (0,240]
          --position-scale n     PMX metres/unit; default 0.08
          --coordinate n         0=flip Z, 1=flip X, 2=Y180 (default), 3=identity
          --passes n             Default 3; 1..20
          --range-policy policy  tangent (default) or strict calibrated range
          --leg-fit policy       auto (native calibration: geometry; FBX: rotation), rotation, geometry
          --fk-continuity bool   Source-guided equivalent FK branch selection; default true
          --target-leg-ik bool   Additional target IK; default false
          --curve-error n        Muscle reduction tolerance; default 0.00001; 0 keeps all keys
          --rotation-error n     FK angular tolerance in degrees; default 0.01
          --position-error n     FK position tolerance in metres; default 0.00005
          --threads n            Total worker budget, 1..256; default CPU count minus one
          --job-threads n        Parallel files; default min(2,jobs,threads); 1..32
          --memory-mb n          Estimated pose-cache budget; default 256 MiB (not RSS limit)
          --duration seconds     Positive output duration limit
        Morphs:
          --bake-morphs bool     Default true
          --expression-vmd file Separate facial VMD (single job; manifest expressionVmd for batches)
          --morph-map file       {"mappings":[{"source":"smile","name":"Happy","path":"Body"}]}
                                Unmapped morphs use their source name at Body.
        Additional output:
          --anim-mode mode       humanoid (default pipeline) or transform (full-FBX target-fk)
          --coverage-report path Transform mapping report file or batch directory
          --structural-bones b   Map intervening transform helpers; default false
          --calibrate file       Measured axes for transform mode
          --residual file        Per-bone correction for transform mode
          --diagnostics-dir dir  Optional pipeline-stage pose files
          --help, -h             Show help
        """;
}
