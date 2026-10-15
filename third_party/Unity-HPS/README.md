# Unity-HPS SDK source

This directory contains the Unity-HPS managed SDK, embedded response profiles and native FBX reader used by the VMDCC HPS adapter. It targets .NET 10 and is loaded by the external converter.

VMDCC builds this SDK from the included source. Use `scripts/Sync-HpsSdk.ps1 -SourceDirectory <Unity-HPS repository>` from the VMDCC repository root to update the snapshot. The native reader is built automatically by `scripts/Build-Release.ps1`.

Unity-HPS is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE). The ufbx reader uses the MIT alternative in [third_party/ufbx/LICENSE](third_party/ufbx/LICENSE), with its version recorded in [VERSION.md](third_party/ufbx/VERSION.md).
