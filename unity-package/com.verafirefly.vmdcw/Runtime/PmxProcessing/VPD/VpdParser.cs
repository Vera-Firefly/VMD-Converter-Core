using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core
{
    /// <summary>
    /// Reads an MMD VPD pose and exposes it as a two-key VMD motion. Keeping the
    /// two identical keys one MMD frame apart gives the generated AnimationClip a
    /// usable duration without introducing interpolation or resampling error.
    /// </summary>
    public static class VpdParser
    {
        private const uint PoseEndFrame = 1;

        public static VmdMotion Parse(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            string text = Decode(File.ReadAllBytes(path));
            List<string> lines = ReadLines(text);
            int lineIndex = 0;

            string header = ReadRequiredLine(lines, ref lineIndex, "header");
            if (!header.StartsWith("Vocaloid Pose Data file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The file is not a VPD pose.");

            string modelName = TrimTerminator(ReadRequiredLine(lines, ref lineIndex, "model name"));
            string countText = TrimTerminator(ReadRequiredLine(lines, ref lineIndex, "bone count"));
            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int declaredBoneCount) || declaredBoneCount < 0)
                throw new InvalidDataException("The VPD bone count is invalid.");

            var bones = new Dictionary<string, BonePose>(StringComparer.Ordinal);
            var boneOrder = new List<string>();
            var morphs = new Dictionary<string, float>(StringComparer.Ordinal);
            var morphOrder = new List<string>();
            int parsedBoneCount = 0;

            while (lineIndex < lines.Count)
            {
                string blockHeader = lines[lineIndex++];
                int brace = blockHeader.IndexOf('{');
                if (brace < 0) continue;

                string blockType = blockHeader.Substring(0, brace).Trim();
                string name = blockHeader.Substring(brace + 1).Trim();
                var statements = new List<string>(2);
                bool closed = ExtractClosingBrace(ref name, statements);
                while (!closed && lineIndex < lines.Count)
                {
                    string line = lines[lineIndex++];
                    int close = line.IndexOf('}');
                    string content = close >= 0 ? line.Substring(0, close) : line;
                    AddStatements(content, statements);
                    closed = close >= 0;
                }
                if (!closed)
                    throw new InvalidDataException($"The VPD block '{blockType}' is incomplete.");

                name = name.Trim();
                if (string.IsNullOrEmpty(name))
                    throw new InvalidDataException($"The VPD block '{blockType}' has no name.");

                if (blockType.StartsWith("Bone", StringComparison.OrdinalIgnoreCase))
                {
                    if (statements.Count < 2)
                        throw new InvalidDataException($"The VPD bone '{name}' is incomplete.");
                    float[] position = ParseComponents(statements[0], 3, "bone translation", name);
                    float[] rotation = ParseComponents(statements[1], 4, "bone rotation", name);
                    var quaternion = NormalizeSafe(new Quaternion(
                        rotation[0], rotation[1], rotation[2], rotation[3]));
                    if (!bones.ContainsKey(name)) boneOrder.Add(name);
                    bones[name] = new BonePose
                    {
                        Translation = new Vector3(position[0], position[1], position[2]),
                        Rotation = quaternion
                    };
                    parsedBoneCount++;
                }
                else if (blockType.StartsWith("Morph", StringComparison.OrdinalIgnoreCase))
                {
                    if (statements.Count < 1)
                        throw new InvalidDataException($"The VPD morph '{name}' is incomplete.");
                    float weight = ParseComponents(statements[0], 1, "morph weight", name)[0];
                    if (!morphs.ContainsKey(name)) morphOrder.Add(name);
                    morphs[name] = weight;
                }
            }

            if (parsedBoneCount < declaredBoneCount)
                throw new InvalidDataException(
                    $"The VPD declares {declaredBoneCount} bones but only {parsedBoneCount} were found.");

            var motion = new VmdMotion
            {
                Header = "Vocaloid Pose Data file",
                ModelName = string.IsNullOrEmpty(modelName)
                    ? Path.GetFileNameWithoutExtension(path)
                    : modelName
            };
            foreach (string name in boneOrder)
            {
                BonePose pose = bones[name];
                AddBoneKey(motion, name, 0, pose);
                AddBoneKey(motion, name, PoseEndFrame, pose);
            }
            foreach (string name in morphOrder)
            {
                float weight = morphs[name];
                motion.MorphKeyframes.Add(new VmdMorphKeyframe
                    { MorphName = name, Frame = 0, Weight = weight });
                motion.MorphKeyframes.Add(new VmdMorphKeyframe
                    { MorphName = name, Frame = PoseEndFrame, Weight = weight });
            }
            // Also preserves the one-frame clip duration for a valid but empty pose.
            motion.IkKeyframes.Add(new VmdIkKeyframe
                { Frame = PoseEndFrame, ModelVisible = true });
            motion.FinalizeData();
            return motion;
        }

        private static void AddBoneKey(VmdMotion motion, string name, uint frame, BonePose pose)
        {
            motion.BoneKeyframes.Add(new VmdBoneKeyframe
            {
                BoneName = name,
                Frame = frame,
                Translation = pose.Translation,
                Rotation = pose.Rotation,
                Interpolation = new VmdBoneInterpolation()
            });
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new InvalidDataException("The VPD file is empty.");
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(932).GetString(bytes);
            }
        }

        private static List<string> ReadLines(string text)
        {
            var result = new List<string>();
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) line = line.Substring(0, comment);
                    line = line.Trim();
                    if (line.Length > 0) result.Add(line);
                }
            }
            return result;
        }

        private static string ReadRequiredLine(
            IReadOnlyList<string> lines, ref int index, string section)
        {
            if (index >= lines.Count)
                throw new InvalidDataException($"The VPD {section} is missing.");
            return lines[index++];
        }

        private static string TrimTerminator(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd(';').Trim().Trim('"');
        }

        private static bool ExtractClosingBrace(ref string name, List<string> statements)
        {
            int close = name.IndexOf('}');
            if (close < 0) return false;
            string beforeClose = name.Substring(0, close).Trim();
            int separator = beforeClose.IndexOf(';');
            if (separator >= 0)
            {
                name = beforeClose.Substring(0, separator).Trim();
                AddStatements(beforeClose.Substring(separator + 1), statements);
            }
            else
            {
                name = beforeClose;
            }
            return true;
        }

        private static void AddStatements(string content, List<string> statements)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            string[] values = content.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < values.Length; index++)
            {
                string value = values[index].Trim();
                if (value.Length > 0) statements.Add(value);
            }
        }

        private static float[] ParseComponents(
            string statement, int expectedCount, string section, string name)
        {
            string[] tokens = statement.Split(',');
            if (tokens.Length != expectedCount)
                throw new InvalidDataException(
                    $"The VPD {section} for '{name}' must contain {expectedCount} values.");
            var result = new float[expectedCount];
            for (int index = 0; index < tokens.Length; index++)
            {
                if (!float.TryParse(tokens[index].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value) ||
                    float.IsNaN(value) || float.IsInfinity(value))
                    throw new InvalidDataException(
                        $"The VPD {section} for '{name}' contains an invalid number.");
                result[index] = value;
            }
            return result;
        }

        private static Quaternion NormalizeSafe(Quaternion value)
        {
            float lengthSquared = value.x * value.x + value.y * value.y +
                                  value.z * value.z + value.w * value.w;
            if (lengthSquared < 1e-16f) return Quaternion.identity;
            float inverse = 1f / Mathf.Sqrt(lengthSquared);
            return new Quaternion(
                value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
        }

        private struct BonePose
        {
            internal Vector3 Translation;
            internal Quaternion Rotation;
        }
    }
}
