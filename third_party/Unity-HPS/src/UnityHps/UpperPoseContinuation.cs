namespace UnityHps;

internal sealed class UpperPoseContinuation
{
    readonly UpperModel model;
    readonly Dictionary<int,int> bones;
    readonly Dictionary<int,Joint> joints;
    readonly Dictionary<int,Q> pre,post;
    readonly double[] GuardNegative,GuardPositive;
    internal UpperPoseContinuation(UpperModel model,double[] negative,double[] positive)
    {
        this.model=model;GuardNegative=negative;GuardPositive=positive;
        bones=model.ReferenceRig.bones.Select((b,i)=>(b.humanBone,i)).Where(b=>b.humanBone>=0).ToDictionary(b=>b.humanBone,b=>b.i);
        joints=model.Joints.ToDictionary(j=>j.HumanBone);
        pre=joints.ToDictionary(j=>j.Key,j=>Q.Read(j.Value.Pre));post=joints.ToDictionary(j=>j.Key,j=>Q.Read(j.Value.Post));
    }
    static double[] GAdd(double[] a,double[] b)=>a.Zip(b,(x,y)=>x+y).ToArray();
    static double[] GRotate(Q q,double[] v){var r=q*new Q(v[0],v[1],v[2],0)*q.Inverse;return[r.X,r.Y,r.Z];}
    static Q GRoll(double angle)=>new(Math.Sin(angle/2),0,0,Math.Cos(angle/2));
    static double GWrap(double a)=>a-2*Math.PI*Math.Floor((a+Math.PI)/(2*Math.PI));
    static double GAngle(Q a,Q b){Q q=(a.Inverse*b).Normalized;return 2*Math.Acos(Math.Clamp(Math.Abs(q.W),0,1));}
    static Q GHinge(double a,int axis){double s=Math.Sin(a/2);return new(axis==0?s:0,axis==1?s:0,axis==2?s:0,Math.Cos(a/2));}

    double SRadians(double[] output,int channel){var m=model.ReferenceRig.muscles[channel];double v=output[channel-37];return v*(v<0?-m.minimum:m.maximum)*joints[m.humanBone].LimitSign[m.dof]*Math.PI/180;}
    Q[] SLocal(double[] output,int side){
        int s=11+side,u=13+side,f=15+side,h=17+side,o=9*side;double a=GWrap(SRadians(output,41+o)),b=GWrap(SRadians(output,43+o));int axis=model.ReferenceRig.muscles[42+o].dof;
        return[(pre[s]*Swing(output,37+o,38+o,s)*post[s].Inverse).Normalized,
            (pre[u]*Swing(output,39+o,40+o,u)*GRoll(a/2)*post[u].Inverse).Normalized,
            (post[u]*GRoll(a/2)*post[u].Inverse*pre[f]*GHinge(SRadians(output,42+o),axis)*GRoll(b/2)*post[f].Inverse).Normalized,
            (post[f]*GRoll(b/2)*post[f].Inverse*pre[h]*Swing(output,44+o,45+o,h)*post[h].Inverse).Normalized];
    }

    Q Swing(double[] v,int c1,int c2,int h){
        var t=new double[3];foreach(int c in new[]{c1,c2}){var m=model.ReferenceRig.muscles[c];double a=v[c-37]*(v[c-37]<0?-m.minimum:m.maximum)*joints[h].LimitSign[m.dof];t[m.dof]=Math.Tan(a*Math.PI/360);}
        return new Q(0,t[1],t[2],1).Normalized;
    }
    // One ordered motion. Raw pose increments guide branch continuity, not accuracy.
    internal void StabilizeSequence(double[][] raw,double[][] values)
    {
        Q[][]? previousRaw=null,previousSolved=null;double[]? previous=null;int corrected=0;
        for(int f=0;f<values.Length;f++){
            // Native evaluation folds total twist before splitting it. Keep
            // serialized keys in that chart too: equivalent endpoint values
            // one whole turn apart are not equivalent under linear interpolation.
            foreach(int slot in new[]{4,6,13,15})values[f][slot]=NormalizeDegrees(Principal(Degrees(values[f],slot)),slot);
            var currentRaw=new Q[2][];var solved=new Q[2][];
            for(int side=0;side<2;side++){
                int[] chain=[11+side,13+side,15+side,17+side];
                currentRaw[side]=chain.Select(h=>Q.Read(raw[f].AsSpan(bones[h]*4,4))).ToArray();
                solved[side]=SLocal(values[f],side);
                if(previous==null)continue;
                var expected=Enumerable.Range(0,4).Select(k=>(previousSolved![side][k]*previousRaw![side][k].Inverse*currentRaw[side][k]).Normalized).ToArray();
                double maximum=Enumerable.Range(0,4).Max(k=>GAngle(expected[k],solved[side][k])*180/Math.PI);
                bool nearCut=Enumerable.Range(0,9).Where(c=>c!=5).Any(c=>Math.Abs(Principal(Degrees(values[f],side*9+c)))>178.75);
                bool crossing=Enumerable.Range(0,9).Where(c=>c!=5).Any(c=>Math.Abs(Principal(Degrees(values[f],side*9+c))-Principal(Degrees(previous,side*9+c)))>180);
                if(maximum<45&&!nearCut&&!crossing)continue;
                var baseline=(double[])values[f].Clone();var target=LocalArm(baseline,side);
                double length=Length(Sub(target.position[1],target.position[2]))+Length(Sub(target.position[2],target.position[3]));
                double[]? best=null;double score=double.PositiveInfinity;
                for(int seed=0;seed<2;seed++){
                    var start=(double[])baseline.Clone();if(seed==1)Array.Copy(previous,side*9,start,side*9,9);
                    BoundGuard(start,previous,side);
                    var fitted=FitGuard(start,previous,baseline,side,expected,target.position[2],target.position[3],target.world[3],length);
                    double cost=SumSquares(GuardResidual(fitted,baseline,side,expected,target.position[2],target.position[3],target.world[3],length));
                    if(cost<score){score=cost;best=fitted;}
                }
                if(best==null||best.Any(x=>!double.IsFinite(x)))throw new InvalidDataException("Nonfinite upper continuation");
                Array.Copy(best,side*9,values[f],side*9,9);solved[side]=SLocal(values[f],side);corrected++;
            }
            previousRaw=currentRaw;previousSolved=solved;previous=values[f];
        }
        Console.Error.WriteLine($"Temporal arm guard: {values.Length} frames; corrected side/frames={corrected}; continuity only");
    }
    double Degrees(double[] v,int slot){var m=model.ReferenceRig.muscles[37+slot];return v[slot]*(v[slot]<0?-m.minimum:m.maximum);}
    double NormalizeDegrees(double angle,int slot){var m=model.ReferenceRig.muscles[37+slot];return angle/(angle<0?-m.minimum:m.maximum);}
    static double Principal(double a){a-=360*Math.Floor((a+180)/360);return a<=-179.9999?179.9999:a;}
    void BoundGuard(double[] values,double[] previous,int side){
        for(int c=0;c<9;c++){
            int slot=side*9+c;double anchor=Degrees(previous,slot),angle=Degrees(values,slot);bool guard=c!=5;
            if(guard){anchor=Principal(anchor);angle=Principal(angle);}
            while(angle-anchor>180)angle-=360;while(angle-anchor< -180)angle+=360;
            double minimum=-GuardNegative[slot],maximum=GuardPositive[slot];
            if(guard){minimum=Math.Max(minimum,-179);maximum=Math.Min(maximum,179);}
            anchor=Math.Clamp(anchor,minimum,maximum);
            double low=Math.Max(anchor-60,minimum),high=Math.Min(anchor+60,maximum);
            values[slot]=NormalizeDegrees(Math.Clamp(angle,low,high),slot);
        }
    }
    (Q[] local,Q[] world,double[][] position) LocalArm(double[] v,int side){
        var local=SLocal(v,side);var world=new Q[4];var position=new double[4][];
        int[] chain=[11+side,13+side,15+side,17+side];var rest=model.ReferenceRig.bindBones;
        for(int k=0;k<4;k++){
            var offset=rest[bones[chain[k]]].position.Select(x=>(double)x).ToArray();
            world[k]=k==0?local[k]:(world[k-1]*local[k]).Normalized;
            position[k]=k==0?new double[3]:GAdd(position[k-1],GRotate(world[k-1],offset));
        }
        return(local,world,position);
    }
    static double[] Sub(double[] a,double[] b)=>a.Zip(b,(x,y)=>x-y).ToArray();
    static double Length(double[] a)=>Math.Sqrt(a.Sum(x=>x*x));
    static double[] LogDifference(Q target,Q actual){
        var q=(target.Inverse*actual).Normalized;if(q.W<0)q=new Q(-q.X,-q.Y,-q.Z,-q.W);
        double n=Math.Sqrt(q.X*q.X+q.Y*q.Y+q.Z*q.Z),scale=n<1e-10?2:2*Math.Atan2(n,q.W)/n;
        return[q.X*scale,q.Y*scale,q.Z*scale];
    }
    double[] GuardResidual(double[] values,double[] baseline,int side,Q[] expected,double[] elbow,double[] wrist,Q wristRotation,double length){
        var decoded=LocalArm(values,side);var r=new List<double>(30);
        for(int k=0;k<4;k++)r.AddRange(LogDifference(expected[k],decoded.local[k]));
        r.AddRange(LogDifference(wristRotation,decoded.world[3]).Select(x=>x*3));
        r.AddRange(Sub(decoded.position[3],wrist).Select(x=>x*5/Math.Max(.001,length)));
        r.AddRange(Sub(decoded.position[2],elbow).Select(x=>x*5/Math.Max(.001,length)));
        for(int c=0;c<9;c++){int slot=side*9+c;r.Add(.02*Principal(Degrees(values,slot)-Degrees(baseline,slot))/90);}
        return r.ToArray();
    }
    static double SumSquares(double[] v)=>v.Sum(x=>x*x);
    double[] FitGuard(double[] start,double[] previous,double[] baseline,int side,Q[] expected,double[] elbow,double[] wrist,Q wristRotation,double length){
        var values=(double[])start.Clone();double lambda=.001;
        double[] Residual(double[] v)=>GuardResidual(v,baseline,side,expected,elbow,wrist,wristRotation,length);
        var residual=Residual(values);double cost=SumSquares(residual);
        for(int iteration=0;iteration<18;iteration++){
            const double epsilon=.001;var jacobian=new double[9][];
            for(int c=0;c<9;c++){
                var probe=(double[])values.Clone();probe[side*9+c]+=epsilon;var r=Residual(probe);
                jacobian[c]=r.Select((x,j)=>(x-residual[j])/epsilon).ToArray();
            }
            var a=new double[9,10];
            for(int j=0;j<9;j++){
                for(int k=0;k<9;k++)for(int n=0;n<residual.Length;n++)a[j,k]+=jacobian[j][n]*jacobian[k][n];
                for(int n=0;n<residual.Length;n++)a[j,9]-=jacobian[j][n]*residual[n];a[j,j]+=lambda*Math.Max(1,a[j,j]);
            }
            for(int j=0;j<9;j++){
                int pivot=j;for(int k=j+1;k<9;k++)if(Math.Abs(a[k,j])>Math.Abs(a[pivot,j]))pivot=k;
                if(pivot!=j)for(int k=j;k<=9;k++)(a[j,k],a[pivot,k])=(a[pivot,k],a[j,k]);
                double d=a[j,j];for(int k=j;k<=9;k++)a[j,k]/=d;
                for(int k=0;k<9;k++)if(k!=j){d=a[k,j];for(int n=j;n<=9;n++)a[k,n]-=d*a[j,n];}
            }
            var next=(double[])values.Clone();for(int c=0;c<9;c++)next[side*9+c]+=Math.Clamp(a[c,9],-.2,.2);
            BoundGuard(next,previous,side);var candidate=Residual(next);double nextCost=SumSquares(candidate);
            if(nextCost<cost){double gain=cost-nextCost;values=next;residual=candidate;cost=nextCost;lambda=Math.Max(1e-8,lambda/3);if(gain<1e-7)break;}
            else{lambda*=5;if(lambda>1e8)break;}
        }
        return values;
    }
}
