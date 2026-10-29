## Quick start

```powershell
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd dance.vmd --fbx avatar.fbx --output-kind target-humanoid-all --output dance.anim
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd a.vmd --vmd b.vmd --hps-rig rig.json --output-kind target-fk-all --output-dir output
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd dance.vmd --output dance.anim
.\VmdConverterCore.exe --help
```

The third command uses the PMX Humanoid rig. Any supplied `--fbx`, `--hps-rig` or `--descriptor` selects the target explicitly; an invalid target fails instead of falling back. FBX and HPS rig inputs are mutually exclusive. Adjacent FBX `.meta` files supply Unity bone mapping and importer settings. `--meta` overrides that file; `--bone-map` supplies an explicit mapping. All `target-*` modes require a target. An HPS rig preserves the calibration available in its JSON; offline FBX import uses portable Humanoid responses.

Portable FBX target responses are not Avatar-specific calibration. Their accuracy warning does not stop conversion. FBX metadata applies asymmetric shoulder limits with zero centers when the response profile supports coupled shoulder swing. Other custom muscle limits retain profile defaults and emit a warning; use an exported `--hps-rig` for measured Avatar responses. Invalid input data and unsupported affine bone scales remain errors.

## Output modes

| `--output-kind` | Target fitting |
|---|---|
| `humanoid-ik` | PMX source IK; an explicit rig retargets the whole Humanoid pose without target limb reconstruction |
| `target-humanoid`, `target-fk` | Normal policy: target root/body/legs, source Humanoid upper limb |
| `target-humanoid-leg`, `target-fk-leg` | Explicit normal/leg policy |
| `target-humanoid-arm`, `target-fk-arm` | Target upper limb; source Humanoid body/legs |
| `target-humanoid-all`, `target-fk-all` | Target whole body including upper-limb reconstruction |

Humanoid writes RootT, RootQ and muscle curves. FK writes local rotations and necessary translations at target skeleton paths. FK continuity chooses equivalent axial branches using source motion and preserves wrist orientation; it does not smooth muscle values by wrapping them. Target modes export a whole animation; the suffix selects the region fitted to the target. FBX and descriptor target rigs use horizontal Avatar heading for neutral leg flexion and transport coupled leg responses in the owning segment frame; ankle pitch/roll uses horizontal Avatar heading without requiring mapped Toes. Torso axes follow the next mapped torso link across an absent UpperChest; when Chest is mapped, UpperChest muscle channels still act on it. Shoulder axes use horizontal heading. Existing HPS rig JSON keeps its supplied responses.

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
