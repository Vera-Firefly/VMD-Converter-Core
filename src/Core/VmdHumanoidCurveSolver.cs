using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using VeraFirefly.VmdConverter.Core.Nlu;

namespace VeraFirefly.VmdConverter.Core
{
    /// <summary>
    /// Converts already-solved PMX/IK poses to Animator root and muscle curves.
    /// The target Avatar's static HumanPose response is measured once in Unity and
    /// supplied in the rig JSON; no Unity Avatar API runs in this executable.
    /// </summary>
    public static class VmdHumanoidCurveSolver
    {
        /// <summary>Pure managed Root/Muscle DLL pipeline selected by --nlu.</summary>
        public static VmdTargetCurveData SolveNlu(PmxModel model, VmdMotion motion,
            PmxPose[] samples, VmdTargetRigData rig, int threads = 0)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (rig.canonicalRig == null)
                return SolveNluCore(model, motion, samples, rig, threads, false);
            VmdTargetCurveData canonical = SolveNluCore(model, motion, samples,
                rig.canonicalRig, threads, true);
            if (rig.outputKind == VmdExternalOutputKind.HumanoidIk)
                return canonical;
            VmdTargetCurveData calibrated = SolveNluCore(model, motion, samples,
                rig, threads, false);
            calibrated.Curves.RemoveAll(curve => IsUpperLimb(curve.Property));
            calibrated.Curves.AddRange(canonical.Curves.Where(curve =>
                IsUpperLimb(curve.Property)));
            return calibrated;
        }

        private static VmdTargetCurveData SolveNluCore(PmxModel model,
            VmdMotion motion, PmxPose[] samples, VmdTargetRigData rig,
            int threads, bool canonical)
        {
            if (samples == null || samples.Length == 0 || rig.humanoid == null)
                throw new ArgumentException("NLU Humanoid output needs poses and calibration.");
            VmdTargetFkCurveSolver.ResolveSourceBindings(model, rig);
            var result = new VmdTargetCurveData
            {
                SampleRate = rig.sampleRate,
                SampleCount = samples.Length,
                BoundBoneCount = rig.bones.Count(b => b.sourceBoneIndex >= 0)
            };
            result.Curves.AddRange(VmdRootMotionSolver.Solve(
                model, motion, samples, rig, canonical));
            result.Curves.AddRange(VmdMuscleCurveSolver.Solve(
                model, motion, samples, rig, threads, canonical));
            if (rig.bakeMorphs)
                VmdTargetFkCurveSolver.AddMorphCurves(motion, rig, result);
            return result;
        }

        public static VmdTargetCurveData Solve(PmxModel model, VmdMotion motion,
            PmxPose[] samples, VmdTargetRigData rig, int threads = 0)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (rig.canonicalRig != null)
            {
                VmdTargetCurveData canonical = SolveCore(model, motion, samples,
                    rig.canonicalRig, threads, true);
                if (rig.outputKind == VmdExternalOutputKind.HumanoidIk)
                    return canonical;
                VmdTargetCurveData calibrated = SolveCore(model, motion, samples,
                    rig, threads, false);
                calibrated.Curves.RemoveAll(curve => IsUpperLimb(curve.Property));
                calibrated.Curves.AddRange(canonical.Curves.Where(curve =>
                    IsUpperLimb(curve.Property)));
                return calibrated;
            }
            return SolveCore(model, motion, samples, rig, threads, false);
        }

        private static bool IsUpperLimb(string property)
        {
            if (string.IsNullOrEmpty(property)) return false;
            if (property.StartsWith("LeftHand.", StringComparison.Ordinal) ||
                property.StartsWith("RightHand.", StringComparison.Ordinal))
                return true;
            string[] prefixes =
            {
                "Left Shoulder ", "Right Shoulder ",
                "Left Arm ", "Right Arm ",
                "Left Forearm ", "Right Forearm ",
                "Left Hand ", "Right Hand "
            };
            return prefixes.Any(prefix => property.StartsWith(prefix,
                StringComparison.Ordinal));
        }

        private static VmdTargetCurveData SolveCore(PmxModel model, VmdMotion motion,
            PmxPose[] samples, VmdTargetRigData rig, int threads, bool canonical)
        {
            if (model == null || motion == null || samples == null || rig == null)
                throw new ArgumentNullException("Humanoid inputs are required.");
            if (samples.Length == 0 || rig.sampleRate <= 0f ||
                rig.bones == null || rig.bones.Length == 0 ||
                rig.humanoid == null || rig.humanoid.muscles == null ||
                rig.humanoid.muscles.Length == 0)
                throw new ArgumentException("Humanoid output requires target calibration data.");
            VmdTargetFkCurveSolver.ResolveSourceBindings(model, rig);
            VmdHumanoidCalibrationData calibration = rig.humanoid;
            var result = new VmdTargetCurveData
            {
                SampleRate = rig.sampleRate,
                SampleCount = samples.Length,
                BoundBoneCount = rig.bones.Count(b => b.sourceBoneIndex >= 0)
            };
            float duration = motion.MaximumFrame / 30f;
            var times = new float[samples.Length];
            for (int i = 0; i < times.Length; i++)
                times[i] = i == times.Length - 1 ? duration : i / rig.sampleRate;

            VmdTargetBoneBinding hips = rig.bones.First(b => b.humanBone == 0);
            if (canonical)
            {
                rig.rootMotionSourceBoneIndex = hips.sourceBoneIndex;
                rig.rootMotionSourceRestPosition =
                    model.Bones[hips.sourceBoneIndex].Position;
                rig.rootMotionTargetScale = 1f;
            }
            bool targetCalibrated =
                rig.outputKind == VmdExternalOutputKind.TargetCalibratedHumanoid;
            var rootPositions = new Vector3[samples.Length];
            var rootRotations = new Quaternion[samples.Length];
            var currentBonePositions = new Vector3[rig.bones.Length];
            var resolvedBonePositions = new bool[rig.bones.Length];
            for (int frame = 0; frame < samples.Length; frame++)
            {
                Array.Clear(resolvedBonePositions, 0, resolvedBonePositions.Length);
                Vector3 displacement = rig.bakeRootTranslation
                    ? VmdTargetFkCurveSolver.EvaluateRootPosition(samples[frame], rig, hips) -
                      hips.restLocalPosition : Vector3.zero;
                rootPositions[frame] = calibration.restBodyPosition +
                    calibration.bodyPositionAxisX * displacement.x +
                    calibration.bodyPositionAxisY * displacement.y +
                    calibration.bodyPositionAxisZ * displacement.z +
                    CenterOfMassOffset(model, samples[frame], rig,
                        calibration, canonical, displacement,
                        currentBonePositions, resolvedBonePositions);
                Quaternion sourceWorld = VmdTargetFkCurveSolver.ConvertRotation(
                    samples[frame].WorldRotations[hips.sourceBoneIndex],
                    rig.coordinateConversion);
                Quaternion body = VmdTargetFkCurveSolver.Normalize(
                    sourceWorld * calibration.restBodyRotation);
                if (frame > 0 && VmdTargetFkCurveSolver.Dot(rootRotations[frame - 1], body) < 0f)
                    body = new Quaternion(-body.x, -body.y, -body.z, -body.w);
                rootRotations[frame] = body;
            }
            string[] positionNames = { "RootT.x", "RootT.y", "RootT.z" };
            string[] rotationNames = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            for (int c = 0; c < 3; c++)
            {
                int component = c;
                float[] values = rootPositions.Select(p => Component(p, component)).ToArray();
                result.Curves.Add(VmdTargetFkCurveSolver.BuildCurve(string.Empty,
                    positionNames[c], 2, times,
                    ReduceScalar(times, values, rig.muscleError), i => values[i]));
            }
            for (int c = 0; c < 4; c++)
            {
                int component = c;
                float[] values = rootRotations.Select(q => Component(q, component)).ToArray();
                result.Curves.Add(VmdTargetFkCurveSolver.BuildCurve(string.Empty,
                    rotationNames[c], 2, times,
                    ReduceScalar(times, values, rig.muscleError), i => values[i]));
            }

            bool suppressLeft = targetCalibrated &&
                (rig.suppressLeftWaistCancellation ||
                 VmdTargetFkCurveSolver.ShouldSuppressWaistCancellation(model, motion, true));
            bool suppressRight = targetCalibrated &&
                (rig.suppressRightWaistCancellation ||
                 VmdTargetFkCurveSolver.ShouldSuppressWaistCancellation(model, motion, false));
            var groups = calibration.muscles
                .Select((muscle, index) => new { muscle, index })
                .Where(pair => pair.muscle != null && pair.muscle.bindingIndex >= 0 &&
                    pair.muscle.bindingIndex < rig.bones.Length &&
                    rig.bones[pair.muscle.bindingIndex].sourceBoneIndex >= 0 &&
                    !string.IsNullOrEmpty(pair.muscle.property))
                .GroupBy(pair => pair.muscle.bindingIndex)
                .ToArray();
            var muscleCurves = new VmdFloatCurveData[calibration.muscles.Length];
            Parallel.ForEach(groups, new ParallelOptions
            {
                MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount
            }, group =>
            {
                int bindingIndex = group.Key;
                var channels = group.ToArray();
                var values = new float[channels.Length][];
                for (int i = 0; i < values.Length; i++)
                    values[i] = new float[samples.Length];
                var contribution = new Vector3[channels.Length];
                for (int frame = 0; frame < samples.Length; frame++)
                {
                    Array.Clear(contribution, 0, contribution.Length);
                    Quaternion desired = canonical
                        ? EvaluateCanonicalLocalRotation(samples[frame], rig,
                            bindingIndex)
                        : VmdTargetFkCurveSolver.EvaluateLocalRotation(
                            samples[frame], rig, bindingIndex,
                            suppressLeft, suppressRight);
                    Vector3 residual = LogRotation(Quaternion.Inverse(
                        channels[0].muscle.zeroLocalRotation) * desired);
                    for (int pass = 0; pass < 5; pass++)
                    {
                        for (int c = 0; c < channels.Length; c++)
                        {
                            residual += contribution[c];
                            VmdMuscleCalibration channel = channels[c].muscle;
                            Vector3 positive = LogRotation(channel.positiveDelta);
                            Vector3 negative = LogRotation(channel.negativeDelta);
                            float positiveAmount = Projection(residual, positive);
                            float negativeAmount = Projection(residual, negative);
                            Vector3 positiveFit = positive * positiveAmount;
                            Vector3 negativeFit = negative * negativeAmount;
                            if ((residual - positiveFit).sqrMagnitude <=
                                (residual - negativeFit).sqrMagnitude)
                            {
                                contribution[c] = positiveFit;
                                values[c][frame] = positiveAmount;
                            }
                            else
                            {
                                contribution[c] = negativeFit;
                                values[c][frame] = -negativeAmount;
                            }
                            residual -= contribution[c];
                        }
                    }
                }
                for (int c = 0; c < channels.Length; c++)
                {
                    float[] channelValues = values[c];
                    if (channelValues.All(value => Math.Abs(value) <= 1e-6f)) continue;
                    int[] keys = ReduceScalar(times, channelValues, rig.muscleError);
                    muscleCurves[channels[c].index] = VmdTargetFkCurveSolver.BuildCurve(
                        string.Empty, channels[c].muscle.property, 2,
                        times, keys, i => channelValues[i]);
                }
            });
            foreach (VmdFloatCurveData curve in muscleCurves)
                if (curve != null) result.Curves.Add(curve);
            if (rig.bakeMorphs)
                VmdTargetFkCurveSolver.AddMorphCurves(motion, rig, result);
            return result;
        }

        private static float Projection(Vector3 residual, Vector3 axis)
        {
            float length = axis.sqrMagnitude;
            return length > 1e-10f
                ? Math.Clamp(Vector3.Dot(residual, axis) / length, 0f, 1f) : 0f;
        }

        private static Quaternion EvaluateCanonicalLocalRotation(PmxPose sample,
            VmdTargetRigData rig, int bindingIndex)
        {
            VmdTargetBoneBinding bone = rig.bones[bindingIndex];
            if (bone.syntheticThumbProximal)
                return Quaternion.identity;
            // The built-in PMX Humanoid proxy assigns solved PMX world rotations
            // directly to its bones. Unlike target FK, it does not multiply by
            // the Avatar's rest-world rotation.
            Quaternion world = VmdTargetFkCurveSolver.ConvertRotation(
                sample.WorldRotations[bone.sourceBoneIndex], 2);
            if (bone.parentBindingIndex < 0) return world;
            VmdTargetBoneBinding parent = rig.bones[bone.parentBindingIndex];
            if (parent.syntheticThumbProximal &&
                parent.parentBindingIndex >= 0)
                parent = rig.bones[parent.parentBindingIndex];
            Quaternion parentWorld = VmdTargetFkCurveSolver.ConvertRotation(
                sample.WorldRotations[parent.sourceBoneIndex], 2);
            return VmdTargetFkCurveSolver.Normalize(
                Quaternion.Inverse(parentWorld) * world);
        }

        private static Vector3 CenterOfMassOffset(PmxModel model, PmxPose sample,
            VmdTargetRigData rig, VmdHumanoidCalibrationData calibration,
            bool canonical, Vector3 rootDisplacement,
            Vector3[] positions, bool[] resolved)
        {
            if (calibration.bodyInfluences == null) return Vector3.zero;
            Vector3 result = Vector3.zero;
            foreach (VmdBodyPositionInfluence influence in calibration.bodyInfluences)
            {
                if (influence == null || influence.bindingIndex < 0 ||
                    influence.bindingIndex >= rig.bones.Length) continue;
                int index = influence.bindingIndex;
                if (rig.bones[index].sourceBoneIndex < 0) continue;
                VmdTargetBoneBinding bone = rig.bones[index];
                if (canonical && bone.syntheticThumbProximal) continue;
                Vector3 delta;
                if (canonical)
                {
                    int source = bone.sourceBoneIndex;
                    Vector3 moved = sample.WorldPositions[source] -
                                    model.Bones[source].Position;
                    // The built-in canonical proxy receives PMX world positions
                    // directly. Remove common Hips travel, which is already
                    // represented by bodyPositionAxisX/Y/Z above.
                    delta = new Vector3(-moved.x, moved.y, -moved.z) *
                            rig.positionScale - rootDisplacement;
                }
                else
                    delta = CurrentPosition(sample, rig, index,
                        positions, resolved) - bone.restWorldPosition;
                result += influence.axisX * delta.x +
                    influence.axisY * delta.y + influence.axisZ * delta.z;
            }
            return result;
        }

        private static Vector3 CurrentPosition(PmxPose sample,
            VmdTargetRigData rig, int index, Vector3[] positions, bool[] resolved)
        {
            if (resolved[index]) return positions[index];
            resolved[index] = true;
            VmdTargetBoneBinding bone = rig.bones[index];
            int parentIndex = bone.parentBindingIndex;
            if (parentIndex < 0 || rig.bones[parentIndex].sourceBoneIndex < 0)
                return positions[index] = bone.restWorldPosition;
            VmdTargetBoneBinding parent = rig.bones[parentIndex];
            Vector3 parentPosition = CurrentPosition(sample, rig, parentIndex,
                positions, resolved);
            Quaternion parentRotation = VmdTargetFkCurveSolver.ConvertRotation(
                sample.WorldRotations[parent.sourceBoneIndex], rig.coordinateConversion);
            return positions[index] = parentPosition + parentRotation *
                (bone.restWorldPosition - parent.restWorldPosition);
        }

        private static int[] ReduceScalar(float[] times, float[] values, float error)
        {
            if (values.Length <= 2 || error <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            return VmdTargetFkCurveSolver.Reduce(times, values.Length,
                (start, end, i) =>
                {
                    float denominator = times[end] - times[start];
                    float t = Math.Abs(denominator) > 1e-12f
                        ? (times[i] - times[start]) / denominator : 0f;
                    return Math.Abs(values[i] -
                        (values[start] + (values[end] - values[start]) * t));
                }, error);
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

        private static float Component(Vector3 value, int c) =>
            c == 0 ? value.x : c == 1 ? value.y : value.z;
        private static float Component(Quaternion value, int c) =>
            c == 0 ? value.x : c == 1 ? value.y : c == 2 ? value.z : value.w;
    }
}
