// Copy this file into a Unity project's Assets/Editor folder. Unity 2022.3+.
// This exporter is the only component that uses UnityEngine / UnityEditor.
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
        [Serializable] public class RigData
        {
            public int version = 1;
            public string source;
            public float humanScale;
            public int hips;
            public BoneData[] bones;
            public BoneData[] bindBones;
            public MuscleData[] muscles;
            public GroupData[] groups;
            public float[] bodyPosition, bodyRotation, bodyPositionJacobian;
        }
        [Serializable] public class BoneData
        {
            public string name;
            public int parent = -1, humanBone = -1;
            public float[] position, rotation;
        }
        [Serializable] public class MuscleData { public string name; public int unityIndex; public int humanBone, dof; public float minimum, maximum; }
        [Serializable] public class GroupData
        {
            public int[] muscles, bones;
            public int resolution;
            public SampleData[] samples;
        }
        [Serializable] public class SampleData { public float[] positions, rotations, muscles; }
        [Serializable] public class HumanData { public float[] bodyPosition, bodyRotation, muscles; }
        [Serializable] public class FrameData
        {
            public float time;
            public float[] positions, rotations;
            public HumanData reference;
            public HumanData inputHumanPose;
        }
        [Serializable] public class PoseData { public int version = 1; public FrameData[] frames; }
        [Serializable] public class ExportRequest
        {
            public string assetPath;
            public string outputDirectory;
            public int resolution = 5;
            public int referenceFrames = 128;
            public int seed = 12345;
            public string rigPath, referencePath, resultPath, replayReportPath;
        }
        [Serializable] public class ExportRequests { public ExportRequest[] requests; }

        [MenuItem("Tools/Unity HPS/Export selected Humanoid calibration")]
        public static void ExportSelected()
        {
            GameObject source = Selection.activeGameObject;
            if (source == null) throw new InvalidOperationException("Select a Humanoid GameObject or FBX first.");
            string directory = EditorUtility.OpenFolderPanel("Export Unity HPS calibration", "", "");
            if (string.IsNullOrEmpty(directory)) return;
            Export(source, directory, 5, 128, 12345);
        }

        // CLI batch use: Unity -batchmode -nographics -quit -projectPath ...
        // -executeMethod UnityHps.Reference.UnityHpsCalibration.Batch -hpsRequest request.json
        public static void Batch()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-hpsRequest");
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing -hpsRequest.");
                string json = File.ReadAllText(args[index + 1]);
                ExportRequests batch = JsonUtility.FromJson<ExportRequests>(json);
                ExportRequest[] requests = batch.requests ?? new[] { JsonUtility.FromJson<ExportRequest>(json) };
                foreach (ExportRequest request in requests)
                {
                    var source = AssetDatabase.LoadAssetAtPath<GameObject>(request.assetPath);
                    if (source == null) throw new FileNotFoundException("No imported model: " + request.assetPath);
                    Export(source, request.outputDirectory, request.resolution, request.referenceFrames, request.seed);
                }
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        /// <summary>Export from a scene instance, prefab, or Unity-imported FBX; original is never posed.</summary>
        public static void Export(GameObject source, string directory, int resolution = 5, int referenceFrames = 128, int seed = 12345)
        {
            if (resolution < 3 || resolution > 17 || resolution % 2 == 0 || referenceFrames < 1)
                throw new ArgumentOutOfRangeException("Use odd resolution 3..17 and positive referenceFrames.");
            Animator original = source.GetComponent<Animator>() ?? source.GetComponentInChildren<Animator>(true);
            if (original == null || original.avatar == null || !original.avatar.isValid || !original.avatar.isHuman)
                throw new InvalidOperationException("A valid Unity Humanoid Avatar is required: " + source.name);
            var clones = new Dictionary<Transform, Transform>();
            var root = new GameObject("UnityHps_Calibration_Temporary") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var required = new HashSet<Transform> { original.transform };
                var human = new Dictionary<Transform, int>();
                for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
                {
                    Transform t = original.GetBoneTransform((HumanBodyBones)b);
                    if (t == null) continue;
                    human[t] = b;
                    for (Transform p = t; p != null; p = p.parent)
                    { required.Add(p); if (p == original.transform) break; }
                }
                clones[original.transform] = root.transform;
                Clone(original.transform, root.transform, required, clones);
                var animator = root.AddComponent<Animator>();
                animator.avatar = original.avatar;
                animator.applyRootMotion = false;
                using (var handler = new HumanPoseHandler(original.avatar, root.transform))
                {
                    SampleData bindPose = Capture(root.GetComponentsInChildren<Transform>(true));
                    var zero = new HumanPose(); handler.GetHumanPose(ref zero);
                    zero.muscles = new float[HumanTrait.MuscleCount];
                    handler.SetHumanPose(ref zero);
                    handler.GetHumanPose(ref zero);
                    Array.Clear(zero.muscles, 0, zero.muscles.Length);
                    handler.SetHumanPose(ref zero);
                    var readZero = new HumanPose(); handler.GetHumanPose(ref readZero);
                    Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                    var indices = transforms.Select((t, i) => new { t, i }).ToDictionary(p => p.t, p => p.i);
                    var humanIndices = new Dictionary<int, int>();
                    foreach (var pair in human) humanIndices[pair.Value] = indices[clones[pair.Key]];
                    foreach (Transform t in transforms)
                    {
                        Vector3 s = t.lossyScale;
                        if (Mathf.Abs(s.x - s.y) > 0.0001f || Mathf.Abs(s.x - s.z) > 0.0001f || s.x <= 0)
                            throw new NotSupportedException("Nonuniform/negative bone scales need an affine solver: " + t.name);
                    }
                    SampleData neutral = Capture(transforms);
                    var rig = new RigData
                    {
                        source = source.name + " / Unity " + Application.unityVersion,
                        humanScale = animator.humanScale,
                        hips = humanIndices[(int)HumanBodyBones.Hips],
                        bodyPosition = V(readZero.bodyPosition), bodyRotation = Q(readZero.bodyRotation),
                        bones = new BoneData[transforms.Length],
                        bindBones = new BoneData[transforms.Length],
                        muscles = Enumerable.Range(0, HumanTrait.MuscleCount).Select(m => new MuscleData { name = HumanTrait.MuscleName[m], unityIndex = m,
                            humanBone = HumanTrait.BoneFromMuscle(m), dof = Enumerable.Range(0,3).FirstOrDefault(d => HumanTrait.MuscleFromBone(HumanTrait.BoneFromMuscle(m), d) == m),
                            minimum = HumanTrait.GetMuscleDefaultMin(m), maximum = HumanTrait.GetMuscleDefaultMax(m) }).ToArray()
                    };
                    for (int b = 0; b < transforms.Length; b++)
                    {
                        int humanBone = humanIndices.Where(p => p.Value == b).Select(p => p.Key).DefaultIfEmpty(-1).First();
                        rig.bones[b] = new BoneData { name = transforms[b].name, parent = transforms[b].parent == null ? -1 : indices[transforms[b].parent],
                            humanBone = humanBone, position = Slice(neutral.positions, b * 3, 3), rotation = Slice(neutral.rotations, b * 4, 4) };
                        rig.bindBones[b] = new BoneData { name = transforms[b].name, parent = rig.bones[b].parent, humanBone = humanBone,
                            position = Slice(bindPose.positions, b * 3, 3), rotation = Slice(bindPose.rotations, b * 4, 4) };
                    }
                    rig.bodyPositionJacobian = MeasureBodyPosition(handler, transforms);
                    handler.SetHumanPose(ref zero);
                    var groups = new List<GroupData>();
                    var byBone = Enumerable.Range(0, HumanTrait.MuscleCount)
                        .Where(m => humanIndices.ContainsKey(HumanTrait.BoneFromMuscle(m)))
                        .GroupBy(HumanTrait.BoneFromMuscle);
                    foreach (var group in byBone)
                    {
                        int[] channels = group.ToArray();
                        if (channels.Length > 3) throw new NotSupportedException("More than 3 muscles per group.");
                        int count = (int)Math.Pow(resolution, channels.Length);
                        var raw = new SampleData[count];
                        var affected = new HashSet<int>();
                        for (int sample = 0; sample < count; sample++)
                        {
                            Array.Clear(zero.muscles, 0, zero.muscles.Length);
                            int cursor = sample;
                            foreach (int channel in channels)
                            { zero.muscles[channel] = (cursor % resolution) * 2f / (resolution - 1) - 1; cursor /= resolution; }
                            handler.SetHumanPose(ref zero);
                            raw[sample] = Capture(transforms);
                            var readback = new HumanPose(); handler.GetHumanPose(ref readback);
                            if (raw[sample].rotations.Any(v => !float.IsFinite(v)) ||
                                raw[sample].positions.Any(v => !float.IsFinite(v)) ||
                                readback.muscles.Any(v => !float.IsFinite(v)))
                                throw new InvalidDataException("Non-finite native calibration: " + source.name + "/" +
                                    HumanTrait.BoneName[group.Key] + " sample=" + sample + " input=" +
                                    string.Join(",", channels.Select(ch => zero.muscles[ch].ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                            raw[sample].muscles = readback.muscles;
                            for (int b = 0; b < transforms.Length; b++)
                                if (Quaternion.Angle(ReadQ(raw[sample].rotations, b * 4), ReadQ(neutral.rotations, b * 4)) > 0.00001f ||
                                    (ReadV(raw[sample].positions, b * 3) - ReadV(neutral.positions, b * 3)).sqrMagnitude > 1e-14f)
                                    affected.Add(b);
                        }
                        int[] observed = affected.OrderBy(b => b).ToArray();
                        groups.Add(new GroupData { muscles = channels, bones = observed, resolution = resolution,
                            samples = raw.Select(s => Subset(s, observed, channels)).ToArray() });
                        Debug.Log("HPS calibrated " + HumanTrait.BoneName[group.Key] + ": " + count + " samples, " + observed.Length + " affected bones");
                    }
                    rig.groups = groups.ToArray();
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "rig.json"), JsonUtility.ToJson(rig, true));
                    File.WriteAllText(Path.Combine(directory, "training.json"), JsonUtility.ToJson(GenerateFrames(handler, transforms, readZero, rig, 512, seed + 7919), true));
                    File.WriteAllText(Path.Combine(directory, "reference.json"), JsonUtility.ToJson(GenerateFrames(handler, transforms, readZero, rig, referenceFrames, seed), true));
                    Debug.Log("HPS_EXPORT_OK " + directory + " bones=" + transforms.Length + " humanScale=" + rig.humanScale);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static PoseData GenerateFrames(HumanPoseHandler handler, Transform[] transforms, HumanPose readZero, RigData rig, int count, int seed)
        {
                    var random = new System.Random(seed);
                    var frames = new FrameData[count];
                    for (int f = 0; f < frames.Length; f++)
                    {
                        var pose = new HumanPose { bodyPosition = readZero.bodyPosition, bodyRotation = readZero.bodyRotation, muscles = new float[HumanTrait.MuscleCount] };
                        if (f > 0)
                        {
                            pose.bodyPosition += new Vector3(Next(random, 0.25f), Next(random, 0.15f), Next(random, 0.25f));
                            pose.bodyRotation = Quaternion.Euler(Next(random, 25), Next(random, 70), Next(random, 25)) * readZero.bodyRotation;
                            // Mix sparse and dense poses; independent muscles and root rotations.
                            foreach (GroupData group in rig.groups)
                                foreach (int channel in group.muscles)
                                    if (f % 3 != 0 || random.NextDouble() < 0.15) pose.muscles[channel] = Next(random, 0.7f);
                        }
                        handler.SetHumanPose(ref pose);
                        var reference = new HumanPose(); handler.GetHumanPose(ref reference);
                        SampleData snapshot = Capture(transforms);
                        frames[f] = new FrameData { time = f / 30f, positions = snapshot.positions, rotations = snapshot.rotations,
                            inputHumanPose = new HumanData { bodyPosition = V(pose.bodyPosition), bodyRotation = Q(pose.bodyRotation), muscles = (float[])pose.muscles.Clone() },
                            reference = new HumanData { bodyPosition = V(reference.bodyPosition), bodyRotation = Q(reference.bodyRotation), muscles = reference.muscles } };
                    }
                    return new PoseData { frames = frames };
        }

        private static float[] MeasureBodyPosition(HumanPoseHandler handler, Transform[] bones)
        {
            var result = new float[bones.Length * 9];
            const float step = 0.1f;
            for (int b = 0; b < bones.Length; b++)
            {
                Transform bone = bones[b]; Vector3 original = bone.position;
                Transform[] children = Enumerable.Range(0, bone.childCount).Select(bone.GetChild).ToArray();
                Vector3[] childPositions = children.Select(t => t.position).ToArray();
                for (int axis = 0; axis < 3; axis++)
                {
                    Vector3 direction = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                    Vector3[] responses = new Vector3[2];
                    for (int side = 0; side < 2; side++)
                    {
                        bone.position = original + direction * (side == 0 ? step : -step);
                        for (int c = 0; c < children.Length; c++) children[c].position = childPositions[c];
                        var pose = new HumanPose(); handler.GetHumanPose(ref pose); responses[side] = pose.bodyPosition;
                    }
                    bone.position = original;
                    for (int c = 0; c < children.Length; c++) children[c].position = childPositions[c];
                    Vector3 derivative = (responses[0] - responses[1]) / (2 * step);
                    result[b * 9 + axis] = derivative.x;
                    result[b * 9 + 3 + axis] = derivative.y;
                    result[b * 9 + 6 + axis] = derivative.z;
                }
            }
            return result;
        }

        private static void Clone(Transform source, Transform parent, HashSet<Transform> required, Dictionary<Transform, Transform> clones)
        {
            foreach (Transform child in source)
            {
                if (!required.Contains(child)) continue;
                var t = new GameObject(child.name) { hideFlags = HideFlags.HideAndDontSave }.transform;
                t.SetParent(parent, false); t.localPosition = child.localPosition; t.localRotation = child.localRotation; t.localScale = child.localScale;
                clones[child] = t; Clone(child, t, required, clones);
            }
        }

        private static SampleData Capture(Transform[] bones)
        {
            var sample = new SampleData { positions = new float[bones.Length * 3], rotations = new float[bones.Length * 4] };
            for (int b = 0; b < bones.Length; b++)
            {
                Transform t = bones[b];
                Vector3 p = t.parent == null ? t.position : Quaternion.Inverse(t.parent.rotation) * (t.position - t.parent.position);
                Quaternion q = t.parent == null ? t.rotation : Quaternion.Inverse(t.parent.rotation) * t.rotation;
                Array.Copy(V(p), 0, sample.positions, b * 3, 3); Array.Copy(Q(q.normalized), 0, sample.rotations, b * 4, 4);
            }
            return sample;
        }
        private static SampleData Subset(SampleData raw, int[] bones, int[] channels)
        {
            var output = new SampleData { positions = new float[bones.Length * 3], rotations = new float[bones.Length * 4], muscles = channels.Select(m => raw.muscles[m]).ToArray() };
            for (int i = 0; i < bones.Length; i++)
            { Array.Copy(raw.positions, bones[i] * 3, output.positions, i * 3, 3); Array.Copy(raw.rotations, bones[i] * 4, output.rotations, i * 4, 4); }
            return output;
        }
        private static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        private static float[] Q(Quaternion q) { return new[] { q.x, q.y, q.z, q.w }; }
        private static Vector3 ReadV(float[] a, int i) { return new Vector3(a[i], a[i + 1], a[i + 2]); }
        private static Quaternion ReadQ(float[] a, int i) { return new Quaternion(a[i], a[i + 1], a[i + 2], a[i + 3]); }
        private static float[] Slice(float[] a, int index, int length) { var result = new float[length]; Array.Copy(a, index, result, 0, length); return result; }
        private static float Next(System.Random random, float range) { return (float)(random.NextDouble() * 2 - 1) * range; }
    }
}
