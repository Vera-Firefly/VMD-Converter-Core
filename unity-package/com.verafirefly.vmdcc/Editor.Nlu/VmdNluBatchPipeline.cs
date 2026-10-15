using System;
using System.Collections.Generic;
using System.Threading;
using VeraFirefly.VmdConverter.Core.Editor;

namespace VeraFirefly.VmdConverter.Core.Editor.Nlu
{
    /// <summary>
    /// Public batch dispatcher for the external RootMotion and Muscles assemblies.
    /// The worker receives --nlu and all conversion options through this entry point.
    /// </summary>
    public static class VmdNluBatchPipeline
    {
        public static VmdExternalPoseBatch PrepareBatch(string executablePath,
            string pmxPath, IReadOnlyList<string> vmdPaths,
            string targetRigJsonPath, float sampleRate = 30f, int threads = 0,
            Func<int, int, bool> cancelRequested = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<string> expressionVmdPaths = null)
        {
            if (string.IsNullOrWhiteSpace(targetRigJsonPath))
                throw new ArgumentException("A Humanoid rig JSON is required.",
                    nameof(targetRigJsonPath));
            return VmdExternalPoseWorker.PrepareBatch(executablePath,
                pmxPath, vmdPaths, sampleRate, threads, cancelRequested,
                cancellationToken, targetRigJsonPath, expressionVmdPaths,
                noUnity: true);
        }

        public static string BuildCommandLine(string executablePath,
            string pmxPath, string vmdPath, string outputPath,
            string targetRigJsonPath, float sampleRate = 30f, int threads = 0,
            VmdExternalOutputKind outputKind = VmdExternalOutputKind.HumanoidIk)
        {
            return VmdConverterCore.BuildCommandLine(executablePath,
                pmxPath, vmdPath, outputPath, targetRigJsonPath,
                sampleRate, threads, outputKind, noUnity: true);
        }
    }
}
