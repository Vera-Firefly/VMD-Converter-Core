using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;

namespace VeraFirefly.VmdConverter.Core.Nlu;

/// <summary>Standalone RootT/RootQ projection over PMX poses already solved by the worker.</summary>
public static class VmdRootMotionSolver
{
    public static IReadOnlyList<VmdFloatCurveData> Solve(PmxModel model,
        VmdMotion motion, PmxPose[] samples, VmdTargetRigData rig, bool canonical)
    {
        if (model == null || motion == null || samples == null || rig == null ||
            rig.humanoid == null)
            throw new ArgumentNullException("Root motion inputs are required.");
        if (samples.Length == 0) throw new ArgumentException("No pose samples.");
        VmdTargetFkCurveSolver.ResolveSourceBindings(model, rig);
        VmdTargetBoneBinding hips = rig.bones.First(b => b.humanBone == 0);
        if (canonical)
        {
            rig.rootMotionSourceBoneIndex = hips.sourceBoneIndex;
            rig.rootMotionSourceRestPosition = model.Bones[hips.sourceBoneIndex].Position;
            rig.rootMotionTargetScale = 1f;
        }
        var calibration = rig.humanoid;
        var values = new float[7][];
        for (int i = 0; i < values.Length; i++) values[i] = new float[samples.Length];
        var positions = new Vector3[rig.bones.Length];
        var resolved = new bool[rig.bones.Length];
        Quaternion previous = Quaternion.identity;
        for (int frame = 0; frame < samples.Length; frame++)
        {
            PmxPose sample = samples[frame];
            Array.Clear(resolved);
            Vector3 displacement = rig.bakeRootTranslation
                ? VmdTargetFkCurveSolver.EvaluateRootPosition(sample, rig, hips) -
                  hips.restLocalPosition : Vector3.zero;
            Vector3 bodyPosition = calibration.restBodyPosition +
                calibration.bodyPositionAxisX * displacement.x +
                calibration.bodyPositionAxisY * displacement.y +
                calibration.bodyPositionAxisZ * displacement.z +
                CenterOfMassOffset(model, sample, rig, calibration, canonical,
                    displacement, positions, resolved);
            Quaternion sourceWorld = VmdTargetFkCurveSolver.ConvertRotation(
                sample.WorldRotations[hips.sourceBoneIndex],
                rig.coordinateConversion);
            Quaternion bodyRotation = VmdTargetFkCurveSolver.Normalize(
                sourceWorld * calibration.restBodyRotation);
            if (frame > 0 && VmdTargetFkCurveSolver.Dot(previous, bodyRotation) < 0f)
                bodyRotation = new Quaternion(-bodyRotation.x, -bodyRotation.y,
                    -bodyRotation.z, -bodyRotation.w);
            previous = bodyRotation;
            values[0][frame] = bodyPosition.x;
            values[1][frame] = bodyPosition.y;
            values[2][frame] = bodyPosition.z;
            values[3][frame] = bodyRotation.x;
            values[4][frame] = bodyRotation.y;
            values[5][frame] = bodyRotation.z;
            values[6][frame] = bodyRotation.w;
        }
        var times = new float[samples.Length];
        for (int i = 0; i < times.Length; i++)
            times[i] = i == times.Length - 1
                ? motion.MaximumFrame / 30f : i / rig.sampleRate;
        string[] names = { "RootT.x", "RootT.y", "RootT.z",
            "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
        var curves = new List<VmdFloatCurveData>(7);
        // Root channels are small relative to the muscle and morph payload.
        // Preserve every sample so curve reduction cannot introduce foot drift.
        int[] allKeys = Enumerable.Range(0, samples.Length).ToArray();
        for (int channel = 0; channel < 7; channel++)
        {
            int c = channel;
            curves.Add(VmdTargetFkCurveSolver.BuildCurve(string.Empty,
                names[channel], 2, times, allKeys, i => values[c][i]));
        }
        return curves;
    }

    private static Vector3 CenterOfMassOffset(PmxModel model, PmxPose sample,
        VmdTargetRigData rig, VmdHumanoidCalibrationData calibration,
        bool canonical, Vector3 rootDisplacement, Vector3[] positions, bool[] resolved)
    {
        if (calibration.bodyInfluences == null) return Vector3.zero;
        double x = 0, y = 0, z = 0;
        foreach (VmdBodyPositionInfluence influence in calibration.bodyInfluences)
        {
            if (influence == null || influence.bindingIndex < 0 ||
                influence.bindingIndex >= rig.bones.Length) continue;
            int index = influence.bindingIndex;
            VmdTargetBoneBinding bone = rig.bones[index];
            if (bone.sourceBoneIndex < 0 || canonical && bone.syntheticThumbProximal)
                continue;
            Vector3 delta;
            if (canonical)
            {
                Vector3 moved = sample.WorldPositions[bone.sourceBoneIndex] -
                                model.Bones[bone.sourceBoneIndex].Position;
                delta = new Vector3(-moved.x, moved.y, -moved.z) *
                        rig.positionScale - rootDisplacement;
            }
            else
                delta = CurrentPosition(sample, rig, index, positions, resolved) -
                        bone.restWorldPosition;
            x += (double)influence.axisX.x * delta.x +
                 (double)influence.axisY.x * delta.y +
                 (double)influence.axisZ.x * delta.z;
            y += (double)influence.axisX.y * delta.x +
                 (double)influence.axisY.y * delta.y +
                 (double)influence.axisZ.y * delta.z;
            z += (double)influence.axisX.z * delta.x +
                 (double)influence.axisY.z * delta.y +
                 (double)influence.axisZ.z * delta.z;
        }
        return new Vector3((float)x, (float)y, (float)z);
    }

    private static Vector3 CurrentPosition(PmxPose sample, VmdTargetRigData rig,
        int index, Vector3[] positions, bool[] resolved)
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

}
