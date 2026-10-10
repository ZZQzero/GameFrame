using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GameFrame.Editor.Art
{
    public sealed class ImageCropWindow : EditorWindow
    {
        private static readonly string[] CropLabels = { "仅透明边", "自动（透明 / 黑白空白边）", "不裁剪" };
        private static readonly string[] ResizeLabels = { "不缩放", "指定宽高（等比装入）", "锁定宽度", "锁定高度", "相对原图比例" };

        [SerializeField] private List<Texture2D> sources = new List<Texture2D>();
        [SerializeField] private ImageCropSettings settings = new ImageCropSettings();
        [SerializeField] private DefaultAsset outputFolder;
        [SerializeField] private string outputFileName = "image_crop.png";
        [SerializeField] private bool overwriteSource;
        private Vector2 scroll;
        private Vector2 sourceScroll;
        private Texture2D previewSource;
        private Texture2D previewResult;
        private RectInt previewCrop;
        private string previewError;
        private string lastLog;

        [MenuItem("Tools/GameFrame/Art/Image Crop")]
        public static void Open()
        {
            var window = GetWindow<ImageCropWindow>("图片裁剪");
            window.minSize = new Vector2(460, 640);
            window.SyncSelection(true);
            window.Show();
        }

        [MenuItem("Assets/GameFrame/Image Crop")]
        private static void OpenFromSelection()
        {
            Open();
        }

        [MenuItem("Assets/GameFrame/Image Crop", true)]
        private static bool ValidateSelection()
        {
            foreach (var selected in Selection.objects)
            {
                if (selected is Texture2D)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnDisable()
        {
            ReleasePreview();
        }

        private void ReleasePreview()
        {
            if (previewSource != null)
            {
                DestroyImmediate(previewSource);
                previewSource = null;
            }

            if (previewResult != null)
            {
                DestroyImmediate(previewResult);
                previewResult = null;
            }
        }

        private void SyncSelection(bool replace)
        {
            if (replace)
            {
                sources.Clear();
            }

            foreach (var selected in Selection.objects)
            {
                if (selected is Texture2D texture && !sources.Contains(texture))
                {
                    sources.Add(texture);
                }
            }

            if (sources.Count > 0 && sources[0] != null)
            {
                var path = AssetDatabase.GetAssetPath(sources[0]);
                outputFileName = Path.GetFileNameWithoutExtension(path) + "_crop.png";
                if (outputFolder == null)
                {
                    outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(Path.GetDirectoryName(path));
                }
            }

            UpdatePreview();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("图片裁剪与缩放", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("裁掉空白边后等比缩放，输出 PNG；可多选批量。自动模式仅确定裁剪框，保留框内原像素。", MessageType.Info);
            DrawSources();

            EditorGUI.BeginChangeCheck();
            settings.CropMode = (ImageCropMode)EditorGUILayout.Popup("裁剪方式", (int)settings.CropMode, CropLabels);
            using (new EditorGUI.DisabledScope(settings.CropMode == ImageCropMode.None))
            {
                settings.AlphaThreshold = EditorGUILayout.IntSlider("Alpha 阈值", settings.AlphaThreshold, 0, 254);
                settings.Padding = Mathf.Clamp(EditorGUILayout.IntField("保留边距（原图像素）", settings.Padding), 0, 4096);
            }

            settings.ResizeMode = (ImageResizeMode)EditorGUILayout.Popup("缩放方式", (int)settings.ResizeMode, ResizeLabels);
            if (settings.ResizeMode == ImageResizeMode.Fit || settings.ResizeMode == ImageResizeMode.LockWidth)
            {
                settings.Width = Mathf.Max(1, EditorGUILayout.IntField("目标宽度", settings.Width));
            }

            if (settings.ResizeMode == ImageResizeMode.Fit || settings.ResizeMode == ImageResizeMode.LockHeight)
            {
                settings.Height = Mathf.Max(1, EditorGUILayout.IntField("目标高度", settings.Height));
            }

            if (settings.ResizeMode == ImageResizeMode.RelativeScale)
            {
                settings.Scale = EditorGUILayout.Slider("比例（相对磁盘原图）", settings.Scale, 0.01f, 1f);
            }

            if (EditorGUI.EndChangeCheck())
            {
                ReleasePreview();
                previewError = null;
            }

            EditorGUILayout.Space(8);
            overwriteSource = EditorGUILayout.ToggleLeft("原地覆盖源图（仅 PNG，保留 GUID 和导入设置）", overwriteSource);
            using (new EditorGUI.DisabledScope(overwriteSource))
            {
                outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("输出目录（Assets 内）", outputFolder, typeof(DefaultAsset), false);
                if (sources.Count <= 1)
                {
                    outputFileName = EditorGUILayout.TextField("输出文件名", outputFileName);
                }
                else
                {
                    EditorGUILayout.LabelField("输出文件名", "原文件名 + _crop.png");
                }
            }

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(sources.Count == 0))
            {
                if (GUILayout.Button("更新预览（首张，读取磁盘原图）"))
                {
                    UpdatePreview();
                }
            }

            DrawPreview();
            using (new EditorGUI.DisabledScope(sources.Count == 0 || EditorApplication.isCompiling || EditorApplication.isPlaying))
            {
                if (GUILayout.Button($"裁剪并导出（{sources.Count} 张）", GUILayout.Height(34)))
                {
                    ExportImages();
                }
            }

            if (!string.IsNullOrEmpty(lastLog))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("处理结果", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(lastLog, EditorStyles.textArea, GUILayout.MinHeight(80));
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSources()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"源图（{sources.Count}）", EditorStyles.boldLabel);
                if (GUILayout.Button("同步选中", GUILayout.Width(78)))
                {
                    SyncSelection(true);
                }

                if (GUILayout.Button("追加选中", GUILayout.Width(78)))
                {
                    SyncSelection(false);
                }

                if (GUILayout.Button("清空", GUILayout.Width(48)))
                {
                    sources.Clear();
                    ReleasePreview();
                    previewError = null;
                }
            }

            sourceScroll = EditorGUILayout.BeginScrollView(sourceScroll, GUILayout.Height(90));
            for (var i = 0; i < sources.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var replacement = (Texture2D)EditorGUILayout.ObjectField(sources[i], typeof(Texture2D), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (replacement == null)
                        {
                            sources.RemoveAt(i);
                            UpdatePreview();
                            break;
                        }

                        if (!sources.Contains(replacement))
                        {
                            sources[i] = replacement;
                            UpdatePreview();
                        }
                    }

                    if (GUILayout.Button("移除", GUILayout.Width(48)))
                    {
                        sources.RemoveAt(i);
                        UpdatePreview();
                        break;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
            var added = (Texture2D)EditorGUILayout.ObjectField("添加图片", null, typeof(Texture2D), false);
            if (added != null && !sources.Contains(added))
            {
                sources.Add(added);
                if (sources.Count == 1)
                {
                    var path = AssetDatabase.GetAssetPath(added);
                    outputFileName = Path.GetFileNameWithoutExtension(path) + "_crop.png";
                    if (outputFolder == null)
                    {
                        outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(Path.GetDirectoryName(path));
                    }
                }

                UpdatePreview();
            }
        }

        private void UpdatePreview()
        {
            ReleasePreview();
            previewError = null;
            if (sources.Count == 0)
            {
                return;
            }

            try
            {
                if (sources[0] == null)
                {
                    throw new InvalidOperationException("首张源图已丢失，请重新选择。");
                }

                previewSource = ImageCropProcessor.LoadSource(AssetDatabase.GetAssetPath(sources[0]));
                previewCrop = ImageCropProcessor.FindCropRect(previewSource, settings);
                var size = ImageCropProcessor.GetOutputSize(new Vector2Int(previewSource.width, previewSource.height), previewCrop, settings);
                previewResult = ImageCropProcessor.Process(previewSource, previewCrop, size);
            }
            catch (Exception exception)
            {
                ReleasePreview();
                previewError = exception.Message;
            }

            Repaint();
        }

        private void DrawPreview()
        {
            if (!string.IsNullOrEmpty(previewError))
            {
                EditorGUILayout.HelpBox(previewError, MessageType.Error);
                return;
            }

            if (previewResult == null)
            {
                EditorGUILayout.HelpBox("点击更新预览查看当前设置的裁剪范围和输出尺寸。", MessageType.None);
                return;
            }

            EditorGUILayout.LabelField($"原图 {previewSource.width} × {previewSource.height} → 裁剪 {previewCrop.width} × {previewCrop.height} → 输出 {previewResult.width} × {previewResult.height}", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var sourceArea = GUILayoutUtility.GetRect(100, 180, GUILayout.ExpandWidth(true));
                var resultArea = GUILayoutUtility.GetRect(100, 180, GUILayout.ExpandWidth(true));
                var imageRect = FitRect(sourceArea, previewSource.width, previewSource.height);
                EditorGUI.DrawTextureTransparent(imageRect, previewSource);
                EditorGUI.DrawTextureTransparent(FitRect(resultArea, previewResult.width, previewResult.height), previewResult);
                var cropRect = new Rect(imageRect.x + imageRect.width * previewCrop.x / previewSource.width,
                    imageRect.y + imageRect.height * (previewSource.height - previewCrop.yMax) / previewSource.height,
                    imageRect.width * previewCrop.width / previewSource.width,
                    imageRect.height * previewCrop.height / previewSource.height);
                Handles.BeginGUI();
                Handles.DrawSolidRectangleWithOutline(cropRect, Color.clear, Color.green);
                Handles.EndGUI();
            }
        }

        private static Rect FitRect(Rect area, int width, int height)
        {
            var scale = Mathf.Min(area.width / width, area.height / height);
            return new Rect(area.center.x - width * scale / 2f, area.center.y - height * scale / 2f, width * scale, height * scale);
        }

        internal static string[] BuildOutputPaths(string[] sourcePaths, string folder, string fileName, bool overwrite)
        {
            if (!overwrite && (string.IsNullOrEmpty(folder) ||
                (!folder.StartsWith("Assets/", StringComparison.Ordinal) && folder != "Assets") ||
                !AssetDatabase.IsValidFolder(folder)))
            {
                throw new ArgumentException("请选择 Assets 内的有效输出目录。");
            }

            var outputs = new string[sourcePaths.Length];
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceSet = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sourcePaths.Length; i++)
            {
                var source = sourcePaths[i];
                if (string.IsNullOrEmpty(source) || !source.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(source))
                {
                    throw new ArgumentException($"源图必须是 Assets 内的图片文件：{source}");
                }

                var extension = Path.GetExtension(source);
                if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                    (overwrite || !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException("源图只支持 PNG / JPG / JPEG；原地覆盖只支持 PNG。");
                }

                var name = sourcePaths.Length == 1 ? fileName?.Trim() : Path.GetFileNameWithoutExtension(source) + "_crop.png";
                if (!overwrite)
                {
                    if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\") || name == "." || name == "..")
                    {
                        throw new ArgumentException("输出文件名无效，请填写单个文件名。");
                    }

                    if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        name += ".png";
                    }
                }

                var output = overwrite ? source : folder + "/" + name;
                if (!used.Add(output))
                {
                    throw new ArgumentException($"多张图片的输出路径重复：{output}");
                }

                if (!overwrite && sourceSet.Contains(output))
                {
                    throw new ArgumentException("输出会覆盖选中的源图，请修改文件名或明确勾选原地覆盖。");
                }

                outputs[i] = output;
            }

            return outputs;
        }

        private void ExportImages()
        {
            var log = new StringBuilder();
            var completed = 0;
            try
            {
                var paths = new string[sources.Count];
                for (var i = 0; i < sources.Count; i++)
                {
                    paths[i] = sources[i] != null ? AssetDatabase.GetAssetPath(sources[i]) : null;
                }

                var outputs = BuildOutputPaths(paths, AssetDatabase.GetAssetPath(outputFolder), outputFileName, overwriteSource);
                var existing = new List<string>();
                foreach (var output in outputs)
                {
                    if (File.Exists(output))
                    {
                        existing.Add(output);
                    }
                }

                if (existing.Count > 0 && !EditorUtility.DisplayDialog("确认覆盖图片", $"将覆盖 {existing.Count} 个已有文件，无法通过 Undo 恢复。\n{string.Join("\n", existing)}", "覆盖", "取消"))
                {
                    return;
                }

                for (var i = 0; i < paths.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("图片裁剪", paths[i], (float)i / paths.Length);
                    var size = ImageCropProcessor.Export(paths[i], outputs[i], settings);
                    completed++;
                    log.AppendLine($"[成功] {outputs[i]} ({size.x} × {size.y})");
                }

                UpdatePreview();
            }
            catch (Exception exception)
            {
                log.AppendLine($"[失败] 已完成 {completed} 张，停止后续处理。{exception.Message}");
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                lastLog = log.ToString();
            }
        }
    }
}
