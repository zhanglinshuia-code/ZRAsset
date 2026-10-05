using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>资源工作台：配置入口、预分析、构建报告与可点击的 Bundle 依赖图。</summary>
    public sealed class ResourceBuildWindow: EditorWindow
    {
        [UnityEngine.Serialization.FormerlySerializedAs("config")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private BundleBuildConfig m_config;
        private ResourceBuildReport m_report;
        private Vector2 m_scroll, m_graphScroll;
        private int m_tab;
        private string m_selectedBundle;
        private string m_message;
        private bool m_stale;
        private bool m_showActual = true;
        private bool m_actionPending;
        private ResourceCollectionPanel m_collection;
        private BundleBuildConfig[] m_configurations = Array.Empty<BundleBuildConfig>();
        private string m_previewSearch = "";
        private int m_previewPage;
        private MessageType m_messageType = MessageType.Info;
        private const int PageSize = 100;

        [MenuItem("Tools/ZRAsset/资源收集")]
        [MenuItem("Tools/ZRAsset/资源工作台 (V3)")]
        public static void Open()
        {
            ResourceBuildWindow window = GetWindow<ResourceBuildWindow>("ZRAsset 资源收集");
            window.titleContent = new GUIContent("ZRAsset 资源收集");
            window.minSize = new Vector2(1000, 650);
            if (Selection.activeObject is BundleBuildConfig selected) {
                window.SelectConfig(selected);
            }

            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("ZRAsset 资源收集");
            minSize = new Vector2(1000, 650);
            m_collection = new ResourceCollectionPanel(ConfigurationChanged);
            RefreshConfigurations();
            Undo.undoRedoPerformed += OnUndo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndo;
        }

        private void OnUndo() { ConfigurationChanged(); Repaint(); }
        private void OnProjectChange() { RefreshConfigurations(); ConfigurationChanged(); Repaint(); }
        private void Update()
        {
            if (m_collection != null && m_collection.Tick()) {
                Repaint();
            }
        }
        private void ConfigurationChanged()
        {
            m_stale = m_report != null;
            m_collection?.Invalidate();
        }

        private void RefreshConfigurations()
        {
            m_configurations = AssetDatabase.FindAssets("t:BundleBuildConfig").Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal).Select(AssetDatabase.LoadAssetAtPath<BundleBuildConfig>)
                .Where(c => c != null).ToArray();
        }

        private void SelectConfig(BundleBuildConfig value)
        {
            m_config = value; m_report = null; m_selectedBundle = null; m_message = null; m_stale = false;
            m_previewPage = 0; m_collection?.Bind(m_config);
        }

        private void OnGUI()
        {
            m_collection ??= new ResourceCollectionPanel(ConfigurationChanged);
            DrawToolbar();
            m_collection.Bind(m_config);
            if (!string.IsNullOrEmpty(m_message)) {
                EditorGUILayout.HelpBox(m_message, m_messageType);
            }

            if (m_config == null) {
                EditorGUILayout.HelpBox("选择已有资源配置，或点击“新建配置”。可以把 Project 中的配置拖入顶部资源配置栏。", MessageType.Info);
                if (EditorSimulationSettings.IsEnabled && GUILayout.Button("关闭失效的模拟配置")) {
                    EditorSimulationSettings.Disable();
                }

                return;
            }
            m_tab = GUILayout.Toolbar(m_tab, new[] { "收集配置", "收集预览", "Bundle 依赖图", "共享与重复依赖" }, GUILayout.Height(28));
            if (m_tab == 0) {
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode)) {
                    m_collection.Draw();
                }

                return;
            }
            if (m_report == null) {
                EditorGUILayout.HelpBox("点击“分析 / 校验”生成全部收集器的预览、地址冲突及 Bundle 依赖报告。", MessageType.Info);
                return;
            }
            if (m_stale) {
                EditorGUILayout.HelpBox("配置或资源发生变化，以下是上次快照，请重新分析。", MessageType.Warning);
            }

            EditorGUILayout.LabelField($"地址 {m_report.Assets.Length}  ·  Bundle {m_report.Bundles.Length}  ·  错误 {m_report.Errors.Count}  ·  警告 {m_report.Warnings.Count}", EditorStyles.boldLabel);
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            if (m_tab == 1) {
                DrawOverview();
            }
            else if (m_tab == 2) {
                DrawGraph();
            }
            else {
                DrawShared();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
                var selected = Array.IndexOf(m_configurations, m_config) + 1;
                var labels = new[] { "选择资源配置…" }.Concat(m_configurations.Select(c => c.name)).ToArray();
                var next = EditorGUILayout.Popup(Math.Max(0, selected), labels, EditorStyles.toolbarPopup, GUILayout.Width(210));
                if (next != selected) {
                    SelectConfig(next == 0 ? null : m_configurations[next - 1]);
                }

                EditorGUI.BeginChangeCheck();
                var picked = (BundleBuildConfig)EditorGUILayout.ObjectField(m_config, typeof(BundleBuildConfig), false, GUILayout.MinWidth(150));
                if (EditorGUI.EndChangeCheck()) {
                    SelectConfig(picked);
                }

                using (new EditorGUI.DisabledScope(m_actionPending || EditorApplication.isPlayingOrWillChangePlaymode)) {
                    if (GUILayout.Button("新建配置", EditorStyles.toolbarButton, GUILayout.Width(70))) {
                        Run(() =>
                    {
                        var path = EditorUtility.SaveFilePanelInProject("新建资源配置", "ZRAssetBuildConfig", "asset", "选择配置保存位置");
                        if (path.Length == 0) {
                            return;
                        }

                        BundleBuildConfig created = ScriptableObject.CreateInstance<BundleBuildConfig>();
                        AssetDatabase.CreateAsset(created, path); AssetDatabase.SaveAssetIfDirty(created);
                        RefreshConfigurations(); SelectConfig(created); Selection.activeObject = created;
                    });
                    }

                    using (new EditorGUI.DisabledScope(m_config == null)) {
                        if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(45))) {
                            Run(() => AssetDatabase.SaveAssetIfDirty(m_config));
                        }
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Label("目标：" + EditorUserBuildSettings.activeBuildTarget, GUILayout.Width(200));
                GUILayout.Label(EditorSimulationSettings.IsEnabled ? "Editor 模拟模式" : "真实 Bundle 模式", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(m_actionPending || m_config == null || EditorApplication.isPlayingOrWillChangePlaymode)) {
                    if (GUILayout.Button("分析 / 校验", GUILayout.Width(95))) {
                        Run(Analyze);
                    }

                    if (GUILayout.Button("构建", GUILayout.Width(55))) {
                        Run(Build);
                    }

                    if (GUILayout.Button("更多 ▾", GUILayout.Width(70))) {
                        ShowActions();
                    }
                }
            }
            if (m_config != null) {
                var serialized = new SerializedObject(m_config);
                serialized.Update();
                ResourceEditorChinese.PropertyField(serialized.FindProperty("ExtractSharedDependencies"), new GUIContent("自动提取多包共享依赖"));
                if (serialized.ApplyModifiedProperties()) {
                    ConfigurationChanged();
                }
            }
        }

        private void ShowActions()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("拷贝当前平台构建到 StreamingAssets"), false, () => Run(() =>
            {
                BundleBuilder.CopyBuild(BundleBuilder.GetDefaultOutput(m_config, EditorUserBuildSettings.activeBuildTarget),
                    BundleBuilder.GetDefaultBuiltInRoot(m_config), EditorUserBuildSettings.activeBuildTarget);
                AssetDatabase.Refresh();
                m_message = "已拷贝当前平台构建到：" + Path.Combine(Application.streamingAssetsPath, "ZRAsset");
            }));
            menu.AddItem(new GUIContent("使用此配置模拟"), false, () => Run(() =>
            { EditorSimulationSettings.Enable(m_config); m_message = "模拟模式已启用，下次创建管理器生效。"; }));
            menu.AddItem(new GUIContent("切回真实 Bundle"), false, () => Run(() =>
            { EditorSimulationSettings.Disable(); m_message = "真实模式已启用，下次创建管理器生效。"; }));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("导出分析 JSON"), false, () => Run(() =>
            {
                Analyze();
                var path = Path.Combine(BundleBuilder.GetDefaultOutput(m_config, EditorUserBuildSettings.activeBuildTarget), "analysis-report.json");
                m_report.Save(path); m_message = "已导出：" + path;
            }));
            menu.AddItem(new GUIContent("定位当前配置"), false, () => EditorGUIUtility.PingObject(m_config));
            menu.ShowAsContext();
        }

        private void Run(Action action)
        {
            if (m_actionPending) {
                return;
            }

            m_actionPending = true;
            // 构建、资产导入和对话框可能重置 IMGUI 状态，必须在当前布局作用域全部退出后执行。
            EditorApplication.delayCall += () =>
            {
                if (this == null) {
                    return;
                }

                try { m_messageType = MessageType.Info; action(); }
                catch (ExitGUIException) { throw; }
                catch (Exception exception) { m_message = exception.Message; m_messageType = MessageType.Error; Debug.LogException(exception); }
                finally { m_actionPending = false; Repaint(); }
            };
        }

        private void Analyze()
        {
            m_report = ResourceBuildAnalyzer.Analyze(m_config, EditorUserBuildSettings.activeBuildTarget).Report;
            m_stale = false;
            m_previewPage = 0;
            m_messageType = m_report.IsValid ? MessageType.Info : MessageType.Error;
            m_message = m_report.IsValid ? "预分析通过。依赖图为源资源预测，真实构建后以 Unity 清单为准。" : "校验失败，请查看错误列表。";
        }

        private void Build()
        {
            Analyze();
            if (!m_report.IsValid) {
                return;
            }

            var output = BundleBuilder.GetDefaultOutput(m_config, EditorUserBuildSettings.activeBuildTarget);
            BundleBuilder.Build(m_config, output, EditorUserBuildSettings.activeBuildTarget);
            m_report = JsonUtility.FromJson<ResourceBuildReport>(File.ReadAllText(Path.Combine(output, "build-report.json")));
            m_stale = false;
            m_message = "构建完成，完整报告：" + output + "/build-report.json";
        }

        private void DrawOverview()
        {
            foreach (var error in m_report.Errors) {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            foreach (var warning in m_report.Warnings) {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            EditorGUILayout.LabelField("已完成阶段", string.Join(" → ", m_report.CompletedSteps.Select(ResourceEditorChinese.Text)));
            EditorGUILayout.LabelField("构建后端", ResourceEditorChinese.Text(m_report.BuildBackend.ToString()) + (string.IsNullOrEmpty(m_report.BackendVersion) ? "" : " / " + m_report.BackendVersion));
            EditorGUILayout.LabelField("构建策略", m_report.ForceRebuild ? "强制重建" : m_report.BuildBackend == ResourceBuildBackend.ScriptableBuildPipeline ? "允许 SBP 缓存复用" : "内置管线增量缓存");
            if (m_report.BuildBackend == ResourceBuildBackend.BuiltIn && m_report.NativeBuildExecuted) {
                EditorGUILayout.LabelField("缓存校验", m_report.BuiltinCacheRestored ? "已恢复上次完整缓存" : "冷构建 / 强制重建");
                EditorGUILayout.LabelField("内容未变化 Bundle", m_report.BuiltinUnchangedBundleCount.ToString());
            }
            if (m_report.Built) {
                EditorGUILayout.LabelField("原生 Bundle 构建", m_report.NativeBuildExecuted ? $"{m_report.NativeBuildMilliseconds:F1} ms · {m_report.DependencyScope} 依赖" : "无原生 Bundle，已跳过");
            }

            EditorGUILayout.LabelField("加密", string.IsNullOrEmpty(m_report.EncryptionKeyId) ? "未启用" : "密钥标识：" + m_report.EncryptionKeyId);
            EditorGUILayout.HelpBox("源文件大小仅供打包分析，不等于内存占用。Shader、SpriteAtlas 等实际依赖请检查构建报告。", MessageType.None);
            EditorGUI.BeginChangeCheck();
            m_previewSearch = EditorGUILayout.TextField("搜索地址 / 路径 / Bundle", m_previewSearch);
            if (EditorGUI.EndChangeCheck()) {
                m_previewPage = 0;
            }

            AssetInfo[] matches = m_report.Assets.Where(a => string.IsNullOrEmpty(m_previewSearch) ||
                (a.Address + a.AssetPath + a.BundleName).IndexOf(m_previewSearch, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var pages = Math.Max(1, (matches.Length + PageSize - 1) / PageSize);
            m_previewPage = Math.Min(m_previewPage, pages - 1);
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"匹配 {matches.Length} 项 · 第 {m_previewPage + 1} / {pages} 页");
                using (new EditorGUI.DisabledScope(m_previewPage == 0)) {
                    if (GUILayout.Button("上一页", GUILayout.Width(65))) {
                        m_previewPage--;
                    }
                }

                using (new EditorGUI.DisabledScope(m_previewPage >= pages - 1)) {
                    if (GUILayout.Button("下一页", GUILayout.Width(65))) {
                        m_previewPage++;
                    }
                }
            }
            foreach (AssetInfo asset in matches.Skip(m_previewPage * PageSize).Take(PageSize)) {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(asset.Address, asset.Kind == ResourceKind.RawFile ? ResourceEditorChinese.Text(asset.FileType.ToString()) : asset.Kind == ResourceKind.Scene ? "场景" : "资源");
                    DrawPath(asset.AssetPath);
                    EditorGUILayout.SelectableLabel(asset.BundleName, GUILayout.Height(18));
                }
            }
        }

        private void DrawShared()
        {
            EditorGUILayout.LabelField("多包共享依赖（自动提取前的消费者）", EditorStyles.boldLabel);
            foreach (SharedDependency shared in m_report.SharedDependencies) {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    DrawPath(shared.AssetPath);
                    EditorGUILayout.LabelField(shared.Extracted ? "已提取独立 shared 包" : "未提取，可能重复打包");
                    foreach (var consumer in shared.Consumers) {
                        EditorGUILayout.SelectableLabel(consumer, GUILayout.Height(18));
                    }
                }
            }
            EditorGUILayout.LabelField($"分包后仍然重复：{m_report.RemainingDuplicates.Length}", EditorStyles.boldLabel);
            foreach (SharedDependency duplicate in m_report.RemainingDuplicates) {
                DrawPath(duplicate.AssetPath);
                EditorGUILayout.HelpBox(string.Join("\n", duplicate.Consumers), MessageType.Warning);
            }
        }

        private void DrawGraph()
        {
            if (m_report.Built) {
                m_showActual = EditorGUILayout.Toggle("显示 Unity 实际依赖", m_showActual);
            }

            EditorGUILayout.LabelField("连线方向：右侧使用者 → 左侧依赖。点击节点查看包内资源。");
            Dictionary<string, string[]> edges = m_report.Built && m_showActual
                ? m_report.ActualBundles.ToDictionary(b => b.Name, b => b.Dependencies)
                : m_report.Bundles.ToDictionary(b => b.Name, b => b.Dependencies);
            var levels = new Dictionary<string, int>();
            var visiting = new HashSet<string>();
            foreach (var name in edges.Keys) {
                Level(name);
            }

            var rectangles = new Dictionary<string, Rect>();
            var rows = new Dictionary<int, int>();
            foreach (var name in edges.Keys.OrderBy(n => n, StringComparer.Ordinal)) {
                var level = levels[name];
                rows.TryGetValue(level, out var row);
                rectangles[name] = new Rect(15 + (level * 275), 15 + (row * 85), 230, 60);
                rows[level] = row + 1;
            }
            Rect viewport = GUILayoutUtility.GetRect(0, 340, GUILayout.ExpandWidth(true));
            var canvas = new Rect(0, 0, Math.Max(viewport.width - 20, 40 + ((levels.Count == 0 ? 1 : levels.Values.Max() + 1) * 275)),
                Math.Max(320, 30 + ((rows.Count == 0 ? 1 : rows.Values.Max()) * 85)));
            m_graphScroll = GUI.BeginScrollView(viewport, m_graphScroll, canvas);
            foreach (KeyValuePair<string, string[]> pair in edges) {
                foreach (var dependency in pair.Value) {
                    if (rectangles.TryGetValue(dependency, out Rect target)) {
                        Rect source = rectangles[pair.Key];
                        var start = new Vector3(source.xMin, source.center.y);
                        var end = new Vector3(target.xMax, target.center.y);
                        Handles.DrawBezier(start, end, start + (Vector3.left * 55), end + (Vector3.right * 55), Color.gray, null, 2);
                    }
                }
            }

            var style = new GUIStyle(GUI.skin.button) { wordWrap = true };
            foreach (KeyValuePair<string, Rect> pair in rectangles) {
                Color previous = GUI.backgroundColor;
                if (pair.Key == m_selectedBundle) {
                    GUI.backgroundColor = new Color(0.3f, 0.8f, 1);
                }

                if (GUI.Button(pair.Value, pair.Key, style)) {
                    m_selectedBundle = pair.Key;
                }

                GUI.backgroundColor = previous;
            }
            GUI.EndScrollView();
            BundleAnalysis selected = m_report.Bundles.FirstOrDefault(b => b.Name == m_selectedBundle);
            if (selected != null) {
                EditorGUILayout.LabelField(selected.Name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"源文件：{EditorUtility.FormatBytes(selected.SourceBytes)}    构建文件：{(m_report.Built ? EditorUtility.FormatBytes(selected.BuiltBytes) : "未构建")}");
                foreach (var path in selected.IncludedAssets) {
                    DrawPath(path);
                }
            }

            int Level(string name)
            {
                if (levels.TryGetValue(name, out var known)) {
                    return known;
                }

                if (!visiting.Add(name) || !edges.ContainsKey(name)) {
                    return 0;
                }

                var depth = edges[name].Length == 0 ? 0 : edges[name].Max(Level) + 1;
                visiting.Remove(name);
                return levels[name] = depth;
            }
        }

        private static void DrawPath(string path)
        {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.SelectableLabel(path, GUILayout.Height(18));
                if (GUILayout.Button("定位", GUILayout.Width(48))) {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
                }
            }
        }
    }

    [CustomEditor(typeof(BundleBuildConfig))]
    internal sealed class BundleBuildConfigInspector: UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("在资源收集窗口中管理分组、目录规则和显式资源，支持拖入、复制、预览与撤销。", MessageType.Info);
            if (GUILayout.Button("打开资源收集窗口", GUILayout.Height(30))) { Selection.activeObject = target; ResourceBuildWindow.Open(); }
            ResourceEditorChinese.DrawInspector(serializedObject);
        }
    }
}
