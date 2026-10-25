# External converter runtime

Keep VmdConverterCore.exe, VmdConverterCore.Shared.dll, VmdConverterCore.Hps.dll, UnityHps.Core.dll and UnityHps.Fbx.dll together. Animation/curve conversion requires HPS; raw .vccpose preparation for native Unity baking does not.

These .NET10/native binaries execute in the external process. Preserve their disabled Unity PluginImporter settings. build-manifest.json records binary SHA256 digests. Run VmdConverterCore.exe --help for command-line options.

Build the complete CLI and Unity packages with scripts/Build-Release.ps1 from the source repository. See the package README for public Editor APIs and license notices in this folder.
