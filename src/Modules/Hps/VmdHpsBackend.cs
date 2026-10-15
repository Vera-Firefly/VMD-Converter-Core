using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityHps;
using Q=System.Numerics.Quaternion;
using V=System.Numerics.Vector3;

namespace VeraFirefly.VmdConverter.Core.Hps;

public sealed class VmdHpsBackend : IVmdHumanoidBackend
{
    public string Name=>"Unity-HPS";
    public IVmdHumanoidSession Create(PmxModel model,VmdHumanoidBackendOptions options,VmdTargetRigData? legacyRig)=>new Session(model,options,legacyRig);

    private sealed class Session : IVmdHumanoidSession
    {
        private readonly PmxModel model;
        private readonly VmdHumanoidBackendOptions options;
        private readonly Rig rig;
        private readonly SkeletonPose bind;
        private readonly HumanoidSolver solver;
        private readonly Dictionary<int,int> source;
        private readonly Dictionary<int,int> target;
        private readonly bool canonical;
        private readonly float targetScale;
        private readonly string? legacyFkJson;
        private static readonly JsonSerializerOptions legacyJson=new(){IncludeFields=true,IgnoreReadOnlyProperties=true};
        public VmdTargetRigData AnimationRig{get;}
        public bool UsesSharedFramePool=>legacyFkJson==null;
        public string Precision=>legacyFkJson==null?rig.precision:"legacy-target-fk";
        public long EstimatedBytesPerSample=>rig.bones.Length*192L+rig.muscles.Length*32L+4096;
        public byte[] RigIdentity{get;}

        public Session(PmxModel model,VmdHumanoidBackendOptions options,VmdTargetRigData? legacy)
        {
            this.model=model;this.options=options;source=PmxRigBuilder.Map(model);
            legacyFkJson=legacy!=null&&options.FbxPath==null&&options.HpsRigPath==null&&options.OutputKind==VmdExternalOutputKind.TargetFk
                ?JsonSerializer.Serialize(legacy,legacyJson):null;
            canonical=options.OutputKind==VmdExternalOutputKind.HumanoidIk||options.FbxPath==null&&options.HpsRigPath==null&&legacy==null;
            if(options.HpsRigPath!=null) rig=HpsJson.ReadRig(options.HpsRigPath);
            else if(options.FbxPath!=null&&options.OutputKind!=VmdExternalOutputKind.HumanoidIk)
                rig=OfflineRigFactory.FromFbx(options.FbxPath,new FbxImportOptions {MetaPath=options.MetaPath,BoneMapPath=options.BoneMapPath},options.ProfilePath);
            else if(legacy!=null&&options.OutputKind!=VmdExternalOutputKind.HumanoidIk)
                rig=FromLegacy(legacy);
            else rig=PmxRigBuilder.Build(model,options.PositionScale,options.CoordinateConversion,options.ProfilePath);
            if(rig.bindBones.Length==0)rig.bindBones=rig.bones.Select(b=>new Bone{name=b.name,parent=b.parent,humanBone=b.humanBone,position=b.position,rotation=b.rotation}).ToArray();
            bind=new SkeletonPose(new Rig {bones=rig.bindBones});solver=new HumanoidSolver(rig);
            target=rig.bones.Select((b,i)=>(b,i)).Where(p=>p.b.humanBone>=0).ToDictionary(p=>p.b.humanBone,p=>p.i);
            if(!source.ContainsKey(0))throw new InvalidDataException("PMX Hips mapping is missing.");
            float sourceHeight=Math.Abs(model.Bones[source[0]].Position.y*options.PositionScale);
            targetScale=canonical?1:sourceHeight>1e-5?Math.Abs(bind.WorldPositions[rig.hips].Y)/sourceHeight:1;
            AnimationRig=new VmdTargetRigData {sampleRate=options.SampleRate,positionScale=options.PositionScale,outputKind=options.OutputKind,bakeMorphs=options.BakeMorphs,
                humanoid=new VmdHumanoidCalibrationData{muscles=rig.muscles.Select(m=>new VmdMuscleCalibration {property=MuscleProperty(m.name)}).ToArray()},
                morphs=rig.morphs.Select(m=>new VmdTargetMorphBinding{name=m.name,path=m.path}).ToArray()};
            // Standard Humanoid uses the PMX source rig, but morph paths still
            // belong to the optional target FBX.
            if(options.FbxPath!=null&&canonical)
                AnimationRig.morphs=FbxReader.Read(options.FbxPath,new FbxImportOptions{MetaPath=options.MetaPath,BoneMapPath=options.BoneMapPath}).morphs
                    .GroupBy(m=>(m.name,m.path)).Select(g=>new VmdTargetMorphBinding{name=g.Key.name,path=g.Key.path}).ToArray();
            RigIdentity=SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rig,HpsJsonContext.Default.Rig)));
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
            Rig rig=OfflineRigFactory.FromSkeleton(new FbxDocument{source="legacy VMDCC bindings",bones=bones.ToArray(),morphs=legacy.morphs.Select(m=>new MorphBinding{name=m.name,path=m.path}).ToArray()});
            return rig;
        }

        public VmdTargetCurveData Solve(VmdMotion motion,PmxPose[] samples,int threads,TaskScheduler? scheduler=null)
        {
            if(legacyFkJson!=null)
            {
                // Legacy bindings preserve actual helper-parent axes and full
                // Transform paths. Reuse their pure managed FK solver instead
                // of losing those paths in a reduced Humanoid skeleton.
                var copy=JsonSerializer.Deserialize<VmdTargetRigData>(legacyFkJson,legacyJson)!;
                copy.sampleRate=options.SampleRate;copy.positionScale=options.PositionScale;
                copy.coordinateConversion=options.CoordinateConversion;copy.bakeMorphs=options.BakeMorphs;
                copy.rotationErrorDegrees=options.RotationErrorDegrees;copy.positionError=options.PositionError;
                return VmdTargetFkCurveSolver.Solve(model,motion,samples,copy,threads);
            }
            bool suppressLeft=!canonical&&VmdTargetFkCurveSolver.ShouldSuppressWaistCancellation(model,motion,true);
            bool suppressRight=!canonical&&VmdTargetFkCurveSolver.ShouldSuppressWaistCancellation(model,motion,false);
            var frames=new PoseFrame[samples.Length];
            Parallel.For(0,frames.Length,new ParallelOptions{MaxDegreeOfParallelism=threads,TaskScheduler=scheduler??TaskScheduler.Default},frame=>
            {
                var pose=new SkeletonPose(new Rig{bones=rig.bindBones});PmxPose sample=samples[frame];
                var worlds=new Q[rig.bones.Length];
                for(int b=0;b<rig.bones.Length;b++)
                {
                    int parent=rig.bones[b].parent,h=rig.bones[b].humanBone;
                    if(h>=0&&source.TryGetValue(h,out int index))
                    {
                        Q sourceWorld=PmxRigBuilder.Rotation(sample.WorldRotations[index],options.CoordinateConversion);
                        if((h==1&&suppressLeft||h==2&&suppressRight)&&parent>=0)
                        {
                            int ph=rig.bones[parent].humanBone;
                            Q parentSource=source.TryGetValue(ph,out int pi)?PmxRigBuilder.Rotation(sample.WorldRotations[pi],options.CoordinateConversion):Q.Identity;
                            sourceWorld=parentSource*PmxRigBuilder.Rotation(sample.LocalRotations[index],options.CoordinateConversion);
                        }
                        worlds[b]=canonical?sourceWorld:Q.Normalize(sourceWorld*bind.WorldRotations[b]);
                        pose.LocalRotations[b]=parent<0?worlds[b]:Q.Normalize(Q.Inverse(worlds[parent])*worlds[b]);
                    }
                    else worlds[b]=parent<0?pose.LocalRotations[b]:Q.Normalize(worlds[parent]*pose.LocalRotations[b]);
                }
                int hipsSource=source[0];
                V displacement=PmxRigBuilder.Position(sample.WorldPositions[hipsSource]-model.Bones[hipsSource].Position,options.CoordinateConversion)*(options.PositionScale*targetScale);
                int hp=rig.bones[rig.hips].parent;
                pose.LocalPositions[rig.hips]+=hp<0?displacement:Math3.Rotate(Q.Inverse(bind.WorldRotations[hp]),displacement);
                pose.UpdateWorld();
                bool legs=options.TargetLegIk??options.OutputKind==VmdExternalOutputKind.TargetCalibratedHumanoid;
                if(legs&&!canonical)
                    foreach(bool left in new[]{true,false})
                    {
                        int thigh=left?1:2,knee=left?3:4,foot=left?5:6;
                        if(!target.ContainsKey(thigh)||!target.ContainsKey(knee)||!target.ContainsKey(foot)||!source.ContainsKey(foot))continue;
                        int f=target[foot],si=source[foot];
                        V p=bind.WorldPositions[f]+PmxRigBuilder.Position(sample.WorldPositions[si]-model.Bones[si].Position,options.CoordinateConversion)*(options.PositionScale*targetScale);
                        V pole=pose.WorldPositions[target[knee]]+Math3.Rotate(pose.WorldRotations[target[thigh]],new V(0,0,0.1f));
                        TwoBoneIk.Solve(pose,new IkGoal{root=target[thigh],middle=target[knee],tip=f,target=Math3.A(p),pole=Math3.A(pole),rotation=Math3.A(pose.WorldRotations[f])});
                    }
                frames[frame]=pose.ToFrame(frame==frames.Length-1?motion.MaximumFrame/30f:frame/options.SampleRate);
            });
            var result=new VmdTargetCurveData {SampleCount=frames.Length,SampleRate=options.SampleRate,BoundBoneCount=target.Count};
            float[] times=frames.Select(f=>f.time).ToArray();int[] all=Enumerable.Range(0,frames.Length).ToArray();
            void Curve(string path,string property,byte kind,float[] values,bool preserve=false)
            {
                int[] keys=preserve||options.CurveError<=0?all:VmdTargetFkCurveSolver.Reduce(times,values.Length,(a,b,i)=>{
                    float t=(times[i]-times[a])/(times[b]-times[a]);return Math.Abs(values[i]-(values[a]+(values[b]-values[a])*t));},options.CurveError);
                result.Curves.Add(VmdTargetFkCurveSolver.BuildCurve(path,property,kind,times,keys,i=>values[i]));
            }
            if(options.OutputKind==VmdExternalOutputKind.TargetFk)
            {
                foreach(var pair in target)
                {
                    int b=pair.Value;if(!source.ContainsKey(pair.Key))continue;
                    string path=BonePath(b);
                    // Share times across all quaternion components for YAML vector serialization.
                    var rotations=frames.Select(f=>Math3.Q(f.rotations,b*4)).ToArray();
                    for(int i=1;i<rotations.Length;i++)if(Q.Dot(rotations[i-1],rotations[i])<0)rotations[i]=-rotations[i];
                    var unityRotations=rotations.Select(q=>new UnityEngine.Quaternion(q.X,q.Y,q.Z,q.W)).ToArray();
                    int[] keys=options.RotationErrorDegrees<=0?all:VmdTargetFkCurveSolver.ReduceQuaternion(times,unityRotations,options.RotationErrorDegrees);
                    for(int c=0;c<4;c++){int axis=c;result.Curves.Add(VmdTargetFkCurveSolver.BuildCurve(path,"m_LocalRotation."+"xyzw"[c],0,times,keys,i=>axis==0?rotations[i].X:axis==1?rotations[i].Y:axis==2?rotations[i].Z:rotations[i].W));}
                    if(pair.Key==0)
                    {
                        var positions=frames.Select(f=>new UnityEngine.Vector3(f.positions[b*3],f.positions[b*3+1],f.positions[b*3+2])).ToArray();
                        int[] positionKeys=options.PositionError<=0?all:VmdTargetFkCurveSolver.ReduceVector(times,positions,options.PositionError);
                        for(int c=0;c<3;c++){int axis=c;result.Curves.Add(VmdTargetFkCurveSolver.BuildCurve(path,"m_LocalPosition."+"xyz"[c],0,times,positionKeys,i=>axis==0?positions[i].x:axis==1?positions[i].y:positions[i].z));}
                    }
                }
            }
            else
            {
                ResultBatch solved=solver.SolveBatch(new PoseBatch{frames=frames},threads,options.Passes,scheduler);
                // q and -q encode the same orientation; choose a continuous
                // hemisphere before scalar .anim interpolation.
                for(int i=1;i<solved.frames.Length;i++)
                    if(Q.Dot(Math3.Q(solved.frames[i-1].humanPose.bodyRotation),Math3.Q(solved.frames[i].humanPose.bodyRotation))<0)
                        for(int c=0;c<4;c++)solved.frames[i].humanPose.bodyRotation[c]*=-1;
                for(int c=0;c<3;c++){int axis=c;Curve("","RootT."+"xyz"[c],2,solved.frames.Select(f=>f.humanPose.bodyPosition[axis]).ToArray(),true);}
                for(int c=0;c<4;c++){int axis=c;Curve("","RootQ."+"xyzw"[c],2,solved.frames.Select(f=>f.humanPose.bodyRotation[axis]).ToArray(),true);}
                for(int m=0;m<rig.muscles.Length;m++)
                {
                    int muscle=m;float[] values=solved.frames.Select(f=>f.humanPose.muscles[muscle]).ToArray();
                    if(values.All(v=>Math.Abs(v)<1e-7))continue;
                    Curve("",MuscleProperty(rig.muscles[m].name),2,values);
                }
                var worst=solved.frames.MaxBy(f=>f.maxFitErrorDegrees)!;
                var decoded=solver.Decode(worst.humanPose.muscles);
                int worstBone=Enumerable.Range(0,rig.bones.Length).Where(b=>b!=rig.hips&&rig.bones[b].humanBone>=0).MaxBy(b=>Math3.Angle(decoded.LocalRotations[b],Math3.Q(worst.rotations,b*4)));
                Console.Error.WriteLine($"HPS: max local fit error {worst.maxFitErrorDegrees:G6} deg ({rig.bones[worstBone].name}, t={worst.time:G6}); profile={Precision}");
            }
            if(options.BakeMorphs)VmdTargetFkCurveSolver.AddMorphCurves(motion,AnimationRig,result);
            return result;
        }
        private string BonePath(int b)
        {
            var names=new List<string>();for(int i=b;i>=0&&rig.bones[i].parent>=0;i=rig.bones[i].parent)names.Add(rig.bones[i].name);
            names.Reverse();return string.Join('/',names);
        }
        private static string MuscleProperty(string name)
        {
            foreach(string side in new[]{"Left","Right"})foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
                if(name.StartsWith(side+" "+finger+" ",StringComparison.Ordinal))return side+"Hand."+finger+"."+name[(side.Length+finger.Length+2)..];
            return name;
        }
    }
}
