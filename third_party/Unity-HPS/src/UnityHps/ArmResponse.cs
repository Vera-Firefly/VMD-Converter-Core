using System.Numerics;

namespace UnityHps;

// Recover a swing followed by distributed axial twist only from charts that
// satisfy that model at every supplied node. Measured alternatives stay intact.
internal static class ArmResponse
{
    internal static Quaternion[] Evaluate(Rig rig,MuscleGroup group,float[] values)
    {
        int count=group.bones.Length;
        var center=group.samples[(group.samples.Length-1)/2];
        var output=new Quaternion[count];
        for(int b=0;b<count;b++)
        {
            double x=0,y=0,z=0;Quaternion twist=Quaternion.Identity;
            for(int d=0;d<values.Length;d++)
            {
                int slot=d*count+b;var muscle=rig.muscles[group.muscles[d]];
                double radians=(double)values[d]*group.armResponseRadians[slot]*
                    (values[d]<0?-muscle.minimum/muscle.maximum:1);
                var axis=Math3.V(group.armResponseAxes,slot*3);
                if(muscle.dof==0)
                {
                    double sine=Math.Sin(radians/2);
                    twist=new Quaternion((float)(axis.X*sine),(float)(axis.Y*sine),(float)(axis.Z*sine),(float)Math.Cos(radians/2));
                }
                else
                {
                    double tangent=Math.Tan(radians/2);
                    x+=axis.X*tangent;y+=axis.Y*tangent;z+=axis.Z*tangent;
                }
            }
            double norm=Math.Sqrt(1+x*x+y*y+z*z);
            var swing=new Quaternion((float)(x/norm),(float)(y/norm),(float)(z/norm),(float)(1/norm));
            output[b]=Quaternion.Normalize(Math3.Q(center.rotations,b*4)*swing*twist);
        }
        return output;
    }

    internal static void Configure(Rig rig,bool legs=false)
    {
        foreach(var group in rig.groups)
        {
            int dimensions=group.muscles.Length,count=group.bones.Length;
            int owner=rig.muscles[group.muscles[0]].humanBone;
            if((legs ? owner is <1 or >6 : owner is <13 or >18) || dimensions is <2 or >3 ||
                group.muscles.Any(m=>rig.muscles[m].humanBone!=owner || rig.muscles[m].dof is <0 or >2 ||
                    !float.IsFinite(rig.muscles[m].minimum) || !float.IsFinite(rig.muscles[m].maximum) ||
                    rig.muscles[m].minimum>=0 || rig.muscles[m].maximum<=0) ||
                group.muscles.Select(m=>rig.muscles[m].dof).Distinct().Count()!=dimensions)continue;
            int center=(group.samples.Length-1)/2;
            var axes=new float[dimensions*count*3];var radians=new float[dimensions*count];
            for(int d=0,stride=1;d<dimensions;d++,stride*=group.resolution)
            for(int b=0;b<count;b++)
            {
                var neutral=Math3.Q(group.samples[center].rotations,b*4);
                var delta=Math3.Log(Quaternion.Inverse(neutral)*Math3.Q(group.samples[center+stride*(group.resolution/2)].rotations,b*4));
                float length=delta.Length(),range=group.muscleRanges.Length==0?1:group.muscleRanges[d];
                // Ignore floating-point residue on bones unaffected by a channel.
                if(length<1e-5f)continue;
                radians[d*count+b]=length/range;
                Math3.Put(axes,(d*count+b)*3,delta/length);
            }
            group.armResponseAxes=axes;group.armResponseRadians=radians;
            bool valid=true;
            for(int s=0;s<group.samples.Length && valid;s++)
            {
                var values=new float[dimensions];
                for(int d=0,stride=1;d<dimensions;d++,stride*=group.resolution)
                    values[d]=(s/stride%group.resolution*2f/(group.resolution-1)-1)*(group.muscleRanges.Length==0?1:group.muscleRanges[d]);
                var expected=Evaluate(rig,group,values);
                for(int b=0;b<count;b++)valid &= Math3.Angle(expected[b],Math3.Q(group.samples[s].rotations,b*4))<.01f &&
                    Vector3.Distance(Math3.V(group.samples[s].positions,b*3),Math3.V(group.samples[center].positions,b*3))<1e-6f;
            }
            if(!valid){group.armResponseAxes=[];group.armResponseRadians=[];}
            else
            {
                // Distributed twist rotates a segment and its child about one
                // world axis. Re-express that axis through the child's actual
                // neutral transform, including target-only helper rotations.
                int twist=Array.FindIndex(group.muscles,m=>rig.muscles[m].dof==0);
                int ownerSlot=Array.FindIndex(group.bones,b=>rig.bones[b].humanBone==owner);
                if(twist>=0 && ownerSlot>=0 && count>1)
                {
                    var neutral=new SkeletonPose(rig);
                    Vector3 axis=Math3.Rotate(neutral.WorldRotations[group.bones[ownerSlot]],Math3.V(axes,(twist*count+ownerSlot)*3));
                    for(int b=0;b<count;b++)if(radians[twist*count+b]>0)
                        Math3.Put(axes,(twist*count+b)*3,Math3.Rotate(Quaternion.Inverse(neutral.WorldRotations[group.bones[b]]),axis));
                    for(int s=0;s<group.samples.Length;s++)
                    {
                        var values=new float[dimensions];
                        for(int d=0,stride=1;d<dimensions;d++,stride*=group.resolution)
                            values[d]=(s/stride%group.resolution*2f/(group.resolution-1)-1)*(group.muscleRanges.Length==0?1:group.muscleRanges[d]);
                        var rotations=Evaluate(rig,group,values);
                        for(int b=0;b<count;b++)Math3.Put(group.samples[s].rotations,b*4,rotations[b]);
                    }
                }
            }
        }
    }
}
