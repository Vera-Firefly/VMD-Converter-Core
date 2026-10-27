using System.Numerics;
using UnityHps;

internal static class SelfTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
        Rig rig = Demo.CreateRig(); var solver = new HumanoidSolver(rig);
        PoseBatch poses = Demo.CreatePoses(rig, 24);
        ResultBatch result = solver.SolveBatch(poses, 1);
        AccuracyReport report = solver.Compare(poses, result);
        Check(report.maxRootPositionMeters < 1e-5f && report.maxRootRotationDegrees < 0.001f && report.maxMuscleError < 0.0001f, "synthetic 3-axis + moving/rotating root round trip");
        ResultBatch parallel = solver.SolveBatch(poses, 4);
        Check(result.frames.Zip(parallel.frames).All(pair => pair.First.humanPose.muscles.SequenceEqual(pair.Second.humanPose.muscles)), "parallel determinism");
        var skeleton = new SkeletonPose(rig);
        float error = TwoBoneIk.Solve(skeleton, new IkGoal { root = 3, middle = 4, tip = 5, target = [-0.12f, 0.3f, 0.15f], pole = [-0.12f, 0.4f, 1] });
        Check(error < 0.00001f, "reachable two-bone IK end position");
        Check(Math.Abs(Vector3.Distance(skeleton.WorldPositions[3], skeleton.WorldPositions[4]) - 0.45f) < 1e-6f &&
            Math.Abs(Vector3.Distance(skeleton.WorldPositions[4], skeleton.WorldPositions[5]) - 0.45f) < 1e-6f, "IK preserves both limb lengths");
        Quaternion tipBefore = skeleton.WorldRotations[5];
        error = TwoBoneIk.Solve(skeleton, new IkGoal { root = 3, middle = 4, tip = 5, target = [-0.12f, -4, 0], pole = [-0.12f, -4, 0] });
        Check(float.IsFinite(error) && error > 4, "unreachable/collinear IK stays finite without stretching");
        Check(Math3.Angle(tipBefore, skeleton.WorldRotations[5]) < 0.001f, "IK preserves original tip orientation when unspecified");
        PoseFrame bad = new() { positions = [0, 0, 0], rotations = [0, 0, 0, 1] };
        bool rejected = false;
        try { solver.Solve(bad); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject incomplete pose instead of silently defaulting bones");
        rejected = false;
        try { solver.Decode([float.NaN, 0, 0]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject non-finite muscles");
        foreach (string field in new[] { "position", "rotation", "reference muscle" })
        {
            Rig invalidRig = Demo.CreateRig();
            LatticeSample sample = invalidRig.groups[0].samples[0];
            if (field == "position") sample.positions[0] = float.NaN;
            else if (field == "rotation") sample.rotations[0] = float.PositiveInfinity;
            else sample.muscles = [float.NaN];
            rejected = false;
            try { new HumanoidSolver(invalidRig); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject non-finite calibration " + field);
        }
        Rig rangedRig = Demo.CreateRig();
        rangedRig.groups[0].muscleRanges = [2f, 1f, 1f];
        var rangedSolver = new HumanoidSolver(rangedRig);
        Check(Math3.Angle(rangedSolver.Decode([2f, 0f, 0f]).LocalRotations[1],
                solver.Decode([1f, 0f, 0f]).LocalRotations[1]) < 0.001f,
            "sampled muscle range selects the correct lattice endpoint");
        rejected = false;
        try { rangedSolver.Decode([2.001f, 0f, 0f]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject muscle values beyond sampled range");
        Check(Math3.Angle(Quaternion.Identity, Math3.FromTo(Vector3.UnitY, -Vector3.UnitY)) > 179.9f, "antiparallel quaternion handling");
        float closeReverseError = 0;
        foreach (Vector3 direction in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.Normalize(new Vector3(.2f, -.3f, .7f)) })
        foreach (float separation in new[] { .001f, .0001f, .00001f, -.001f, -.0001f })
        {
            Vector3 perpendicular = Vector3.Normalize(Vector3.Cross(direction,
                Math.Abs(direction.X) < .8f ? Vector3.UnitX : Vector3.UnitY));
            Vector3 wanted = -direction * MathF.Cos(separation) + perpendicular * MathF.Sin(separation);
            Vector3 rotated = Math3.Rotate(Math3.FromTo(direction * 3.7f, wanted * .23f), direction);
            closeReverseError = Math.Max(closeReverseError, Vector3.Distance(rotated, wanted));
        }
        Check(closeReverseError < 5e-7f,
            $"near-antiparallel alignment preserves the actual target direction: max={closeReverseError * 1000:G6} mm at 1 m");
        // Rotation-invariant RootT affine mapping in world axes.
        Rig rootRig = Demo.CreateRig(); rootRig.bodyPositionJacobian = new float[rootRig.bones.Length * 9];
        rootRig.bodyPositionJacobian[0] = rootRig.bodyPositionJacobian[4] = rootRig.bodyPositionJacobian[8] = 1;
        var rootSolver = new HumanoidSolver(rootRig);
        report = rootSolver.Compare(poses, rootSolver.SolveBatch(poses, 1));
        Check(report.maxRootPositionMeters < 1e-5f, "RootT Jacobian uses world-space position once");
        Rig nested = Demo.CreateRig();
        foreach (var b in nested.bones) b.parent++;
        nested.bones = new[] { new Bone { name = "AnimatorRoot" } }.Concat(nested.bones).ToArray();
        nested.hips++;
        foreach (var group in nested.groups) group.bones = group.bones.Select(b => b + 1).ToArray();
        var nestedSolver = new HumanoidSolver(nested);
        var nestedPoses = Demo.CreatePoses(nested, 12);
        report = nestedSolver.Compare(nestedPoses, nestedSolver.SolveBatch(nestedPoses, 2));
        Check(report.maxRootPositionMeters < 1e-5f && nestedPoses.frames.All(f => Math3.V(f.positions) == Vector3.Zero && Math3.Angle(Math3.Q(f.rotations), Quaternion.Identity) < 0.001f),
            "HumanPose moves Hips under an unchanged Animator root");
        var analytic = new SkeletonPose(rig);
        analytic.LocalRotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.23f * 0.6f) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.31f * 0.5f) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.62f * 0.4f);
        analytic.UpdateWorld();
        HumanPose fitted = solver.Solve(analytic.ToFrame()).humanPose;
        Check(Math.Abs(fitted.muscles[0] - 0.23f) < 0.001f && Math.Abs(fitted.muscles[1] + 0.31f) < 0.001f && Math.Abs(fitted.muscles[2] - 0.62f) < 0.001f,
            "inverse fit against independent analytic quaternion pose");
        Rig selectedRig = Demo.CreateRig();
        foreach (Muscle muscle in selectedRig.muscles) muscle.humanBone = 7;
        PoseFrame selectedFrame = Demo.CreatePoses(selectedRig, 12).frames[7];
        float[] selectedMuscles = new HumanoidSolver(selectedRig, new HashSet<int> { 7 }).SolveMuscles(selectedFrame);
        float[] excludedMuscles = new HumanoidSolver(selectedRig, new HashSet<int> { 13 }).SolveMuscles(selectedFrame);
        float[] completeMuscles = new HumanoidSolver(selectedRig).SolveMuscles(selectedFrame);
        Check(selectedMuscles.Zip(completeMuscles).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6f) &&
              excludedMuscles.All(value => value == 0), "selected muscle groups fit independently");
        float[] seed = rig.muscles.Select((_, i) => i % 2 == 0 ? 0.2f : -0.3f).ToArray();
        PoseFrame decodedFrame = solver.Decode(seed).ToFrame();
        ResultBatch seeded = solver.SolveBatch(new PoseBatch { frames = [decodedFrame] }, 1, 2,
            initialMuscles: [seed]);
        Check(seeded.frames[0].humanPose.muscles.Zip(seed).All(pair => Math.Abs(pair.First - pair.Second) < 1e-4f),
            "initial muscles preserve target-rig decoded pose");
        Rig armRig = OfflineRigFactory.LoadProfile(false);
        var armSolver = new HumanoidSolver(armRig, new HashSet<int> { 14, 16, 18 });
        float[] armSeed = new float[armRig.muscles.Length];
        armSeed[48] = -0.6f; armSeed[49] = -0.2f; armSeed[50] = -0.02f;
        armSeed[51] = -0.6f; armSeed[52] = -0.09f;
        SkeletonPose armPose = armSolver.Decode(armSeed);
        int upper = Array.FindIndex(armRig.bones, b => b.humanBone == 14);
        int elbow = Array.FindIndex(armRig.bones, b => b.humanBone == 16);
        int hand = Array.FindIndex(armRig.bones, b => b.humanBone == 18);
        Vector3 elbowPosition = armPose.WorldPositions[elbow], handPosition = armPose.WorldPositions[hand];
        Quaternion elbowRotation = armPose.WorldRotations[elbow], handRotation = armPose.WorldRotations[hand];
        float[] unrolled = armSolver.SolveMuscles(armPose.ToFrame());
        // Redistribute local roll without changing joint locations or the hand pose.
        // A rotation-only chain fit incorrectly uses the upper-arm muscle to follow it.
        armPose.LocalRotations[upper] *= Quaternion.CreateFromAxisAngle(Vector3.Normalize(armPose.LocalPositions[elbow]), 1.4f);
        armPose.UpdateWorld();
        armPose.LocalRotations[elbow] = Quaternion.Inverse(armPose.WorldRotations[upper]) * elbowRotation;
        armPose.UpdateWorld();
        Check(Vector3.Distance(elbowPosition, armPose.WorldPositions[elbow]) < 1e-6f &&
              Vector3.Distance(handPosition, armPose.WorldPositions[hand]) < 1e-6f &&
              Math3.Angle(handRotation, armPose.WorldRotations[hand]) < 0.001f,
            "arm-roll fixture preserves joint geometry and hand orientation");
        float[] rolled = armSolver.SolveMuscles(armPose.ToFrame());
        Check(rolled.Zip(unrolled).All(pair => Math.Abs(pair.First - pair.Second) < 0.001f) &&
              Math.Abs(rolled[50]) < 0.1f, "arm projection does not chase redistributed local twist to a limit");
        SolveResult seededArm = armSolver.Solve(armSolver.Decode(armSeed).ToFrame(), initialMuscles: armSeed);
        Check(seededArm.humanPose.muscles.Zip(armSeed).All(pair => Math.Abs(pair.First - pair.Second) < 1e-5f),
            "arm projection preserves an exact decoded initial pose");
        var folded = new List<PoseFrame>();
        for (int i = 0; i < 5; i++)
        {
            float[] fold = (float[])armSeed.Clone();
            fold[50] = 0.07f; fold[51] = 0.98f + i * 0.005f; fold[52] = 0.04f;
            folded.Add(armSolver.Decode(fold).ToFrame(i / 30f));
        }
        float[][] foldedMuscles = folded.Select(f => armSolver.SolveMuscles(f)).ToArray();
        Check(foldedMuscles.All(ms => ms.All(float.IsFinite)) && foldedMuscles.Zip(foldedMuscles.Skip(1))
            .All(pair => Math.Abs(pair.First[50] - pair.Second[50]) < 0.1f && Math.Abs(pair.First[52] - pair.Second[52]) < 0.1f),
            "near-collinear arm projection keeps finite, stable twist branches");
        ResultBatch armSerial = armSolver.SolveBatch(new PoseBatch { frames = folded.ToArray() }, 1);
        ResultBatch armParallel = armSolver.SolveBatch(new PoseBatch { frames = folded.ToArray() }, 4);
        Check(armSerial.frames.Zip(armParallel.frames).All(pair => pair.First.humanPose.muscles.SequenceEqual(pair.Second.humanPose.muscles)),
            "arm projection is deterministic across frame worker counts");
        FkContinuityTests.Run(Check);
        RootReconstructionTests.Run(Check);
        BodyMassCalibrationTests.Run(Check);
        ArmContextTests.Run(Check);
        LegGeometryTests.Run(Check);
        RigTransportTests.Run(Check);
        TorsoTransportTests.Run(Check);
        UpperProjectionTests.Run(Check);
        RangePolicyTests.Run(Check);
        FkBranchTests.Run(Check);
        FbxImportTests.Run(Check);
        Console.WriteLine($"{passed} tests passed. These synthetic tests do NOT establish Unity equivalence.");
    }
}
