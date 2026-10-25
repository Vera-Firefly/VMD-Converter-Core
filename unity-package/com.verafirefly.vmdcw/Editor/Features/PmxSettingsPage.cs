#if VMDCW_HAS_VMDCC
using VmdSourceFbxUtility = VeraFirefly.VmdConverter.Window.CoreDispatch.SourceFbx;
using VmdStandaloneAnimOptions = VeraFirefly.VmdConverter.Window.CoreDispatch.AnimOptions;
using VmdStandaloneAnimJob = VeraFirefly.VmdConverter.Window.CoreDispatch.AnimJob;
using VmdStandaloneMorphTarget = VeraFirefly.VmdConverter.Window.CoreDispatch.MorphTarget;
using VmdStandaloneAnimConverter = VeraFirefly.VmdConverter.Window.CoreDispatch.AnimConverter;
using VmdExternalOutputKind = VeraFirefly.VmdConverter.Window.CoreDispatch.OutputKind;
using VmdOutputModes = VeraFirefly.VmdConverter.Window.CoreDispatch.OutputModes;
using VmdExternalPoseWorker = VeraFirefly.VmdConverter.Window.CoreDispatch.PoseWorker;
using VmdExternalPoseBatch = VeraFirefly.VmdConverter.Window.CoreDispatch.PoseBatch;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;
using VeraFirefly.VmdConverter.Core.Editor;

[Serializable]
internal sealed class PmxSettingsPage : ConverterFeaturePage
{
    [SerializeField] private Vector2 pageScroll;
    [SerializeField] private DefaultAsset debugPmxAsset;
    [SerializeField] private DefaultAsset debugVmdAsset;
    [SerializeField] private GameObject debugSkeletonRoot;
    [SerializeField] private GameObject debugMorphTargetRoot;
    [SerializeField] private BuiltInDebugOutput debugOutput = BuiltInDebugOutput.Humanoid;
    [SerializeField] private float debugSampleRate = 30f;
    [SerializeField] private float debugPositionScale = 1f;
    [SerializeField] private PmxCoordinateConversion debugCoordinateConversion =
        PmxCoordinateConversion.RotateY180;
    [SerializeField] private bool debugBakeMorphs = true;
#if VMDCW_HAS_VMDCC
    [SerializeField] private string coreCommand;
#endif

    internal void OnEnable(ConverterWindowContext context)
    {
        Attach(context);
        NormalizeModelParser();
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawPmxSettings();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawPmxSettings()
    {
        NormalizeModelParser();
        ConverterWindowGui.DrawSectionTitle(L("PMX转换设置", "PMX Conversion Settings", "PMX変換設定"));

        IReadOnlyList<VmdMotionConverter.ModelParserMethod> parsers =
            VmdMotionConverter.GetAvailableModelParsers();
        string[] parserLabels = parsers.Select(GetModelParserLabel).ToArray();
        int currentIndex = Mathf.Max(0, parsers.ToList().IndexOf(modelParserMethod));
        int nextIndex;
        nextIndex = EditorGUILayout.Popup(
            L("模型解析方式", "Model Parser", "モデル解析方式"),
            currentIndex,
            parserLabels);
        modelParserMethod = parsers[Mathf.Clamp(nextIndex, 0, parsers.Count - 1)];

        EditorGUILayout.Space(8f);
        if (modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            Debug_DrawBuiltInSettings();
            EditorGUILayout.Space(14f);
            DrawPmxCacheManagement();
        }
        else if (modelParserMethod == VmdMotionConverter.ModelParserMethod.VmdConverterCore)
#if VMDCW_HAS_VMDCC
            DrawCoreSettings();
#else
            modelParserMethod = VmdMotionConverter.ModelParserMethod.BuiltInManaged;
#endif
    }

#if VMDCW_HAS_VMDCC
    private void DrawCoreSettings()
    {
        ConverterWindowGui.DrawSectionTitle("VMD Converter Core");
        EditorGUI.BeginChangeCheck();
        using (new EditorGUILayout.HorizontalScope())
        {
            coreExecutablePath = EditorGUILayout.TextField(
                L("核心程序", "Core EXE", "コア EXE"), coreExecutablePath);
            if (GUILayout.Button("...", GUILayout.Width(28f)))
            {
                string selected = EditorUtility.OpenFilePanel("VmdConverterCore.exe",
                    Path.GetDirectoryName(coreExecutablePath) ?? Application.dataPath, "exe");
                if (!string.IsNullOrEmpty(selected)) coreExecutablePath = selected;
            }
        }
        if (!File.Exists(coreExecutablePath))
            EditorGUILayout.HelpBox(L("未找到 VmdConverterCore.exe。",
                "VmdConverterCore.exe was not found.",
                "VmdConverterCore.exe が見つかりません。"), MessageType.Warning);

        debugPmxAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("PMX文件", "PMX File", "PMXファイル"),
            debugPmxAsset, typeof(DefaultAsset), false);
        debugVmdAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("VMD文件", "VMD File", "VMDファイル"),
            debugVmdAsset, typeof(DefaultAsset), false);
        DrawMotionOutputChoicePopup();
        debugSkeletonRoot = (GameObject)EditorGUILayout.ObjectField(
            L("目标 Humanoid 模型根", "Target Humanoid Root", "対象 Humanoid ルート"),
            debugSkeletonRoot, typeof(GameObject), true);
        coreSampleRate = EditorGUILayout.Slider(
            L("采样率", "Sample Rate", "サンプルレート"), coreSampleRate, 1f, 120f);
        corePositionScale = EditorGUILayout.FloatField(
            L("位置缩放", "Position Scale", "位置スケール"), corePositionScale);
        coreCoordinateConversion = (PmxCoordinateConversion)EditorGUILayout.Popup(
            L("坐标转换", "Coordinates", "座標変換"),
            (int)coreCoordinateConversion, new[]
            {
                "MMD → Unity", "MMD → Unity + Y 180°", "Y 180°",
                L("无", "None", "なし")
            });
        coreBakeMorphs = EditorGUILayout.Toggle(
            L("烘焙形态键", "Bake Morphs", "モーフをベイク"), coreBakeMorphs);
        coreFullyManaged = EditorGUILayout.Toggle("由VMDCC全部托管", coreFullyManaged);
        if (coreFullyManaged)
        {
            EditorGUILayout.HelpBox("该选项是调试功能, 不经UnityAPI处理曲线, 转换精度将会有差异", MessageType.Warning);
            coreHpsRigPath = EditorGUILayout.TextField(
                L("HPS rig JSON (可选)", "HPS rig JSON (optional)", "HPS rig JSON（任意）"), coreHpsRigPath);
            if (GUILayout.Button(L("选择 HPS rig", "Choose HPS rig", "HPS rig を選択")))
            {
                string selected = EditorUtility.OpenFilePanel("HPS rig JSON",
                    Path.GetDirectoryName(coreHpsRigPath) ?? Application.dataPath, "json");
                if (!string.IsNullOrEmpty(selected)) coreHpsRigPath = selected;
            }
            EditorGUILayout.HelpBox(L(
                "提供 HPS rig 时优先使用它；否则从目标模型查找 FBX。Humanoid IK 先在 PMX 上完成源 IK，再将整身 Humanoid 姿态重定向到目标 rig，不隐式启用目标肢体重解；没有目标 rig 时使用 PMX rig。目标校准 Humanoid 的普通/下肢模式校准根运动、躯干和下肢，上肢保留源肌肉值；全身模式重解全身，上肢模式只重解目标上肢。目标校准 Humanoid 和目标 FK 必须有 FBX 或 HPS rig。",
                "An HPS rig takes priority; otherwise the target model's FBX is used. Humanoid IK solves source IK on the PMX, then retargets the complete Humanoid pose to an explicit rig without target limb reconstruction; without a target, it uses the PMX rig. Normal/Leg target Humanoid fits root, body and legs while retaining source upper muscles; All reconstructs the whole target pose and Arm reconstructs only target upper limbs. Target Humanoid and FK require FBX or HPS rig.",
                "HPS rig を優先し、なければ対象モデルの FBX を使用します。Humanoid IK は PMX のソース IK を解き、対象 rig があれば全身 Humanoid 姿勢をリターゲットします。対象の四肢の再解決は暗黙に有効化しません。対象がなければ PMX rig を使用します。対象 Humanoid の Normal/Leg はルート・胴体・脚を校正し、腕のソース筋肉を維持します。All は全身、Arm は対象上肢のみを再解決します。対象 Humanoid / FK には FBX または HPS rig が必要です。"), MessageType.Info);
        }
        if (!coreFullyManaged)
            EditorGUILayout.HelpBox(L(
                "EXE 并行生成 PMX/IK 姿态；Unity HumanPoseHandler 求解 RootT、RootQ 和肌肉曲线，FK 也由 Unity 目标骨架烘焙。控制台命令为独立离线调用，需要 HPS。",
                "The EXE prepares PMX/IK poses. Unity HumanPoseHandler solves RootT, RootQ and muscles; target FK is baked on the Unity skeleton. Console commands run offline and require HPS.",
                "EXE が PMX/IK 姿勢を生成し、Unity API が Humanoid と対象 FK をベイクします。コンソール単独実行には HPS が必要です。"), MessageType.Info);
        coreThreads = EditorGUILayout.IntField(
            L("线程数 (0=自动)", "Threads (0=auto)", "スレッド数 (0=自動)"),
            coreThreads);
        coreThreads = Mathf.Clamp(coreThreads, 0, 256);
        curveReductionError = EditorGUILayout.Slider("Curve Error",
            curveReductionError, 0f, 0.001f);
        if (motionOutputType == MotionOutputType.TargetFk)
        {
            fkRotationErrorDegrees = EditorGUILayout.Slider(
                L("FK旋转角误差 (度)", "FK Rotation Error (deg)", "FK回転角誤差（度）"),
                fkRotationErrorDegrees, 0f, 0.1f);
            fkPositionError = EditorGUILayout.Slider(
                L("FK根位置误差", "FK Root Position Error", "FKルート位置誤差"),
                fkPositionError, 0f, 0.001f);
        }
        if (EditorGUI.EndChangeCheck()) coreCommand = null;

        string pmxPath = AssetDatabase.GetAssetPath(debugPmxAsset);
        string vmdPath = AssetDatabase.GetAssetPath(debugVmdAsset);
        bool ready = File.Exists(coreExecutablePath) &&
                     IsAssetExtension(pmxPath, ".pmx") &&
                     IsAssetExtension(vmdPath, ".vmd") &&
                     (coreFullyManaged || HasValidHumanoidAnimator(debugSkeletonRoot));
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button(L("生成控制台命令", "Generate Console Command", "コマンドを生成")))
                GenerateCoreCommand(pmxPath, vmdPath);
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(28f)))
                Debug_ConvertCore(pmxPath, vmdPath);
        }
        EditorGUILayout.Space(12f);
        ConverterWindowGui.DrawSectionTitle(
            L("控制台调用命令", "Console Command", "コンソールコマンド"));
        var commandStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
        EditorGUILayout.TextArea(coreCommand ?? string.Empty, commandStyle,
            GUILayout.MinHeight(68f));
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(coreCommand)))
            if (GUILayout.Button(L("复制命令", "Copy Command", "コマンドをコピー")))
                EditorGUIUtility.systemCopyBuffer = coreCommand;
    }

    private VmdExternalOutputKind GetCoreOutputKind()
    {
        return VmdOutputModes.Parse(MotionOutputArgument);
    }

    private PmxAnimationClipBakerOptions CreateCoreBakeOptions() =>
        new PmxAnimationClipBakerOptions
        {
            SampleRate = coreSampleRate,
            PositionScale = corePositionScale,
            CoordinateConversion = coreCoordinateConversion,
            BakeMorphs = coreBakeMorphs,
            CurveReductionError = curveReductionError,
            TargetSolveScope = targetSolveScope, DebugTargetUpperLimbInverseSolve = SolveTargetUpper
        };

    private PmxDebugTargetFkBakeOptions CreateCoreFkOptions()
    {
        PmxDebugTargetFkBakeOptions options = Debug_CreateFkBakeOptions();
        options.UpperLimbInverseSolve = SolveTargetUpper;
        return options;
    }

    private void GenerateCoreCommand(string pmxPath, string vmdPath)
    {
        try
        {
            string directory = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Library", "VmdCoreCommands"));
            Directory.CreateDirectory(directory);
            coreCommand = VmdStandaloneAnimConverter.BuildCommandLine(CreateStandaloneOptions(pmxPath, debugSkeletonRoot),
                new VmdStandaloneAnimJob { VmdPath = PmxAnimationClipBaker.ToPhysicalPath(vmdPath),
                    OutputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(vmdPath) + ".anim") });

        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(L("生成命令失败", "Command Generation Failed", "コマンド生成失敗"),
                exception.Message);
        }
    }

    private void Debug_ConvertCore(string pmxPath, string vmdPath)
    {
        string outputPath = EditorUtility.SaveFilePanelInProject(
            L("保存MMD动画", "Save MMD Animation", "MMDアニメーションを保存"),
            Path.GetFileNameWithoutExtension(vmdPath) + "_Core", "anim",
            L("选择输出AnimationClip路径。", "Choose the AnimationClip path.",
                "AnimationClipの出力先を選択してください。"));
        if (string.IsNullOrEmpty(outputPath)) return;
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath) != null &&
            !ConfirmOverwrite("Overwrite animation", outputPath, "Overwrite")) return;
        try
        {
            if (coreFullyManaged)
            {
                VmdStandaloneAnimConverter.ConvertBatch(CreateStandaloneOptions(pmxPath, debugSkeletonRoot),
                    new[] { new VmdStandaloneAnimJob { VmdPath = PmxAnimationClipBaker.ToPhysicalPath(vmdPath),
                        OutputPath = PmxAnimationClipBaker.ToPhysicalPath(outputPath) } },
                    cancelRequested: (done,total) => EditorUtility.DisplayCancelableProgressBar("VMD Converter Core", $"{done}/{total} VMD", done/(float)Math.Max(1,total)));
                AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport);
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
                return;
            }
            PmxAnimationBakeReport report;
            {
                using (var poses = VmdExternalPoseWorker.PrepareBatch(
                           coreExecutablePath, pmxPath, new[] { vmdPath },
                           coreSampleRate, coreThreads))
                using (var session = new VmdBatchConversionSession(
                           new VmdConversionSettings
                           {
                               PmxPath = pmxPath,
                               Output = motionOutputType == MotionOutputType.Humanoid
                                   ? VmdClipOutput.Humanoid
                                   : IsTargetFk ? VmdClipOutput.TargetFk : VmdClipOutput.TargetHumanoid,
                               TargetAvatar = debugSkeletonRoot,
                               MorphTargetRoot = debugSkeletonRoot.transform,
                               BakeOptions = CreateCoreBakeOptions(),
                               FkOptions = CreateCoreFkOptions()
                           }))
                    report = session.ConvertPreparedPoseToClip(vmdPath,
                        poses.GetPosePath(Path.GetFullPath(
                            PmxAnimationClipBaker.ToPhysicalPath(vmdPath))));
            }
            AssetDatabase.DeleteAsset(outputPath);
            AssetDatabase.CreateAsset(report.Clip, outputPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = report.Clip;
            EditorGUIUtility.PingObject(report.Clip);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(L("转换失败", "Conversion Failed", "変換に失敗"),
                exception.Message);
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    private VmdStandaloneAnimOptions CreateStandaloneOptions(string pmxPath, GameObject targetModel)
    {
        string hpsRig = string.IsNullOrWhiteSpace(coreHpsRigPath) ? null : coreHpsRigPath.Trim();
        return new VmdStandaloneAnimOptions {
            ExecutablePath = coreExecutablePath, PmxPath = PmxAnimationClipBaker.ToPhysicalPath(pmxPath),
            FbxPath = hpsRig == null ? VmdSourceFbxUtility.FindSourceFbx(targetModel) : null,
            HpsRigPath = hpsRig, OutputKind = GetCoreOutputKind(),
            SampleRate = coreSampleRate, PositionScale = corePositionScale, CoordinateConversion = (int)coreCoordinateConversion,
            RotationErrorDegrees=fkRotationErrorDegrees, PositionError=fkPositionError, CurveError = curveReductionError, BakeMorphs = coreBakeMorphs, Threads = coreThreads };
    }
#endif

    private int GetDebugOutputChoiceIndex()
    {
        if (debugOutput == BuiltInDebugOutput.GenericTransform) return MotionOutputChoices.Values.Length;
        if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity) return MotionOutputChoices.Values.Length + 1;
        var type = debugOutput == BuiltInDebugOutput.Humanoid ? MotionOutputType.Humanoid :
            debugOutput == BuiltInDebugOutput.TargetModelFkFidelity ? MotionOutputType.TargetFk : MotionOutputType.TargetCalibratedHumanoid;
        return MotionOutputChoices.IndexOf(type, targetSolveScope);
    }

    private void SetDebugOutputChoiceIndex(int index)
    {
        if (index >= MotionOutputChoices.Values.Length)
        {
            debugOutput = index == MotionOutputChoices.Values.Length ? BuiltInDebugOutput.GenericTransform : BuiltInDebugOutput.TargetModelLowerBodyFkFidelity;
            targetSolveScope = PmxTargetSolveScope.Normal;
            return;
        }
        var choice = MotionOutputChoices.Values[Mathf.Clamp(index, 0, MotionOutputChoices.Values.Length - 1)];
        debugOutput = choice.Type == MotionOutputType.Humanoid ? BuiltInDebugOutput.Humanoid :
            choice.Type == MotionOutputType.TargetFk ? BuiltInDebugOutput.TargetModelFkFidelity : BuiltInDebugOutput.TargetCalibratedHumanoid;
        targetSolveScope = choice.Scope;
    }

    private PmxDebugTargetFkBakeOptions CreateBuiltInDebugFkOptions()
    {
        var options = Debug_CreateFkBakeOptions();
        if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
            options.UpperLimbInverseSolve = targetUpperLimbInverseSolve;
        return options;
    }

    private void Debug_DrawBuiltInSettings()
    {
        ConverterWindowGui.DrawSectionTitle(L("内置解析方案调试", "Built-in Parser Debug", "内蔵解析方式のデバッグ"));
        debugPmxAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("PMX文件", "PMX File", "PMXファイル"),
            debugPmxAsset,
            typeof(DefaultAsset),
            false);
        debugVmdAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("VMD文件", "VMD File", "VMDファイル"),
            debugVmdAsset,
            typeof(DefaultAsset),
            false);

        string[] outputLabels = GetMotionOutputChoiceLabels().Concat(new[]
        {
            L("通用 PMX 骨骼", "Generic PMX Transform", "汎用 PMX ボーン"),
            L("下肢 FK + 其余 Humanoid", "Lower-body FK + Humanoid remainder", "下半身 FK + 残り Humanoid")
        }).ToArray();
        int outputIndex = EditorGUILayout.Popup(
            L("输出类型", "Output", "出力タイプ"), GetDebugOutputChoiceIndex(), outputLabels);
        if (outputIndex != GetDebugOutputChoiceIndex()) SetDebugOutputChoiceIndex(outputIndex);
        if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity)
        {
            EditorGUILayout.HelpBox(
                L("按所选输出类型写入目标骨架 FK。Normal/Leg 保留源 Humanoid 上肢；All/Arm 启用目标上肢重解。",
                    "Writes target FK for the selected output. Normal/Leg retain source Humanoid arms; All/Arm reconstruct target upper limbs.",
                    "選択した出力タイプで対象 FK を書き出します。Normal/Leg はソースの腕を維持し、All/Arm は対象上肢を再解決します。"),
                MessageType.Info);
        }
        if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
        {
            EditorGUILayout.HelpBox(
                L("下肢输出目标骨架逐骨 FK 曲线，其余骨骼由目标校准 Humanoid 姿态实体化。开启“目标上肢反解”时，上肢改用目标 FK 重建后经 HumanPose 反解的结果。",
                    "Writes per-bone target FK curves for the lower body and materializes the remaining bones from the target-calibrated Humanoid pose. When Target Upper-limb Inverse Solve is enabled, upper limbs use target FK reconstruction followed by HumanPose inverse solving.",
                    "下半身は対象骨格のボーン別 FK カーブを出力し、その他のボーンは対象校正 Humanoid 姿勢から実体化します。対象上肢の逆解析を有効にすると、上肢は対象 FK 再構築後の HumanPose 逆解析結果を使用します。"),
                MessageType.Info);
        }

        if (debugOutput == BuiltInDebugOutput.Humanoid)
        {
            EditorGUILayout.LabelField(
                L("源骨架", "Source Skeleton", "ソーススケルトン"),
                L("从 PMX 自动构建", "Built automatically from PMX", "PMXから自動構築"));
            debugMorphTargetRoot = (GameObject)EditorGUILayout.ObjectField(
                L("形态键目标根（可选）", "Morph Target Root (Optional)", "モーフ対象ルート（任意）"),
                debugMorphTargetRoot,
                typeof(GameObject),
                true);
        }
        else
        {
            debugSkeletonRoot = (GameObject)EditorGUILayout.ObjectField(
                debugOutput == BuiltInDebugOutput.TargetModelFkFidelity ||
                debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity ||
                debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid
                    ? L("目标 Humanoid 模型根", "Target Humanoid Model Root", "対象Humanoidモデルルート")
                    : L("现有PMX骨架根", "Existing PMX Skeleton Root", "既存PMXスケルトンルート"),
                debugSkeletonRoot,
                typeof(GameObject),
                true);
        }

        debugSampleRate = EditorGUILayout.Slider(
            L("采样率", "Sample Rate", "サンプルレート"),
            debugSampleRate,
            1f,
            120f);
        debugPositionScale = EditorGUILayout.FloatField(
            L("位置缩放", "Position Scale", "位置スケール"),
            debugPositionScale);
        debugCoordinateConversion = (PmxCoordinateConversion)EditorGUILayout.Popup(
            L("坐标转换", "Coordinates", "座標変換"),
            (int)debugCoordinateConversion,
            new[]
            {
                "MMD → Unity",
                "MMD → Unity + Y 180°",
                "Y 180°",
                L("无", "None", "なし")
            });
        debugBakeMorphs = EditorGUILayout.Toggle(
            L("烘焙同名形态键", "Bake Same-name Morphs", "同名モーフをベイク"),
            debugBakeMorphs);
        if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
        {
            targetUpperLimbInverseSolve = EditorGUILayout.Toggle(
                new GUIContent(
                    L("目标上肢反解", "Target Upper-limb Inverse Solve", "対象上肢の逆解析"),
                    L("影响目标校准 Humanoid 与目标模型 FK 保真。启用后，上肢执行目标 FK 重建并由 HumanPose 反解；默认关闭。",
                        "Affects target-calibrated Humanoid and target-model FK fidelity. Reconstructs target upper-limb FK and inverse-solves it through HumanPose; disabled by default.",
                        "対象校正 Humanoid と対象モデル FK 保持に適用します。対象上肢 FK を再構築して HumanPose から逆解析します。既定では無効です。")),
                targetUpperLimbInverseSolve);
            if (targetUpperLimbInverseSolve)
            {
                EditorGUILayout.HelpBox(
                    L("调试模式：会为每个采样帧增加上肢 HumanPose 往返求解，因此转换更慢；极端扭转仍可能发生分支翻转。",
                        "Debug mode: adds an upper-limb HumanPose round trip per sample and is slower; extreme twists may still flip branches.",
                        "デバッグモード：各サンプルで上肢 HumanPose の往復解析を追加するため遅くなります。極端なツイストでは分岐反転が残る場合があります。"),
                    MessageType.Warning);
            }
        }
        else if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity || debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid)
            EditorGUILayout.LabelField(L("目标上肢反解", "Target upper-limb reconstruction", "対象上肢の再解決"),
                SolveTargetUpper ? L("开启（由输出类型决定）", "Enabled by output type", "出力タイプで有効") : L("关闭（由输出类型决定）", "Disabled by output type", "出力タイプで無効"));
        curveReductionError = EditorGUILayout.Slider(
            new GUIContent(
                "Curve Error",
                L("此项同时应用于VMD动作转换。0表示保留全部采样点。",
                    "This also applies to VMD Motion conversion. Zero keeps every sampled key.",
                    "この設定はVMDモーション変換にも適用されます。0では全サンプルキーを保持します。")),
            curveReductionError,
            0f,
            0.001f);
        if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity ||
            debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
        {
            fkRotationErrorDegrees = EditorGUILayout.Slider(
                L("FK旋转角误差 (度)", "FK Rotation Error (deg)", "FK回転角誤差（度）"),
                fkRotationErrorDegrees,
                0f,
                0.1f);
            fkPositionError = EditorGUILayout.Slider(
                L("FK根位置误差", "FK Root Position Error", "FKルート位置誤差"),
                fkPositionError,
                0f,
                0.001f);
        }

        string pmxPath = AssetDatabase.GetAssetPath(debugPmxAsset);
        string vmdPath = AssetDatabase.GetAssetPath(debugVmdAsset);
        bool canConvert = IsAssetExtension(pmxPath, ".pmx") &&
                          IsAssetExtension(vmdPath, ".vmd") &&
                          (debugOutput == BuiltInDebugOutput.Humanoid || debugSkeletonRoot != null);
        using (new EditorGUI.DisabledScope(!canConvert))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(28f)))
                Debug_ConvertBuiltIn(pmxPath, vmdPath);
        }
    }

    private void DrawPmxCacheManagement()
    {
        ConverterWindowGui.DrawSectionTitle(L("PMX解析缓存管理", "PMX Parse Cache", "PMX解析キャッシュ管理"));
        PmxModelCache.GetDiskCacheStats(out int cacheFiles, out long cacheBytes);
        EditorGUILayout.LabelField(
            L("PMX解析缓存", "PMX Parse Cache", "PMX解析キャッシュ"),
            L($"{cacheFiles} 个文件，{FormatBytes(cacheBytes)}",
                $"{cacheFiles} files, {FormatBytes(cacheBytes)}",
                $"{cacheFiles} ファイル、{FormatBytes(cacheBytes)}"));
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.TextField(
                L("缓存位置", "Cache Location", "キャッシュ場所"),
                PmxModelCache.DiskCacheDirectory);

        if (!GUILayout.Button(
                L("清理 PMX 解析缓存", "Clear PMX Parse Cache", "PMX解析キャッシュを削除"),
                GUILayout.Height(28f)))
            return;

        if (!EditorUtility.DisplayDialog(
                L("清理 PMX 解析缓存", "Clear PMX Parse Cache", "PMX解析キャッシュを削除"),
                L("将清除内存与磁盘中的 PMX 解析缓存。下次转换会重新解析 PMX。",
                    "This clears the PMX parse cache in memory and on disk. The next conversion will parse the PMX again.",
                    "メモリとディスク上の PMX 解析キャッシュを削除します。次回の変換時に PMX を再解析します。"),
                L("清理", "Clear", "削除"),
                L("取消", "Cancel", "キャンセル")))
            return;

        try
        {
            PmxModelCache.ClearAll();
            ShowDialog(
                L("PMX解析缓存", "PMX Parse Cache", "PMX解析キャッシュ"),
                L("缓存已清理。", "Cache cleared.", "キャッシュを削除しました。"));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(
                L("缓存清理失败", "Cache Cleanup Failed", "キャッシュ削除に失敗"),
                exception.Message);
        }
    }

    private void Debug_ConvertBuiltIn(string pmxPath, string vmdPath)
    {
        string defaultName = Path.GetFileNameWithoutExtension(vmdPath) + "_CSharp.anim";
        string outputPath = EditorUtility.SaveFilePanelInProject(
            L("保存MMD动画", "Save MMD Animation", "MMDアニメーションを保存"),
            defaultName,
            "anim",
            L("选择输出AnimationClip路径。",
                "Choose the output AnimationClip path.",
                "AnimationClipの出力先を選択してください。"));
        if (string.IsNullOrEmpty(outputPath)) return;

        try
        {
            var options = new PmxAnimationClipBakerOptions
            {
                SampleRate = debugSampleRate,
                PositionScale = debugPositionScale,
                CoordinateConversion = debugCoordinateConversion,
                BakeMorphs = debugBakeMorphs,
                CurveReductionError = curveReductionError,
                // Target-FK receives the same UI value through its own bake options below.
                // Do not leak the Humanoid inverse-solve flag into another output pipeline.
                TargetSolveScope = targetSolveScope,
                DebugTargetUpperLimbInverseSolve =
                    debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid &&
                    SolveTargetUpper
            };

            double lastProgressUpdate = double.NegativeInfinity;
            string lastStage = string.Empty;
            Action<float, string> progress = (value, message) =>
            {
                double now = EditorApplication.timeSinceStartup;
                string stage = GetProgressStage(message);
                bool stageChanged = !string.Equals(stage, lastStage, StringComparison.Ordinal);
                if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.1d) return;
                lastProgressUpdate = now;
                lastStage = stage;
                if (EditorUtility.DisplayCancelableProgressBar(
                        L("内置解析方案", "Built-in Parser", "内蔵解析方式"),
                        message,
                        value))
                    throw new OperationCanceledException();
            };

            PmxAnimationBakeReport report;
            if (debugOutput == BuiltInDebugOutput.Humanoid)
            {
                report = PmxAnimationConversion.Debug_ConvertHumanoidToAsset(
                    pmxPath,
                    vmdPath,
                    debugMorphTargetRoot != null ? debugMorphTargetRoot.transform : null,
                    outputPath,
                    options,
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid)
            {
                report = PmxAnimationConversion.Debug_ConvertTargetCalibratedHumanoidToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity)
            {
                report = PmxAnimationConversion.Debug_ConvertTargetFkToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    CreateBuiltInDebugFkOptions(),
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
            {
                report = PmxAnimationConversion.Debug_ConvertTargetLowerBodyFkToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    CreateBuiltInDebugFkOptions(),
                    progress);
            }
            else
            {
                report = PmxAnimationConversion.Debug_ConvertGenericPmxToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot.transform,
                    outputPath,
                    options,
                    progress);
            }

            Selection.activeObject = report.Clip;
            EditorGUIUtility.PingObject(report.Clip);
            Debug.Log(
                $"[MMD Play Mode Preview] Baked {report.SampleCount} samples as " +
                $"{debugOutput}, {report.BoundBoneCount} mapped bones and " +
                $"{report.BoundMorphCount} morph curves.",
                report.Clip);
            ShowDialog(
                L("内置解析方案", "Built-in Parser", "内蔵解析方式"),
                L("转换完成：\n", "Conversion completed:\n", "変換が完了しました：\n") + outputPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(
                L("转换失败", "Conversion Failed", "変換に失敗"),
                exception.Message);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
