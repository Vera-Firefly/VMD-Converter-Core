namespace VeraFirefly.VmdConverter.Core;

/// <summary>Optional backend contract. It has no dependency on Unity-HPS.</summary>
public sealed class VmdHumanoidBackendOptions
{
    public string? FbxPath { get; set; }
    public string? HpsRigPath { get; set; }
    public string? MetaPath { get; set; }
    public string? BoneMapPath { get; set; }
    public string? ProfilePath { get; set; }
    public float PositionScale { get; set; } = 0.08f;
    public int CoordinateConversion { get; set; } = 2;
    public float SampleRate { get; set; } = 30;
    public int Passes { get; set; } = 2;
    public float RotationErrorDegrees { get; set; } = 0.01f;
    public float PositionError { get; set; } = 0.00005f;
    public float CurveError { get; set; } = 0.00001f;
    public bool? TargetLegIk { get; set; }
    public bool BakeMorphs { get; set; } = true;
    public VmdExternalOutputKind OutputKind { get; set; } = VmdExternalOutputKind.HumanoidIk;
}

public interface IVmdHumanoidBackend
{
    string Name { get; }
    IVmdHumanoidSession Create(PmxModel model, VmdHumanoidBackendOptions options, VmdTargetRigData? legacyRig);
}

public interface IVmdHumanoidSession
{
    VmdTargetRigData AnimationRig { get; }
    string Precision { get; }
    bool UsesSharedFramePool { get; }
    long EstimatedBytesPerSample { get; }
    byte[] RigIdentity { get; }
    void SaveRig(string path);
    VmdTargetCurveData Solve(VmdMotion motion, PmxPose[] samples, int threads, TaskScheduler? scheduler = null);
}
