using System.Reflection;
using System.Runtime.Loader;

namespace VeraFirefly.VmdConverter.Core.Executable;

internal static class HpsBackendLoader
{
    // Offline animation/curve output always requires HPS. Raw PMX preprocessing is independent.
    public static IVmdHumanoidBackend Require(string? directory)
    {
        string folder = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        string adapter = Path.Combine(folder, "VmdConverterCore.Hps.dll");
        string core = Path.Combine(folder, "UnityHps.Core.dll");
        if (!File.Exists(adapter) || !File.Exists(core))
        {
            throw new FileNotFoundException("Offline animation conversion requires Unity-HPS. Keep VmdConverterCore.Hps.dll and UnityHps.Core.dll beside the EXE, or specify --hps-dir.");
        }
        // Share VMDCC's contract assembly with the plug-in; load its SDK from
        // the selected folder without requiring it in the EXE dependency graph.
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (name.Name != "UnityHps.Core") return null;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(core);
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(adapter);
        var type = assembly.GetType("VeraFirefly.VmdConverter.Core.Hps.VmdHpsBackend", true)!;
        return (IVmdHumanoidBackend)Activator.CreateInstance(type)!;
    }
}
