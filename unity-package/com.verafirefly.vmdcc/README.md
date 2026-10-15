# VMD Converter Core for Unity

English | [简体中文](README.zh-CN.md)

This package provides public Unity Editor interfaces for the external VMD Converter Core application. It supports PMX/VMD motion conversion, Humanoid IK, target-calibrated Humanoid, target-skeleton FK and morph mapping. It can be used without VMDCW or VRCSDK.

## Installation

Install this directory as a local package in Unity 2022.3 on Windows x64. Keep the external executable and its DLLs in `Tools/`. They run in a separate process; the .NET 10 DLLs are disabled as Unity plugins.

## Editor integration

Reference `com.verafirefly.vmdcc` and `com.verafirefly.vmdcc.editor` in your Editor assembly definition. For dedicated NLU calls, also reference `com.verafirefly.vmdcc.nlu.editor`.

```csharp
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;

var options = new VmdStandaloneAnimOptions {
    ExecutablePath = exePath,
    PmxPath = pmxPath,
    FbxPath = VmdSourceFbxUtility.FindSourceFbx(targetGameObject),
    OutputKind = VmdExternalOutputKind.TargetCalibratedHumanoid,
    Threads = 8
};
var jobs = new[] {
    new VmdStandaloneAnimJob { VmdPath = vmdPath, OutputPath = physicalAnimPath }
};
VmdStandaloneAnimConverter.ConvertBatch(options, jobs);
```

Use absolute physical file paths. Import outputs under Assets with `AssetDatabase.ImportAsset`. `BuildCommandLine(options, job)` returns a single-line command; `WriteMorphMap` writes custom morph mappings. Set `MorphMapPath` and `ExpressionVmdPath` on individual jobs. Unmapped morphs use `Body`.

The bridge passes `--anim-w`. Offline rig generation requires `VmdConverterCore.Hps.dll` and `UnityHps.Core.dll`; FBX reading additionally requires `UnityHps.Fbx.dll`. HPS is detected only with `--anim-w`. Without HPS, the worker requires a VMDCC-format `--rig` for direct `.anim` output.

## Curve-cache integration

Use `VmdTargetRigExporter.Export/ExportJson/AttachCanonical/WriteJson` to export the target rig. Submit a batch with `VmdConverterCore.PrepareBatch`, read it with `ReadCurves`, then create clips with Unity Editor APIs. `VmdNluBatchPipeline.PrepareBatch` provides standalone Humanoid RootMotion/Muscles processing.

HPS rigs use `--hps-rig`, while Unity-exported VMDCC rigs use `--rig`. Scene changes are not automatically reflected in source FBX data. Use calibration matching the target Humanoid configuration.

See [the CLI guide](Documentation/OFFLINE.md) and [external binary requirements](Tools/README.md). This package uses [Apache-2.0](LICENSE).
