using System;
using System.Collections.Generic;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    internal static class VmdHumanoidCalibrationExporter
    {
        internal static VmdHumanoidCalibrationData Export(GameObject targetRoot,
            IReadOnlyList<VmdTargetBoneBinding> bindings)
        {
            Animator animator = targetRoot.GetComponent<Animator>() ??
                                targetRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("A valid Humanoid Avatar is required.");
            Dictionary<Transform, Transform> clones =
                CloneHierarchy(animator, out GameObject cloneRoot);
            try
            {
                using (var handler = new HumanPoseHandler(animator.avatar, cloneRoot.transform))
                {
                    var pose = new HumanPose();
                    handler.GetHumanPose(ref pose);
                    pose.muscles = new float[HumanTrait.MuscleCount];
                    handler.SetHumanPose(ref pose);
                    handler.GetHumanPose(ref pose);
                    Array.Clear(pose.muscles, 0, pose.muscles.Length);
                    handler.SetHumanPose(ref pose);
                    var result = new VmdHumanoidCalibrationData
                    {
                        restBodyPosition = pose.bodyPosition,
                        restBodyRotation = pose.bodyRotation,
                        bodyInfluences = new VmdBodyPositionInfluence[bindings.Count],
                        muscles = new VmdMuscleCalibration[HumanTrait.MuscleCount]
                    };
                    Transform sourceHips = animator.GetBoneTransform(HumanBodyBones.Hips);
                    if (sourceHips == null || !clones.TryGetValue(sourceHips,
                            out Transform hips))
                        throw new InvalidOperationException("Target Hips could not be cloned.");
                    result.zeroHipsWorldRotation = hips.rotation;
                    Vector3 initialHipsPosition = hips.localPosition;
                    const float step = 0.01f;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        Vector3 moved = initialHipsPosition;
                        moved[axis] += step;
                        hips.localPosition = moved;
                        var shifted = new HumanPose();
                        handler.GetHumanPose(ref shifted);
                        Vector3 response = (shifted.bodyPosition -
                            result.restBodyPosition) / step;
                        ValidateResponse(response, "Hips", axis);
                        if (axis == 0) result.bodyPositionAxisX = response;
                        else if (axis == 1) result.bodyPositionAxisY = response;
                        else result.bodyPositionAxisZ = response;
                    }
                    hips.localPosition = initialHipsPosition;
                    for (int index = 0; index < bindings.Count; index++)
                    {
                        int humanBone = bindings[index].humanBone;
                        Transform original = animator.GetBoneTransform((HumanBodyBones)humanBone);
                        if (original == null || !clones.TryGetValue(original,
                                out Transform bone)) continue;
                        result.bodyInfluences[index] = MeasureBodyInfluence(
                            handler, cloneRoot.transform, bone,
                            result.restBodyPosition, index);
                    }
                    handler.SetHumanPose(ref pose);
                    var bindingByBone = new Dictionary<int, int>();
                    for (int i = 0; i < bindings.Count; i++)
                        bindingByBone[bindings[i].humanBone] = i;
                    for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                    {
                        int humanBone = HumanTrait.BoneFromMuscle(muscle);
                        if (!bindingByBone.TryGetValue(humanBone,
                                out int bindingIndex)) continue;
                        Transform source = animator.GetBoneTransform((HumanBodyBones)humanBone);
                        if (source == null || !clones.TryGetValue(source,
                                out Transform bone)) continue;
                        Quaternion zero = bone.localRotation;
                        pose.muscles[muscle] = 1f;
                        handler.SetHumanPose(ref pose);
                        Quaternion positive = Quaternion.Inverse(zero) *
                                              bone.localRotation;
                        pose.muscles[muscle] = -1f;
                        handler.SetHumanPose(ref pose);
                        Quaternion negative = Quaternion.Inverse(zero) *
                                              bone.localRotation;
                        pose.muscles[muscle] = 0f;
                        handler.SetHumanPose(ref pose);
                        result.muscles[muscle] = new VmdMuscleCalibration
                        {
                            bindingIndex = bindingIndex,
                            property = GetAnimatorMusclePropertyName(muscle),
                            zeroLocalRotation = zero,
                            positiveDelta = Normalize(positive),
                            negativeDelta = Normalize(negative)
                        };
                    }
                    return result;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cloneRoot);
            }
        }

        private static Quaternion Normalize(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y +
                                      q.z * q.z + q.w * q.w);
            return length > 1e-8f
                ? new Quaternion(q.x / length, q.y / length,
                    q.z / length, q.w / length)
                : Quaternion.identity;
        }

        private static VmdBodyPositionInfluence MeasureBodyInfluence(
            HumanPoseHandler handler, Transform root, Transform bone,
            Vector3 restBodyPosition, int bindingIndex)
        {
            const float step = 0.01f;
            var result = new VmdBodyPositionInfluence
            {
                bindingIndex = bindingIndex
            };
            Vector3 original = bone.position;
            var children = new Transform[bone.childCount];
            var childPositions = new Vector3[bone.childCount];
            for (int child = 0; child < bone.childCount; child++)
            {
                children[child] = bone.GetChild(child);
                childPositions[child] = children[child].position;
            }
            for (int axis = 0; axis < 3; axis++)
            {
                try
                {
                    Vector3 direction = axis == 0 ? Vector3.right
                        : axis == 1 ? Vector3.up : Vector3.forward;
                    bone.position = original + root.TransformVector(direction * step);
                    for (int child = 0; child < children.Length; child++)
                        children[child].position = childPositions[child];
                    var shifted = new HumanPose();
                    handler.GetHumanPose(ref shifted);
                    Vector3 response = (shifted.bodyPosition - restBodyPosition) / step;
                    ValidateResponse(response, bone.name, axis);
                    if (axis == 0) result.axisX = response;
                    else if (axis == 1) result.axisY = response;
                    else result.axisZ = response;
                }
                finally
                {
                    bone.position = original;
                    for (int child = 0; child < children.Length; child++)
                        children[child].position = childPositions[child];
                }
            }
            return result;
        }

        private static void ValidateResponse(Vector3 value, string bone, int axis)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                throw new InvalidOperationException(
                    "Invalid Humanoid body calibration for " + bone +
                    " axis " + axis + ".");
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static string GetAnimatorMusclePropertyName(int index)
        {
            string name = HumanTrait.MuscleName[index];
            if (string.IsNullOrEmpty(name)) return name;
            string hand;
            string remainder;
            if (name.StartsWith("Left ", StringComparison.Ordinal))
            {
                hand = "LeftHand.";
                remainder = name.Substring(5);
            }
            else if (name.StartsWith("Right ", StringComparison.Ordinal))
            {
                hand = "RightHand.";
                remainder = name.Substring(6);
            }
            else return name;
            foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                if (remainder.StartsWith(finger + " ", StringComparison.Ordinal))
                    return hand + finger + "." + remainder.Substring(finger.Length + 1);
            return name;
        }

        private static Dictionary<string, SkeletonBone> BuildSkeletonLookup(Avatar avatar)
        {
            var skeleton = new Dictionary<string, SkeletonBone>(StringComparer.Ordinal);
            try
            {
                foreach (SkeletonBone bone in avatar.humanDescription.skeleton)
                    if (!string.IsNullOrEmpty(bone.name) && !skeleton.ContainsKey(bone.name))
                        skeleton.Add(bone.name, bone);
            }
            catch (Exception)
            {
                // Scene-local transforms remain a valid fallback for generated Avatars.
            }
            return skeleton;
        }

        private static Dictionary<Transform, Transform> CloneHierarchy(
            Animator animator, out GameObject cloneRoot)
        {
            Transform sourceRoot = animator.transform;
            cloneRoot = new GameObject(sourceRoot.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            cloneRoot.transform.localPosition = Vector3.zero;
            cloneRoot.transform.localRotation = Quaternion.identity;
            cloneRoot.transform.localScale = Vector3.one;
            var result = new Dictionary<Transform, Transform>
            {
                [sourceRoot] = cloneRoot.transform
            };
            Dictionary<string, SkeletonBone> skeleton =
                BuildSkeletonLookup(animator.avatar);
            var required = new HashSet<Transform> { sourceRoot };
            for (int index = 0; index < (int)HumanBodyBones.LastBone; index++)
                AddWithAncestors(animator.GetBoneTransform((HumanBodyBones)index),
                    sourceRoot, required);
            foreach (Transform transform in
                     sourceRoot.GetComponentsInChildren<Transform>(true))
                if (skeleton.ContainsKey(transform.name))
                    AddWithAncestors(transform, sourceRoot, required);
            CloneChildren(sourceRoot, cloneRoot.transform, skeleton, required, result);
            return result;
        }

        private static void AddWithAncestors(Transform transform, Transform root,
            ISet<Transform> required)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                required.Add(current);
                if (current == root) break;
            }
        }

        private static void CloneChildren(Transform source, Transform parent,
            IReadOnlyDictionary<string, SkeletonBone> skeleton,
            ISet<Transform> required,
            IDictionary<Transform, Transform> result)
        {
            for (int index = 0; index < source.childCount; index++)
            {
                Transform child = source.GetChild(index);
                if (!required.Contains(child)) continue;
                var cloneObject = new GameObject(child.name)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Transform clone = cloneObject.transform;
                clone.SetParent(parent, false);
                if (skeleton.TryGetValue(child.name, out SkeletonBone rest))
                {
                    clone.localPosition = rest.position;
                    clone.localRotation = rest.rotation;
                    clone.localScale = rest.scale;
                }
                else
                {
                    clone.localPosition = child.localPosition;
                    clone.localRotation = child.localRotation;
                    clone.localScale = child.localScale;
                }
                result[child] = clone;
                CloneChildren(child, clone, skeleton, required, result);
            }
        }
    }
}
