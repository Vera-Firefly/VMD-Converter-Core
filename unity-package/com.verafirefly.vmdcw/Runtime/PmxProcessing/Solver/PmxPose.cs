using System;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core
{
    public sealed class PmxPose
    {
        public readonly Vector3[] LocalPositions;
        public readonly Quaternion[] LocalRotations;
        public readonly Vector3[] WorldPositions;
        public readonly Quaternion[] WorldRotations;
        internal readonly Vector3[] MotionTranslations;
        internal readonly Quaternion[] MotionRotations;

        public int BoneCount => WorldPositions.Length;

        internal PmxPose(int boneCount, bool sampleOnly = false)
        {
            LocalPositions = sampleOnly ? Array.Empty<Vector3>() : new Vector3[boneCount];
            LocalRotations = new Quaternion[boneCount];
            WorldPositions = new Vector3[boneCount];
            WorldRotations = new Quaternion[boneCount];
            MotionTranslations = sampleOnly ? Array.Empty<Vector3>() : new Vector3[boneCount];
            MotionRotations = sampleOnly ? Array.Empty<Quaternion>() : new Quaternion[boneCount];
        }
    }
}
