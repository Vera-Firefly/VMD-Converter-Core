using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using VeraFirefly.VmdConverter.Core;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    /// <summary>Public EXE entry point independent of VMDCW and the built-in parser.</summary>
    public static class VmdConverterCore
    {
        /// <summary>Builds the same single-job command that the Unity adapter uses.</summary>
        public static string BuildCommandLine(string executablePath, string pmxPath,
            string vmdPath, string outputPath, string targetRigJsonPath,
            float sampleRate = 30f, int threads = 0,
            VmdExternalOutputKind outputKind = VmdExternalOutputKind.TargetFk,
            bool noUnity = false)
        {
            if (sampleRate <= 0f || sampleRate > 240f)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (threads < 0 || threads > 256)
                throw new ArgumentOutOfRangeException(nameof(threads));
            if (noUnity && outputKind == VmdExternalOutputKind.TargetFk)
                throw new ArgumentException("--nlu does not apply to target FK.");
            return Quote(Path.GetFullPath(executablePath)) + " --pmx " +
                Quote(Path.GetFullPath(pmxPath)) + " --vmd " +
                Quote(Path.GetFullPath(vmdPath)) + " --output " +
                Quote(Path.GetFullPath(outputPath)) + " --sample-rate " +
                sampleRate.ToString(CultureInfo.InvariantCulture) + " --threads " +
                (threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount - 1)) +
                " --rig " + Quote(Path.GetFullPath(targetRigJsonPath)) +
                " --output-kind " + (outputKind == VmdExternalOutputKind.HumanoidIk
                    ? "humanoid-ik" : outputKind == VmdExternalOutputKind.TargetCalibratedHumanoid
                        ? "target-humanoid" : "target-fk") +
                (noUnity ? " --nlu" : string.Empty);
        }

        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

        public static VmdExternalPoseBatch PrepareBatch(string executablePath,
            string pmxPath, IReadOnlyList<string> vmdPaths, string targetRigJsonPath,
            float sampleRate = 30f, int threads = 0,
            CancellationToken cancellationToken = default,
            IReadOnlyList<string> expressionVmdPaths = null,
            bool noUnity = false)
        {
            if (string.IsNullOrWhiteSpace(targetRigJsonPath))
                throw new ArgumentException("A target rig JSON path is required.",
                    nameof(targetRigJsonPath));
            return VmdExternalPoseWorker.PrepareBatch(executablePath, pmxPath,
                vmdPaths, sampleRate, threads, cancellationToken: cancellationToken,
                targetRigPath: targetRigJsonPath,
                expressionVmdPaths: expressionVmdPaths,
                noUnity: noUnity);
        }

        public static VmdTargetCurveData ReadCurves(string cachePath,
            string pmxPath, string vmdPath, string targetRigJsonPath,
            string expressionVmdPath = null)
        {
            using (var stream = System.IO.File.OpenRead(cachePath))
                return VmdTargetCurveFile.Read(stream,
                    VmdTargetCurveFile.HashFile(pmxPath),
                    VmdTargetCurveFile.HashMotionSources(vmdPath, expressionVmdPath),
                    VmdTargetCurveFile.HashFile(targetRigJsonPath));
        }
    }
}
