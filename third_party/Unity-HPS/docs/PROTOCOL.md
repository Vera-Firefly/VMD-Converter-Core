# 数据协议 v1

JSON 字段名称采用 camelCase。`Rig` 和数组结构的完整定义见 `src/UnityHps/Contracts.cs`。

## 骨骼姿态输入

```json
{
  "version": 1,
  "frames": [{
    "time": 0.0,
    "positions": [0.0, 0.0, 0.0, 0.0, 0.6, 0.0],
    "rotations": [0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0],
    "goals": []
  }]
}
```

示例只展示两个骨骼的数组布局。实际必须提供 rig 中全部骨骼，包括 Animator 根节点和非 Humanoid 中间节点。

- `positions`：`boneCount * 3` 个局部位置分量；缩放已烘焙到米制骨骼平移中。
  上肢肌肉投影会使用肘、腕位置，须填写实际动画姿态；合并辅助骨层级时应同步折叠位置与旋转。
- `rotations`：`boneCount * 4` 个局部四元数分量，xyzw，Hamilton 乘法。JSON 数据与 Unity 坐标方向一致，不要进行额外左右手转换。
- `time`：秒，非负、有限，帧序列应按时间排序。
- `reference`：可选，Unity `GetHumanPose` 的 `bodyPosition/bodyRotation/muscles`，仅 `validate` 使用。
- `inputHumanPose`：可选，生成训练姿态时传入 Unity `SetHumanPose` 的值。它与 `reference` 可能不完全相同。
- `goals`：可选双骨骼 IK 列表。每项含 `root/middle/tip` 骨骼索引、米制世界 `target`、世界空间 `pole`、`weight`（0–1）、可选目标 `rotation`。目前要求直接相邻的三骨骼链，不拉伸。

位置和 IK 的“世界”均指归一化 Animator 根节点空间。将场景实例数据交给 EXE 时，应先去除场景根节点的位置、旋转、缩放。

`continue-fk` 的源和目标均使用上述骨骼姿态格式，各自对应自己的 rig。两序列须有相同数量的帧，时间非负、有限、严格递增且逐帧对应；SDK 容许的时间差为 `1e-5` 秒。输出仍是骨骼姿态格式，只包含时间及局部位置、旋转。它不输出或重新拟合 `humanPose`，也不处理 IK 目标或保留参考标签。连续性状态属于单段动作，不能跨帧并行共享。

## 人体姿态输入/输出

```json
{
  "version": 1,
  "frames": [{
    "time": 0.0,
    "humanPose": {
      "bodyPosition": [0.0, 1.0, 0.0],
      "bodyRotation": [0.0, 0.0, 0.0, 1.0],
      "muscles": [0.0, 0.0, 0.0]
    }
  }]
}
```

肌肉数组示例被缩短；实际长度、顺序取自 `rig.muscles`，Unity 导出通常包含 `HumanTrait.MuscleCount` 个值。`unityIndex` 是原生索引，`name` 是 HumanTrait 名称，不保证可直接当成所有手指的 AnimationClip 属性名。调用方写 `.anim` 时需要处理 Unity 的手指属性名称。

`bodyPosition` 无量纲，其米制长度需要乘 `rig.humanScale`。输出使用此 Avatar 的约定，不保证可以直接替换成其他 Avatar 的 FK 数据。

## rig

- `bones`：父索引、Humanoid 骨骼枚举、中立局部位置/旋转。`hips` 指向实际 Hips 的数组位置，不是固定索引。
- `groups`：每个关节的 1–3 个肌肉通道、共同作用到的局部骨骼，以及响应格点。`muscleRanges` 给出每个通道的正负采样边界；缺省时各边界为 `±1`。默认 Strict 策略只接受格点覆盖的范围；显式 Tangent 策略在有界范围内延拓边界响应。
- `samples`：第 0 维变化最快。`rotations/positions` 只包含该组 `bones` 数组中的骨骼；`muscles` 保存 Unity 的格点读回值，用于描述响应格点。
- `bodyPositionWeights`：校准后的身体位置投影权重；运行时对骨骼世界位置加权求和，再除以 `humanScale`。
- `bodyPositionJacobian`：身体位置的平移响应矩阵，未配置位置权重时使用。
- `bodyOrientationMode=landmarks`：使用腿根与上臂的身体框架估计身体方向。
- `reverseCompositionBones`：存在多组肌肉旋转叠加时，使用反向组合顺序的骨骼索引。

更改 Avatar 的导入配置或骨架后应重新导出。不要手工混合不同模型、不同版本的 rig 与姿态序列。
