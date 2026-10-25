using System.Numerics;
using UnityHps;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace VeraFirefly.VmdConverter.Core.Hps;

public static class PmxRigBuilder
{
    /// <summary>
    /// The PMX 親指０ bone is mapped to Humanoid ThumbProximal instead of a synthetic midpoint
    /// proxy, so every thumb bone carries real animation (see Motion/V05 P1 note).
    /// </summary>
    public static Dictionary<int,int> Map(PmxModel model)
    {
        // Thumb0 carries the proximal thumb motion.
        var rules=new Dictionary<int,string[]> {
            [0]=["下半身","lower body","LowerBody","腰","waist","Hips"], [7]=["上半身","upper body","Spine"],
            [8]=["上半身2","上半身２","upper body2","Chest"], [54]=["上半身3","上半身３","UpperChest"],
            [9]=["首","Neck"], [10]=["頭","Head"], [21]=["左目","LeftEye"], [22]=["右目","RightEye"], [23]=["あご","顎","Jaw"] };
        foreach(bool left in new[]{true,false})
        {
            string j=left?"左":"右",s=left?"Left":"Right";
            rules[left?11:12]=[j+"肩",s+"Shoulder"];
            rules[left?13:14]=[j+"腕",s+"UpperArm",s+"Arm"];
            rules[left?15:16]=[j+"ひじ",j+"肘",s+"LowerArm",s+"Elbow"];
            rules[left?17:18]=[j+"手首",s+"Hand",s+"Wrist"];
            rules[left?1:2]=[j+"足D",j+"足Ｄ",j+"足",s+"UpperLeg",s+"Leg"];
            rules[left?3:4]=[j+"ひざD",j+"膝D",j+"ひざＤ",j+"ひざ",j+"膝",s+"LowerLeg",s+"Knee"];
            rules[left?5:6]=[j+"足首D",j+"足首Ｄ",j+"足首",s+"Foot",s+"Ankle"];
            rules[left?19:20]=[j+"つま先EX",j+"つま先",s+"Toes",s+"Toe"];
            string[] fingers=["親指","人指","中指","薬指","小指"],english=["Thumb","Index","Middle","Ring","Little"];
            for(int f=0;f<5;f++) for(int p=0;p<3;p++)
            {
                int number=f==0?p:p+1;
                string digit=number.ToString(),wide="０１２３"[number].ToString();
                rules[(left?24:39)+f*3+p]=[j+fingers[f]+digit,j+fingers[f]+wide,s+english[f]+new[]{"Proximal","Intermediate","Distal"}[p]];
            }
        }
        var result=new Dictionary<int,int>();var used=new HashSet<int>();
        foreach(var rule in rules)
            foreach(string name in rule.Value)
            {
                int index=model.FindBone(name);
                if(index>=0&&used.Add(index)){result.Add(rule.Key,index);break;}
            }
        foreach(var bone in model.Bones)
        {
            int human=HumanBones.Guess(bone.NameEnglish);
            if(human>=0&&human!=24&&human!=39&&!result.ContainsKey(human)&&used.Add(bone.Index)) result.Add(human,bone.Index);
        }
        return result;
    }

    public static Rig Build(PmxModel model,float scale=0.08f,int coordinate=2,string? profile=null)
    {
        var map=Map(model);var bones=new List<Bone>{new(){name="PMX_Humanoid"}};var humanToRig=new Dictionary<int,int>();
        int Parent(int h)=>h switch {0=>-1,1 or 2 or 7=>0,3=>1,4=>2,5=>3,6=>4,19=>5,20=>6,8=>7,54=>8,9 or 11 or 12=>54,10=>9,21 or 22 or 23=>10,13=>11,14=>12,15=>13,16=>14,17=>15,18=>16,_=>h>=24&&h<=38?((h-24)%3==0?17:h-1):h>=39&&h<=53?((h-39)%3==0?18:h-1):0};
        int AvailableParent(int h)
        {
            int parent=Parent(h);
            while(parent>=0&&!map.ContainsKey(parent)&&!humanToRig.ContainsKey(parent)) parent=Parent(parent);
            return parent;
        }
        V WorldPosition(int h)
        {
            if(map.TryGetValue(h,out int index))return Position(model.Bones[index].Position,coordinate)*scale;
            int hand=h==24?17:18,intermediate=h==24?25:40;
            return (WorldPosition(hand)+WorldPosition(intermediate))*0.5f;
        }
        void Add(int h)
        {
            if(humanToRig.ContainsKey(h))return;
            int parentHuman=AvailableParent(h);if(parentHuman>=0)Add(parentHuman);
            V p=WorldPosition(h);
            V pp=parentHuman<0?V.Zero:WorldPosition(parentHuman);
            humanToRig[h]=bones.Count;
            bones.Add(new Bone {name=model.Bones[map[h]].NameJapanese,parent=parentHuman<0?0:humanToRig[parentHuman],humanBone=h,position=Math3.A(p-pp)});
        }
        void AddThumbProxy(int h,int hand,int intermediate)
        {
            if(!map.ContainsKey(hand)||!map.ContainsKey(intermediate))return;
            Add(hand);
            humanToRig[h]=bones.Count;
            bones.Add(new Bone {name=h==24?"__LeftThumb0M__":"__RightThumb0M__",parent=humanToRig[hand],humanBone=h,
                position=Math3.A(WorldPosition(h)-WorldPosition(hand))});
        }
        if(!map.ContainsKey(24))AddThumbProxy(24,17,25);
        if(!map.ContainsKey(39))AddThumbProxy(39,18,40);
        foreach(int h in map.Keys.Order())Add(h);
        var reference=new Rig {bones=bones.ToArray(),hips=humanToRig.GetValueOrDefault(0)};
        var pose=new SkeletonPose(reference);
        // Build a T-pose reference while preserving PMX's original solved poses.
        // Native Avatar import performs the equivalent reference-pose correction.
        if(humanToRig.TryGetValue(13,out int la)&&humanToRig.TryGetValue(14,out int ra))
        {
            V left=pose.WorldPositions[la]-pose.WorldPositions[ra];left.Y=0;left=V.Normalize(left);
            foreach(bool isLeft in new[]{true,false})
            {
                int a=isLeft?13:14,b=isLeft?15:16,c=isLeft?17:18;
                if(!humanToRig.ContainsKey(a)||!humanToRig.ContainsKey(b)||!humanToRig.ContainsKey(c))continue;
                int ai=humanToRig[a],bi=humanToRig[b],ci=humanToRig[c];V direction=isLeft?left:-left;
                pose.SetWorldRotation(ai,Math3.FromTo(pose.WorldPositions[bi]-pose.WorldPositions[ai],direction)*pose.WorldRotations[ai]);
                pose.SetWorldRotation(bi,Math3.FromTo(pose.WorldPositions[ci]-pose.WorldPositions[bi],direction)*pose.WorldRotations[bi]);
            }
        }
        for(int i=0;i<bones.Count;i++){bones[i].position=Math3.A(pose.LocalPositions[i]);bones[i].rotation=Math3.A(pose.LocalRotations[i]);}
        return OfflineRigFactory.FromSkeleton(new FbxDocument {source="PMX: "+model.NameJapanese,bones=bones.ToArray()},profile);
    }
    internal static V Position(UnityEngine.Vector3 p,int coordinate)=>coordinate switch {0=>new(p.x,p.y,-p.z),1=>new(-p.x,p.y,p.z),2=>new(-p.x,p.y,-p.z),_=>new(p.x,p.y,p.z)};
    internal static Q Rotation(UnityEngine.Quaternion q,int coordinate)
    {
        var converted=VmdCurveUtility.ConvertRotation(q,coordinate);return Q.Normalize(new(converted.x,converted.y,converted.z,converted.w));
    }
}
