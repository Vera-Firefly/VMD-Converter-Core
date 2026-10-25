using System.Numerics;

namespace VeraFirefly.VmdConverter.Core.Retarget;

/// <summary>Pure rotation transport in the target skeleton's local rest frames.</summary>
public static class RetargetMath
{
    /// <summary>
    /// Source rotations are world-space deltas from an identity PMX rest.
    /// The nearest mapped target ancestor may be separated from the direct
    /// parent by rest-only helper nodes. Conjugating by their rest chain moves
    /// the source delta into the direct parent's local coordinate frame.
    /// </summary>
    public static Quaternion LocalRotation(
        Quaternion sourceWorld,
        Quaternion sourceAncestorWorld,
        Quaternion targetAncestorRestWorld,
        Quaternion targetParentRestWorld,
        Quaternion targetRestLocal)
    {
        Quaternion sourceDelta = Quaternion.Normalize(
            Quaternion.Inverse(sourceAncestorWorld) * sourceWorld);
        Quaternion ancestorToParentRest = Quaternion.Normalize(
            Quaternion.Inverse(targetAncestorRestWorld) * targetParentRestWorld);
        Quaternion parentDelta = Quaternion.Normalize(
            Quaternion.Inverse(ancestorToParentRest) * sourceDelta * ancestorToParentRest);
        return Quaternion.Normalize(parentDelta * targetRestLocal);
    }

    public static int NearestMappedAncestor(int parent, IReadOnlyList<int> parents,
        IReadOnlyList<bool> mapped)
    {
        for (int index = parent; index >= 0; index = parents[index])
            if (mapped[index]) return index;
        return -1;
    }
}
