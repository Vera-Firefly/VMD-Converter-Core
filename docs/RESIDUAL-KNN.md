# Transform corrections

`--anim-mode transform --output-kind target-fk` supports optional per-bone correction through `--residual model.json`. The model must use the same rig, bone mappings and coordinate convention as its input. `--calibrate descriptor.json` supplies measured muscle axes for this transform path. These options do not apply to the calibrated Humanoid/region pipeline.

The model schema and evaluation are implemented by `src/Modules/Hps/ResidualModel.cs`; the measured-axis reader is `CalibratedMuscles.cs`. Pass a valid explicit file; invalid files fail rather than selecting another target or algorithm.

## Export measured axes in Unity

Install the Core Unity package and run `Tools/VMDCC/Export HPS Calibration` with a Humanoid root selected. For batch mode, set `VMDCC_CALIB_PREFAB` to an Assets-relative prefab/FBX path and `VMDCC_CALIB_OUT` to the output JSON path, then launch Unity with `-batchmode -projectPath <project> -executeMethod VeraFirefly.VmdConverter.Core.Editor.HpsCalibrationExporter.ExportBatch -quit`. Alternatively, provide project-root `calibration_args.json` containing `{"prefab":"Assets/avatar.prefab","output":"C:/output/axes.json"}`.

This operation uses Unity APIs and requires a licensed Editor. Its measured-axis JSON is consumed by `--anim-mode transform --calibrate`; use `UnityHps.Reference.UnityHpsCalibration.Export` to create HPS lattice rig JSON instead.
