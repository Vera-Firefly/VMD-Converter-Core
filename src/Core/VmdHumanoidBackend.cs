namespace VeraFirefly.VmdConverter.Core;

public enum VmdAnimationMode
{
    Legacy = 0,
    Transform = 1,
    Humanoid = 2
}

/// <summary>Optional backend contract. It has no dependency on Unity-HPS.</summary>
public sealed class VmdHumanoidBackendOptions
{
    public string? FbxPath { get; set; }
    public string? HpsRigPath { get; set; }
    public string? MetaPath { get; set; }
    public string? BoneMapPath { get; set; }
    public string? ProfilePath { get; set; }
    public string? DescriptorPath { get; set; }
    public VmdAnimationMode AnimationMode { get; set; }
    public float PositionScale { get; set; } = 0.08f;
    public int CoordinateConversion { get; set; } = 2;
    public float SampleRate { get; set; } = 30;
    public int Passes { get; set; } = 3;
    public string RangePolicy { get; set; } = "tangent";
    public string LegFit { get; set; } = "auto";
    public bool FkContinuity { get; set; } = true;
    public float? DurationSeconds { get; set; }
    public string? DiagnosticsDirectory { get; set; }
    public float RotationErrorDegrees { get; set; } = 0.01f;
    public float PositionError { get; set; } = 0.00005f;
    public float CurveError { get; set; } = 0.00001f;
    public bool? TargetLegIk { get; set; }
    public bool BakeMorphs { get; set; } = true;
    /// <summary>Map target helper nodes that sit between two mapped bones onto the
    /// unused source bones between their sources (transform mode only). Off by
    /// default: the mapping is validated, the transport of those nodes is not.</summary>
    public bool StructuralBones { get; set; }
    /// <summary>Unity calibration descriptor (Motion/M4-标定集导出规格.md). When set,
    /// the upper body is decoded with the measured muscle axes instead of the profile.</summary>
    public string? CalibrationPath { get; set; }
    /// <summary>Per-bone residual model fitted on a reference clip (route B). Applied to the
    /// upper-body world rotations after the muscle decode.</summary>
    public string? ResidualPath { get; set; }
    public VmdExternalOutputKind OutputKind { get; set; } = VmdExternalOutputKind.HumanoidIk;
}

public interface IVmdCoverageSession
{
    string CoverageJson(VmdMotion motion, VmdTargetCurveData curves);
}

public interface IVmdDescriptorBackend
{
    void ExportDescriptor(string fbxPath, string? metaPath, string? boneMapPath,
        string? profilePath, string outputPath);
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
