using System;

namespace VeraFirefly.VmdConverter.Core
{
    public enum VmdTargetSolveRegion { Canonical = 0, Leg = 1, Arm = 2, All = 3 }

    public static class VmdOutputModes
    {
        public static bool IsFk(VmdExternalOutputKind kind)
        {
            return kind == VmdExternalOutputKind.TargetFk || kind == VmdExternalOutputKind.TargetFkAll ||
                kind == VmdExternalOutputKind.TargetFkArm || kind == VmdExternalOutputKind.TargetFkLeg;
        }

        public static VmdTargetSolveRegion Region(VmdExternalOutputKind kind)
        {
            switch (kind)
            {
                case VmdExternalOutputKind.HumanoidIk: return VmdTargetSolveRegion.Canonical;
                case VmdExternalOutputKind.TargetCalibratedHumanoid:
                case VmdExternalOutputKind.TargetFk:
                case VmdExternalOutputKind.TargetCalibratedHumanoidLeg:
                case VmdExternalOutputKind.TargetFkLeg: return VmdTargetSolveRegion.Leg;
                case VmdExternalOutputKind.TargetCalibratedHumanoidArm:
                case VmdExternalOutputKind.TargetFkArm: return VmdTargetSolveRegion.Arm;
                case VmdExternalOutputKind.TargetCalibratedHumanoidAll:
                case VmdExternalOutputKind.TargetFkAll: return VmdTargetSolveRegion.All;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static string ToArgument(VmdExternalOutputKind kind)
        {
            switch (kind)
            {
                case VmdExternalOutputKind.HumanoidIk: return "humanoid-ik";
                case VmdExternalOutputKind.TargetFk: return "target-fk";
                case VmdExternalOutputKind.TargetFkAll: return "target-fk-all";
                case VmdExternalOutputKind.TargetFkArm: return "target-fk-arm";
                case VmdExternalOutputKind.TargetFkLeg: return "target-fk-leg";
                case VmdExternalOutputKind.TargetCalibratedHumanoid: return "target-humanoid";
                case VmdExternalOutputKind.TargetCalibratedHumanoidAll: return "target-humanoid-all";
                case VmdExternalOutputKind.TargetCalibratedHumanoidArm: return "target-humanoid-arm";
                case VmdExternalOutputKind.TargetCalibratedHumanoidLeg: return "target-humanoid-leg";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static VmdExternalOutputKind Parse(string value)
        {
            for (int i = 0; i <= 8; i++)
                if (string.Equals(ToArgument((VmdExternalOutputKind)i), value, StringComparison.OrdinalIgnoreCase))
                    return (VmdExternalOutputKind)i;
            throw new ArgumentException("Unknown output mode: " + value, nameof(value));
        }
    }
}
