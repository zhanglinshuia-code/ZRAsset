using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using ZRAsset.HotUpdate;

namespace ZRAsset.Editor
{
    /// <summary>明确展示接入缺口，仅用户点击时创建配置或登记其选定的 asmdef；不安装、不联网、不改场景。</summary>
    public sealed class HotUpdateStartupWindow: EditorWindow
    {
        [UnityEngine.Serialization.FormerlySerializedAs("startup")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private HotUpdateStartupConfig m_startup;
        [UnityEngine.Serialization.FormerlySerializedAs("assembly")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private AssemblyDefinitionAsset m_assembly;
        private string m_report, m_message;
        private Vector2 m_scroll;

        [MenuItem("Tools/ZRAsset/热更新/启动配置与就绪检查 (V9)")]
        public static void Open()
        {
            HotUpdateStartupWindow window = GetWindow<HotUpdateStartupWindow>("ZRAsset 热更接入");
            window.minSize = new Vector2(680, 440);
            if (Selection.activeObject is HotUpdateStartupConfig config) {
                window.m_startup = config;
            }

            window.Show();
        }
        private void OnEnable()
        {
            RefreshReport();
        }

        private void OnGUI()
        {
            m_startup = (HotUpdateStartupConfig)EditorGUILayout.ObjectField("启动配置", m_startup, typeof(HotUpdateStartupConfig), false);
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button("创建启动配置")) {
                    CreateConfiguration();
                }

                using (new EditorGUI.DisabledScope(m_startup == null)) {
                    if (GUILayout.Button("检查启动配置")) {
                        Run(() => { m_startup.CreateOptions(); m_message = "配置结构通过；请在 Inspector 填入真实发布地址、公钥与宿主标识。"; });
                    }

                    if (GUILayout.Button("在 Inspector 编辑")) {
                        Selection.activeObject = m_startup;
                    }
                }
                if (GUILayout.Button("刷新环境")) {
                    RefreshReport();
                }
            }
            EditorGUILayout.Space();
            m_assembly = (AssemblyDefinitionAsset)EditorGUILayout.ObjectField("已有业务热更程序集", m_assembly, typeof(AssemblyDefinitionAsset), false);
            using (new EditorGUI.DisabledScope(m_assembly == null || EditorApplication.isPlayingOrWillChangePlaymode)) {
                if (GUILayout.Button("将所选 asmdef 加入 HybridCLR 热更列表")) {
                    Run(() => { HotUpdateStartupTools.RegisterHotAssembly(m_assembly); m_message = "已登记所选程序集。重新 Generate/All 后构建对应 Player。"; RefreshReport(); });
                }
            }

            EditorGUILayout.HelpBox("启动组件位于 ZRAsset.Samples.HotUpdateBootstrapComponent，可手动挂到已有启动场景根对象。业务入口由 热更构建配置和签名资源决定。", MessageType.Info);
            if (!string.IsNullOrEmpty(m_message)) {
                EditorGUILayout.HelpBox(m_message, MessageType.Info);
            }

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            EditorGUILayout.SelectableLabel(m_report ?? "尚未检查", EditorStyles.wordWrappedLabel, GUILayout.ExpandHeight(true), GUILayout.MinHeight(420));
            EditorGUILayout.EndScrollView();
        }
        private void CreateConfiguration()
        {
            var path = EditorUtility.SaveFilePanelInProject("创建热更启动配置", "ZRAssetStartupConfig", "asset", "选择主包配置的保存位置");
            if (string.IsNullOrEmpty(path)) {
                return;
            }

            Run(() =>
            {
                HotUpdateStartupConfig value = CreateInstance<HotUpdateStartupConfig>();
                AssetDatabase.CreateAsset(value, path); AssetDatabase.SaveAssets();
                m_startup = value; Selection.activeObject = value;
                m_message = "已创建空配置。请填写真实发布信息，并在现有启动场景中自行添加启动组件。";
            });
        }
        private void RefreshReport()
        {
            Run(() => m_report = HotUpdateEnvironmentDiagnostics.GetReport());
        }

        private void Run(Action action) { try { action(); } catch (Exception error) { m_message = error.GetBaseException().Message; } }
    }

    public static class HotUpdateStartupTools
    {
        [DataContract]
        private sealed class Definition
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "includePlatforms")] public string[] IncludePlatforms;
        }

        /// <summary>只追加业务明确选定的现有程序集，保留原配置。该方法不生成业务代码或改动后端。</summary>
        public static void RegisterHotAssembly(AssemblyDefinitionAsset assembly)
        {
            if (assembly == null) {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode) {
                throw new InvalidOperationException("请退出 Play Mode 后登记热更程序集。");
            }

            var path = AssetDatabase.GetAssetPath(assembly);
            ValidateHotAssemblyDefinition(path, assembly.text);
            Type settingsType = HotUpdateEnvironmentDiagnostics.FindOptionalType("HybridCLR.Editor.Settings.HybridCLRSettings") ?? throw new InvalidOperationException("未找到 HybridCLR 设置类型，请先通过项目自身流程安装插件。");
            FieldInfo field = settingsType.GetField("hotUpdateAssemblyDefinitions", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo instanceProperty = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            MethodInfo save = settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (field == null || field.FieldType != typeof(AssemblyDefinitionAsset[]) || instanceProperty == null || save == null) {
                throw new NotSupportedException("已安装 HybridCLR 设置接口不兼容，未修改配置。");
            }

            var settings = instanceProperty.GetValue(null);
            AssemblyDefinitionAsset[] existing = field.GetValue(settings) as AssemblyDefinitionAsset[] ?? Array.Empty<AssemblyDefinitionAsset>();
            if (existing.Contains(assembly)) {
                return;
            }

            field.SetValue(settings, existing.Concat(new[] { assembly }).ToArray());
            try { save.Invoke(null, null); }
            catch { field.SetValue(settings, existing); throw; }
        }

        /// <summary>只检查定义，不导入或保存资产；同时包含 Editor 和 Player 平台的业务程序集允许登记。</summary>
        public static string ValidateHotAssemblyDefinition(string assetPath, string json)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var serializer = new DataContractJsonSerializer(typeof(Definition));
            var definition = (Definition)serializer.ReadObject(input);
            var editorOnly = definition?.IncludePlatforms != null && definition.IncludePlatforms.Length > 0 &&
                definition.IncludePlatforms.All(platform => platform == "Editor");
            return assetPath == null || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                definition == null || string.IsNullOrWhiteSpace(definition.Name) ||
                definition.Name.StartsWith("ZRAsset.", StringComparison.Ordinal) || definition.Name.StartsWith("Unity", StringComparison.Ordinal) ||
                definition.Name.StartsWith("HybridCLR", StringComparison.Ordinal) || editorOnly
                ? throw new InvalidOperationException("请选择 Assets 下独立的业务运行时 asmdef；不能把资源框架、Unity、HybridCLR 或仅 Editor 程序集登记为热更。")
                : definition.Name;
        }
    }
}
