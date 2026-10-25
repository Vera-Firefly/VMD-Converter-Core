using System.Numerics;
using UnityHps;

namespace UnityHps;

// Equivalent axial half-turns preserve elbow/wrist positions and wrist orientation.
// Restrict selection to frames with a large output step and modest source motion.
public static class FkBranchContinuation
{
    public static PoseFrame[] Process(Rig rig, Rig source, PoseFrame[] raw, PoseFrame[] input)
    {
        if (raw.Length != input.Length) throw new InvalidDataException("FK source and target frame counts differ");
        var frames = (PoseFrame[])input.Clone();
        for (int i = 0; i < frames.Length; i++)
            if (!float.IsFinite(frames[i].time) || !float.IsFinite(raw[i].time) || frames[i].time < 0 || Math.Abs(raw[i].time - frames[i].time) > 1e-5f ||
                i > 0 && (frames[i].time <= frames[i - 1].time || raw[i].time <= raw[i - 1].time))
                throw new InvalidDataException("FK frames require matching chronological times");
        var by = rig.bones.Select((b, i) => (b, i)).Where(x => x.b.humanBone >= 0).ToDictionary(x => x.b.humanBone, x => x.i);
        var sb = source.bones.Select((b, i) => (b, i)).Where(x => x.b.humanBone >= 0).ToDictionary(x => x.b.humanBone, x => x.i);
        SkeletonPose? previous = null;
        for (int i = 0; i < frames.Length; i++)
        {
            var pose = new SkeletonPose(rig, frames[i]);
            if (previous != null) foreach (int side in new[] { 0, 1 })
            {
                int u = by[13 + side], e = by[15 + side], w = by[17 + side]; int[] chain = [u, e, w];
                double Step(SkeletonPose p) => chain.Max(b => (double)Math3.Angle(p.LocalRotations[b], previous.LocalRotations[b]));
                if (Step(pose) < 100) continue;
                double sourceStep = new[] { 13 + side, 15 + side, 17 + side }.Max(h => (double)Math3.Angle(Math3.Q(raw[i - 1].rotations, sb[h] * 4), Math3.Q(raw[i].rotations, sb[h] * 4)));
                if (sourceStep > 70) continue;
                double Cost(SkeletonPose p) => chain.Sum(b => Math.Pow(Math3.Angle(p.LocalRotations[b], previous.LocalRotations[b]), 2));
                double best = Cost(pose); var original = new SkeletonPose(rig, pose.ToFrame(frames[i].time)); SkeletonPose? chosen = null;
                foreach (int variant in new[] { 1, 2, 3 })
                {
                    var trial = new SkeletonPose(rig, original.ToFrame(frames[i].time));
                    void Flip(int bone, int child)
                    {
                        Quaternion childRotation = trial.WorldRotations[child];
                        Vector3 direction = trial.WorldPositions[child] - trial.WorldPositions[bone];
                        if (direction.LengthSquared() < 1e-12f) return;
                        Vector3 axis = Vector3.Normalize(direction);
                        trial.SetWorldRotation(bone, Quaternion.Normalize(new Quaternion(axis, 0) * trial.WorldRotations[bone]));
                        trial.SetWorldRotation(child, childRotation);
                    }
                    if ((variant & 1) != 0) Flip(u, e);
                    if ((variant & 2) != 0) Flip(e, w);
                    trial.SetWorldRotation(w, original.WorldRotations[w]);
                    double cost = Cost(trial);
                    if (cost + 400 < best) { best = cost; chosen = trial; }
                }
                if (chosen != null)
                {
                    pose = chosen;
                }
            }
            frames[i] = pose.ToFrame(frames[i].time); previous = pose;
        }
        return frames;
    }
}
