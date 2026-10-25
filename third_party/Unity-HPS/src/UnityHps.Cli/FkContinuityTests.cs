using System.Numerics;
using UnityHps;

internal static class FkContinuityTests
{
    public static void Run(Action<bool, string> check)
    {
        Rig rig = MakeRig();
        foreach (float speed in new[] { 5f, 140f, -140f })
        foreach (bool injected in new[] { false, true })
        {
            var solver = new FkRollContinuation(rig, rig);
            double maxError = 0, positionError = 0;
            for (int frame = 0; frame < 60; frame++)
            {
                PoseFrame source = Frame(rig, frame, speed, false), target = Frame(rig, frame, speed, injected && frame >= 10 && frame <= 18);
                PoseFrame actual = solver.Process(source, target);
                var a = new SkeletonPose(rig, actual); var expected = new SkeletonPose(rig, source);
                for (int b = 0; b < rig.bones.Length; b++)
                {
                    maxError = Math.Max(maxError, FkRollContinuation.Angle(a.WorldRotations[b], expected.WorldRotations[b]));
                    positionError = Math.Max(positionError, Vector3.Distance(a.WorldPositions[b], expected.WorldPositions[b]));
                }
            }
            check(maxError < .001 && positionError < 1e-5 && solver.AlteredForearmFrames == (injected ? 18 : 0), $"FK continuity preserves source motion: {speed} deg/frame, injected={injected}");
        }
        var signed = new FkRollContinuation(rig, rig);
        for (int frame = 0; frame < 60; frame++)
        {
            var source = Frame(rig, frame, 140, false); var target = Frame(rig, frame, 140, false);
            if (frame % 2 != 0) for (int i = 0; i < target.rotations.Length; i++) target.rotations[i] = -target.rotations[i];
            signed.Process(source, target);
        }
        check(signed.AlteredForearmFrames == 0, "FK continuity ignores quaternion sign changes");
        var order = new FkRollContinuation(rig, rig);
        order.Process(Frame(rig, 0, 5, false), Frame(rig, 0, 5, false));
        bool rejected = false;
        try { order.Process(Frame(rig, 0, 5, false), Frame(rig, 0, 5, false)); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "FK continuity rejects duplicate frame times");
        order.Reset();
        check(order.Process(Frame(rig, 0, 5, false), Frame(rig, 0, 5, false)).time == 0, "FK continuity resets between clips");
        order.Reset();
        var negative = Frame(rig, 0, 5, false); negative.time = -1e-6f;
        rejected = false;
        try { order.Process(negative, Frame(rig, 0, 5, false)); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "FK continuity rejects negative source time within matching tolerance");
        var closeSource = Frame(rig, 0, 5, false); closeSource.time = 1e-6f;
        var closeTarget = Frame(rig, 0, 5, false); closeTarget.time = 2e-6f;
        order.Process(closeSource, closeTarget);
        closeSource.time = .5e-6f; closeTarget.time = 3e-6f;
        rejected = false;
        try { order.Process(closeSource, closeTarget); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "FK continuity checks source chronology within matching tolerance");
        check(Parallel.For(0, 12, _ =>
        {
            var separate = new FkRollContinuation(rig, rig);
            for (int frame = 0; frame < 60; frame++) separate.Process(Frame(rig, frame, 140, false), Frame(rig, frame, 140, frame >= 10 && frame <= 18));
            if (separate.AlteredForearmFrames != 18) throw new Exception("Parallel clip state leaked.");
        }).IsCompleted, "FK continuity isolates state across parallel clips");
    }
    static Rig MakeRig() => new() { bones = [
        new() { name="root" }, new() { name="chest", parent=0, humanBone=8, position=[0,1,0] },
        new() { name="leftUpper", parent=1, humanBone=13, position=[-.2f,0,0] },
        new() { name="leftFore", parent=2, humanBone=15, position=[-.3f,0,0] },
        new() { name="leftHand", parent=3, humanBone=17, position=[-.25f,0,0] },
        new() { name="leftFinger", parent=4, humanBone=24, position=[-.05f,.01f,0] },
        new() { name="rightUpper", parent=1, humanBone=14, position=[.2f,0,0] },
        new() { name="rightFore", parent=6, humanBone=16, position=[.3f,0,0] },
        new() { name="rightHand", parent=7, humanBone=18, position=[.25f,0,0] },
        new() { name="rightFinger", parent=8, humanBone=39, position=[.05f,.01f,0] }
    ] };
    static PoseFrame Frame(Rig rig, int frame, float speed, bool flip)
    {
        var pose = new SkeletonPose(rig);
        pose.LocalPositions[0] = new Vector3(frame*.01f, .1f, 0);
        pose.LocalRotations[1] = Quaternion.CreateFromYawPitchRoll(frame*.03f, frame*.01f, frame*.02f);
        foreach ((int fore, int hand) in new[] { (3, 4), (7, 8) })
        {
            pose.LocalRotations[fore] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, frame*speed*MathF.PI/180);
            if (flip)
            {
                pose.LocalRotations[fore] *= Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
                pose.LocalRotations[hand] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI);
            }
        }
        return pose.ToFrame(frame/30f);
    }
}
