using System.Text.Json;
using UnityHps;

namespace VeraFirefly.VmdConverter.Core.Hps;

internal static class ConversionDiagnostics
{
    public static void Write(string directory,VmdMotion motion,Rig target,Rig source,
        PoseFrame[] raw,PoseFrame[] direct,PoseFrame[] goal,PoseFrame[] fk,PoseFrame[] decoded,string region,string kind)
    {
        Directory.CreateDirectory(directory);
        string name=motion.ModelName;
        foreach(char c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');
        string prefix=Path.Combine(directory,name+"_"+kind+"_"+Guid.NewGuid().ToString("N"));
        var stages=new Dictionary<string,(Rig rig,PoseFrame[] frames)>
        { ["pmx"]=(source,raw),["target-direct"]=(target,direct),["goal"]=(target,goal),["final-fk"]=(target,fk),["humanoid-decoded"]=(target,decoded) };
        using(var writer=new BinaryWriter(File.Create(prefix+".posebin")))
        {
            byte[] header=JsonSerializer.SerializeToUtf8Bytes(new{version=1,frameCount=goal.Length,
                stages=stages.Select(s=>new{name=s.Key,bones=s.Value.rig.bones})});
            writer.Write(header.Length);writer.Write(header);
            foreach(var stage in stages.Values)foreach(var frame in stage.frames)
            {writer.Write(frame.time);foreach(float x in frame.positions)writer.Write(x);foreach(float x in frame.rotations)writer.Write(x);}
        }
        File.WriteAllText(prefix+".json",JsonSerializer.Serialize(new{kind,region,frames=goal.Length,duration=goal[^1].time,
            rigSource=target.source,rigPrecision=target.precision},new JsonSerializerOptions{WriteIndented=true}));
    }
}
