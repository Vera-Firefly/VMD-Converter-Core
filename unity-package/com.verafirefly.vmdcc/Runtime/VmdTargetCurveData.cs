using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core
{
    public enum VmdExternalOutputKind
    {
        HumanoidIk = 0,
        TargetCalibratedHumanoid = 1,
        TargetFk = 2
    }

    // Unity exports only the target Avatar's static binding data. The executable owns
    // every per-frame PMX/IK evaluation, target-FK projection and curve reduction.
    [Serializable]
    public sealed class VmdTargetRigData
    {
        public float sampleRate = 30f;
        public float positionScale = 1f;
        public int coordinateConversion = 2;
        public float rotationErrorDegrees = 0.01f;
        public float positionError = 0.00005f;
        public float muscleError = 0.0001f;
        public bool bakeRootTranslation = true;
        public bool bakeMorphs = true;
        public int rootMotionSourceBoneIndex = -1;
        public float rootMotionTargetScale = 1f;
        public float targetHipsHeight;
        public Vector3 rootMotionSourceRestPosition;
        public bool suppressLeftWaistCancellation;
        public bool suppressRightWaistCancellation;
        public VmdExternalOutputKind outputKind = VmdExternalOutputKind.TargetFk;
        public VmdHumanoidCalibrationData humanoid;
        // The canonical PMX Humanoid Avatar is used for standard output and
        // for the upper limbs of target-calibrated Humanoid output.
        public VmdTargetRigData canonicalRig;
        public VmdTargetBoneBinding[] bones = Array.Empty<VmdTargetBoneBinding>();
        public VmdTargetMorphBinding[] morphs = Array.Empty<VmdTargetMorphBinding>();
    }

    [Serializable]
    public sealed class VmdHumanoidCalibrationData
    {
        public Vector3 restBodyPosition;
        public Quaternion restBodyRotation;
        public Quaternion zeroHipsWorldRotation;
        public Vector3 bodyPositionAxisX;
        public Vector3 bodyPositionAxisY;
        public Vector3 bodyPositionAxisZ;
        public VmdBodyPositionInfluence[] bodyInfluences =
            Array.Empty<VmdBodyPositionInfluence>();
        public VmdMuscleCalibration[] muscles = Array.Empty<VmdMuscleCalibration>();
    }

    [Serializable]
    public sealed class VmdBodyPositionInfluence
    {
        public int bindingIndex = -1;
        public Vector3 axisX;
        public Vector3 axisY;
        public Vector3 axisZ;
    }

    [Serializable]
    public sealed class VmdMuscleCalibration
    {
        public int bindingIndex = -1;
        public string property;
        public Quaternion zeroLocalRotation;
        public Quaternion positiveDelta;
        public Quaternion negativeDelta;
    }

    [Serializable]
    public sealed class VmdTargetBoneBinding
    {
        public string path;
        public int sourceBoneIndex;
        public string[] sourceCandidates = Array.Empty<string>();
        public int parentBindingIndex = -1;
        public int humanBone;
        public bool syntheticThumbProximal;
        public Quaternion restLocalRotation;
        public Quaternion restWorldRotation;
        public Vector3 restWorldPosition;
        public Quaternion restParentWorldRotation;
        public Vector3 restLocalPosition;
        // The three vector columns of target.parent.worldToLocal * targetRoot.localToWorld.
        public Vector3 parentAxisX;
        public Vector3 parentAxisY;
        public Vector3 parentAxisZ;
    }

    [Serializable]
    public sealed class VmdTargetMorphBinding
    {
        public string name;
        public string path;
    }

    public sealed class VmdFloatCurveData
    {
        public string Path;
        public string Property;
        public byte BindingKind; // 0 = Transform, 1 = SkinnedMeshRenderer, 2 = Animator
        public float[] Times;
        public float[] Values;
    }

    public sealed class VmdTargetCurveData
    {
        public float SampleRate;
        public int SampleCount;
        public int BoundBoneCount;
        public int BoundMorphCount;
        public readonly List<VmdFloatCurveData> Curves = new List<VmdFloatCurveData>();
    }

    public static class VmdTargetCurveFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VCCCURV1");
        private const int MaximumCurves = 20000;
        private const int MaximumKeys = 10000000;

        public static byte[] HashMotionSources(string vmdPath, string expressionVmdPath)
        {
            byte[] motion = HashFile(vmdPath);
            if (string.IsNullOrEmpty(expressionVmdPath) || string.Equals(
                    Path.GetFullPath(vmdPath), Path.GetFullPath(expressionVmdPath),
                    StringComparison.OrdinalIgnoreCase))
                return motion;
            byte[] expression = HashFile(expressionVmdPath);
            var combined = new byte[64];
            Buffer.BlockCopy(motion, 0, combined, 0, 32);
            Buffer.BlockCopy(expression, 0, combined, 32, 32);
            using (var sha = SHA256.Create()) return sha.ComputeHash(combined);
        }

        public static byte[] HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return sha.ComputeHash(stream);
        }

        public static void Write(Stream stream, byte[] pmxHash, byte[] vmdHash,
            byte[] rigHash, VmdTargetCurveData data)
        {
            if (stream == null || data == null) throw new ArgumentNullException();
            CheckHash(pmxHash); CheckHash(vmdHash); CheckHash(rigHash);
            if (data.Curves.Count > MaximumCurves) throw new InvalidDataException("Too many curves.");
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(1);
                writer.Write(pmxHash);
                writer.Write(vmdHash);
                writer.Write(rigHash);
                writer.Write(data.SampleRate);
                writer.Write(data.SampleCount);
                writer.Write(data.BoundBoneCount);
                writer.Write(data.BoundMorphCount);
                writer.Write(data.Curves.Count);
            }
            using (var zipped = new DeflateStream(stream,
                System.IO.Compression.CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(zipped, Encoding.UTF8, true))
            {
                foreach (VmdFloatCurveData curve in data.Curves)
                {
                    if (curve.Times == null || curve.Values == null ||
                        curve.Times.Length != curve.Values.Length ||
                        curve.Times.Length > MaximumKeys)
                        throw new InvalidDataException("Invalid curve key count.");
                    writer.Write(curve.BindingKind);
                    writer.Write(curve.Path ?? string.Empty);
                    writer.Write(curve.Property ?? string.Empty);
                    writer.Write(curve.Times.Length);
                    for (int i = 0; i < curve.Times.Length; i++)
                    {
                        writer.Write(curve.Times[i]);
                        writer.Write(curve.Values[i]);
                    }
                }
            }
        }

        public static VmdTargetCurveData Read(Stream stream, byte[] pmxHash,
            byte[] vmdHash, byte[] rigHash)
        {
            CheckHash(pmxHash); CheckHash(vmdHash); CheckHash(rigHash);
            int count;
            var data = new VmdTargetCurveData();
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (!Same(magic, Magic) || reader.ReadInt32() != 1 ||
                    !Same(reader.ReadBytes(32), pmxHash) ||
                    !Same(reader.ReadBytes(32), vmdHash) ||
                    !Same(reader.ReadBytes(32), rigHash))
                    throw new InvalidDataException("Curve cache does not match the inputs.");
                data.SampleRate = reader.ReadSingle();
                data.SampleCount = reader.ReadInt32();
                data.BoundBoneCount = reader.ReadInt32();
                data.BoundMorphCount = reader.ReadInt32();
                count = reader.ReadInt32();
            }
            if (count < 0 || count > MaximumCurves || data.SampleCount < 1 ||
                data.SampleRate <= 0f)
                throw new InvalidDataException("Invalid curve cache header.");
            using (var zipped = new DeflateStream(stream, CompressionMode.Decompress, true))
            using (var reader = new BinaryReader(zipped, Encoding.UTF8, true))
            {
                int totalKeys = 0;
                for (int c = 0; c < count; c++)
                {
                    var curve = new VmdFloatCurveData();
                    curve.BindingKind = reader.ReadByte();
                    curve.Path = reader.ReadString();
                    curve.Property = reader.ReadString();
                    int keys = reader.ReadInt32();
                    totalKeys = checked(totalKeys + keys);
                    if (curve.BindingKind > 2 || keys < 0 || totalKeys > MaximumKeys ||
                        curve.Path.Length > 1024 || curve.Property.Length > 256)
                        throw new InvalidDataException("Invalid curve record.");
                    curve.Times = new float[keys];
                    curve.Values = new float[keys];
                    for (int i = 0; i < keys; i++)
                    {
                        curve.Times[i] = reader.ReadSingle();
                        curve.Values[i] = reader.ReadSingle();
                    }
                    data.Curves.Add(curve);
                }
            }
            return data;
        }

        private static void CheckHash(byte[] hash)
        {
            if (hash == null || hash.Length != 32)
                throw new ArgumentException("Expected a SHA-256 hash.");
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
