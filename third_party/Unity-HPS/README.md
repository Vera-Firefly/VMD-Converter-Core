# Unity-HPS

[简体中文](README.zh-CN.md)

Unity-HPS is a C# HumanPose solver and Windows x64 command-line application. It converts local skeleton poses to Humanoid RootT, RootQ and muscles, and decodes those curves back to local bone poses without loading Unity. It includes FBX rig import, calibrated response interpolation, anatomical upper projection, two-bone IK and source-guided FK branch continuation. VMD parsing and Unity `.anim` serialization belong to the caller, such as VMD Converter Core.

## CLI

```powershell
.\UnityHps.exe rig --fbx avatar.fbx --output rig.json
.\UnityHps.exe solve --rig rig.json --input poses.json --output result.json --threads 4 --passes 3 --range-policy tangent
.\UnityHps.exe decode --rig rig.json --input human-poses.json --output local-poses.json --range-policy tangent
.\UnityHps.exe --help
```

`rig` uses adjacent FBX `.meta` automatically. Override with `--meta`; supply human-bone mappings with `--bone-map`, disable metadata with `--ignore-meta true`, or override source scale with `--scale`. `--profile` selects another portable response profile. Offline FBX rigs use portable responses; an exported Unity HPS rig contains native Avatar responses.

Other commands: `fbx-info` exports skeleton/morph metadata; `inspect` describes a rig; `calibrate` fits root mass weights from pose/reference data; `profile` creates a portable response profile; `continue-fk` applies streaming source-guided FK roll continuity; `validate` compares against supplied HumanPose references and exits 2 if the requested limits fail. `demo` creates schema examples and `self-test` runs automated component checks. Each takes the paths listed by `--help`.

`solve` and `validate` accept `--threads` (0 = automatic), `--passes` (default 2), `--leg-geometry`, `--arm-context` and `--decoded-root` (boolean, default false). `solve`, `decode` and `validate` accept `--range-policy strict|tangent`. Strict is the SDK/CLI default and rejects values outside the calibration lattice. Tangent extends boundary responses with bounded extrapolation and total-twist phase evaluation. Shoulder swing channels can exceed four normalized units when their angular limits require it; the additional symmetric range is bounded by 180 degrees divided by the larger absolute angular limit. Input poses use metres, xyzw unit quaternions and the exact rig bone order.

## Managed API

Reference `UnityHps.Core.dll` from .NET10 and keep `UnityHps.Fbx.dll` beside the application when importing FBX. Namespace: `UnityHps`.

```csharp
Rig rig = HpsJson.ReadRig("rig.json");
var solver = new HumanoidSolver(rig, rangePolicy: HumanoidRangePolicy.Tangent)
{
    FitDecodedArmContext = true,
    ReconstructDecodedRoot = true
};
ResultBatch result = solver.SolveBatch(HpsJson.ReadPoses("poses.json"), 4, 3);
PoseFrame local = solver.Decode(result.frames[0].humanPose.muscles,
    result.frames[0].humanPose).ToFrame(result.frames[0].time);
```

- `OfflineRigFactory.FromFbx`, `FbxReader.Read`: offline skeleton, mappings and morphs.
- FBX and descriptor target rigs use `HumanoidResponseTransport.AnatomicalTargets`: neutral leg flexion uses horizontal Avatar heading while coupled leg responses follow their owning segment; torso frames follow the next mapped torso link; shoulder and ankle axes use gravity-stable heading. When Chest is mapped, unmapped UpperChest channels delegate to it. For an existing skeleton, select it with `OfflineRigFactory.FromSkeleton(document, profilePath, HumanoidResponseTransport.AnatomicalTargets)`. `AnatomicalLegs` retains the earlier leg-only transport. The two-argument `FromSkeleton` and `Transfer` overloads retain `JointFrames` for source-pose encoding. Serialized HPS rigs keep their supplied responses. Anatomical target shoulders use a coupled swing response when the supplied profile supports it. FBX metadata supplies asymmetric shoulder limits with zero centers; other custom limits retain profile defaults and emit a warning. Export an HPS rig for Avatar-specific calibrated responses.
- `HumanoidSolver.Solve`, `SolveBatch`, `SolveMuscles`, `Decode`: pose/curve conversion, optional active human-bone groups and initial muscles.
- `HumanoidSolver.ReconstructRoot`: root fitting after external muscle edits. `FitDecodedArmContext` fits arms under the decoded shoulder/body context; `FitLegGeometry` selects geometry-aware leg fitting. `AnchorLimbTwistToInitial` adds a soft initial twist prior.
- `HumanoidUpperProjector.Project`: anatomical source/target upper projection; returns a new muscle array and preserves unselected channels.
- `FkBranchContinuation.Process`: whole-sequence equivalent arm branches; preserves source/target arrays. `FkRollContinuation` provides the separate streaming forearm policy.
- `HumanoidBodyMassCalibration.Fit`, `RootCalibration.Fit`: root mass calibration. Use independent reference data for acceptance before applying fitted weights.
- `SkeletonPose`, `TwoBoneIk`, `AvatarDescriptorIO`, `HpsJson`: pose math, IK, FBX descriptors and serialization.

Batch solving shares immutable calibration/model data while frame state remains local. Root fitting uses the final muscle pose. Calibration coverage and target skeleton differences determine accuracy; portable FBX import and native Avatar calibration are distinct input types.

## Native Unity calibration

Copy `unity/Editor/UnityHpsCalibration.cs` into an Editor folder. In Unity, call `UnityHps.Reference.UnityHpsCalibration.Export(sourceGameObject, outputDirectory, resolution: 5, referenceFrames: 128, seed: 12345)`. It clones the Humanoid skeleton, samples `HumanPoseHandler` and writes `rig.json`, `training.json`, and `reference.json`. Run this on the Editor main thread. The exported JSON can subsequently be consumed offline; importing an FBX offline does not call Unity APIs.

## Build

Install .NET SDK10 and Visual Studio C++ x64 tools on Windows:

```powershell
./scripts/Build.ps1
# Framework-dependent SDK build:
./scripts/Build-Native.ps1
dotnet build src/UnityHps.Cli -c Release
```

The default executable uses NativeAOT. Add `-NoAot` to build a self-contained managed executable.

| Output | Path |
|---|---|
| Executable | `artifacts/publish/win-x64/UnityHps.exe` |
| Managed SDK | `artifacts/release/Unity-HPS-win-x64/managed/` |
| Unity calibration script | `unity/Editor/UnityHpsCalibration.cs` |

Upper response data is embedded in `UnityHps.Core.dll`; no external model-data directory is required.

See [offline usage](docs/OFFLINE.md), [JSON protocol](docs/PROTOCOL.md), [LICENSE](LICENSE) and [third-party notices](NOTICE).
