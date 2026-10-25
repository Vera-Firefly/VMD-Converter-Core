using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    [Serializable]
    internal sealed class ExternalPoseJob
    {
        public string vmd;
        public string output;
        public string expressionVmd;
    }

    [Serializable]
    internal sealed class ExternalPoseJobManifest
    {
        public List<ExternalPoseJob> jobs = new List<ExternalPoseJob>();
    }

    /// <summary>Temporary pose files for one conversion batch.</summary>
    public sealed class VmdExternalPoseBatch : IDisposable
    {
        private readonly string folder;
        private readonly Dictionary<string, string> paths;

        internal VmdExternalPoseBatch(string folder, Dictionary<string, string> paths)
        {
            this.folder = folder;
            this.paths = paths;
        }

        public string GetPosePath(string physicalVmdPath) =>
            paths[Path.GetFullPath(physicalVmdPath)];

        public void Dispose()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /// <summary>Runs the optional lightweight IK worker once for an entire VMD batch.</summary>
    public static class VmdExternalPoseWorker
    {
        public static VmdExternalPoseBatch PrepareBatch(string workerExePath,
            string pmxPath, IReadOnlyList<string> vmdPaths, float sampleRate = 30f,
            int threads = 0, Func<int, int, bool> cancelRequested = null,
            CancellationToken cancellationToken = default,
            string targetRigPath = null,
            IReadOnlyList<string> expressionVmdPaths = null,
            bool noUnity = false)
        {
            if (noUnity && string.IsNullOrWhiteSpace(targetRigPath))
                throw new ArgumentException("--nlu requires a Humanoid rig JSON.",
                    nameof(targetRigPath));
            if (string.IsNullOrWhiteSpace(workerExePath) || !File.Exists(workerExePath))
                throw new FileNotFoundException("VMD worker EXE was not found.", workerExePath);
            if (vmdPaths == null || vmdPaths.Count == 0)
                throw new ArgumentException("At least one VMD path is required.", nameof(vmdPaths));
            if (expressionVmdPaths != null && expressionVmdPaths.Count != vmdPaths.Count)
                throw new ArgumentException("Expression VMD paths must match the job count.");
            if (threads < 0) throw new ArgumentOutOfRangeException(nameof(threads));
            string physicalPmx = ToPhysicalPath(pmxPath);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string cacheRoot = Path.GetFullPath(Path.Combine(
                projectRoot, "Library", "VmdPoseCache"));
            string folder = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var manifest = new ExternalPoseJobManifest();
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < vmdPaths.Count; index++)
                {
                    string physicalVmd = Path.GetFullPath(
                        ToPhysicalPath(vmdPaths[index]));
                    if (paths.ContainsKey(physicalVmd))
                        throw new ArgumentException("Duplicate VMD path: " + physicalVmd);
                    string output = Path.Combine(folder, index.ToString("D6") +
                        (targetRigPath == null ? ".vccpose" : ".vcccurves"));
                    string expression = expressionVmdPaths == null ||
                        string.IsNullOrEmpty(expressionVmdPaths[index]) ? null :
                        Path.GetFullPath(ToPhysicalPath(
                            expressionVmdPaths[index]));
                    manifest.jobs.Add(new ExternalPoseJob
                    {
                        vmd = physicalVmd,
                        output = output,
                        expressionVmd = expression
                    });
                    paths.Add(physicalVmd, output);
                }
                string manifestPath = Path.Combine(folder, "jobs.json");
                File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest), new UTF8Encoding(false));
                string outputKind = string.Empty;
                if (targetRigPath != null)
                {
                    VmdTargetRigData rig = JsonUtility.FromJson<VmdTargetRigData>(
                        File.ReadAllText(targetRigPath));
                    if (rig == null) throw new InvalidDataException("Invalid target rig JSON.");
                    outputKind = VmdOutputModes.ToArgument(rig.outputKind);
                    if (noUnity && VmdOutputModes.IsFk(rig.outputKind))
                        throw new ArgumentException("--nlu does not apply to target FK.");
                }
                var errorLog = new StringBuilder();
                var start = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(workerExePath),
                    Arguments = "--pmx " + Quote(physicalPmx) + " --jobs " +
                                Quote(manifestPath) + " --sample-rate " +
                                sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                " --threads " + (threads > 0 ? threads : Math.Max(1,
                                    Environment.ProcessorCount - 1)) +
                                (targetRigPath == null ? string.Empty :
                                    " --rig " + Quote(Path.GetFullPath(targetRigPath)) +
                                    " --output-kind " + outputKind) +
                                (noUnity ? " --nlu" : string.Empty),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += (_, args) => { };
                    process.ErrorDataReceived += (_, args) =>
                    { if (args.Data != null) lock (errorLog) errorLog.AppendLine(args.Data); };
                    if (!process.Start()) throw new IOException("The VMD worker could not start.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    while (!process.WaitForExit(100))
                    {
                        int completed = manifest.jobs.Count(job => File.Exists(job.output));
                        if (cancellationToken.IsCancellationRequested ||
                            cancelRequested != null && cancelRequested(completed, manifest.jobs.Count))
                        {
                            process.Kill();
                            process.WaitForExit();
                            throw new OperationCanceledException("External pose preparation was cancelled.");
                        }
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("VMD worker failed: " + errorLog);
                }
                foreach (ExternalPoseJob job in manifest.jobs)
                    if (!File.Exists(job.output))
                        throw new InvalidDataException("VMD worker did not write " + job.output);
                return new VmdExternalPoseBatch(folder, paths);
            }
            catch
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                throw;
            }
        }

        private static string Quote(string path)
        {
            if (path.IndexOf('"') >= 0)
                throw new ArgumentException("Windows paths cannot contain quote characters.");
            return "\"" + path + "\"";
        }

        private static string ToPhysicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A file path is required.", nameof(path));
            return Path.GetFullPath(Path.IsPathRooted(path) ? path :
                Path.Combine(Application.dataPath, "..", path));
        }
    }
}
