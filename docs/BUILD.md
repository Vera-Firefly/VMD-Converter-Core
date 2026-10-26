# Build and deployment

Requirements: Windows x64, .NET SDK10, Visual Studio C++ x64 desktop tools. Run `./scripts/Build-Release.ps1 -OutputDirectory artifacts/win-x64` with a new/empty output directory.

Build order: quaternion tolerance checks → native ufbx bridge → HPS adapter and embedded response resources → Shared contract and EXE → self-contained publish → VMDCC Unity integration package. HPS is a mandatory external dependency for curve/animation output and is rebuilt separately from the host project.

Deploy these files together: VmdConverterCore.exe, VmdConverterCore.Shared.dll, VmdConverterCore.Hps.dll, UnityHps.Core.dll, UnityHps.Fbx.dll. Do not mix files from different builds. The release manifest records SHA256 digests. The Core Unity package includes them under Tools; plugin importers disable loading these .NET10 libraries into Unity.

The output includes the com.verafirefly.vmdcc Unity package, Unity calling examples, and license notices. For source development use `dotnet build VmdConverterCore.csproj -c Release` and `dotnet build src/Modules/Hps/VmdConverterCore.Hps.csproj -c Release`; run through the release script to deploy a complete set.
