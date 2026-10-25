using System;
using UnityEditor;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    internal static class VmdCoreAssetPaths
    {
        internal static void EnsureAssetFolderExists(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            if (!string.Equals(folder, "Assets", StringComparison.Ordinal) &&
                !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("The storage folder must be inside Assets.", nameof(folder));
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int index = 1; index < parts.Length; index++)
            {
                string next = current + "/" + parts[index];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[index]);
                current = next;
            }
        }
    }
}
