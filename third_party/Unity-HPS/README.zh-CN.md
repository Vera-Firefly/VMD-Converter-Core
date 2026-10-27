# Unity-HPS

[English](README.md)

Unity-HPS 是 C# Humanoid 姿态求解程序集及 Windows x64 命令行程序。不加载 Unity，将骨架局部姿态转换为 RootT、RootQ、肌肉值，并可解码回局部骨骼姿态。支持 FBX rig 读取、标定响应插值、上肢解剖坐标投影、双骨 IK、参考源动作的 FK 分支连续性处理。VMD 解析和 `.anim` 写入由 VMD Converter Core 等调用方负责。

## 命令行

```powershell
.\UnityHps.exe rig --fbx avatar.fbx --output rig.json
.\UnityHps.exe solve --rig rig.json --input poses.json --output result.json --threads 4 --passes 3 --range-policy tangent
.\UnityHps.exe decode --rig rig.json --input human-poses.json --output local-poses.json --range-policy tangent
.\UnityHps.exe --help
```

`rig` 自动读取相邻 FBX `.meta`。`--meta` 覆盖文件，`--bone-map` 提供 Humanoid 骨骼映射，`--ignore-meta true` 禁用元数据，`--scale` 覆盖源缩放，`--profile` 指定可移植响应配置。离线 FBX 使用可移植响应；Unity 导出的 HPS rig 包含目标 Avatar 的原生响应。

其他命令：`fbx-info` 导出骨架和形态键信息；`inspect` 查看 rig；`calibrate` 从姿态及 HumanPose 拟合根质量权重；`profile` 生成响应配置；`continue-fk` 使用流式前臂连续性策略；`validate` 与输入参考比对，不满足精度参数时返回 2；`demo` 生成协议示例；`self-test` 运行组件自动检查。路径参数见 `--help`。

`solve`／`validate` 支持 `--threads`（0 自动）、`--passes`（默认 2）、`--leg-geometry`、`--arm-context`、`--decoded-root`（布尔，默认 false）。`solve`／`decode`／`validate` 支持 `--range-policy strict|tangent`。SDK 与 CLI 默认 strict，拒绝超出标定采样范围；tangent 在有界范围内延拓边界响应，并对总扭转相位求值。输入单位为米，四元数为 xyzw，骨骼顺序必须与 rig 完全一致。

## 程序集接口

.NET10 程序引用 `UnityHps.Core.dll`，读取 FBX 时还需同目录 `UnityHps.Fbx.dll`。命名空间为 `UnityHps`。

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

- `OfflineRigFactory.FromFbx`、`FbxReader.Read`：离线骨架、映射与形态键。
- FBX 和描述文件的目标 rig 使用 `HumanoidResponseTransport.AnatomicalTargets`：下肢联动响应按所属肢段传递，躯干沿下一已映射的躯干骨骼定向，肩部与踝部采用重力稳定的水平朝向；Chest 已映射且 UpperChest 未映射时，其通道作用于 Chest。已有骨架可调用 `OfflineRigFactory.FromSkeleton(document, profilePath, HumanoidResponseTransport.AnatomicalTargets)`。`AnatomicalLegs` 保留此前仅调整下肢的规则。双参数 `FromSkeleton`、`Transfer` 保留 `JointFrames`，供源姿态编码使用；读取 HPS rig JSON 时保留其中的响应数据。
- `HumanoidSolver.Solve`、`SolveBatch`、`SolveMuscles`、`Decode`：姿态／曲线转换，可选活动 HumanBone 分组和初始肌肉。
- `ReconstructRoot`：外部修改肌肉后重算根运动。`FitDecodedArmContext` 在解码后的躯干和肩坐标中求手臂；`FitLegGeometry` 启用几何下肢拟合；`AnchorLimbTwistToInitial` 添加初始扭转软约束。
- `HumanoidUpperProjector.Project`：源／目标上肢解剖投影，返回独立肌肉数组，不改动未选择通道。
- `FkBranchContinuation.Process`：全序列等效手臂分支处理；不修改输入数组。`FkRollContinuation` 提供独立的流式前臂策略。
- `HumanoidBodyMassCalibration.Fit`、`RootCalibration.Fit`：根质量标定，应用前使用独立参考验收。
- `SkeletonPose`、`TwoBoneIk`、`AvatarDescriptorIO`、`HpsJson`：姿态、IK、FBX 描述及序列化。

批量共用不可变标定与模型数据，各帧状态独立。根运动依据最终肌肉求解。精度取决于标定覆盖及目标骨架差异；纯 FBX 与原生 Avatar 标定是不同输入类型。

## Unity 原生标定

将 `unity/Editor/UnityHpsCalibration.cs` 放入 Editor 目录，调用 `UnityHps.Reference.UnityHpsCalibration.Export(sourceGameObject, outputDirectory, resolution: 5, referenceFrames: 128, seed: 12345)`。它克隆 Humanoid 骨架，通过 `HumanPoseHandler` 采样，输出 `rig.json`、`training.json`、`reference.json`。该步骤在 Editor 主线程运行；导出结果可脱离 Unity 使用。离线读取 FBX 不会调用 Unity API。

## 构建

Windows 安装 .NET SDK10 与 Visual Studio C++ x64 工具：

```powershell
./scripts/Build.ps1
# 常规程序集构建：
./scripts/Build-Native.ps1
dotnet build src/UnityHps.Cli -c Release
```

默认生成 NativeAOT EXE；添加 `-NoAot` 可生成自包含托管 EXE。

| 内容 | 路径 |
|---|---|
| 可执行文件 | `artifacts/publish/win-x64/UnityHps.exe` |
| 托管 SDK | `artifacts/release/Unity-HPS-win-x64/managed/` |
| Unity 标定脚本 | `unity/Editor/UnityHpsCalibration.cs` |

上肢响应数据嵌入 `UnityHps.Core.dll`，运行时不需要额外的模型数据目录。

详见 [离线用法](docs/OFFLINE.md)、[JSON 协议](docs/PROTOCOL.md)、[开源协议](LICENSE)、[第三方声明](NOTICE)。
