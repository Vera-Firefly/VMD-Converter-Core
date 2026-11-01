namespace UnityHps;

public sealed partial class HumanoidUpperProjector
{
    /// <summary>Project a chronological motion with per-motion upper-arm branch continuity; preserve all other initial muscle channels.</summary>
    public float[][] ProjectBatch(PoseFrame[] frames,float[][] initial,int threads=0,TaskScheduler? scheduler=null,HumanoidRangePolicy rangePolicy=HumanoidRangePolicy.Tangent)
        => ContinueBatch(frames,initial,false,threads,scheduler,rangePolicy);
    /// <summary>Project source motion using current-pose branch guidance and finite short-cut repair; requires nonnegative, strictly increasing frame times.</summary>
    public float[][] ProjectSourceBatch(PoseFrame[] frames,float[][] initial,int threads=0,TaskScheduler? scheduler=null,HumanoidRangePolicy rangePolicy=HumanoidRangePolicy.Tangent)
        => ContinueBatch(frames,initial,false,threads,scheduler,rangePolicy,true);
    /// <summary>Continue existing fitted upper muscle curves without replacing them with a new source projection.</summary>
    public float[][] StabilizeBatch(PoseFrame[] frames,float[][] initial,int threads=0,TaskScheduler? scheduler=null,HumanoidRangePolicy rangePolicy=HumanoidRangePolicy.Tangent)
        => ContinueBatch(frames,initial,true,threads,scheduler,rangePolicy);
    float[][] ContinueBatch(PoseFrame[] frames,float[][] initial,bool fitted,int threads,TaskScheduler? scheduler,HumanoidRangePolicy rangePolicy,bool sourceMotion=false)
    {
        if(threads<0)throw new ArgumentOutOfRangeException(nameof(threads));
        if(frames.Length!=initial.Length||initial.Any(v=>v.Length!=muscleCount||v.Any(x=>!float.IsFinite(x))))throw new InvalidDataException("Upper continuity inputs differ");
        for(int f=0;sourceMotion&&f<frames.Length;f++)if(!float.IsFinite(frames[f].time)||frames[f].time<0||f>0&&frames[f].time<=frames[f-1].time)
            throw new InvalidDataException("Upper continuity requires chronological frame times");
        var mapped=new double[frames.Length][];var projected=new double[frames.Length][];
        Parallel.For(0,frames.Length,new ParallelOptions{MaxDegreeOfParallelism=threads==0?Environment.ProcessorCount:threads,TaskScheduler=scheduler??TaskScheduler.Default},f=>{
            mapped[f]=adapter.Map(frames[f]);
            if(!fitted)projected[f]=ProjectUpper(mapped[f]);
            else projected[f]=Enumerable.Range(0,18).Select(c=>{
                double value=initial[f][outputSlots[c]],angle=value*(value<0?-outputLimits[c].minimum:outputLimits[c].maximum);
                var reference=model.ReferenceRig.muscles[c+37];return angle/(angle<0?-reference.minimum:reference.maximum);
            }).ToArray();
        });
        var limits=new HumanoidSolver(outputRig,rangePolicy:rangePolicy).CreateFitSeed(Enumerable.Repeat(1000f,muscleCount).ToArray());
        var negative=Enumerable.Range(0,18).Select(c=>(double)-outputLimits[c].minimum*limits[outputSlots[c]]-.001).ToArray();
        var positive=Enumerable.Range(0,18).Select(c=>(double)outputLimits[c].maximum*limits[outputSlots[c]]-.001).ToArray();
        if(sourceMotion)new SourceUpperPoseContinuation(model,negative,positive).StabilizeSequence(mapped,projected,frames.Select(f=>f.time).ToArray());
        else new UpperPoseContinuation(model,negative,positive).StabilizeSequence(mapped,projected);
        var result=new float[frames.Length][];
        for(int f=0;f<frames.Length;f++){
            result[f]=(float[])initial[f].Clone();
            for(int c=0;c<18;c++){
                var reference=model.ReferenceRig.muscles[c+37];double value=projected[f][c];double angle=value*(value<0?-reference.minimum:reference.maximum);
                double limit=angle<0?-outputLimits[c].minimum:outputLimits[c].maximum;
                result[f][outputSlots[c]]=(float)(angle/limit);
            }
        }
        return result;
    }
}
