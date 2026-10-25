using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityHps;

// Arrays use Unity's component order: xyz, xyzw. Positions are metres in an
// identity, unit-scale Animator root; quaternions use Hamilton composition.
public sealed class Rig
{
    public int version { get; set; } = 1;
    public string source { get; set; } = "";
    public float humanScale { get; set; } = 1;
    public int hips { get; set; }
    public Bone[] bones { get; set; } = [];
    public Bone[] bindBones { get; set; } = [];
    public MorphBinding[] morphs { get; set; } = [];
    public string precision { get; set; } = "avatar-calibrated";
    public Muscle[] muscles { get; set; } = [];
    public MuscleGroup[] groups { get; set; } = [];
    public float[] bodyPosition { get; set; } = [0, 1, 0];
    public float[] bodyRotation { get; set; } = [0, 0, 0, 1];
    // d(bodyPosition) / d(independent world position of bone), row major.
    public float[] bodyPositionJacobian { get; set; } = [];
    public float[] bodyPositionWeights { get; set; } = [];
    public string bodyOrientationMode { get; set; } = "hips";
    public int[] reverseCompositionBones { get; set; } = [];
}

public sealed class Bone
{
    public string name { get; set; } = "";
    public int parent { get; set; } = -1;
    public int humanBone { get; set; } = -1;
    public float[] position { get; set; } = [0, 0, 0];
    public float[] rotation { get; set; } = [0, 0, 0, 1];
}

public sealed class Muscle
{
    public string name { get; set; } = "";
    public int unityIndex { get; set; }
    public int humanBone { get; set; } = -1;
    public int dof { get; set; } = -1;
    public float minimum { get; set; } = -90;
    public float maximum { get; set; } = 90;
}

public sealed class MuscleGroup
{
    public int[] muscles { get; set; } = [];
    public int[] bones { get; set; } = [];
    public int resolution { get; set; } = 5;
    // Positive absolute muscle limit for each lattice dimension. Empty means 1.
    public float[] muscleRanges { get; set; } = [];
    // Uniform [-muscleRanges,+muscleRanges] lattice; dimension zero varies fastest.
    public LatticeSample[] samples { get; set; } = [];
}

public sealed class LatticeSample
{
    // Absolute neutral-body local transforms, flattened by group.bones order.
    public float[] rotations { get; set; } = [];
    public float[] positions { get; set; } = [];
    public float[] muscles { get; set; } = [];
}

public sealed class PoseBatch
{
    public int version { get; set; } = 1;
    public PoseFrame[] frames { get; set; } = [];
}

public sealed class PoseFrame
{
    public float time { get; set; }
    public float[] positions { get; set; } = [];
    public float[] rotations { get; set; } = [];
    public IkGoal[] goals { get; set; } = [];
    // Optional Unity GetHumanPose reference for accuracy verification.
    public HumanPose? reference { get; set; }
    public HumanPose? inputHumanPose { get; set; }
}

public sealed class IkGoal
{
    public int root { get; set; }
    public int middle { get; set; }
    public int tip { get; set; }
    public float[] target { get; set; } = [0, 0, 0];
    public float[] pole { get; set; } = [0, 0, 1];
    public float[]? rotation { get; set; }
    public float weight { get; set; } = 1;
}

public sealed class HumanPose
{
    public float[] bodyPosition { get; set; } = [0, 0, 0];
    public float[] bodyRotation { get; set; } = [0, 0, 0, 1];
    public float[] muscles { get; set; } = [];
}

public sealed class SolveResult
{
    public float time { get; set; }
    public HumanPose humanPose { get; set; } = new();
    public float maxFitErrorDegrees { get; set; }
    public float[] ikPositionErrors { get; set; } = [];
    public float[] positions { get; set; } = [];
    public float[] rotations { get; set; } = [];
}

public sealed class ResultBatch
{
    public int version { get; set; } = 1;
    public double elapsedMilliseconds { get; set; }
    public SolveResult[] frames { get; set; } = [];
}

public sealed class HumanPoseBatch
{
    public int version { get; set; } = 1;
    public HumanFrame[] frames { get; set; } = [];
}

public sealed class HumanFrame
{
    public float time { get; set; }
    public HumanPose humanPose { get; set; } = new();
}

public sealed class AccuracyReport
{
    public int frameCount { get; set; }
    public float maxRootPositionMeters { get; set; }
    public float rmsRootPositionMeters { get; set; }
    public float maxRootRotationDegrees { get; set; }
    public float maxMuscleError { get; set; }
    public float maxBoneFitDegrees { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Rig))]
[JsonSerializable(typeof(PoseBatch))]
[JsonSerializable(typeof(ResultBatch))]
[JsonSerializable(typeof(AccuracyReport))]
[JsonSerializable(typeof(HumanPoseBatch))]
[JsonSerializable(typeof(FbxDocument))]
[JsonSerializable(typeof(AvatarDescriptor))]
public partial class HpsJsonContext : JsonSerializerContext;

public static class HpsJson
{
    public static Rig ReadRig(string path)
    {
        using var file = File.OpenRead(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
            return JsonSerializer.Deserialize(gzip, HpsJsonContext.Default.Rig) ?? throw new InvalidDataException("Empty rig.");
        }
        return JsonSerializer.Deserialize(file, HpsJsonContext.Default.Rig) ?? throw new InvalidDataException("Empty rig.");
    }
    public static PoseBatch ReadPoses(string path) => JsonSerializer.Deserialize(
        File.ReadAllText(path), HpsJsonContext.Default.PoseBatch) ?? throw new InvalidDataException("Empty poses.");
    public static HumanPoseBatch ReadHumanPoses(string path) => JsonSerializer.Deserialize(
        File.ReadAllText(path), HpsJsonContext.Default.HumanPoseBatch) ?? throw new InvalidDataException("Empty human poses.");
    public static void Write(string path, Rig value) => Save(path, JsonSerializer.Serialize(value, HpsJsonContext.Default.Rig));
    public static void Write(string path, PoseBatch value) => Save(path, JsonSerializer.Serialize(value, HpsJsonContext.Default.PoseBatch));
    public static void Write(string path, ResultBatch value) => Save(path, JsonSerializer.Serialize(value, HpsJsonContext.Default.ResultBatch));
    public static void Write(string path, AccuracyReport value) => Save(path, JsonSerializer.Serialize(value, HpsJsonContext.Default.AccuracyReport));
    private static void Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text);
    }
}
