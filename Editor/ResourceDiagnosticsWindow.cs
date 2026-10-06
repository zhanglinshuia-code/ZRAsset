using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>窗口只持有纯数据快照，关闭窗口或切换选择不会影响资源引用。</summary>
    public sealed class ResourceDiagnosticsWindow: EditorWindow
    {
        private ResourceDiagnosticManager[] m_managers = Array.Empty<ResourceDiagnosticManager>();
        private ResourceDiagnosticSnapshot m_snapshot;
        private long m_selectedId;
        private Vector2 m_scroll;
        private string m_filter = "", m_message;
        private int m_tab;
        private bool m_autoRefresh;
        private double m_nextRefresh;

        [MenuItem("ZRAsset/运行时资源诊断")]
        public static void Open()
        {
            ResourceDiagnosticsWindow window = GetWindow<ResourceDiagnosticsWindow>("ZRAsset 资源诊断");
            window.minSize = new Vector2(760, 440);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshSnapshot();
        }

        private void Update()
        {
            if (!m_autoRefresh || EditorApplication.timeSinceStartup < m_nextRefresh) {
                return;
            }

            m_nextRefresh = EditorApplication.timeSinceStartup + 1;
            RefreshSnapshot();
            Repaint();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(55))) {
                    RefreshSnapshot();
                }

                m_autoRefresh = GUILayout.Toggle(m_autoRefresh, "每秒刷新", EditorStyles.toolbarButton, GUILayout.Width(75));
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(m_snapshot == null)) {
                    if (GUILayout.Button("导出 JSON", EditorStyles.toolbarButton, GUILayout.Width(90))) {
                        ExportSnapshot();
                    }
                }
            }
            if (m_managers.Length == 0) {
                EditorGUILayout.HelpBox("尚无可观察的资源管理器。运行资源示例后点击刷新。", MessageType.Info);
                if (!string.IsNullOrEmpty(m_message)) {
                    EditorGUILayout.HelpBox(m_message, MessageType.Info);
                }

                return;
            }
            var index = Array.FindIndex(m_managers, m => m.Id == m_selectedId);
            var selected = EditorGUILayout.Popup("资源管理器", Math.Max(0, index), m_managers.Select(m => m.Name + " (#" + m.Id + ")").ToArray());
            if (selected != index) { m_selectedId = m_managers[selected].Id; RefreshSnapshot(); }
            if (!string.IsNullOrEmpty(m_message)) {
                EditorGUILayout.HelpBox(m_message, MessageType.Info);
            }

            if (m_snapshot == null) {
                return;
            }

            ResourceDiagnosticSummary stats = m_snapshot.Summary;
            EditorGUILayout.LabelField($"资源 {stats.Assets}  ·  Bundle {stats.Bundles}  ·  使用凭证 {stats.Handles}  ·  实例 {stats.Instances}  ·  场景 {stats.Scenes}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"资源加载 {stats.ActiveAssetLoads} / 等待 {stats.QueuedAssetLoads}    Bundle 加载 {stats.ActiveBundleLoads} / 等待 {stats.QueuedBundleLoads}    下载 {stats.ActiveDownloads} / 等待 {stats.QueuedDownloads}");
            EditorGUILayout.LabelField("采集时间", m_snapshot.CapturedAtUtc);
            EditorGUILayout.LabelField($"原始文件 {stats.RawFiles}  ·  文件/流引用 {stats.RawReferences}");
            m_filter = EditorGUILayout.TextField("筛选地址或名称", m_filter);
            m_tab = GUILayout.Toolbar(m_tab, new[] { "资源与引用", "Bundle 与依赖", "实例与场景", "下载与错误", "业务作用域" });
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            if (m_tab == 0) { DrawAssets(); DrawRawFiles(); }
            else if (m_tab == 1) {
                DrawBundles();
            }
            else if (m_tab == 2) {
                DrawLifetimes();
            }
            else if (m_tab == 3) {
                DrawDownloads();
            }
            else {
                DrawScopes();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawScopes()
        {
            foreach (ResourceScopeDiagnostic scope in m_snapshot.Scopes ?? Array.Empty<ResourceScopeDiagnostic>()) {
                if (!Matches(scope.Name) && !scope.Entries.Any(entry => Matches(entry.Address))) {
                    continue;
                }
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(scope.Name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("状态 / 登记条目", (scope.Closing ? "关闭中" : "使用中") + " / " + scope.Entries.Length);
                    foreach (ResourceScopeEntryDiagnostic entry in scope.Entries) {
                        EditorGUILayout.LabelField(entry.Kind, entry.Address);
                        if (!string.IsNullOrEmpty(entry.CreationStack)) {
                            EditorGUILayout.SelectableLabel(entry.CreationStack, EditorStyles.wordWrappedMiniLabel,
                                GUILayout.MinHeight(60));
                        }
                    }
                }
            }
        }

        private void DrawRawFiles()
        {
            foreach (ResourceRawFileDiagnostic item in m_snapshot.RawFiles ?? Array.Empty<ResourceRawFileDiagnostic>()) {
                if (!Matches(item.AssetPath) && !item.Addresses.Any(address => Matches(address))) {
                    continue;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(string.Join(", ", item.Addresses), EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("原始文件 / 引用", $"{ResourceEditorChinese.Text(item.FileType)} / {item.References} / {ResourceEditorChinese.Text(item.State)}");
                    EditorGUILayout.LabelField("容器", item.Container);
                    if (!string.IsNullOrEmpty(item.Encryption)) {
                        EditorGUILayout.LabelField("加密 / 密钥标识", item.Encryption + " / " + item.EncryptionKeyId);
                    }

                    EditorGUILayout.LabelField("偏移 / 长度", $"{item.Offset} / {item.Length}");
                    DrawError(item.Error);
                }
            }
        }

        private void RefreshSnapshot()
        {
            try {
                m_managers = ResourceDiagnostics.GetManagers();
                if (!m_managers.Any(m => m.Id == m_selectedId)) {
                    m_selectedId = m_managers.Length == 0 ? 0 : m_managers[0].Id;
                }

                if (m_selectedId == 0 || !ResourceDiagnostics.TryCapture(m_selectedId, out m_snapshot)) {
                    m_snapshot = null;
                }
            }
            catch (Exception exception) { m_snapshot = null; m_message = "采集失败：" + exception.Message; }
        }

        private void ExportSnapshot()
        {
            var path = EditorUtility.SaveFilePanel("导出资源诊断快照", "", "zrasset-diagnostics.json", "json");
            if (string.IsNullOrEmpty(path)) {
                return;
            }

            try {
                // 导出当前显示的快照，便于把同一时刻的诊断记录交给其他开发者。
                File.WriteAllText(path, m_snapshot.ToJson(), new UTF8Encoding(false));
                m_message = "已导出：" + path;
            }
            catch (Exception exception) { m_message = "导出失败：" + exception.Message; }
        }

        private void DrawAssets()
        {
            foreach (ResourceAssetDiagnostic item in m_snapshot.Assets) {
                var addresses = string.Join(", ", item.Addresses);
                if (!Matches(item.AssetPath ?? item.SourceBundle, addresses)) {
                    continue;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(addresses, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("资源路径 / 包", item.AssetPath ?? item.SourceBundle);
                    EditorGUILayout.LabelField("加载范围", ResourceEditorChinese.Text(item.LoadKind));
                    EditorGUILayout.LabelField("类型", item.AssetType);
                    EditorGUILayout.LabelField("状态 / 使用凭证", $"{ResourceEditorChinese.Text(item.State)} / {item.References}");
                    EditorGUILayout.LabelField("依赖 Bundle", string.Join(", ", item.BundleNames));
                    if (item.References == 0) {
                        EditorGUILayout.LabelField("回收", item.Loading ? "加载结束后可检查回收" : item.CanCollect ? "下次回收时释放" : $"剩余延迟 {item.SecondsUntilCollection:F1} 秒");
                    }

                    DrawError(item.Error);
                }
            }
        }

        private void DrawBundles()
        {
            EditorGUILayout.HelpBox("Bundle 引用来自资源和场景；文件大小是磁盘数据量。", MessageType.None);
            foreach (ResourceBundleDiagnostic item in m_snapshot.Bundles) {
                if (!Matches(item.Name)) {
                    continue;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(item.Name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("状态 / 引用", $"{ResourceEditorChinese.Text(item.State)} / {item.References}");
                    EditorGUILayout.LabelField("文件大小", EditorUtility.FormatBytes(item.FileBytes));
                    if (!string.IsNullOrEmpty(item.Encryption)) {
                        EditorGUILayout.LabelField("加密 / 密钥标识", item.Encryption + " / " + item.EncryptionKeyId);
                        EditorGUILayout.LabelField("明文容器大小", EditorUtility.FormatBytes(item.ContentBytes));
                    }
                    EditorGUILayout.LabelField("直接依赖", string.Join(", ", item.Dependencies));
                    DrawError(item.Error);
                }
            }
        }

        private void DrawLifetimes()
        {
            foreach (ResourceInstanceDiagnostic item in m_snapshot.Instances) {
                if (!Matches(item.Address, item.Name)) {
                    continue;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField("实例 · " + item.Address, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("状态", ResourceEditorChinese.Text(item.State));
                    if (item.InstanceId != 0) {
                        EditorGUILayout.LabelField("对象", item.Name + " (#" + item.InstanceId + ")");
                    }

                    DrawError(item.Error);
                }
            }
            foreach (ResourceSceneDiagnostic item in m_snapshot.Scenes) {
                var addresses = string.Join(", ", item.Addresses);
                if (!Matches(item.AssetPath, addresses)) {
                    continue;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField("场景 · " + addresses, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("路径 / 状态", item.AssetPath + " / " + ResourceEditorChinese.Text(item.State));
                    EditorGUILayout.LabelField("依赖 Bundle", string.Join(", ", item.BundleNames));
                    DrawError(item.Error);
                }
            }
        }

        private void DrawDownloads()
        {
            foreach (ResourceDownloadDiagnostic item in m_snapshot.Downloads) {
                if (!Matches(item.BundleName)) {
                    continue;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(item.BundleName, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("状态 / 尝试", $"{ResourceEditorChinese.Text(item.State)} / {item.Attempt}");
                    EditorGUILayout.LabelField("已接收 / 总计", EditorUtility.FormatBytes(item.ReceivedBytes) + " / " + EditorUtility.FormatBytes(item.TotalBytes));
                    DrawError(item.Error);
                }
            }
            foreach (ResourceAssetDiagnostic item in m_snapshot.Assets) {
                if (!string.IsNullOrEmpty(item.Error) && Matches(item.AssetPath ?? item.SourceBundle, string.Join(", ", item.Addresses))) {
                    DrawError((item.AssetPath ?? item.SourceBundle) + "\n" + item.Error);
                }
            }

            foreach (ResourceBundleDiagnostic item in m_snapshot.Bundles) {
                if (!string.IsNullOrEmpty(item.Error) && Matches(item.Name)) {
                    DrawError(item.Name + "\n" + item.Error);
                }
            }
        }

        private bool Matches(params string[] values)
        {
            return string.IsNullOrEmpty(m_filter) || values.Any(v => v != null && v.IndexOf(m_filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void DrawError(string error)
        {
            if (!string.IsNullOrEmpty(error)) {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
        }
    }
}
