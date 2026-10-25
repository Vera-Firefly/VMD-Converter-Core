# Unity 脚本引用 VMDCC

安装 `com.verafirefly.vmdcc`；调用程序集在 asmdef 引用 `com.verafirefly.vmdcc` 和 `com.verafirefly.vmdcc.editor`。无需 VMDCW 或 VRCSDK。

```csharp
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;
var options = new VmdStandaloneAnimOptions {
    ExecutablePath = executable, PmxPath = sourcePmx,
    FbxPath = targetFbx, OutputKind = VmdExternalOutputKind.TargetFkAll
};
var jobs = new[] { new VmdStandaloneAnimJob {
    VmdPath = sourceVmd, OutputPath = outputAnim
} };
string command = VmdStandaloneAnimConverter.BuildCommandLine(options, jobs[0]);
VmdStandaloneAnimConverter.ConvertBatch(options, jobs);
UnityEditor.AssetDatabase.ImportAsset(outputAssetPath);
```

外部转换必须有 HPS。填写 `HpsRigPath` 可替代 `FbxPath`，不可同时填写。其余参数涵盖采样、坐标、误差、拟合轮数、采样区延拓、下肢策略及 FK 连续性。输出目录、覆盖策略由调用方管理；参数用物理路径，AssetDatabase 用 Assets 相对路径。一批任务启动一个 EXE。

## GameObject → HPS rig

```csharp
UnityHps.Reference.UnityHpsCalibration.Export(targetGameObject, calibrationDirectory,
    resolution: 5, referenceFrames: 128, seed: 12345);
options.FbxPath = null;
options.HpsRigPath = System.IO.Path.Combine(calibrationDirectory, "rig.json");
```

标定通过 Unity `HumanPoseHandler` 执行，必须在 Editor 主线程调用；导出的 JSON 可重复离线使用。纯 FBX 输入不会自动包含场景中的骨架修改。

`VmdTargetRigExporter.ExportJson` 导出的是旧版 `--rig` 静态绑定协议，不是 `--hps-rig`。完整曲线文件调用示例见 [VmdccTargetFkExample.cs](VmdccTargetFkExample.cs)，其中外部曲线仍由 HPS 求解，Unity 通过 `AnimationUtility.SetEditorCurves` 建立资源。

## Unity API 求解

`VmdExternalPoseWorker.PrepareBatch(executable, pmx, vmdPaths, sampleRate, threads)` 不传目标 rig 时生成原始 `.vccpose`；该步骤不需要 HPS。使用完缓存前保留返回的批任务对象，完成后 Dispose。

实际 HumanPose 烘焙由调用者完成，VMDCC 不包含 VMDCW 内置求解器。可另装 VMDCW，使用其公开 `VmdBatchConversionSession.ConvertPreparedPoseToClip`，通过 `VmdConversionSettings`、`VmdClipOutput` 指定输出，并由 `BakeOptions.TargetSolveScope`、`FkOptions.TargetSolveScope` 指定 Normal／All／Arm／Leg。PMX 准备可并行，原生 HumanPose、Transform、动画资源操作留在主线程。详见 [窗口调用链](VMDCW_CALL_CHAIN.md)。
