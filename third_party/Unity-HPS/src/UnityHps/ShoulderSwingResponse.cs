using System.Numerics;

namespace UnityHps;

// A shoulder has two coupled swing coordinates. Independent tangent
// extrapolation misses their coupling outside a measured lattice.
internal static class ShoulderSwingResponse
{
    internal static Quaternion Evaluate(Quaternion neutral, ReadOnlySpan<float> axes,
        double firstDegrees, double secondDegrees)
    {
        double a=Math.Tan(firstDegrees*Math.PI/360),b=Math.Tan(secondDegrees*Math.PI/360);
        double x=a*axes[0]+b*axes[3],y=a*axes[1]+b*axes[4],z=a*axes[2]+b*axes[5];
        double norm=Math.Sqrt(x*x+y*y+z*z+1);
        return Quaternion.Normalize(neutral*new Quaternion((float)(x/norm),(float)(y/norm),(float)(z/norm),(float)(1/norm)));
    }

    internal static void Configure(Rig rig,FbxMuscleLimit[] limits)
    {
        // Limits belong to this target; never mutate the shared source profile.
        rig.muscles=rig.muscles.Select(m=>new Muscle{name=m.name,unityIndex=m.unityIndex,humanBone=m.humanBone,
            dof=m.dof,minimum=m.minimum,maximum=m.maximum}).ToArray();
        foreach(var group in rig.groups)
        {
            if(group.muscles.Length!=2 || group.bones.Length!=1)continue;
            int human=rig.bones[group.bones[0]].humanBone;
            if(human is not (11 or 12) || group.muscles.Any(m=>rig.muscles[m].humanBone!=human || rig.muscles[m].dof is not (1 or 2)))continue;
            var muscles=group.muscles.Select(m=>rig.muscles[m]).ToArray();
            if(muscles.Select(m=>m.dof).Distinct().Count()!=2 || muscles.Any(m=>m.minimum>=0 || m.maximum<=0))continue;
            int center=(group.samples.Length-1)/2;var neutral=Math3.Q(group.samples[center].rotations);
            var axes=new float[6];bool valid=true;
            for(int d=0;d<2;d++)
            {
                int stride=d==0?1:group.resolution;
                var delta=Math3.Log(Quaternion.Inverse(neutral)*Math3.Q(group.samples[center+stride*(group.resolution/2)].rotations));
                float range=group.muscleRanges.Length==0?1:group.muscleRanges[d];
                valid &= delta.Length()>1e-5f && Math.Abs(delta.Length()*Math3.Degrees-muscles[d].maximum*range)<.01f;
                if(!valid)break;
                Math3.Put(axes,d*3,Vector3.Normalize(delta));
            }
            if(!valid || Math.Abs(Vector3.Dot(Math3.V(axes),Math3.V(axes,3)))>.0001f)continue;
            // Opt in only when the complete supplied chart obeys this model.
            // Non-swing/custom calibrated charts keep their original lattice.
            for(int s=0;s<group.samples.Length && valid;s++)
            {
                double Degree(int d)
                {
                    int node=d==0?s%group.resolution:s/group.resolution;
                    double value=(node*2.0/(group.resolution-1)-1)*(group.muscleRanges.Length==0?1:group.muscleRanges[d]);
                    return value*(value<0?-muscles[d].minimum:muscles[d].maximum);
                }
                valid=Math3.Angle(Evaluate(neutral,axes,Degree(0),Degree(1)),Math3.Q(group.samples[s].rotations))<.01f &&
                    Vector3.Distance(Math3.V(group.samples[s].positions),Math3.V(group.samples[center].positions))<1e-6f;
            }
            if(!valid)continue;
            group.shoulderSwingAxes=axes;
            var matches=limits.Where(l=>l.humanBone==human).ToArray();
            if(matches.Length!=1)continue;
            var limit=matches[0];
            if(limit.minimum.Length!=3 || limit.maximum.Length!=3 || limit.center.Length!=3 ||
                limit.center.Any(v=>!float.IsFinite(v)||Math.Abs(v)>1e-6f) ||
                muscles.Any(m=>!float.IsFinite(limit.minimum[m.dof])||!float.IsFinite(limit.maximum[m.dof])||
                    limit.minimum[m.dof]>=0||limit.maximum[m.dof]<=0||limit.minimum[m.dof]<-180||limit.maximum[m.dof]>180))continue;
            foreach(var muscle in muscles){muscle.minimum=limit.minimum[muscle.dof];muscle.maximum=limit.maximum[muscle.dof];}
            // Keep stored lattice nodes consistent with their declared limits,
            // including consumers that use the chart instead of the analytic path.
            for(int s=0;s<group.samples.Length;s++)
            {
                double Degree(int d)
                {
                    int node=d==0?s%group.resolution:s/group.resolution;
                    double value=(node*2.0/(group.resolution-1)-1)*(group.muscleRanges.Length==0?1:group.muscleRanges[d]);
                    return value*(value<0?-muscles[d].minimum:muscles[d].maximum);
                }
                group.samples[s].rotations=Math3.A(Evaluate(neutral,axes,Degree(0),Degree(1)));
            }
        }
    }
}
