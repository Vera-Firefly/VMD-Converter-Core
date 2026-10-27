using System.Numerics;
using UnityHps;

internal static class TorsoTransportTests
{
    public static void Run(Action<bool,string> check)
    {
        Rig profile=OfflineRigFactory.LoadProfile(false);
        var document=new FbxDocument{bones=profile.bindBones.Select(b=>new Bone{name=b.name,parent=b.parent,humanBone=b.humanBone,
            position=b.humanBone==9 ? Math3.A(Math3.V(b.position)+new Vector3(0,0,-.04f)) : (float[])b.position.Clone(),rotation=(float[])b.rotation.Clone()}).ToArray()};
        Rig rig=OfflineRigFactory.Transfer(document,profile,HumanoidResponseTransport.AnatomicalTargets);
        int Find(Rig r,int h)=>Array.FindIndex(r.bones,b=>b.humanBone==h);
        var solver=new HumanoidSolver(rig);var neutral=solver.Decode(new float[rig.muscles.Length]);
        var muscles=new float[rig.muscles.Length];muscles[5]=.5f;var pose=solver.Decode(muscles);int chest=Find(rig,8),neck=Find(rig,9);
        Vector3 axis=Vector3.Normalize(Math3.Rotate(neutral.WorldRotations[chest],Math3.Log(Quaternion.Inverse(neutral.LocalRotations[chest])*pose.LocalRotations[chest])));
        var bind=new SkeletonPose(new Rig{bones=rig.bindBones});var direction=Vector3.Normalize(bind.WorldPositions[neck]-bind.WorldPositions[chest]);
        check(Vector3.Dot(axis,direction)>.99999f,"Chest twist follows the next mapped torso link when UpperChest is absent");
        check(!rig.bones.Any(b=>b.humanBone==54) && rig.bones.Length==OfflineRigFactory.Transfer(document,profile,HumanoidResponseTransport.AnatomicalLegs).bones.Length &&
            rig.groups.Count(g=>g.muscles.Any(m=>rig.muscles[m].unityIndex==6))==1,
            "virtual UpperChest channels retain a single response without inventing a target Transform");
        Array.Clear(muscles);muscles[6]=.5f;pose=solver.Decode(muscles);
        check(Math3.Angle(neutral.LocalRotations[chest],pose.LocalRotations[chest])>9.9f,
            "unmapped UpperChest muscle channels still rotate the mapped Chest");
        Rig full=OfflineRigFactory.LoadProfile(true);
        var mapped=OfflineRigFactory.Transfer(new FbxDocument{bones=full.bindBones},full,HumanoidResponseTransport.AnatomicalTargets);
        check(mapped.groups.Count(g=>g.muscles.Any(m=>mapped.muscles[m].unityIndex==6))==1,
            "mapped UpperChest does not receive an extra virtual response");
        var active=new HashSet<int>{7,8,54};var fitter=new HumanoidSolver(rig,active){ReconstructDecodedRoot=true};
        var frames=Enumerable.Range(0,9).Select(i=>{
            var values=new float[rig.muscles.Length];values[0]=.05f*i;values[3]=-.025f*i;values[4]=.02f*i;values[6]=.01f*i;values[8]=-.03f*i;
            return solver.Decode(values).ToFrame(i/30f);
        }).ToArray();
        var parallel=fitter.SolveBatch(new PoseBatch{frames=frames},4,3);var serial=fitter.SolveBatch(new PoseBatch{frames=frames},1,3);
        check(parallel.frames.Select((f,i)=>Math3.Angle(solver.Decode(f.humanPose.muscles,f.humanPose).WorldRotations[chest],new SkeletonPose(rig,frames[i]).WorldRotations[chest])<.02f).All(x=>x) &&
            parallel.frames.Zip(serial.frames).All(p=>p.First.humanPose.muscles.SequenceEqual(p.Second.humanPose.muscles)),
            "shared Chest/virtual UpperChest fitting preserves the physical pose and worker determinism");
    }
}
