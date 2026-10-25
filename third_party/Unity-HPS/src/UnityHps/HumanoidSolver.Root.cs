using System.Numerics;

namespace UnityHps;

public sealed partial class HumanoidSolver
{
    /// <summary>Reconstruct root transforms during Solve/SolveBatch; configure before sharing the solver.</summary>
    public bool ReconstructDecodedRoot { get; init; }

    /// <summary>
    /// Reconstruct RootT/RootQ so the decoded Hips matches an already solved
    /// target pose. Keeps the supplied muscles unchanged and returns owned arrays.
    /// Accuracy against Unity depends on the rig's forward calibration. Resolve
    /// IK goals first; this method neither fits muscles nor solves IK.
    /// </summary>
    public HumanPose ReconstructRoot(PoseFrame target, float[] muscles)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(muscles);
        if (!float.IsFinite(target.time) || target.time < 0)
            throw new InvalidDataException("Invalid frame time.");
        if (target.goals.Length != 0)
            throw new InvalidDataException("Resolve IK goals before root reconstruction.");
        var desired = new SkeletonPose(rig, target);
        var decoded = Decode(muscles, new HumanPose
        {
            bodyPosition = rig.bodyPosition,
            bodyRotation = rig.bodyRotation
        });
        return ReconstructRoot(desired, muscles, decoded);
    }

    private HumanPose ReconstructRoot(SkeletonPose desired, float[] muscles, SkeletonPose decoded)
    {
        Quaternion rotation = Quaternion.Normalize(desired.WorldRotations[rig.hips] *
            Quaternion.Inverse(decoded.WorldRotations[rig.hips]) * Math3.Q(rig.bodyRotation));
        Quaternion delta = Quaternion.Normalize(rotation * Quaternion.Inverse(Math3.Q(rig.bodyRotation)));
        Vector3 center = Math3.V(rig.bodyPosition) * rig.humanScale;
        Vector3 anchoredHips = center + Math3.Rotate(delta, decoded.WorldPositions[rig.hips] - center);
        Vector3 position = Math3.V(rig.bodyPosition) +
            (desired.WorldPositions[rig.hips] - anchoredHips) / rig.humanScale;
        return new HumanPose
        {
            bodyPosition = Math3.A(position),
            bodyRotation = Math3.A(rotation),
            muscles = (float[])muscles.Clone()
        };
    }
}
