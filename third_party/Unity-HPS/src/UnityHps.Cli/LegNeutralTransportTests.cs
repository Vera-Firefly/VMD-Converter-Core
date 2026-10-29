using System.Numerics;
using UnityHps;

internal static class LegNeutralTransportTests
{
    public static void Run(Action<bool,string> check)
    {
        Rig profile=OfflineRigFactory.LoadProfile(false);
        FbxDocument Document(float yaw)=>new(){bones=profile.bindBones.Select(b=>new Bone{
            name=b.name,parent=b.parent,humanBone=b.humanBone,
            position=b.humanBone is 5 or 6 ? Math3.A(Math3.V(b.position)+new Vector3(b.humanBone==5?.012f:-.012f,0,-.04f)) : (float[])b.position.Clone(),
            rotation=b.parent<0 ? Math3.A(Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw)*Math3.Q(b.rotation)) : (float[])b.rotation.Clone()
        }).ToArray()};
        int Find(Rig r,int h)=>Array.FindIndex(r.bones,b=>b.humanBone==h);
        var sourceBind=new SkeletonPose(new Rig{bones=profile.bindBones});
        bool headingNeutral=true,zeroCenters=true,legacyDistinct=false;
        foreach(float yaw in new[]{0f,.7f})
        {
            var doc=Document(yaw);
            var rig=OfflineRigFactory.Transfer(doc,profile,HumanoidResponseTransport.AnatomicalTargets);
            var previous=OfflineRigFactory.Transfer(doc,profile,HumanoidResponseTransport.AnatomicalLegs);
            var bind=new SkeletonPose(new Rig{bones=rig.bindBones});
            var zero=new HumanoidSolver(rig).Decode(new float[rig.muscles.Length]);
            var heading=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
            foreach(int h in new[]{1,2,3,4})
            {
                int s=Find(profile,h),t=Find(rig,h),sp=profile.bones[s].parent,tp=rig.bones[t].parent;
                Quaternion sourceDelta=sourceBind.WorldRotations[sp]*Math3.Q(profile.bones[s].rotation)*Quaternion.Inverse(sourceBind.WorldRotations[s]);
                Quaternion targetDelta=bind.WorldRotations[tp]*zero.LocalRotations[t]*Quaternion.Inverse(bind.WorldRotations[t]);
                headingNeutral &= Math3.Angle(targetDelta,heading*sourceDelta*Quaternion.Inverse(heading))<.002f;
                legacyDistinct |= Math3.Angle(zero.LocalRotations[t],Math3.Q(previous.bones[t].rotation))>.1f;
            }
            zeroCenters &= Enumerable.Range(0,rig.bones.Length).All(i=>Math3.Angle(zero.LocalRotations[i],Math3.Q(rig.bones[i].rotation))<.002f);
            var values=new float[rig.muscles.Length];values[25]=.5f;
            var moved=new HumanoidSolver(rig).Decode(values);
            int knee=Find(rig,3),ankle=Find(rig,5);
            Vector3 Axis(int b)=>Vector3.Normalize(Math3.Rotate(zero.WorldRotations[b],Math3.Log(Quaternion.Inverse(zero.LocalRotations[b])*moved.LocalRotations[b])));
            check(Vector3.Dot(Axis(knee),Axis(ankle))>.99999f,
                $"target leg neutral correction retains coupled knee/ankle twist at heading {yaw}");
        }
        check(headingNeutral && legacyDistinct,"lateral leg bind offsets do not tilt neutral flexion, including a rotated Avatar heading");
        check(zeroCenters,"target leg response centers retain the corrected neutral pose across shared groups");
    }
}
