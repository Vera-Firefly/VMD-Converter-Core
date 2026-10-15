using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core
{
    /// <summary>Pure managed target-Avatar FK projection; safe to run outside Unity.</summary>
    public static class VmdTargetFkCurveSolver
    {
        public static VmdTargetCurveData Solve(PmxModel model, VmdMotion motion,
            PmxPose[] samples, VmdTargetRigData rig, int threads = 0)
        {
            if (model == null || motion == null || samples == null || rig == null)
                throw new ArgumentNullException("Target FK inputs are required.");
            if (samples.Length == 0 || rig.bones == null || rig.bones.Length == 0)
                throw new ArgumentException("Target FK requires samples and bone bindings.");
            if (rig.sampleRate <= 0f)
                throw new ArgumentException("Invalid target rig sample rate.");
            ResolveSourceBindings(model, rig);
            var result = new VmdTargetCurveData
            {
                SampleRate = rig.sampleRate,
                SampleCount = samples.Length
            };
            float duration = motion.MaximumFrame / 30f;
            var times = new float[samples.Length];
            for (int i = 0; i < times.Length; i++)
                times[i] = i == times.Length - 1 ? duration : i / rig.sampleRate;
            var perBone = new List<VmdFloatCurveData>[rig.bones.Length];
            bool suppressLeft = rig.suppressLeftWaistCancellation ||
                ShouldSuppressWaistCancellation(model, motion, true);
            bool suppressRight = rig.suppressRightWaistCancellation ||
                ShouldSuppressWaistCancellation(model, motion, false);
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount
            };
            Parallel.For(0, rig.bones.Length, parallel, bindingIndex =>
            {
                VmdTargetBoneBinding bone = rig.bones[bindingIndex];
                if (bone.sourceBoneIndex < 0) return;
                var rotations = new Quaternion[samples.Length];
                Vector3[] positions = bone.humanBone == 0 && rig.bakeRootTranslation
                    ? new Vector3[samples.Length] : null;
                for (int i = 0; i < samples.Length; i++)
                {
                    Quaternion local = EvaluateLocalRotation(samples[i], rig,
                        bindingIndex, suppressLeft, suppressRight);
                    if (i > 0 && Dot(rotations[i - 1], local) < 0f)
                        local = new Quaternion(-local.x, -local.y, -local.z, -local.w);
                    rotations[i] = local;
                    if (positions != null)
                        positions[i] = EvaluateRootPosition(samples[i], rig, bone);
                }
                var curves = new List<VmdFloatCurveData>(7);
                if (Differs(rotations, bone.restLocalRotation))
                {
                    int[] indices = ReduceQuaternion(times, rotations,
                        Math.Max(0f, rig.rotationErrorDegrees));
                    string[] names = { "m_LocalRotation.x", "m_LocalRotation.y",
                        "m_LocalRotation.z", "m_LocalRotation.w" };
                    for (int component = 0; component < 4; component++)
                        curves.Add(BuildCurve(bone.path, names[component], 0, times,
                            indices, i => Component(rotations[i], component)));
                }
                if (positions != null && Differs(positions, bone.restLocalPosition))
                {
                    int[] indices = ReduceVector(times, positions,
                        Math.Max(0f, rig.positionError));
                    string[] names = { "m_LocalPosition.x", "m_LocalPosition.y",
                        "m_LocalPosition.z" };
                    for (int component = 0; component < 3; component++)
                        curves.Add(BuildCurve(bone.path, names[component], 0, times,
                            indices, i => Component(positions[i], component)));
                }
                perBone[bindingIndex] = curves;
            });
            foreach (List<VmdFloatCurveData> curves in perBone)
            {
                if (curves == null || curves.Count == 0) continue;
                result.BoundBoneCount++;
                result.Curves.AddRange(curves);
            }
            if (rig.bakeMorphs) AddMorphCurves(motion, rig, result);
            return result;
        }

        internal static void ResolveSourceBindings(PmxModel model, VmdTargetRigData rig)
        {
            var used = new HashSet<int>();
            for (int index = 0; index < rig.bones.Length; index++)
            {
                VmdTargetBoneBinding bone = rig.bones[index];
                if (bone.sourceCandidates != null && bone.sourceCandidates.Length > 0)
                {
                    bone.sourceBoneIndex = -1;
                    foreach (string candidate in bone.sourceCandidates)
                    {
                        int source = model.FindBone(candidate);
                        if (source < 0 || used.Contains(source)) continue;
                        bone.sourceBoneIndex = source;
                        used.Add(source);
                        break;
                    }
                }
                else if (bone.sourceBoneIndex < 0 ||
                         bone.sourceBoneIndex >= model.Bones.Count)
                    bone.sourceBoneIndex = -1;
            }
            VmdTargetBoneBinding hips = null;
            for (int index = 0; index < rig.bones.Length; index++)
            {
                VmdTargetBoneBinding bone = rig.bones[index];
                int parent = bone.parentBindingIndex;
                while (parent >= 0 && rig.bones[parent].sourceBoneIndex < 0)
                    parent = rig.bones[parent].parentBindingIndex;
                bone.parentBindingIndex = parent;
                if (bone.humanBone == 0 && bone.sourceBoneIndex >= 0) hips = bone;
            }
            if (hips == null)
                throw new InvalidOperationException("No source/target Hips mapping.");
            int root = hips.sourceBoneIndex;
            for (int current = root; current >= 0 && current < model.Bones.Count;
                 current = model.Bones[current].ParentIndex)
            {
                PmxBone source = model.Bones[current];
                if (!string.Equals(source.NameJapanese, "腰", StringComparison.Ordinal) &&
                    !string.Equals(source.NameEnglish, "waist", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(source.NameEnglish, "hips", StringComparison.OrdinalIgnoreCase))
                    continue;
                root = current;
                break;
            }
            rig.rootMotionSourceBoneIndex = root;
            rig.rootMotionSourceRestPosition = model.Bones[root].Position;
            float sourceHeight = Math.Abs(
                rig.rootMotionSourceRestPosition.y * rig.positionScale);
            rig.rootMotionTargetScale = sourceHeight > 1e-5f &&
                                        rig.targetHipsHeight > 1e-5f
                ? rig.targetHipsHeight / sourceHeight : 1f;
        }

        internal static Quaternion EvaluateLocalRotation(PmxPose sample,
            VmdTargetRigData rig, int index, bool suppressLeft, bool suppressRight)
        {
            VmdTargetBoneBinding binding = rig.bones[index];
            Quaternion sourceWorld = ConvertRotation(
                sample.WorldRotations[binding.sourceBoneIndex], rig.coordinateConversion);
            Quaternion sourceParentWorld = Quaternion.identity;
            if (binding.parentBindingIndex >= 0)
            {
                VmdTargetBoneBinding parent = rig.bones[binding.parentBindingIndex];
                sourceParentWorld = ConvertRotation(
                    sample.WorldRotations[parent.sourceBoneIndex], rig.coordinateConversion);
            }
            bool suppress = binding.humanBone == 1
                ? suppressLeft
                : binding.humanBone == 2 && suppressRight;
            if (suppress && binding.parentBindingIndex >= 0)
            {
                Quaternion sourceLocal = ConvertRotation(
                    sample.LocalRotations[binding.sourceBoneIndex], rig.coordinateConversion);
                sourceWorld = Normalize(sourceParentWorld * sourceLocal);
            }
            Quaternion desiredWorld = Normalize(sourceWorld * binding.restWorldRotation);
            Quaternion desiredParent = binding.restParentWorldRotation;
            if (binding.parentBindingIndex >= 0)
                desiredParent = Normalize(sourceParentWorld * desiredParent);
            return Normalize(Quaternion.Inverse(desiredParent) * desiredWorld);
        }

        internal static Vector3 EvaluateRootPosition(PmxPose sample,
            VmdTargetRigData rig, VmdTargetBoneBinding bone)
        {
            Vector3 source = sample.WorldPositions[rig.rootMotionSourceBoneIndex] -
                             rig.rootMotionSourceRestPosition;
            Vector3 root = ConvertPosition(source, rig.coordinateConversion) *
                           (rig.positionScale * rig.rootMotionTargetScale);
            return bone.restLocalPosition + bone.parentAxisX * root.x +
                   bone.parentAxisY * root.y + bone.parentAxisZ * root.z;
        }

        private static Vector3 ConvertPosition(Vector3 value, int coordinate)
        {
            switch (coordinate)
            {
                case 0: return new Vector3(value.x, value.y, -value.z);
                case 1: return new Vector3(-value.x, value.y, value.z);
                case 2: return new Vector3(-value.x, value.y, -value.z);
                default: return value;
            }
        }

        internal static Quaternion ConvertRotation(Quaternion value, int coordinate)
        {
            switch (coordinate)
            {
                case 0: return Normalize(new Quaternion(-value.x, -value.y, value.z, value.w));
                case 1: return Normalize(new Quaternion(value.x, -value.y, -value.z, value.w));
                case 2: return Normalize(new Quaternion(-value.x, value.y, -value.z, value.w));
                default: return Normalize(value);
            }
        }

        internal static Quaternion Normalize(Quaternion value)
        {
            float length = (float)Math.Sqrt(Dot(value, value));
            return length > 1e-8f
                ? new Quaternion(value.x / length, value.y / length,
                    value.z / length, value.w / length)
                : Quaternion.identity;
        }

        internal static float Dot(Quaternion a, Quaternion b) =>
            a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        private static bool Differs(Quaternion[] values, Quaternion rest)
        {
            for (int i = 0; i < values.Length; i++)
                if (Math.Abs(Dot(values[i], rest)) < 1f - 1e-8f) return true;
            return false;
        }

        private static bool Differs(Vector3[] values, Vector3 rest)
        {
            for (int i = 0; i < values.Length; i++)
                if ((values[i] - rest).sqrMagnitude > 1e-14f) return true;
            return false;
        }

        internal static int[] ReduceQuaternion(float[] times, Quaternion[] values,
            float maximumDegrees)
        {
            if (values.Length <= 2 || maximumDegrees <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            double limit = 1.0 - Math.Cos(maximumDegrees * Math.PI / 360.0);
            return Reduce(times, values.Length, (start, end, i) =>
            {
                float factor = Factor(times, start, end, i);
                Quaternion a = values[start], b = values[end];
                Quaternion expected = Normalize(new Quaternion(
                    a.x + (b.x - a.x) * factor,
                    a.y + (b.y - a.y) * factor,
                    a.z + (b.z - a.z) * factor,
                    a.w + (b.w - a.w) * factor));
                return 1.0 - Math.Min(1.0, Math.Abs(Dot(expected, values[i])));
            }, limit);
        }

        internal static int[] ReduceVector(float[] times, Vector3[] values,
            float maximumError)
        {
            if (values.Length <= 2 || maximumError <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            return Reduce(times, values.Length, (start, end, i) =>
            {
                float factor = Factor(times, start, end, i);
                Vector3 expected = values[start] +
                    (values[end] - values[start]) * factor;
                return (expected - values[i]).sqrMagnitude;
            }, (double)maximumError * maximumError);
        }

        internal static int[] Reduce(float[] times, int count,
            Func<int, int, int, double> error, double limit)
        {
            var keep = new bool[count];
            keep[0] = keep[count - 1] = true;
            var ranges = new Stack<int>();
            ranges.Push(0); ranges.Push(count - 1);
            while (ranges.Count > 0)
            {
                int end = ranges.Pop(), start = ranges.Pop();
                int split = -1;
                double largest = -1;
                for (int i = start + 1; i < end; i++)
                {
                    double e = error(start, end, i);
                    if (e <= largest) continue;
                    largest = e;
                    split = i;
                }
                if (split < 0 || largest <= limit) continue;
                keep[split] = true;
                ranges.Push(start); ranges.Push(split);
                ranges.Push(split); ranges.Push(end);
            }
            var indices = new List<int>();
            for (int i = 0; i < count; i++) if (keep[i]) indices.Add(i);
            return indices.ToArray();
        }

        private static float Factor(float[] times, int start, int end, int i)
        {
            float duration = times[end] - times[start];
            return Math.Abs(duration) > 1e-12f
                ? (times[i] - times[start]) / duration : 0f;
        }

        internal static VmdFloatCurveData BuildCurve(string path, string property,
            byte kind, float[] times, int[] indices, Func<int, float> value)
        {
            var curve = new VmdFloatCurveData
            {
                Path = path,
                Property = property,
                BindingKind = kind,
                Times = new float[indices.Length],
                Values = new float[indices.Length]
            };
            for (int i = 0; i < indices.Length; i++)
            {
                curve.Times[i] = times[indices[i]];
                curve.Values[i] = value(indices[i]);
            }
            return curve;
        }

        private static float Component(Quaternion value, int component) =>
            component == 0 ? value.x : component == 1 ? value.y :
                component == 2 ? value.z : value.w;

        private static float Component(Vector3 value, int component) =>
            component == 0 ? value.x : component == 1 ? value.y : value.z;

        internal static void AddMorphCurves(VmdMotion motion, VmdTargetRigData rig,
            VmdTargetCurveData result)
        {
            var mapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (VmdTargetMorphBinding binding in rig.morphs ??
                     Array.Empty<VmdTargetMorphBinding>())
            {
                if (!motion.MorphTracks.TryGetValue(binding.name, out VmdMorphTrack track))
                    continue;
                AddMorph(binding.path, binding.name, track, result);
                mapped.Add(binding.name);
            }
            foreach (KeyValuePair<string, VmdMorphTrack> pair in motion.MorphTracks)
            {
                if (string.IsNullOrEmpty(pair.Key) || mapped.Contains(pair.Key)) continue;
                AddMorph("Body", pair.Key, pair.Value, result);
            }
        }

        private static void AddMorph(string path, string name, VmdMorphTrack track,
            VmdTargetCurveData result)
        {
            var curve = new VmdFloatCurveData
            {
                Path = path,
                Property = "blendShape." + name,
                BindingKind = 1,
                Times = new float[track.Keys.Count],
                Values = new float[track.Keys.Count]
            };
            for (int i = 0; i < track.Keys.Count; i++)
            {
                curve.Times[i] = track.Keys[i].Frame / 30f;
                curve.Values[i] = track.Keys[i].Weight * 100f;
            }
            result.Curves.Add(curve);
            result.BoundMorphCount++;
        }

        internal static bool ShouldSuppressWaistCancellation(PmxModel model,
            VmdMotion motion, bool left)
        {
            if (MotionModelMatchesPmx(model, motion)) return false;
            string[] upper = left
                ? new[] { "左足", "left leg", "LeftLeg", "LeftUpperLeg" }
                : new[] { "右足", "right leg", "RightLeg", "RightUpperLeg" };
            string[] lower = left
                ? new[] { "左ひざ", "左膝", "left knee", "LeftKnee", "LeftLowerLeg" }
                : new[] { "右ひざ", "右膝", "right knee", "RightKnee", "RightLowerLeg" };
            string[] foot = left
                ? new[] { "左足首", "left ankle", "LeftAnkle", "LeftFoot" }
                : new[] { "右足首", "right ankle", "RightAnkle", "RightFoot" };
            string[] ik = left
                ? new[] { "左足ＩＫ", "左足IK", "left leg IK", "LeftLegIK" }
                : new[] { "右足ＩＫ", "右足IK", "right leg IK", "RightLegIK" };
            if (!HasAnimatedTrack(motion, upper) || !HasAnimatedTrack(motion, lower) ||
                !HasAnimatedTrack(motion, foot)) return false;
            var names = new HashSet<string>(ik, StringComparer.OrdinalIgnoreCase);
            bool found = false;
            foreach (VmdIkKeyframe key in motion.IkKeyframes)
                foreach (VmdIkState state in key.States)
                {
                    if (!names.Contains(state.Name)) continue;
                    found = true;
                    if (state.Enabled) return false;
                }
            return found;
        }

        private static bool HasAnimatedTrack(VmdMotion motion, IEnumerable<string> names)
        {
            foreach (string name in names)
                if (motion.BoneTracks.TryGetValue(name, out VmdBoneTrack track) &&
                    track.Keys.Count > 1) return true;
            return false;
        }

        private static bool MotionModelMatchesPmx(PmxModel model, VmdMotion motion)
        {
            string motionName = NormalizeModelName(motion.ModelName);
            if (string.IsNullOrEmpty(motionName)) return true;
            foreach (string sourceName in new[] { model.NameJapanese, model.NameEnglish })
            {
                string modelName = NormalizeModelName(sourceName);
                if (string.IsNullOrEmpty(modelName)) continue;
                if (string.Equals(motionName, modelName, StringComparison.OrdinalIgnoreCase) ||
                    motionName.Length >= 3 && modelName.IndexOf(
                        motionName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    modelName.Length >= 3 && motionName.IndexOf(
                        modelName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string NormalizeModelName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return new string(value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());
        }
    }
}
