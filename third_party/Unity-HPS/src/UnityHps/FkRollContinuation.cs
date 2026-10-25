using System.Numerics;
namespace UnityHps;

/// <summary>
/// Optional transform-FK forearm roll continuity pass, guided by the raw source FK.
/// It changes forearm and wrist local rotations, preserving the wrist world pose.
/// This does not solve or rewrite Humanoid muscles. Use one instance per clip;
/// process frames chronologically and parallelize across clips, not within a clip.
/// </summary>
public sealed class FkRollContinuation
{
    readonly Rig sourceRig, targetRig;
    readonly int sourceChest, targetChest;
    readonly int[] sourceForearms, forearms, wrists;
    readonly Quaternion alignment;
    readonly Quaternion[] previousSource = new Quaternion[2], previousSelected = new Quaternion[2];
    bool initialized;
    float previousTime, previousSourceTime;
    public int AlteredForearmFrames { get; private set; }

    public FkRollContinuation(Rig source, Rig target)
    {
        sourceRig = source; targetRig = target;
        ValidateOrder(source); ValidateOrder(target);
        int Find(Rig r, int human)
        {
            int i = Array.FindIndex(r.bones, b => b.humanBone == human);
            return i >= 0 ? i : throw new InvalidDataException("Missing human bone " + human);
        }
        sourceChest = Find(source, 8); targetChest = Find(target, 8);
        sourceForearms = [Find(source, 15), Find(source, 16)];
        forearms = [Find(target, 15), Find(target, 16)];
        wrists = [Find(target, 17), Find(target, 18)];
        for (int side = 0; side < 2; side++)
        {
            int ancestor = target.bones[wrists[side]].parent;
            while (ancestor >= 0 && ancestor != forearms[side]) ancestor = target.bones[ancestor].parent;
            if (ancestor < 0) throw new InvalidDataException("Wrist must descend from forearm.");
        }
        alignment = Unit(Quaternion.Inverse(new SkeletonPose(target).WorldRotations[targetChest]) * new SkeletonPose(source).WorldRotations[sourceChest]);
    }

    /// <summary>Reset temporal state before reusing this instance for a new clip.</summary>
    public void Reset() { initialized = false; AlteredForearmFrames = 0; }

    /// <summary>Return an independent FK frame; inputs are never modified.</summary>
    public PoseFrame Process(PoseFrame source, PoseFrame target)
    {
        if (!float.IsFinite(target.time) || target.time < 0 || Math.Abs(source.time - target.time) > 1e-5f || !float.IsFinite(source.time) || source.time < 0)
            throw new InvalidDataException("Source and target must have matching finite times.");
        if (initialized && (target.time <= previousTime || source.time <= previousSourceTime)) throw new InvalidDataException("Frames must be strictly chronological; Reset between clips.");
        var input = new SkeletonPose(sourceRig, source);
        var output = new SkeletonPose(targetRig, target);
        var sources = new Quaternion[2]; var selected = new Quaternion[2];
        int altered = 0;
        for (int side = 0; side < 2; side++)
        {
            int fore = forearms[side], hand = wrists[side];
            Quaternion native = Unit(Quaternion.Inverse(output.WorldRotations[targetChest]) * output.WorldRotations[fore]);
            Quaternion raw = Unit(Quaternion.Inverse(input.WorldRotations[sourceChest]) * input.WorldRotations[sourceForearms[side]]);
            Vector3 axis = Vector3.Transform(output.WorldPositions[hand] - output.WorldPositions[fore], Quaternion.Inverse(output.WorldRotations[fore]));
            if (axis.LengthSquared() < 1e-12f) throw new InvalidDataException("Zero forearm-to-wrist length.");
            Quaternion halfTurn = new(Vector3.Normalize(axis), 0);
            Quaternion alternate = Unit(native * halfTurn);
            bool flip = false;
            if (initialized)
            {
                Quaternion delta = Unit(raw * Quaternion.Inverse(previousSource[side]));
                Quaternion expected = Unit(alignment * delta * Quaternion.Inverse(alignment) * previousSelected[side]);
                double a = Angle(expected, native), b = Angle(expected, alternate);
                flip = a > 110 && b < 75 && b + 20 < a;
            }
            if (flip)
            {
                Quaternion originalHand = output.WorldRotations[hand];
                output.LocalRotations[fore] = Unit(output.LocalRotations[fore] * halfTurn);
                output.UpdateWorld();
                output.SetWorldRotation(hand, originalHand);
                altered++;
            }
            sources[side] = raw; selected[side] = flip ? alternate : native;
        }
        sources.CopyTo(previousSource, 0); selected.CopyTo(previousSelected, 0);
        initialized = true; previousTime = target.time; previousSourceTime = source.time; AlteredForearmFrames += altered;
        return output.ToFrame(target.time);
    }

    static void ValidateOrder(Rig rig)
    {
        for (int i = 0; i < rig.bones.Length; i++)
            if (rig.bones[i].parent < -1 || rig.bones[i].parent >= i)
                throw new InvalidDataException("Rig needs parent-before-child ordering.");
    }
    static Quaternion Unit(Quaternion q) => Quaternion.Normalize(q);
    public static double Angle(Quaternion a, Quaternion b)
    {
        double dot = (double)a.X*b.X + (double)a.Y*b.Y + (double)a.Z*b.Z + (double)a.W*b.W;
        double aa = (double)a.X*a.X + (double)a.Y*a.Y + (double)a.Z*a.Z + (double)a.W*a.W;
        double bb = (double)b.X*b.X + (double)b.Y*b.Y + (double)b.Z*b.Z + (double)b.W*b.W;
        return 2*Math.Acos(Math.Clamp(Math.Abs(dot)/Math.Sqrt(aa*bb), 0, 1))*180/Math.PI;
    }
}
