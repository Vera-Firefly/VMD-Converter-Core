using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    public sealed class VmdStandaloneAnimOptions
    {
        public string ExecutablePath, PmxPath, FbxPath, HpsRigPath;
        public VmdExternalOutputKind OutputKind = VmdExternalOutputKind.HumanoidIk;
        public float SampleRate = 30, PositionScale = 0.08f, CurveError = 0.00001f;
        public float RotationErrorDegrees = 0.01f, PositionError = 0.00005f;
        public int CoordinateConversion = 2, Threads;
        public bool BakeMorphs = true, FkContinuity = true;
        public int Passes = 3;
        public string RangePolicy = "tangent", LegFit = "auto";
    }

    public sealed class VmdStandaloneAnimJob
    {
        public string VmdPath, OutputPath, ExpressionVmdPath, MorphMapPath;
    }

    public sealed class VmdStandaloneMorphTarget { public string Source, Name, Path; }

    /// <summary>
    /// Process/file-only .anim bridge. Contains no Unity APIs, Avatar calibration,
    /// AnimationClip construction or curve postprocessing. The caller imports files.
    /// </summary>
    public static class VmdStandaloneAnimConverter
    {
        public static string BuildCommandLine(VmdStandaloneAnimOptions options, VmdStandaloneAnimJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return Quote(Path.GetFullPath(options.ExecutablePath)) + " " + Arguments(options) +
                " --vmd " + Quote(Path.GetFullPath(job.VmdPath)) +
                " --output " + Quote(Path.GetFullPath(job.OutputPath)) +
                (string.IsNullOrEmpty(job.ExpressionVmdPath)?"":" --expression-vmd "+Quote(Path.GetFullPath(job.ExpressionVmdPath))) +
                (string.IsNullOrEmpty(job.MorphMapPath)?"":" --morph-map "+Quote(Path.GetFullPath(job.MorphMapPath)));
        }

        public static void ConvertBatch(VmdStandaloneAnimOptions options,
            IReadOnlyList<VmdStandaloneAnimJob> jobs, CancellationToken cancellationToken = default,
            Func<int, int, bool> cancelRequested = null)
        {
            if (options == null || jobs == null || jobs.Count == 0) throw new ArgumentException("At least one animation job is required.");
            if (!File.Exists(options.ExecutablePath)) throw new FileNotFoundException("VmdConverterCore.exe not found.", options.ExecutablePath);
            foreach (var job in jobs)
            {
                if (string.IsNullOrWhiteSpace(job.VmdPath) || string.IsNullOrWhiteSpace(job.OutputPath) || !string.Equals(Path.GetExtension(job.OutputPath), ".anim", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Every job requires a VMD and an .anim output path.");
            }
            string folder = Path.Combine(Path.GetTempPath(), "VmdccAnim", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string manifest = Path.Combine(folder, "jobs.json");
                File.WriteAllText(manifest, "{\"jobs\":[" + string.Join(",", jobs.Select(j =>
                    "{\"vmd\":" + Json(Path.GetFullPath(j.VmdPath)) + ",\"output\":" + Json(Path.GetFullPath(j.OutputPath)) +
                    ",\"expressionVmd\":" + Json(j.ExpressionVmdPath) + ",\"morphMap\":" + Json(j.MorphMapPath) + "}")) + "]}", new UTF8Encoding(false));
                var error = new StringBuilder();
                int done = 0;
                using (var process = new Process { StartInfo = new ProcessStartInfo {
                    FileName = Path.GetFullPath(options.ExecutablePath),
                    Arguments = Arguments(options) + " --jobs " + Quote(manifest),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true } })
                {
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) Interlocked.Increment(ref done); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
                    if (!process.Start()) throw new IOException("Cannot start VMD Converter Core.");
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    while (!process.WaitForExit(100))
                        if (cancellationToken.IsCancellationRequested || cancelRequested != null && cancelRequested(Volatile.Read(ref done), jobs.Count))
                        { process.Kill(); process.WaitForExit(); throw new OperationCanceledException(); }
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidOperationException("VMD Converter Core failed:\n" + error);
                }
                foreach (var job in jobs) if (!File.Exists(job.OutputPath)) throw new IOException("Missing animation output: " + job.OutputPath);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        public static void WriteMorphMap(string path, IReadOnlyList<VmdStandaloneMorphTarget> targets)
        {
            File.WriteAllText(path,"{\"mappings\":["+string.Join(",",targets.Select(m=>"{\"source\":"+Json(m.Source)+",\"name\":"+Json(m.Name)+",\"path\":"+Json(string.IsNullOrEmpty(m.Path)?"Body":m.Path)+"}"))+"]}",new UTF8Encoding(false));
        }

        private static string Arguments(VmdStandaloneAnimOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (float.IsNaN(o.SampleRate) || float.IsInfinity(o.SampleRate) || o.SampleRate <= 0 || o.SampleRate > 240 || o.Threads < 0 || o.Threads > 256)
                throw new ArgumentException("Invalid sample rate or threads.");
            bool hasFbx = !string.IsNullOrWhiteSpace(o.FbxPath);
            bool hasHpsRig = !string.IsNullOrWhiteSpace(o.HpsRigPath);
            if (hasFbx && hasHpsRig)
                throw new ArgumentException("Choose either an FBX or an HPS rig for standalone conversion.");
            if (o.OutputKind != VmdExternalOutputKind.HumanoidIk && !hasFbx && !hasHpsRig)
                throw new ArgumentException("Target Humanoid/FK output requires an FBX or HPS rig. Select a target model with a source FBX, or set HPS rig JSON in PMX settings.");
            if (hasFbx && !File.Exists(o.FbxPath)) throw new FileNotFoundException("Target FBX not found.", o.FbxPath);
            if (hasHpsRig && !File.Exists(o.HpsRigPath)) throw new FileNotFoundException("HPS rig not found.", o.HpsRigPath);
            string kind = VmdOutputModes.ToArgument(o.OutputKind);
            if (o.RangePolicy != "strict" && o.RangePolicy != "tangent" || o.LegFit != "auto" && o.LegFit != "rotation" && o.LegFit != "geometry" || o.Passes < 1 || o.Passes > 20)
                throw new ArgumentException("Invalid HPS fitting options.");
            return "--anim-w --pmx " + Quote(Path.GetFullPath(o.PmxPath)) + " --output-kind " + kind +
                " --sample-rate " + o.SampleRate.ToString(CultureInfo.InvariantCulture) +
                " --threads " + (o.Threads > 0 ? o.Threads : Math.Max(1, Environment.ProcessorCount - 1)) +
                " --position-scale " + o.PositionScale.ToString(CultureInfo.InvariantCulture) +
                " --coordinate " + o.CoordinateConversion + " --curve-error " + o.CurveError.ToString(CultureInfo.InvariantCulture) +
                " --rotation-error " + o.RotationErrorDegrees.ToString(CultureInfo.InvariantCulture) +
                " --position-error " + o.PositionError.ToString(CultureInfo.InvariantCulture) +
                " --bake-morphs " + (o.BakeMorphs ? "true" : "false") +
                " --passes " + o.Passes + " --range-policy " + o.RangePolicy + " --leg-fit " + o.LegFit +
                " --fk-continuity " + (o.FkContinuity ? "true" : "false") +
                (string.IsNullOrEmpty(o.FbxPath) ? "" : " --fbx " + Quote(Path.GetFullPath(o.FbxPath))) +
                (string.IsNullOrEmpty(o.HpsRigPath) ? "" : " --hps-rig " + Quote(Path.GetFullPath(o.HpsRigPath)));
        }
        private static string Quote(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('"') >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
                throw new ArgumentException("Invalid command path.");
            return "\"" + text + "\"";
        }
        private static string Json(string text)
        {
            if (text == null) return "null";
            var result = new StringBuilder("\"");
            foreach (char c in text)
                if (c == '"' || c == '\\') result.Append('\\').Append(c);
                else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            return result.Append('"').ToString();
        }
    }
}
