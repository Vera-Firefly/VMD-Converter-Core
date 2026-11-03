namespace UnityHps;

internal readonly record struct SourceSphereNeighbor(int Index,double Distance);

internal sealed class SourceSphereIndex
{
    readonly double[][] points;
    readonly int[] order;
    readonly IComparer<int>[] comparers;
    readonly double maximumSquaredNorm;
    internal SourceSphereIndex(double[][] points)
    {
        this.points=points;
        order=Enumerable.Range(0,points.Length).ToArray();
        maximumSquaredNorm=points.Max(p=>p[0]*p[0]+p[1]*p[1]+p[2]*p[2]);
        comparers=Enumerable.Range(0,3).Select(axis=>Comparer<int>.Create((a,b)=>{
            int comparison=points[a][axis].CompareTo(points[b][axis]);return comparison!=0?comparison:a.CompareTo(b);
        })).ToArray();
        Build(0,order.Length,0);
    }
    void Build(int begin,int end,int depth)
    {
        if(end-begin<=1)return;
        Array.Sort(order,begin,end-begin,comparers[depth%3]);int middle=(begin+end)/2;
        Build(begin,middle,depth+1);Build(middle+1,end,depth+1);
    }
    internal SourceSphereNeighbor[] Nearest(double[] query,int count)
    {
        if(count<1||count>points.Length)throw new ArgumentOutOfRangeException(nameof(count));
        var queue=new PriorityQueue<SourceSphereNeighbor,(double distance,int index)>();
        double queryNorm=query[0]*query[0]+query[1]*query[1]+query[2]*query[2];
        void Visit(int begin,int end,int depth)
        {
            if(begin>=end)return;
            int middle=(begin+end)/2,index=order[middle],axis=depth%3;double[] point=points[index];
            double distance=Math.Max(0,1-point[0]*query[0]-point[1]*query[1]-point[2]*query[2]);
            if(queue.Count<count)queue.Enqueue(new(index,distance),(-distance,-index));
            else if(distance<queue.Peek().Distance||distance==queue.Peek().Distance&&index<queue.Peek().Index){queue.Dequeue();queue.Enqueue(new(index,distance),(-distance,-index));}
            double split=query[axis]-point[axis];
            if(split<0)Visit(begin,middle,depth+1);else Visit(middle+1,end,depth+1);
            // The resource axes originate as float quaternions. Include their
            // norm error in the pruning bound; selection still uses 1-dot.
            double radius=queue.Count<count?double.PositiveInfinity:Math.Max(0,2*queue.Peek().Distance+queryNorm+maximumSquaredNorm-2)+1e-12;
            if(split*split<=radius){if(split<0)Visit(middle+1,end,depth+1);else Visit(begin,middle,depth+1);}
        }
        Visit(0,order.Length,0);
        return queue.UnorderedItems.Select(item=>item.Element).ToArray();
    }
}
