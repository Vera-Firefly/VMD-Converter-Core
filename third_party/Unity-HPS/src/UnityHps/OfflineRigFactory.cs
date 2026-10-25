using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace UnityHps;

/// <summary>
/// Construct a target rig offline from a FBX skeleton and portable anatomical
/// muscle responses measured on an authored synthetic skeleton at build time.
/// No target Avatar calibration, native Unity code, or Unity process is used.
/// </summary>
public static class OfflineRigFactory
{
    public static Rig FromFbx(string path, FbxImportOptions? options = null, string? profilePath = null) =>
        FromSkeleton(FbxReader.Read(path, options), profilePath);

    public static Rig FromSkeleton(FbxDocument document, string? profilePath = null)
    {
        foreach (var group in document.bones.Where(b => b.humanBone >= 0).GroupBy(b => b.humanBone))
            if (group.Count() != 1) throw new InvalidDataException("Ambiguous Humanoid bone: " + HumanBones.Names[group.Key]);
        var available = document.bones.Where(b => b.humanBone >= 0).Select(b => b.humanBone).ToHashSet();
        int[] mandatory = [0,1,2,3,4,5,6,7,10,13,14,15,16,17,18];
        var missing = mandatory.Where(b => !available.Contains(b)).Select(b => HumanBones.Names[b]).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Missing required Humanoid mappings: " + string.Join(", ", missing) + ". Provide --bone-map or a Humanoid FBX .meta.");
        Rig profile = profilePath == null ? LoadProfile(available.Contains(54)) : HpsJson.ReadRig(profilePath);
        return Transfer(document, profile);
    }

    public static Rig LoadProfile(bool upperChest)
    {
        string resource = "UnityHps.Profiles." + (upperChest ? "standard55" : "standard53") + ".json.gz";
        using var stream = typeof(OfflineRigFactory).Assembly.GetManifestResourceStream(resource) ??
            throw new InvalidOperationException("Built-in profile is missing: " + resource + ". Build the repository with its profiles directory.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gzip, HpsJsonContext.Default.Rig) ?? throw new InvalidDataException("Empty default profile.");
    }

    public static Rig Transfer(FbxDocument document, Rig profile)
    {
        if (profile.bindBones.Length != profile.bones.Length || profile.bodyPositionWeights.Length != profile.bones.Length)
            throw new InvalidDataException("Portable profile needs bindBones and calibrated mass weights.");
        var needed = new HashSet<int>();
        for (int b = 0; b < document.bones.Length; b++)
            if (document.bones[b].humanBone >= 0)
                for (int p = b; p >= 0 && needed.Add(p); p = document.bones[p].parent) { }
        int[] order = Enumerable.Range(0,document.bones.Length).Where(needed.Contains).ToArray();
        var indices = order.Select((old,index) => (old,index)).ToDictionary(p => p.old,p => p.index);
        var bind = order.Select(old => new Bone { name=document.bones[old].name, parent=document.bones[old].parent<0?-1:indices[document.bones[old].parent],
            humanBone=document.bones[old].humanBone, position=(float[])document.bones[old].position.Clone(), rotation=(float[])document.bones[old].rotation.Clone() }).ToArray();
        var target = new Rig { source=document.source, bindBones=bind, bones=bind.Select(Clone).ToArray(), muscles=profile.muscles,
            precision="offline-default-profile", morphs=document.morphs, bodyOrientationMode="landmarks" };
        var targetByHuman = bind.Select((b,i)=>(b,i)).Where(p=>p.b.humanBone>=0).ToDictionary(p=>p.b.humanBone,p=>p.i);
        var sourceByHuman = profile.bones.Select((b,i)=>(b,i)).Where(p=>p.b.humanBone>=0).ToDictionary(p=>p.b.humanBone,p=>p.i);
        target.hips=targetByHuman[0];
        var sourceBind = new SkeletonPose(new Rig { bones=profile.bindBones });
        var targetBind = new SkeletonPose(new Rig { bones=bind });
        var alignments = targetByHuman.Where(p=>sourceByHuman.ContainsKey(p.Key)).ToDictionary(
            p=>p.Value,p=>JointFrame(targetBind,p.Value)*Quaternion.Inverse(JointFrame(sourceBind,sourceByHuman[p.Key])));
        Quaternion ConvertLocal(int source, int dest, Quaternion local)
        {
            int sp=profile.bindBones[source].parent, tp=bind[dest].parent;
            Quaternion sourceParent=sp<0?Quaternion.Identity:sourceBind.WorldRotations[sp];
            Quaternion targetParent=tp<0?Quaternion.Identity:targetBind.WorldRotations[tp];
            Quaternion alignment=alignments[dest];
            Quaternion delta=sourceParent*local*Quaternion.Inverse(sourceBind.WorldRotations[source]);
            return Quaternion.Normalize(Quaternion.Inverse(targetParent)*alignment*delta*Quaternion.Inverse(alignment)*targetBind.WorldRotations[dest]);
        }
        foreach (var pair in targetByHuman)
            if (sourceByHuman.TryGetValue(pair.Key,out int src) && pair.Key!=0)
                target.bones[pair.Value].rotation=Math3.A(ConvertLocal(src,pair.Value,Math3.Q(profile.bones[src].rotation)));
        var groups=new List<MuscleGroup>();
        foreach (var group in profile.groups)
        {
            int owner=profile.muscles[group.muscles[0]].humanBone;
            if (owner>=0&&!targetByHuman.ContainsKey(owner)) continue;
            int[] slots=Enumerable.Range(0,group.bones.Length).Where(i=>profile.bones[group.bones[i]].humanBone>0 && targetByHuman.ContainsKey(profile.bones[group.bones[i]].humanBone)).ToArray();
            if (slots.Length==0) continue;
            int[] bones=slots.Select(i=>targetByHuman[profile.bones[group.bones[i]].humanBone]).ToArray();
            var samples=new LatticeSample[group.samples.Length];
            for (int s=0;s<samples.Length;s++)
            {
                samples[s]=new LatticeSample { rotations=new float[bones.Length*4], positions=new float[bones.Length*3] };
                for (int k=0;k<bones.Length;k++)
                {
                    Math3.Put(samples[s].rotations,k*4,ConvertLocal(group.bones[slots[k]],bones[k],Math3.Q(group.samples[s].rotations,slots[k]*4)));
                    Array.Copy(bind[bones[k]].position,0,samples[s].positions,k*3,3);
                }
            }
            groups.Add(new MuscleGroup { muscles=(int[])group.muscles.Clone(),bones=bones,resolution=group.resolution,
                muscleRanges=(float[])group.muscleRanges.Clone(),samples=samples });
        }
        target.groups=groups.ToArray();
        target.reverseCompositionBones=profile.reverseCompositionBones.Select(i=>profile.bones[i].humanBone)
            .Where(targetByHuman.ContainsKey).Select(h=>targetByHuman[h]).ToArray();
        target.bodyPositionWeights=new float[bind.Length];
        for (int i=0;i<profile.bones.Length;i++)
        {
            int source=i;
            while (source>=0&&!targetByHuman.ContainsKey(profile.bones[source].humanBone)) source=profile.bones[source].parent;
            if (source>=0) target.bodyPositionWeights[targetByHuman[profile.bones[source].humanBone]]+=profile.bodyPositionWeights[i];
        }
        Vector3 Center(SkeletonPose pose)
        {
            Vector3 result=Vector3.Zero;
            for (int i=0;i<bind.Length;i++) result+=pose.WorldPositions[i]*target.bodyPositionWeights[i];
            return result;
        }
        // Unity's humanScale is the neutral standing body height. Derive it from
        // the same portable mass model, not Hips height or overall mesh bounds.
        target.humanScale=Center(targetBind).Y;
        if (target.humanScale<0.01f||!float.IsFinite(target.humanScale)) throw new InvalidDataException("Invalid FBX human height; check units/orientation.");
        var neutral=new SkeletonPose(target);
        target.bodyPosition=Math3.A(Center(neutral)/target.humanScale);
        // Body rotation is expressed in Animator space, not the alignment
        // between two differently proportioned T-pose landmark frames.
        target.bodyRotation=(float[])profile.bodyRotation.Clone();
        HumanoidSolver.Validate(target);
        return target;
    }
    private static Bone Clone(Bone b)=>new(){ name=b.name,parent=b.parent,humanBone=b.humanBone,position=(float[])b.position.Clone(),rotation=(float[])b.rotation.Clone() };
    private static readonly Dictionary<int,int> Children=new(){[0]=7,[7]=8,[8]=54,[54]=9,[9]=10,[1]=3,[2]=4,[3]=5,[4]=6,[5]=19,[6]=20,[11]=13,[12]=14,[13]=15,[14]=16,[15]=17,[16]=18,[17]=30,[18]=45};
    private static Quaternion JointFrame(SkeletonPose pose,int b)
    {
        int h=pose.Rig.bones[b].humanBone;
        Quaternion body=BodyFrame(pose); Vector3 up=Math3.Rotate(body,Vector3.UnitY),forward=Math3.Rotate(body,Vector3.UnitZ);
        int wanted=Children.GetValueOrDefault(h,-1);
        if (h>=24&&h<=53&&(h-24)%3!=2) wanted=h+1;
        int child=Array.FindIndex(pose.Rig.bones,x=>x.humanBone==wanted&&wanted>=0);
        Vector3 direction=child>=0?pose.WorldPositions[child]-pose.WorldPositions[b]:Vector3.Zero;
        if (direction.LengthSquared()<1e-10f)
        {
            int parent=pose.Rig.bones[b].parent;
            direction=parent>=0?pose.WorldPositions[b]-pose.WorldPositions[parent]:up;
        }
        direction=Vector3.Normalize(direction);
        Vector3 hint=Math.Abs(Vector3.Dot(direction,forward))>0.9f?up:forward;
        return Math3.LookRotation(direction,hint);
    }
    public static Quaternion BodyFrame(SkeletonPose pose)
    {
        Vector3 P(int h)=>pose.WorldPositions[Array.FindIndex(pose.Rig.bones,b=>b.humanBone==h)];
        Vector3 up=(P(13)+P(14)-P(1)-P(2))*0.5f;
        Vector3 right=P(14)-P(13)+P(2)-P(1);
        return Math3.LookRotation(Vector3.Cross(right,up),up);
    }
}
