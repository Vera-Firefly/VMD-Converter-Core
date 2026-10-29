using UnityHps;

internal static class ShoulderSwingTests
{
    public static void Run(Action<bool,string> check)
    {
        var profile=OfflineRigFactory.LoadProfile(false);
        FbxDocument Document(float center=0)=>new(){bones=profile.bindBones,customMuscleLimits=[
            new(){humanBone=11,minimum=[0,-15,-5],maximum=[0,15,30],center=[0,center,0]},
            new(){humanBone=12,minimum=[0,-15,-5],maximum=[0,15,30],center=[0,center,0]}]};
        Rig Target(FbxDocument doc)=>OfflineRigFactory.Transfer(doc,profile,HumanoidResponseTransport.AnatomicalTargets);
        var target=Target(Document());
        var baseline=Target(new(){bones=profile.bindBones});
        var groups=target.groups.Where(g=>g.shoulderSwingAxes.Length>0).ToArray();
        check(groups.Length==2 && target.muscles[37].minimum==-5 && target.muscles[46].minimum==-5,
            "target shoulders apply asymmetric metadata limits and analytic swing");
        check(profile.muscles[37].minimum==-15 && baseline.muscles[37].minimum==-15 &&
            OfflineRigFactory.Transfer(Document(),profile).groups.All(g=>g.shoulderSwingAxes.Length==0) &&
            OfflineRigFactory.Transfer(Document(),profile,HumanoidResponseTransport.AnatomicalLegs).groups.All(g=>g.shoulderSwingAxes.Length==0),
            "shoulder target limits do not mutate the source profile or legacy transport");
        check(Target(Document(2)).muscles[37].minimum==-15,
            "nonzero custom shoulder centers retain the supported default response");
        var solver=new HumanoidSolver(target,rangePolicy:HumanoidRangePolicy.Tangent);
        var original=new HumanoidSolver(baseline,rangePolicy:HumanoidRangePolicy.Tangent);
        bool equivalent=true,lattice=true;
        foreach(float a in new[]{-4f,-2f,-.7f,0,.6f,2f,4f})
        foreach(float b in new[]{-4f,-1.7f,0,.8f,4f})
        {
            var input=new float[95];input[37]=input[46]=a;input[38]=input[47]=b;
            var expected=(float[])input.Clone();if(a<0)expected[37]=expected[46]=a/3;
            var pose=solver.Decode(input);var other=original.Decode(expected);
            foreach(var group in groups)equivalent &= Math3.Angle(pose.LocalRotations[group.bones[0]],other.LocalRotations[group.bones[0]])<.001f;
        }
        foreach(var group in groups)
        for(int s=0;s<group.samples.Length;s++)
        {
            var input=new float[95];
            for(int d=0;d<2;d++)input[group.muscles[d]]=((d==0?s%group.resolution:s/group.resolution)*2f/(group.resolution-1)-1)*(group.muscleRanges.Length==0?1:group.muscleRanges[d]);
            lattice &= Math3.Angle(solver.Decode(input).LocalRotations[group.bones[0]],Math3.Q(group.samples[s].rotations))<.001f;
        }
        check(equivalent,"asymmetric shoulder limits preserve equivalent physical rotations throughout extended two-channel motion");
        check(lattice,"serialized shoulder lattice agrees with declared custom limits");
        string path=Path.Combine(Path.GetTempPath(),"hps-shoulder-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            HpsJson.Write(path,target);var loaded=HpsJson.ReadRig(path);
            check(loaded.groups.Count(g=>g.shoulderSwingAxes.Length==6)==2 && loaded.muscles[46].minimum==-5,
                "rig JSON roundtrip retains analytic shoulders and custom limits");
            var group=loaded.groups.First(g=>g.shoulderSwingAxes.Length>0);
            loaded.muscles[group.muscles[1]].dof=loaded.muscles[group.muscles[0]].dof;
            bool rejected=false;try{_ =new HumanoidSolver(loaded);}catch(InvalidDataException){rejected=true;}
            check(rejected,"malformed analytic shoulder DOFs are rejected");
        }
        finally{File.Delete(path);}
    }
}
