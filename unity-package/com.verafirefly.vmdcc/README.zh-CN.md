# VMD Converter Core Unity 集成包

[English](README.md) | 简体中文

本包提供外部 VMD Converter Core 的公开 Unity Editor 调用接口，支持 PMX/VMD 动作转换、Humanoid IK、目标校准 Humanoid、目标骨架 FK 及形态键映射，无需安装 VMDCW 或 VRCSDK。

## 安装

在 Windows x64 的 Unity 2022.3 工程中安装为本地包。外部程序及其 DLL 位于 `Tools/`，在独立进程中运行；.NET 10 DLL 的 Unity 插件加载已禁用。

## Editor 调用

在自己的 Editor 程序集定义中引用 `com.verafirefly.vmdcc` 和 `com.verafirefly.vmdcc.editor`。直接调用 NLU 接口时另需引用 `com.verafirefly.vmdcc.nlu.editor`。

```csharp
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;

var options = new VmdStandaloneAnimOptions {
    ExecutablePath = exePath,
    PmxPath = pmxPath,
    FbxPath = VmdSourceFbxUtility.FindSourceFbx(targetGameObject),
    OutputKind = VmdExternalOutputKind.TargetCalibratedHumanoid,
    Threads = 8
};
var jobs = new[] {
    new VmdStandaloneAnimJob { VmdPath = vmdPath, OutputPath = physicalAnimPath }
};
VmdStandaloneAnimConverter.ConvertBatch(options, jobs);
```

转换使用绝对文件路径。输出位于 Assets 下时，由调用方通过 `AssetDatabase.ImportAsset` 导入。`BuildCommandLine(options, job)` 返回单行控制台命令，`WriteMorphMap` 写出形态键映射；逐任务设置 `MorphMapPath` 与 `ExpressionVmdPath`。未映射形态键使用 `Body` 路径。

该接口传入 `--anim-w`。离线 rig 生成需要 `VmdConverterCore.Hps.dll`、`UnityHps.Core.dll`，读取 FBX 另需 `UnityHps.Fbx.dll`。仅有 `--anim-w` 时查找 HPS；缺少 HPS 时，直接输出 `.anim` 需要 VMDCC 格式的 `--rig`。

## 曲线缓存调用

使用 `VmdTargetRigExporter.Export/ExportJson/AttachCanonical/WriteJson` 导出目标 rig，通过 `VmdConverterCore.PrepareBatch` 提交任务，以 `ReadCurves` 读取曲线，再由 Unity Editor API 创建动画。`VmdNluBatchPipeline.PrepareBatch` 提供独立的 Humanoid RootMotion/Muscles 处理。

HPS rig 使用 `--hps-rig`，Unity 导出的 VMDCC rig 使用 `--rig`。场景修改不会自动反映到源 FBX，应使用与目标 Humanoid 配置相匹配的校准数据。

完整参数见[命令行指南](Documentation/OFFLINE.md)，二进制文件要求见 [Tools 说明](Tools/README.md)。本包使用 [Apache-2.0](LICENSE)。
