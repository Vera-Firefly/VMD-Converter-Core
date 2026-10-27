using System.Security.Cryptography;
using System.Text.Json;

namespace UnityHps;

/// <summary>
/// Versioned, offline target-skeleton input. A Unity-exported calibrated Rig
/// may be embedded, but FBX/.meta descriptors never claim Unity equivalence.
/// </summary>
public sealed class AvatarDescriptor
{
    public int version { get; set; } = 1;
    public string source { get; set; } = "";
    public string sourceSha256 { get; set; } = "";
    public string provenance { get; set; } = "heuristic";
    public string expectedAccuracy { get; set; } = "unproven";
    public string upAxis { get; set; } = "Y";
    public float unitScale { get; set; } = 1;
    public float humanScale { get; set; }
    public FbxDocument skeleton { get; set; } = new();
    public Rig? calibratedRig { get; set; }
}

public static class AvatarDescriptorIO
{
    public static AvatarDescriptor FromFbx(string path, FbxImportOptions? options = null,
        string? profilePath = null)
    {
        options ??= new();
        FbxDocument document = FbxReader.Read(path, options);
        float humanScale = 0;
        try { humanScale = OfflineRigFactory.FromSkeleton(document, profilePath).humanScale; }
        catch (InvalidDataException) { /* Generic transform skeletons may have no Humanoid mapping. */ }
        return new AvatarDescriptor
        {
            source = Path.GetFileName(path),
            sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            provenance = document.usedUnityMeta ? "meta" : "heuristic",
            expectedAccuracy = "unproven",
            unitScale = document.unitScale,
            humanScale = humanScale,
            skeleton = document
        };
    }

    public static AvatarDescriptor Read(string path)
    {
        using var stream = File.OpenRead(path);
        AvatarDescriptor descriptor = JsonSerializer.Deserialize(stream,
            HpsJsonContext.Default.AvatarDescriptor) ?? throw new InvalidDataException("Empty Avatar descriptor.");
        if (descriptor.version != 1 || descriptor.skeleton.bones.Length == 0)
            throw new InvalidDataException("Unsupported or empty Avatar descriptor.");
        if (descriptor.expectedAccuracy != "unproven" &&
            !descriptor.expectedAccuracy.StartsWith("measured:", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid Avatar descriptor accuracy grade.");
        if (descriptor.provenance is not ("meta" or "heuristic" or "unity"))
            throw new InvalidDataException("Invalid Avatar descriptor provenance.");
        if (descriptor.upAxis != "Y" || !float.IsFinite(descriptor.unitScale) || descriptor.unitScale <= 0)
            throw new InvalidDataException("Unsupported Avatar descriptor axis or unit scale.");
        SkeletonView.FromDocument(descriptor.skeleton);
        if (descriptor.calibratedRig != null) HumanoidSolver.Validate(descriptor.calibratedRig);
        return descriptor;
    }

    public static void Write(string path, AvatarDescriptor descriptor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, descriptor, HpsJsonContext.Default.AvatarDescriptor);
    }

    public static Rig BuildHumanoidRig(AvatarDescriptor descriptor, string? profilePath = null)
    {
        Rig rig = descriptor.calibratedRig ??
            OfflineRigFactory.FromSkeleton(descriptor.skeleton, profilePath, HumanoidResponseTransport.AnatomicalTargets);
        rig.precision = descriptor.calibratedRig == null
            ? "descriptor-" + descriptor.provenance + "-unproven"
            : "descriptor-unity-" + descriptor.expectedAccuracy;
        return rig;
    }
}
