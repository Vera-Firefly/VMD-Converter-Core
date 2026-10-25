#if VMDCW_HAS_VMDCC
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor.Nlu;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    public sealed class VmdMorphCurveTarget
    {
        public string TargetName;
        public string TargetPath;
    }

    /// <summary>
    /// Exports static Avatar bindings, asks the EXE to solve target FK, then only
    /// creates Unity AnimationClip curves from the result. No FBX is involved.
    /// </summary>
    public sealed class VmdExternalTargetFkSession : IDisposable
    {
        private readonly string pmxPath;
        private readonly string rigPath;
        private readonly string scratchFolder;
        private readonly VmdExternalPoseBatch curves;
        private readonly Dictionary<string, string> expressionPaths;
        private bool disposed;

        public VmdExternalTargetFkSession(string executablePath, string pmxPath,
            IReadOnlyList<string> vmdPaths, GameObject targetAvatar,
            PmxAnimationClipBakerOptions bakeOptions = null,
            PmxDebugTargetFkBakeOptions fkOptions = null,
            int threads = 0, Func<int, int, bool> cancelRequested = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<string> expressionVmdPaths = null,
            VmdExternalOutputKind outputKind = VmdExternalOutputKind.TargetFk,
            bool noUnity = false)
        {
            if (noUnity && VmdOutputModes.IsFk(outputKind))
                throw new ArgumentException("The --nlu compatibility flag is for Humanoid curve output.");
            if (targetAvatar == null) throw new ArgumentNullException(nameof(targetAvatar));
            if (fkOptions != null && VmdOutputModes.IsFk(outputKind))
            {
                if (fkOptions.TargetSolveScope == PmxTargetSolveScope.All || fkOptions.UpperLimbInverseSolve) outputKind = VmdExternalOutputKind.TargetFkAll;
                if (fkOptions.TargetSolveScope == PmxTargetSolveScope.Arm) outputKind = VmdExternalOutputKind.TargetFkArm;
                if (fkOptions.TargetSolveScope == PmxTargetSolveScope.Leg) outputKind = VmdExternalOutputKind.TargetFkLeg;
            }
            this.pmxPath = PmxAnimationClipBaker.ToPhysicalPath(pmxPath);
            expressionPaths = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (expressionVmdPaths != null)
            {
                if (expressionVmdPaths.Count != vmdPaths.Count)
                    throw new ArgumentException("Expression paths must match VMD jobs.");
                for (int i = 0; i < vmdPaths.Count; i++)
                    expressionPaths[Path.GetFullPath(PmxAnimationClipBaker.ToPhysicalPath(
                        vmdPaths[i]))] = string.IsNullOrEmpty(expressionVmdPaths[i])
                        ? null : PmxAnimationClipBaker.ToPhysicalPath(
                            expressionVmdPaths[i]);
            }
            bakeOptions = bakeOptions ?? new PmxAnimationClipBakerOptions();
            fkOptions = fkOptions ?? new PmxDebugTargetFkBakeOptions();
            VmdRigExportOptions rigOptions = CreateRigOptions(
                bakeOptions, fkOptions, outputKind);
            VmdTargetRigData rig = VmdTargetRigExporter.Export(targetAvatar,
                rigOptions);
            if (!VmdOutputModes.IsFk(outputKind))
            {
                PmxModel model = PmxModelCache.Load(this.pmxPath);
                using (PmxHumanoidSourceRig source =
                       PmxHumanoidSourceRig.CreateRestSource(model, bakeOptions))
                {
                    VmdTargetRigExporter.AttachCanonical(rig, source.Root,
                        rigOptions);
                }
            }
            scratchFolder = Path.Combine(Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Library", "VmdPoseCache")),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratchFolder);
            rigPath = Path.Combine(scratchFolder, "target-rig.json");
            try
            {
                VmdTargetRigExporter.WriteJson(rig, rigPath);
                curves = noUnity
                    ? VmdNluBatchPipeline.PrepareBatch(executablePath,
                        this.pmxPath, vmdPaths, rigPath,
                        bakeOptions.SampleRate, threads, cancelRequested,
                        cancellationToken, expressionVmdPaths)
                    : VmdExternalPoseWorker.PrepareBatch(executablePath,
                        this.pmxPath, vmdPaths, bakeOptions.SampleRate, threads,
                        cancelRequested, cancellationToken, rigPath,
                        expressionVmdPaths);
            }
            catch
            {
                if (Directory.Exists(scratchFolder)) Directory.Delete(scratchFolder, true);
                throw;
            }
        }

        public PmxAnimationBakeReport ConvertToClip(string vmdPath,
            IReadOnlyDictionary<string, VmdMorphCurveTarget> morphMappings = null)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VmdExternalTargetFkSession));
            string physicalVmd = PmxAnimationClipBaker.ToPhysicalPath(vmdPath);
            string curvePath = curves.GetPosePath(physicalVmd);
            VmdTargetCurveData data;
            using (var stream = File.OpenRead(curvePath))
                data = VmdTargetCurveFile.Read(stream,
                    PmxPreparedPoseFile.HashFile(pmxPath),
                    VmdTargetCurveFile.HashMotionSources(physicalVmd,
                        expressionPaths.TryGetValue(Path.GetFullPath(physicalVmd),
                            out string expression) ? expression : null),
                    PmxPreparedPoseFile.HashFile(rigPath));
            var clip = new AnimationClip
            {
                name = Path.GetFileNameWithoutExtension(vmdPath),
                frameRate = data.SampleRate,
                legacy = false
            };
            var bindings = new List<EditorCurveBinding>(data.Curves.Count);
            var animationCurves = new List<AnimationCurve>(data.Curves.Count);
            foreach (VmdFloatCurveData curve in data.Curves)
            {
                var keys = new Keyframe[curve.Times.Length];
                for (int i = 0; i < keys.Length; i++)
                    keys[i] = new Keyframe(curve.Times[i], curve.Values[i]);
                Type type = curve.BindingKind == 0 ? typeof(Transform)
                    : curve.BindingKind == 1 ? typeof(SkinnedMeshRenderer)
                    : typeof(Animator);
                string path = curve.Path;
                string property = curve.Property;
                if (curve.BindingKind == 1 && property.StartsWith("blendShape.",
                        StringComparison.Ordinal) && morphMappings != null &&
                    morphMappings.TryGetValue(property.Substring("blendShape.".Length),
                        out VmdMorphCurveTarget target) && target != null &&
                    !string.IsNullOrEmpty(target.TargetName))
                {
                    path = target.TargetPath ?? string.Empty;
                    property = "blendShape." + target.TargetName;
                }
                bindings.Add(EditorCurveBinding.FloatCurve(path, type, property));
                animationCurves.Add(new AnimationCurve(
                    PmxAnimationClipBaker.CreateLinearKeys(keys)));
            }
            if (bindings.Count > 0)
                AnimationUtility.SetEditorCurves(clip,
                    bindings.ToArray(), animationCurves.ToArray());
            clip.EnsureQuaternionContinuity();
            return new PmxAnimationBakeReport
            {
                Clip = clip,
                SampleCount = data.SampleCount,
                BoundBoneCount = data.BoundBoneCount,
                BoundMorphCount = data.BoundMorphCount
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (curves != null) curves.Dispose(); }
            finally
            {
                if (Directory.Exists(scratchFolder)) Directory.Delete(scratchFolder, true);
            }
        }

        internal static VmdRigExportOptions CreateRigOptions(
            PmxAnimationClipBakerOptions bake, PmxDebugTargetFkBakeOptions fk,
            VmdExternalOutputKind outputKind)
        {
            return new VmdRigExportOptions
            {
                SampleRate = bake.SampleRate,
                PositionScale = bake.PositionScale,
                CoordinateConversion = (int)bake.CoordinateConversion,
                RotationErrorDegrees = fk.RotationErrorDegrees,
                PositionError = fk.PositionError,
                MuscleError = bake.CurveReductionError,
                BakeRootTranslation = fk.BakeRootTranslation,
                BakeMorphs = bake.BakeMorphs,
                OutputKind = outputKind
            };
        }

    }
}
#endif
