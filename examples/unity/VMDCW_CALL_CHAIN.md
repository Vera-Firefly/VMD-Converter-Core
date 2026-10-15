# Integration call chain

The same public interfaces can be used by VMDCW or any independent Unity Editor host.

## Curve-cache integration

1. Export a target rig with `VmdTargetRigExporter.Export` or `ExportJson`.
2. For Humanoid curves, attach the canonical PMX rest-pose calibration with `AttachCanonical`, then save with `WriteJson`.
3. Submit motions through `VmdConverterCore.PrepareBatch`; select `noUnity: true` for standalone Humanoid curve solving.
4. Read each output using `VmdExternalPoseBatch.GetPosePath` and `VmdConverterCore.ReadCurves`.
5. Create and save AnimationClip assets using Unity's Editor APIs. Dispose the batch to release temporary files.

The [target FK example](VmdccTargetFkExample.cs) implements this path. `VmdNluBatchPipeline.PrepareBatch` provides a dedicated entry point for the standalone RootMotion/Muscles modules.

## Direct .anim integration

1. Collect PMX/VMD paths and the target FBX or HPS rig. `VmdSourceFbxUtility.FindSourceFbx` resolves a selected GameObject to its source file.
2. Build `VmdStandaloneAnimOptions` and `VmdStandaloneAnimJob` entries.
3. Call `VmdStandaloneAnimConverter.ConvertBatch` once for the batch. The bridge passes `--anim-w` and the worker performs pose solving and `.anim` serialization.
4. Import completed files under Assets with `AssetDatabase.ImportAsset`.

The worker loads Unity-HPS only in the `--anim-w` path. Direct conversion uses source-file skeleton data; export changes explicitly when the scene skeleton differs from that source.

## Source locations

| Role | Source |
| --- | --- |
| Target rig export | [VmdTargetRigExporter.cs](../../unity-package/com.verafirefly.vmdcc/Editor/VmdTargetRigExporter.cs) |
| Humanoid calibration | [VmdHumanoidCalibrationExporter.cs](../../unity-package/com.verafirefly.vmdcc/Editor/VmdHumanoidCalibrationExporter.cs) |
| Batch invocation | [VmdConverterCoreBridge.cs](../../unity-package/com.verafirefly.vmdcc/Editor/VmdConverterCoreBridge.cs) |
| Pose worker | [VmdExternalPoseWorker.cs](../../unity-package/com.verafirefly.vmdcc/Editor/VmdExternalPoseWorker.cs) |
| NLU integration | [VmdNluBatchPipeline.cs](../../unity-package/com.verafirefly.vmdcc/Editor.Nlu/VmdNluBatchPipeline.cs) |
| Direct .anim invocation | [VmdStandaloneAnimConverter.cs](../../unity-package/com.verafirefly.vmdcc/Editor/VmdStandaloneAnimConverter.cs) |
| FBX path resolution | [VmdSourceFbxUtility.cs](../../unity-package/com.verafirefly.vmdcc/Editor/VmdSourceFbxUtility.cs) |
| Curve data contracts | [VmdTargetCurveData.cs](../../unity-package/com.verafirefly.vmdcc/Runtime/VmdTargetCurveData.cs) |
