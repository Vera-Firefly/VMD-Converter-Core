using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VeraFirefly.VmdConverter.Core.Editor
{
    internal sealed class PmxHumanoidPoseSample
    {
        internal readonly Vector3[] WorldPositions;
        internal readonly Quaternion[] WorldRotations;
        internal readonly Quaternion[] LocalRotations;

        internal PmxHumanoidPoseSample(PmxPose pose)
            : this(pose, true)
        {
        }

        internal PmxHumanoidPoseSample(PmxPose pose, bool copy)
        {
            int count = pose.BoneCount;
            if (!copy)
            {
                WorldPositions = pose.WorldPositions;
                WorldRotations = pose.WorldRotations;
                LocalRotations = pose.LocalRotations;
                return;
            }
            WorldPositions = new Vector3[count];
            WorldRotations = new Quaternion[count];
            LocalRotations = new Quaternion[count];
            Array.Copy(pose.WorldPositions, WorldPositions, count);
            Array.Copy(pose.WorldRotations, WorldRotations, count);
            Array.Copy(pose.LocalRotations, LocalRotations, count);
        }
    }
}
