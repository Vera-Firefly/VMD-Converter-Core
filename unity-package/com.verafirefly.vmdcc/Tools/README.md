# External converter files

Keep these required files together:

- `VmdConverterCore.exe`
- `VmdConverterCore.Shared.dll`
- `VmdConverterCore.RootMotion.dll`
- `VmdConverterCore.Muscles.dll`

Offline rig generation with `--anim-w` additionally requires `VmdConverterCore.Hps.dll` and `UnityHps.Core.dll`. FBX import requires `UnityHps.Fbx.dll`. Only `--anim-w` searches for HPS.

The .NET 10 and native DLLs are loaded by the external process. Their Unity PluginImporter settings are disabled. Preserve these settings when redistributing the package.

Source checkouts omit binaries. Run `scripts/Build-Release.ps1` from the repository root to assemble the complete package. Releases include the project licenses and third-party notices. See the [package README](../README.md) and [CLI guide](../Documentation/OFFLINE.md).
