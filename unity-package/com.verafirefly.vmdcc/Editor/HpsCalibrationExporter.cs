// VMDCC HPS calibration exporter (Unity 2022.3, Editor only).
//
// Writes a per-muscle sweep of Unity's Humanoid pose so the offline pipeline can
// reconstruct the avatar's muscle space (joint frames, twist split, body model).
// The measured-axis descriptor is consumed by --anim-mode transform --calibrate.
// This descriptor is distinct from the HPS rig.json lattice format.
//
// Install: copy into Packages/com.verafirefly.vmdcc/Editor/ (same asmdef as
// VmdHumanoidCalibrationExporter.cs) and use
//   Tools/VMDCC/Export HPS Calibration
// with the avatar root selected. The scene hierarchy is posed during the sweep and
// restored in a finally block.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    public static class HpsCalibrationExporter
    {
        private const int SweepSteps = 21;      // -1 .. +1 in 0.1 steps
        private const int ComboSamples = 300;

        /// <summary>Non-interactive entry point for -executeMethod.
        /// Reads VMDCC_CALIB_PREFAB / VMDCC_CALIB_OUT, or falls back to
        /// &lt;project&gt;/calibration_args.json with {"prefab":"...","output":"..."}.</summary>
        public static void ExportBatch()
        {
            string prefab = Environment.GetEnvironmentVariable("VMDCC_CALIB_PREFAB");
            string output = Environment.GetEnvironmentVariable("VMDCC_CALIB_OUT");
            string arguments = Path.Combine(Application.dataPath, "..", "calibration_args.json");
            if ((string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(output)) &&
                File.Exists(arguments))
            {
                string text = File.ReadAllText(arguments);
                prefab = string.IsNullOrEmpty(prefab) ? Field(text, "prefab") : prefab;
                output = string.IsNullOrEmpty(output) ? Field(text, "output") : output;
            }
            if (string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(output))
                throw new InvalidOperationException(
                    "Calibration needs VMDCC_CALIB_PREFAB/VMDCC_CALIB_OUT or calibration_args.json.");
            Debug.Log($"[VMDCC] calibration: prefab={prefab} output={output}");
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            if (asset == null) throw new FileNotFoundException("Prefab not found: " + prefab);
            GameObject instance = UnityEngine.Object.Instantiate(asset);
            try
            {
                Animator animator = instance.GetComponent<Animator>() ??
                                    instance.GetComponentInChildren<Animator>(true);
                if (animator == null || animator.avatar == null ||
                    !animator.avatar.isValid || !animator.avatar.isHuman)
                    throw new InvalidOperationException(
                        "Prefab has no usable Humanoid Animator: " + prefab);
                Export(animator, output);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>Batch entry point that exports EVERY Humanoid model under a root.
        /// calibration_args.json: {"output":"...","searchRoot":"Assets","resume":true}</summary>
        public static void ExportAllBatch()
        {
            string output = Environment.GetEnvironmentVariable("VMDCC_CALIB_OUT");
            string root = null;
            bool resume = true;
            string arguments = Path.Combine(Application.dataPath, "..", "calibration_args.json");
            if (File.Exists(arguments))
            {
                string text = File.ReadAllText(arguments);
                if (string.IsNullOrEmpty(output)) output = Field(text, "output");
                root = Field(text, "searchRoot");
                string flag = Field(text, "resume");
                if (!string.IsNullOrEmpty(flag)) resume = flag != "false";
            }
            if (string.IsNullOrEmpty(output))
                throw new InvalidOperationException("ExportAllBatch needs an output directory.");
            if (string.IsNullOrEmpty(root)) root = "Assets";
            Directory.CreateDirectory(output);
            var report = new StringBuilder(4096);
            int exported = 0, skipped = 0, failed = 0;
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { root });
            Array.Sort(guids, StringComparer.Ordinal);
            Debug.Log($"[VMDCC] ExportAllBatch: {guids.Length} model(s) under {root} -> {output}");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                string folder = Path.Combine(output, name);
                if (resume && File.Exists(Path.Combine(folder, "sweep.jsonl")))
                {
                    skipped++;
                    report.AppendLine($"skip\t{path}");
                    continue;
                }
                try
                {
                    GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (asset == null) throw new FileNotFoundException("load failed");
                    GameObject instance = UnityEngine.Object.Instantiate(asset);
                    try
                    {
                        Animator animator = instance.GetComponent<Animator>() ??
                                            instance.GetComponentInChildren<Animator>(true);
                        if (animator == null || animator.avatar == null ||
                            !animator.avatar.isValid || !animator.avatar.isHuman)
                        {
                            skipped++;
                            report.AppendLine($"non-humanoid\t{path}");
                            continue;
                        }
                        Export(animator, folder);
                        exported++;
                        report.AppendLine($"ok\t{path}\t{animator.avatar.humanDescription.human.Length}");
                        Debug.Log($"[VMDCC] exported {name}");
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(instance);
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    report.AppendLine($"FAIL\t{path}\t{exception.GetType().Name}: {exception.Message}");
                    Debug.LogWarning($"[VMDCC] {name} failed: {exception.Message}");
                }
            }
            report.AppendLine($"# exported={exported} skipped={skipped} failed={failed}");
            File.WriteAllText(Path.Combine(output, "summary.tsv"), report.ToString(),
                new UTF8Encoding(false));
            Debug.Log($"[VMDCC] ExportAllBatch done: exported={exported} skipped={skipped} failed={failed}");
        }

        /// <summary>Pose trace export, two modes.
        /// mode "clip":   {"prefab":"...","clip":"Assets/x.anim","output":"...","fps":30}
        ///   samples the clip and records HumanPose muscles + bone locals.
        /// mode "muscles": {"prefab":"...","csv":"...","output":"...","fps":30}
        ///   APPLIES muscle vectors from a CSV (frame,95 values) through Unity's own
        ///   HumanPoseHandler and records the resulting bone locals, so an offline
        ///   solver can be compared against Unity's decode with the same muscles.</summary>
        public static void ExportPoseTraceBatch()
        {
            string arguments = Path.Combine(Application.dataPath, "..", "calibration_args.json");
            if (!File.Exists(arguments))
                throw new InvalidOperationException("calibration_args.json is required.");
            string text = File.ReadAllText(arguments);
            string prefab = Field(text, "prefab");
            string output = Field(text, "output");
            string clipPath = Field(text, "clip");
            string csvPath = Field(text, "csv");
            string mode = Field(text, "mode");
            if (string.IsNullOrEmpty(mode)) mode = string.IsNullOrEmpty(csvPath) ? "clip" : "muscles";
            if (string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(output))
                throw new InvalidOperationException("prefab and output are required.");
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            if (asset == null) throw new FileNotFoundException("Prefab not found: " + prefab);
            GameObject instance = UnityEngine.Object.Instantiate(asset);
            try
            {
                Animator animator = instance.GetComponent<Animator>() ??
                                    instance.GetComponentInChildren<Animator>(true);
                if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                    throw new InvalidOperationException("Prefab has no usable Humanoid Animator.");
                List<BoneEntry> bones = CollectBones(animator);
                using var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                using var writer = new StreamWriter(output, false, new UTF8Encoding(false)) { NewLine = "\n" };
                var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
                handler.SetHumanPose(ref pose);
                handler.GetHumanPose(ref pose);
                if (mode == "clip")
                {
                    AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                    if (clip == null) throw new FileNotFoundException("Clip not found: " + clipPath);
                    float fps = 30f;
                    string fpsText = Field(text, "fps");
                    if (!string.IsNullOrEmpty(fpsText)) float.TryParse(fpsText,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out fps);
                    int count = Mathf.Max(1, Mathf.RoundToInt(clip.length * fps));
                    AnimationMode.StartAnimationMode();
                    try
                    {
                        for (int index = 0; index <= count; index++)
                        {
                            float time = clip.length * index / count;
                            AnimationMode.BeginSampling();
                            AnimationMode.SampleAnimationClip(instance, clip, time);
                            AnimationMode.EndSampling();
                            handler.GetHumanPose(ref pose);
                            WriteTrace(writer, index, time, pose, bones);
                        }
                    }
                    finally { AnimationMode.StopAnimationMode(); }
                }
                else
                {
                    string[] lines = File.ReadAllLines(csvPath);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string[] parts = line.Split(',');
                        int frame = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        int limit = Math.Min(pose.muscles.Length, parts.Length - 1);
                        for (int m = 0; m < limit; m++)
                            pose.muscles[m] = float.Parse(parts[m + 1],
                                System.Globalization.CultureInfo.InvariantCulture);
                        handler.SetHumanPose(ref pose);
                        handler.GetHumanPose(ref pose);
                        WriteTrace(writer, frame, frame / 30f, pose, bones);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
            Debug.Log("[VMDCC] pose trace written: " + output);
        }

        private static void WriteTrace(StreamWriter writer, int index, float time,
            HumanPose pose, List<BoneEntry> bones)
        {
            var text = new StringBuilder(16384);
            text.Append("{\"i\":").Append(index).Append(",\"t\":").Append(Number(time))
                .Append(",\"m\":[");
            for (int m = 0; m < pose.muscles.Length; m++)
            {
                if (m > 0) text.Append(',');
                text.Append(Number(pose.muscles[m]));
            }
            text.Append("],\"p\":[").Append(Number(pose.bodyPosition.x)).Append(',')
                .Append(Number(pose.bodyPosition.y)).Append(',')
                .Append(Number(pose.bodyPosition.z)).Append("],\"bones\":[");
            for (int b = 0; b < bones.Count; b++)
            {
                if (b > 0) text.Append(',');
                text.Append("{\"h\":").Append(bones[b].HumanBone).Append(",\"l\":[")
                    .Append(Number(bones[b].Bone.localRotation.x)).Append(',')
                    .Append(Number(bones[b].Bone.localRotation.y)).Append(',')
                    .Append(Number(bones[b].Bone.localRotation.z)).Append(',')
                    .Append(Number(bones[b].Bone.localRotation.w)).Append("]}");
            }
            text.Append("]}");
            writer.WriteLine(text.ToString());
        }

        /// <summary>Minimal field reader for flat {"key":"value"} objects.</summary>
        private static string Field(string json, string name)
        {
            string key = "\"" + name + "\":";
            int start = json.IndexOf(key, StringComparison.Ordinal);
            if (start < 0)
            {
                key = "\"" + name + "\" :";
                start = json.IndexOf(key, StringComparison.Ordinal);
            }
            if (start < 0) return null;
            start += key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '"') return null;
            int end = json.IndexOf('"', start + 1);
            if (end < 0) return null;
            return json.Substring(start + 1, end - start - 1).Replace("\\\\", "\\");
        }

        private readonly struct BoneEntry
        {
            public readonly Transform Bone;
            public readonly int HumanBone;
            public BoneEntry(Transform bone, int humanBone) { Bone = bone; HumanBone = humanBone; }
        }

        [MenuItem("Tools/VMDCC/Export HPS Calibration")]
        private static void Export()
        {
            GameObject target = Selection.activeGameObject;
            if (target == null)
            {
                EditorUtility.DisplayDialog("VMDCC", "Select the avatar root first.", "OK");
                return;
            }
            Animator animator = target.GetComponent<Animator>() ??
                                target.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                EditorUtility.DisplayDialog("VMDCC",
                    "A valid Humanoid Avatar is required.", "OK");
                return;
            }
            string directory = EditorUtility.SaveFolderPanel("HPS calibration output",
                Path.Combine(Application.dataPath, "..", "Calibration"), string.Empty);
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                Export(animator, directory);
                EditorUtility.DisplayDialog("VMDCC",
                    "Calibration written to\n" + directory, "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("VMDCC", exception.Message, "OK");
            }
        }

        private static void Export(Animator animator, string directory)
        {
            Directory.CreateDirectory(directory);
            List<BoneEntry> bones = CollectBones(animator);
            var localPositions = new Vector3[bones.Count];
            var localRotations = new Quaternion[bones.Count];
            var localScales = new Vector3[bones.Count];
            for (int index = 0; index < bones.Count; index++)
            {
                localPositions[index] = bones[index].Bone.localPosition;
                localRotations[index] = bones[index].Bone.localRotation;
                localScales[index] = bones[index].Bone.localScale;
            }
            bool wasEnabled = animator.enabled;
            animator.enabled = false;
            try
            {
                WriteAvatarJson(animator, Path.Combine(directory, "avatar.json"));
                using (var handler = new HumanPoseHandler(animator.avatar, animator.transform))
                {
                    var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
                    handler.SetHumanPose(ref pose);
                    using (var sweep = new StreamWriter(Path.Combine(directory, "sweep.jsonl"),
                               false, new UTF8Encoding(false)) { NewLine = "\n" })
                    {
                        WritePose(sweep, "baseline", -1, 0f, handler, ref pose, bones);
                        for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                        {
                            for (int step = 0; step < SweepSteps; step++)
                            {
                                float value = -1f + 2f * step / (SweepSteps - 1);
                                Array.Clear(pose.muscles, 0, pose.muscles.Length);
                                pose.muscles[muscle] = value;
                                WritePose(sweep, "sweep", muscle, value, handler, ref pose, bones);
                            }
                            Array.Clear(pose.muscles, 0, pose.muscles.Length);
                        }
                        handler.SetHumanPose(ref pose);
                    }
                    var random = new System.Random(20261017);
                    using (var combo = new StreamWriter(Path.Combine(directory, "combo.jsonl"),
                               false, new UTF8Encoding(false)) { NewLine = "\n" })
                    {
                        for (int sample = 0; sample < ComboSamples; sample++)
                        {
                            Array.Clear(pose.muscles, 0, pose.muscles.Length);
                            if (sample % 3 == 0)
                            {
                                int first = random.Next(HumanTrait.MuscleCount);
                                int second = Math.Min(HumanTrait.MuscleCount - 1, first + 1);
                                pose.muscles[first] = RandomValue(random);
                                pose.muscles[second] = RandomValue(random);
                            }
                            else if (sample % 3 == 1)
                            {
                                int muscle = random.Next(HumanTrait.MuscleCount);
                                float value = RandomValue(random);
                                pose.muscles[muscle] = value;
                                int paired = PairMuscle(muscle);
                                if (paired >= 0) pose.muscles[paired] = value;
                            }
                            else
                            {
                                for (int muscle = 0; muscle < pose.muscles.Length; muscle++)
                                    pose.muscles[muscle] = (float)(random.NextDouble() * 0.6 - 0.3);
                            }
                            WritePose(combo, "combo", -1, 0f, handler, ref pose, bones);
                        }
                        handler.SetHumanPose(ref pose);
                    }
                }
            }
            finally
            {
                for (int index = 0; index < bones.Count; index++)
                {
                    bones[index].Bone.localPosition = localPositions[index];
                    bones[index].Bone.localRotation = localRotations[index];
                    bones[index].Bone.localScale = localScales[index];
                }
                animator.enabled = wasEnabled;
            }
            Debug.Log($"[VMDCC] calibration written for {animator.name}: {bones.Count} bones");
        }

        private static float RandomValue(System.Random random) =>
            (float)(random.NextDouble() * 2.0 - 1.0);

        private static int PairMuscle(int muscle)
        {
            string name = HumanTrait.MuscleName[muscle];
            string other = name.StartsWith("Left", StringComparison.Ordinal)
                ? "Right" + name.Substring(4)
                : name.StartsWith("Right", StringComparison.Ordinal)
                    ? "Left" + name.Substring(5) : null;
            if (other == null) return -1;
            for (int index = 0; index < HumanTrait.MuscleCount; index++)
                if (string.Equals(HumanTrait.MuscleName[index], other,
                        StringComparison.Ordinal)) return index;
            return -1;
        }

        private static List<BoneEntry> CollectBones(Animator animator)
        {
            var bones = new List<BoneEntry>();
            var seen = new HashSet<Transform>();
            for (int index = 0; index < (int)HumanBodyBones.LastBone; index++)
            {
                Transform bone = animator.GetBoneTransform((HumanBodyBones)index);
                if (bone != null && seen.Add(bone)) bones.Add(new BoneEntry(bone, index));
            }
            return bones;
        }

        private static void WritePose(StreamWriter writer, string kind, int muscle,
            float value, HumanPoseHandler handler, ref HumanPose pose, List<BoneEntry> bones)
        {
            handler.SetHumanPose(ref pose);
            handler.GetHumanPose(ref pose);
            var text = new StringBuilder(16384);
            text.Append("{\"kind\":\"").Append(kind).Append("\",\"muscle\":").Append(muscle)
                .Append(",\"value\":").Append(Number(value)).Append(",\"humanPose\":{\"p\":[")
                .Append(Number(pose.bodyPosition.x)).Append(',').Append(Number(pose.bodyPosition.y))
                .Append(',').Append(Number(pose.bodyPosition.z)).Append("],\"q\":[")
                .Append(Number(pose.bodyRotation.x)).Append(',').Append(Number(pose.bodyRotation.y))
                .Append(',').Append(Number(pose.bodyRotation.z)).Append(',')
                .Append(Number(pose.bodyRotation.w)).Append("],\"m\":[");
            for (int index = 0; index < pose.muscles.Length; index++)
            {
                if (index > 0) text.Append(',');
                text.Append(Number(pose.muscles[index]));
            }
            text.Append("]},\"bones\":[");
            for (int index = 0; index < bones.Count; index++)
            {
                BoneEntry entry = bones[index];
                if (index > 0) text.Append(',');
                text.Append("{\"h\":").Append(entry.HumanBone)
                    .Append(",\"l\":[").Append(Number(entry.Bone.localRotation.x)).Append(',')
                    .Append(Number(entry.Bone.localRotation.y)).Append(',')
                    .Append(Number(entry.Bone.localRotation.z)).Append(',')
                    .Append(Number(entry.Bone.localRotation.w)).Append("],\"w\":[")
                    .Append(Number(entry.Bone.rotation.x)).Append(',')
                    .Append(Number(entry.Bone.rotation.y)).Append(',')
                    .Append(Number(entry.Bone.rotation.z)).Append(',')
                    .Append(Number(entry.Bone.rotation.w)).Append("]}");
            }
            text.Append("]}");
            writer.WriteLine(text.ToString());
        }

        private static string Number(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static void WriteAvatarJson(Animator animator, string path)
        {
            Avatar avatar = animator.avatar;
            HumanDescription description = avatar.humanDescription;
            var text = new StringBuilder(32768);
            text.Append("{\"version\":1,\"avatar\":\"").Append(Escape(animator.name))
                .Append("\",\"unityVersion\":\"").Append(Escape(Application.unityVersion))
                .Append("\",\"humanScale\":0")
                .Append(",\"upAxis\":\"Y\",\"parameters\":{")
                .Append("\"armStretch\":").Append(Number(description.armStretch))
                .Append(",\"legStretch\":").Append(Number(description.legStretch))
                .Append(",\"upperArmTwist\":").Append(Number(description.upperArmTwist))
                .Append(",\"lowerArmTwist\":").Append(Number(description.lowerArmTwist))
                .Append(",\"upperLegTwist\":").Append(Number(description.upperLegTwist))
                .Append(",\"lowerLegTwist\":").Append(Number(description.lowerLegTwist))
                .Append(",\"feetSpacing\":").Append(Number(description.feetSpacing))
                .Append(",\"hasTranslationDoF\":")
                .Append(description.hasTranslationDoF ? "true" : "false").Append("}");
            text.Append(",\"boneMap\":{");
            for (int index = 0; index < description.human.Length; index++)
            {
                if (index > 0) text.Append(',');
                text.Append('"').Append(Escape(description.human[index].humanName))
                    .Append("\":\"").Append(Escape(description.human[index].boneName))
                    .Append('"');
            }
            text.Append("},\"skeleton\":[");
            for (int index = 0; index < description.skeleton.Length; index++)
            {
                SkeletonBone bone = description.skeleton[index];
                if (index > 0) text.Append(',');
                text.Append("{\"path\":\"").Append(Escape(bone.name)).Append("\",\"p\":[")
                    .Append(Number(bone.position.x)).Append(',').Append(Number(bone.position.y))
                    .Append(',').Append(Number(bone.position.z)).Append("],\"q\":[")
                    .Append(Number(bone.rotation.x)).Append(',').Append(Number(bone.rotation.y))
                    .Append(',').Append(Number(bone.rotation.z)).Append(',')
                    .Append(Number(bone.rotation.w)).Append("],\"s\":[")
                    .Append(Number(bone.scale.x)).Append(',').Append(Number(bone.scale.y))
                    .Append(',').Append(Number(bone.scale.z)).Append("]}");
            }
            text.Append("],\"muscles\":[");
            for (int index = 0; index < HumanTrait.MuscleCount; index++)
            {
                // min/max are Unity constants (HumanTrait), not per-avatar data; the
                // offline side owns them. Only the name and owning bone are exported.
                if (index > 0) text.Append(',');
                text.Append("{\"index\":").Append(index).Append(",\"name\":\"")
                    .Append(Escape(HumanTrait.MuscleName[index]))
                    .Append("\",\"bone\":").Append(HumanTrait.BoneFromMuscle(index))
                    .Append(",\"min\":0,\"max\":0}");
            }
            text.Append("]}");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        private static string Escape(string value) => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
