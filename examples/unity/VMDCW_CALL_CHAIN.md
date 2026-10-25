# VMDCW conversion call chain

## Built-in parser

`VmdConverterPage.ConvertManagedMotionsParallel` → `ManagedBatchSession.Prepare` → PMX evaluation workers → main-thread `Bake`, `BakeTargetHumanoid` or `BakeTargetFk` → Unity HumanPoseHandler / Transform baking → `AnimationUtility` / `AssetDatabase`.

## Core parser with native Unity curves (default)

`ConvertDirectMotions` → `ConvertManagedMotionsParallel(forceExternalPose: true)` → `VmdExternalPoseWorker.PrepareBatch` without `--rig` or `--anim-w` → EXE `.vccpose` → `ManagedBatchSession.LoadPreparedPose` (hash, shape and PMX parity checks) → the same native Unity bakers. Invalid/unmatched caches are reevaluated with the built-in PMX evaluator. Target FK also follows this path. PMX settings' conversion button uses `VmdBatchConversionSession.ConvertPreparedPoseToClip`.

## Core fully managed offline curves

`ConvertStandaloneMotions` → `VmdStandaloneAnimConverter.ConvertBatch` → EXE `--anim-w` with selected FBX/HPS rig, output kind and all settings → HPS calibrated pose pipeline → YAML `.anim` → Unity AssetDatabase import. The window reads scene selection and mapping on the main thread, but does not bake curves using Unity APIs in this branch.

VMDCW's optional package-dispatch facade passes parameters to VMDCC's public Editor APIs by reflection. The main window assembly has no mandatory Core reference; the typed legacy curve bridge lives in a separately constrained integration assembly. .NET10 solver DLLs run in the external process.

## Unity API responsibilities

GameObject/Animator/Avatar access, `HumanPoseHandler`, native calibration export, AnimationClip creation, `AnimationUtility` and `AssetDatabase` require Unity's Editor/runtime bindings and lifecycle. They cannot safely be called by the standalone .NET10 worker. Offline HPS reconstructs these curve conversions numerically; it does not load Unity assemblies. PMX parsing, IK, numerical retargeting, curve reduction and YAML serialization execute outside Unity.
