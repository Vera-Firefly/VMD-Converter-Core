using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityHps.Reference
{
    public static partial class UnityHpsCalibration
    {
        [Serializable] public class ReplayFrame { public float time; public HumanData humanPose; }
        [Serializable] public class ReplayResults { public ReplayFrame[] frames; }
        [Serializable] public class ReplayReport
        {
            public string unityVersion;
            public int frameCount, mappedBones;
            public float maxWorldPositionErrorMm, rmsWorldPositionErrorMm;
            public float maxWorldRotationErrorDegrees, maxFootPositionErrorMm;
        }

        public static void ReplayBatch()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-hpsRequest");
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing -hpsRequest.");
                var requests = JsonUtility.FromJson<ExportRequests>(File.ReadAllText(args[index + 1]));
                foreach (ExportRequest request in requests.requests) Replay(request);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        public static void Replay(ExportRequest request)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(request.assetPath);
            Animator original = source == null ? null : source.GetComponent<Animator>();
            if (original == null || original.avatar == null || !original.avatar.isHuman)
                throw new InvalidOperationException("No valid source Humanoid for replay.");
            RigData rig = JsonUtility.FromJson<RigData>(File.ReadAllText(request.rigPath));
            PoseData reference = JsonUtility.FromJson<PoseData>(File.ReadAllText(request.referencePath));
            ReplayResults result = JsonUtility.FromJson<ReplayResults>(File.ReadAllText(request.resultPath));
            if (reference.frames.Length != result.frames.Length) throw new InvalidDataException("Replay frame count differs.");
            var root = new GameObject("UnityHps_Calibration_Temporary") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var required = new HashSet<Transform> { original.transform };
                for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
                    for (Transform t = original.GetBoneTransform((HumanBodyBones)b); t != null; t = t.parent)
                    { required.Add(t); if (t == original.transform) break; }
                var clones = new Dictionary<Transform, Transform> { [original.transform] = root.transform };
                Clone(original.transform, root.transform, required, clones);
                var animator = root.AddComponent<Animator>(); animator.avatar = original.avatar;
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                if (transforms.Length != rig.bones.Length) throw new InvalidDataException("Replay skeleton count changed.");
                for (int b = 1; b < transforms.Length; b++)
                    if (transforms[b].name != rig.bones[b].name) throw new InvalidDataException("Replay bone ordering changed.");
                var report = new ReplayReport { unityVersion = Application.unityVersion, frameCount = result.frames.Length };
                foreach (BoneData bone in rig.bones) if (bone.humanBone >= 0) report.mappedBones++;
                double squares = 0; int comparisons = 0;
                var positions = new Vector3[transforms.Length]; var rotations = new Quaternion[transforms.Length];
                using (var handler = new HumanPoseHandler(original.avatar, root.transform))
                {
                    for (int f = 0; f < result.frames.Length; f++)
                    {
                        HumanData input = result.frames[f].humanPose;
                        var pose = new HumanPose { bodyPosition = ReadV(input.bodyPosition, 0), bodyRotation = ReadQ(input.bodyRotation, 0), muscles = input.muscles };
                        handler.SetHumanPose(ref pose);
                        FrameData expected = reference.frames[f];
                        for (int b = 0; b < transforms.Length; b++)
                        {
                            int parent = rig.bones[b].parent;
                            Vector3 lp = ReadV(expected.positions, b * 3); Quaternion lq = ReadQ(expected.rotations, b * 4).normalized;
                            positions[b] = parent < 0 ? lp : positions[parent] + rotations[parent] * lp;
                            rotations[b] = parent < 0 ? lq : (rotations[parent] * lq).normalized;
                            int human = rig.bones[b].humanBone; if (human < 0) continue;
                            float error = Vector3.Distance(transforms[b].position, positions[b]) * 1000;
                            report.maxWorldPositionErrorMm = Mathf.Max(report.maxWorldPositionErrorMm, error);
                            if (human == (int)HumanBodyBones.LeftFoot || human == (int)HumanBodyBones.RightFoot ||
                                human == (int)HumanBodyBones.LeftToes || human == (int)HumanBodyBones.RightToes)
                                report.maxFootPositionErrorMm = Mathf.Max(report.maxFootPositionErrorMm, error);
                            Quaternion delta = (Quaternion.Inverse(rotations[b]) * transforms[b].rotation).normalized;
                            float sine = Mathf.Sqrt(delta.x * delta.x + delta.y * delta.y + delta.z * delta.z);
                            float angle = 2 * Mathf.Atan2(sine, Mathf.Abs(delta.w)) * Mathf.Rad2Deg;
                            report.maxWorldRotationErrorDegrees = Mathf.Max(report.maxWorldRotationErrorDegrees, angle);
                            squares += error * error; comparisons++;
                        }
                    }
                }
                report.rmsWorldPositionErrorMm = (float)Math.Sqrt(squares / comparisons);
                File.WriteAllText(request.replayReportPath, JsonUtility.ToJson(report, true));
                Debug.Log("HPS_REPLAY_OK " + request.replayReportPath + " max foot=" + report.maxFootPositionErrorMm + " mm");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
