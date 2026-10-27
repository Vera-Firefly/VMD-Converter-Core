using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityHps;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace VeraFirefly.VmdConverter.Core.Hps;

public sealed class VmdHpsBackend : IVmdHumanoidBackend, IVmdDescriptorBackend
{
    public string Name => "Unity-HPS";
    public IVmdHumanoidSession Create(PmxModel model,VmdHumanoidBackendOptions options,VmdTargetRigData? legacyRig) =>
        options.AnimationMode == VmdAnimationMode.Transform ? new VmdTransformSession(model,options) : new Session(model,options,legacyRig);
    public void ExportDescriptor(string fbxPath,string? metaPath,string? boneMapPath,string? profilePath,string outputPath) =>
        AvatarDescriptorIO.Write(outputPath,AvatarDescriptorIO.FromFbx(fbxPath,new FbxImportOptions{MetaPath=metaPath,BoneMapPath=boneMapPath},profilePath));

    private sealed class Session : IVmdHumanoidSession
    {
        readonly PmxModel model;
        readonly VmdHumanoidBackendOptions options;
        readonly Rig rig,sourceRig;
        readonly SkeletonPose bind,sourceBind;
        readonly HumanoidSolver solver,sourceSolver,decoder;
        readonly HumanoidUpperProjector sourceUpper,targetUpper;
        readonly Dictionary<int,int> source,target,sourceTarget;
        readonly (int target,int source)[] musclePairs;
        readonly int[] targetMappedAncestors;
        readonly bool[] upperNodes;
        readonly bool canonical,nativeCalibration;
        readonly VmdTargetSolveRegion region;
        readonly float targetScale;
        readonly int rootMotionSource;
        readonly Dictionary<int,VmdTargetBoneBinding> legacyBindings;
        public VmdTargetRigData AnimationRig {get;}
        public string Precision => rig.precision;
        public bool UsesSharedFramePool => true;
        public long EstimatedBytesPerSample => (rig.bones.Length+sourceRig.bones.Length)*256L+rig.muscles.Length*32L+4096;
        public byte[] RigIdentity {get;}

        public Session(PmxModel model,VmdHumanoidBackendOptions options,VmdTargetRigData? legacy)
        {
            this.model=model;this.options=options;region=VmdOutputModes.Region(options.OutputKind);
            source=PmxRigBuilder.Map(model);
            sourceRig=PmxRigBuilder.Build(model,options.PositionScale,options.CoordinateConversion,options.ProfilePath);
            if(sourceRig.bindBones.Length==0)sourceRig.bindBones=sourceRig.bones;
            sourceBind=new SkeletonPose(new Rig{bones=sourceRig.bindBones});
            canonical=options.FbxPath==null&&options.HpsRigPath==null&&options.DescriptorPath==null&&legacy==null;
            if(canonical && region!=VmdTargetSolveRegion.Canonical)
                throw new InvalidDataException("Target output requires --fbx, --hps-rig, --descriptor or an exported Unity target rig.");
            if(options.DescriptorPath!=null)
            {
                AvatarDescriptor descriptor=AvatarDescriptorIO.Read(options.DescriptorPath);
                if(options.FbxPath!=null && descriptor.sourceSha256.Length>0 &&
                   !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.FbxPath))),descriptor.sourceSha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("--descriptor and --fbx describe different FBX files.");
                rig=AvatarDescriptorIO.BuildHumanoidRig(descriptor,options.ProfilePath);
            }
            else if(options.HpsRigPath!=null)rig=HpsJson.ReadRig(options.HpsRigPath);
            else if(options.FbxPath!=null)rig=OfflineRigFactory.FromFbx(options.FbxPath,new FbxImportOptions{MetaPath=options.MetaPath,BoneMapPath=options.BoneMapPath},options.ProfilePath);
            else if(legacy!=null)rig=FromLegacy(legacy);
            else rig=sourceRig;
            // An explicit rig retargets the complete source Humanoid pose.
            // Target limb reconstruction remains exclusive to target-* modes.
            nativeCalibration=HasNativeCompensation(rig);
            if(!canonical && (rig.precision.Contains("profile",StringComparison.Ordinal) ||
                rig.precision.EndsWith("-unproven",StringComparison.Ordinal)))
                Console.Error.WriteLine("Warning: Target rig uses portable Humanoid responses; Avatar accuracy is unverified. " +
                    "Conversion continues. Use an Avatar-calibrated --hps-rig for measured target responses.");
            if(nativeCalibration && rig.bodyPositionWeights.Length==0)
            {
                var fitted=HumanoidBodyMassCalibration.Fit(rig,OfflineRigFactory.LoadProfile(rig.bones.Any(b=>b.humanBone==54)));
                rig.bodyPositionWeights=fitted.weights;rig.bodyOrientationMode="landmarks";
            }
            if(rig.bindBones.Length==0)rig.bindBones=rig.bones;
            bind=new SkeletonPose(new Rig{bones=rig.bindBones});
            target=rig.bones.Select((b,i)=>(b,i)).Where(x=>x.b.humanBone>=0).ToDictionary(x=>x.b.humanBone,x=>x.i);
            sourceTarget=sourceRig.bones.Select((b,i)=>(b,i)).Where(x=>x.b.humanBone>=0).ToDictionary(x=>x.b.humanBone,x=>x.i);
            var policy=options.RangePolicy switch {"strict"=>HumanoidRangePolicy.Strict,"tangent"=>HumanoidRangePolicy.Tangent,_=>throw new ArgumentException("Unknown range policy")};
            var selected=rig.muscles.Where(m=>region==VmdTargetSolveRegion.All ||
                region==VmdTargetSolveRegion.Arm&&IsUpper(m.humanBone) ||
                region==VmdTargetSolveRegion.Leg&&!IsUpper(m.humanBone)).Select(m=>m.humanBone).ToHashSet();
            bool geometry=options.LegFit switch {"auto"=>true,"geometry"=>true,"rotation"=>false,_=>throw new ArgumentException("Unknown leg fitting policy")};
            solver=new HumanoidSolver(rig,selected,policy){FitDecodedArmContext=true,FitLegGeometry=geometry,AnchorLimbTwistToInitial=true,ReconstructDecodedRoot=true};
            decoder=new HumanoidSolver(rig,rangePolicy:policy);
            sourceSolver=new HumanoidSolver(sourceRig,sourceRig.muscles.Where(m=>m.humanBone is <11 or >18).Select(m=>m.humanBone).ToHashSet(),policy)
                {ReconstructDecodedRoot=true};
            sourceUpper=new HumanoidUpperProjector(sourceRig,RawSourceBind());
            targetUpper=new HumanoidUpperProjector(rig);
            var byName=sourceRig.muscles.Select((m,i)=>(m,i)).ToDictionary(x=>x.m.name,x=>x.i);
            musclePairs=rig.muscles.Select((m,i)=>(m,i)).Select(x=>(x.i,byName.TryGetValue(x.m.name,out int slot)?slot:throw new InvalidDataException("Incompatible Humanoid muscle names"))).ToArray();
            targetMappedAncestors=new int[rig.bones.Length];
            for(int b=0;b<rig.bones.Length;b++)
            {
                int ancestor=rig.bones[b].parent;
                while(ancestor>=0&&(rig.bones[ancestor].humanBone<0||!source.ContainsKey(rig.bones[ancestor].humanBone)))ancestor=rig.bones[ancestor].parent;
                targetMappedAncestors[b]=ancestor;
            }
            upperNodes=ClassifyUpperNodes(rig);
            rootMotionSource=FindRootMotionSourceBone(model,source[0]);
            float sourceHeight=Math.Abs(model.Bones[rootMotionSource].Position.y*options.PositionScale);
            targetScale=!canonical&&Math.Abs(sourceHeight)>1e-6f?Math.Abs(bind.WorldPositions[rig.hips].Y)/sourceHeight:1;
            legacyBindings=(legacy?.bones??Array.Empty<VmdTargetBoneBinding>()).ToDictionary(b=>b.humanBone);
            AnimationRig=new VmdTargetRigData{sampleRate=options.SampleRate,positionScale=options.PositionScale,coordinateConversion=options.CoordinateConversion,
                outputKind=options.OutputKind,bakeMorphs=options.BakeMorphs,
                humanoid=new VmdHumanoidCalibrationData{muscles=rig.muscles.Select(m=>new VmdMuscleCalibration{property=MuscleProperty(m.name)}).ToArray()},
                morphs=legacy?.morphs??rig.morphs.Select(m=>new VmdTargetMorphBinding{name=m.name,path=m.path}).ToArray()};
            RigIdentity=SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rig,HpsJsonContext.Default.Rig)+"|source-retarget-target-leg-seed-v3|"+VmdOutputModes.ToArgument(options.OutputKind)+"|"+options.RangePolicy+"|"+options.LegFit));
        }

        Rig RawSourceBind()
        {
            var positions=new V[sourceRig.bones.Length];
            for(int i=0;i<positions.Length;i++)
            {
                Bone bone=sourceRig.bones[i];
                positions[i]=source.TryGetValue(bone.humanBone,out int index)?PmxRigBuilder.Position(model.Bones[index].Position,options.CoordinateConversion)*options.PositionScale:
                    (bone.parent<0?V.Zero:positions[bone.parent])+Math3.V(bone.position);
            }
            return new Rig{bones=sourceRig.bones.Select((b,i)=>new Bone{name=b.name,parent=b.parent,humanBone=b.humanBone,
                position=Math3.A(positions[i]-(b.parent<0?V.Zero:positions[b.parent]))}).ToArray()};
        }
        static bool HasNativeCompensation(Rig rig)
        {
            V rest=Math3.V(rig.bones[rig.hips].position);
            foreach(var group in rig.groups)
            {
                int slot=Array.IndexOf(group.bones,rig.hips);
                if(slot>=0 && group.samples.Any(s=>V.Distance(Math3.V(s.positions,slot*3),rest)>1e-5f))return true;
            }
            return false;
        }
        static bool[] ClassifyUpperNodes(Rig rig)
        {
            var result=new bool[rig.bones.Length];
            for(int i=rig.bones.Length-1;i>=0;i--)
            {
                if(rig.bones[i].humanBone>=0){result[i]=IsUpper(rig.bones[i].humanBone);continue;}
                var children=Enumerable.Range(i+1,rig.bones.Length-i-1).Where(c=>rig.bones[c].parent==i).ToArray();
                if(children.Length>0)result[i]=children.All(c=>result[c]);
                else {int p=rig.bones[i].parent;while(p>=0&&rig.bones[p].humanBone<0)p=rig.bones[p].parent;result[i]=p>=0&&IsUpper(rig.bones[p].humanBone);}
            }
            return result;
        }
        static int FindRootMotionSourceBone(PmxModel model,int hips)
        {
            for(int b=hips;b>=0&&b<model.Bones.Count;b=model.Bones[b].ParentIndex)
                if(model.Bones[b].NameJapanese=="腰"||string.Equals(model.Bones[b].NameEnglish,"waist",StringComparison.OrdinalIgnoreCase))return b;
            return hips;
        }
        public void SaveRig(string path)=>HpsJson.Write(path,rig);
        private static Rig FromLegacy(VmdTargetRigData legacy)
        {
            // Use only the imported skeleton here. Rich HPS calibration can be
            // supplied explicitly with --hps-rig without launching Unity.
            var old=legacy.bones;var bones=new List<Bone>{new(){name="Avatar"}};var slots=new Dictionary<int,int>();
            void Add(int i)
            {
                if(slots.ContainsKey(i))return;
                int parent=old[i].parentBindingIndex;if(parent>=0)Add(parent);
                var world=old[i].restWorldPosition;var parentPosition=parent<0?UnityEngine.Vector3.zero:old[parent].restWorldPosition;
                Q parentRotation=parent<0?Q.Identity:new Q(old[parent].restWorldRotation.x,old[parent].restWorldRotation.y,old[parent].restWorldRotation.z,old[parent].restWorldRotation.w);
                Q rotation=new(old[i].restWorldRotation.x,old[i].restWorldRotation.y,old[i].restWorldRotation.z,old[i].restWorldRotation.w);
                slots[i]=bones.Count;bones.Add(new Bone {name=old[i].path.Split('/').Last(),parent=parent<0?0:slots[parent],humanBone=old[i].humanBone,
                    position=Math3.A(Math3.Rotate(Q.Inverse(parentRotation),new V(world.x-parentPosition.x,world.y-parentPosition.y,world.z-parentPosition.z))),rotation=Math3.A(Q.Inverse(parentRotation)*rotation)});
            }
            for(int i=0;i<old.Length;i++)Add(i);
            Rig rig=OfflineRigFactory.FromSkeleton(new FbxDocument{source="legacy VMDCC bindings",bones=bones.ToArray(),morphs=legacy.morphs.Select(m=>new MorphBinding{name=m.name,path=m.path}).ToArray()},null,HumanoidResponseTransport.AnatomicalTargets);
            return rig;
        }

        public VmdTargetCurveData Solve(VmdMotion motion,PmxPose[] samples,int threads,TaskScheduler? scheduler=null)
        {
            float duration=Math.Min(motion.MaximumFrame/30f,options.DurationSeconds??float.PositiveInfinity);
            bool suppressLeft=!canonical;
            bool suppressRight=!canonical;
            var frames=new PoseFrame[samples.Length];var rawSource=new PoseFrame[samples.Length];var directFrames=new PoseFrame[samples.Length];
            var seeds=new float[samples.Length][];
            Parallel.For(0,frames.Length,new ParallelOptions{MaxDegreeOfParallelism=threads,TaskScheduler=scheduler??TaskScheduler.Default},frame=>
            {
                PmxPose sample=samples[frame];
                PoseFrame Build(Rig activeRig,SkeletonPose activeBind,Dictionary<int,int> activeTarget,bool sourceCanonical,float scale,bool legIk,bool keepUpperRest)
                {
                    var pose=new SkeletonPose(activeBind.Rig);
                    var worlds=new Q[activeRig.bones.Length];
                    for(int b=0;b<activeRig.bones.Length;b++)
                    {
                        int parent=activeRig.bones[b].parent,h=activeRig.bones[b].humanBone;
                        if(h>=0&&(!keepUpperRest||!IsUpper(h))&&source.TryGetValue(h,out int index))
                        {
                            Q sourceWorld=PmxRigBuilder.Rotation(sample.WorldRotations[index],options.CoordinateConversion);
                            if(!sourceCanonical&&(h==1&&suppressLeft||h==2&&suppressRight)&&parent>=0)
                            {
                                int nearest=targetMappedAncestors[b];
                                int ph=nearest<0?-1:activeRig.bones[nearest].humanBone;
                                Q parentSource=source.TryGetValue(ph,out int pi)?PmxRigBuilder.Rotation(sample.WorldRotations[pi],options.CoordinateConversion):Q.Identity;
                                sourceWorld=parentSource*PmxRigBuilder.Rotation(sample.LocalRotations[index],options.CoordinateConversion);
                            }
                            if(sourceCanonical)worlds[b]=sourceWorld;
                            else
                            {
                                // PMX delta is relative to the nearest mapped ancestor.
                                // A rest-only target helper chain rotates that ancestor's
                                // coordinate frame before the delta reaches this bone.
                                int ancestor=targetMappedAncestors[b];
                                int ancestorHuman=ancestor>=0?activeRig.bones[ancestor].humanBone:-1;
                                Q ancestorSource=ancestorHuman>=0&&source.TryGetValue(ancestorHuman,out int ancestorIndex)
                                    ?PmxRigBuilder.Rotation(sample.WorldRotations[ancestorIndex],options.CoordinateConversion):Q.Identity;
                                Q parentRest=parent<0?Q.Identity:activeBind.WorldRotations[parent];
                                Q parentWorld=parent<0?Q.Identity:worlds[parent];
                                Q desiredWorld=Q.Normalize(sourceWorld*activeBind.WorldRotations[b]);
                                Q desiredParentWorld=Q.Normalize(ancestorSource*parentRest);
                                Q local=Q.Normalize(Q.Inverse(desiredParentWorld)*desiredWorld);
                                worlds[b]=Q.Normalize(parentWorld*local);
                            }
                            pose.LocalRotations[b]=parent<0?worlds[b]:Q.Normalize(Q.Inverse(worlds[parent])*worlds[b]);
                            if(sourceCanonical&&IsUpper(h)&&parent>=0&&
                                source.TryGetValue(activeRig.bones[parent].humanBone,out int sourceParent))
                                pose.LocalPositions[b]=Math3.Rotate(Q.Inverse(worlds[parent]),
                                    PmxRigBuilder.Position(sample.WorldPositions[index]-sample.WorldPositions[sourceParent],options.CoordinateConversion)*options.PositionScale);
                        }
                        else worlds[b]=parent<0?pose.LocalRotations[b]:Q.Normalize(worlds[parent]*pose.LocalRotations[b]);
                    }
                    int displacementSource=sourceCanonical?source[0]:rootMotionSource;
                    V displacement=PmxRigBuilder.Position(sample.WorldPositions[displacementSource]-model.Bones[displacementSource].Position,options.CoordinateConversion)*(options.PositionScale*scale);
                    int hp=activeRig.bones[activeRig.hips].parent;
                    pose.LocalPositions[activeRig.hips]+=hp<0?displacement:Math3.Rotate(Q.Inverse(activeBind.WorldRotations[hp]),displacement);
                    pose.UpdateWorld();
                    if(legIk&&!sourceCanonical)
                        foreach(bool left in new[]{true,false})
                        {
                            int thigh=left?1:2,knee=left?3:4,foot=left?5:6;
                            if(!activeTarget.ContainsKey(thigh)||!activeTarget.ContainsKey(knee)||!activeTarget.ContainsKey(foot)||!source.ContainsKey(foot))continue;
                            int f=activeTarget[foot],si=source[foot];
                            V p=activeBind.WorldPositions[f]+PmxRigBuilder.Position(sample.WorldPositions[si]-model.Bones[si].Position,options.CoordinateConversion)*(options.PositionScale*scale);
                            V pole=pose.WorldPositions[activeTarget[knee]]+Math3.Rotate(pose.WorldRotations[activeTarget[thigh]],new V(0,0,0.1f));
                            TwoBoneIk.Solve(pose,new IkGoal{root=activeTarget[thigh],middle=activeTarget[knee],tip=f,target=Math3.A(p),pole=Math3.A(pole),rotation=Math3.A(pose.WorldRotations[f])});
                        }
                    return pose.ToFrame(Math.Min(frame/options.SampleRate,duration));
                }
                var raw=Build(sourceRig,sourceBind,sourceTarget,true,1,false,false);rawSource[frame]=raw;
                var sourceResult=sourceSolver.Solve(raw,options.Passes);
                float[] sourceMuscles=sourceUpper.Project(raw,sourceResult.humanPose.muscles);
                var sourceRoot=sourceSolver.ReconstructRoot(raw,sourceMuscles);
                var seed=new float[rig.muscles.Length];foreach(var pair in musclePairs)seed[pair.target]=sourceMuscles[pair.source];
                var root=new HumanPose{muscles=seed,
                    bodyPosition=Math3.A(Math3.V(rig.bodyPosition)+Math3.V(sourceRoot.bodyPosition)-Math3.V(sourceRig.bodyPosition)),
                    bodyRotation=Math3.A(Q.Normalize(Math3.Q(sourceRoot.bodyRotation)*Q.Inverse(Math3.Q(sourceRig.bodyRotation))*Math3.Q(rig.bodyRotation)))};
                var pose=decoder.Decode(seed,root);
                PoseFrame direct=Build(rig,bind,target,canonical,targetScale,options.TargetLegIk??false,false);directFrames[frame]=direct;
                if(region!=VmdTargetSolveRegion.Canonical)
                {
                    var targetPose=new SkeletonPose(rig,direct);
                    for(int b=0;b<rig.bones.Length;b++)if(region==VmdTargetSolveRegion.All ||
                        region==VmdTargetSolveRegion.Arm&&upperNodes[b] || region==VmdTargetSolveRegion.Leg&&!upperNodes[b])
                    {pose.LocalPositions[b]=targetPose.LocalPositions[b];pose.LocalRotations[b]=targetPose.LocalRotations[b];}
                    pose.UpdateWorld();
                }
                var goal=pose.ToFrame(raw.time);
                if(region is VmdTargetSolveRegion.All or VmdTargetSolveRegion.Arm)
                    // Projection is an optimizer estimate, not a final muscle constraint.
                    // Keep the original goal and start its fit inside the solver domain.
                    seed=solver.CreateFitSeed(targetUpper.Project(goal,seed));
                // PMX muscle coordinates are valid for source retargeting, but
                // can use a remote periodic branch (including tangent limits).
                // They are not target-leg optimizer coordinates. Start only
                // the selected leg channels in the target's neutral chart;
                // preserve all source channels outside that target solve.
                if(region is VmdTargetSolveRegion.All or VmdTargetSolveRegion.Leg)
                    for(int m=0;m<seed.Length;m++)if(rig.muscles[m].humanBone is >=1 and <=6)seed[m]=0;
                frames[frame]=goal;seeds[frame]=seed;
            });
            ResultBatch fitted=solver.SolveBatch(new PoseBatch{frames=frames},threads,options.Passes,scheduler,seeds);
            if(region is VmdTargetSolveRegion.Canonical or VmdTargetSolveRegion.Leg)
                for(int i=0;i<frames.Length;i++)for(int m=0;m<rig.muscles.Length;m++)
                    if(IsUpper(rig.muscles[m].humanBone)&&fitted.frames[i].humanPose.muscles[m]!=seeds[i][m])
                        throw new InvalidDataException("Unselected source upper muscles changed during body fitting.");
            if(region==VmdTargetSolveRegion.Arm)
                for(int i=0;i<frames.Length;i++)for(int m=0;m<rig.muscles.Length;m++)
                    if(!IsUpper(rig.muscles[m].humanBone)&&fitted.frames[i].humanPose.muscles[m]!=seeds[i][m])
                        throw new InvalidDataException("Unselected source body muscles changed during arm fitting.");
            var decoded=new PoseFrame[frames.Length];var fk=new PoseFrame[frames.Length];
            Parallel.For(0,frames.Length,new ParallelOptions{MaxDegreeOfParallelism=threads,TaskScheduler=scheduler??TaskScheduler.Default},i=>
            {
                var evaluated=decoder.Decode(fitted.frames[i].humanPose.muscles,fitted.frames[i].humanPose);
                decoded[i]=evaluated.ToFrame(frames[i].time);
                var output=new SkeletonPose(rig,frames[i]);
                if(region is VmdTargetSolveRegion.All or VmdTargetSolveRegion.Arm)
                    for(int b=0;b<rig.bones.Length;b++)if(upperNodes[b])
                    {
                        int p=rig.bones[b].parent;
                        output.LocalPositions[b]=p<0?evaluated.WorldPositions[b]:Math3.Rotate(Q.Inverse(output.WorldRotations[p]),evaluated.WorldPositions[b]-output.WorldPositions[p]);
                        output.LocalRotations[b]=p<0?evaluated.WorldRotations[b]:Q.Normalize(Q.Inverse(output.WorldRotations[p])*evaluated.WorldRotations[b]);
                        output.UpdateWorld();
                    }
                fk[i]=output.ToFrame(frames[i].time);
            });
            if(VmdOutputModes.IsFk(options.OutputKind)&&options.FkContinuity)fk=FkBranchContinuation.Process(rig,sourceRig,rawSource,fk);
            var result=WriteCurves(frames,fitted,fk);
            if(options.BakeMorphs)VmdCurveUtility.AddMorphCurves(motion,AnimationRig,result);
            if(options.DiagnosticsDirectory!=null)ConversionDiagnostics.Write(options.DiagnosticsDirectory,motion,rig,sourceRig,rawSource,directFrames,frames,fk,decoded,region.ToString(),VmdOutputModes.ToArgument(options.OutputKind));
            return result;
        }

        VmdTargetCurveData WriteCurves(PoseFrame[] goals,ResultBatch fitted,PoseFrame[] fk)
        {
            float[] times=goals.Select(f=>f.time).ToArray();int[] all=Enumerable.Range(0,times.Length).ToArray();
            var result=new VmdTargetCurveData{SampleRate=options.SampleRate,SampleCount=times.Length,BoundBoneCount=target.Count};
            void Curve(string path,string name,byte kind,float[] values)
            {
                int[] keys=options.CurveError<=0?all:VmdCurveUtility.Reduce(times,times.Length,(a,b,i)=>{float t=(times[i]-times[a])/(times[b]-times[a]);double d=values[i]-(values[a]+t*(values[b]-values[a]));return d*d;},options.CurveError*options.CurveError);
                result.Curves.Add(VmdCurveUtility.BuildCurve(path,name,kind,times,keys,i=>values[i]));
            }
            if(!VmdOutputModes.IsFk(options.OutputKind))
            {
                for(int c=0;c<3;c++){int axis=c;Curve("","RootT."+"xyz"[c],2,fitted.frames.Select(f=>f.humanPose.bodyPosition[axis]).ToArray());}
                // Quaternion components must share the same retained key times.
                var qs=fitted.frames.Select(f=>Math3.Q(f.humanPose.bodyRotation)).ToArray();
                WriteQuaternion("","RootQ.",2,qs);
                for(int m=0;m<rig.muscles.Length;m++){int channel=m;var values=fitted.frames.Select(f=>f.humanPose.muscles[channel]).ToArray();if(values.Any(v=>Math.Abs(v)>1e-7))Curve("",MuscleProperty(rig.muscles[m].name),2,values);}
            }
            else
            {
                var poses=fk.Select(f=>new SkeletonPose(rig,f)).ToArray();
                for(int b=0;b<rig.bones.Length;b++)
                {
                    if(rig.bones[b].parent<0)continue;
                    string path=BonePath(b);Q[] rotations;V[] positions;
                    if(legacyBindings.TryGetValue(rig.bones[b].humanBone,out var binding))
                    {
                        path=binding.path;rotations=new Q[fk.Length];positions=new V[fk.Length];
                        for(int i=0;i<fk.Length;i++)LegacyLocal(binding,b,poses[i],out positions[i],out rotations[i]);
                    }
                    else {rotations=poses.Select(p=>p.LocalRotations[b]).ToArray();positions=poses.Select(p=>p.LocalPositions[b]).ToArray();}
                    WriteQuaternion(path,"m_LocalRotation.",0,rotations);
                    var rest=legacyBindings.TryGetValue(rig.bones[b].humanBone,out var old)?new V(old.restLocalPosition.x,old.restLocalPosition.y,old.restLocalPosition.z):Math3.V(rig.bindBones[b].position);
                    if(b==rig.hips||positions.Any(p=>V.Distance(p,rest)>1e-7f))
                    {
                        var vs=positions.Select(p=>new UnityEngine.Vector3(p.X,p.Y,p.Z)).ToArray();
                        int[] keys=options.PositionError<=0?all:VmdCurveUtility.ReduceVector(times,vs,options.PositionError);
                        for(int c=0;c<3;c++){int axis=c;result.Curves.Add(VmdCurveUtility.BuildCurve(path,"m_LocalPosition."+"xyz"[c],0,times,keys,i=>positions[i][axis]));}
                    }
                }
            }
            return result;
            void WriteQuaternion(string path,string prefix,byte kind,Q[] rotations)
            {
                for(int i=1;i<rotations.Length;i++)if(Q.Dot(rotations[i-1],rotations[i])<0)rotations[i]=-rotations[i];
                var unity=rotations.Select(q=>new UnityEngine.Quaternion(q.X,q.Y,q.Z,q.W)).ToArray();
                int[] keys=options.RotationErrorDegrees<=0?all:VmdCurveUtility.ReduceQuaternion(times,unity,options.RotationErrorDegrees);
                for(int c=0;c<4;c++){int axis=c;result.Curves.Add(VmdCurveUtility.BuildCurve(path,prefix+"xyzw"[c],kind,times,keys,i=>axis==0?rotations[i].X:axis==1?rotations[i].Y:axis==2?rotations[i].Z:rotations[i].W));}
            }
        }
        void LegacyLocal(VmdTargetBoneBinding binding,int b,SkeletonPose pose,out V position,out Q rotation)
        {
            int parent=rig.bones[b].parent;
            Q delta=parent<0?Q.Identity:Q.Normalize(pose.WorldRotations[parent]*Q.Inverse(bind.WorldRotations[parent]));
            Q restParent=new(binding.restParentWorldRotation.x,binding.restParentWorldRotation.y,binding.restParentWorldRotation.z,binding.restParentWorldRotation.w);
            rotation=Q.Normalize(Q.Inverse(delta*restParent)*pose.WorldRotations[b]);
            var matrix=new Matrix4x4(binding.parentAxisX.x,binding.parentAxisX.y,binding.parentAxisX.z,0,
                binding.parentAxisY.x,binding.parentAxisY.y,binding.parentAxisY.z,0,binding.parentAxisZ.x,binding.parentAxisZ.y,binding.parentAxisZ.z,0,0,0,0,1);
            if(!Matrix4x4.Invert(matrix,out var toRoot))throw new InvalidDataException("Singular Unity parent transform");
            V localRest=new(binding.restLocalPosition.x,binding.restLocalPosition.y,binding.restLocalPosition.z);
            V parentRest=bind.WorldPositions[b]-V.TransformNormal(localRest,toRoot);
            V parentPosition=parent<0?parentRest:pose.WorldPositions[parent]+Math3.Rotate(delta,parentRest-bind.WorldPositions[parent]);
            position=V.TransformNormal(Math3.Rotate(Q.Inverse(delta),pose.WorldPositions[b]-parentPosition),matrix);
        }
        string BonePath(int b){var names=new List<string>();for(int i=b;i>=0&&rig.bones[i].parent>=0;i=rig.bones[i].parent)names.Add(rig.bones[i].name);names.Reverse();return string.Join('/',names);}
        static bool IsUpper(int h)=>h is >=11 and <=18 or >=24 and <=53;
        static string MuscleProperty(string name)
        {
            foreach(string side in new[]{"Left","Right"})foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
                if(name.StartsWith(side+" "+finger+" ",StringComparison.Ordinal))return side+"Hand."+finger+"."+name[(side.Length+finger.Length+2)..];
            return name;
        }
    }
}
