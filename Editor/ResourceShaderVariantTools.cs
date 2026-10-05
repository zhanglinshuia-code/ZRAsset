using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ZRAsset.Editor
{
    public static class ResourceShaderVariantTools
    {
        public static IEnumerable<ShaderVariantCollection.ShaderVariant> Read(ShaderVariantCollection collection)
        {
            if (collection == null) { yield break; }
            using var serialized = new SerializedObject(collection);
            SerializedProperty shaders = serialized.FindProperty("m_Shaders") ?? throw new NotSupportedException("Unity ShaderVariantCollection 序列化格式已变化。");
            for (var i = 0; i < shaders.arraySize; i++) {
                SerializedProperty entry = shaders.GetArrayElementAtIndex(i);
                var shader = (Shader)entry.FindPropertyRelative("first").objectReferenceValue;
                SerializedProperty variants = entry.FindPropertyRelative("second.variants");
                for (var j = 0; j < variants.arraySize; j++) {
                    SerializedProperty variant = variants.GetArrayElementAtIndex(j);
                    yield return new ShaderVariantCollection.ShaderVariant(shader,
                        (PassType)variant.FindPropertyRelative("passType").intValue,
                        variant.FindPropertyRelative("keywords").stringValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }

        /// <summary>按 shader/pass/keywords 去重；不修改输入集合。</summary>
        public static ShaderVariantCollection Merge(IEnumerable<ShaderVariantCollection> collections)
        {
            if (collections == null) { throw new ArgumentNullException(nameof(collections)); }
            var result = new ShaderVariantCollection();
            try {
                foreach (ShaderVariantCollection collection in collections) {
                    foreach (ShaderVariantCollection.ShaderVariant variant in Read(collection)) { result.Add(variant); }
                }
                return result;
            }
            catch { Object.DestroyImmediate(result); throw; }
        }

        [MenuItem("ZRAsset/着色器/开始采集运行变体")]
        public static void BeginCapture()
        {
            InvokeRecorder("ClearCurrentShaderVariantCollection");
        }

        public static ShaderVariantCollection SaveCapture(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetPath.Contains("..") || !assetPath.EndsWith(".shadervariants", StringComparison.Ordinal)) {
                throw new ArgumentException("输出必须是 Assets 下新的 .shadervariants 路径。", nameof(assetPath));
            }
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null || System.IO.File.Exists(assetPath)) {
                throw new InvalidOperationException("请选择新的输出路径，避免覆盖已有变种集合。");
            }
            InvokeRecorder("SaveCurrentShaderVariantCollection", assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(assetPath) ?? throw new InvalidOperationException("Unity 未生成变种集合。");
        }

        [MenuItem("ZRAsset/着色器/保存运行变体")]
        private static void SaveSelectedCapture()
        {
            var path = EditorUtility.SaveFilePanelInProject("保存实际运行变种", "CapturedVariants", "shadervariants", "覆盖运行时关键字与目标场景后保存。");
            if (path.Length > 0) { Selection.activeObject = SaveCapture(path); }
        }

        private static void InvokeRecorder(string name, params object[] arguments)
        {
            MethodInfo method = typeof(ShaderUtil).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NotSupportedException("当前 Unity 不支持变种录制接口：" + name);
            method.Invoke(null, arguments);
        }

        /// <summary>在预览场景实际渲染材质与 Terrain；不会打开、保存或修改业务场景。</summary>
        public static ShaderVariantCollection CollectRendered(ResourceShaderVariantProfile profile, string assetPath)
        {
            if (profile == null) { throw new ArgumentNullException(nameof(profile)); }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { throw new InvalidOperationException("实际渲染收集需要图形设备，不能使用 -nographics。"); }
            BeginCapture();
            Scene scene = EditorSceneManager.NewPreviewScene();
            var target = RenderTexture.GetTemporary(256, 256, 24);
            var asynchronous = ShaderUtil.allowAsyncCompilation;
            try {
                ShaderUtil.allowAsyncCompilation = false;
                var cameraObject = new GameObject("Variant Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.targetTexture = target;
                var lightObject = new GameObject("Variant Light");
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(45, 30, 0);
                foreach (Material material in profile.Materials ?? Array.Empty<Material>()) {
                    if (material == null) { continue; }
                    var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    SceneManager.MoveGameObjectToScene(sphere, scene);
                    sphere.GetComponent<Renderer>().sharedMaterial = material;
                    try { Render(camera, target, new Bounds(Vector3.zero, Vector3.one)); }
                    finally { Object.DestroyImmediate(sphere); }
                }
                foreach (ResourceShaderVariantProfile.TerrainCase item in profile.Terrains ?? Array.Empty<ResourceShaderVariantProfile.TerrainCase>()) {
                    if (item?.Data == null) { continue; }
                    GameObject terrainObject = Terrain.CreateTerrainGameObject(item.Data);
                    SceneManager.MoveGameObjectToScene(terrainObject, scene);
                    Terrain terrain = terrainObject.GetComponent<Terrain>();
                    terrain.materialTemplate = item.Material;
                    terrain.drawInstanced = item.DrawInstanced;
                    try { Render(camera, target, new Bounds(item.Data.size * 0.5f, item.Data.size)); }
                    finally { Object.DestroyImmediate(terrainObject); }
                }
                ShaderVariantCollection collection = SaveCapture(assetPath);
                foreach (ResourceShaderVariantProfile.KeywordCase item in profile.RuntimeCases ?? Array.Empty<ResourceShaderVariantProfile.KeywordCase>()) {
                    if (item?.Shader != null) { collection.Add(new ShaderVariantCollection.ShaderVariant(item.Shader, item.Pass, item.Keywords)); }
                }
                foreach (ShaderVariantCollection source in profile.Collections ?? Array.Empty<ShaderVariantCollection>()) {
                    foreach (ShaderVariantCollection.ShaderVariant variant in Read(source)) { collection.Add(variant); }
                }
                EditorUtility.SetDirty(collection);
                AssetDatabase.SaveAssets();
                return collection;
            }
            finally {
                ShaderUtil.allowAsyncCompilation = asynchronous;
                EditorSceneManager.ClosePreviewScene(scene);
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void Render(Camera camera, RenderTexture target, Bounds bounds)
        {
            var distance = Mathf.Max(2, bounds.size.magnitude * 1.5f);
            camera.transform.position = bounds.center + new Vector3(0, distance, -distance);
            camera.transform.LookAt(bounds.center);
            camera.farClipPlane = distance * 5;
            if (GraphicsSettings.currentRenderPipeline == null) { camera.Render(); return; }
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(camera, request)) { throw new NotSupportedException("当前渲染管线不支持 StandardRequest；请使用 Play Session Capture。"); }
            RenderPipeline.SubmitRenderRequest(camera, request);
        }

        [MenuItem("ZRAsset/着色器/渲染所选变体配置")]
        private static void RenderSelected()
        {
            if (Selection.activeObject is not ResourceShaderVariantProfile profile) { throw new InvalidOperationException("请先选择 Shader Variant Profile。"); }
            var path = EditorUtility.SaveFilePanelInProject("保存渲染变种", "RenderedVariants", "shadervariants", "实际渲染材质与地形。");
            if (path.Length > 0) { Selection.activeObject = CollectRendered(profile, path); }
        }
    }

    internal sealed class ResourceShaderBuildScope: IDisposable
    {
        private static ResourceShaderBuildScope s_current;
        private readonly ResourceShaderBuildScope m_previous;
        private readonly HashSet<string> m_variants = new(StringComparer.Ordinal);
        private readonly HashSet<int> m_shaders = new();
        private readonly HashSet<string> m_excluded;
        private readonly bool m_strip;

        internal ResourceShaderBuildScope(BundleBuildConfig config)
        {
            m_previous = s_current;
            ResourceShaderVariantProfile profile = config.ShaderVariants;
            m_strip = profile != null && profile.StripUnlistedVariants;
            m_excluded = new HashSet<string>(profile?.ExcludedKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (m_strip) {
                ShaderVariantCollection collection = ResourceShaderVariants.Collect(config);
                try {
                    foreach (ShaderVariantCollection.ShaderVariant variant in ResourceShaderVariantTools.Read(collection)) {
                        m_shaders.Add(variant.shader.GetInstanceID());
                        m_variants.Add(Key(variant.shader, variant.passType, variant.keywords));
                    }
                }
                finally { Object.DestroyImmediate(collection); }
            }
            s_current = this;
        }

        private static string Key(Shader shader, PassType pass, IEnumerable<string> keywords)
        {
            return shader.GetInstanceID() + ":" + (int)pass + ":" + string.Join(" ", keywords.OrderBy(value => value, StringComparer.Ordinal));
        }

        public void Dispose()
        {
            s_current = m_previous;
        }

        internal static void Process(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> variants)
        {
            ResourceShaderBuildScope scope = s_current;
            if (scope == null || (!scope.m_strip && scope.m_excluded.Count == 0)) { return; }
            for (var i = variants.Count - 1; i >= 0; i--) {
                var keywords = variants[i].shaderKeywordSet.GetShaderKeywords().Select(keyword => keyword.name).ToArray();
                if (keywords.Any(scope.m_excluded.Contains) || (scope.m_strip && scope.m_shaders.Contains(shader.GetInstanceID()) &&
                    !scope.m_variants.Contains(Key(shader, snippet.passType, keywords)))) { variants.RemoveAt(i); }
            }
        }
    }

    internal sealed class ResourceShaderVariantStripper: IPreprocessShaders
    {
        public int callbackOrder
        {
            get { return 0; }
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            ResourceShaderBuildScope.Process(shader, snippet, data);
        }
    }
}
