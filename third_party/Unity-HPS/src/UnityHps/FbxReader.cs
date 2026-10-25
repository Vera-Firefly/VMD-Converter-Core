using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityHps;

public sealed class FbxDocument
{
    public string source { get; set; } = "";
    public Bone[] bones { get; set; } = [];
    public MorphBinding[] morphs { get; set; } = [];
    public bool usedUnityMeta { get; set; }
    public float unitScale { get; set; } = 1;
}
public sealed class MorphBinding
{
    public string name { get; set; } = "";
    public string path { get; set; } = "Body";
}
public sealed class FbxImportOptions
{
    public string? MetaPath { get; set; }
    public string? BoneMapPath { get; set; }
    public bool IgnoreMeta { get; set; }
    public float Scale { get; set; } = 1;
    /// <summary>Receive recoverable import warnings; null writes them to standard error.</summary>
    public Action<string>? Warning { get; set; }
}

/// <summary>Native ufbx C parser; no Autodesk SDK, Python, or Unity required.</summary>
public static class FbxReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public int parent, isBone;
        public double px, py, pz, qx, qy, qz, qw, sx, sy, sz;
    }
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr hps_fbx_load(byte[] bytes, nuint size, StringBuilder message, nuint capacity);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern void hps_fbx_free(IntPtr scene);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern int hps_fbx_count(IntPtr scene);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr hps_fbx_name(IntPtr scene, int index);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern int hps_fbx_node(IntPtr scene, int index, out Node node);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern int hps_fbx_morph_count(IntPtr scene);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr hps_fbx_morph_name(IntPtr scene, int index);
    [DllImport("UnityHps.Fbx", CallingConvention = CallingConvention.Cdecl)] private static extern int hps_fbx_morph_node(IntPtr scene, int index);

    public static FbxDocument Read(string path, FbxImportOptions? options = null)
    {
        options ??= new();
        if (!float.IsFinite(options.Scale) || options.Scale <= 0) throw new ArgumentException("FBX scale must be finite and positive.");
        byte[] bytes = File.ReadAllBytes(path);
        var error = new StringBuilder(4096);
        IntPtr scene = hps_fbx_load(bytes, (nuint)bytes.Length, error, 4096);
        if (scene == IntPtr.Zero) throw new InvalidDataException("FBX: " + error);
        try
        {
            int count = hps_fbx_count(scene);
            if (count < 2 || count > 100000) throw new InvalidDataException("Unsupported FBX node count.");
            var raw = new Node[count]; var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                if (hps_fbx_node(scene, i, out raw[i]) == 0) throw new InvalidDataException("Could not read FBX node.");
                names[i] = Marshal.PtrToStringUTF8(hps_fbx_name(scene, i)) ?? "";
                if (raw[i].parent < 0) names[i] = Path.GetFileNameWithoutExtension(path);
            }
            string? metaPath = options.IgnoreMeta ? null : options.MetaPath ?? (File.Exists(path + ".meta") ? path + ".meta" : null);
            var meta = metaPath == null ? new UnityFbxMeta() : UnityFbxMeta.Read(metaPath);
            if (meta.HasCustomMuscleLimits)
            {
                string warning = "FBX metadata contains custom Humanoid muscle limits. Conversion continues with default profile limits; " +
                    "accuracy for these settings is unverified. Use an exported --hps-rig for Avatar-specific responses.";
                if (options.Warning != null) options.Warning(warning);
                else Console.Error.WriteLine("Warning: " + warning);
            }
            var map = new Dictionary<string, string>(meta.HumanBones, StringComparer.Ordinal);
            if (options.BoneMapPath != null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(options.BoneMapPath));
                foreach (var property in doc.RootElement.EnumerateObject()) map[property.Name] = property.Value.GetString() ?? throw new InvalidDataException("Bone map values must be node names.");
            }
            var ordered = new List<int>(); var visited = new byte[count];
            var required = new HashSet<int>();
            for (int i = 0; i < count; i++)
                if (map.Values.Contains(names[i]) || map.Count == 0 && HumanBones.Guess(names[i]) >= 0)
                    for (int p = i; p >= 0 && required.Add(p); p = raw[p].parent) { }
            void Visit(int i)
            {
                if (visited[i] == 2) return;
                if (visited[i] == 1) throw new InvalidDataException("FBX hierarchy cycle.");
                visited[i] = 1; int parent = raw[i].parent;
                if (parent >= count) throw new InvalidDataException("Invalid FBX parent.");
                if (parent >= 0) Visit(parent);
                ordered.Add(i); visited[i] = 2;
            }
            for (int i = 0; i < count; i++) Visit(i);
            var oldToNew = new int[count]; for (int i = 0; i < count; i++) oldToNew[ordered[i]] = i;
            var bones = new Bone[count]; var scales = new float[count];
            var worldP = new Vector3[count]; var worldQ = new Quaternion[count];
            for (int n = 0; n < count; n++)
            {
                int i = ordered[n], parent = raw[i].parent < 0 ? -1 : oldToNew[raw[i].parent]; Node node = raw[i];
                Vector3 p = new((float)node.px, (float)node.py, (float)node.pz);
                Quaternion q = Quaternion.Normalize(new((float)node.qx, (float)node.qy, (float)node.qz, (float)node.qw));
                Vector3 localP = parent < 0 ? p : Math3.Rotate(Quaternion.Inverse(worldQ[parent]), p - worldP[parent]);
                Quaternion localQ = parent < 0 ? q : Quaternion.Normalize(Quaternion.Inverse(worldQ[parent]) * q);
                float scale = (float)Math.Abs(node.sx);
                if (required.Contains(i) && meta.Skeleton.Count == 0 &&
                    (node.sx <= 0 || node.sy <= 0 || node.sz <= 0 || Math.Abs(node.sx-node.sy)>Math.Max(1e-7,scale*1e-4) || Math.Abs(node.sx-node.sz)>Math.Max(1e-7,scale*1e-4)))
                    throw new NotSupportedException("Nonuniform/negative Humanoid ancestor scale: " + names[i]);
                // Unity .meta skeleton positions are already in imported units.
                // ufbx's synthetic scene-root conversion scale must not be
                // applied a second time to those values.
                if (parent < 0 && meta.Skeleton.Count > 0) { localP=Vector3.Zero; localQ=Quaternion.Identity; scale=1; }
                if (meta.Skeleton.TryGetValue(names[i], out var rest))
                {
                    if (!rest.Uniform && required.Contains(i)) throw new NotSupportedException("Nonuniform/negative Humanoid ancestor scale: " + names[i]);
                    if (rest.Uniform)
                    {
                        localP = rest.Position * (parent < 0 ? 1 : scales[parent]); localQ = rest.Rotation;
                        scale = rest.Scale * (parent < 0 ? 1 : scales[parent]);
                    }
                }
                scales[n] = scale;
                worldP[n] = parent < 0 ? localP : worldP[parent] + Math3.Rotate(worldQ[parent], localP);
                worldQ[n] = parent < 0 ? localQ : Quaternion.Normalize(worldQ[parent] * localQ);
                int human = -1;
                foreach (var pair in map) if (pair.Value == names[i] && HumanBones.TryGetId(pair.Key, out int id)) { human = id; break; }
                if (human < 0 && (map.Count == 0 || options.BoneMapPath == null && meta.HumanBones.Count == 0)) human = HumanBones.Guess(names[i]);
                bones[n] = new Bone { name = names[i], parent = parent, humanBone = human, position = Math3.A(localP * options.Scale * meta.GlobalScale), rotation = Math3.A(localQ) };
            }
            string PathFor(int old)
            {
                if (old < 0) return "Body";
                var parts = new List<string>();
                for (int i = old; i >= 0 && raw[i].parent >= 0; i = raw[i].parent) parts.Add(names[i]);
                parts.Reverse(); return string.Join('/', parts);
            }
            var morphs = new List<MorphBinding>();
            for (int i = 0; i < hps_fbx_morph_count(scene); i++)
            {
                string name = Marshal.PtrToStringUTF8(hps_fbx_morph_name(scene, i)) ?? "";
                if (name.Length > 0) morphs.Add(new MorphBinding { name = name, path = PathFor(hps_fbx_morph_node(scene, i)) });
            }
            return new FbxDocument { source = Path.GetFullPath(path), bones = bones, morphs = morphs.ToArray(), usedUnityMeta = metaPath != null,
                unitScale = options.Scale * meta.GlobalScale };
        }
        finally { hps_fbx_free(scene); }
    }
}

internal sealed class UnityFbxMeta
{
    internal sealed record Rest(Vector3 Position, Quaternion Rotation, float Scale, bool Uniform);
    public Dictionary<string, string> HumanBones { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Rest> Skeleton { get; } = new(StringComparer.Ordinal);
    public float GlobalScale { get; private set; } = 1;
    public bool HasCustomMuscleLimits { get; private set; }
    private static float[] Components(string text) => Regex.Matches(text, @"[xyzw]:\s*([-+\d.eE]+)")
        .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
    public static UnityFbxMeta Read(string path)
    {
        string text = File.ReadAllText(path); var result = new UnityFbxMeta();
        text = Regex.Replace(text, @"\{[^{}]*\}", m => Regex.Replace(m.Value, @"\r?\n\s*", " "));
        var scaleMatch = Regex.Match(text, @"(?m)^    globalScale:\s*([-+\d.eE]+)");
        if (scaleMatch.Success) result.GlobalScale = float.Parse(scaleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        bool human = false, skeleton = false; string? boneName = null, restName = null;
        Vector3 p = Vector3.Zero; Quaternion q = Quaternion.Identity;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed == "human:") { human = true; skeleton = false; continue; }
            if (trimmed == "skeleton:") { human = false; skeleton = true; continue; }
            if (human)
            {
                if (trimmed.StartsWith("- boneName: ")) boneName = trimmed[12..].Trim('"');
                if (trimmed.StartsWith("humanName: ") && boneName != null)
                    result.HumanBones[trimmed[11..].Trim('"').Replace(" ", string.Empty)] = boneName;
                if (trimmed == "modified: 1") result.HasCustomMuscleLimits = true;
            }
            if (!skeleton) continue;
            if (trimmed.StartsWith("- name: ")) { restName = trimmed[8..].Trim('"'); p = Vector3.Zero; q = Quaternion.Identity; }
            else if (restName != null && trimmed.StartsWith("position:")) { var a = Components(trimmed); p = Math3.V(a); }
            else if (restName != null && trimmed.StartsWith("rotation:")) q = Math3.Q(Components(trimmed));
            else if (restName != null && trimmed.StartsWith("scale:"))
            {
                var a = Components(trimmed);
                if (a.Length != 3) throw new InvalidDataException("Invalid FBX metadata scale.");
                bool uniform = Math.Abs(a[0] - a[1]) <= 1e-4 && Math.Abs(a[0] - a[2]) <= 1e-4 && a[0] > 0;
                result.Skeleton[restName] = new Rest(p, q, a[0], uniform); restName = null;
            }
            else if (!line.StartsWith("    ") && trimmed.Length > 0) skeleton = false;
        }
        return result;
    }
}

public static class HumanBones
{
    public static readonly string[] Names = ["Hips","LeftUpperLeg","RightUpperLeg","LeftLowerLeg","RightLowerLeg","LeftFoot","RightFoot","Spine","Chest","Neck","Head","LeftShoulder","RightShoulder","LeftUpperArm","RightUpperArm","LeftLowerArm","RightLowerArm","LeftHand","RightHand","LeftToes","RightToes","LeftEye","RightEye","Jaw","LeftThumbProximal","LeftThumbIntermediate","LeftThumbDistal","LeftIndexProximal","LeftIndexIntermediate","LeftIndexDistal","LeftMiddleProximal","LeftMiddleIntermediate","LeftMiddleDistal","LeftRingProximal","LeftRingIntermediate","LeftRingDistal","LeftLittleProximal","LeftLittleIntermediate","LeftLittleDistal","RightThumbProximal","RightThumbIntermediate","RightThumbDistal","RightIndexProximal","RightIndexIntermediate","RightIndexDistal","RightMiddleProximal","RightMiddleIntermediate","RightMiddleDistal","RightRingProximal","RightRingIntermediate","RightRingDistal","RightLittleProximal","RightLittleIntermediate","RightLittleDistal","UpperChest"];
    public static bool TryGetId(string name, out int id) { string key = Normalize(name); id = Array.FindIndex(Names, n => Normalize(n) == key); return id >= 0; }
    private static string Normalize(string name) => Regex.Replace(name, @"[^\p{L}\p{N}]", "").ToLowerInvariant().Replace("mixamorig", "").Replace("jbip", "");
    public static int Guess(string name)
    {
        string normalized = Normalize(name);
        for (int i = 0; i < Names.Length; i++)
        {
            string n = Normalize(Names[i]);
            if (normalized == n || normalized == n.Replace("left", "l").Replace("right", "r")) return i;
            // Blender convention: UpperLeg.L, LowerArm_R, etc.
            foreach (string side in new[] { "left", "right" })
                if (n.StartsWith(side, StringComparison.Ordinal))
                {
                    string part = n[side.Length..];
                    if (normalized == part + side || normalized == part + side[0]) return i;
                }
        }
        return normalized switch { "pelvis" or "bip001pelvis" => 0, "head" => 10, "larm" => 13, "rarm" => 14, "lforearm" => 15, "rforearm" => 16,
            "toel" or "ltoe" or "lefttoe" or "toeleft" => 19,
            "toer" or "rtoe" or "righttoe" or "toeright" => 20, _ => -1 };
    }
}
