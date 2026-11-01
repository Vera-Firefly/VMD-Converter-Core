namespace UnityHps;

internal sealed class SourceUpperPoseContinuation
{
    // Continuing a rotation branch must not replace the current clavicle motion.
    const double MaximumShoulderCorrectionRadians = 15 * Math.PI / 180;
    readonly UpperModel model;
    readonly Dictionary<int,int> bones;
    readonly Dictionary<int,Joint> joints;
    readonly Dictionary<int,Q> pre,post;
    readonly double[] GuardNegative,GuardPositive;
    internal SourceUpperPoseContinuation(UpperModel model,double[] negative,double[] positive)
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
    Q[] RawArm(double[] raw,int side){
        int s=11+side,u=13+side,f=15+side,h=17+side;
        Q Read(int bone)=>Q.Read(raw.AsSpan(bones[bone]*4,4));
        Q shoulderFrame=pre[s].Inverse*Read(s)*post[s];
        Q shoulder=(pre[s]*shoulderFrame.Swing*post[s].Inverse).Normalized;
        Q upper=pre[u].Inverse*(shoulder.Inverse*Read(s)*Read(u))*post[u],hand=pre[h].Inverse*Read(h)*post[h];
        Q handTwist=hand.Twist;
        Q forearm=pre[f].Inverse*(Read(f)*pre[h]*handTwist*pre[h].Inverse)*post[f];
        Q Half(Q value){Q twist=value.Twist;if(twist.W<0||twist.W==0&&twist.X<0)twist=new Q(-twist.X,0,0,-twist.W);return new Q(twist.X,0,0,1+twist.W).Normalized;}
        Q a=Half(upper),b=Half(forearm);
        // Guide motion in the same distributed-twist topology as the decoder.
        // Moving wrist axial rotation into the forearm preserves the complete
        // wrist orientation; splitting each total twist shares its increment.
        return[shoulder,(pre[u]*upper.Swing*a*post[u].Inverse).Normalized,
            (post[u]*a*post[u].Inverse*pre[f]*forearm.Swing*b*post[f].Inverse).Normalized,
            (post[f]*b*post[f].Inverse*pre[h]*(handTwist.Inverse*hand.Swing*handTwist)*post[h].Inverse).Normalized];
    }
    // Select branches against the current source pose. Integrating corrections
    // from prior frames can carry an incorrect axial branch through a whole clip.
    internal void StabilizeSequence(double[][] raw,double[][] values,float[] times)
    {
        double[]? previous=null;int corrected=0,rejected=0,excursions=0;
        var initial=values.Select(v=>(double[])v.Clone()).ToArray();
        var isolated=IsolatedTwistCuts(initial,times);
        for(int f=0;f<values.Length;f++){
            // Native evaluation folds total twist before splitting it. Keep
            // serialized keys in that chart too: equivalent endpoint values
            // one whole turn apart are not equivalent under linear interpolation.
            foreach(int slot in new[]{4,6,13,15})values[f][slot]=NormalizeDegrees(Principal(Degrees(values[f],slot)),slot);
            var currentRaw=new Q[2][];var solved=new Q[2][];
            for(int side=0;side<2;side++){
                currentRaw[side]=RawArm(raw[f],side);
                solved[side]=SLocal(values[f],side);
                if(previous==null)continue;
                var expected=new Q[4];for(int k=0;k<4;k++)expected[k]=k==0?currentRaw[side][k]:(expected[k-1]*currentRaw[side][k]).Normalized;
                var decoded=LocalArm(values[f],side);
                double maximum=Enumerable.Range(0,4).Max(k=>GAngle(expected[k],decoded.world[k])*180/Math.PI);
                bool nearCut=Enumerable.Range(0,9).Where(c=>c!=5).Any(c=>Math.Abs(Principal(Degrees(values[f],side*9+c)))>178.75);
                bool crossing=Enumerable.Range(0,9).Where(c=>c!=5).Any(c=>Math.Abs(Principal(Degrees(values[f],side*9+c))-Principal(Degrees(previous,side*9+c)))>180);
                if(maximum<100&&!nearCut&&!crossing)continue;
                var baseline=(double[])values[f].Clone();var target=LocalArm(baseline,side);
                double length=Length(Sub(target.position[1],target.position[2]))+Length(Sub(target.position[2],target.position[3]));
                if(isolated[f,side]){
                    var prior=SLocal(previous,side);var localExpected=new Q[4];double sourceStep=0,outputStep=0;
                    for(int k=0;k<4;k++){
                        int b=bones[11+2*k+side];Q a=Q.Read(raw[f-1].AsSpan(b*4,4)),bq=Q.Read(raw[f].AsSpan(b*4,4));
                        sourceStep=Math.Max(sourceStep,GAngle(a,bq)*180/Math.PI);
                        outputStep=Math.Max(outputStep,GAngle(prior[k],target.local[k])*180/Math.PI);
                        localExpected[k]=(prior[k]*a.Inverse*bq).Normalized;
                    }
                    if(sourceStep<70&&outputStep>100){
                        double[]? localBest=null;double localScore=SumSquares(GuardResidual(baseline,baseline,side,localExpected,target.position[2],target.position[3],target.world[3],length,true));
                        for(int seed=0;seed<2;seed++){
                            var start=(double[])baseline.Clone();if(seed==1)Array.Copy(previous,side*9,start,side*9,9);BoundGuard(start,previous,side);
                            var fitted=FitGuard(start,previous,baseline,side,localExpected,target.position[2],target.position[3],target.world[3],length,true);
                            double cost=SumSquares(GuardResidual(fitted,baseline,side,localExpected,target.position[2],target.position[3],target.world[3],length,true));
                            if(cost<localScore&&GAngle(target.local[0],SLocal(fitted,side)[0])<=MaximumShoulderCorrectionRadians){localScore=cost;localBest=fitted;}
                        }
                        if(localBest!=null){Array.Copy(localBest,side*9,values[f],side*9,9);excursions++;continue;}
                    }
                    if(sourceStep<70&&outputStep<=100)continue;
                }
                double[] best=baseline;
                double score=SumSquares(GuardResidual(baseline,baseline,side,expected,target.position[2],target.position[3],target.world[3],length));
                for(int seed=0;seed<2;seed++){
                    var start=(double[])baseline.Clone();if(seed==1)Array.Copy(previous,side*9,start,side*9,9);
                    BoundGuard(start,previous,side);
                    var fitted=FitGuard(start,previous,baseline,side,expected,target.position[2],target.position[3],target.world[3],length);
                    double cost=SumSquares(GuardResidual(fitted,baseline,side,expected,target.position[2],target.position[3],target.world[3],length));
                    if(cost<score){score=cost;best=fitted;}
                }
                if(best.Any(x=>!double.IsFinite(x)))throw new InvalidDataException("Nonfinite upper continuation");
                if(GAngle(solved[side][0],SLocal(best,side)[0])>MaximumShoulderCorrectionRadians){rejected++;continue;}
                if(Enumerable.Range(0,9).Any(c=>best[side*9+c]!=baseline[side*9+c]))corrected++;
                Array.Copy(best,side*9,values[f],side*9,9);
            }
            previous=values[f];
        }
        Console.Error.WriteLine($"Source arm branch guard: {values.Length} frames; corrected side/frames={corrected}; rejected clavicle changes={rejected}; isolated cuts={excursions}; continuity only");
    }
    bool[,] IsolatedTwistCuts(double[][] initial,float[] times){
        var result=new bool[initial.Length,2];
        for(int f=1;f<initial.Length;f++)for(int side=0;side<2;side++)foreach(int c in new[]{4,6}){
            int slot=side*9+c;double a=Principal(Degrees(initial[f-1],slot)),b=Principal(Degrees(initial[f],slot));
            if(Math.Abs(a-b)<=180)continue;
            for(int j=f+1;j<initial.Length&&times[j]-times[f]<=.12f;j++){
                double end=Principal(Degrees(initial[j],slot));
                if(Math.Abs(end-a)<180&&Math.Abs(end-b)>180){for(int i=f-1;i<=j;i++)result[i,side]=true;break;}
            }
        }
        return result;
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
    double[] GuardResidual(double[] values,double[] baseline,int side,Q[] expected,double[] elbow,double[] wrist,Q wristRotation,double length,bool local=false){
        var decoded=LocalArm(values,side);var r=new List<double>(33);
        for(int k=0;k<4;k++)r.AddRange(LogDifference(expected[k],local?decoded.local[k]:decoded.world[k]));
        if(!local)r.AddRange(LogDifference(SLocal(baseline,side)[0],decoded.local[0]).Select(x=>x*5));
        r.AddRange(LogDifference(wristRotation,decoded.world[3]).Select(x=>x*3));
        r.AddRange(Sub(decoded.position[3],wrist).Select(x=>x*5/Math.Max(.001,length)));
        r.AddRange(Sub(decoded.position[2],elbow).Select(x=>x*5/Math.Max(.001,length)));
        for(int c=0;c<9;c++){int slot=side*9+c;r.Add(.02*Principal(Degrees(values,slot)-Degrees(baseline,slot))/90);}
        return r.ToArray();
    }
    static double SumSquares(double[] v)=>v.Sum(x=>x*x);
    double[] FitGuard(double[] start,double[] previous,double[] baseline,int side,Q[] expected,double[] elbow,double[] wrist,Q wristRotation,double length,bool local=false){
        var values=(double[])start.Clone();double lambda=.001;
        double[] Residual(double[] v)=>GuardResidual(v,baseline,side,expected,elbow,wrist,wristRotation,length,local);
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
