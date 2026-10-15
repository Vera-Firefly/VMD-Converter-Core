using System.Reflection;
using System.Runtime.Loader;

namespace VeraFirefly.VmdConverter.Core.Executable;

internal static class HpsBackendLoader
{
    // Called exclusively by the --anim-w branch. Normal Unity/package calls
    // neither probe the filesystem nor load any HPS assembly.
    public static IVmdHumanoidBackend? Find(string? directory)
    {
        string folder = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        string adapter = Path.Combine(folder, "VmdConverterCore.Hps.dll");
        string core = Path.Combine(folder, "UnityHps.Core.dll");
        if (!File.Exists(adapter) || !File.Exists(core))
        {
            Console.Error.WriteLine("Unity-HPS unavailable; using the original VMDCC solver.");
            return null;
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
