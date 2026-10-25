# Calling VMDCC from Unity

Install `com.verafirefly.vmdcc`. Its public Editor APIs do not require VMDCW or VRCSDK. Referencing assemblies add `com.verafirefly.vmdcc` and `com.verafirefly.vmdcc.editor` to their asmdef references.

## Offline animation

```csharp
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;
var options = new VmdStandaloneAnimOptions {
    ExecutablePath = executable, PmxPath = sourcePmx,
    FbxPath = targetFbx, OutputKind = VmdExternalOutputKind.TargetFkAll
};
var jobs = new[] { new VmdStandaloneAnimJob {
    VmdPath = sourceVmd, OutputPath = outputAnim
} };
string command = VmdStandaloneAnimConverter.BuildCommandLine(options, jobs[0]);
VmdStandaloneAnimConverter.ConvertBatch(options, jobs);
// outputAssetPath must be the output's Assets-relative path.
UnityEditor.AssetDatabase.ImportAsset(outputAssetPath);
```

Pass an explicit HPS rig using `HpsRigPath` instead of `FbxPath`. Options also include sampling, coordinates, reduction tolerances, passes, range/leg policies and FK continuity. Paths are physical paths except AssetDatabase paths. Validate overwrite policy and create output directories in the caller. A batch starts one EXE process and sends the complete manifest. HPS is mandatory for `.anim` and `.vcccurves`.

## GameObject calibration

```csharp
UnityHps.Reference.UnityHpsCalibration.Export(targetGameObject, calibrationDirectory,
    resolution: 5, referenceFrames: 128, seed: 12345);
options.FbxPath = null;
options.HpsRigPath = System.IO.Path.Combine(calibrationDirectory, "rig.json");
```

Export uses Unity `HumanPoseHandler` and must run on the Editor main thread. Offline FBX import cannot read scene-only skeleton changes. The exported rig can be reused offline for this skeleton and Avatar configuration.

`VmdTargetRigExporter.ExportJson` is a different, legacy static binding contract for `--rig`. [VmdccTargetFkExample.cs](VmdccTargetFkExample.cs) shows that curve-file API and `AnimationUtility.SetEditorCurves`; it requires HPS for the external solve. Do not pass its JSON as `--hps-rig`.

## Native Unity solving

`VmdExternalPoseWorker.PrepareBatch(executable, pmx, vmdPaths, sampleRate, threads)` without a target rig produces raw `.vccpose` data. Keep the returned disposable batch alive while consuming its files. This preprocessing has no HPS dependency. The caller's Unity baker then reads the source poses and obtains HumanPose through Unity APIs. VMDCC itself does not ship VMDCW's built-in solver.

With optional VMDCW installed, its public `VmdBatchConversionSession.ConvertPreparedPoseToClip` consumes these files using `VmdConversionSettings` and the requested `VmdClipOutput`; `BakeOptions.TargetSolveScope` and `FkOptions.TargetSolveScope` select Normal/All/Arm/Leg. Native HumanPose/Transform baking and clip creation remain on the Editor main thread. PMX preparation may run in worker threads. See [VMDCW call chain](VMDCW_CALL_CHAIN.md).
