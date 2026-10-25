using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core
{
    /// <summary>Intermediate IK pose samples produced by the optional external worker.</summary>
    public sealed class PmxPreparedPoseData
    {
        public float SampleRate;
        public float Duration;
        public uint MaximumFrame;
        /// <summary>Only world positions, world rotations and local rotations are populated.</summary>
        public PmxPose[] Samples;
    }

    /// <summary>Versioned, compressed exchange format shared by Unity and the worker EXE.</summary>
    public static class PmxPreparedPoseFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VCCPOSE1");
        private const int Version = 1;
        private const int MaximumElements = 5000000;

        public static byte[] HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return sha.ComputeHash(stream);
        }

        public static void Write(Stream stream, byte[] modelHash, byte[] motionHash,
            float sampleRate, float duration, uint maximumFrame, PmxPose[] samples)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (modelHash == null || modelHash.Length != 32 ||
                motionHash == null || motionHash.Length != 32)
                throw new ArgumentException("SHA-256 hashes must be 32 bytes.");
            if (samples == null || samples.Length == 0 || samples[0] == null)
                throw new ArgumentException("At least one pose sample is required.", nameof(samples));
            int boneCount = samples[0].BoneCount;
            ValidateCounts(samples.Length, boneCount);
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(modelHash);
                writer.Write(motionHash);
                writer.Write(sampleRate);
                writer.Write(duration);
                writer.Write(maximumFrame);
                writer.Write(samples.Length);
                writer.Write(boneCount);
                writer.Flush();
            }
            using (var compressed = new DeflateStream(stream,
                System.IO.Compression.CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(compressed, Encoding.UTF8, true))
            {
                foreach (PmxPose sample in samples)
                {
                    if (sample == null || sample.BoneCount != boneCount)
                        throw new InvalidDataException("Pose sample bone counts differ.");
                    for (int bone = 0; bone < boneCount; bone++)
                    {
                        WriteVector(writer, sample.WorldPositions[bone]);
                        WriteQuaternion(writer, sample.WorldRotations[bone]);
                        WriteQuaternion(writer, sample.LocalRotations[bone]);
                    }
                }
            }
        }

        public static PmxPreparedPoseData Read(Stream stream, byte[] expectedModelHash,
            byte[] expectedMotionHash)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            int sampleCount;
            int boneCount;
            var result = new PmxPreparedPoseData();
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (!Equal(Magic, reader.ReadBytes(Magic.Length)) || reader.ReadInt32() != Version)
                    throw new InvalidDataException("Unsupported VMD pose cache format.");
                byte[] modelHash = reader.ReadBytes(32);
                byte[] motionHash = reader.ReadBytes(32);
                if (!Equal(modelHash, expectedModelHash) ||
                    !Equal(motionHash, expectedMotionHash))
                    throw new InvalidDataException("The pose cache does not match its PMX/VMD inputs.");
                result.SampleRate = reader.ReadSingle();
                result.Duration = reader.ReadSingle();
                result.MaximumFrame = reader.ReadUInt32();
                sampleCount = reader.ReadInt32();
                boneCount = reader.ReadInt32();
            }
            ValidateCounts(sampleCount, boneCount);
            if (result.SampleRate <= 0f || result.SampleRate > 240f ||
                float.IsNaN(result.Duration) || result.Duration < 0f)
                throw new InvalidDataException("Invalid pose cache timing.");
            result.Samples = new PmxPose[sampleCount];
            using (var compressed = new DeflateStream(stream, CompressionMode.Decompress, true))
            using (var reader = new BinaryReader(compressed, Encoding.UTF8, true))
            {
                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    var pose = new PmxPose(boneCount, true);
                    for (int bone = 0; bone < boneCount; bone++)
                    {
                        pose.WorldPositions[bone] = ReadVector(reader);
                        pose.WorldRotations[bone] = ReadQuaternion(reader);
                        pose.LocalRotations[bone] = ReadQuaternion(reader);
                    }
                    result.Samples[sampleIndex] = pose;
                }
            }
            return result;
        }

        private static void ValidateCounts(int samples, int bones)
        {
            if (samples <= 0 || bones <= 0 || (long)samples * bones > MaximumElements)
                throw new InvalidDataException("Pose cache exceeds its supported size.");
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int difference = 0;
            for (int index = 0; index < a.Length; index++) difference |= a[index] ^ b[index];
            return difference == 0;
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
        }

        private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w);
        }

        private static Vector3 ReadVector(BinaryReader reader) =>
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static Quaternion ReadQuaternion(BinaryReader reader) =>
            new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
