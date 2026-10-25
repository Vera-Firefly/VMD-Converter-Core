using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core
{
    /// <summary>Coordinate and curve-reduction helpers for external backends.</summary>
    internal static class VmdCurveUtility
    {
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

        internal static int[] ReduceQuaternion(float[] times, Quaternion[] values,
            float maximumDegrees)
        {
            if (values.Length <= 2 || maximumDegrees <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            // A float dot product rounds sub-0.04-degree errors to one. Compare
            // normalized quaternion chords in double precision instead.
            double chord = 2.0 * Math.Sin(maximumDegrees * Math.PI / 720.0);
            double limit = chord * chord;
            return Reduce(times, values.Length, (start, end, i) =>
            {
                double factor = ((double)times[i] - times[start]) /
                    ((double)times[end] - times[start]);
                Quaternion a = values[start], b = values[end];
                double x = a.x + ((double)b.x - a.x) * factor;
                double y = a.y + ((double)b.y - a.y) * factor;
                double z = a.z + ((double)b.z - a.z) * factor;
                double w = a.w + ((double)b.w - a.w) * factor;
                Quaternion actual = values[i];
                double length = Math.Sqrt(x*x+y*y+z*z+w*w);
                double actualLength = Math.Sqrt((double)actual.x*actual.x+
                    (double)actual.y*actual.y+(double)actual.z*actual.z+(double)actual.w*actual.w);
                if (actualLength < 1e-12 || double.IsNaN(actualLength))
                    throw new System.IO.InvalidDataException("Invalid curve quaternion.");
                if (length < 1e-12) return double.PositiveInfinity;
                x /= length; y /= length; z /= length; w /= length;
                double sign = x*actual.x+y*actual.y+z*actual.z+w*actual.w < 0 ? -1.0 : 1.0;
                double dx = x-sign*actual.x/actualLength, dy = y-sign*actual.y/actualLength;
                double dz = z-sign*actual.z/actualLength, dw = w-sign*actual.w/actualLength;
                return dx*dx+dy*dy+dz*dz+dw*dw;
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
