using System.Numerics;

namespace UnityHps;

public sealed class SkeletonPose
{
    public Rig Rig { get; }
    public Vector3[] LocalPositions { get; }
    public Quaternion[] LocalRotations { get; }
    public Vector3[] WorldPositions { get; }
    public Quaternion[] WorldRotations { get; }
    public SkeletonPose(Rig rig, PoseFrame? frame = null)
    {
        Rig = rig;
        int count = rig.bones.Length;
        if (frame != null && (frame.positions.Length != count * 3 || frame.rotations.Length != count * 4))
            throw new InvalidDataException("Frame must contain all rig bones' local positions and rotations.");
        LocalPositions = new Vector3[count]; LocalRotations = new Quaternion[count];
        WorldPositions = new Vector3[count]; WorldRotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            LocalPositions[i] = frame == null ? Math3.V(rig.bones[i].position) : Math3.V(frame.positions, i * 3);
            LocalRotations[i] = frame == null ? Math3.Q(rig.bones[i].rotation) : Math3.Q(frame.rotations, i * 4);
            Vector3 p = LocalPositions[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                throw new InvalidDataException("Non-finite position.");
        }
        UpdateWorld();
    }
    public void UpdateWorld()
    {
        for (int i = 0; i < Rig.bones.Length; i++)
        {
            int parent = Rig.bones[i].parent;
            if (parent < 0) { WorldPositions[i] = LocalPositions[i]; WorldRotations[i] = LocalRotations[i]; }
            else
            {
                WorldPositions[i] = WorldPositions[parent] + Math3.Rotate(WorldRotations[parent], LocalPositions[i]);
                WorldRotations[i] = Quaternion.Normalize(WorldRotations[parent] * LocalRotations[i]);
            }
        }
    }
    public void SetWorldRotation(int bone, Quaternion rotation)
    {
        int parent = Rig.bones[bone].parent;
        LocalRotations[bone] = parent < 0 ? rotation : Quaternion.Normalize(Quaternion.Inverse(WorldRotations[parent]) * rotation);
        UpdateWorld();
    }
    public PoseFrame ToFrame(float time = 0)
    {
        var frame = new PoseFrame { time = time, positions = new float[Rig.bones.Length * 3], rotations = new float[Rig.bones.Length * 4] };
        for (int i = 0; i < Rig.bones.Length; i++)
        { Math3.Put(frame.positions, i * 3, LocalPositions[i]); Math3.Put(frame.rotations, i * 4, LocalRotations[i]); }
        return frame;
    }
}
