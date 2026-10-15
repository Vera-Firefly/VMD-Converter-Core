# VMDCC 命令行使用指南

## 运行方式

```powershell
.\VmdConverterCore.exe --help
.\VmdConverterCore.exe --pmx model.pmx --vmd motion.vmd --output motion.anim --anim-w
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --vmd motion.vmd --output motion-target.anim --anim-w
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-fk --vmd motion.vmd --output motion-fk.anim --anim-w
```

`humanoid-ik` 使用 PMX 源人体骨架，`--fbx` 在此类型中提供形态键绑定。`target-humanoid` 使用目标骨架及腿部 IK；`target-fk` 输出目标骨架 Transform 曲线。未指定 FBX/HPS rig 时使用 PMX 人体代理骨架，其 FK 路径不代表任意目标 Avatar 的路径。

## Unity-HPS 配置

| 条件 | 求解方式 |
| --- | --- |
| 不传 `--anim-w` | VMDCC 原求解器，不加载 HPS。 |
| `--anim-w` 且安装了 HPS | 通过 `VmdConverterCore.Hps.dll` 调用 `UnityHps.Core.dll`。 |
| `--anim-w` 且未安装 HPS | VMDCC 原求解器，要求提供原格式 `--rig`。 |

HPS 默认从 EXE 同目录查找，可用 `--hps-dir` 指定目录。读取 FBX 另需 `UnityHps.Fbx.dll`。安装损坏或版本不兼容时返回错误。

## rig

```powershell
# PMX 源 rig：
.\VmdConverterCore.exe --pmx model.pmx --anim-w --rig-only --rig-output pmx-rig.json
# FBX 目标 rig：
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --anim-w --rig-only --rig-output avatar-rig.json
# 复用 HPS rig：
.\VmdConverterCore.exe --pmx model.pmx --hps-rig avatar-rig.json --output-kind target-humanoid --vmd motion.vmd --output motion.anim --anim-w
```

上述命令需要 HPS。FBX 映射默认读取相邻 `.fbx.meta`，也可传 `--meta` 或 `--bone-map`。骨骼映射 JSON 为 Humanoid 名称到 FBX 节点名的对象，例如 `{"Hips":"pelvis","LeftUpperLeg":"thigh_l"}`。

HPS rig 使用 `--hps-rig`，Unity 导出的 VMDCC 校准 rig 使用 `--rig`，不可互换。修改骨架、比例或 Humanoid 配置后应重新生成。专用校准 rig 适用于自定义人体轴和肌肉限制。

## 批量转换

```powershell
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --vmd a.vmd --vmd b.vmd --output-dir output --anim-w --threads 8 --job-threads 2 --memory-mb 256
```

输入文件的基本名不能重复；需要自定义输出时使用 `--jobs jobs.json`：

```json
{
  "jobs": [
    {"vmd": "motions/a.vmd", "output": "output/a.anim"},
    {"vmd": "motions/b.vmd", "output": "output/b.anim", "expressionVmd": "faces/b.vmd", "morphMap": "morphs.json"}
  ]
}
```

```powershell
.\VmdConverterCore.exe --pmx model.pmx --fbx avatar.fbx --output-kind target-humanoid --jobs jobs.json --anim-w --threads 8 --job-threads 2
```

清单中的路径按当前工作目录解析。每个输出先写 `.partial`，成功后替换目标文件；部分任务失败时保留其他成功输出。

`--threads` 设置计算线程预算，`--job-threads` 设置同时转换的文件数。`--memory-mb` 限制估算的并发姿态缓存，不是进程总内存限制；单个大动作可独占预算。可降低并发文件数以减少内存占用。

## 通用参数

| 参数 | 用途与默认值 |
| --- | --- |
| `--help`、`-h` | 显示帮助并退出。 |
| `--pmx <file>` | 必填，PMX 骨架和 IK 来源。 |
| `--vmd <file>` | VMD 输入，可重复。 |
| `--output <file>` | 单任务输出。 |
| `--output-dir <dir>` | 多个 VMD 的输出目录，输出使用输入基本名。 |
| `--jobs <file>` | JSON 任务清单，替代逐项 VMD/输出参数。 |
| `--rig <file>` | VMDCC 目标校准 JSON；未传 rig 的普通模式输出姿态缓存。 |
| `--output-kind <kind>` | `humanoid-ik`、`target-humanoid` 或 `target-fk`；有 `--rig` 时默认采用其类型，否则为 `humanoid-ik`。 |
| `--sample-rate <fps>` | 默认 `30`，范围 `(0,240]`；须与 VMDCC rig 的采样率一致。 |
| `--threads <count>` | 默认逻辑 CPU 数减一、最小 `1`，范围 `1..256`。 |
| `--job-threads <count>` | 默认 `min(2,任务数,threads)`，范围 `1..min(32,threads)`。 |
| `--memory-mb <MiB>` | 姿态缓存预算，默认 `256`，范围 `32..8192`。 |
| `--expression-vmd <file>` | 单任务表情 VMD；批量使用清单的 `expressionVmd`。 |
| `--nlu` | 使用独立 RootMotion/Muscles 模块，要求 `--rig`，不适用于目标 FK。 |
| `--anim-w` | 直接输出 `.anim` 并启用 HPS 查找；每个任务输出须以 `.anim` 结尾。 |

## HPS 参数

以下参数用于 `--anim-w` 且已安装 HPS 的调用：

| 参数 | 用途与默认值 |
| --- | --- |
| `--hps-dir <dir>` | HPS 适配器与 SDK 所在目录。 |
| `--fbx <file>` | 目标 FBX。 |
| `--hps-rig <file>` | 已生成的 HPS rig。 |
| `--meta <file>` | FBX 导入元数据。 |
| `--bone-map <file>` | 显式人体骨骼映射 JSON。 |
| `--profile <file>` | 响应模板，JSON 或 JSON.gz。 |
| `--rig-output <file>` | 保存本次使用的 HPS rig。 |
| `--rig-only` | 仅生成 rig 后退出；要求 `--rig-output`，无需 VMD，仍需 PMX。 |
| `--position-scale <number>` | PMX 单位到米的比例，默认 `0.08`。 |
| `--coordinate <number>` | `0` 翻转 Z，`1` 翻转 X，`2` 绕 Y 轴旋转 180°（默认），`3` 不转换。 |
| `--passes <count>` | 拟合轮数，默认 `2`，范围 `1..20`。 |
| `--target-leg-ik <bool>` | 目标腿部 IK；目标 Humanoid 默认 `true`，FK 默认 `false`。 |
| `--curve-error <number>` | 肌肉曲线减帧误差，默认 `0.00001`；`0` 保留所有样本。 |
| `--rotation-error <degrees>` | FK 旋转减帧误差，默认 `0.01` 度；`0` 保留所有样本。 |
| `--position-error <metres>` | FK 位置减帧误差，默认 `0.00005` 米；`0` 保留所有样本。 |
| `--bake-morphs <bool>` | 输出形态键曲线，默认 `true`。 |
| `--morph-map <file>` | 形态键映射 JSON；也可按任务传入 `morphMap`。 |

布尔参数填写 `true` 或 `false`；`--nlu`、`--anim-w`、`--rig-only` 不带值。

## 形态键

```json
{"mappings":[{"source":"Smile","name":"Happy","path":"Body"}]}
```

`source` 为 VMD 形态键名，`name` 为目标形态键名，`path` 为相对 Avatar 根节点的渲染器路径。未映射项按原名输出到 `Body`。表情 VMD 仅替换同名的源形态键轨道。

## Unity 集成

调用方可选择读取 `.vccpose`/`.vcccurves` 后创建 AnimationClip，或调用 `VmdStandaloneAnimConverter.ConvertBatch` 由外部程序生成 `.anim` 后导入。`BuildCommandLine` 返回单行命令，`WriteMorphMap` 写出映射文件。GameObject 到 FBX 的路径解析使用 `VmdSourceFbxUtility.FindSourceFbx`；场景中对骨架的修改不会自动写回源 FBX。

具体接口和示例见[包调用指南](../README.md)。离线 Humanoid 转换采用响应模型，自定义人体设置应使用匹配的校准 rig。`.anim` 使用 Unity 2022.3 文本序列化格式，导入时由 Unity 管理 `.meta`。
