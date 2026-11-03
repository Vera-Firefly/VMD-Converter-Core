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
        readonly Rig rig,sourceRig,encodingRig;
        readonly SkeletonPose bind,sourceBind;
        readonly HumanoidSolver solver,sourceSolver,decoder;
        readonly HumanoidUpperProjector sourceUpper,targetUpper;
        readonly SourceLegEncoder? sourceLegEncoder;
        readonly Dictionary<int,int> source,target,sourceTarget;
        readonly (int target,int source)[] musclePairs;
        readonly int[] targetMappedAncestors;
        readonly bool[] upperNodes;
        readonly bool canonical,nativeCalibration;
        readonly bool sourceForwardFix;
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
            sourceForwardFix=options.OutputKind is VmdExternalOutputKind.HumanoidIk or VmdExternalOutputKind.TargetFk;
            source=PmxRigBuilder.Map(model);
            sourceRig=PmxRigBuilder.Build(model,options.PositionScale,options.CoordinateConversion,options.ProfilePath);
            if(sourceRig.bindBones.Length==0)sourceRig.bindBones=sourceRig.bones;
            sourceBind=new SkeletonPose(new Rig{bones=sourceRig.bindBones});
            canonical=options.FbxPath==null&&options.HpsRigPath==null&&options.DescriptorPath==null&&legacy==null;
            bool sourceLegEncoding=options.OutputKind==VmdExternalOutputKind.HumanoidIk&&!canonical;
            if(sourceLegEncoding)sourceRig=SourceLegEncoder.CreateRig(sourceRig,options.ProfilePath);
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
            encodingRig=canonical?rig:OfflineRigFactory.ForHumanoidEncoding(rig,options.ProfilePath);
            if(sourceLegEncoding)encodingRig=OfflineRigFactory.ForHumanoidLegEncoding(encodingRig);
            solver=new HumanoidSolver(encodingRig,selected,policy){FitDecodedArmContext=true,FitLegGeometry=geometry,AnchorLimbTwistToInitial=true,ReconstructDecodedRoot=true};
            decoder=new HumanoidSolver(encodingRig,rangePolicy:policy);
            sourceSolver=new HumanoidSolver(sourceRig,sourceRig.muscles.Where(m=>(m.humanBone is <11 or >18)&&!(sourceLegEncoding&&m.humanBone is >=1 and <=6)).Select(m=>m.humanBone).ToHashSet(),policy)
                {ReconstructDecodedRoot=true};
            if(sourceLegEncoding)sourceLegEncoder=new SourceLegEncoder(sourceRig,policy);
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
            RigIdentity=SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rig,HpsJsonContext.Default.Rig)+"|source-retarget-upper-continuity-root-v5|"+(sourceForwardFix?"source-forward-clock-curves-v2|":"")+(sourceLegEncoding?"source-leg-direction-v2|":"")+VmdOutputModes.ToArgument(options.OutputKind)+"|"+options.RangePolicy+"|"+options.LegFit));
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
            var seeds=new float[samples.Length][];var sourceBase=new float[samples.Length][];
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
                sourceBase[frame]=sourceLegEncoder?.Project(raw,sourceResult.humanPose.muscles,options.Passes)??sourceResult.humanPose.muscles;
                directFrames[frame]=Build(rig,bind,target,canonical,targetScale,options.TargetLegIk??false,false);
            });
            var sourceProjected=(sourceForwardFix?sourceUpper.ProjectSourceBatch(rawSource,sourceBase,threads,scheduler,sourceSolver.RangePolicy):sourceUpper.ProjectBatch(rawSource,sourceBase,threads,scheduler,sourceSolver.RangePolicy));
            Parallel.For(0,frames.Length,new ParallelOptions{MaxDegreeOfParallelism=threads,TaskScheduler=scheduler??TaskScheduler.Default},frame=>
            {
                var raw=rawSource[frame];float[] sourceMuscles=sourceProjected[frame];
                var sourceRoot=sourceSolver.ReconstructRoot(raw,sourceMuscles);
                var seed=new float[rig.muscles.Length];foreach(var pair in musclePairs)seed[pair.target]=sourceMuscles[pair.source];
                var root=new HumanPose{muscles=seed,
                    bodyPosition=Math3.A(Math3.V(rig.bodyPosition)+Math3.V(sourceRoot.bodyPosition)-Math3.V(sourceRig.bodyPosition)),
                    bodyRotation=Math3.A(Q.Normalize(Math3.Q(sourceRoot.bodyRotation)*Q.Inverse(Math3.Q(sourceRig.bodyRotation))*Math3.Q(rig.bodyRotation)))};
                var pose=decoder.Decode(seed,root);
                PoseFrame direct=directFrames[frame];
                if(region!=VmdTargetSolveRegion.Canonical)
                {
                    var targetPose=new SkeletonPose(rig,direct);
                    for(int b=0;b<rig.bones.Length;b++)if(region==VmdTargetSolveRegion.All ||
                        region==VmdTargetSolveRegion.Arm&&upperNodes[b] || region==VmdTargetSolveRegion.Leg&&!upperNodes[b])
                    {pose.LocalPositions[b]=targetPose.LocalPositions[b];pose.LocalRotations[b]=targetPose.LocalRotations[b];}
                    pose.UpdateWorld();
                }
                var goal=pose.ToFrame(raw.time);

                // PMX muscle coordinates are valid for source retargeting, but
                // can use a remote periodic branch (including tangent limits).
                // They are not target-leg optimizer coordinates. Start only
                // the selected leg channels in the target's neutral chart;
                // preserve all source channels outside that target solve.
                if(region is VmdTargetSolveRegion.All or VmdTargetSolveRegion.Leg)
                    for(int m=0;m<seed.Length;m++)if(rig.muscles[m].humanBone is >=1 and <=6)seed[m]=0;
                frames[frame]=goal;seeds[frame]=seed;
            });
            if(region is VmdTargetSolveRegion.All or VmdTargetSolveRegion.Arm)
            {
                var upperSeeds=targetUpper.ProjectBatch(frames,seeds,threads,scheduler,solver.RangePolicy);
                for(int i=0;i<seeds.Length;i++)seeds[i]=solver.CreateFitSeed(upperSeeds[i]);
            }
            ResultBatch fitted=solver.SolveBatch(new PoseBatch{frames=frames},threads,options.Passes,scheduler,seeds);
            if(region is VmdTargetSolveRegion.Canonical or VmdTargetSolveRegion.Leg)
                for(int i=0;i<frames.Length;i++)for(int m=0;m<rig.muscles.Length;m++)
                    if(IsUpper(rig.muscles[m].humanBone)&&fitted.frames[i].humanPose.muscles[m]!=seeds[i][m])
                        throw new InvalidDataException("Unselected source upper muscles changed during body fitting.");
            if(region==VmdTargetSolveRegion.Arm)
                for(int i=0;i<frames.Length;i++)for(int m=0;m<rig.muscles.Length;m++)
                    if(!IsUpper(rig.muscles[m].humanBone)&&fitted.frames[i].humanPose.muscles[m]!=seeds[i][m])
                        throw new InvalidDataException("Unselected source body muscles changed during arm fitting.");
            if(region is VmdTargetSolveRegion.All or VmdTargetSolveRegion.Arm)
            {
                var continued=targetUpper.StabilizeBatch(frames,fitted.frames.Select(f=>f.humanPose.muscles).ToArray(),threads,scheduler,solver.RangePolicy);
                // Arm continuation changes the body basis and mass center used by
                // RootT/RootQ. Rebuild them after the final muscles to keep Hips
                // at the already solved target, before decoding FK or writing curves.
                Parallel.For(0,frames.Length,new ParallelOptions{MaxDegreeOfParallelism=threads,TaskScheduler=scheduler??TaskScheduler.Default},
                    i=>fitted.frames[i].humanPose=solver.ReconstructRoot(frames[i],continued[i]));
            }
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
            // Forward source projection already owns the arm branch; do not replace it in a second FK pass.
            if(VmdOutputModes.IsFk(options.OutputKind)&&options.FkContinuity&&!sourceForwardFix)fk=FkBranchContinuation.Process(rig,sourceRig,rawSource,fk);
            if(options.DiagnosticsDirectory!=null)ConversionDiagnostics.Write(options.DiagnosticsDirectory,motion,rig,sourceRig,rawSource,directFrames,frames,fk,decoded,region.ToString(),VmdOutputModes.ToArgument(options.OutputKind));
            if(sourceForwardFix&&!VmdOutputModes.IsFk(options.OutputKind)) (frames,fitted,fk)=RefineForwardCurves(frames,fitted,fk,threads,scheduler);
            var result=WriteCurves(frames,fitted,fk);
            if(options.BakeMorphs)VmdCurveUtility.AddMorphCurves(motion,AnimationRig,result);
            return result;
        }

        // Fit the forward Humanoid curve path without target IK goals or changes to unselected muscles.
        (PoseFrame[],ResultBatch,PoseFrame[]) RefineForwardCurves(PoseFrame[] frames,ResultBatch fitted,PoseFrame[] fk,int threads,TaskScheduler? scheduler)
        {
            var encoder=new HumanoidSolver(encodingRig,Enumerable.Range(13,6).ToHashSet(),decoder.RangePolicy,true){FitArmGeometry=true,FitDecodedArmContext=true,AnchorLimbTwistToInitial=true,FitLegGeometry=false,ReconstructDecodedRoot=true};
            PoseFrame Mix(PoseFrame a,PoseFrame b,float t,float time){
                var rotations=new float[a.rotations.Length];var positions=new float[a.positions.Length];
                for(int i=0;i<rig.bones.Length;i++){Q q=Q.Lerp(Math3.Q(a.rotations,i*4),Math3.Q(b.rotations,i*4),t);var v=Math3.A(q);Array.Copy(v,0,rotations,i*4,4);}
                for(int i=0;i<positions.Length;i++)positions[i]=a.positions[i]+t*(b.positions[i]-a.positions[i]);
                return new PoseFrame{time=time,rotations=rotations,positions=positions};
            }
            float[] Seed(int i,float t)=>fitted.frames[i].humanPose.muscles.Zip(fitted.frames[i+1].humanPose.muscles,(a,b)=>a+t*(b-a)).ToArray();
            (V[] directions,Q[] hands,V[] points) Shape(PoseFrame goal,float[] values){
                var actual=decoder.Decode(values);var directions=new List<V>();var hands=new List<Q>();var points=new List<V>();Q inverse=Q.Inverse(actual.WorldRotations[rig.hips]);V origin=actual.WorldPositions[rig.hips];
                for(int side=0;side<2;side++){int a=target[13+side],b=target[15+side],c=target[17+side];directions.Add(V.Normalize(Math3.Rotate(inverse,actual.WorldPositions[b]-actual.WorldPositions[a])));directions.Add(V.Normalize(Math3.Rotate(inverse,actual.WorldPositions[c]-actual.WorldPositions[b])));foreach(int node in new[]{a,b,c}){hands.Add(Q.Normalize(inverse*actual.WorldRotations[node]));points.Add(Math3.Rotate(inverse,actual.WorldPositions[node]-origin));}}
                return(directions.ToArray(),hands.ToArray(),points.ToArray());
            }
            (V[] directions,Q[] hands,V[] points) Expected(PoseFrame goal){
                var actual=new SkeletonPose(rig,goal);var directions=new List<V>();var hands=new List<Q>();var points=new List<V>();Q inverse=Q.Inverse(actual.WorldRotations[rig.hips]);V origin=actual.WorldPositions[rig.hips];
                for(int side=0;side<2;side++){int a=target[13+side],b=target[15+side],c=target[17+side];directions.Add(V.Normalize(Math3.Rotate(inverse,actual.WorldPositions[b]-actual.WorldPositions[a])));directions.Add(V.Normalize(Math3.Rotate(inverse,actual.WorldPositions[c]-actual.WorldPositions[b])));foreach(int node in new[]{a,b,c}){hands.Add(Q.Normalize(inverse*actual.WorldRotations[node]));points.Add(Math3.Rotate(inverse,actual.WorldPositions[node]-origin));}}
                return(directions.ToArray(),hands.ToArray(),points.ToArray());
            }
            float ShapeError((V[] directions,Q[] hands,V[] points) a,(V[] directions,Q[] hands,V[] points) b){float error=0;for(int i=0;i<a.directions.Length;i++)error=Math.Max(error,MathF.Acos(Math.Clamp(V.Dot(a.directions[i],b.directions[i]),-1,1))*180/MathF.PI);for(int i=0;i<a.hands.Length;i++){if(i%3==2)error=Math.Max(error,Math3.Angle(a.hands[i],b.hands[i]));error=Math.Max(error,V.Distance(a.points[i],b.points[i])/Math.Max(.1f,rig.humanScale)*180/MathF.PI);}return error;}
            float Error(PoseFrame goal,float[] values)=>ShapeError(Shape(goal,values),Expected(goal));
            float[] CoupledFit(PoseFrame goal,float[] initial){
                var expected=Expected(goal);var values=(float[])initial.Clone();
                for(int side=0;side<2;side++){
                    int shoulder=target[11+side];int[] slots=Enumerable.Range(0,rig.muscles.Length).Where(m=>new[]{13+side,15+side,17+side}.Contains(rig.muscles[m].humanBone)).ToArray();
                    if(slots.Length!=7)throw new InvalidDataException("Curve arm parameter count differs");
                    double[] Residual(float[] v){
                        var pose=encoder.Decode(v);Q inverse=Q.Inverse(pose.WorldRotations[rig.hips]);V origin=pose.WorldPositions[rig.hips];var r=new List<double>();
                        for(int k=0;k<3;k++){
                            int node=target[13+side+2*k];Q q=Q.Normalize(inverse*pose.WorldRotations[node]);V log=Math3.Log(Q.Normalize(Q.Inverse(expected.hands[side*3+k])*q));float weight=k==2?1.5f:.02f;r.Add(log.X*weight);r.Add(log.Y*weight);r.Add(log.Z*weight);
                            V delta=(Math3.Rotate(inverse,pose.WorldPositions[node]-origin)-expected.points[side*3+k])/Math.Max(.1f,rig.humanScale)*4;r.Add(delta.X);r.Add(delta.Y);r.Add(delta.Z);
                        }
                        var clav=Math3.Log(Q.Normalize(Q.Inverse(Math3.Q(goal.rotations,shoulder*4))*pose.LocalRotations[shoulder]));r.Add(clav.X*.03);r.Add(clav.Y*.03);r.Add(clav.Z*.03);
                        foreach(int slot in slots)r.Add((v[slot]-initial[slot])*.001);return r.ToArray();
                    }
                    double Sum(double[] v)=>v.Sum(x=>x*x);var residual=Residual(values);double cost=Sum(residual),lambda=.001;
                    for(int iteration=0;iteration<28;iteration++){
                        var jacobian=new double[7][];
                        for(int c=0;c<7;c++){
                            var probe=(float[])values.Clone();probe[slots[c]]+=.001f;probe=encoder.CreateFitSeed(probe);double step=probe[slots[c]]-values[slots[c]];
                            if(Math.Abs(step)<1e-7){probe[slots[c]]-=.001f;probe=encoder.CreateFitSeed(probe);step=probe[slots[c]]-values[slots[c]];}
                            var r=Residual(probe);jacobian[c]=r.Select((v,i)=>Math.Abs(step)<1e-7?0:(v-residual[i])/step).ToArray();
                        }
                        var matrix=new double[7,8];for(int i=0;i<7;i++){for(int j=0;j<7;j++)for(int n=0;n<residual.Length;n++)matrix[i,j]+=jacobian[i][n]*jacobian[j][n];for(int n=0;n<residual.Length;n++)matrix[i,7]-=jacobian[i][n]*residual[n];matrix[i,i]+=lambda*Math.Max(1,matrix[i,i]);}
                        for(int i=0;i<7;i++){int pivot=i;for(int j=i+1;j<7;j++)if(Math.Abs(matrix[j,i])>Math.Abs(matrix[pivot,i]))pivot=j;if(pivot!=i)for(int k=i;k<8;k++)(matrix[i,k],matrix[pivot,k])=(matrix[pivot,k],matrix[i,k]);double d=matrix[i,i];for(int k=i;k<8;k++)matrix[i,k]/=d;for(int j=0;j<7;j++)if(j!=i){d=matrix[j,i];for(int k=i;k<8;k++)matrix[j,k]-=d*matrix[i,k];}}
                        var next=(float[])values.Clone();for(int i=0;i<7;i++)next[slots[i]]+=(float)Math.Clamp(matrix[i,7],-.2,.2);next=encoder.CreateFitSeed(next);var candidate=Residual(next);double nextCost=Sum(candidate);
                        if(nextCost<cost){double gain=cost-nextCost;values=next;residual=candidate;cost=nextCost;lambda=Math.Max(1e-8,lambda/3);if(gain<1e-9)break;}else{lambda*=5;if(lambda>1e8)break;}
                    }
                }
                return values;
            }

            (float angle,float increment) PathScore(int index,List<(PoseFrame frame,SolveResult result)> inserted){
                var poses=new List<SolveResult>{fitted.frames[index]};poses.AddRange(inserted.Select(e=>e.result));poses.Add(fitted.frames[index+1]);
                var keys=poses.Select(p=>p.time).ToArray();var channels=new float[rig.muscles.Length][];
                for(int m=0;m<channels.Length;m++){channels[m]=poses.Select(p=>p.humanPose.muscles[m]).ToArray();UnwrapUpperAngle(channels[m],rig.muscles[m]);}
                float angle=0,increment=0;(V[] directions,Q[] hands,V[] points)? previous=null,previousGoal=null;
                for(int k=0;k<=16;k++){
                    float t=k/16f,time=frames[index].time+t*(frames[index+1].time-frames[index].time);int right=Array.FindIndex(keys,v=>v>=time);if(right<0)right=keys.Length-1;int left=Math.Max(0,right-1);float f=right==left?0:(time-keys[left])/(keys[right]-keys[left]);var values=new float[channels.Length];
                    for(int m=0;m<values.Length;m++){
                        float value=channels[m][left]+f*(channels[m][right]-channels[m][left]);
                        if(rig.muscles[m].humanBone is >=11 and <=18 && rig.muscles[m].dof!=0){var muscle=rig.muscles[m];float degrees=value*(value<0?-muscle.minimum:muscle.maximum);degrees-=360*MathF.Floor((degrees+180)/360);value=degrees/(degrees<0?-muscle.minimum:muscle.maximum);}values[m]=value;
                    }
                    var goal=Mix(fk[index],fk[index+1],t,time);var q=Shape(goal,values);var expected=Expected(goal);angle=Math.Max(angle,ShapeError(q,expected));
                    if(previous!=null){for(int b=0;b<q.hands.Length;b++)if(b%3==2)increment=Math.Max(increment,Math3.Angle(q.hands[b]*Q.Inverse(previous.Value.hands[b]),expected.hands[b]*Q.Inverse(previousGoal!.Value.hands[b])));for(int b=0;b<q.directions.Length;b++)increment=Math.Max(increment,((q.directions[b]-previous.Value.directions[b])-(expected.directions[b]-previousGoal!.Value.directions[b])).Length()*180/MathF.PI);}
                    previous=q;previousGoal=expected;
                }
                return(angle,increment);
            }
            int refined=0,rejected=0,pathRejected=0;
            (PoseFrame frame,SolveResult result) FitPoint(int i,float t,float[]? continuation){
                        var goal=Mix(fk[i],fk[i+1],t,frames[i].time+t*(frames[i+1].time-frames[i].time));var seed=encoder.CreateFitSeed(Seed(i,t));float initial=Error(goal,seed);
                        var coupled=CoupledFit(goal,seed);var solved=new SolveResult{time=goal.time,humanPose=encoder.ReconstructRoot(goal,coupled)};float error=Error(goal,coupled);
                        if(error>.5f){
                            var neutral=(float[])seed.Clone();for(int m=0;m<neutral.Length;m++)if(rig.muscles[m].humanBone is >=13 and <=18)neutral[m]=0;
                            var projected=targetUpper.Project(goal,neutral);
                            var starts=new[]{neutral,projected,fitted.frames[i].humanPose.muscles,fitted.frames[i+1].humanPose.muscles};
                            foreach(var start in starts){
                                var alternative=(float[])seed.Clone();for(int m=0;m<alternative.Length;m++)if(rig.muscles[m].humanBone is >=13 and <=18)alternative[m]=start[m];
                                alternative=CoupledFit(goal,encoder.CreateFitSeed(alternative));float e=Error(goal,alternative);
                                if(e<error){error=e;solved=new SolveResult{time=goal.time,humanPose=encoder.ReconstructRoot(goal,alternative)};}
                            }
                        }
                        if(error>initial+.001f){solved=new SolveResult{time=goal.time,humanPose=encoder.ReconstructRoot(goal,seed)};Interlocked.Increment(ref rejected);}
                        for(int m=0;m<rig.muscles.Length;m++)if(rig.muscles[m].humanBone is not (>=11 and <=18)&&solved.humanPose.muscles[m]!=seed[m])throw new InvalidDataException("Curve encoder modified unselected muscles");
                        if(continuation!=null){var warm=(float[])seed.Clone();for(int m=0;m<warm.Length;m++)if(rig.muscles[m].humanBone is >=11 and <=18)warm[m]=continuation[m];warm=CoupledFit(goal,encoder.CreateFitSeed(warm));float warmError=Error(goal,warm);if(warmError<error){solved=new SolveResult{time=goal.time,humanPose=encoder.ReconstructRoot(goal,warm)};}}
                        return(goal,solved);
            }
            List<(PoseFrame frame,SolveResult result)> FitTrajectory(int index){
                const int divisions=4,samples=16;var knots=new float[divisions+1][];knots[0]=fitted.frames[index].humanPose.muscles;knots[^1]=fitted.frames[index+1].humanPose.muscles;
                for(int k=1;k<divisions;k++)knots[k]=encoder.CreateFitSeed(Seed(index,k/(float)divisions));
                var goals=Enumerable.Range(0,samples+1).Select(k=>Mix(fk[index],fk[index+1],k/(float)samples,frames[index].time+k/(float)samples*(frames[index+1].time-frames[index].time))).ToArray();var desired=goals.Select(Expected).ToArray();
                for(int side=0;side<2;side++){
                    int[] slots=Enumerable.Range(0,rig.muscles.Length).Where(m=>new[]{13+side,15+side,17+side}.Contains(rig.muscles[m].humanBone)).ToArray();int n=slots.Length*(divisions-1);if(n!=21)throw new InvalidDataException("Curve trajectory dimension differs");
                    var initial=knots.Select(k=>(float[])k.Clone()).ToArray();
                    double[] Residual(float[][] values){
                        var channels=new float[slots.Length][];for(int c=0;c<slots.Length;c++){channels[c]=values.Select(v=>v[slots[c]]).ToArray();UnwrapUpperAngle(channels[c],rig.muscles[slots[c]]);}
                        var actual=new (V[] directions,Q[] hands,V[] points)[samples+1];
                        for(int k=0;k<=samples;k++){
                            float t=k/(float)samples;int a=Math.Min(divisions-1,k*divisions/samples),b=a+1;float f=t*divisions-a;var v=Seed(index,t);
                            for(int c=0;c<slots.Length;c++){
                                int slot=slots[c];float x=channels[c][a]+f*(channels[c][b]-channels[c][a]);var muscle=rig.muscles[slot];
                                if(muscle.dof!=0){float angle=x*(x<0?-muscle.minimum:muscle.maximum);angle-=360*MathF.Floor((angle+180)/360);x=angle/(angle<0?-muscle.minimum:muscle.maximum);}v[slot]=x;
                            }
                            actual[k]=Shape(goals[k],v);
                        }
                        var r=new List<double>();double velocity=2*(1d/30)/(frames[index+1].time-frames[index].time);
                        void Put(V v,double weight){r.Add(v.X*weight);r.Add(v.Y*weight);r.Add(v.Z*weight);}
                        for(int k=0;k<=samples;k++)for(int node=0;node<3;node++){
                            int slot=side*3+node;double weight=node==2?1.5:.02;
                            if(k>0&&k<samples){Put(Math3.Log(Q.Normalize(Q.Inverse(desired[k].hands[slot])*actual[k].hands[slot])),weight);Put((actual[k].points[slot]-desired[k].points[slot])/Math.Max(.1f,rig.humanScale),4);}
                            if(k>0){Q a=Q.Normalize(actual[k].hands[slot]*Q.Inverse(actual[k-1].hands[slot])),b=Q.Normalize(desired[k].hands[slot]*Q.Inverse(desired[k-1].hands[slot]));Put(Math3.Log(Q.Normalize(Q.Inverse(b)*a)),velocity*weight);Put(((actual[k].points[slot]-actual[k-1].points[slot])-(desired[k].points[slot]-desired[k-1].points[slot]))/Math.Max(.1f,rig.humanScale),velocity*4);}
                        }
                        for(int k=1;k<divisions;k++)foreach(int slot in slots)r.Add((values[k][slot]-initial[k][slot])*.001);return r.ToArray();
                    }
                    double Sum(double[] v)=>v.Sum(x=>x*x);var residual=Residual(knots);double cost=Sum(residual),lambda=.001;
                    for(int iteration=0;iteration<24;iteration++){
                        var jacobian=new double[n][];
                        for(int c=0;c<n;c++){
                            int k=c/slots.Length+1,slot=slots[c%slots.Length];var probe=knots.Select(v=>(float[])v.Clone()).ToArray();probe[k][slot]+=.001f;probe[k]=encoder.CreateFitSeed(probe[k]);double step=probe[k][slot]-knots[k][slot];
                            if(Math.Abs(step)<1e-7){probe[k][slot]-=.001f;probe[k]=encoder.CreateFitSeed(probe[k]);step=probe[k][slot]-knots[k][slot];}
                            var value=Residual(probe);jacobian[c]=value.Select((x,i)=>Math.Abs(step)<1e-7?0:(x-residual[i])/step).ToArray();
                        }
                        var matrix=new double[n,n+1];for(int i=0;i<n;i++){for(int j=0;j<n;j++)for(int k=0;k<residual.Length;k++)matrix[i,j]+=jacobian[i][k]*jacobian[j][k];for(int k=0;k<residual.Length;k++)matrix[i,n]-=jacobian[i][k]*residual[k];matrix[i,i]+=lambda*Math.Max(1,matrix[i,i]);}
                        for(int i=0;i<n;i++){int pivot=i;for(int j=i+1;j<n;j++)if(Math.Abs(matrix[j,i])>Math.Abs(matrix[pivot,i]))pivot=j;if(pivot!=i)for(int k=i;k<=n;k++)(matrix[i,k],matrix[pivot,k])=(matrix[pivot,k],matrix[i,k]);double d=matrix[i,i];for(int k=i;k<=n;k++)matrix[i,k]/=d;for(int j=0;j<n;j++)if(j!=i){d=matrix[j,i];for(int k=i;k<=n;k++)matrix[j,k]-=d*matrix[i,k];}}
                        var next=knots.Select(v=>(float[])v.Clone()).ToArray();for(int c=0;c<n;c++)next[c/slots.Length+1][slots[c%slots.Length]]+=(float)Math.Clamp(matrix[c,n],-.2,.2);for(int k=1;k<divisions;k++)next[k]=encoder.CreateFitSeed(next[k]);var candidate=Residual(next);double nextCost=Sum(candidate);
                        if(nextCost<cost){double gain=cost-nextCost;knots=next;residual=candidate;cost=nextCost;lambda=Math.Max(1e-8,lambda/3);if(gain<1e-8)break;}else{lambda*=5;if(lambda>1e8)break;}
                    }
                }
                var result=new List<(PoseFrame,SolveResult)>();for(int k=1;k<divisions;k++){var goal=goals[k*samples/divisions];result.Add((goal,new SolveResult{time=goal.time,humanPose=encoder.ReconstructRoot(goal,knots[k])}));}return result;
            }
            var segments=new List<(PoseFrame frame,SolveResult result)>[Math.Max(0,frames.Length-1)];
            Parallel.For(0,segments.Length,new ParallelOptions{MaxDegreeOfParallelism=threads,TaskScheduler=scheduler??TaskScheduler.Default},i=>{
                var entries=new List<(PoseFrame,SolveResult)>();bool needs=false;
                for(int k=1;k<4;k++){float t=k/4f;var goal=Mix(fk[i],fk[i+1],t,frames[i].time+t*(frames[i+1].time-frames[i].time));if(Error(goal,Seed(i,t))>.1f){needs=true;break;}}
                if(needs){
                    for(int k=1;k<4;k++)entries.Add(FitPoint(i,k/4f,null));
                    try{
                        var oldPath=PathScore(i,new List<(PoseFrame,SolveResult)>());var newPath=PathScore(i,entries);
                        if(newPath.angle>oldPath.angle+.05f||newPath.increment>oldPath.increment+.05f){
                            var denser=new List<(PoseFrame frame,SolveResult result)>();float[] previous=fitted.frames[i].humanPose.muscles;
                            for(int k=1;k<8;k++){var point=FitPoint(i,k/8f,previous);denser.Add(point);previous=point.result.humanPose.muscles;}
                            var densePath=PathScore(i,denser);
                            if(densePath.angle<=oldPath.angle+.05f&&densePath.increment<=oldPath.increment+.05f){entries=denser;Interlocked.Increment(ref refined);}else{
                                var trajectory=FitTrajectory(i);var trajectoryPath=PathScore(i,trajectory);
                                if(trajectoryPath.angle<=oldPath.angle+.05f&&trajectoryPath.increment<=oldPath.increment+.05f){entries=trajectory;Interlocked.Increment(ref refined);}else{entries.Clear();Interlocked.Increment(ref pathRejected);}
                            }
                        }else Interlocked.Increment(ref refined);
                    }catch(InvalidDataException){entries.Clear();Interlocked.Increment(ref pathRejected);}
                }
                segments[i]=entries;
            });
            var outputFrames=new List<PoseFrame>();var outputResults=new List<SolveResult>();var outputFk=new List<PoseFrame>();
            for(int i=0;i<frames.Length;i++){outputFrames.Add(frames[i]);outputResults.Add(fitted.frames[i]);outputFk.Add(fk[i]);if(i<segments.Length)foreach(var entry in segments[i]){outputFrames.Add(entry.frame);outputResults.Add(entry.result);outputFk.Add(entry.frame);}}
            Console.Error.WriteLine($"Forward pose curve encoding: intervals={refined}; added={outputFrames.Count-frames.Length}; fit fallback={rejected}; path rejected={pathRejected}; PMX samples unchanged={frames.Length}");
            return(outputFrames.ToArray(),new ResultBatch{frames=outputResults.ToArray()},outputFk.ToArray());
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
                for(int m=0;m<rig.muscles.Length;m++){int channel=m;var values=fitted.frames.Select(f=>f.humanPose.muscles[channel]).ToArray();UnwrapUpperAngle(values,rig.muscles[m]);if(values.Any(v=>Math.Abs(v)>1e-7))Curve("",MuscleProperty(rig.muscles[m].name),2,values);}
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
        static void UnwrapUpperAngle(float[] values,Muscle muscle)
        {
            // Distributed upper/lower twist is deliberately excluded.
            if(muscle.unityIndex is not (37 or 38 or 39 or 40 or 42 or 44 or 45 or 46 or 47 or 48 or 49 or 51 or 53 or 54))return;
            double previous=0;for(int i=0;i<values.Length;i++){
                double angle=values[i]*(values[i]<0?-muscle.minimum:muscle.maximum);
                if(i>0){while(angle-previous>180)angle-=360;while(angle-previous< -180)angle+=360;}
                previous=angle;values[i]=(float)(angle/(angle<0?-muscle.minimum:muscle.maximum));
            }
        }
        static bool IsUpper(int h)=>h is >=11 and <=18 or >=24 and <=53;
        static string MuscleProperty(string name)
        {
            foreach(string side in new[]{"Left","Right"})foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
                if(name.StartsWith(side+" "+finger+" ",StringComparison.Ordinal))return side+"Hand."+finger+"."+name[(side.Length+finger.Length+2)..];
            return name;
        }
    }
}
