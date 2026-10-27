using System.Numerics;
using UnityHps;

internal static class RigTransportTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig profile=OfflineRigFactory.LoadProfile(false);
        FbxDocument Document(bool toes, bool lean=false) => new()
        {
            bones=profile.bindBones.Select(b=>new Bone{name=b.name,parent=b.parent,
                humanBone=!toes && b.humanBone is 19 or 20 ? -1 : b.humanBone,
                position=b.humanBone==7 && lean ? Math3.A(Math3.V(b.position)+new Vector3(0,0,.08f)) : (float[])b.position.Clone(),
                rotation=(float[])b.rotation.Clone()}).ToArray()
        };
        var withToes=OfflineRigFactory.Transfer(Document(true),profile,HumanoidResponseTransport.AnatomicalLegs);
        var withoutToes=OfflineRigFactory.Transfer(Document(false),profile,HumanoidResponseTransport.AnatomicalLegs);
        var legacy=OfflineRigFactory.Transfer(Document(true),profile);
        var explicitLegacy=OfflineRigFactory.Transfer(Document(true),profile,HumanoidResponseTransport.JointFrames);
        check(legacy.groups.Zip(explicitLegacy.groups).All(pair=>pair.First.samples.Zip(pair.Second.samples)
            .All(s=>s.First.rotations.SequenceEqual(s.Second.rotations))),
            "source joint-frame transport retains the existing default and explicit API behavior");
        int Find(Rig rig,int human)=>Array.FindIndex(rig.bones,b=>b.humanBone==human);
        var a=new HumanoidSolver(withToes);var b=new HumanoidSolver(withoutToes);
        float error=0,legacyDifference=0;
        var old=new HumanoidSolver(OfflineRigFactory.Transfer(Document(false),profile));
        foreach(int m in Enumerable.Range(0,profile.muscles.Length).Where(m=>profile.muscles[m].humanBone is >=1 and <=6))
            foreach(float value in new[]{-.8f,.5f,.9f})
            {
                var muscles=new float[profile.muscles.Length];muscles[m]=value;
                var pa=a.Decode(muscles);var pb=b.Decode(muscles);var po=old.Decode(muscles);
                foreach(int human in new[]{5,6})
                {
                    error=Math.Max(error,Math3.Angle(pa.LocalRotations[Find(withToes,human)],pb.LocalRotations[Find(withoutToes,human)]));
                    legacyDifference=Math.Max(legacyDifference,Math3.Angle(pb.LocalRotations[Find(withoutToes,human)],po.LocalRotations[Find(withoutToes,human)]));
                }
            }
        check(error<.002f && legacyDifference>10,
            "target ankle responses do not change when optional Toes mappings are absent");

        var neutral=b.Decode(new float[profile.muscles.Length]);
        foreach(int human in new[]{3,4})
        {
            int muscle=Array.FindIndex(profile.muscles,m=>m.humanBone==human && m.dof==0);
            var values=new float[profile.muscles.Length];values[muscle]=.5f;var pose=b.Decode(values);
            Vector3 Axis(int h)
            {
                int bone=Find(withoutToes,h);
                return Vector3.Normalize(Math3.Rotate(neutral.WorldRotations[bone],
                    Math3.Log(Quaternion.Inverse(neutral.LocalRotations[bone])*pose.LocalRotations[bone])));
            }
            check(Vector3.Dot(Axis(human),Axis(human+2))>.99999f,
                $"lower-leg twist and its ankle response share the owning segment axis: human={human}");
        }
        var leaning=OfflineRigFactory.Transfer(Document(true,true),profile,HumanoidResponseTransport.AnatomicalLegs);
        var leanSolver=new HumanoidSolver(leaning);bool stable=true;
        foreach(int m in Enumerable.Range(0,profile.muscles.Length).Where(m=>profile.muscles[m].humanBone is 5 or 6))
        {
            var values=new float[profile.muscles.Length];values[m]=.7f;
            var pa=a.Decode(values);var pl=leanSolver.Decode(values);
            foreach(int h in new[]{5,6})stable &= Math3.Angle(pa.LocalRotations[Find(withToes,h)],pl.LocalRotations[Find(leaning,h)])<.002f;
        }
        check(stable,"target ankle pitch/roll follows horizontal Avatar heading instead of torso lean");
    }
}
