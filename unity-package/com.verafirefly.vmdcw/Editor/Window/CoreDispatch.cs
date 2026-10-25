#if VMDCW_HAS_VMDCC
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace VeraFirefly.VmdConverter.Window.CoreDispatch
{
    // Optional package dispatch only. Conversion algorithms and public contracts
    // remain in VMDCC; this facade does not load .NET10 binaries into Unity.
    internal enum OutputKind
    {
        HumanoidIk, TargetCalibratedHumanoid, TargetFk, TargetCalibratedHumanoidAll,
        TargetFkAll, TargetCalibratedHumanoidArm, TargetFkArm,
        TargetCalibratedHumanoidLeg, TargetFkLeg
    }

    internal static class OutputModes
    {
        internal static OutputKind Parse(string kind)
        {
            string[] names = { "humanoid-ik", "target-humanoid", "target-fk", "target-humanoid-all",
                "target-fk-all", "target-humanoid-arm", "target-fk-arm", "target-humanoid-leg", "target-fk-leg" };
            int i = Array.IndexOf(names, kind);
            if (i < 0) throw new ArgumentException("Unknown output kind: " + kind);
            return (OutputKind)i;
        }
    }

    internal sealed class AnimOptions
    {
        public string ExecutablePath, PmxPath, FbxPath, HpsRigPath;
        public OutputKind OutputKind;
        public float SampleRate = 30, PositionScale = .08f, CurveError = .00001f;
        public float RotationErrorDegrees = .01f, PositionError = .00005f;
        public int CoordinateConversion = 2, Threads, Passes = 3;
        public bool BakeMorphs = true, FkContinuity = true;
        public string RangePolicy = "tangent", LegFit = "auto";
    }
    internal sealed class AnimJob { public string VmdPath, OutputPath, ExpressionVmdPath, MorphMapPath; }
    internal sealed class MorphTarget { public string Source, Name, Path; }

    internal static class Api
    {
        internal static Type Type(string name) => System.Type.GetType(
            "VeraFirefly.VmdConverter.Core.Editor." + name + ", com.verafirefly.vmdcc.editor", true);

        internal static object Convert(object source, string targetName)
        {
            var target = Activator.CreateInstance(Type(targetName));
            foreach (var input in source.GetType().GetFields())
            {
                var field = target.GetType().GetField(input.Name);
                if (field == null) throw new MissingFieldException(targetName, input.Name);
                object value = input.GetValue(source);
                if (field.FieldType.IsEnum) value = Enum.ToObject(field.FieldType, (int)value);
                field.SetValue(target, value);
            }
            return target;
        }

        internal static Array ConvertArray<T>(IReadOnlyList<T> values, string type)
        {
            var array = Array.CreateInstance(Type(type), values.Count);
            for (int i = 0; i < values.Count; i++) array.SetValue(Convert(values[i], type), i);
            return array;
        }

        internal static object Call(string type, string method, params object[] values)
        {
            try { return Type(type).GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, values); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
    }

    internal static class AnimConverter
    {
        internal static string BuildCommandLine(AnimOptions options, AnimJob job) => (string)Api.Call(
            "VmdStandaloneAnimConverter", "BuildCommandLine", Api.Convert(options, "VmdStandaloneAnimOptions"), Api.Convert(job, "VmdStandaloneAnimJob"));

        internal static void ConvertBatch(AnimOptions options, IReadOnlyList<AnimJob> jobs,
            CancellationToken cancellationToken = default, Func<int, int, bool> cancelRequested = null) => Api.Call(
                "VmdStandaloneAnimConverter", "ConvertBatch", Api.Convert(options, "VmdStandaloneAnimOptions"),
                Api.ConvertArray(jobs, "VmdStandaloneAnimJob"), cancellationToken, cancelRequested);

        internal static void WriteMorphMap(string path, IReadOnlyList<MorphTarget> targets) => Api.Call(
            "VmdStandaloneAnimConverter", "WriteMorphMap", path, Api.ConvertArray(targets, "VmdStandaloneMorphTarget"));
    }

    internal sealed class PoseBatch : IDisposable
    {
        readonly object inner;
        internal PoseBatch(object value) { inner = value; }
        internal string GetPosePath(string vmd) => (string)inner.GetType().GetMethod("GetPosePath").Invoke(inner, new object[] { vmd });
        public void Dispose() => ((IDisposable)inner).Dispose();
    }

    internal static class PoseWorker
    {
        internal static PoseBatch PrepareBatch(string workerExePath, string pmxPath,
            IReadOnlyList<string> vmdPaths, float sampleRate = 30, int threads = 0,
            Func<int, int, bool> cancelRequested = null) => new PoseBatch(Api.Call(
                "VmdExternalPoseWorker", "PrepareBatch", workerExePath, pmxPath, vmdPaths,
                sampleRate, threads, cancelRequested, default(CancellationToken), null, null, false));
    }

    internal static class SourceFbx
    {
        internal static string FindSourceFbx(UnityEngine.GameObject target) =>
            (string)Api.Call("VmdSourceFbxUtility", "FindSourceFbx", target);
    }
}
#endif
