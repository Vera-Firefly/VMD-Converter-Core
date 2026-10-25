using System.Numerics;
using System.Text.Json;
using UnityHps;

namespace VeraFirefly.VmdConverter.Core.Hps;

/// <summary>
/// Muscle-space model reconstructed from a Unity calibration export
/// (see Motion/M4-标定集导出规格.md). For every (muscle, bone) pair the export
/// yields a fixed rotation axis and a linear gain; a pose is composed by
/// multiplying those rotations in ascending muscle index order and applying the
/// product on the left of the bone's rest local rotation.
/// </summary>
internal sealed class CalibratedMuscles
{
    private readonly record struct Effect(int Muscle, int Bone, float X, float Y, float Z, float GainDeg);

    private readonly Effect[] effects;
    private readonly Dictionary<int, int> effectStart = new();
    private readonly int boneCount;
    private readonly Dictionary<int, int> boneByHuman = new();

    private CalibratedMuscles(Effect[] effects, int boneCount, Dictionary<int, int> boneByHuman)
    {
        this.effects = effects;
        this.boneCount = boneCount;
        this.boneByHuman = boneByHuman;
    }

    internal int EffectCount => effects.Length;
    internal int MappedBones => boneByHuman.Count;
    internal int BoneCount => boneCount;

    private static readonly string[] HumanNames =
    {
        "Hips", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg",
        "LeftFoot", "RightFoot", "Spine", "Chest", "Neck", "Head",
        "LeftShoulder", "RightShoulder", "LeftUpperArm", "RightUpperArm",
        "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand",
        "LeftToes", "RightToes", "LeftEye", "RightEye", "Jaw",
        "LeftThumbProximal", "LeftThumbIntermediate", "LeftThumbDistal",
        "LeftIndexProximal", "LeftIndexIntermediate", "LeftIndexDistal",
        "LeftMiddleProximal", "LeftMiddleIntermediate", "LeftMiddleDistal",
        "LeftRingProximal", "LeftRingIntermediate", "LeftRingDistal",
        "LeftLittleProximal", "LeftLittleIntermediate", "LeftLittleDistal",
        "RightThumbProximal", "RightThumbIntermediate", "RightThumbDistal",
        "RightIndexProximal", "RightIndexIntermediate", "RightIndexDistal",
        "RightMiddleProximal", "RightMiddleIntermediate", "RightMiddleDistal",
        "RightRingProximal", "RightRingIntermediate", "RightRingDistal",
        "RightLittleProximal", "RightLittleIntermediate", "RightLittleDistal",
        "UpperChest"
    };

    /// <summary>Unity writes finger human names with spaces ("Left Index Distal")
    /// while rigs use dotted names, so matching ignores spaces and dots.</summary>
    private static string Key(string name) =>
        name.Replace(" ", string.Empty).Replace(".", string.Empty).ToUpperInvariant();

    internal static CalibratedMuscles? Load(string path, Rig rig)    {
        if (!File.Exists(path)) return null;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("effects", out JsonElement list)) return null;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rig.bones.Length; i++)
            byName[Key(rig.bones[i].name ?? string.Empty)] = i;
        var humanToBone = new Dictionary<int, int>();
        if (root.TryGetProperty("boneMap", out JsonElement map))
        {
            var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in map.EnumerateObject())
            {
                string? target = property.Value.GetString();
                if (target != null) mapped[Key(property.Name)] = target;
            }
            for (int human = 0; human < HumanNames.Length; human++)
            {
                if (!mapped.TryGetValue(Key(HumanNames[human]), out string? target)) continue;
                if (byName.TryGetValue(Key(target), out int index)) humanToBone[human] = index;
            }
        }
        var effects = new List<Effect>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("muscle", out JsonElement muscle) ||
                !item.TryGetProperty("bone", out JsonElement bone) ||
                !item.TryGetProperty("axis", out JsonElement axis) ||
                !item.TryGetProperty("gainDeg", out JsonElement gain)) continue;
            int human = bone.GetInt32();
            if (!humanToBone.TryGetValue(human, out int rigBone)) continue;
            JsonElement ax = axis[0], ay = axis[1], az = axis[2];
            effects.Add(new Effect(muscle.GetInt32(), rigBone,
                ax.GetSingle(), ay.GetSingle(), az.GetSingle(), gain.GetSingle()));
        }
        if (effects.Count == 0) return null;
        effects.Sort((a, b) => a.Bone != b.Bone
            ? a.Bone.CompareTo(b.Bone) : a.Muscle.CompareTo(b.Muscle));
        return new CalibratedMuscles(effects.ToArray(), rig.bones.Length, humanToBone);
    }

    /// <summary>Writes the calibrated local rotations into <paramref name="locals"/>.</summary>
    internal void Apply(float[] muscles, Quaternion[] rest, Quaternion[] locals)
    {
        int index = 0;
        while (index < effects.Length)
        {
            int bone = effects[index].Bone;
            int start = index;
            while (index < effects.Length && effects[index].Bone == bone) index++;
            // ascending muscle order == the order inside the run
            float px = 0f, py = 0f, pz = 0f, pw = 1f;
            for (int i = start; i < index; i++)
            {
                Effect effect = effects[i];
                if (effect.Muscle >= muscles.Length) continue;
                float value = muscles[effect.Muscle];
                if (MathF.Abs(value) < 1e-6f) continue;
                float degrees = effect.GainDeg * value;
                float half = degrees * (MathF.PI / 360f);
                float s = MathF.Sin(half);
                float dx = effect.X * s, dy = effect.Y * s, dz = effect.Z * s, dw = MathF.Cos(half);
                // product = delta * product
                float nx = dw * px + dx * pw + dy * pz - dz * py;
                float ny = dw * py - dx * pz + dy * pw + dz * px;
                float nz = dw * pz + dx * py - dy * px + dz * pw;
                float nw = dw * pw - dx * px - dy * py - dz * pz;
                px = nx; py = ny; pz = nz; pw = nw;
            }
            if (bone >= locals.Length) continue;
            Quaternion restQ = rest[bone];
            locals[bone] = new Quaternion(
                pw * restQ.X + px * restQ.W + py * restQ.Z - pz * restQ.Y,
                pw * restQ.Y - px * restQ.Z + py * restQ.W + pz * restQ.X,
                pw * restQ.Z + px * restQ.Y - py * restQ.X + pz * restQ.W,
                pw * restQ.W - px * restQ.X - py * restQ.Y - pz * restQ.Z);
        }
    }

    internal bool HasHumanBone(int human) => boneByHuman.ContainsKey(human);
}
