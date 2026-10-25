# VMD Converter Core

[English](README.md)

VMDCC 是 Windows x64 PMX/VMD 转换程序及 Unity 接入包，负责 PMX 骨骼与 IK 求解、Humanoid 姿态转换、目标骨架 FK 曲线及动画输出。不提供 PMX 几何模型转 FBX，不依赖 VRCSDK。

**离线动画及曲线输出必须使用 Unity-HPS。** 以下文件需放在同一目录：`VmdConverterCore.exe`、`VmdConverterCore.Shared.dll`、`VmdConverterCore.Hps.dll`、`UnityHps.Core.dll`、`UnityHps.Fbx.dll`。供 Unity 原生烘焙使用的原始 `.vccpose` 预处理不需要 HPS。

## 使用

```powershell
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd dance.vmd --fbx avatar.fbx --output-kind target-humanoid-all --output dance.anim
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd a.vmd --vmd b.vmd --hps-rig rig.json --output-kind target-fk-all --output-dir output
.\VmdConverterCore.exe --anim-w --pmx model.pmx --vmd dance.vmd --output dance.anim
.\VmdConverterCore.exe --help
```

第三条命令仅使用 PMX Humanoid rig。明确填写 `--fbx`、`--hps-rig` 或 `--descriptor` 后必须使用该目标，错误输入直接失败，不回退到 PMX。FBX 与 HPS rig 互斥；FBX 相邻 `.meta` 提供骨骼映射及导入设置，`--meta` 可覆盖，`--bone-map` 可另给映射。所有 `target-*` 模式必须有目标。HPS rig 保留 JSON 内的标定数据；纯离线 FBX 采用可移植 Humanoid 响应。

## 模式

| `--output-kind` | 目标求解范围 |
|---|---|
| `humanoid-ik` | 无目标时使用 PMX；有明确目标时对整身目标姿态拟合 |
| `target-humanoid`、`target-fk` | 正常模式：目标根运动、躯干、下肢，保留源 Humanoid 上肢 |
| `target-humanoid-leg`、`target-fk-leg` | 显式采用正常／下肢策略 |
| `target-humanoid-arm`、`target-fk-arm` | 目标上肢；保留源 Humanoid 躯干和下肢 |
| `target-humanoid-all`、`target-fk-all` | 目标全身，包括目标上肢反解 |

Humanoid 输出 RootT、RootQ、肌肉曲线；FK 按目标路径输出局部旋转及必要的位移。模式后缀决定目标拟合区域，输出仍为整段全身动作。FK 连续性处理参考源动作选择等效轴向分支，保持手腕方向，不通过肌肉标量取模消除跳变。

## 求解流程

PMX/VMD 解析及 PMX IK → 源 Humanoid 编码 → 选定目标 rig 解码 → 合并所选目标区域 → HPS 上肢投影及区域拟合 → 依据最终肌肉重建根运动 → 曲线压缩及输出。FK 再进行参考源动作的分支连续性处理。依赖链先拟合躯干、肩，再拟合手臂。

批量共用解析后的模型、rig 和响应数据；总帧线程受 `--threads` 限制，文件并发由 `--job-threads` 控制，`--memory-mb` 是姿态缓存估算预算，不是进程总内存上限。

## 参数

| 参数 | 用途／默认值 |
|---|---|
| `--pmx`、`--vmd`、`--output` | 源模型、动作、单文件输出 |
| `--output-dir`、`--jobs` | 重复 `--vmd` 的输出目录；JSON 批任务，路径相对进程工作目录 |
| `--anim-w` | 离线写出 Unity YAML `.anim`，必须有 HPS |
| `--rig` | Unity 导出的旧版绑定 JSON，用于 `.vcccurves`，必须有 HPS；与 `--hps-rig` 不同 |
| `--nlu` | 兼容曲线调用参数，不再选择另一套求解器 |
| `--hps-dir` | HPS 适配程序集目录，默认 EXE 所在目录 |
| `--fbx`、`--hps-rig`、`--descriptor` | 明确指定目标，边界见上文 |
| `--meta`、`--bone-map`、`--profile` | FBX 导入信息、骨骼映射、可移植响应配置 |
| `--rig-output`、`--rig-only` | 保存 rig；仅生成 rig，仍需 `--anim-w` |
| `--descriptor-output` | 从 FBX 导出描述并退出，不添加 VMD 任务 |
| `--sample-rate`、`--position-scale` | 30 fps；0.08 米/PMX单位 |
| `--coordinate` | 默认 2；0 翻转 Z，1 翻转 X，2 绕 Y180，3 不转换 |
| `--passes`、`--range-policy` | 默认 3；`tangent` 或 `strict` |
| `--leg-fit` | `auto`：原生位移补偿标定用几何拟合，纯 FBX 用旋转拟合；可指定 `geometry`／`rotation` |
| `--fk-continuity`、`--target-leg-ik` | 默认 true／false |
| `--curve-error` | 肌肉压缩误差 0.00001，0 保留全部关键帧 |
| `--rotation-error`、`--position-error` | FK 压缩误差：0.01 度、0.00005 米 |
| `--threads`、`--job-threads`、`--memory-mb` | CPU 数减一；min(2,任务数,线程数)；256 MiB |
| `--bake-morphs`、`--expression-vmd`、`--morph-map` | 默认 true；表情 VMD；形态键映射 |
| `--duration` | 限定正数秒数 |
| `--anim-mode transform` | 完整 FBX Transform 输出，仅普通 `target-fk` |
| `--structural-bones`、`--coverage-report` | Transform 辅助骨映射与覆盖报告 |
| `--calibrate`、`--residual` | Transform 模式的测量轴与逐骨修正 |
| `--diagnostics-dir` | 可选的中间姿态与 rig 输出目录 |

`tangent` 在有限范围内延拓边界响应；`strict` 拒绝超出采样范围的值。精度受标定覆盖范围影响。

```json
{"jobs":[{"vmd":"a.vmd","output":"out/a.anim"},{"vmd":"b.vmd","output":"out/b.anim","expressionVmd":"face.vmd","morphMap":"morphs.json"}]}
```

映射 JSON：`{"mappings":[{"source":"smile","name":"Happy","path":"Body"}]}`。未映射形态键按源名称写入 `Body`。

## Unity 公开接口与调用链

安装 `unity-package/com.verafirefly.vmdcc`。可另装 `com.verafirefly.vmdcw` 获得窗口及内置解析。任意 Editor 脚本可调用：

- `VmdExternalPoseWorker.PrepareBatch`：EXE 并行生成原始 PMX/IK 姿态，供 Unity 原生烘焙。
- `VmdStandaloneAnimConverter.ConvertBatch`／`BuildCommandLine`：离线 HPS `.anim` 与对应命令。
- `HpsCalibrationExporter.ExportBatch`：通过 Unity 导出 Transform 模式 `--calibrate` 使用的测量轴描述，与 HPS rig JSON 不同。
- `VmdTargetRigExporter.Export`／`WriteJson`：GameObject 的旧版绑定参数。
- `UnityHps.Reference.UnityHpsCalibration.Export(GameObject, directory, resolution, referenceFrames, seed)`：通过 Unity API 导出 HPS `rig.json` 标定。
- `VmdConverterCore.PrepareBatch`／`ReadCurves`：HPS 曲线文件调用与校验读取。

除标定导出器外，命名空间为 `VeraFirefly.VmdConverter.Core` 及 `.Editor`。Unity 场景、Avatar 操作必须在 Editor 主线程运行。Tools 内 .NET10 DLL 的 Unity 插件加载已禁用，由外部进程使用。

.NET10 宿主可引用 Shared、Hps、UnityHps.Core，通过 `IVmdHumanoidBackend` 实例化 `VeraFirefly.VmdConverter.Core.Hps.VmdHpsBackend`。`Create(PmxModel, VmdHumanoidBackendOptions, legacyRig)` 返回 `IVmdHumanoidSession`；`Solve(VmdMotion, PmxPose[], threads, scheduler)` 返回 `VmdTargetCurveData`，`SaveRig` 导出当前 rig。调用者按配置采样率提供独立的 PMX 姿态快照；写 `.anim` 使用 CLI。这些 .NET10 程序集不加载到 Unity，Editor 脚本使用前述进程桥。

VMDCW 选择 Core 时默认调用链为 **EXE 原始 PMX 预处理 → Unity `HumanPoseHandler` → AnimationClip／AssetDatabase**；目标 FK 同样在 Unity 目标骨架上烘焙。勾选全部托管后才使用 **EXE＋HPS → YAML `.anim` → Unity 导入**；场景目标选择、读取及资源导入仍在 Unity 中进行。内置解析在进程内评估 PMX，随后使用 Unity 原生烘焙器。详见 [Unity 调用示例](examples/unity/README.zh-CN.md)。

## 构建

Windows 安装 .NET SDK10 与 Visual Studio C++ x64 构建工具，运行：

```powershell
./scripts/Build-Release.ps1 -OutputDirectory artifacts/win-x64
```

输出目录必须为空。可执行文件、运行库与 Unity 包位于 `artifacts/win-x64`。修改 `third_party/Unity-HPS` 下的源码或响应资源后，应重新构建 HPS 适配程序集。详见 [构建说明](docs/BUILD.md)。

## 协议

Apache-2.0，见 [LICENSE](LICENSE)。
