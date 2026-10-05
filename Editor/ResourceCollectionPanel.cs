using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZRAsset.Editor
{
    /// <summary>分组、收集器列表和规则详情三个区域；使用原配置序列化字段，支持 Undo 和旧配置。</summary>
    internal sealed class ResourceCollectionPanel
    {
        private BundleBuildConfig m_config;
        private SerializedObject m_serialized;
        private string m_group = "", m_search = "", m_error;
        private bool m_isRule = true;
        private int m_selected = -1;
        private Vector2 m_groupScroll, m_listScroll, m_detailScroll, m_previewScroll;
        private AssetInfo[] m_preview = Array.Empty<AssetInfo>();
        private readonly List<string> m_previewErrors = new(), m_previewWarnings = new();
        private double m_previewAt;
        private bool m_previewDirty = true;
        private readonly Action m_changed;
        private static readonly string[] s_packingNames = { "单资源独立包", "按所在目录合包", "按合包名称合包", "按资源标签合包" };

        internal ResourceCollectionPanel(Action changed) { m_changed = changed; }

        internal void Bind(BundleBuildConfig value)
        {
            if (m_config == value) {
                return;
            }
            m_config = value;
            m_serialized = m_config == null ? null : new SerializedObject(m_config);
            m_group = ""; m_selected = -1; m_error = null;
            Invalidate();
        }

        internal void Invalidate()
        {
            m_previewDirty = true;
            m_previewAt = EditorApplication.timeSinceStartup + 0.3;
        }

        internal bool Tick()
        {
            if (!m_previewDirty || m_config == null || EditorApplication.timeSinceStartup < m_previewAt) {
                return false;
            }
            m_previewDirty = false;
            if (!string.IsNullOrEmpty(m_group) && !(m_config.Groups?.Any(g => g != null && g.Name == m_group) ?? false)) {
                m_group = "";
            }
            if (HasSelection) {
                m_group = (m_isRule ? m_config.Rules[m_selected].CollectionGroup : m_config.Entries[m_selected].CollectionGroup) ?? "";
            }
            m_previewErrors.Clear(); m_previewWarnings.Clear();
            BundleBuildConfig temporary = ScriptableObject.CreateInstance<BundleBuildConfig>();
            try {
                temporary.Groups = m_config.Groups;
                temporary.PackageName = m_config.PackageName;
                temporary.PackageVersion = m_config.PackageVersion;
                if (HasSelection) {
                    if (m_isRule) {
                        temporary.Rules.Add(m_config.Rules[m_selected]);
                    }
                    else {
                        temporary.Entries.Add(m_config.Entries[m_selected]);
                    }
                }
                m_preview = HasSelection ? AssetCollector.Collect(temporary, m_previewErrors, m_previewWarnings) : Array.Empty<AssetInfo>();
                var inactive = HasSelection && ((m_isRule ? m_config.Rules[m_selected].Disabled : m_config.Entries[m_selected].Disabled) ||
                    (m_config.Groups?.Any(g => g != null && g.Name == m_group && g.Disabled) ?? false));
                if (inactive) { m_previewErrors.Remove("配置没有可构建资源。"); m_previewWarnings.Add("当前收集器已停用，预览为 0 项。"); }
            }
            catch (Exception exception) { m_preview = Array.Empty<AssetInfo>(); m_previewErrors.Add(exception.Message); }
            finally { Object.DestroyImmediate(temporary); }
            return true;
        }

        private bool HasSelection
        {
            get
            {
                return m_config != null && m_selected >= 0 &&
            (m_isRule ? m_selected < (m_config.Rules?.Count ?? 0) && m_config.Rules[m_selected] != null
                    : m_selected < (m_config.Entries?.Count ?? 0) && m_config.Entries[m_selected] != null);
            }
        }
        internal void Draw()
        {
            m_serialized.Update();
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("PackageName"), new GUIContent("资源包名称（留空为旧配置）"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("Compression"), new GUIContent("AB 包压缩"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("BuildBackend"), new GUIContent("构建后端"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("ForceRebuild"), new GUIContent("强制重建"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("UseAssetDependencies"), new GUIContent("资源粒度依赖（V6）"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("ExportBinaryManifest"), new GUIContent("导出二进制清单"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("ExportEncodedManifest"), new GUIContent("导出压缩/加密清单"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("ManifestEncryptionKeyId"), new GUIContent("清单加密密钥标识"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("QualityPolicy"), new GUIContent("发布质量策略"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("CollectShadersSeparately"), new GUIContent("着色器独立分包"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("StripUnityVersion"), new GUIContent("剥离 Bundle Unity 版本"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("ShaderVariants"), new GUIContent("着色器变体配置"));
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("DisableWriteTypeTree"), new GUIContent("禁用 TypeTree（需保持脚本兼容）"));
            if (m_serialized.FindProperty("BuildBackend").enumValueIndex == (int)ResourceBuildBackend.ScriptableBuildPipeline) {
                if (!ResourceBuildBackends.IsSbpAvailable) {
                    EditorGUILayout.HelpBox("尚未安装 SBP。请安装 com.unity.scriptablebuildpipeline 2.6.1 或兼容版本，或选择 BuiltIn。", MessageType.Info);
                }

                ResourceEditorChinese.PropertyField(m_serialized.FindProperty("GenerateLinkXml"), new GUIContent("生成 Player 防裁剪配置"));
                ResourceEditorChinese.PropertyField(m_serialized.FindProperty("CacheServerHost"), new GUIContent("缓存服务器（留空为本机）"));
                if (!string.IsNullOrWhiteSpace(m_serialized.FindProperty("CacheServerHost").stringValue)) {
                    ResourceEditorChinese.PropertyField(m_serialized.FindProperty("CacheServerPort"), new GUIContent("缓存服务器端口"));
                }
            }
            ResourceEditorChinese.PropertyField(m_serialized.FindProperty("EncryptionKeyId"), new GUIContent("加密密钥标识（留空不加密）"));
            if (!string.IsNullOrEmpty(m_serialized.FindProperty("PackageName").stringValue)) {
                ResourceEditorChinese.PropertyField(m_serialized.FindProperty("PackageVersion"), new GUIContent("资源包版本"));
            }
            if (m_serialized.ApplyModifiedProperties()) { Invalidate(); m_changed(); }
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(180), GUILayout.ExpandHeight(true))) {
                    DrawGroups();
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(245), GUILayout.ExpandHeight(true))) {
                    DrawList();
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                    DrawDetails();
                }
            }
        }

        private void DrawGroups()
        {
            EditorGUILayout.LabelField("资源分组", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("组织收集器与控制启用状态", EditorStyles.miniLabel);
            m_groupScroll = EditorGUILayout.BeginScrollView(m_groupScroll);
            GroupButton("", "默认分组", false);
            foreach (BundleBuildConfig.CollectionGroup item in m_config.Groups ?? new List<BundleBuildConfig.CollectionGroup>()) {
                if (item == null) {
                    continue;
                }
                GroupButton(item.Name, item.Name, item.Disabled);
            }
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("+ 添加分组")) {
                Change(() => { m_group = ResourceCollectionEditing.AddGroup(m_config); m_selected = -1; });
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_group))) {
                if (GUILayout.Button("移除分组")) {
                    Change(() => { ResourceCollectionEditing.RemoveGroup(m_config, m_group); m_group = ""; m_selected = -1; });
                }
            }

            EditorGUILayout.LabelField("移除分组后，条目保留在默认分组。", EditorStyles.wordWrappedMiniLabel);
        }

        private void GroupButton(string name, string label, bool disabled)
        {
            var count = (m_config.Rules?.Count(r => r != null && (r.CollectionGroup ?? "") == name) ?? 0) +
                (m_config.Entries?.Count(e => e != null && (e.CollectionGroup ?? "") == name) ?? 0);
            var active = name == m_group;
            Color previous = GUI.backgroundColor;
            if (active) {
                GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
            }
            if (GUILayout.Button(label + "  (" + count + ")" + (disabled ? " · 停用" : ""), GUILayout.Height(30)) && !active) {
                m_group = name; m_selected = -1; GUI.FocusControl(null); Invalidate();
            }
            GUI.backgroundColor = previous;
        }

        private void DrawList()
        {
            EditorGUILayout.LabelField("收集器", EditorStyles.boldLabel);
            m_search = EditorGUILayout.TextField(m_search, EditorStyles.toolbarSearchField);
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button("+ 目录")) {
                    Change(() =>
                {
                    Undo.RecordObject(m_config, "添加目录收集器");
                    m_config.Rules ??= new List<BundleBuildConfig.CollectionRule>();
                    m_config.Rules.Add(new BundleBuildConfig.CollectionRule { CollectionGroup = m_group });
                    m_isRule = true; m_selected = m_config.Rules.Count - 1; EditorUtility.SetDirty(m_config);
                });
                }
                if (GUILayout.Button("+ 资源")) {
                    Change(() =>
                                {
                                    Undo.RecordObject(m_config, "添加显式资源");
                                    m_config.Entries ??= new List<BundleBuildConfig.Entry>();
                                    m_config.Entries.Add(new BundleBuildConfig.Entry { CollectionGroup = m_group });
                                    m_isRule = false; m_selected = m_config.Entries.Count - 1; EditorUtility.SetDirty(m_config);
                                });
                }
            }
            m_listScroll = EditorGUILayout.BeginScrollView(m_listScroll);
            EditorGUILayout.LabelField("目录规则", EditorStyles.miniBoldLabel);
            for (var i = 0; i < (m_config.Rules?.Count ?? 0); i++) {
                BundleBuildConfig.CollectionRule item = m_config.Rules[i]; if (item == null || (item.CollectionGroup ?? "") != m_group) {
                    continue;
                }
                DrawRow(true, i, item.Folder, item.AddressPrefix, item.Disabled);
            }
            GUILayout.Space(8);
            EditorGUILayout.LabelField("显式资源", EditorStyles.miniBoldLabel);
            for (var i = 0; i < (m_config.Entries?.Count ?? 0); i++) {
                BundleBuildConfig.Entry item = m_config.Entries[i]; if (item == null || (item.CollectionGroup ?? "") != m_group) {
                    continue;
                }
                DrawRow(false, i, item.Asset, item.Address, item.Disabled);
            }
            EditorGUILayout.EndScrollView();
            Rect drop = GUILayoutUtility.GetRect(10, 64, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "拖入 Project 目录或资源\n自动添加到当前分组", EditorStyles.helpBox);
            HandleDrop(drop);
        }

        private void DrawRow(bool rule, int index, Object asset, string address, bool disabled)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            var title = asset == null ? (rule ? "请选择目录" : "请选择资源") : asset.name;
            if (!string.IsNullOrEmpty(m_search) && (title + path + address).IndexOf(m_search, StringComparison.OrdinalIgnoreCase) < 0) {
                return;
            }
            var active = m_selected == index && m_isRule == rule;
            Color previous = GUI.backgroundColor;
            if (active) {
                GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
            }
            var style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
            var content = new GUIContent((disabled ? "○ " : "● ") + title + "\n" +
                (rule ? "目录 · " : "地址 · ") + (string.IsNullOrEmpty(address) ? "未设置" : address), path);
            if (GUILayout.Button(content, style, GUILayout.Height(48)) && !active) { m_selected = index; m_isRule = rule; GUI.FocusControl(null); Invalidate(); }
            GUI.backgroundColor = previous;
        }

        private void HandleDrop(Rect rect)
        {
            Event evt = Event.current;
            if (!rect.Contains(evt.mousePosition) || EditorApplication.isPlayingOrWillChangePlaymode) {
                return;
            }
            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform) {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform) {
                    DragAndDrop.AcceptDrag();
                    Change(() =>
                    {
                        var added = ResourceCollectionEditing.AddObjects(m_config, m_group, DragAndDrop.objectReferences);
                        if (added == 0) {
                            m_error = "没有新增条目：请拖入 Assets 下的可打包主资源或目录，已存在路径会跳过。";
                        }
                    });
                }
                evt.Use();
            }
        }

        private void DrawDetails()
        {
            m_detailScroll = EditorGUILayout.BeginScrollView(m_detailScroll);
            DrawGroupSettings();
            if (!string.IsNullOrEmpty(m_error)) {
                EditorGUILayout.HelpBox(m_error, MessageType.Warning);
            }
            GUILayout.Space(10);
            if (!HasSelection) {
                EditorGUILayout.LabelField("选择收集器", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("选择中间列表的条目，在这里编辑规则并预览收集结果。也可以直接拖入目录或资源。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            EditorGUILayout.LabelField(m_isRule ? "目录收集器" : "显式资源", EditorStyles.boldLabel);
            DrawSelectionActions();
            if (!HasSelection) { EditorGUILayout.EndScrollView(); return; }
            m_serialized.Update();
            SerializedProperty property = m_serialized.FindProperty(m_isRule ? "Rules" : "Entries").GetArrayElementAtIndex(m_selected);
            var oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 105;
            SerializedProperty disabled = property.FindPropertyRelative("Disabled");
            disabled.boolValue = !EditorGUILayout.Toggle("启用收集器", !disabled.boolValue);
            DrawGroupPopup();
            if (m_isRule) { DrawRule(property); }
            else {
                DrawEntry(property);
            }
            EditorGUIUtility.labelWidth = oldLabelWidth;
            if (m_serialized.ApplyModifiedProperties()) { m_changed(); Invalidate(); }
            GUILayout.Space(12);
            EditorGUILayout.LabelField("当前收集器预览", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(m_previewDirty ? "正在更新预览…" : $"匹配 {m_preview.Length} 项 · 错误 {m_previewErrors.Count} · 警告 {m_previewWarnings.Count}", EditorStyles.miniLabel);
            foreach (var value in m_previewErrors) {
                EditorGUILayout.HelpBox(value, MessageType.Error);
            }
            foreach (var value in m_previewWarnings) {
                EditorGUILayout.HelpBox(value, MessageType.Warning);
            }
            m_previewScroll = EditorGUILayout.BeginScrollView(m_previewScroll, GUILayout.Height(200));
            foreach (AssetInfo asset in m_preview.Take(100)) {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.SelectableLabel(asset.Address, GUILayout.Height(18));
                    EditorGUILayout.LabelField(new GUIContent(asset.BundleName, asset.BundleName), EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent(asset.AssetPath, asset.AssetPath), EditorStyles.linkLabel)) {
                        EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(asset.AssetPath));
                    }
                }
            }
            EditorGUILayout.EndScrollView();
            if (m_preview.Length > 100) {
                EditorGUILayout.LabelField("显示前 100 项；全部地址与冲突请查看“收集预览”。", EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawGroupSettings()
        {
            var index = m_config.Groups?.FindIndex(g => g != null && g.Name == m_group) ?? -1;
            EditorGUILayout.LabelField(string.IsNullOrEmpty(m_group) ? "默认分组" : m_group, EditorStyles.boldLabel);
            if (index < 0) {
                EditorGUILayout.LabelField("旧配置与未分组条目在此显示。", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            var renamed = EditorGUILayout.DelayedTextField("分组名称", m_group);
            if (renamed != m_group) {
                Change(() => { ResourceCollectionEditing.RenameGroup(m_config, m_group, renamed); m_group = renamed.Trim(); });
            }
            m_serialized.Update();
            // RenameGroup 会刷新名称，但保持原列表索引；SerializedObject 负责字段编辑的撤销。
            SerializedProperty property = m_serialized.FindProperty("Groups").GetArrayElementAtIndex(index);
            SerializedProperty disabled = property.FindPropertyRelative("Disabled");
            disabled.boolValue = !EditorGUILayout.Toggle("启用分组", !disabled.boolValue);
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Description"), new GUIContent("备注"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Tags"), new GUIContent("下载标签"), true);
            if (m_serialized.ApplyModifiedProperties()) { m_changed(); Invalidate(); }
            if (m_config.Groups[index].Disabled) {
                EditorGUILayout.HelpBox("此分组已停用，其中的收集器不会进入构建或编辑器模拟。", MessageType.Warning);
            }
        }

        private void DrawGroupPopup()
        {
            var names = new[] { "" }.Concat((m_config.Groups ?? new List<BundleBuildConfig.CollectionGroup>()).Where(g => g != null).Select(g => g.Name)).ToArray();
            var current = m_isRule ? m_config.Rules[m_selected].CollectionGroup : m_config.Entries[m_selected].CollectionGroup;
            var index = Array.IndexOf(names, current ?? "");
            var next = EditorGUILayout.Popup("所属分组", Math.Max(0, index), names.Select(n => n == "" ? "默认分组" : n).ToArray());
            if (next != index && index >= 0) {
                // 先提交本帧其它字段，再执行可撤销的分组移动，避免旧序列化快照覆盖新分组。
                m_serialized.ApplyModifiedProperties();
                Change(() => { ResourceCollectionEditing.MoveToGroup(m_config, m_isRule, m_selected, names[next]); m_group = names[next]; });
                m_serialized.Update();
            }
        }

        private void DrawRule(SerializedProperty property)
        {
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Folder"), new GUIContent("收集目录"));
            var path = AssetDatabase.GetAssetPath(property.FindPropertyRelative("Folder").objectReferenceValue);
            if (!string.IsNullOrEmpty(path)) {
                EditorGUILayout.SelectableLabel(path, EditorStyles.miniLabel, GUILayout.Height(18));
            }
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Recursive"), new GUIContent("包含子目录"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("AddressPrefix"), new GUIContent("地址前缀", "地址 = 前缀 + 相对文件路径，保留扩展名"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Extensions"), new GUIContent("扩展名过滤"));
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.PrefixLabel("快速过滤");
                var choice = EditorGUILayout.Popup(0, new[] { "选择预设…", "全部可打包资源", "预制体", "图片", "文本 / 数据", "场景" });
                if (choice != 0) {
                    property.FindPropertyRelative("Extensions").stringValue =
                    new[] { "", "", ".prefab", ".png,.jpg,.jpeg,.tga,.psd", ".txt,.json,.bytes,.asset", ".unity" }[choice];
                }
            }
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("RequiredLabel"), new GUIContent("资源标签过滤"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("FileType"), new GUIContent("文件格式"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Tags"), new GUIContent("下载标签"), true);
            SerializedProperty packing = property.FindPropertyRelative("Packing");
            packing.enumValueIndex = EditorGUILayout.Popup("分包方式", packing.enumValueIndex, s_packingNames);
            if (packing.enumValueIndex == (int)BundlePacking.Together) {
                ResourceEditorChinese.PropertyField(property.FindPropertyRelative("GroupName"), new GUIContent("合包名称", "相同合包名称的规则会合包；与左侧逻辑分组无关"));
            }
            EditorGUILayout.HelpBox("目录规则地址保留扩展名。场景与普通资源会自动分开打包。", MessageType.None);
        }

        private void DrawEntry(SerializedProperty property)
        {
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Asset"), new GUIContent("资源"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Address"), new GUIContent("业务地址"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("BundleName"), new GUIContent("AB 包名称"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("FileType"), new GUIContent("文件格式"));
            ResourceEditorChinese.PropertyField(property.FindPropertyRelative("Tags"), new GUIContent("下载标签"), true);
            EditorGUILayout.HelpBox("AB 包名称留空时按资源 GUID 独立分包；手动名称使用小写 .bundle 文件名。", MessageType.None);
        }

        private void DrawSelectionActions()
        {
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button("定位")) {
                    EditorGUIUtility.PingObject(m_isRule ? m_config.Rules[m_selected].Folder : m_config.Entries[m_selected].Asset);
                }
                if (GUILayout.Button("复制")) {
                    Change(() => { ResourceCollectionEditing.Duplicate(m_config, m_isRule, m_selected); m_selected++; });
                }
                if (GUILayout.Button("移除")) {
                    Change(() =>
                                {
                                    Undo.RecordObject(m_config, "移除资源收集器");
                                    if (m_isRule) { m_config.Rules.RemoveAt(m_selected); }
                                    else {
                                        m_config.Entries.RemoveAt(m_selected);
                                    }
                                    m_selected = -1; EditorUtility.SetDirty(m_config);
                                });
                }
                if (!HasSelection) {
                    return;
                }
                var count = m_isRule ? m_config.Rules.Count : m_config.Entries.Count;
                using (new EditorGUI.DisabledScope(m_selected == 0)) {
                    if (GUILayout.Button("↑", GUILayout.Width(28))) {
                        Reorder(-1);
                    }
                }

                using (new EditorGUI.DisabledScope(m_selected >= count - 1)) {
                    if (GUILayout.Button("↓", GUILayout.Width(28))) {
                        Reorder(1);
                    }
                }
            }
        }

        private void Reorder(int delta)
        {
            Change(() =>
        {
            // 始终只在同分组可见条目间移动；保持跨组条目的原有顺序。
            var candidates = m_isRule
                ? m_config.Rules.Select((r, i) => (name: r?.CollectionGroup ?? "", index: i)).Where(x => x.name == m_group).Select(x => x.index).ToArray()
                : m_config.Entries.Select((e, i) => (name: e?.CollectionGroup ?? "", index: i)).Where(x => x.name == m_group).Select(x => x.index).ToArray();
            int visible = Array.IndexOf(candidates, m_selected), target = visible + delta;
            if (target < 0 || target >= candidates.Length) {
                return;
            }
            Undo.RecordObject(m_config, "调整收集器顺序");
            var next = candidates[target];
            if (m_isRule) { BundleBuildConfig.CollectionRule item = m_config.Rules[m_selected]; m_config.Rules[m_selected] = m_config.Rules[next]; m_config.Rules[next] = item; }
            else { BundleBuildConfig.Entry item = m_config.Entries[m_selected]; m_config.Entries[m_selected] = m_config.Entries[next]; m_config.Entries[next] = item; }
            m_selected = next; EditorUtility.SetDirty(m_config);
        });
        }
        private void Change(Action action)
        {
            try { m_error = null; action(); m_changed(); Invalidate(); }
            catch (Exception exception) { m_error = exception.Message; }
        }
    }
}
