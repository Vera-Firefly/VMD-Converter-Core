using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    /// <summary>Unity-only inputs needed to export the worker's target rig contract.</summary>
    [Serializable]
    public sealed class VmdRigExportOptions
    {
        public float SampleRate = 30f;
        public float PositionScale = 1f;
        public int CoordinateConversion = 2;
        public float RotationErrorDegrees = 0.01f;
        public float PositionError = 0.00005f;
        public float MuscleError = 0.0001f;
        public bool BakeRootTranslation = true;
        public bool BakeMorphs = true;
        public VmdExternalOutputKind OutputKind = VmdExternalOutputKind.TargetFk;
    }

    /// <summary>
    /// Exports a Unity-imported Humanoid GameObject or FBX model to the static rig
    /// JSON read by VmdConverterCore.exe. This API belongs to VMDCC and uses Unity
    /// Avatar APIs only once per target, never in the EXE's frame loop.
    /// </summary>
    public static class VmdTargetRigExporter
    {
        private sealed class BoneRule
        {
            internal readonly HumanBodyBones Bone;
            internal readonly string[] Candidates;
            internal BoneRule(HumanBodyBones bone, params string[] candidates)
            {
                Bone = bone;
                Candidates = candidates;
            }
        }

        private static BoneRule Rule(HumanBodyBones bone, params string[] names) =>
            new BoneRule(bone, names);

        // Keep candidate order aligned with the PMX Humanoid source used by the
        // built-in parser; the worker resolves the first matching PMX bone.
        private static readonly BoneRule[] BoneRules =
        {
            Rule(HumanBodyBones.Hips, "下半身", "lower body", "LowerBody", "腰", "waist", "Waist", "hips", "Hips"),
            Rule(HumanBodyBones.Spine, "上半身", "upper body", "UpperBody", "spine", "Spine"),
            Rule(HumanBodyBones.Chest, "上半身2", "上半身２", "upper body2", "UpperBody2", "chest", "Chest"),
            Rule(HumanBodyBones.UpperChest, "上半身3", "上半身３", "upper body3", "UpperBody3", "upper chest", "UpperChest"),
            Rule(HumanBodyBones.Neck, "首", "neck", "Neck"),
            Rule(HumanBodyBones.Head, "頭", "head", "Head"),
            Rule(HumanBodyBones.LeftEye, "左目", "left eye", "LeftEye"),
            Rule(HumanBodyBones.RightEye, "右目", "right eye", "RightEye"),
            Rule(HumanBodyBones.Jaw, "あご", "顎", "jaw", "Jaw"),
            Rule(HumanBodyBones.LeftShoulder, "左肩", "left shoulder", "LeftShoulder"),
            Rule(HumanBodyBones.LeftUpperArm, "左腕", "left arm", "LeftArm", "LeftUpperArm"),
            Rule(HumanBodyBones.LeftLowerArm, "左ひじ", "左肘", "left elbow", "LeftElbow", "LeftLowerArm"),
            Rule(HumanBodyBones.LeftHand, "左手首", "left wrist", "LeftWrist", "LeftHand"),
            Rule(HumanBodyBones.RightShoulder, "右肩", "right shoulder", "RightShoulder"),
            Rule(HumanBodyBones.RightUpperArm, "右腕", "right arm", "RightArm", "RightUpperArm"),
            Rule(HumanBodyBones.RightLowerArm, "右ひじ", "右肘", "right elbow", "RightElbow", "RightLowerArm"),
            Rule(HumanBodyBones.RightHand, "右手首", "right wrist", "RightWrist", "RightHand"),
            Rule(HumanBodyBones.LeftUpperLeg, "左足D", "左足Ｄ", "左足", "left leg", "LeftLeg", "LeftUpperLeg"),
            Rule(HumanBodyBones.LeftLowerLeg, "左ひざD", "左膝D", "左ひざＤ", "左ひざ", "左膝", "left knee", "LeftKnee", "LeftLowerLeg"),
            Rule(HumanBodyBones.LeftFoot, "左足首D", "左足首Ｄ", "左足首", "left ankle", "LeftAnkle", "LeftFoot"),
            Rule(HumanBodyBones.LeftToes, "左つま先EX", "左つま先", "left toe", "LeftToe", "LeftToes"),
            Rule(HumanBodyBones.RightUpperLeg, "右足D", "右足Ｄ", "右足", "right leg", "RightLeg", "RightUpperLeg"),
            Rule(HumanBodyBones.RightLowerLeg, "右ひざD", "右膝D", "右ひざＤ", "右ひざ", "右膝", "right knee", "RightKnee", "RightLowerLeg"),
            Rule(HumanBodyBones.RightFoot, "右足首D", "右足首Ｄ", "右足首", "right ankle", "RightAnkle", "RightFoot"),
            Rule(HumanBodyBones.RightToes, "右つま先EX", "右つま先", "right toe", "RightToe", "RightToes"),
            Rule(HumanBodyBones.LeftThumbIntermediate, "左親指1", "左親指１", "left thumb1", "LeftThumb1"),
            Rule(HumanBodyBones.LeftThumbDistal, "左親指2", "左親指２", "left thumb2", "LeftThumb2"),
            Rule(HumanBodyBones.LeftIndexProximal, "左人指1", "左人指１", "left index1", "LeftIndex1"),
            Rule(HumanBodyBones.LeftIndexIntermediate, "左人指2", "左人指２", "left index2", "LeftIndex2"),
            Rule(HumanBodyBones.LeftIndexDistal, "左人指3", "左人指３", "left index3", "LeftIndex3"),
            Rule(HumanBodyBones.LeftMiddleProximal, "左中指1", "左中指１", "left middle1", "LeftMiddle1"),
            Rule(HumanBodyBones.LeftMiddleIntermediate, "左中指2", "左中指２", "left middle2", "LeftMiddle2"),
            Rule(HumanBodyBones.LeftMiddleDistal, "左中指3", "左中指３", "left middle3", "LeftMiddle3"),
            Rule(HumanBodyBones.LeftRingProximal, "左薬指1", "左薬指１", "left ring1", "LeftRing1"),
            Rule(HumanBodyBones.LeftRingIntermediate, "左薬指2", "左薬指２", "left ring2", "LeftRing2"),
            Rule(HumanBodyBones.LeftRingDistal, "左薬指3", "左薬指３", "left ring3", "LeftRing3"),
            Rule(HumanBodyBones.LeftLittleProximal, "左小指1", "左小指１", "left little1", "LeftLittle1"),
            Rule(HumanBodyBones.LeftLittleIntermediate, "左小指2", "左小指２", "left little2", "LeftLittle2"),
            Rule(HumanBodyBones.LeftLittleDistal, "左小指3", "左小指３", "left little3", "LeftLittle3"),
            Rule(HumanBodyBones.RightThumbIntermediate, "右親指1", "右親指１", "right thumb1", "RightThumb1"),
            Rule(HumanBodyBones.RightThumbDistal, "右親指2", "右親指２", "right thumb2", "RightThumb2"),
            Rule(HumanBodyBones.RightIndexProximal, "右人指1", "右人指１", "right index1", "RightIndex1"),
            Rule(HumanBodyBones.RightIndexIntermediate, "右人指2", "右人指２", "right index2", "RightIndex2"),
            Rule(HumanBodyBones.RightIndexDistal, "右人指3", "右人指３", "right index3", "RightIndex3"),
            Rule(HumanBodyBones.RightMiddleProximal, "右中指1", "右中指１", "right middle1", "RightMiddle1"),
            Rule(HumanBodyBones.RightMiddleIntermediate, "右中指2", "右中指２", "right middle2", "RightMiddle2"),
            Rule(HumanBodyBones.RightMiddleDistal, "右中指3", "右中指３", "right middle3", "RightMiddle3"),
            Rule(HumanBodyBones.RightRingProximal, "右薬指1", "右薬指１", "right ring1", "RightRing1"),
            Rule(HumanBodyBones.RightRingIntermediate, "右薬指2", "右薬指２", "right ring2", "RightRing2"),
            Rule(HumanBodyBones.RightRingDistal, "右薬指3", "右薬指３", "right ring3", "RightRing3"),
            Rule(HumanBodyBones.RightLittleProximal, "右小指1", "右小指１", "right little1", "RightLittle1"),
            Rule(HumanBodyBones.RightLittleIntermediate, "右小指2", "右小指２", "right little2", "RightLittle2"),
            Rule(HumanBodyBones.RightLittleDistal, "右小指3", "右小指３", "right little3", "RightLittle3"),
            Rule(HumanBodyBones.LeftThumbProximal, "左親指０", "左親指0", "left thumb0", "LeftThumb0"),
            Rule(HumanBodyBones.RightThumbProximal, "右親指０", "右親指0", "right thumb0", "RightThumb0")
        };

        public static VmdTargetRigData Export(GameObject targetRoot,
            VmdRigExportOptions options)
        {
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            if (options == null) throw new ArgumentNullException(nameof(options));
            Animator animator = targetRoot.GetComponent<Animator>() ??
                                targetRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("A valid Humanoid target Avatar is required.");
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || !hips.IsChildOf(targetRoot.transform))
                throw new InvalidOperationException("The target needs a Hips mapping.");
            Dictionary<string, SkeletonBone> skeleton = BuildSkeletonLookup(animator.avatar);
            var restWorld = new Dictionary<Transform, Quaternion>();
            var restPositions = new Dictionary<Transform, Vector3>();
            var targetBindings = new List<VmdTargetBoneBinding>();
            var targets = new List<Transform>();
            var used = new HashSet<Transform>();
            foreach (BoneRule rule in BoneRules)
            {
                Transform target = animator.GetBoneTransform(rule.Bone);
                if (target == null || !target.IsChildOf(targetRoot.transform) ||
                    !used.Add(target)) continue;
                SkeletonBone restBone;
                Quaternion localRest = skeleton.TryGetValue(target.name, out restBone)
                    ? restBone.rotation : target.localRotation;
                Vector3 positionRest = skeleton.TryGetValue(target.name, out restBone)
                    ? restBone.position : target.localPosition;
                Matrix4x4 parentSpace = target.parent != null
                    ? target.parent.worldToLocalMatrix * targetRoot.transform.localToWorldMatrix
                    : Matrix4x4.identity;
                targetBindings.Add(new VmdTargetBoneBinding
                {
                    path = AnimationUtility.CalculateTransformPath(target,
                        targetRoot.transform),
                    sourceBoneIndex = -1,
                    sourceCandidates = rule.Candidates,
                    humanBone = (int)rule.Bone,
                    syntheticThumbProximal = target.name == "__LeftThumb0M__" ||
                                              target.name == "__RightThumb0M__",
                    restLocalRotation = Normalize(localRest),
                    restWorldRotation = GetRestWorldRotation(target,
                        targetRoot.transform, skeleton, restWorld),
                    restWorldPosition = GetRestWorldPosition(target,
                        targetRoot.transform, skeleton, restWorld, restPositions),
                    restParentWorldRotation = target.parent != null
                        ? GetRestWorldRotation(target.parent, targetRoot.transform,
                            skeleton, restWorld) : Quaternion.identity,
                    restLocalPosition = positionRest,
                    parentAxisX = parentSpace.MultiplyVector(Vector3.right),
                    parentAxisY = parentSpace.MultiplyVector(Vector3.up),
                    parentAxisZ = parentSpace.MultiplyVector(Vector3.forward)
                });
                targets.Add(target);
            }
            var targetIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < targets.Count; i++) targetIndex[targets[i]] = i;
            for (int i = 0; i < targets.Count; i++)
                for (Transform parent = targets[i].parent;
                     parent != null && parent != targetRoot.transform;
                     parent = parent.parent)
                    if (targetIndex.TryGetValue(parent, out int index))
                    {
                        targetBindings[i].parentBindingIndex = index;
                        break;
                    }
            var rig = new VmdTargetRigData
            {
                sampleRate = options.SampleRate,
                positionScale = options.PositionScale,
                coordinateConversion = options.CoordinateConversion,
                rotationErrorDegrees = options.RotationErrorDegrees,
                positionError = options.PositionError,
                muscleError = options.MuscleError,
                bakeRootTranslation = options.BakeRootTranslation,
                bakeMorphs = options.BakeMorphs,
                targetHipsHeight = Mathf.Abs(hips.position.y - animator.transform.position.y),
                outputKind = options.OutputKind,
                bones = targetBindings.ToArray()
            };
            var morphs = new List<VmdTargetMorphBinding>();
            foreach (SkinnedMeshRenderer renderer in
                     targetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                string path = AnimationUtility.CalculateTransformPath(renderer.transform,
                    targetRoot.transform);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    morphs.Add(new VmdTargetMorphBinding
                    {
                        name = mesh.GetBlendShapeName(i),
                        path = path
                    });
            }
            rig.morphs = morphs.ToArray();
            if (options.OutputKind != VmdExternalOutputKind.TargetFk)
                rig.humanoid = VmdHumanoidCalibrationExporter.Export(targetRoot,
                    rig.bones);
            return rig;
        }

        public static string ExportJson(GameObject targetRoot, string outputPath,
            VmdRigExportOptions options)
        {
            VmdTargetRigData rig = Export(targetRoot, options);
            WriteJson(rig, outputPath);
            return Path.GetFullPath(outputPath);
        }

        /// <summary>
        /// Adds calibration for the canonical PMX Humanoid source. The caller
        /// supplies its already constructed GameObject in the PMX rest pose.
        /// </summary>
        public static void AttachCanonical(VmdTargetRigData targetRig,
            GameObject canonicalRoot, VmdRigExportOptions options)
        {
            if (targetRig == null) throw new ArgumentNullException(nameof(targetRig));
            if (canonicalRoot == null)
                throw new ArgumentNullException(nameof(canonicalRoot));
            if (options == null) throw new ArgumentNullException(nameof(options));
            var canonicalOptions = new VmdRigExportOptions
            {
                SampleRate = options.SampleRate,
                PositionScale = options.PositionScale,
                CoordinateConversion = options.CoordinateConversion,
                RotationErrorDegrees = options.RotationErrorDegrees,
                PositionError = options.PositionError,
                MuscleError = options.MuscleError,
                BakeRootTranslation = options.BakeRootTranslation,
                BakeMorphs = false,
                OutputKind = VmdExternalOutputKind.HumanoidIk
            };
            VmdTargetRigData canonical = Export(canonicalRoot, canonicalOptions);
            Animator animator = canonicalRoot.GetComponent<Animator>() ??
                                canonicalRoot.GetComponentInChildren<Animator>(true);
            using (var handler = new HumanPoseHandler(animator.avatar,
                       canonicalRoot.transform))
            {
                var pose = new HumanPose();
                handler.GetHumanPose(ref pose);
                canonical.humanoid.restBodyPosition = pose.bodyPosition;
                canonical.humanoid.restBodyRotation = pose.bodyRotation;
            }
            targetRig.canonicalRig = canonical;
        }

        public static void WriteJson(VmdTargetRigData rig, string outputPath)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("A rig output path is required.",
                    nameof(outputPath));
            string path = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = JsonUtility.ToJson(rig, true);
            if (rig.canonicalRig != null)
            {
                if (rig.canonicalRig.canonicalRig != null)
                    throw new InvalidOperationException(
                        "Canonical rigs cannot contain another canonical rig.");
                // JsonUtility silently omits a field whose type recursively
                // matches the containing type. Serialize the second rig as a
                // root object, then add it to the outer JSON explicitly.
                if (json.Contains("\"canonicalRig\""))
                    throw new InvalidOperationException(
                        "Unexpected canonicalRig field in Unity JSON.");
                int close = json.LastIndexOf('}');
                if (close < 0)
                    throw new InvalidOperationException("Invalid rig JSON.");
                string canonical = JsonUtility.ToJson(rig.canonicalRig, true)
                    .Replace("\n", "\n  ");
                json = json.Substring(0, close).TrimEnd() +
                       ",\n  \"canonicalRig\": " + canonical + "\n}";
            }
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private static Dictionary<string, SkeletonBone> BuildSkeletonLookup(Avatar avatar)
        {
            var result = new Dictionary<string, SkeletonBone>(StringComparer.Ordinal);
            try
            {
                foreach (SkeletonBone bone in avatar.humanDescription.skeleton)
                    if (!string.IsNullOrEmpty(bone.name) && !result.ContainsKey(bone.name))
                        result.Add(bone.name, bone);
            }
            catch (Exception)
            {
                // Generated Avatars can use scene-local transforms as a fallback.
            }
            return result;
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float length = Mathf.Sqrt(value.x * value.x + value.y * value.y +
                                      value.z * value.z + value.w * value.w);
            return length > 1e-8f
                ? new Quaternion(value.x / length, value.y / length,
                    value.z / length, value.w / length)
                : Quaternion.identity;
        }

        private static Quaternion GetRestWorldRotation(Transform target,
            Transform root, IReadOnlyDictionary<string, SkeletonBone> skeleton,
            IDictionary<Transform, Quaternion> cache)
        {
            if (target == null || target == root) return Quaternion.identity;
            if (cache.TryGetValue(target, out Quaternion cached)) return cached;
            Quaternion parent = GetRestWorldRotation(target.parent, root, skeleton, cache);
            Quaternion local = skeleton.TryGetValue(target.name, out SkeletonBone bone)
                ? bone.rotation : target.localRotation;
            Quaternion result = Normalize(parent * local);
            cache[target] = result;
            return result;
        }

        private static Vector3 GetRestWorldPosition(Transform target,
            Transform root, IReadOnlyDictionary<string, SkeletonBone> skeleton,
            IDictionary<Transform, Quaternion> rotations,
            IDictionary<Transform, Vector3> positions)
        {
            if (target == null || target == root) return Vector3.zero;
            if (positions.TryGetValue(target, out Vector3 cached)) return cached;
            Vector3 parent = GetRestWorldPosition(target.parent, root,
                skeleton, rotations, positions);
            Quaternion parentRotation = GetRestWorldRotation(target.parent,
                root, skeleton, rotations);
            Vector3 local = skeleton.TryGetValue(target.name, out SkeletonBone bone)
                ? bone.position : target.localPosition;
            Vector3 result = parent + parentRotation * local;
            positions[target] = result;
            return result;
        }
    }
}
