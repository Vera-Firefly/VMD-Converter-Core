using System.Numerics;

namespace UnityHps;

/// <summary>Synthetic fixture only; does not measure agreement with Unity.</summary>
public static class Demo
{
    public static Rig CreateRig()
    {
        var rig = new Rig
        {
            source = "Synthetic test rig, NOT a Unity calibration",
            hips = 0, humanScale = 1, bodyPosition = [0, 1, 0],
            bones =
            [
                B("Hips", -1, [0, 1, 0], 0), B("Spine", 0, [0, 0.2f, 0], 7), B("Head", 1, [0, 0.4f, 0], 10),
                B("LeftUpperLeg", 0, [-0.12f, 0, 0], 1), B("LeftLowerLeg", 3, [0, -0.45f, 0], 3), B("LeftFoot", 4, [0, -0.45f, 0], 5),
                B("RightUpperLeg", 0, [0.12f, 0, 0], 2), B("RightLowerLeg", 6, [0, -0.45f, 0], 4), B("RightFoot", 7, [0, -0.45f, 0], 6),
                B("LeftUpperArm", 1, [-0.2f, 0.25f, 0], 13), B("LeftLowerArm", 9, [-0.3f, 0, 0], 15), B("LeftHand", 10, [-0.25f, 0, 0], 17),
                B("RightUpperArm", 1, [0.2f, 0.25f, 0], 14), B("RightLowerArm", 12, [0.3f, 0, 0], 16), B("RightHand", 13, [0.25f, 0, 0], 18)
            ],
            muscles = [new() { name = "Spine Front-Back", unityIndex = 0 }, new() { name = "Spine Left-Right", unityIndex = 1 }, new() { name = "Spine Twist Left-Right", unityIndex = 2 }]
        };
        var group = new MuscleGroup { bones = [1], muscles = [0, 1, 2], resolution = 5, samples = new LatticeSample[125] };
        for (int i = 0; i < group.samples.Length; i++)
        {
            float x = (i % 5 - 2) / 2f, y = (i / 5 % 5 - 2) / 2f, z = (i / 25 - 2) / 2f;
            Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x * 0.6f) *
                           Quaternion.CreateFromAxisAngle(Vector3.UnitZ, y * 0.5f) *
                           Quaternion.CreateFromAxisAngle(Vector3.UnitY, z * 0.4f);
            group.samples[i] = new LatticeSample { rotations = Math3.A(q), positions = [0, 0.2f, 0] };
        }
        rig.groups = [group];
        return rig;
    }

    public static PoseBatch CreatePoses(Rig rig, int count = 60)
    {
        var solver = new HumanoidSolver(rig);
        var frames = new PoseFrame[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / 30f;
            var human = new HumanPose
            {
                bodyPosition = [t * 0.05f, 1 + MathF.Sin(t) * 0.1f, 0],
                bodyRotation = Math3.A(Quaternion.CreateFromAxisAngle(Vector3.UnitY, t * 0.2f)),
                muscles = [MathF.Sin(t * 2) * 0.7f, MathF.Cos(t) * 0.5f, MathF.Sin(t) * 0.6f]
            };
            frames[i] = solver.Decode(human.muscles, human).ToFrame(t);
            frames[i].reference = human;
        }
        return new PoseBatch { frames = frames };
    }
    private static Bone B(string name, int parent, float[] p, int human) => new() { name = name, parent = parent, position = p, humanBone = human };
}
