using System.Numerics;
using UnityHps;

namespace VeraFirefly.VmdConverter.Core.Hps;

// Source pose encoding only. This does not reconstruct a target Avatar limb.
internal sealed class SourceLegEncoder
{
    readonly Rig rig;
    readonly HumanoidSolver solver;
    readonly (MuscleGroup group,int slot,int bone)[] joints;
    internal static Rig CreateRig(Rig source,string? profile)
    {
        var anatomical=OfflineRigFactory.ForHumanoidLegEncoding(OfflineRigFactory.FromSkeleton(
            new FbxDocument{bones=source.bindBones,source=source.source},profile,HumanoidResponseTransport.AnatomicalTargets));
        var bones=source.bones.Select((b,i)=>b.humanBone is >=1 and <=6?anatomical.bones[i]:b).ToArray();
        var groups=source.groups.Select(g=>source.muscles[g.muscles[0]].humanBone is >=1 and <=6
            ?anatomical.groups.Single(a=>a.muscles.SequenceEqual(g.muscles)):g).ToArray();
        var result=new Rig{version=source.version,source=source.source,humanScale=source.humanScale,hips=source.hips,
            bones=bones,bindBones=source.bindBones,morphs=source.morphs,precision=source.precision,muscles=source.muscles,
            groups=groups,bodyRotation=source.bodyRotation,bodyPositionJacobian=source.bodyPositionJacobian,
            bodyPositionWeights=source.bodyPositionWeights,bodyOrientationMode=source.bodyOrientationMode,
            reverseCompositionBones=source.reverseCompositionBones};
        var neutral=new SkeletonPose(result);Vector3 center=Vector3.Zero;
        for(int b=0;b<bones.Length;b++)center+=neutral.WorldPositions[b]*result.bodyPositionWeights[b];
        result.bodyPosition=Math3.A(center/result.humanScale);
        return result;
    }
    internal SourceLegEncoder(Rig value,HumanoidRangePolicy policy)
    {
        rig=value;
        joints=Enumerable.Range(1,6).Select(h=>{
            var group=rig.groups.Single(g=>rig.muscles[g.muscles[0]].humanBone==h);
            int slot=Array.FindIndex(group.bones,b=>rig.bones[b].humanBone==h);
            if(group.armResponseAxes.Length==0||slot<0)throw new InvalidDataException("Source leg response is not analytic.");
            return(group,slot,group.bones[slot]);
        }).ToArray();
        solver=new HumanoidSolver(rig,new HashSet<int>{1,2,3,4,5,6},policy)
            {FitLegGeometry=true,AnchorLimbTwistToInitial=true};
    }
    internal float[] Project(PoseFrame frame,float[] initial,int passes)
    {
        var seed=(float[])initial.Clone();
        foreach(var (group,slot,bone) in joints)
        {
            int n=group.bones.Length;Vector3 Axis(int d)=>Math3.V(group.armResponseAxes,(d*n+slot)*3);
            int twist=Array.FindIndex(group.muscles,m=>rig.muscles[m].dof==0);
            int[] swing=Enumerable.Range(0,group.muscles.Length).Where(d=>rig.muscles[group.muscles[d]].dof!=0).ToArray();
            Vector3 axis=twist>=0?Axis(twist):Vector3.Normalize(Vector3.Cross(Axis(swing[0]),Axis(swing[1])));
            Quaternion q=Quaternion.Normalize(Quaternion.Inverse(Math3.Q(rig.bones[bone].rotation))*Math3.Q(frame.rotations,bone*4));
            var axial=new Quaternion(axis*Vector3.Dot(new Vector3(q.X,q.Y,q.Z),axis),q.W);
            axial=axial.LengthSquared()>1e-12f?Quaternion.Normalize(axial):Quaternion.Identity;
            Quaternion sw=Quaternion.Normalize(q*Quaternion.Inverse(axial));Vector3 v=new(sw.X,sw.Y,sw.Z);
            float[] coefficients=swing.Select(d=>Vector3.Dot(v,Axis(d))).ToArray();
            if(swing.Length==2){float dot=Vector3.Dot(Axis(swing[0]),Axis(swing[1]));float a=coefficients[0],b=coefficients[1],den=1-dot*dot;coefficients[0]=(a-dot*b)/den;coefficients[1]=(b-dot*a)/den;}
            void Set(int d,double angle){var m=rig.muscles[group.muscles[d]];seed[group.muscles[d]]=(float)(angle*180/Math.PI/(angle<0?-m.minimum:m.maximum));}
            for(int i=0;i<swing.Length;i++)Set(swing[i],2*Math.Atan2(coefficients[i],sw.W));
            if(twist>=0){double angle=2*Math.Atan2(Vector3.Dot(new Vector3(axial.X,axial.Y,axial.Z),axis),axial.W);angle-=2*Math.PI*Math.Floor((angle+Math.PI)/(2*Math.PI));Set(twist,angle);}
        }
        seed=solver.CreateFitSeed(seed);
        return solver.Solve(frame,passes,seed).humanPose.muscles;
    }
}
