using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[Serializable]
internal sealed class VpdConversionPage : ConverterFeaturePage
{
    private static readonly Color[] DarkEntryColors =
    {
        new Color(0.38f, 0.78f, 1f), new Color(1f, 0.62f, 0.38f),
        new Color(0.55f, 0.88f, 0.48f), new Color(0.85f, 0.58f, 1f),
        new Color(1f, 0.78f, 0.30f), new Color(0.40f, 0.88f, 0.82f)
    };
    private static readonly Color[] LightEntryColors =
    {
        new Color(0.05f, 0.42f, 0.72f), new Color(0.72f, 0.25f, 0.05f),
        new Color(0.16f, 0.52f, 0.12f), new Color(0.52f, 0.20f, 0.68f),
        new Color(0.65f, 0.42f, 0.02f), new Color(0.02f, 0.48f, 0.43f)
    };

    [SerializeField] private List<DefaultAsset> vpdAssets = new List<DefaultAsset>();
    [SerializeField] private List<bool> vpdFileFoldouts = new List<bool>();
    [SerializeField] private Vector2 vpdListScroll;
    [SerializeField] private Vector2 pageScroll;
    [SerializeField] private bool vpdConversionSettingsExpanded = true;
    [SerializeField] private bool vpdRetargetModel;
    [SerializeField] private DefaultAsset vpdModelAsset;
    [SerializeField] private GameObject vpdTargetAvatar;
    [SerializeField] private MotionOutputType vpdOutputType;
    [SerializeField] private MotionStorageLocation vpdOutputStorageLocation;
    [SerializeField] private string customVpdOutputStorageFolder = DefaultMotionStorageFolder;

    internal void OnEnable(ConverterWindowContext context, DefaultAsset selected, bool acceptSelection)
    {
        Attach(context);
        EnsureVpdEntryState();
        if (acceptSelection && IsVpdAsset(selected))
            AddVpdAsset(vpdAssets, selected);
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        EnsureVpdEntryState();
        RemoveMissingVpdEntries();
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawVpdConverter();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        Attach(context);
        EditorGUILayout.Space(8f);
        List<string> sourcePaths = GetVpdAssetPaths(vpdAssets);
        string modelPath = vpdRetargetModel
            ? AssetDatabase.GetAssetPath(vpdModelAsset)
            : VmdMotionConverter.DefaultModelPath;
        bool needsTarget = vpdOutputType == MotionOutputType.TargetCalibratedHumanoid;
        bool canConvert = sourcePaths.Count > 0 &&
                          VmdMotionConverter.IsModelPath(
                              modelPath,
                              VmdMotionConverter.ModelParserMethod.BuiltInManaged) &&
                          TryGetVpdOutputStorageFolder(out _) &&
                          (!needsTarget || HasValidHumanoidAnimator(vpdTargetAvatar));
        using (new EditorGUI.DisabledScope(!canConvert))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                ConvertVpds(sourcePaths, modelPath, vpdTargetAvatar);
        }
    }

    private static Color GetMotionEntryColor(int index)
    {
        Color[] palette = EditorGUIUtility.isProSkin ? DarkEntryColors : LightEntryColors;
        return palette[Mathf.Abs(index) % palette.Length];
    }

    private void DrawVpdConverter()
    {
        ConverterWindowGui.DrawSectionTitle(L("VPD姿势转换", "VPD Pose Conversion", "VPDポーズ変換"));
        DrawVpdFileList();

        ConverterWindowGui.DrawOutlinedSetting(() =>
        {
            vpdConversionSettingsExpanded = EditorGUILayout.Foldout(
                vpdConversionSettingsExpanded,
                L("转换设置", "Conversion Settings", "変換設定"),
                true);
            if (!vpdConversionSettingsExpanded) return;

            EditorGUILayout.Space(5f);
            vpdOutputType = (MotionOutputType)EditorGUILayout.Popup(
                L("输出类型", "Output Type", "出力タイプ"),
                (int)vpdOutputType,
                new[]
                {
                    "Humanoid",
                    L("目标校准 Humanoid", "Target-calibrated Humanoid", "対象校正Humanoid")
                });
            if (vpdOutputType == MotionOutputType.TargetCalibratedHumanoid)
            {
                EditorGUILayout.HelpBox(
                    L("在目标Avatar骨架上校准后输出标准HumanoidRoot/Muscle曲线, 该方案会额外经过目标 Avatar 的 HumanPoseHandler 校准, 因此转换通常更慢",
                        "Outputs standard Humanoid Root/Muscle curves after calibration on the target Avatar skeleton. This adds a target Avatar HumanPoseHandler calibration pass, so conversion is usually slower.",
                        "対象Avatar骨格で校正後、標準Humanoid Root/Muscleカーブを出力します。対象AvatarのHumanPoseHandler校正処理が追加されるため、通常は変換が遅くなります。"),
                    MessageType.Info);
            }

            EditorGUILayout.Space(5f);
            vpdTargetAvatar = (GameObject)EditorGUILayout.ObjectField(
                vpdOutputType == MotionOutputType.TargetCalibratedHumanoid
                    ? L("目标 Avatar", "Target Avatar", "対象Avatar")
                    : L("Morph目标 Avatar（可选）", "Morph Target Avatar (Optional)", "Morph対象Avatar（任意）"),
                vpdTargetAvatar,
                typeof(GameObject),
                true);

            EditorGUILayout.Space(5f);
            DrawVpdConversionModelSettings();

            EditorGUILayout.Space(5f);
            DrawStorageLocationSetting(
                L("输出位置", "Output Location", "出力場所"),
                ref vpdOutputStorageLocation,
                ref customVpdOutputStorageFolder,
                L("输出存储位置", "Output Storage Location", "出力保存場所"));
            EditorGUILayout.Space(5f);
            EditorGUILayout.HelpBox(
                L("每个VPD固定生成一个.anim。姿势写入0与1/30秒两个相同关键帧，不进行密集采样。",
                    "Each VPD always produces one .anim. The pose is written as identical keys at 0 and 1/30 seconds without dense sampling.",
                    "各VPDから必ず1つの.animを生成します。ポーズは0秒と1/30秒の同一キーとして書き込み、密なサンプリングは行いません。"),
                MessageType.None);
        });
    }

    private void DrawVpdConversionModelSettings()
    {
        vpdRetargetModel = EditorGUILayout.Toggle(
            L("重定向模型", "Retarget Model", "モデルを置換"),
            vpdRetargetModel);
        if (!vpdRetargetModel) return;

        DefaultAsset nextModel = (DefaultAsset)EditorGUILayout.ObjectField(
            L("MMD模型", "MMD Model", "MMDモデル"),
            vpdModelAsset,
            typeof(DefaultAsset),
            false);
        if (nextModel == vpdModelAsset) return;

        string modelPath = nextModel != null
            ? AssetDatabase.GetAssetPath(nextModel)
            : string.Empty;
        if (nextModel != null && !VmdMotionConverter.IsModelPath(
                modelPath, VmdMotionConverter.ModelParserMethod.BuiltInManaged))
        {
            ShowDialog(
                L("MMD模型", "MMD Model", "MMDモデル"),
                L("内置解析方案仅支持.pmx模型。",
                    "The built-in parser supports .pmx models only.",
                    "内蔵解析方式は.pmxモデルのみ対応しています。"));
            return;
        }
        vpdModelAsset = nextModel;
    }

    private void DrawVpdFileList()
    {
        EnsureVpdEntryState();
        if (vpdAssets.Count > 0)
        {
            const float maxListHeight = 220f;
            float rowHeight = EditorGUIUtility.singleLineHeight +
                              EditorGUIUtility.standardVerticalSpacing;
            float contentHeight = 0f;
            for (int index = 0; index < vpdAssets.Count; index++)
                contentHeight += rowHeight * (vpdFileFoldouts[index] ? 2f : 1f) +
                                 (index > 0 ? 5f : 0f);
            bool useScrollView = contentHeight > maxListHeight;
            if (useScrollView)
            {
                vpdListScroll = EditorGUILayout.BeginScrollView(
                    vpdListScroll, false, false, GUILayout.Height(maxListHeight));
            }

            try
            {
                for (int index = 0; index < vpdAssets.Count; index++)
                {
                if (index > 0) ConverterWindowGui.DrawEntrySeparator();
                Color entryColor = GetMotionEntryColor(index);
                bool removeEntry;
                Color previousContentColor = GUI.contentColor;
                try
                {
                    GUI.contentColor = entryColor;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string arrow = vpdFileFoldouts[index] ? "▼ " : "▶ ";
                        if (GUILayout.Button(
                                arrow + GetVpdDisplayName(vpdAssets[index]),
                                EditorStyles.boldLabel,
                                GUILayout.ExpandWidth(true)))
                            vpdFileFoldouts[index] = !vpdFileFoldouts[index];
                        removeEntry = GUILayout.Button(
                            "×", EditorStyles.miniButton, GUILayout.Width(24f));
                    }
                }
                finally
                {
                    GUI.contentColor = previousContentColor;
                }
                if (removeEntry)
                {
                    vpdAssets.RemoveAt(index);
                    vpdFileFoldouts.RemoveAt(index);
                    break;
                }
                if (!vpdFileFoldouts[index]) continue;

                int previousIndentLevel = EditorGUI.indentLevel;
                try
                {
                    EditorGUI.indentLevel = previousIndentLevel + 1;
                    if (DrawVpdDataRow(
                            L("姿势数据", "Pose Data", "ポーズデータ"),
                            vpdAssets[index],
                            out DefaultAsset next))
                    {
                        if (next == null)
                        {
                            vpdAssets[index] = null;
                        }
                        else
                        {
                            string nextPath = GetVpdAssetPath(next);
                            bool duplicate = vpdAssets
                                .Where((_, otherIndex) => otherIndex != index)
                                .Any(other => string.Equals(
                                    GetVpdAssetPath(other), nextPath,
                                    StringComparison.OrdinalIgnoreCase));
                            if (duplicate)
                            {
                                ShowDialog(
                                    L("VPD文件", "VPD Files", "VPDファイル"),
                                    L("该VPD已在列表中。",
                                        "That VPD is already in the list.",
                                        "そのVPDはすでにリストにあります。"));
                            }
                            else
                            {
                                vpdAssets[index] = next;
                            }
                        }
                    }
                }
                finally
                {
                    EditorGUI.indentLevel = previousIndentLevel;
                }
                if (vpdAssets[index] != null) continue;
                vpdAssets.RemoveAt(index);
                vpdFileFoldouts.RemoveAt(index);
                break;
                }
            }
            finally
            {
                if (useScrollView) EditorGUILayout.EndScrollView();
            }
        }

        Rect dropRect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
        var dropStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
        GUI.Box(
            dropRect,
            L("拖入一个或多个VPD文件",
                "Drop one or more VPD files",
                "1つ以上のVPDファイルをドロップ"),
            dropStyle);
        Event currentEvent = Event.current;
        if (!dropRect.Contains(currentEvent.mousePosition) ||
            (currentEvent.type != EventType.DragUpdated &&
             currentEvent.type != EventType.DragPerform))
            return;

        bool hasVpd = DragAndDrop.objectReferences.OfType<DefaultAsset>().Any(IsVpdAsset);
        DragAndDrop.visualMode = hasVpd
            ? DragAndDropVisualMode.Copy
            : DragAndDropVisualMode.Rejected;
        if (currentEvent.type == EventType.DragPerform && hasVpd)
        {
            DragAndDrop.AcceptDrag();
            foreach (DefaultAsset asset in DragAndDrop.objectReferences.OfType<DefaultAsset>())
                AddVpdAsset(vpdAssets, asset);
            EnsureVpdEntryState();
        }
        currentEvent.Use();
    }

    private bool DrawVpdDataRow(string label, DefaultAsset current, out DefaultAsset next)
    {
        next = current;
        using (new EditorGUILayout.HorizontalScope())
        {
            DefaultAsset selected = (DefaultAsset)EditorGUILayout.ObjectField(
                label, current, typeof(DefaultAsset), false);
            if (selected != current)
            {
                if (selected == null || IsVpdAsset(selected))
                {
                    next = selected;
                }
                else
                {
                    ShowDialog(
                        L("VPD文件", "VPD Files", "VPDファイル"),
                        L("请选择Assets目录中的.vpd文件。",
                            "Select .vpd files inside Assets.",
                            "Assets内の.vpdファイルを選択してください。"));
                }
            }
            if (GUILayout.Button("×", GUILayout.Width(24f))) next = null;
        }
        return next != current;
    }

    private void EnsureVpdEntryState()
    {
        if (vpdAssets == null) vpdAssets = new List<DefaultAsset>();
        if (vpdFileFoldouts == null) vpdFileFoldouts = new List<bool>();
        while (vpdFileFoldouts.Count < vpdAssets.Count)
            vpdFileFoldouts.Add(false);
        while (vpdFileFoldouts.Count > vpdAssets.Count)
            vpdFileFoldouts.RemoveAt(vpdFileFoldouts.Count - 1);
    }

    private void RemoveMissingVpdEntries()
    {
        EnsureVpdEntryState();
        for (int index = vpdAssets.Count - 1; index >= 0; index--)
        {
            if (vpdAssets[index] != null) continue;
            vpdAssets.RemoveAt(index);
            vpdFileFoldouts.RemoveAt(index);
        }
    }

    private void ConvertVpds(
        IReadOnlyList<string> sourcePaths, string modelPath, GameObject targetAvatar)
    {
        if (!TryGetVpdOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The VPD output storage folder must be inside Assets.");

        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        VpdAnimationConverter converter = null;
        try
        {
            bool targetCalibrated = vpdOutputType == MotionOutputType.TargetCalibratedHumanoid;
            converter = new VpdAnimationConverter(
                modelPath,
                targetAvatar,
                targetCalibrated,
                curveReductionError,
                targetUpperLimbInverseSolve);

            for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
            {
                string sourcePath = sourcePaths[fileIndex];
                string outputPath = VpdAnimationConverter.GetOutputPath(
                    sourcePath, outputStorageFolder);
                try
                {
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outputPath) != null &&
                        !ConfirmOverwrite("Overwrite VPD animation", outputPath, "Overwrite"))
                    {
                        skipped++;
                        continue;
                    }

                    double lastProgressUpdate = double.NegativeInfinity;
                    Action<float, string> progress = (value, message) =>
                    {
                        double now = EditorApplication.timeSinceStartup;
                        if (value < 1f && now - lastProgressUpdate < 0.08d) return;
                        lastProgressUpdate = now;
                        if (EditorUtility.DisplayCancelableProgressBar(
                                L("VPD转换", "VPD Conversion", "VPD変換"),
                                $"[{fileIndex + 1}/{sourcePaths.Count}] " +
                                Path.GetFileName(sourcePath) + " — " + message,
                                (fileIndex + Mathf.Clamp01(value)) / Mathf.Max(1f, sourcePaths.Count)))
                            throw new OperationCanceledException("VPD conversion was cancelled.");
                    };

                    lastClip = converter.ConvertToAsset(sourcePath, outputPath, progress);
                    converted++;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(sourcePath) + ": " + exception.Message);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            errors.Add("VPD conversion initialization failed: " + exception.Message);
        }
        finally
        {
            if (converter != null) converter.Dispose();
            EditorUtility.ClearProgressBar();
        }

        ApplyExportedClip(lastClip);
        ShowBatchResult(
            L("VPD转换", "VPD Conversion", "VPD変換"),
            converted, skipped, errors, cancelled);
    }

    private bool TryGetVpdOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            vpdOutputStorageLocation, customVpdOutputStorageFolder, out folder);
    }
}
