// Build-time/reference utility only. No target Avatar or mesh is embedded in the profile.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityHps.Reference
{
    public static partial class UnityHpsCalibration
    {
        public static void ExportSynthetic()
        {
            GameObject root = null; Avatar avatar = null;
            try
            {
                string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, "-hpsOutput");
                if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing -hpsOutput.");
                root = new GameObject("HpsSynthetic"); var bones = new Dictionary<HumanBodyBones, Transform>();
                bool massProbe = Array.IndexOf(args, "-hpsMassProbe") >= 0;
                bool topology53 = Array.IndexOf(args, "-hpsTopology53") >= 0;
                Transform Add(HumanBodyBones id, Transform parent, Vector3 position)
                {
                    var bone = new GameObject(id.ToString()).transform; bone.SetParent(parent, false); bone.localPosition = position; bones.Add(id, bone); return bone;
                }
                Transform hips = Add(HumanBodyBones.Hips, root.transform, new Vector3(0,1,0));
                Transform spine = Add(HumanBodyBones.Spine, hips, new Vector3(0,0.1f,0));
                Transform chest = Add(HumanBodyBones.Chest, spine, new Vector3(0,0.15f,0));
                Transform upper = topology53 ? chest : Add(HumanBodyBones.UpperChest, chest, new Vector3(0,0.1f,0));
                Transform neck = Add(HumanBodyBones.Neck, upper, new Vector3(0,0.12f,0));
                Transform head = Add(HumanBodyBones.Head, neck, new Vector3(0,0.12f,0));
                if (!topology53) Add(HumanBodyBones.Jaw, head, new Vector3(0,0.01f,0.05f));
                Add(HumanBodyBones.LeftEye, head, new Vector3(-0.035f,0.07f,0.08f));
                Add(HumanBodyBones.RightEye, head, new Vector3(0.035f,0.07f,0.08f));
                foreach (bool left in new[] { true, false })
                {
                    string side = left ? "Left" : "Right"; float sign = left ? -1 : 1;
                    HumanBodyBones Id(string part) { return (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones),side + part); }
                    Transform thigh = Add(Id("UpperLeg"),hips,new Vector3(sign*0.1f,massProbe?-0.04f:0,massProbe?0.025f:0));
                    Transform shin = Add(Id("LowerLeg"),thigh,new Vector3(0,-0.45f,0));
                    Transform foot = Add(Id("Foot"),shin,new Vector3(0,-0.45f,0));
                    Add(Id("Toes"),foot,new Vector3(0,-0.035f,0.15f));
                    Transform shoulder = Add(Id("Shoulder"),upper,new Vector3(sign*0.08f,0.04f,0));
                    Transform arm = Add(Id("UpperArm"),shoulder,new Vector3(sign*0.13f,0,0));
                    Transform elbow = Add(Id("LowerArm"),arm,new Vector3(sign*0.3f,0,0));
                    Transform hand = Add(Id("Hand"),elbow,new Vector3(sign*0.25f,0,0));
                    int f = 0;
                    foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                    {
                        float z = 0.045f - f * 0.02f;
                        Transform proximal = Add(Id(finger+"Proximal"),hand,new Vector3(sign*(finger=="Thumb"?0.025f:0.065f),0,z));
                        Transform middle = Add(Id(finger+"Intermediate"),proximal,new Vector3(sign*0.035f,0,0));
                        Add(Id(finger+"Distal"),middle,new Vector3(sign*0.025f,0,0)); f++;
                    }
                }
                var description = new HumanDescription
                {
                    human = bones.Select(p => new HumanBone { boneName = p.Value.name, humanName = HumanTrait.BoneName[(int)p.Key], limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                    skeleton = root.GetComponentsInChildren<Transform>().Select(t => new SkeletonBone { name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale }).ToArray(),
                    upperArmTwist=0.5f,lowerArmTwist=0.5f,upperLegTwist=0.5f,lowerLegTwist=0.5f,armStretch=0.05f,legStretch=0.05f
                };
                avatar = AvatarBuilder.BuildHumanAvatar(root, description);
                if (avatar == null || !avatar.isValid || !avatar.isHuman) throw new InvalidOperationException("Synthetic Avatar is invalid.");
                root.AddComponent<Animator>().avatar = avatar;
                Export(root,args[i+1],7,256,1729);
                Debug.Log("HPS_SYNTHETIC_PROFILE_OK " + args[i+1]);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
            finally { if (root != null) UnityEngine.Object.DestroyImmediate(root); if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar); }
        }
    }
}
