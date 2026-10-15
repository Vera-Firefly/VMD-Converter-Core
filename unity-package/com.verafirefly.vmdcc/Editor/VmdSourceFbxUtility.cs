using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    /// <summary>Editor selection adapter only; no pose or curve generation.</summary>
    public static class VmdSourceFbxUtility
    {
        public static string FindSourceFbx(GameObject target)
        {
            if (target == null) return null;
            foreach (Animator animator in target.GetComponentsInChildren<Animator>(true))
            {
                string path = animator.avatar == null ? null : AssetDatabase.GetAssetPath(animator.avatar);
                if (IsFbx(path)) return Physical(path);
            }
            foreach (Transform transform in target.GetComponentsInChildren<Transform>(true))
            {
                var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(transform.gameObject);
                string path = AssetDatabase.GetAssetPath(original != null ? original : transform.gameObject);
                if (IsFbx(path)) return Physical(path);
            }
            return null;
        }
        private static bool IsFbx(string path) => !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase);
        private static string Physical(string path) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
    }
}
