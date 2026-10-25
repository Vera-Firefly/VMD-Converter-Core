using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    /// <summary>Unity clip output from a VMD motion. Target modes require a Humanoid Avatar.</summary>
    public enum VmdClipOutput
    {
        Humanoid,
        TargetHumanoid,
        TargetFk
    }

    /// <summary>Inputs for the public, VRC SDK independent conversion API.</summary>
    public sealed class VmdConversionSettings
    {
        public string PmxPath;
        public VmdClipOutput Output = VmdClipOutput.Humanoid;
        public GameObject TargetAvatar;
        public Transform MorphTargetRoot;
        public PmxAnimationClipBakerOptions BakeOptions = new PmxAnimationClipBakerOptions();
        public PmxDebugTargetFkBakeOptions FkOptions = new PmxDebugTargetFkBakeOptions();
        public int MaximumParallelism;
    }

    public sealed class VmdAssetConversionItem
    {
        public string VmdPath;
        public string OutputAssetPath;
    }

    /// <summary>
    /// Parses the PMX once and reuses conversion resources across VMD files. Frame evaluation
    /// can run on worker threads; Unity HumanPose and AnimationClip creation stay on the caller's
    /// Editor main thread. This API does not create FBX files.
    /// </summary>
    public sealed class VmdBatchConversionSession : IDisposable
    {
        private readonly VmdConversionSettings settings;
        private readonly PmxModel model;
        private readonly PmxHumanoidAnimationClipBaker.BatchSession session;
        private bool disposed;

        public VmdBatchConversionSession(VmdConversionSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.PmxPath))
                throw new ArgumentException("A PMX path is required.", nameof(settings));
            if (settings.Output != VmdClipOutput.Humanoid && settings.TargetAvatar == null)
                throw new ArgumentException("A target Humanoid Avatar is required for target output.", nameof(settings));
            if (settings.MaximumParallelism < 0)
                throw new ArgumentOutOfRangeException(nameof(settings.MaximumParallelism));
            model = PmxParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(settings.PmxPath));
            session = new PmxHumanoidAnimationClipBaker.BatchSession(
                model, settings.BakeOptions ?? new PmxAnimationClipBakerOptions(),
                settings.Output != VmdClipOutput.TargetFk);
        }

        public PmxAnimationBakeReport ConvertToClip(string vmdPath,
            Action<int, int> threadSafeFrameProgress = null,
            Action<float, string> mainThreadBakeProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VmdBatchConversionSession));
            if (string.IsNullOrWhiteSpace(vmdPath)) throw new ArgumentNullException(nameof(vmdPath));
            cancellationToken.ThrowIfCancellationRequested();
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared = session.Prepare(
                motion, settings.MaximumParallelism, threadSafeFrameProgress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PmxAnimationBakeReport report;
            switch (settings.Output)
            {
                case VmdClipOutput.TargetHumanoid:
                    report = session.BakeTargetHumanoid(prepared, settings.TargetAvatar,
                        mainThreadBakeProgress);
                    break;
                case VmdClipOutput.TargetFk:
                    report = session.Debug_BakeTargetFk(prepared, settings.TargetAvatar,
                        settings.FkOptions, mainThreadBakeProgress);
                    break;
                default:
                    report = session.Bake(prepared, settings.MorphTargetRoot,
                        mainThreadBakeProgress);
                    break;
            }
            if ((settings.BakeOptions ?? new PmxAnimationClipBakerOptions()).BakeMorphs)
            {
                if (settings.Output == VmdClipOutput.TargetHumanoid)
                    report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                        report.Clip, motion, settings.TargetAvatar.transform);
                else if (settings.Output == VmdClipOutput.Humanoid && settings.MorphTargetRoot == null)
                    report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                        report.Clip, motion, null);
            }
            report.Clip.name = Path.GetFileNameWithoutExtension(vmdPath);
            return report;
        }

        /// <summary>Bakes poses prepared by the external worker after validating both source hashes.</summary>
        public PmxAnimationBakeReport ConvertPreparedPoseToClip(string vmdPath,
            string poseCachePath, Action<float, string> mainThreadBakeProgress = null)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VmdBatchConversionSession));
            if (string.IsNullOrWhiteSpace(vmdPath)) throw new ArgumentNullException(nameof(vmdPath));
            if (string.IsNullOrWhiteSpace(poseCachePath))
                throw new ArgumentNullException(nameof(poseCachePath));
            string physicalVmd = PmxAnimationClipBaker.ToPhysicalPath(vmdPath);
            string physicalPmx = PmxAnimationClipBaker.ToPhysicalPath(settings.PmxPath);
            VmdMotion motion = VmdParser.Parse(physicalVmd);
            PmxPreparedPoseData cached;
            using (var stream = File.OpenRead(poseCachePath))
                cached = PmxPreparedPoseFile.Read(stream,
                    PmxPreparedPoseFile.HashFile(physicalPmx),
                    PmxPreparedPoseFile.HashFile(physicalVmd));
            float sampleRate = (settings.BakeOptions ?? new PmxAnimationClipBakerOptions()).SampleRate;
            if (cached.MaximumFrame != motion.MaximumFrame ||
                Mathf.Abs(cached.SampleRate - sampleRate) > 0.0001f ||
                cached.Samples[0].BoneCount != model.Bones.Count)
                throw new InvalidDataException("The pose cache sampling does not match the Unity settings.");
            if (!HasUnityPoseParity(motion, cached, sampleRate))
                return ConvertToClip(vmdPath, mainThreadBakeProgress: mainThreadBakeProgress);
            var samples = new PmxHumanoidPoseSample[cached.Samples.Length];
            for (int index = 0; index < samples.Length; index++)
                samples[index] = new PmxHumanoidPoseSample(cached.Samples[index]);
            var prepared = new PmxHumanoidAnimationClipBaker.PreparedMotion(
                motion, cached.Duration, samples);
            PmxAnimationBakeReport report;
            switch (settings.Output)
            {
                case VmdClipOutput.TargetHumanoid:
                    report = session.BakeTargetHumanoid(prepared, settings.TargetAvatar,
                        mainThreadBakeProgress);
                    break;
                case VmdClipOutput.TargetFk:
                    report = session.Debug_BakeTargetFk(prepared, settings.TargetAvatar,
                        settings.FkOptions, mainThreadBakeProgress);
                    break;
                default:
                    report = session.Bake(prepared, settings.MorphTargetRoot,
                        mainThreadBakeProgress);
                    break;
            }
            if ((settings.BakeOptions ?? new PmxAnimationClipBakerOptions()).BakeMorphs)
            {
                if (settings.Output == VmdClipOutput.TargetHumanoid)
                    report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                        report.Clip, motion, settings.TargetAvatar.transform);
                else if (settings.Output == VmdClipOutput.Humanoid && settings.MorphTargetRoot == null)
                    report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                        report.Clip, motion, null);
            }
            report.Clip.name = Path.GetFileNameWithoutExtension(vmdPath);
            return report;
        }

        private bool HasUnityPoseParity(VmdMotion motion, PmxPreparedPoseData cached,
            float sampleRate)
        {
            int last = cached.Samples.Length - 1;
            var indices = new HashSet<int>();
            for (int step = 0; step <= 32; step++)
                indices.Add(Mathf.RoundToInt(last * step / 32f));
            foreach (VmdIkKeyframe key in motion.IkKeyframes)
            {
                int sample = Mathf.Clamp(Mathf.RoundToInt(key.Frame / 30f * sampleRate),
                    0, last);
                indices.Add(Mathf.Max(0, sample - 1));
                indices.Add(sample);
                indices.Add(Mathf.Min(last, sample + 1));
            }
            PmxEvaluationOptions evaluation = settings.BakeOptions?.Evaluation ??
                                              new PmxEvaluationOptions();
            var evaluator = new PmxMotionEvaluator(model, motion, evaluation);
            PmxPose reusable = evaluator.CreatePose();
            foreach (int index in indices)
            {
                float frame = index == last ? motion.MaximumFrame :
                    Mathf.Abs(sampleRate - 30f) <= 1e-6f
                        ? Mathf.Min(motion.MaximumFrame, index)
                        : Mathf.Min(motion.MaximumFrame, index / sampleRate * 30f);
                PmxPose unity = evaluator.Evaluate(frame, reusable);
                PmxPose external = cached.Samples[index];
                for (int bone = 0; bone < model.Bones.Count; bone++)
                {
                    float distance = Vector3.Distance(
                        unity.WorldPositions[bone], external.WorldPositions[bone]);
                    float worldAngle = Quaternion.Angle(
                        unity.WorldRotations[bone], external.WorldRotations[bone]);
                    float localAngle = Quaternion.Angle(
                        unity.LocalRotations[bone], external.LocalRotations[bone]);
                    if (float.IsNaN(distance) || float.IsNaN(worldAngle) ||
                        float.IsNaN(localAngle) || distance > 0.0005f ||
                        worldAngle > 0.2f || localAngle > 0.2f)
                        return false;
                }
            }
            return true;
        }

        public PmxAnimationBakeReport ConvertToAsset(VmdAssetConversionItem item,
            bool overwrite = false, CancellationToken cancellationToken = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            string output = item.OutputAssetPath?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(output) ||
                !output.StartsWith("Assets/", StringComparison.Ordinal) ||
                !output.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("OutputAssetPath must be an Assets/*.anim path.", nameof(item));
            UnityEngine.Object existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(output);
            if (existing != null && !(existing is AnimationClip))
                throw new IOException("The output path contains a different asset type: " + output);
            if (existing != null && !overwrite)
                throw new IOException("The output asset already exists: " + output);
            PmxAnimationBakeReport report = ConvertToClip(item.VmdPath,
                cancellationToken: cancellationToken);
            VmdCoreAssetPaths.EnsureAssetFolderExists(
                Path.GetDirectoryName(output)?.Replace('\\', '/'));
            if (overwrite) AssetDatabase.DeleteAsset(output);
            AssetDatabase.CreateAsset(report.Clip, output);
            return report;
        }

        public IReadOnlyList<PmxAnimationBakeReport> ConvertBatchToAssets(
            IReadOnlyList<VmdAssetConversionItem> items, bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            var reports = new List<PmxAnimationBakeReport>(items.Count);
            foreach (VmdAssetConversionItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reports.Add(ConvertToAsset(item, overwrite, cancellationToken));
            }
            AssetDatabase.SaveAssets();
            return reports;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            session.Dispose();
        }
    }

}
