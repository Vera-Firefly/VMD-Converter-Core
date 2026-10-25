using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityHps;
using VeraFirefly.VmdConverter.Core.Retarget;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace VeraFirefly.VmdConverter.Core.Hps;

/// <summary>Full-FBX transform output. This path never constructs a muscle rig.</summary>
internal sealed class VmdTransformSession : IVmdHumanoidSession, IVmdCoverageSession
{
    private readonly PmxModel model;
    private readonly VmdHumanoidBackendOptions options;
    private readonly SkeletonView view;
    private readonly int[] sourceBones;
    private readonly int[] nearestMappedAncestors;
    private readonly int hips;
    private readonly int rootMotionSource;
    private readonly float targetScale;
    private readonly string[] ambiguous;
    private readonly List<string> collapsed = new();
    private readonly Dictionary<int, int> sourceByHuman;
    private readonly HumanoidSolver? upperCanonical;
    private readonly HumanoidSolver? upperTarget;
    private readonly Rig? upperCanonicalRig;
    private readonly Rig? upperTargetRig;
    private readonly CalibratedMuscles? calibrated;
    private readonly StreamWriter? muscleDump;
    private readonly StreamWriter? canonicalDump;
    private readonly bool useBindRest;
    private readonly bool rootDirect;
    private readonly bool rootOrderPre;
    private readonly bool sourceTwist;
    private readonly bool fkAbsolute;
    private readonly Dictionary<int, int> canonicalByHuman = new();
    private readonly Q[] canonicalRestWorld = [];
    private readonly Q[] targetRestWorld = [];
    private readonly ResidualModel? residual;
    private readonly int[] viewUpperRigBone;
    private readonly (int View, int Rig)[] upperBodyAnchors = [(-1, -1), (-1, -1)];
    private readonly int[] viewResidualBone = [];

    public VmdTargetRigData AnimationRig { get; }
    public string Precision => "fbx-rest-relative-transform";
    public bool UsesSharedFramePool => true;
    public long EstimatedBytesPerSample => view.Bones.Length * 64L + 4096;
    public byte[] RigIdentity { get; }

    public VmdTransformSession(PmxModel model, VmdHumanoidBackendOptions options)
    {
        this.model = model;
        this.options = options;
        if (string.IsNullOrWhiteSpace(options.FbxPath) && string.IsNullOrWhiteSpace(options.DescriptorPath))
            throw new ArgumentException("--anim-mode transform requires --fbx or --descriptor.");
        if (options.HpsRigPath != null)
            throw new ArgumentException("--anim-mode transform requires the complete FBX hierarchy; --hps-rig contains only Humanoid nodes.");
        if (options.DescriptorPath != null)
        {
            AvatarDescriptor descriptor = AvatarDescriptorIO.Read(options.DescriptorPath);
            if (options.FbxPath != null && descriptor.sourceSha256.Length > 0 &&
                !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.FbxPath))),
                    descriptor.sourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("--descriptor and --fbx describe different FBX files.");
            view = SkeletonView.FromDocument(descriptor.skeleton);
        }
        else
            view = SkeletonView.FromFbx(options.FbxPath!,
                new FbxImportOptions { MetaPath = options.MetaPath, BoneMapPath = options.BoneMapPath });
        sourceBones = Enumerable.Repeat(-1, view.Bones.Length).ToArray();
        var humanSources = PmxRigBuilder.Map(model);
        sourceByHuman = humanSources;
        viewUpperRigBone = Enumerable.Repeat(-1, view.Bones.Length).ToArray();
        for (int b = 0; b < view.Bones.Length; b++)
            if (humanSources.TryGetValue(view.Bones[b].humanBone, out int source))
                sourceBones[b] = source;
        var used = sourceBones.Where(i => i >= 0).ToHashSet();
        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (PmxBone bone in model.Bones)
            foreach (string name in new[] { bone.NameJapanese, bone.NameEnglish })
            {
                string key = NormalizeName(name);
                if (key.Length == 0) continue;
                if (!normalized.TryAdd(key, bone.Index) && normalized[key] != bone.Index)
                    duplicateNames.Add(key);
            }
        var ambiguousList = new List<string>();
        for (int b = 0; b < view.Bones.Length; b++)
        {
            if (sourceBones[b] >= 0) continue;
            int source = model.FindBone(view.Bones[b].name);
            if (source < 0)
            {
                string key = NormalizeName(view.Bones[b].name);
                if (duplicateNames.Contains(key)) { ambiguousList.Add(view.Paths[b]); continue; }
                source = normalized.GetValueOrDefault(key, -1);
            }
            if (source >= 0 && used.Add(source)) sourceBones[b] = source;
        }
        // Structural pass: unmapped target nodes between two mapped nodes take the
        // unused PMX bones that lie between their sources. Chains of any length are
        // handled, which is what recovers helper bones such as
        // Hand -> IndexFinger1 -> IndexFinger2 -> IndexFinger3 on the source as
        // Hand.L -> IndexInterProxima.L -> IndexIntermediate.L -> IndexDistal.L.
        int structural = 0;
        var sourcePath = new List<int>();
        for (int b = 0; b < view.Bones.Length && options.StructuralBones; b++)
        {
            if (sourceBones[b] >= 0) continue;
            int parent = view.Bones[b].parent;
            if (parent < 0 || sourceBones[parent] < 0) continue;
            var chain = new List<int> { b };
            int child = -1;
            var queue = new Queue<List<int>>();
            queue.Enqueue(chain);
            var visited = new HashSet<int> { b };
            while (queue.Count > 0 && child < 0)
            {
                List<int> path = queue.Dequeue();
                int last = path[path.Count - 1];
                for (int c = 0; c < view.Bones.Length; c++)
                {
                    if (view.Bones[c].parent != last || !visited.Add(c)) continue;
                    if (sourceBones[c] >= 0) { child = c; chain = path; break; }
                    var extended = new List<int>(path) { c };
                    queue.Enqueue(extended);
                }
            }
            if (child < 0) continue;
            int parentSource = sourceBones[parent];
            int childSource = sourceBones[child];
            if (childSource < 0 || childSource >= model.Bones.Count) continue;
            sourcePath.Clear();
            for (int s = model.Bones[childSource].ParentIndex; s >= 0 && s != parentSource;
                 s = model.Bones[s].ParentIndex)
                sourcePath.Add(s);
            if (Environment.GetEnvironmentVariable("VMDCC_DEBUG_MAP") == "1" && view.Bones[b].name.Contains("Proxima"))
                Console.Error.WriteLine($"mapdbg {view.Bones[b].name} chain={chain.Count} path={sourcePath.Count} used={sourcePath.Any(s => used.Contains(s))}");
            if (sourcePath.Count < chain.Count) continue;
            // When the source chain is longer than the target chain the extra source
            // joints cannot be represented; folding them into the deepest mapped node
            // keeps every accumulated rotation (the child delta stays relative to it).
            int offset = sourcePath.Count - chain.Count;
            sourcePath.Reverse();
            if (sourcePath.Skip(offset).Any(s => used.Contains(s))) continue;
            for (int i = 0; i < chain.Count; i++)
            {
                sourceBones[chain[i]] = sourcePath[offset + i];
                used.Add(sourcePath[offset + i]);
                structural++;
            }
            if (offset > 0)
            {
                var skipped = string.Join(",", sourcePath.Take(offset)
                    .Select(index => model.Bones[index].NameJapanese));
                collapsed.Add($"{view.Paths[chain[0]]} <- {model.Bones[sourceBones[chain[0]]].NameJapanese} (folded {skipped})");
            }
        }
        if (structural > 0)
            Console.Error.WriteLine($"Structural bone mapping: {structural} helper node(s) recovered");
        if (options.BoneMapPath != null)
        {
            using var map = JsonDocument.Parse(File.ReadAllText(options.BoneMapPath));
            if (map.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("--bone-map must be a JSON object of source bone to target node/path.");
            foreach (JsonProperty pair in map.RootElement.EnumerateObject())
            {
                string target = pair.Value.GetString() ?? throw new InvalidDataException("Bone-map target must be a string.");
                int destination = Array.FindIndex(view.Paths, p => p == target);
                if (destination < 0)
                {
                    int[] matches = Enumerable.Range(0, view.Bones.Length)
                        .Where(i => view.Bones[i].name == target).ToArray();
                    if (matches.Length != 1)
                        throw new InvalidDataException("Bone-map target is missing or ambiguous: " + target);
                    destination = matches[0];
                }
                int source = HumanBones.TryGetId(pair.Name, out int human)
                    ? humanSources.GetValueOrDefault(human, -1) : model.FindBone(pair.Name);
                if (source < 0) throw new InvalidDataException("Bone-map source is missing: " + pair.Name);
                sourceBones[destination] = source;
            }
        }
        var duplicateSource = sourceBones.Where(i => i >= 0).GroupBy(i => i).FirstOrDefault(g => g.Count() > 1);
        if (duplicateSource != null)
            throw new InvalidDataException("One PMX bone maps to multiple target nodes: " + model.Bones[duplicateSource.Key].NameJapanese);
        ambiguous = ambiguousList.ToArray();
        int[] parents = view.Bones.Select(b => b.parent).ToArray();
        bool[] mapped = sourceBones.Select(i => i >= 0).ToArray();
        nearestMappedAncestors = Enumerable.Range(0, view.Bones.Length)
            .Select(i => RetargetMath.NearestMappedAncestor(parents[i], parents, mapped)).ToArray();
        useBindRest = Environment.GetEnvironmentVariable("VMDCC_BINDREST") == "1";
        rootDirect = Environment.GetEnvironmentVariable("VMDCC_ROOTDIRECT") != "0";
        // VMDCW composes the root as rest * delta (verified exactly on Kipfel and Eku);
        // VMDCC_ROOTORDER=post restores the old delta * rest order.
        rootOrderPre = Environment.GetEnvironmentVariable("VMDCC_ROOTORDER") != "post";
        sourceTwist = Environment.GetEnvironmentVariable("VMDCC_SOURCETWIST") == "1";
        // Absolute FK: local = inv(parentRest) * (sourceWorld * inv(sourceRest)) * boneRest.
        // Carries the source roll exactly instead of relying on a muscle round-trip.
        fkAbsolute = Environment.GetEnvironmentVariable("VMDCC_FKMODE") == "abs"; // experimental, see 3.0.5
        if (useBindRest) Console.Error.WriteLine("Rest basis: model rest (bindBones)");
        if (rootDirect) Console.Error.WriteLine("Root chain: direct FK (no ancestor-rest conjugation)");
        if (Environment.GetEnvironmentVariable("VMDCC_DEBUG_ROOT") == "1")
        {
            int armature = Array.FindIndex(view.Bones, b => b.name == "armature");
            Console.Error.WriteLine($"dbgroot armature={armature} mapped={(armature >= 0 && mapped[armature])}");
            for (int i = 0; i < view.Bones.Length; i++)
                if (view.Bones[i].name is "Hips" or "Spine" or "UpperArm.L" or "UpperLeg.L")
                    Console.Error.WriteLine($"dbgroot {view.Bones[i].name} idx={i} parent={view.Bones[i].parent} " +
                        $"ancestor={nearestMappedAncestors[i]} mapped={mapped[i]}");
        }
        if (!humanSources.TryGetValue(0, out int sourceHips))
            throw new InvalidDataException("PMX Hips mapping is missing.");
        hips = Array.FindIndex(sourceBones, i => i == sourceHips);
        if (hips < 0) throw new InvalidDataException("Target Hips node is not mapped; provide --bone-map.");
        rootMotionSource = FindRootMotionSource(sourceHips);
        float sourceHeight = Math.Abs(model.Bones[rootMotionSource].Position.y * options.PositionScale);
        float targetHeight = Math.Abs(view.RestWorldPositions[hips].Y);
        targetScale = sourceHeight > 1e-5f ? targetHeight / sourceHeight : 1f;
        AnimationRig = new VmdTargetRigData
        {
            sampleRate = options.SampleRate,
            positionScale = options.PositionScale,
            outputKind = VmdExternalOutputKind.TargetFk,
            bakeMorphs = options.BakeMorphs,
            morphs = view.Document.morphs.Select(m => new VmdTargetMorphBinding { name = m.name, path = m.path }).ToArray()
        };
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (options.FbxPath != null) identity.AppendData(File.ReadAllBytes(options.FbxPath));
        if (options.DescriptorPath != null) identity.AppendData(File.ReadAllBytes(options.DescriptorPath));
        if (options.MetaPath != null) identity.AppendData(File.ReadAllBytes(options.MetaPath));
        if (options.BoneMapPath != null) identity.AppendData(File.ReadAllBytes(options.BoneMapPath));
        RigIdentity = identity.GetHashAndReset();
        // The muscle-decode path (and therefore any calibration/residual model) is built on
        // the FBX target rig. Requesting it without --fbx would silently do nothing, so fail.
        bool wantsMusclePath = Environment.GetEnvironmentVariable("VMDCC_UPPER_MUSCLE") == "1" ||
                               !string.IsNullOrEmpty(options.CalibrationPath) ||
                               !string.IsNullOrEmpty(options.ResidualPath);
        if (wantsMusclePath && options.FbxPath == null)
            throw new InvalidDataException(
                (options.ResidualPath != null ? "--residual" :
                 options.CalibrationPath != null ? "--calibrate" : "VMDCC_UPPER_MUSCLE=1") +
                " needs --fbx: the upper-body decode is built from the FBX target rig, and a " +
                "descriptor-only transform run cannot apply it.");
        if ((wantsMusclePath || fkAbsolute) && options.FbxPath != null)
        {
            upperCanonicalRig = PmxRigBuilder.Build(model, options.PositionScale, options.CoordinateConversion, options.ProfilePath);
            var upperBones = new HashSet<int>(Enumerable.Range(11, 8).Concat(Enumerable.Range(24, 30)));
            if (wantsMusclePath && options.FbxPath != null)
                upperCanonical = new HumanoidSolver(upperCanonicalRig, upperBones);
            Rig targetRig = OfflineRigFactory.FromFbx(options.FbxPath!,
                new FbxImportOptions { MetaPath = options.MetaPath, BoneMapPath = options.BoneMapPath }, options.ProfilePath);
            upperTarget = new HumanoidSolver(targetRig);
            upperTargetRig = targetRig;
            var rigByHuman = targetRig.bones.Select((b, i) => (b, i))
                .Where(p => p.b.humanBone >= 0).ToDictionary(p => p.b.humanBone, p => p.i);
            for (int b = 0; b < view.Bones.Length; b++)
                if (rigByHuman.TryGetValue(view.Bones[b].humanBone, out int rigBone)) viewUpperRigBone[b] = rigBone;
            for (int side = 0; side < 2; side++)
            {
                int rootHuman = rigByHuman.ContainsKey(11 + side) ? 11 + side : 13 + side;
                int viewRoot = Array.FindIndex(view.Bones, bone => bone.humanBone == rootHuman);
                if (viewRoot >= 0 && rigByHuman.TryGetValue(rootHuman, out int rigRoot))
                    upperBodyAnchors[side] = (view.Bones[viewRoot].parent, targetRig.bones[rigRoot].parent);
            }
            if (sourceTwist || fkAbsolute)
            {
                // Rest worlds on both sides plus the canonical bone per Humanoid id, so the
                // source roll can be restored where the muscle twist channels saturate, and
                // so the absolute-FK retarget mode has a source rest to compare against.
                var canonicalRest = new Q[upperCanonicalRig.bones.Length];
                for (int i = 0; i < canonicalRest.Length; i++)
                {
                    var bone = upperCanonicalRig.bones[i];
                    Q q = Math3.Q(bone.rotation);
                    canonicalRest[i] = bone.parent < 0 ? q : Q.Normalize(canonicalRest[bone.parent] * q);
                    if (bone.humanBone >= 0) canonicalByHuman[bone.humanBone] = i;
                }
                var targetRest = new Q[targetRig.bones.Length];
                for (int i = 0; i < targetRest.Length; i++)
                {
                    var bone = targetRig.bones[i];
                    Q q = Math3.Q(bone.rotation);
                    targetRest[i] = bone.parent < 0 ? q : Q.Normalize(targetRest[bone.parent] * q);
                }
                canonicalRestWorld = canonicalRest;
                targetRestWorld = targetRest;
                if (sourceTwist || fkAbsolute)
                    Console.Error.WriteLine($"Rest worlds: {canonicalByHuman.Count} human bones");
            }
            if (Environment.GetEnvironmentVariable("VMDCC_DEBUG_FP") == "1")
            {
                string material = ResidualModel.Material(targetRig);
                Console.Error.WriteLine($"dbgfp bones={targetRig.bones.Length} fp={ResidualModel.Fingerprint(targetRig)} len={material.Length}");
                Console.Error.WriteLine("dbgfp head=" + material.Substring(0, Math.Min(180, material.Length)));
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsh-rig-material.txt"), ResidualModel.Material(targetRig));
                for (int i = 0; i < Math.Min(6, targetRig.bones.Length); i++)
                    Console.Error.WriteLine($"dbgfp {i} {targetRig.bones[i].name} parent={targetRig.bones[i].parent} human={targetRig.bones[i].humanBone}");
            }
            string? dumpRig = Environment.GetEnvironmentVariable("VMDCC_DUMP_RIG");
            if (!string.IsNullOrEmpty(dumpRig))
            {
                // Export the exact rig the muscle path uses, so a residual model can be fitted
                // against the same identity (see Motion/M5-per-avatar标定操作手册.md).
                File.WriteAllText(dumpRig!, JsonSerializer.Serialize(targetRig,
                    new JsonSerializerOptions { WriteIndented = false }));
                Console.Error.WriteLine($"Rig dump: {dumpRig} (fingerprint {ResidualModel.Fingerprint(targetRig)})");
            }
            if (!string.IsNullOrEmpty(options.ResidualPath))
            {
                residual = ResidualModel.Load(options.ResidualPath!, targetRig);
                if (residual == null)
                    throw new InvalidDataException($"Residual model could not be loaded: {options.ResidualPath}");
                Console.Error.WriteLine($"Residual model: {residual.BoneCount} bones, {residual.Features} features");
                var rigByName = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < targetRig.bones.Length; i++)
                    rigByName[targetRig.bones[i].name ?? string.Empty] = i;
                viewResidualBone = new int[view.Bones.Length];
                for (int b = 0; b < view.Bones.Length; b++)
                    viewResidualBone[b] = rigByName.TryGetValue(
                        view.Bones[b].name ?? string.Empty, out int index) ? index : -1;
            }
            if (!string.IsNullOrEmpty(options.CalibrationPath))
            {
                calibrated = CalibratedMuscles.Load(options.CalibrationPath!, targetRig);
                Console.Error.WriteLine(calibrated == null
                    ? $"Calibration: FAILED to load {options.CalibrationPath}"
                    : $"Calibration: {calibrated.EffectCount} effects over {calibrated.MappedBones} bones");
            }
            string? dumpPath = Environment.GetEnvironmentVariable("VMDCC_DUMP_MUSCLES");
            if (!string.IsNullOrEmpty(dumpPath))
            {
                muscleDump = new StreamWriter(dumpPath!, false);
                Console.Error.WriteLine($"Muscle dump: {dumpPath}");
            }
            string? canonicalPath = Environment.GetEnvironmentVariable("VMDCC_DUMP_CANONICAL");
            if (!string.IsNullOrEmpty(canonicalPath))
                canonicalDump = new StreamWriter(canonicalPath, false);
            Console.Error.WriteLine($"Upper-body muscle decode: canonical {upperCanonicalRig.bones.Length} bones, target {targetRig.bones.Length} bones, {viewUpperRigBone.Count(x => x >= 0)} view bones");
        }
        Console.Error.WriteLine($"Transform skeleton: {view.Bones.Length} FBX nodes, {mapped.Count(x => x)} mapped, {targetScale:G6} root scale");
    }

    public void SaveRig(string path) => throw new NotSupportedException(
        "Transform mode uses a full FBX skeleton, not a Humanoid --rig-output file.");

    public VmdTargetCurveData Solve(VmdMotion motion, PmxPose[] samples, int threads, TaskScheduler? scheduler = null)
    {
        if (samples.Length == 0) throw new ArgumentException("No PMX samples.");
        int count = view.Bones.Length;
        var rotations = new Q[count][];
        for (int b = 0; b < count; b++)
            if (sourceBones[b] >= 0) rotations[b] = new Q[samples.Length];
        var hipsPositions = new V[samples.Length];
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads,
            TaskScheduler = scheduler ?? TaskScheduler.Default
        };
        Parallel.For(0, samples.Length, parallel, frame =>
        {
            PmxPose sample = samples[frame];
            var worlds = new Q[count];
            float[]? frameMuscles = null;
            Q[]? frameCanonicalWorlds = null;
            if (upperCanonical != null && upperCanonicalRig != null)
            {
                int canonicalCount = upperCanonicalRig.bones.Length;
                var canonicalFrame = new PoseFrame
                {
                    time = 0,
                    positions = new float[canonicalCount * 3],
                    rotations = new float[canonicalCount * 4]
                };
                var canonicalWorlds = new Q[canonicalCount];
                var canonicalPositions = new V[canonicalCount];
                for (int i = 0; i < canonicalCount; i++)
                {
                    var bone = upperCanonicalRig.bones[i];
                    Vector3 restPosition = Math3.V(bone.position);
                    int parent = bone.parent;
                    Q restLocal = Math3.Q(bone.rotation);
                    Q localRotation = restLocal;
                    V localPosition = restPosition;
                    int human = bone.humanBone;
                    if (human >= 0 && sourceByHuman.TryGetValue(human, out int sourceBone))
                    {
                        Q world = PmxRigBuilder.Rotation(sample.WorldRotations[sourceBone], options.CoordinateConversion);
                        Q parentWorld = parent < 0 ? Q.Identity : canonicalWorlds[parent];
                        localRotation = Q.Normalize(Q.Inverse(parentWorld) * world);
                        V worldPosition = PmxRigBuilder.Position(sample.WorldPositions[sourceBone], options.CoordinateConversion) * options.PositionScale;
                        localPosition = parent < 0 ? worldPosition : Math3.Rotate(Q.Inverse(parentWorld), worldPosition - canonicalPositions[parent]);
                    }
                    canonicalPositions[i] = parent < 0 ? localPosition : canonicalPositions[parent] + Math3.Rotate(canonicalWorlds[parent], localPosition);
                    canonicalFrame.positions[i * 3] = localPosition.X;
                    canonicalFrame.positions[i * 3 + 1] = localPosition.Y;
                    canonicalFrame.positions[i * 3 + 2] = localPosition.Z;
                    canonicalWorlds[i] = parent < 0 ? localRotation : Q.Normalize(canonicalWorlds[parent] * localRotation);
                    canonicalFrame.rotations[i * 4] = localRotation.X;
                    canonicalFrame.rotations[i * 4 + 1] = localRotation.Y;
                    canonicalFrame.rotations[i * 4 + 2] = localRotation.Z;
                    canonicalFrame.rotations[i * 4 + 3] = localRotation.W;
                }
                float[] muscles = upperCanonical.SolveMuscles(canonicalFrame, options.Passes);
                frameMuscles = muscles;
                frameCanonicalWorlds = canonicalWorlds;
                if (muscleDump != null)
                    lock (muscleDump)
                        muscleDump.WriteLine(frame + "," +
                            string.Join(",", muscles.Select(m => m.ToString("G6",
                                System.Globalization.CultureInfo.InvariantCulture))));
                if (canonicalDump != null)
                    lock (canonicalDump)
                    {
                        for (int i = 0; i < canonicalCount; i++)
                        {
                            int humanId = upperCanonicalRig.bones[i].humanBone;
                            if (humanId < 0) continue;
                            Q q = canonicalWorlds[i];
                            canonicalDump.WriteLine($"{frame},{humanId},{q.X:R},{q.Y:R},{q.Z:R},{q.W:R}");
                        }
                    }
            }
            for (int b = 0; b < count; b++)
            {
                int parent = view.Bones[b].parent;
                Q restLocal = Math3.Q(view.Bones[b].rotation);
                Q local = restLocal;
                int source = sourceBones[b];
                if (source >= 0)
                {
                    int ancestor = nearestMappedAncestors[b];
                    Q sourceWorld = PmxRigBuilder.Rotation(sample.WorldRotations[source], options.CoordinateConversion);
                    Q sourceAncestor = ancestor < 0 ? Q.Identity :
                        PmxRigBuilder.Rotation(sample.WorldRotations[sourceBones[ancestor]], options.CoordinateConversion);
                    Q targetAncestorRest = ancestor < 0 ? Q.Identity : view.RestWorldRotations[ancestor];
                    Q targetParentRest = parent < 0 ? Q.Identity : view.RestWorldRotations[parent];
                    // VMDCW writes the root chain as direct FK (no ancestor->parent conjugation);
                    // including it turns the centre rotation into a 2x error (38.45 deg).
                    // Restricted to bones hanging directly off the armature (the Hips).
                    if (rootDirect && ancestor < 0 && view.Bones[b].humanBone == 0)
                        targetParentRest = Q.Identity;
                    // Absolute-FK variants (VMDCC_FKMODE=abs) were tried and rejected: they break
                    // the legs (0.02 -> 24-30 deg) and do not fix the arms, because the PMX arm
                    // rest axis differs from the FBX one by a roll that only the canonical
                    // (profile-aligned) rig carries. See Motion/V05 3.0.5.
                    // =local tests the convention described by the Unity-side report:
                    // world_target = targetRest * (inv(sourceParent) * sourceWorld).
                    if (fkAbsolute && view.Bones[b].humanBone > 0)
                        local = Q.Normalize(Q.Inverse(targetParentRest) *
                            Q.Normalize(view.RestWorldRotations[b] *
                                Q.Normalize(Q.Inverse(sourceAncestor) * sourceWorld)));
                    else
                        local = RetargetMath.LocalRotation(sourceWorld, sourceAncestor,
                            targetAncestorRest, targetParentRest, restLocal);
                    // Some reference pipelines compose the root as rest*delta rather than
                    // delta*rest; expose both so the convention can be matched per avatar.
                    if (rootOrderPre && ancestor < 0 && view.Bones[b].humanBone == 0)
                        local = Q.Normalize(restLocal * sourceWorld);
                    rotations[b][frame] = local;
                }
                worlds[b] = parent < 0 ? local : Q.Normalize(worlds[parent] * local);
            }
            // Route B, stage 1: correct torso and legs before the upper-body override, so the
            // arms are built on an already-corrected parent frame.
            if (residual != null && frameMuscles != null && viewResidualBone.Length > 0)
            {
                // Correct each bone's ORIGINAL world, then express it against the already
                // corrected parent: W' = W * exp(r), local' = inv(W'_parent) * W'.
                var originalWorlds = (Q[])worlds.Clone();
                for (int b = 0; b < count; b++)
                {
                    int rigBone = viewResidualBone[b];
                    if (rigBone < 0) continue;
                    int human = view.Bones[b].humanBone;
                    if (human is >= 11 and <= 18 or >= 24 and <= 53) continue;
                    int parent = view.Bones[b].parent;
                    Q parentCorrected = parent < 0 ? Q.Identity : worlds[parent];
                    Q world = originalWorlds[b];
                    if (residual.Rotation(rigBone, frameMuscles) is { } correction)
                    {
                        world = Q.Normalize(world * correction);
                        if (rotations[b] == null) rotations[b] = new Q[samples.Length];
                    }
                    if (rotations[b] != null)
                        rotations[b]![frame] = parent < 0
                            ? world
                            : Q.Normalize(Q.Inverse(parentCorrected) * world);
                    worlds[b] = world;
                }
            }
            if (upperCanonical != null && upperTarget != null && upperCanonicalRig != null)
            {
                SkeletonPose decoded = upperTarget.Decode(frameMuscles!);
                // Decode produces a neutral-body skeleton. Attach its arm subtrees
                // to the current torso rather than pinning their worlds to that
                // neutral body and cancelling the animated parent in local curves.
                Q[] armAlignments = upperBodyAnchors.Select(anchor => Q.Normalize(
                    (anchor.View < 0 ? Q.Identity : worlds[anchor.View]) *
                    Q.Inverse(anchor.Rig < 0 ? Q.Identity : decoded.WorldRotations[anchor.Rig]))).ToArray();
                Q[]? calibratedWorlds = null;
                if (calibrated != null && upperTargetRig != null)
                {
                    // The profile-aligned rest differs from the model rest by up to 80 deg
                    // (A-pose vs T-pose); the calibration baseline follows the model rest.
                    Bone[] restBones = useBindRest && upperTargetRig.bindBones is { Length: > 0 }
                        ? upperTargetRig.bindBones
                        : upperTargetRig.bones;
                    var rest = new Q[restBones.Length];
                    var locals = new Q[restBones.Length];
                    for (int i = 0; i < rest.Length; i++) rest[i] = locals[i] = Math3.Q(restBones[i].rotation);
                    calibrated.Apply(frameMuscles!, rest, locals);
                    // keep the solver's parent frame and swap only the local rotation
                    calibratedWorlds = new Q[locals.Length];
                    for (int i = 0; i < locals.Length; i++)
                    {
                        Q parentWorld = Q.Normalize(decoded.WorldRotations[i] *
                                                    Q.Inverse(decoded.LocalRotations[i]));
                        calibratedWorlds[i] = Q.Normalize(parentWorld * locals[i]);
                    }
                }
                for (int b = 0; b < count; b++)
                {
                    int rigBone = viewUpperRigBone[b];
                    if (rigBone < 0 || rotations[b] == null) continue;
                    int human = view.Bones[b].humanBone;
                    if (!(human is >= 11 and <= 18 or >= 24 and <= 53)) continue;
                    Q world = calibratedWorlds != null && rigBone < calibratedWorlds.Length
                        ? calibratedWorlds[rigBone]
                        : decoded.WorldRotations[rigBone];
                    bool left = human >= 24 ? human <= 38 : (human & 1) != 0;
                    world = Q.Normalize(armAlignments[left ? 0 : 1] * world);
                    if (residual != null && residual.Rotation(rigBone, frameMuscles!) is { } correction)
                        world = Q.Normalize(world * correction);
                    if (sourceTwist && frameCanonicalWorlds != null &&
                        canonicalByHuman.TryGetValue(human, out int canonicalBone) &&
                        canonicalBone < canonicalRestWorld.Length && rigBone < targetRestWorld.Length)
                    {
                        // Keep the decoded swing, take the roll implied by the canonical PMX pose:
                        // the muscle twist channels saturate on fast spins, leaving a constant roll
                        // offset afterwards that no continuous residual can remove.
                        Q sourceDelta = Q.Normalize(Q.Inverse(canonicalRestWorld[canonicalBone]) *
                                                    frameCanonicalWorlds![canonicalBone]);
                        Q decodedDelta = Q.Normalize(Q.Inverse(targetRestWorld[rigBone]) * world);
                        float deltaAngle = TwistAboutY(sourceDelta) - TwistAboutY(decodedDelta);
                        if (float.IsFinite(deltaAngle) && MathF.Abs(deltaAngle) > 1e-4f)
                            world = Q.Normalize(world * new Q(0f, MathF.Sin(deltaAngle * 0.5f), 0f,
                                                              MathF.Cos(deltaAngle * 0.5f)));
                    }
                    int parent = view.Bones[b].parent;
                    Q parentWorld = parent < 0 ? Q.Identity : worlds[parent];
                    Q localRotation = Q.Normalize(Q.Inverse(parentWorld) * world);
                    rotations[b][frame] = localRotation;
                    worlds[b] = world;
                }
            }
            V displacement = PmxRigBuilder.Position(
                sample.WorldPositions[rootMotionSource] - model.Bones[rootMotionSource].Position,
                options.CoordinateConversion) * (options.PositionScale * targetScale);
            int hipsParent = view.Bones[hips].parent;
            hipsPositions[frame] = Math3.V(view.Bones[hips].position) + (hipsParent < 0 ? displacement :
                Math3.Rotate(Q.Inverse(view.RestWorldRotations[hipsParent]), displacement));
        });
        muscleDump?.Flush();
        canonicalDump?.Flush();
        float duration = motion.MaximumFrame / 30f;
        float[] times = Enumerable.Range(0, samples.Length)
            .Select(i => i == samples.Length - 1 ? duration : i / options.SampleRate).ToArray();
        var result = new VmdTargetCurveData { SampleRate = options.SampleRate, SampleCount = samples.Length };
        for (int b = 0; b < count; b++)
        {
            Q[]? values = rotations[b];
            if (values == null) continue;
            string path = view.Paths[b];
            for (int i = 1; i < values.Length; i++)
                if (Q.Dot(values[i - 1], values[i]) < 0) values[i] = -values[i];
            Q rest = Math3.Q(view.Bones[b].rotation);
            bool moving = values.Any(q => Math.Abs(Q.Dot(q, rest)) < 1f - 1e-7f);
            if (moving)
            {
                var unityValues = values.Select(q => new UnityEngine.Quaternion(q.X, q.Y, q.Z, q.W)).ToArray();
                int[] keys = VmdCurveUtility.ReduceQuaternion(times, unityValues, options.RotationErrorDegrees);
                for (int axis = 0; axis < 4; axis++)
                {
                    int component = axis;
                    result.Curves.Add(VmdCurveUtility.BuildCurve(path,
                        "m_LocalRotation." + "xyzw"[axis], 0, times, keys,
                        i => component switch { 0 => values[i].X, 1 => values[i].Y, 2 => values[i].Z, _ => values[i].W }));
                }
                result.BoundBoneCount++;
            }
        }
        V hipsRest = Math3.V(view.Bones[hips].position);
        if (hipsPositions.Any(p => V.DistanceSquared(p, hipsRest) > 1e-14f))
        {
            var unityPositions = hipsPositions.Select(p => new UnityEngine.Vector3(p.X, p.Y, p.Z)).ToArray();
            int[] keys = VmdCurveUtility.ReduceVector(times, unityPositions, options.PositionError);
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                result.Curves.Add(VmdCurveUtility.BuildCurve(view.Paths[hips],
                    "m_LocalPosition." + "xyz"[axis], 0, times, keys,
                    i => component switch { 0 => hipsPositions[i].X, 1 => hipsPositions[i].Y, _ => hipsPositions[i].Z }));
            }
        }
        if (options.BakeMorphs) VmdCurveUtility.AddMorphCurves(motion, AnimationRig, result);
        return result;
    }

    public string CoverageJson(VmdMotion motion, VmdTargetCurveData curves)
    {
        var animatedPaths = curves.Curves.Where(c => c.BindingKind == 0)
            .Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var mapped = new List<string>();
        var restOnly = new List<string>();
        for (int b = 0; b < view.Bones.Length; b++)
        {
            if (b == 0 && view.Paths[b].Length == 0) continue;
            if (animatedPaths.Contains(view.Paths[b])) mapped.Add(view.Paths[b]);
            else restOnly.Add(view.Paths[b]);
        }
        var selected = sourceBones.Where(i => i >= 0).ToHashSet();
        string[] dropped = motion.BoneTracks.Keys.Where(name =>
        {
            int source = model.FindBone(name);
            return source < 0 || !selected.Contains(source);
        }).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new
        {
            mappedWithMotion = mapped,
            restOnly,
            collapsed,
            dropped,
            ambiguous,
            decode = new
            {
                calibrationPath = options.CalibrationPath,
                calibrationEffects = calibrated?.EffectCount ?? 0,
                calibrationBones = calibrated?.MappedBones ?? 0,
                rootDirect,
                upperMuscle = upperCanonical != null
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Signed twist angle (radians) of a local-frame rotation about +Y.</summary>
    private static float TwistAboutY(Q q)
    {
        float y = q.Y, w = q.W;
        float length = MathF.Sqrt(y * y + w * w);
        if (length < 1e-6f) return 0f;
        return 2f * MathF.Atan2(y / length, w / length);
    }

    private int FindRootMotionSource(int sourceHips)
    {
        for (int bone = sourceHips; bone >= 0; bone = model.Bones[bone].ParentIndex)
            if (model.Bones[bone].NameJapanese == "腰" ||
                string.Equals(model.Bones[bone].NameEnglish, "waist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Bones[bone].NameEnglish, "hips", StringComparison.OrdinalIgnoreCase))
                return bone;
        return sourceHips;
    }

    private static string NormalizeName(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
