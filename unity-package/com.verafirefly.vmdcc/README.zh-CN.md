# VMD Converter Core

VMDCC 提供外部 PMX/VMD、IK 预处理和 HPS 动画转换，不依赖 VMDCW、VRCSDK，不提供 PMX 模型转 FBX。

## 调用

Editor 程序集引用 `com.verafirefly.vmdcc` 与 `com.verafirefly.vmdcc.editor`。命名空间为 `VeraFirefly.VmdConverter.Core`、`.Editor`。

- `VmdExternalPoseWorker.PrepareBatch`：不传目标 rig 时生成原始 PMX/IK 姿态供 Unity 原生烘焙；无需 HPS。
- `VmdStandaloneAnimConverter.ConvertBatch`／`BuildCommandLine`：通过 EXE＋HPS 离线生成 `.anim`，返回后由调用者导入。
- `VmdTargetRigExporter.ExportJson`：导出旧版静态绑定，用于 `--rig`／`.vcccurves`。
- `UnityHps.Reference.UnityHpsCalibration.Export`：在 Unity 主线程生成含原生 Avatar 响应的 HPS `rig.json`，用于 `--hps-rig`。
- `VmdConverterCore.PrepareBatch`／`ReadCurves`：HPS 曲线文件接口。

```csharp
var options = new VmdStandaloneAnimOptions {
    ExecutablePath = executable, PmxPath = pmx, FbxPath = fbx,
    OutputKind = VmdExternalOutputKind.TargetFkAll
};
VmdStandaloneAnimConverter.ConvertBatch(options, new[] {
    new VmdStandaloneAnimJob { VmdPath = vmd, OutputPath = anim }
});
```

## 模式与运行文件

支持 `humanoid-ik`、`target-humanoid`、`target-fk` 及两种 target 输出的 `-all`／`-arm`／`-leg`。普通 target 采用下肢策略；all 整身目标拟合，arm 仅目标上肢拟合，输出仍为完整动作。Humanoid IK 有明确目标时重定向源姿态，不隐式启用目标肢体重解；目标下肢 auto 策略从目标中立肌肉坐标拟合膝、踝方向及足部姿态。源姿态与仅目标上肢模式保留源下肢通道。

离线曲线必须有 HPS。保留 Tools 中 EXE、Shared、Hps、UnityHps.Core、UnityHps.Fbx 共五个二进制及其禁用 Unity 插件加载的 `.meta`。EXE 接受 `--help`。明确填写 FBX 或 HPS rig 时不得回退；全部 target 模式必须有目标。旧 `--rig` 与 HPS `--hps-rig` 是不同协议。

Unity 场景、Avatar、HumanPoseHandler、AnimationClip、AssetDatabase 操作必须在 Editor 主线程。VMDCW 默认通过原始姿态接入 Unity API；全部托管选项才由 HPS 离线求曲线。

完整参数、引用示例及构建说明见 [项目文档](https://github.com/Vera-Firefly/VMD-Converter-Core/blob/main/README.zh-CN.md)。

`HpsCalibrationExporter.ExportBatch` 另可导出 Transform 模式 `--calibrate` 的 Unity 测量轴；该格式不是 HPS rig JSON。
