using System.Text.Json;
using UnityHps;

internal static class NativeLegEncodingTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach(bool upperChest in new[]{false,true})
        {
            Rig profile=OfflineRigFactory.LoadProfile(upperChest);
            Rig poseRig=OfflineRigFactory.Transfer(new FbxDocument{bones=profile.bindBones},profile,
                HumanoidResponseTransport.AnatomicalTargets);
            string before=JsonSerializer.Serialize(poseRig,HpsJsonContext.Default.Rig);
            Rig response=OfflineRigFactory.ForHumanoidLegEncoding(poseRig);
            check(before==JsonSerializer.Serialize(poseRig,HpsJsonContext.Default.Rig) &&
                ReferenceEquals(response.bones,poseRig.bones) &&
                response.groups.Where((g,i)=>poseRig.muscles[g.muscles[0]].humanBone is <1 or >6)
                    .All(g=>poseRig.groups.Contains(g)),"analytic leg encoding preserves input and non-leg responses");
            check(response.groups.Count(g=>g.armResponseAxes.Length!=0)==6,
                "portable leg charts support six analytic responses");
            var solver=new HumanoidSolver(response,rangePolicy:HumanoidRangePolicy.Tangent);
            foreach(int side in new[]{1,2})
            {
                int channel=Array.FindIndex(response.muscles,m=>m.humanBone==side&&m.dof==0);
                int thigh=Array.FindIndex(response.bones,b=>b.humanBone==side);
                int foot=Array.FindIndex(response.bones,b=>b.humanBone==side+4);
                var first=new float[response.muscles.Length];first[channel]=179.99f/response.muscles[channel].maximum;
                var second=(float[])first.Clone();second[channel]=180.01f/response.muscles[channel].maximum;
                var a=solver.Decode(first);var b=solver.Decode(second);
                check(Math3.Angle(a.LocalRotations[thigh],b.LocalRotations[thigh])>170 &&
                    Math3.Angle(a.WorldRotations[foot],b.WorldRotations[foot])<.1f,
                    "leg distributed twist folds at its native cut without turning the foot");
                Rig restored=JsonSerializer.Deserialize(JsonSerializer.Serialize(response,HpsJsonContext.Default.Rig),
                    HpsJsonContext.Default.Rig)!;
                var decoded=new HumanoidSolver(restored,rangePolicy:HumanoidRangePolicy.Tangent).Decode(second);
                check(Math3.Angle(decoded.LocalRotations[thigh],b.LocalRotations[thigh])<.001f,
                    "analytic leg response survives rig serialization");
            }
        }
        Rig measured=Demo.CreateRig();measured.precision="unity-native";
        check(ReferenceEquals(measured,OfflineRigFactory.ForHumanoidLegEncoding(measured)),
            "explicit Avatar leg calibration is retained");
    }
}
