# VMD Converter Core

[English](README.md) | 简体中文

VMD Converter Core（VMDCC）提供独立的 PMX/VMD 动作转换程序、.NET 程序集及 Unity 集成包。它负责 PMX 骨骼动作与 IK 求解，将动作转换为 Humanoid 或目标骨架 FK 曲线，并支持直接输出 Unity `.anim`。外部转换程序无需 Unity 或 VRCSDK。

## 功能

- PMX/VMD 解析、PMX IK、追加变换和骨骼形态键。
- Humanoid IK、目标校准 Humanoid 和目标骨架 FK 输出。
- 形态键映射、表情 VMD 覆盖，以及未映射形态键的 `Body` 路径输出。
- 通过 Unity-HPS 适配器离线生成 FBX/PMX rig。
- 在一次进程调用中并行转换多个 VMD。
- 直接输出 `.anim`，或输出供调用方读取的姿态、曲线二进制缓存。
- 提供公开 .NET 接口及可独立使用的 Unity Editor 调用脚本。

当前功能不包含 PMX 转 FBX。独立 Humanoid 求解采用校准或响应模型；应使用与目标模型及其 Humanoid 设置相匹配的 rig。

## 运行要求与安装

Windows x64 发布包为自包含程序。须将 `VmdConverterCore.exe`、`VmdConverterCore.Shared.dll`、`VmdConverterCore.RootMotion.dll` 和 `VmdConverterCore.Muscles.dll` 放在同一目录。

离线 rig 生成另需 `VmdConverterCore.Hps.dll` 和 `UnityHps.Core.dll`，读取 FBX 还需 `UnityHps.Fbx.dll`。完整发布包已包含这些文件。**只有传入 `--anim-w` 才查找 Unity-HPS**，其他模式沿用 VMDCC 求解器。没有 HPS 时，直接输出 `.anim` 需要 VMDCC 格式的 `--rig` 文件。

Unity 集成时，将发布包中的 `com.verafirefly.vmdcc/` 安装为本地包。包内包含调用脚本，外部二进制位于 `Tools/`。这些 .NET 10 DLL 的 Unity 插件加载已禁用，由外部进程加载。

## 快速开始

在发布目录执行：

```powershell
.\VmdConverterCore.exe --help
# 标准 Humanoid，使用 PMX 源 rig：
.\VmdConverterCore.exe --pmx model.pmx --vmd motion.vmd --output motion.anim --anim-w
# 目标校准 Humanoid：
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --vmd motion.vmd --output motion-target.anim --anim-w
# 目标骨架 FK：
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-fk --vmd motion.vmd --output motion-fk.anim --anim-w
```

`humanoid-ik` 使用 PMX 源人体骨架。`target-humanoid` 与 `target-fk` 使用目标 FBX/HPS rig，未指定目标时使用 PMX 人体代理骨架。FK 路径须与动画使用方的骨架一致。

## 批量转换

```powershell
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --vmd motion-a.vmd --vmd motion-b.vmd --output-dir output --anim-w --threads 8 --job-threads 2 --memory-mb 256
```

需要指定输出名称、逐动作表情或形态键映射时，以 `--jobs jobs.json` 替代 `--vmd`/`--output-dir`：

```json
{
  "jobs": [
    {"vmd": "motions/a.vmd", "output": "output/a.anim"},
    {"vmd": "motions/b.vmd", "output": "output/b.anim", "expressionVmd": "faces/b.vmd", "morphMap": "morphs.json"}
  ]
}
```

清单中的相对路径以进程当前工作目录为基准。`--threads` 设置计算线程预算，`--job-threads` 设置并发文件数；`--memory-mb` 约束估算的姿态缓存，不代表进程总内存上限。可用 `--job-threads 1` 减少并发内存占用。

## rig 生成与输出格式

安装 HPS 适配器后可使用：

```powershell
# PMX 源 rig：
.\VmdConverterCore.exe --pmx model.pmx --anim-w --rig-only --rig-output pmx-rig.json
# FBX 目标 rig：
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --anim-w --rig-only --rig-output avatar-rig.json
# 复用 HPS rig：
.\VmdConverterCore.exe --pmx model.pmx --hps-rig avatar-rig.json --output-kind target-humanoid --vmd motion.vmd --output motion.anim --anim-w
```

HPS rig 使用 `--hps-rig`；由 Unity 中 `VmdTargetRigExporter` 导出的 VMDCC rig 使用 `--rig`。两者格式不同。骨架或 Humanoid 配置变更后应重新生成 rig。

| 模式 | 输出 |
| --- | --- |
| 不传 rig，也不传 `--anim-w` | `.vccpose` 骨骼姿态缓存。 |
| `--rig` | `.vcccurves` 目标曲线。 |
| `--rig --nlu` | 通过独立 RootMotion/Muscles 模块生成 Humanoid 曲线。 |
| `--anim-w` | Unity 2022.3 格式的文本 `.anim`；有 HPS 时使用 HPS。 |

Unity 导入 `.anim` 时生成 `.meta`。完整参数、形态键映射及输出行为见[命令行使用指南](docs/OFFLINE.md)。

## 公开接口

| 组件 | 调用入口 |
| --- | --- |
| `VmdConverterCore.Shared.dll` | `PmxParser.Parse`、`VmdParser.Parse`、`VpdParser.Parse`、`PmxMotionEvaluator.Evaluate`、`VmdTargetFkCurveSolver.Solve`、`VmdTargetCurveFile.Read/Write/HashMotionSources`。 |
| `VmdConverterCore.RootMotion.dll` | `VmdRootMotionSolver.Solve`。 |
| `VmdConverterCore.Muscles.dll` | `VmdMuscleCurveSolver.Solve`。 |
| `VmdConverterCore.Hps.dll` | `VmdHpsBackend.Create`、`PmxRigBuilder.Build/Map`。 |
| `VmdConverterCore.exe` 程序集 | `VmdHumanoidCurveSolver.Solve/SolveNlu`。 |
| Unity Editor 集成 | `VmdTargetRigExporter.Export/ExportJson/AttachCanonical/WriteJson`、`VmdConverterCore.PrepareBatch/ReadCurves`、`VmdStandaloneAnimConverter.BuildCommandLine/ConvertBatch/WriteMorphMap`。 |

托管接口面向 .NET 10。类型位于 `VeraFirefly.VmdConverter.Core`，NLU 求解器使用其 `.Nlu` 命名空间，Unity 集成使用 `.Editor`。每个并发工作单元须独立持有可变求值器：

```csharp
using VeraFirefly.VmdConverter.Core;

PmxModel model = PmxParser.Parse("model.pmx");
VmdMotion motion = VmdParser.Parse("motion.vmd");
motion.PrepareForConcurrentSampling();
var evaluator = new PmxMotionEvaluator(model, motion);
PmxPose pose = evaluator.Evaluate(30f); // VMD 帧编号
```

目标曲线求解器接收 `VmdTargetRigData` 和按 `rig.sampleRate` 采样的 `PmxPose[]`。使用 System.Text.Json 读取以字段定义的 rig 时，须启用 `IncludeFields`。

Unity 接入见[独立调用指南](examples/unity/README.md)、[C# 示例](examples/unity/VmdccTargetFkExample.cs)和[集成调用链](examples/unity/VMDCW_CALL_CHAIN.md)。调用 VMDCC 不要求安装 VMDCW 或 VRCSDK。

## 构建

安装 .NET 10 SDK、Visual Studio C++ 桌面开发工具和 Windows SDK：

```powershell
.\scripts\Build-Release.ps1
# 指定其他输出目录，目录须为空：
.\scripts\Build-Release.ps1 -OutputDirectory artifacts/release-custom
# 不包含可选 HPS 适配器：
.\scripts\Build-Release.ps1 -WithoutUnityHps -OutputDirectory artifacts/release-basic
```

默认输出为 `artifacts/win-x64/`，包含外部程序、依赖、可安装的 Unity 包及其 `.cs`/`.asmdef`/`.meta` 文件、示例、文档和许可证。GitHub Actions 执行相同脚本并上传整个目录。HPS 源码位于 `third_party/Unity-HPS`，构建无需相邻仓库。不包含 HPS 的构建无需原生 FBX 工具链。

## 开源协议

VMDCC 使用 [Apache-2.0](LICENSE)。随附的 Unity-HPS 使用 Apache-2.0，ufbx 使用其 MIT 许可；源码快照及包含 HPS 的发布包保留相应署名和许可证。
