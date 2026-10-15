# VMD Converter Core

English | [简体中文](README.zh-CN.md)

VMD Converter Core (VMDCC) is a standalone PMX/VMD motion conversion application, a set of .NET assemblies, and a Unity integration package. It evaluates PMX bone animation and IK, converts motion to Humanoid or target-skeleton FK curves, and supports direct Unity `.anim` output. The external converter runs without Unity or VRCSDK.

## Features

- PMX/VMD parsing, PMX IK, append transforms and bone morphs.
- Humanoid IK, target-calibrated Humanoid and target-skeleton FK output.
- Morph mapping, facial VMD overrides and unmapped morph output under `Body`.
- Offline FBX/PMX rig generation through the Unity-HPS adapter.
- Parallel conversion of multiple VMD files in one process.
- Direct `.anim` output or binary pose/curve caches for host applications.
- Public .NET APIs and independent Unity Editor invocation scripts.

PMX-to-FBX export is outside the supported feature set. Standalone Humanoid solving uses calibration/response models; use a rig matching the target model and its Humanoid settings.

## Requirements and installation

The Windows x64 release is self-contained. Keep `VmdConverterCore.exe`, `VmdConverterCore.Shared.dll`, `VmdConverterCore.RootMotion.dll` and `VmdConverterCore.Muscles.dll` together.

Direct offline rig generation additionally uses `VmdConverterCore.Hps.dll` and `UnityHps.Core.dll`; FBX import requires `UnityHps.Fbx.dll`. The complete release includes these files. Unity-HPS is detected **only when `--anim-w` is supplied**. Other modes retain the VMDCC solver. If HPS is absent, direct `.anim` output requires a VMDCC `--rig` file.

For Unity integration, install the release's `com.verafirefly.vmdcc/` directory as a local package. It contains invocation scripts and external binaries under `Tools/`. Unity does not load the .NET 10 DLLs as plugins; they are loaded by the external process.

## Quick start

Run from the release directory:

```powershell
.\VmdConverterCore.exe --help
# Standard Humanoid, using a PMX source rig:
.\VmdConverterCore.exe --pmx model.pmx --vmd motion.vmd --output motion.anim --anim-w
# Target-calibrated Humanoid:
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --vmd motion.vmd --output motion-target.anim --anim-w
# Target-skeleton FK:
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-fk --vmd motion.vmd --output motion-fk.anim --anim-w
```

`humanoid-ik` uses the PMX source Humanoid skeleton. `target-humanoid` and `target-fk` use the target FBX/HPS rig; without one, they use a PMX Humanoid proxy. FK paths must match the destination skeleton.

## Batch conversion

```powershell
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --vmd motion-a.vmd --vmd motion-b.vmd --output-dir output --anim-w --threads 8 --job-threads 2 --memory-mb 256
```

For explicit output names and per-motion facial/morph settings, supply `--jobs jobs.json` instead of `--vmd`/`--output-dir`:

```json
{
  "jobs": [
    {"vmd": "motions/a.vmd", "output": "output/a.anim"},
    {"vmd": "motions/b.vmd", "output": "output/b.anim", "expressionVmd": "faces/b.vmd", "morphMap": "morphs.json"}
  ]
}
```

Paths in the manifest resolve from the process working directory. `--threads` sets the computation budget; `--job-threads` sets concurrent files. `--memory-mb` budgets estimated pose caches, rather than total process memory. Use `--job-threads 1` to reduce concurrent memory use.

## Rig generation and output formats

With the HPS adapter installed:

```powershell
# PMX source rig:
.\VmdConverterCore.exe --pmx model.pmx --anim-w --rig-only --rig-output pmx-rig.json
# FBX target rig:
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --anim-w --rig-only --rig-output avatar-rig.json
# Reuse an HPS rig:
.\VmdConverterCore.exe --pmx model.pmx --hps-rig avatar-rig.json --output-kind target-humanoid --vmd motion.vmd --output motion.anim --anim-w
```

Use `--hps-rig` for HPS rigs. Use `--rig` for VMDCC rigs exported by `VmdTargetRigExporter` in Unity. These are different formats. Regenerate the rig when the skeleton or Humanoid configuration changes.

| Mode | Output |
| --- | --- |
| No rig and no `--anim-w` | `.vccpose` bone pose cache. |
| `--rig` | `.vcccurves` target curves. |
| `--rig --nlu` | Humanoid curves using the standalone RootMotion/Muscles modules. |
| `--anim-w` | Unity 2022.3-style text `.anim`; uses HPS when available. |

Unity generates `.meta` files when importing `.anim`. Detailed parameters, morph mapping and output behavior are documented in [the CLI guide](docs/OFFLINE.md).

## Public APIs

| Component | Entry points |
| --- | --- |
| `VmdConverterCore.Shared.dll` | `PmxParser.Parse`, `VmdParser.Parse`, `VpdParser.Parse`, `PmxMotionEvaluator.Evaluate`, `VmdTargetFkCurveSolver.Solve`, `VmdTargetCurveFile.Read/Write/HashMotionSources`. |
| `VmdConverterCore.RootMotion.dll` | `VmdRootMotionSolver.Solve`. |
| `VmdConverterCore.Muscles.dll` | `VmdMuscleCurveSolver.Solve`. |
| `VmdConverterCore.Hps.dll` | `VmdHpsBackend.Create`, `PmxRigBuilder.Build/Map`. |
| `VmdConverterCore.exe` assembly | `VmdHumanoidCurveSolver.Solve/SolveNlu`. |
| Unity Editor integration | `VmdTargetRigExporter.Export/ExportJson/AttachCanonical/WriteJson`, `VmdConverterCore.PrepareBatch/ReadCurves`, `VmdStandaloneAnimConverter.BuildCommandLine/ConvertBatch/WriteMorphMap`. |

Managed APIs target .NET 10. Types use `VeraFirefly.VmdConverter.Core`; NLU solvers use its `.Nlu` namespace, and Unity integration uses `.Editor`. Keep one mutable evaluator per concurrent worker:

```csharp
using VeraFirefly.VmdConverter.Core;

PmxModel model = PmxParser.Parse("model.pmx");
VmdMotion motion = VmdParser.Parse("motion.vmd");
motion.PrepareForConcurrentSampling();
var evaluator = new PmxMotionEvaluator(model, motion);
PmxPose pose = evaluator.Evaluate(30f); // VMD frame number
```

Target curve solvers accept `VmdTargetRigData` and sampled `PmxPose[]`; sample at `rig.sampleRate`. When using System.Text.Json for the field-based rig contract, enable `IncludeFields`.

For Unity, see [the independent integration guide](examples/unity/README.md), [the C# example](examples/unity/VmdccTargetFkExample.cs) and [the integration call chain](examples/unity/VMDCW_CALL_CHAIN.md). VMDCW and VRCSDK are optional host applications/dependencies, not prerequisites for calling VMDCC.

## Build

Install .NET 10 SDK, Visual Studio C++ desktop build tools and Windows SDK:

```powershell
.\scripts\Build-Release.ps1
# Alternative output directory (must be empty):
.\scripts\Build-Release.ps1 -OutputDirectory artifacts/release-custom
# Build without the optional HPS adapter:
.\scripts\Build-Release.ps1 -WithoutUnityHps -OutputDirectory artifacts/release-basic
```

The default output is `artifacts/win-x64/`. It contains the external converter, dependencies, installable Unity package with `.cs`/`.asmdef`/`.meta` files, examples, documentation and licenses. GitHub Actions runs the same script and uploads the full directory. HPS sources are included under `third_party/Unity-HPS`; the build does not require a sibling repository. The build without HPS does not require the native FBX toolchain.

## License

VMDCC is licensed under [Apache-2.0](LICENSE). Bundled Unity-HPS uses Apache-2.0; ufbx uses its MIT license. Their notices and licenses are retained in the source snapshot and included in releases containing HPS.
