# VMD Converter Core

[简体中文](README.zh-CN.md)

VMDCC is a Windows x64 PMX/VMD conversion worker and a Unity integration package. It evaluates PMX bones and IK, transports poses to a selected Humanoid skeleton, and writes Humanoid or target-bone FK animation. It does not convert PMX geometry to FBX and does not require VRCSDK.

Offline animation and curve output requires **Unity-HPS**. Keep `VmdConverterCore.exe`, `VmdConverterCore.Shared.dll`, `VmdConverterCore.Hps.dll`, `UnityHps.Core.dll` and `UnityHps.Fbx.dll` together. Raw `.vccpose` preprocessing for Unity does not require HPS.

## Quick start

```powershell
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd dance.vmd --fbx avatar.fbx --output-kind target-humanoid-all --output dance.anim
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd a.vmd --vmd b.vmd --hps-rig rig.json --output-kind target-fk-all --output-dir output
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd dance.vmd --output dance.anim
.\VmdConverterCore.exe --help
```

The third command uses the PMX Humanoid rig. Any supplied `--fbx`, `--hps-rig` or `--descriptor` selects the target explicitly; an invalid target fails instead of falling back. FBX and HPS rig inputs are mutually exclusive. Adjacent FBX `.meta` files supply Unity bone mapping and importer settings. `--meta` overrides that file; `--bone-map` supplies an explicit mapping. All `target-*` modes require a target. An HPS rig preserves the calibration available in its JSON; offline FBX import uses portable Humanoid responses.

## Output modes

| `--output-kind` | Target fitting |
|---|---|
| `humanoid-ik` | PMX source IK; an explicit rig retargets the whole Humanoid pose without target limb reconstruction |
| `target-humanoid`, `target-fk` | Normal policy: target root/body/legs, source Humanoid upper limb |
| `target-humanoid-leg`, `target-fk-leg` | Explicit normal/leg policy |
| `target-humanoid-arm`, `target-fk-arm` | Target upper limb; source Humanoid body/legs |
| `target-humanoid-all`, `target-fk-all` | Target whole body including upper-limb reconstruction |

Humanoid writes RootT, RootQ and muscle curves. FK writes local rotations and necessary translations at target skeleton paths. FK continuity chooses equivalent axial branches using source motion and preserves wrist orientation; it does not smooth muscle values by wrapping them. Target modes export a whole animation; the suffix selects the region fitted to the target.

## Processing

PMX/VMD evaluation and PMX IK → source Humanoid encoding → decode on the selected target → merge the selected target region → HPS upper projection and selected-region fitting → reconstruct root from the final muscles → reduce and write curves. FK additionally applies source-guided branch continuation. Body and shoulder context is fitted before dependent arm groups.

Files share the parsed model, rig and response data. `--threads` controls the total frame-worker budget, `--job-threads` controls concurrent files, and `--memory-mb` limits estimated pose-cache reservations. The cache budget is not a process RSS cap.

## Parameters

| Option | Meaning / default |
|---|---|
| `--pmx`, `--vmd`, `--output` | Model, motion and single output |
| `--output-dir` | Output directory for repeated `--vmd`; basenames must be unique |
| `--jobs` | JSON job manifest; paths resolve from the process working directory |
| `--anim-w` | Offline Unity YAML `.anim` output; requires HPS |
| `--rig` | Legacy Unity binding JSON for `.vcccurves`; requires HPS, separate from `--hps-rig` |
| `--nlu` | Compatibility curve-output alias; does not select another solver |
| `--hps-dir` | Adapter/SDK folder, default executable folder |
| `--fbx`, `--hps-rig`, `--descriptor` | Target inputs described above |
| `--profile` | Portable HPS response profile override |
| `--rig-output`, `--rig-only` | Save selected/generated HPS rig; rig-only also requires `--anim-w` |
| `--descriptor-output` | Export an FBX descriptor and exit |
| `--sample-rate`, `--position-scale`, `--coordinate` | 30 fps, 0.08 metres/PMX unit, 2 (Y180) |
| `--coordinate` | 0: flip Z; 1: flip X; 2: Y180; 3: identity |
| `--passes`, `--range-policy` | 3 fitting passes; `tangent` or `strict` |
| `--leg-fit` | `auto`/`geometry`: target knee and foot directions plus foot orientation; `rotation`: local-rotation fitting |
| `--fk-continuity`, `--target-leg-ik` | true / false |
| `--curve-error` | Muscle reduction tolerance 0.00001; zero keeps all keys |
| `--rotation-error`, `--position-error` | FK reduction: 0.01 degrees / 0.00005 metres |
| `--threads`, `--job-threads`, `--memory-mb` | CPU count minus one; min(2,jobs,threads); 256 MiB |
| `--bake-morphs`, `--expression-vmd`, `--morph-map` | true; separate facial VMD; explicit morph bindings |
| `--duration` | Optional positive duration limit in seconds |
| `--anim-mode transform` | Full FBX Transform path, plain `target-fk` only |
| `--structural-bones`, `--coverage-report` | Transform helper mapping and mapping report |
| `--calibrate`, `--residual` | Optional transform-mode measured axes and bone correction |
| `--diagnostics-dir` | Optional pipeline pose/rig output directory |

Tangent evaluation extends boundary responses within bounded values; strict evaluation rejects values outside the sampled range. Calibration coverage determines usable accuracy.

```json
{"jobs":[{"vmd":"a.vmd","output":"out/a.anim"},{"vmd":"b.vmd","output":"out/b.anim","expressionVmd":"face.vmd","morphMap":"morphs.json"}]}
```

Morph map: `{"mappings":[{"source":"smile","name":"Happy","path":"Body"}]}`. Unmapped morphs keep their source name and use `Body`.

## Unity API and packages

Install `unity-package/com.verafirefly.vmdcc`; install the optional `com.verafirefly.vmdcw` package for its conversion window and built-in parser. Other Editor scripts can call:

- `VmdExternalPoseWorker.PrepareBatch`: raw parallel PMX/IK pose files for native Unity baking.
- `VmdStandaloneAnimConverter.ConvertBatch` / `BuildCommandLine`: offline HPS `.anim` conversion and reproducible arguments.
- `HpsCalibrationExporter.ExportBatch`: Unity measured-axis export for transform-mode `--calibrate`; distinct from HPS rig JSON.
- `VmdTargetRigExporter.Export` / `WriteJson`: legacy GameObject binding contract for curve import.
- `UnityHps.Reference.UnityHpsCalibration.Export(GameObject, directory, resolution, referenceFrames, seed)`: native Unity calibration to HPS `rig.json`.
- `VmdConverterCore.PrepareBatch` / `ReadCurves`: HPS `.vcccurves` bridge and checked curve reading.

Namespaces are `VeraFirefly.VmdConverter.Core` and `.Editor`, except the calibration exporter noted above. Run Unity scene/Avatar APIs on the Editor main thread. The .NET 10 DLLs in `Tools` execute outside Unity and have disabled Unity plugin importers.

For .NET10 hosts, reference Shared, Hps and UnityHps.Core, then instantiate `VeraFirefly.VmdConverter.Core.Hps.VmdHpsBackend` through `IVmdHumanoidBackend`. `Create(PmxModel, VmdHumanoidBackendOptions, legacyRig)` returns `IVmdHumanoidSession`; `Solve(VmdMotion, PmxPose[], threads, scheduler)` returns `VmdTargetCurveData`, and `SaveRig` exports its selected rig. Callers supply independently evaluated PMX pose snapshots at the configured sample rate. Use the CLI for `.anim` serialization. These .NET10 assemblies are not loaded into Unity; Editor scripts use the process bridge above.

VMDCW with Core selected normally runs **EXE raw PMX preprocessing → Unity `HumanPoseHandler` → AnimationClip/AssetDatabase**. Target FK is also baked on the Unity target skeleton. The fully managed option instead runs **EXE + HPS → YAML `.anim` → Unity import**. Unity must still select/read the scene object and import the result. The built-in parser evaluates PMX in-process and uses the native Unity bakers. See [Unity integration examples](examples/unity/README.md).

## Build

Use .NET SDK 10 and Visual Studio C++ x64 build tools on Windows. Run:

```powershell
./scripts/Build-Release.ps1 -OutputDirectory artifacts/win-x64
```

Use an empty output directory. The executable, runtime libraries and Unity packages are written to `artifacts/win-x64`. Rebuild the HPS adapter after changing its sources or response resources under `third_party/Unity-HPS`. See [build instructions](docs/BUILD.md).

## License

Apache-2.0; see [LICENSE](LICENSE).
