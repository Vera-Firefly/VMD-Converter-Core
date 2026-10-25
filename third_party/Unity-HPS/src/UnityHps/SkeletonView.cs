using System.Numerics;

namespace UnityHps;

/// <summary>
/// Complete imported FBX hierarchy for transform-clip retargeting. Unlike a
/// Humanoid Rig, this view preserves rest-only helpers and non-human nodes.
/// Paths are relative to the imported scene root, matching Unity clip paths.
/// </summary>
public sealed class SkeletonView
{
    public FbxDocument Document { get; }
    public Bone[] Bones => Document.bones;
    public string[] Paths { get; }
    public Vector3[] RestWorldPositions { get; }
    public Quaternion[] RestWorldRotations { get; }

    private SkeletonView(FbxDocument document, string[] paths, SkeletonPose rest)
    {
        Document = document;
        Paths = paths;
        RestWorldPositions = rest.WorldPositions;
        RestWorldRotations = rest.WorldRotations;
    }

    public static SkeletonView FromDocument(FbxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Bone[] bones = document.bones;
        if (bones.Length == 0) throw new InvalidDataException("FBX skeleton is empty.");
        var paths = new string[bones.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < bones.Length; i++)
        {
            int parent = bones[i].parent;
            if (parent >= i || parent < -1)
                throw new InvalidDataException("FBX bones must be in parent-before-child order.");
            if (parent < 0) { paths[i] = string.Empty; continue; }
            if (string.IsNullOrWhiteSpace(bones[i].name) || bones[i].name.Contains('/'))
                throw new InvalidDataException("FBX node name cannot form a Unity animation path: " + bones[i].name);
            paths[i] = paths[parent].Length == 0 ? bones[i].name : paths[parent] + "/" + bones[i].name;
            if (!seen.Add(paths[i])) throw new InvalidDataException("Duplicate FBX animation path: " + paths[i]);
        }
        return new SkeletonView(document, paths, new SkeletonPose(new Rig { bones = bones }));
    }

    public static SkeletonView FromFbx(string path, FbxImportOptions? options = null) =>
        FromDocument(FbxReader.Read(path, options));
}
