# VMD Converter Core

External PMX/VMD and IK preparation, plus mandatory HPS offline animation conversion. VMDCW and VRCSDK are not required. This package does not convert PMX geometry to FBX.

Reference `com.verafirefly.vmdcc` and `com.verafirefly.vmdcc.editor` from Editor asmdefs. Public APIs in `VeraFirefly.VmdConverter.Core` / `.Editor`:

- `VmdExternalPoseWorker.PrepareBatch`: raw PMX/IK poses for native Unity baking when no target rig is passed; no HPS required.
- `VmdStandaloneAnimConverter.ConvertBatch` / `BuildCommandLine`: EXE + HPS offline `.anim` output; caller imports assets.
- `VmdTargetRigExporter.ExportJson`: legacy static bindings for `--rig` / `.vcccurves`.
- `UnityHps.Reference.UnityHpsCalibration.Export`: native Unity Avatar calibration to HPS `rig.json`, for `--hps-rig`.
- `VmdConverterCore.PrepareBatch` / `ReadCurves`: checked HPS curve-file bridge.

```csharp
var options = new VmdStandaloneAnimOptions {
    ExecutablePath = executable, PmxPath = pmx, FbxPath = fbx,
    OutputKind = VmdExternalOutputKind.TargetFkAll
};
VmdStandaloneAnimConverter.ConvertBatch(options, new[] {
    new VmdStandaloneAnimJob { VmdPath = vmd, OutputPath = anim }
});
```

Output kinds: `humanoid-ik`, `target-humanoid`, `target-fk`, and `-all` / `-arm` / `-leg` variants for both target types. Normal target modes retain the leg policy. All fits the whole target; arm fits target upper limbs. Every mode writes a complete animation. Humanoid IK retargets the source pose when a target rig is explicit; it does not enable target limb reconstruction. Automatic leg fitting uses geometry for native compensation calibration and rotation for portable FBX rigs.

Offline curves require HPS. Keep all five binaries in Tools: EXE, Shared, Hps, UnityHps.Core and UnityHps.Fbx, with disabled Unity plugin importers. Run `--help` for arguments. Explicit FBX/HPS targets never fall back; target modes require one. Legacy `--rig` and HPS `--hps-rig` use different schemas.

Run GameObject, Avatar, HumanPoseHandler, AnimationClip and AssetDatabase operations on the Editor main thread. VMDCW normally consumes raw poses and bakes with Unity APIs; its fully managed option delegates curve solving to offline HPS.

See the [project manual](https://github.com/Vera-Firefly/VMD-Converter-Core/blob/main/README.md) for complete parameters, calling examples and builds.

`HpsCalibrationExporter.ExportBatch` also exports Unity-measured axes for transform-mode `--calibrate`; this is not an HPS rig JSON.
