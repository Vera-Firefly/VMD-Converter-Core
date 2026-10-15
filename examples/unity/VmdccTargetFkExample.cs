// Put this file under an Editor folder in any Unity 2022.3+ project that
// references com.verafirefly.vmdcc. It has no VMDCW or VRCSDK dependency.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;

public static class VmdccTargetFkExample
{
    /// <summary>
    /// Run one external PMX/VMD batch, then create target-Avatar FK .anim assets.
    /// Paths for the worker, PMX and VMDs must be physical filesystem paths.
    /// The output folder must already exist under Assets.
    /// </summary>
    public static IReadOnlyList<AnimationClip> Convert(
        GameObject targetAvatar,
        string workerExePath,
        string pmxPath,
        IReadOnlyList<string> vmdPaths,
        string outputAssetFolder,
        float sampleRate = 30f,
        int threads = 0)
    {
        if (targetAvatar == null) throw new ArgumentNullException(nameof(targetAvatar));
        if (vmdPaths == null || vmdPaths.Count == 0)
            throw new ArgumentException("At least one VMD is required.", nameof(vmdPaths));
        if (sampleRate <= 0f || sampleRate > 240f)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (threads < 0 || threads > 256)
            throw new ArgumentOutOfRangeException(nameof(threads));
        if (string.IsNullOrWhiteSpace(outputAssetFolder))
            throw new ArgumentException("An Assets folder is required.", nameof(outputAssetFolder));

        outputAssetFolder = outputAssetFolder.Replace('\\', '/').TrimEnd('/');
        if (!(outputAssetFolder == "Assets" ||
              outputAssetFolder.StartsWith("Assets/", StringComparison.Ordinal)) ||
            !AssetDatabase.IsValidFolder(outputAssetFolder))
            throw new ArgumentException("Output folder must already exist under Assets.",
                nameof(outputAssetFolder));

        workerExePath = ExistingFile(workerExePath);
        pmxPath = ExistingFile(pmxPath);
        var physicalVmdPaths = new List<string>(vmdPaths.Count);
        var assetPaths = new List<string>(vmdPaths.Count);
        var uniqueInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string vmdPath in vmdPaths)
        {
            string physical = ExistingFile(vmdPath);
            if (!uniqueInputs.Add(physical))
                throw new ArgumentException("Duplicate VMD: " + physical, nameof(vmdPaths));
            string assetPath = outputAssetFolder + "/" +
                Path.GetFileNameWithoutExtension(physical) + "_VMDCC.anim";
            if (!uniqueOutputs.Add(assetPath) ||
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                throw new IOException("Output animation already exists or collides: " + assetPath);
            physicalVmdPaths.Add(physical);
            assetPaths.Add(assetPath);
        }

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string scratch = Path.Combine(projectRoot, "Library", "VmdccExample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string rigPath = Path.Combine(scratch, "target-rig.json");
        try
        {
            var options = new VmdRigExportOptions
            {
                SampleRate = sampleRate,
                OutputKind = VmdExternalOutputKind.TargetFk
            };
            VmdTargetRigExporter.ExportJson(targetAvatar, rigPath, options);
            var clips = new List<AnimationClip>(physicalVmdPaths.Count);
            using (VmdExternalPoseBatch batch = VmdConverterCore.PrepareBatch(
                       workerExePath, pmxPath, physicalVmdPaths, rigPath,
                       sampleRate, threads))
            {
                for (int i = 0; i < physicalVmdPaths.Count; i++)
                {
                    string vmd = physicalVmdPaths[i];
                    VmdTargetCurveData data = VmdConverterCore.ReadCurves(
                        batch.GetPosePath(vmd), pmxPath, vmd, rigPath);
                    AnimationClip clip = CreateClip(data,
                        Path.GetFileNameWithoutExtension(vmd));
                    AssetDatabase.CreateAsset(clip, assetPaths[i]);
                    clips.Add(clip);
                }
            }
            AssetDatabase.SaveAssets();
            return clips;
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    private static string ExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A physical file path is required.", nameof(path));
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("File not found.", full);
        return full;
    }

    private static AnimationClip CreateClip(VmdTargetCurveData data, string name)
    {
        var clip = new AnimationClip { name = name, frameRate = data.SampleRate };
        var bindings = new List<EditorCurveBinding>(data.Curves.Count);
        var curves = new List<AnimationCurve>(data.Curves.Count);
        foreach (VmdFloatCurveData source in data.Curves)
        {
            if (source.Times.Length != source.Values.Length)
                throw new InvalidDataException("Curve time/value count differs.");
            Type targetType = source.BindingKind == 0 ? typeof(Transform)
                : source.BindingKind == 1 ? typeof(SkinnedMeshRenderer)
                : typeof(Animator);
            bindings.Add(EditorCurveBinding.FloatCurve(source.Path, targetType,
                source.Property));
            var keys = new Keyframe[source.Times.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                int previous = i > 0 ? i - 1 : i;
                int next = i + 1 < keys.Length ? i + 1 : i;
                float inSlope = SegmentSlope(source, previous, i);
                float outSlope = SegmentSlope(source, i, next);
                if (i == 0) inSlope = outSlope;
                if (i == keys.Length - 1) outSlope = inSlope;
                keys[i] = new Keyframe(source.Times[i], source.Values[i],
                    inSlope, outSlope);
            }
            curves.Add(new AnimationCurve(keys));
        }
        AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
        clip.EnsureQuaternionContinuity();
        return clip;
    }

    private static float SegmentSlope(VmdFloatCurveData source, int left, int right)
    {
        float duration = source.Times[right] - source.Times[left];
        return duration > 0f
            ? (source.Values[right] - source.Values[left]) / duration
            : 0f;
    }
}
