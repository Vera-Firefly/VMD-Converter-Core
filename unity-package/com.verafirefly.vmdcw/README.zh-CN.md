# VMD Converter Window

Unity 2022.3+ 的 VMD 动作、镜头和 VPD 转换窗口，不依赖 VRCSDK。内置解析可独立使用；安装 VMDCC 后可在 PMX 转换设置中选择 Core 及 EXE 路径。

## 动作输出

动作转换、内置解析调试和 VMDCC 调试的输出列表均直接列出 `humanoid-ik`、`target-humanoid`、`target-fk` 以及两种 target 的 `-all`／`-arm`／`-leg`，共九种。内置调试另提供通用 PMX 骨骼、下肢 FK＋其余 Humanoid 输出。目标模式的求解范围为：Normal 校准目标根运动、躯干和下肢，上肢采用源 Humanoid 姿态；Leg 与 Normal 相同；All 包含目标上肢反解；Arm 对上肢反解并保留源 Humanoid 躯干、下肢。各模式均输出整身动作。

存在 VRCSDK 时寻找层级中激活的 VRC Avatar Descriptor 对象；无可用 Descriptor 或无 SDK 时，在模型选择处指定 GameObject。更换目标时重新加载形态键列表。未映射形态键按源名称写入 Body。

## 求解方式

内置解析：PMX/VMD 并行准备，Unity HumanPoseHandler／目标 Transform 烘焙，创建 .anim。

Core 默认：EXE 批量准备 PMX/IK 原始姿态，校验缓存后交给 Unity 原生烘焙器；Humanoid 与目标 FK 都在 Unity 侧完成最终曲线。

在 PMX 设置的调试区勾选“由VMDCC全部托管”后：EXE 使用必需的 HPS，按明确 FBX 或 HPS rig 生成 .anim，Unity 导入。窗口会提示“该选项是调试功能, 不经UnityAPI处理曲线, 转换精度将会有差异”。这里的曲线求解在外部进行，目标选择及资源导入仍使用 Unity API。

PMX 设置可生成自动换行显示的控制台命令；复制保留单行。离线 target-* 必须指定目标，Humanoid IK 无目标可使用 PMX rig，有目标则整体重定向，不隐式切换目标全身重解。目标下肢的自动策略按 rig 标定类型选择几何或旋转拟合。

## 脚本接口

`VmdBatchConversionSession` 配合 `VmdConversionSettings`、`VmdClipOutput` 批量转换；`ConvertPreparedPoseToClip` 可消费外部 PMX 姿态。目标范围由 `BakeOptions.TargetSolveScope` 与 `FkOptions.TargetSolveScope` 设置。Unity 场景／Avatar／曲线及资源操作必须在 Editor 主线程进行。Core 外部接口及构建见 VMDCC 仓库使用文档。
