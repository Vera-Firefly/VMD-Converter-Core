using System.Globalization;
using System.Text;
using System.Text.Json;
using VeraFirefly.VmdConverter.Core;

namespace VeraFirefly.VmdConverter.Core.Executable;

/// <summary>
/// EXE-only diagnostic serializer for Unity 2022.3 text AnimationClips.
/// It deliberately does not use UnityEngine or UnityEditor assemblies.
/// </summary>
internal static class VmdAnimYamlWriter
{
    private sealed record Binding(uint Path, uint Attribute, int Type, int CustomType);

    private sealed class TransformGroup
    {
        public string Path = string.Empty;
        public VmdFloatCurveData[] Components = Array.Empty<VmdFloatCurveData>();
    }

    public static void Write(Stream stream, string clipName, VmdTargetCurveData data,
        VmdTargetRigData rig)
    {
        if (stream == null || data == null || rig == null)
            throw new ArgumentNullException("Animation output requires stream, curves and rig.");
        if (data.Curves.Count == 0)
            throw new InvalidDataException("No animation curves to write.");
        foreach (VmdFloatCurveData curve in data.Curves)
            ValidateCurve(curve);

        List<TransformGroup> rotations = GroupTransforms(data.Curves,
            "m_LocalRotation.", "xyzw");
        List<TransformGroup> positions = GroupTransforms(data.Curves,
            "m_LocalPosition.", "xyz");
        var bindings = new List<Binding>();
        var seen = new HashSet<Binding>();
        // Compiled binding order follows the serialized curve sections.
        IEnumerable<VmdFloatCurveData> compiledOrder =
            rotations.Select(group => group.Components[0])
                .Concat(positions.Select(group => group.Components[0]))
                .Concat(data.Curves.Where(curve => curve.BindingKind != 0));
        foreach (VmdFloatCurveData curve in compiledOrder)
        {
            Binding binding = GetBinding(curve, rig);
            if (seen.Add(binding)) bindings.Add(binding);
        }

        float stopTime = data.Curves.Max(curve => curve.Times[^1]);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false),
            bufferSize: 65536, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%YAML 1.1");
        writer.WriteLine("%TAG !u! tag:unity3d.com,2011:");
        writer.WriteLine("--- !u!74 &7400000");
        writer.WriteLine("AnimationClip:");
        writer.WriteLine("  m_ObjectHideFlags: 0");
        writer.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        writer.WriteLine("  m_PrefabInstance: {fileID: 0}");
        writer.WriteLine("  m_PrefabAsset: {fileID: 0}");
        writer.WriteLine("  m_Name: " + Quote(clipName));
        writer.WriteLine("  serializedVersion: 7");
        writer.WriteLine("  m_Legacy: 0");
        writer.WriteLine("  m_Compressed: 0");
        writer.WriteLine("  m_UseHighQualityCurve: 1");
        WriteVectorSection(writer, "m_RotationCurves", rotations, "xyzw");
        writer.WriteLine("  m_CompressedRotationCurves: []");
        writer.WriteLine("  m_EulerCurves: []");
        WriteVectorSection(writer, "m_PositionCurves", positions, "xyz");
        writer.WriteLine("  m_ScaleCurves: []");
        WriteFloatSection(writer, "m_FloatCurves",
            data.Curves.Where(curve => curve.BindingKind != 0));
        writer.WriteLine("  m_PPtrCurves: []");
        writer.WriteLine("  m_SampleRate: " + Number(data.SampleRate));
        writer.WriteLine("  m_WrapMode: 0");
        writer.WriteLine("  m_Bounds:");
        writer.WriteLine("    m_Center: {x: 0, y: 0, z: 0}");
        writer.WriteLine("    m_Extent: {x: 0, y: 0, z: 0}");
        writer.WriteLine("  m_ClipBindingConstant:");
        if (bindings.Count == 0)
            writer.WriteLine("    genericBindings: []");
        else
        {
            writer.WriteLine("    genericBindings:");
            foreach (Binding binding in bindings)
            {
                writer.WriteLine("    - serializedVersion: 2");
                writer.WriteLine("      path: " + binding.Path);
                writer.WriteLine("      attribute: " + binding.Attribute);
                writer.WriteLine("      script: {fileID: 0}");
                writer.WriteLine("      typeID: " + binding.Type);
                writer.WriteLine("      customType: " + binding.CustomType);
                writer.WriteLine("      isPPtrCurve: 0");
                writer.WriteLine("      isIntCurve: 0");
                writer.WriteLine("      isSerializeReferenceCurve: 0");
            }
        }
        writer.WriteLine("    pptrCurveMapping: []");
        writer.WriteLine("  m_AnimationClipSettings:");
        writer.WriteLine("    serializedVersion: 2");
        writer.WriteLine("    m_AdditiveReferencePoseClip: {fileID: 0}");
        writer.WriteLine("    m_AdditiveReferencePoseTime: 0");
        writer.WriteLine("    m_StartTime: 0");
        writer.WriteLine("    m_StopTime: " + Number(stopTime));
        writer.WriteLine("    m_OrientationOffsetY: 0");
        writer.WriteLine("    m_Level: 0");
        writer.WriteLine("    m_CycleOffset: 0");
        writer.WriteLine("    m_HasAdditiveReferencePose: 0");
        writer.WriteLine("    m_LoopTime: 0");
        writer.WriteLine("    m_LoopBlend: 0");
        writer.WriteLine("    m_LoopBlendOrientation: 1");
        writer.WriteLine("    m_LoopBlendPositionY: 1");
        writer.WriteLine("    m_LoopBlendPositionXZ: 1");
        writer.WriteLine("    m_KeepOriginalOrientation: 0");
        writer.WriteLine("    m_KeepOriginalPositionY: 1");
        writer.WriteLine("    m_KeepOriginalPositionXZ: 0");
        writer.WriteLine("    m_HeightFromFeet: 0");
        writer.WriteLine("    m_Mirror: 0");
        WriteFloatSection(writer, "m_EditorCurves", data.Curves);
        writer.WriteLine("  m_EulerEditorCurves: []");
        writer.WriteLine("  m_HasGenericRootTransform: 0");
        writer.WriteLine("  m_HasMotionFloatCurves: 0");
        writer.WriteLine("  m_Events: []");
        writer.Flush();
    }

    private static void ValidateCurve(VmdFloatCurveData curve)
    {
        if (curve == null || curve.Times == null || curve.Values == null ||
            curve.Times.Length == 0 || curve.Times.Length != curve.Values.Length)
            throw new InvalidDataException("Invalid animation curve.");
        for (int i = 0; i < curve.Times.Length; i++)
        {
            if (!float.IsFinite(curve.Times[i]) || !float.IsFinite(curve.Values[i]) ||
                i > 0 && curve.Times[i] <= curve.Times[i - 1])
                throw new InvalidDataException("Animation keys must be finite and ordered.");
        }
    }

    private static List<TransformGroup> GroupTransforms(
        IReadOnlyList<VmdFloatCurveData> curves, string prefix, string axes)
    {
        var groups = new List<TransformGroup>();
        foreach (var pathGroup in curves.Where(curve => curve.BindingKind == 0 &&
                     curve.Property.StartsWith(prefix, StringComparison.Ordinal))
                 .GroupBy(curve => curve.Path ?? string.Empty))
        {
            var components = new VmdFloatCurveData[axes.Length];
            foreach (VmdFloatCurveData curve in pathGroup)
            {
                int axis = curve.Property.Length == prefix.Length + 1
                    ? axes.IndexOf(curve.Property[^1]) : -1;
                if (axis < 0 || components[axis] != null)
                    throw new InvalidDataException("Unsupported transform curve: " + curve.Property);
                components[axis] = curve;
            }
            if (components.Any(component => component == null))
                throw new InvalidDataException("Transform vector is missing a component: " + pathGroup.Key);
            float[] times = components[0].Times;
            foreach (VmdFloatCurveData component in components.Skip(1))
                if (!times.SequenceEqual(component.Times))
                    throw new InvalidDataException("Transform component key times differ: " + pathGroup.Key);
            groups.Add(new TransformGroup { Path = pathGroup.Key, Components = components });
        }
        return groups;
    }

    private static Binding GetBinding(VmdFloatCurveData curve, VmdTargetRigData rig)
    {
        if (curve.BindingKind == 0)
        {
            uint attribute = curve.Property.StartsWith("m_LocalPosition.", StringComparison.Ordinal)
                ? 1u : curve.Property.StartsWith("m_LocalRotation.", StringComparison.Ordinal)
                    ? 2u : throw new NotSupportedException("Transform property: " + curve.Property);
            return new Binding(Crc32(curve.Path ?? string.Empty), attribute, 4, 0);
        }
        if (curve.BindingKind == 1 &&
            curve.Property.StartsWith("blendShape.", StringComparison.Ordinal))
            return new Binding(Crc32(curve.Path ?? string.Empty),
                Crc32(curve.Property["blendShape.".Length..]), 137, 20);
        if (curve.BindingKind == 2)
        {
            uint attribute = curve.Property switch
            {
                "RootT.x" => 7, "RootT.y" => 8, "RootT.z" => 9,
                "RootQ.x" => 10, "RootQ.y" => 11,
                "RootQ.z" => 12, "RootQ.w" => 13,
                _ => MuscleAttribute(curve.Property, rig)
            };
            return new Binding(0, attribute, 95, 8);
        }
        throw new NotSupportedException("Animation binding: " + curve.Property);
    }

    private static uint MuscleAttribute(string property, VmdTargetRigData rig)
    {
        VmdHumanoidCalibrationData? calibration = rig.humanoid ?? rig.canonicalRig?.humanoid;
        if (calibration?.muscles == null)
            throw new InvalidDataException("Humanoid calibration is missing.");
        for (int i = 0; i < calibration.muscles.Length; i++)
            if (string.Equals(calibration.muscles[i]?.property, property,
                    StringComparison.Ordinal))
                return checked((uint)(42 + i));
        throw new InvalidDataException("Unknown Humanoid muscle: " + property);
    }

    private static void WriteFloatSection(StreamWriter writer, string name,
        IEnumerable<VmdFloatCurveData> source)
    {
        VmdFloatCurveData[] curves = source.ToArray();
        if (curves.Length == 0)
        {
            writer.WriteLine("  " + name + ": []");
            return;
        }
        writer.WriteLine("  " + name + ":");
        foreach (VmdFloatCurveData curve in curves)
        {
            writer.WriteLine("  - serializedVersion: 2");
            writer.WriteLine("    curve:");
            writer.WriteLine("      serializedVersion: 2");
            writer.WriteLine("      m_Curve:");
            for (int i = 0; i < curve.Times.Length; i++)
            {
                writer.WriteLine("      - serializedVersion: 3");
                WriteNumberLine(writer,"        time: ",curve.Times[i]);
                WriteNumberLine(writer,"        value: ",curve.Values[i]);
                WriteNumberLine(writer,"        inSlope: ",Slope(curve,i,false));
                WriteNumberLine(writer,"        outSlope: ",Slope(curve,i,true));
                writer.Write("        tangentMode: 0\n        weightedMode: 0\n        inWeight: 0\n        outWeight: 0\n");
            }
            writer.WriteLine("      m_PreInfinity: 2");
            writer.WriteLine("      m_PostInfinity: 2");
            writer.WriteLine("      m_RotationOrder: 4");
            writer.WriteLine("    attribute: " + Quote(curve.Property));
            writer.WriteLine("    path: " + Quote(curve.Path ?? string.Empty));
            writer.WriteLine("    classID: " + (curve.BindingKind == 0 ? 4 :
                curve.BindingKind == 1 ? 137 : 95));
            writer.WriteLine("    script: {fileID: 0}");
            writer.WriteLine("    flags: 16");
        }
    }

    private static void WriteVectorSection(StreamWriter writer, string name,
        IReadOnlyList<TransformGroup> groups, string axes)
    {
        if (groups.Count == 0)
        {
            writer.WriteLine("  " + name + ": []");
            return;
        }
        writer.WriteLine("  " + name + ":");
        foreach (TransformGroup group in groups)
        {
            writer.WriteLine("  - curve:");
            writer.WriteLine("      serializedVersion: 2");
            writer.WriteLine("      m_Curve:");
            for (int i = 0; i < group.Components[0].Times.Length; i++)
            {
                writer.WriteLine("      - serializedVersion: 3");
                WriteNumberLine(writer,"        time: ",group.Components[0].Times[i]);
                WriteVectorLine(writer,"        value: ",group.Components,axes,i,0);
                WriteVectorLine(writer,"        inSlope: ",group.Components,axes,i,-1);
                WriteVectorLine(writer,"        outSlope: ",group.Components,axes,i,1);
                writer.WriteLine("        tangentMode: 0");
                writer.WriteLine("        weightedMode: 0");
                writer.WriteLine("        inWeight: " + ConstantVector(axes, "0.33333334"));
                writer.WriteLine("        outWeight: " + ConstantVector(axes, "0.33333334"));
            }
            writer.WriteLine("      m_PreInfinity: 2");
            writer.WriteLine("      m_PostInfinity: 2");
            writer.WriteLine("      m_RotationOrder: 4");
            writer.WriteLine("    path: " + Quote(group.Path));
        }
    }

    private static void WriteNumberLine(StreamWriter writer,string prefix,float value)
    {
        Span<char> buffer=stackalloc char[96];prefix.AsSpan().CopyTo(buffer);
        if(!value.TryFormat(buffer[prefix.Length..],out int size,"R",CultureInfo.InvariantCulture))throw new InvalidDataException("Number formatting overflow.");
        writer.WriteLine(buffer[..(prefix.Length+size)]);
    }

    private static void WriteVectorLine(StreamWriter writer,string prefix,VmdFloatCurveData[] curves,string axes,int index,int mode)
    {
        Span<char> buffer=stackalloc char[256];prefix.AsSpan().CopyTo(buffer);int cursor=prefix.Length;buffer[cursor++]='{';
        for(int c=0;c<curves.Length;c++)
        {
            if(c>0){buffer[cursor++]=',';buffer[cursor++]=' ';}
            buffer[cursor++]=axes[c];buffer[cursor++]=':';buffer[cursor++]=' ';
            float value=mode==0?curves[c].Values[index]:Slope(curves[c],index,mode>0);
            if(!value.TryFormat(buffer[cursor..],out int size,"R",CultureInfo.InvariantCulture))throw new InvalidDataException("Vector formatting overflow.");
            cursor+=size;
        }
        buffer[cursor++]='}';writer.WriteLine(buffer[..cursor]);
    }

    private static string ConstantVector(string axes, string value) =>
        "{" + string.Join(", ", axes.Select(axis => axis + ": " + value)) + "}";

    private static float Slope(VmdFloatCurveData curve, int index, bool outgoing)
    {
        if (curve.Times.Length == 1) return 0;
        int start = outgoing ? Math.Min(index, curve.Times.Length - 2) : Math.Max(0, index - 1);
        float dt = curve.Times[start + 1] - curve.Times[start];
        return dt > 0f ? (curve.Values[start + 1] - curve.Values[start]) / dt : 0f;
    }

    private static uint Crc32(string value)
    {
        uint crc = 0xffffffffu;
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~crc;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
