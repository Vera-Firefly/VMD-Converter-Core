using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace UnityHps;

/// <summary>Coordinate convention for transporting portable muscle responses to a skeleton.</summary>
public enum HumanoidResponseTransport
{
    /// <summary>Each observed bone uses its own joint frame; retained for source-pose encoding.</summary>
    JointFrames,
    /// <summary>Leg responses follow their owning segment; ankle pitch/roll use the Avatar heading.</summary>
    AnatomicalLegs,
    /// <summary>Target leg responses, optional torso links and gravity-stable shoulder frames.</summary>
    AnatomicalTargets
}

/// <summary>
/// Construct a target rig offline from a FBX skeleton and portable anatomical
/// muscle responses measured on an authored synthetic skeleton at build time.
/// No target Avatar calibration, native Unity code, or Unity process is used.
/// </summary>
public static class OfflineRigFactory
{
    public static Rig FromFbx(string path, FbxImportOptions? options = null, string? profilePath = null) =>
        FromSkeleton(FbxReader.Read(path, options), profilePath, HumanoidResponseTransport.AnatomicalTargets);

    public static Rig FromSkeleton(FbxDocument document, string? profilePath = null) =>
        FromSkeleton(document, profilePath, HumanoidResponseTransport.JointFrames);

    public static Rig FromSkeleton(FbxDocument document, string? profilePath, HumanoidResponseTransport transport)
    {
        foreach (var group in document.bones.Where(b => b.humanBone >= 0).GroupBy(b => b.humanBone))
            if (group.Count() != 1) throw new InvalidDataException("Ambiguous Humanoid bone: " + HumanBones.Names[group.Key]);
        var available = document.bones.Where(b => b.humanBone >= 0).Select(b => b.humanBone).ToHashSet();
        int[] mandatory = [0,1,2,3,4,5,6,7,10,13,14,15,16,17,18];
        var missing = mandatory.Where(b => !available.Contains(b)).Select(b => HumanBones.Names[b]).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Missing required Humanoid mappings: " + string.Join(", ", missing) + ". Provide --bone-map or a Humanoid FBX .meta.");
        Rig profile = profilePath == null ? LoadProfile(available.Contains(54)) : HpsJson.ReadRig(profilePath);
        return Transfer(document, profile, transport);
    }

    public static Rig LoadProfile(bool upperChest)
    {
        string resource = "UnityHps.Profiles." + (upperChest ? "standard55" : "standard53") + ".json.gz";
        using var stream = typeof(OfflineRigFactory).Assembly.GetManifestResourceStream(resource) ??
            throw new InvalidOperationException("Built-in profile is missing: " + resource + ". Build the repository with its profiles directory.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gzip, HpsJsonContext.Default.Rig) ?? throw new InvalidDataException("Empty default profile.");
    }

    public static Rig Transfer(FbxDocument document, Rig profile) =>
        Transfer(document, profile, HumanoidResponseTransport.JointFrames);

    public static Rig Transfer(FbxDocument document, Rig profile, HumanoidResponseTransport transport)
    {
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
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
        bool targetFrames=transport==HumanoidResponseTransport.AnatomicalTargets;
        var alignments = targetByHuman.Where(p=>sourceByHuman.ContainsKey(p.Key)).ToDictionary(
            p=>p.Value,p=>JointFrameCore(targetBind,p.Value,targetFrames)*Quaternion.Inverse(JointFrameCore(sourceBind,sourceByHuman[p.Key],targetFrames)));
        Quaternion headingAlignment = transport != HumanoidResponseTransport.JointFrames
            ? Heading(targetBind)*Quaternion.Inverse(Heading(sourceBind)) : Quaternion.Identity;
        Quaternion ConvertLocal(int source, int dest, Quaternion local, Quaternion? responseAlignment = null)
        {
            int sp=profile.bindBones[source].parent, tp=bind[dest].parent;
            Quaternion sourceParent=sp<0?Quaternion.Identity:sourceBind.WorldRotations[sp];
            Quaternion targetParent=tp<0?Quaternion.Identity:targetBind.WorldRotations[tp];
            Quaternion alignment=responseAlignment ?? alignments[dest];
            Quaternion delta=sourceParent*local*Quaternion.Inverse(sourceBind.WorldRotations[source]);
            return Quaternion.Normalize(Quaternion.Inverse(targetParent)*alignment*delta*Quaternion.Inverse(alignment)*targetBind.WorldRotations[dest]);
        }
        foreach (var pair in targetByHuman)
            if (sourceByHuman.TryGetValue(pair.Key,out int src) && pair.Key!=0)
            {
                // Neutral leg flexion is defined in the Avatar heading frame.
                // A lateral bind offset must not tilt the neutral bend axis;
                // variable twist responses still follow their owning segment.
                Quaternion? neutralAlignment=targetFrames && pair.Key is >=1 and <=4 ? headingAlignment : null;
                target.bones[pair.Value].rotation=Math3.A(ConvertLocal(src,pair.Value,Math3.Q(profile.bones[src].rotation),neutralAlignment));
            }
        var groups=new List<MuscleGroup>();
        foreach (var group in profile.groups)
        {
            int owner=profile.muscles[group.muscles[0]].humanBone;
            if (owner>=0&&!targetByHuman.ContainsKey(owner) && !(targetFrames && owner==54 && targetByHuman.ContainsKey(8))) continue;
            int[] slots=Enumerable.Range(0,group.bones.Length).Where(i=>profile.bones[group.bones[i]].humanBone>0 && targetByHuman.ContainsKey(profile.bones[group.bones[i]].humanBone)).ToArray();
            if (slots.Length==0) continue;
            int[] bones=slots.Select(i=>targetByHuman[profile.bones[group.bones[i]].humanBone]).ToArray();
            bool legResponse = transport != HumanoidResponseTransport.JointFrames && owner is >= 1 and <= 6;
            Quaternion? responseAlignment = !legResponse ? null : owner is 5 or 6
                ? headingAlignment : alignments[targetByHuman[owner]];
            // A shared limb response may rotate both a segment and its child.
            // Transport all of that response in the owner's frame, then rebase
            // at the existing neutral pose so a zero muscle stays neutral.
            Quaternion[] centers = legResponse ? slots.Select((slot, k) => ConvertLocal(group.bones[slot], bones[k],
                Math3.Q(group.samples[(group.samples.Length-1)/2].rotations, slot*4), responseAlignment)).ToArray() : [];
            var samples=new LatticeSample[group.samples.Length];
            for (int s=0;s<samples.Length;s++)
            {
                samples[s]=new LatticeSample { rotations=new float[bones.Length*4], positions=new float[bones.Length*3] };
                for (int k=0;k<bones.Length;k++)
                {
                    Quaternion value=ConvertLocal(group.bones[slots[k]],bones[k],Math3.Q(group.samples[s].rotations,slots[k]*4),responseAlignment);
                    if(legResponse)value=Quaternion.Normalize(Math3.Q(target.bones[bones[k]].rotation)*Quaternion.Inverse(centers[k])*value);
                    Math3.Put(samples[s].rotations,k*4,value);
                    Array.Copy(bind[bones[k]].position,0,samples[s].positions,k*3,3);
                }
            }
            groups.Add(new MuscleGroup { muscles=(int[])group.muscles.Clone(),bones=bones,resolution=group.resolution,
                muscleRanges=(float[])group.muscleRanges.Clone(),samples=samples });
        }
        if(targetFrames && !targetByHuman.ContainsKey(54) && targetByHuman.ContainsKey(8) &&
            !groups.Any(g=>g.muscles.Any(m=>target.muscles[m].unityIndex is >=6 and <=8)))
            AddVirtualUpperChest(target,profile,groups,targetBind,targetByHuman[8]);
        target.groups=groups.ToArray();
        if(targetFrames)ShoulderSwingResponse.Configure(target,document.customMuscleLimits);
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
    private static void AddVirtualUpperChest(Rig target,Rig profile,List<MuscleGroup> groups,SkeletonPose targetBind,int chest)
    {
        // Unity delegates these channels to Chest when UpperChest is unmapped.
        // Retain an explicit profile response if supplied; otherwise use the
        // portable UpperChest chart, without adding a fictitious target bone.
        Rig source=profile.bones.Any(b=>b.humanBone==54) ? profile : LoadProfile(true);
        MuscleGroup? group=source.groups.FirstOrDefault(g=>g.muscles.Any(m=>source.muscles[m].unityIndex==6));
        int bone=Array.FindIndex(source.bones,b=>b.humanBone==54);
        if(group==null || bone<0)return;
        int slot=Array.IndexOf(group.bones,bone);
        if(slot<0)return;
        var channels=group.muscles.Select(m=>Array.FindIndex(target.muscles,t=>t.unityIndex==source.muscles[m].unityIndex)).ToArray();
        if(channels.Any(m=>m<0))return;
        var sourceBind=new SkeletonPose(new Rig{bones=source.bindBones});
        Quaternion alignment=JointFrameCore(targetBind,chest,true)*Quaternion.Inverse(JointFrameCore(sourceBind,bone,true));
        Quaternion basis=Quaternion.Normalize(Quaternion.Inverse(targetBind.WorldRotations[chest])*alignment*sourceBind.WorldRotations[bone]);
        Quaternion center=Math3.Q(group.samples[(group.samples.Length-1)/2].rotations,slot*4);
        Quaternion neutral=Math3.Q(target.bones[chest].rotation);
        groups.Add(new MuscleGroup{muscles=channels,bones=[chest],resolution=group.resolution,
            muscleRanges=(float[])group.muscleRanges.Clone(),samples=group.samples.Select(sample=>new LatticeSample{
                positions=(float[])target.bones[chest].position.Clone(),
                rotations=Math3.A(Quaternion.Normalize(neutral*basis*Quaternion.Inverse(center)*Math3.Q(sample.rotations,slot*4)*Quaternion.Inverse(basis)))
            }).ToArray()});
    }
    private static Quaternion Heading(SkeletonPose pose)
    {
        Vector3 forward=Math3.Rotate(BodyFrame(pose),Vector3.UnitZ);
        forward.Y=0;
        if(forward.LengthSquared()<1e-10f)throw new InvalidDataException("Humanoid heading must be defined in the Y-up plane.");
        return Math3.LookRotation(forward,Vector3.UnitY);
    }
    private static Bone Clone(Bone b)=>new(){ name=b.name,parent=b.parent,humanBone=b.humanBone,position=(float[])b.position.Clone(),rotation=(float[])b.rotation.Clone() };
    private static readonly Dictionary<int,int> Children=new(){[0]=7,[7]=8,[8]=54,[54]=9,[9]=10,[1]=3,[2]=4,[3]=5,[4]=6,[5]=19,[6]=20,[11]=13,[12]=14,[13]=15,[14]=16,[15]=17,[16]=18,[17]=30,[18]=45};
    private static Quaternion JointFrame(SkeletonPose pose,int b)=>JointFrameCore(pose,b,false);
    private static Quaternion JointFrameCore(SkeletonPose pose,int b,bool targetFrames)
    {
        int h=pose.Rig.bones[b].humanBone;
        Quaternion body=targetFrames && h is 11 or 12 ? Heading(pose) : BodyFrame(pose);
        Vector3 up=Math3.Rotate(body,Vector3.UnitY),forward=Math3.Rotate(body,Vector3.UnitZ);
        int wanted=Children.GetValueOrDefault(h,-1);
        if (h>=24&&h<=53&&(h-24)%3!=2) wanted=h+1;
        int child=Array.FindIndex(pose.Rig.bones,x=>x.humanBone==wanted&&wanted>=0);
        while(child<0 && targetFrames && h is 7 or 8 or 54 && Children.TryGetValue(wanted,out int next))
        {
            wanted=next;
            child=Array.FindIndex(pose.Rig.bones,x=>x.humanBone==wanted);
        }
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
