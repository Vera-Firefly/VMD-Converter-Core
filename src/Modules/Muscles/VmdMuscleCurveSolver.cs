using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;

namespace VeraFirefly.VmdConverter.Core.Nlu;

/// <summary>Independent, bounded inverse fit from solved bone rotations to muscles.</summary>
public static class VmdMuscleCurveSolver
{
    public static IReadOnlyList<VmdFloatCurveData> Solve(PmxModel model,
        VmdMotion motion, PmxPose[] samples, VmdTargetRigData rig,
        int threads, bool canonical)
    {
        if (model == null || motion == null || samples == null || rig == null ||
            rig.humanoid?.muscles == null)
            throw new ArgumentNullException("Muscle inputs are required.");
        VmdTargetFkCurveSolver.ResolveSourceBindings(model, rig);
        bool targetCalibrated = rig.outputKind ==
                                VmdExternalOutputKind.TargetCalibratedHumanoid;
        bool suppressLeft = targetCalibrated &&
            (rig.suppressLeftWaistCancellation ||
             VmdTargetFkCurveSolver.ShouldSuppressWaistCancellation(model, motion, true));
        bool suppressRight = targetCalibrated &&
            (rig.suppressRightWaistCancellation ||
             VmdTargetFkCurveSolver.ShouldSuppressWaistCancellation(model, motion, false));
        var calibration = rig.humanoid;
        var groups = calibration.muscles.Select((muscle, index) =>
                new { muscle, index })
            .Where(pair => pair.muscle != null && pair.muscle.bindingIndex >= 0 &&
                pair.muscle.bindingIndex < rig.bones.Length &&
                rig.bones[pair.muscle.bindingIndex].sourceBoneIndex >= 0 &&
                !string.IsNullOrEmpty(pair.muscle.property))
            .GroupBy(pair => pair.muscle.bindingIndex).ToArray();
        var times = new float[samples.Length];
        float duration = motion.MaximumFrame / 30f;
        for (int i = 0; i < times.Length; i++)
            times[i] = i == times.Length - 1 ? duration : i / rig.sampleRate;
        var output = new VmdFloatCurveData[calibration.muscles.Length];
        Parallel.ForEach(groups, new ParallelOptions
        {
            MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount
        }, group =>
        {
            int bindingIndex = group.Key;
            var channels = group.ToArray();
            var values = new float[channels.Length][];
            var positive = new Vector3[channels.Length];
            var negative = new Vector3[channels.Length];
            for (int c = 0; c < channels.Length; c++)
            {
                values[c] = new float[samples.Length];
                positive[c] = LogRotation(channels[c].muscle.positiveDelta);
                negative[c] = LogRotation(channels[c].muscle.negativeDelta);
            }
            var fitted = new float[channels.Length];
            for (int frame = 0; frame < samples.Length; frame++)
            {
                Quaternion desired = canonical
                    ? EvaluateCanonicalLocalRotation(samples[frame], rig, bindingIndex)
                    : VmdTargetFkCurveSolver.EvaluateLocalRotation(
                        samples[frame], rig, bindingIndex,
                        suppressLeft, suppressRight);
                Vector3 target = LogRotation(Quaternion.Inverse(
                    channels[0].muscle.zeroLocalRotation) * desired);
                FitSignedAxes(target, positive, negative, fitted);
                for (int c = 0; c < channels.Length; c++)
                    values[c][frame] = fitted[c];
            }
            for (int c = 0; c < channels.Length; c++)
            {
                float[] channel = values[c];
                if (channel.All(value => Math.Abs(value) <= 1e-6f)) continue;
                float tolerance = Math.Min(Math.Max(0f, rig.muscleError), 0.00001f);
                int[] keys = tolerance <= 0f
                    ? Enumerable.Range(0, samples.Length).ToArray()
                    : VmdTargetFkCurveSolver.Reduce(times, samples.Length,
                        (start, end, i) =>
                        {
                            float span = times[end] - times[start];
                            float t = Math.Abs(span) > 1e-12f
                                ? (times[i] - times[start]) / span : 0f;
                            return Math.Abs(channel[i] -
                                (channel[start] + (channel[end] - channel[start]) * t));
                        }, tolerance);
                output[channels[c].index] = VmdTargetFkCurveSolver.BuildCurve(
                    string.Empty, channels[c].muscle.property, 2,
                    times, keys, i => channel[i]);
            }
        });
        return output.Where(curve => curve != null).ToArray();
    }

    private static void FitSignedAxes(Vector3 target, Vector3[] positive,
        Vector3[] negative, float[] output)
    {
        int count = positive.Length;
        if (count == 0) return;
        int masks = 1 << Math.Min(count, 4);
        float bestError = float.PositiveInfinity;
        var candidate = new float[count];
        for (int mask = 0; mask < masks; mask++)
        {
            var axes = new Vector3[count];
            for (int c = 0; c < count; c++)
                axes[c] = (mask & (1 << c)) == 0 ? positive[c] : negative[c];
            Array.Clear(candidate);
            Vector3 residual = target;
            for (int pass = 0; pass < 12; pass++)
            {
                for (int c = 0; c < count; c++)
                {
                    residual += axes[c] * candidate[c];
                    float denominator = axes[c].sqrMagnitude;
                    candidate[c] = denominator > 1e-10f
                        ? Math.Clamp(Vector3.Dot(residual, axes[c]) / denominator,
                            0f, 1f) : 0f;
                    residual -= axes[c] * candidate[c];
                }
            }
            float error = residual.sqrMagnitude;
            if (error >= bestError) continue;
            bestError = error;
            for (int c = 0; c < count; c++)
                output[c] = (mask & (1 << c)) == 0
                    ? candidate[c] : -candidate[c];
        }
    }

    private static Quaternion EvaluateCanonicalLocalRotation(PmxPose sample,
        VmdTargetRigData rig, int bindingIndex)
    {
        VmdTargetBoneBinding bone = rig.bones[bindingIndex];
        if (bone.syntheticThumbProximal) return Quaternion.identity;
        Quaternion world = VmdTargetFkCurveSolver.ConvertRotation(
            sample.WorldRotations[bone.sourceBoneIndex], 2);
        if (bone.parentBindingIndex < 0) return world;
        VmdTargetBoneBinding parent = rig.bones[bone.parentBindingIndex];
        if (parent.syntheticThumbProximal && parent.parentBindingIndex >= 0)
            parent = rig.bones[parent.parentBindingIndex];
        Quaternion parentWorld = VmdTargetFkCurveSolver.ConvertRotation(
            sample.WorldRotations[parent.sourceBoneIndex], 2);
        return VmdTargetFkCurveSolver.Normalize(
            Quaternion.Inverse(parentWorld) * world);
    }

    private static Vector3 LogRotation(Quaternion rotation)
    {
        Quaternion q = VmdTargetFkCurveSolver.Normalize(rotation);
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        float sine = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
        if (sine < 1e-8f) return new Vector3(q.x * 2f, q.y * 2f, q.z * 2f);
        float angle = 2f * (float)Math.Atan2(sine, q.w);
        return new Vector3(q.x, q.y, q.z) * (angle / sine);
    }
}
