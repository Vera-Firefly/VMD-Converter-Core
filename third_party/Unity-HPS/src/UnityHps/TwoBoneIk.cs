using System.Numerics;

namespace UnityHps;

public static class TwoBoneIk
{
    /// <summary>Analytic limb IK with a world-space pole, fixed segment lengths, no stretch.</summary>
    public static float Solve(SkeletonPose pose, IkGoal goal)
    {
        int a = goal.root, b = goal.middle, c = goal.tip;
        if (a < 0 || b < 0 || c < 0 || a >= pose.Rig.bones.Length || b >= pose.Rig.bones.Length || c >= pose.Rig.bones.Length ||
            pose.Rig.bones[b].parent != a || pose.Rig.bones[c].parent != b)
            throw new InvalidDataException("IK requires a direct root -> middle -> tip chain.");
        if (!float.IsFinite(goal.weight) || goal.weight < 0 || goal.weight > 1)
            throw new InvalidDataException("IK weight must be in [0,1].");
        Vector3 target = Math3.V(goal.target), pole = Math3.V(goal.pole);
        if (!IsFinite(target) || !IsFinite(pole)) throw new InvalidDataException("Non-finite IK target/pole.");
        if (goal.weight == 0) return Vector3.Distance(pose.WorldPositions[c], target);
        Quaternion originalA = pose.LocalRotations[a], originalB = pose.LocalRotations[b];
        Quaternion originalTipWorld = pose.WorldRotations[c];
        Vector3 start = pose.WorldPositions[a], mid = pose.WorldPositions[b], end = pose.WorldPositions[c];
        float l1 = Vector3.Distance(start, mid), l2 = Vector3.Distance(mid, end);
        if (l1 < 1e-7f || l2 < 1e-7f) throw new InvalidDataException("Zero-length IK limb.");
        Vector3 reach = target - start;
        float distance = reach.Length();
        Vector3 direction = distance > 1e-7f ? reach / distance : Vector3.Normalize(end - start == Vector3.Zero ? mid - start : end - start);
        float d = Math.Clamp(distance, MathF.Abs(l1 - l2) + 1e-6f, l1 + l2 - 1e-6f);
        Vector3 bend = pole - start;
        bend -= direction * Vector3.Dot(bend, direction);
        if (bend.LengthSquared() < 1e-12f)
        {
            bend = mid - start; bend -= direction * Vector3.Dot(bend, direction);
            if (bend.LengthSquared() < 1e-12f)
                bend = Vector3.Cross(direction, MathF.Abs(direction.X) < 0.8f ? Vector3.UnitX : Vector3.UnitY);
        }
        bend = Vector3.Normalize(bend);
        float along = (l1 * l1 + d * d - l2 * l2) / (2 * d);
        Vector3 desiredMid = start + direction * along + bend * MathF.Sqrt(MathF.Max(0, l1 * l1 - along * along));
        pose.SetWorldRotation(a, Math3.FromTo(mid - start, desiredMid - start) * pose.WorldRotations[a]);
        pose.SetWorldRotation(b, Math3.FromTo(pose.WorldPositions[c] - pose.WorldPositions[b], start + direction * d - pose.WorldPositions[b]) * pose.WorldRotations[b]);
        pose.LocalRotations[a] = Quaternion.Slerp(originalA, pose.LocalRotations[a], goal.weight);
        pose.LocalRotations[b] = Quaternion.Slerp(originalB, pose.LocalRotations[b], goal.weight);
        pose.UpdateWorld();
        Quaternion desiredTip = goal.rotation == null ? originalTipWorld : Math3.Q(goal.rotation);
        pose.SetWorldRotation(c, Quaternion.Slerp(originalTipWorld, desiredTip, goal.weight));
        return Vector3.Distance(pose.WorldPositions[c], target);
    }
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
