using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ZRAsset.Editor
{
    /// <summary>编辑器直接读取源资源，但仍使用正式 ResourceManager 的缓存、引用计数与生命周期。</summary>
    public static class EditorResourceFactory
    {
        public static void InitializePackage(ResourcePackage package, BundleBuildConfig config, double unloadDelaySeconds = 5, EditorSimulationOptions simulation = null)
        {
            if (package == null) {
                throw new ArgumentNullException(nameof(package));
            }

            ResourceBuildPlan plan = ResourceBuildAnalyzer.Analyze(config, EditorUserBuildSettings.activeBuildTarget);
            plan.ThrowIfInvalid();
            package.Initialize(plan.CreateSimulationManifest(), new EditorAssetBackend(plan, simulation), unloadDelaySeconds,
                sceneBackend: new EditorSceneBackend());
        }

        public static ResourceManager Create(BundleBuildConfig config, double unloadDelaySeconds = 5, ResourceRetryPolicy retryPolicy = null, EditorSimulationOptions simulation = null)
        {
            ResourceBuildPlan plan = ResourceBuildAnalyzer.Analyze(config, EditorUserBuildSettings.activeBuildTarget);
            plan.ThrowIfInvalid();
            return new ResourceManager(plan.CreateSimulationManifest(), new EditorAssetBackend(plan, simulation), unloadDelaySeconds,
                retryPolicy, new EditorSceneBackend());
        }
    }

    internal sealed class EditorAssetBackend: IResourceBackend, ISynchronousResourceBackend,
        IResourceCollectionBackend, ISynchronousResourceCollectionBackend, IRawFileBackend, IResourceFileBackend
    {
        private sealed class Token { public string Name; public bool Released; }
        private readonly Dictionary<string, HashSet<string>> m_assets;
        private readonly EditorSimulationFileSystem m_simulation;
        public IResourceFileSystem FileSystem
        {
            get
            {
                return m_simulation;
            }
        }

        public bool SupportsSynchronousLoading
        {
            get
            {
                return m_simulation == null || (m_simulation.FileCapabilities & ResourceFileCapabilities.SynchronousRead) != 0;
            }
        }

        public bool SupportsAssetCollections
        {
            get
            {
                return true;
            }
        }

        public bool SupportsSynchronousCollections
        {
            get
            {
                return SupportsSynchronousLoading;
            }
        }

        public EditorAssetBackend(ResourceBuildPlan plan, EditorSimulationOptions options = null)
        {
            if (options != null) {
                m_simulation = new EditorSimulationFileSystem(plan, options);
            }

            m_assets = plan.Report.Bundles.ToDictionary(b => b.Name, b => new HashSet<string>(b.ExplicitAssets, StringComparer.Ordinal));
        }

        public async ResourceOperationBase<object> LoadBundleAsync(BundleInfo info)
        {
            if (m_simulation != null) {
                await m_simulation.ResolveAsync(info);
            }

            return CreateToken(info);
        }

        public object LoadBundle(BundleInfo info)
        {
            m_simulation?.Resolve(info);
            return CreateToken(info);
        }
        private object CreateToken(BundleInfo info)
        {
            return info.FileType != ResourceFileType.AssetBundle
                ? throw new InvalidOperationException("Raw files require the raw file API.")
                : !m_assets.ContainsKey(info.Name)
                ? throw new FileNotFoundException($"模拟计划中没有 Bundle：{info.Name}")
                : (object)new Token { Name = info.Name };
        }

        public RawFileLocation ResolveRawFile(BundleInfo container, AssetInfo asset)
        { m_simulation?.Resolve(container); return RawLocation(asset); }
        private static RawFileLocation RawLocation(AssetInfo asset)
        {
            return new(new ResourceFileLocation(Path.GetFullPath(asset.AssetPath)), ResourceFileType.RawFile,
                asset.FileSize, asset.FileSha256, 0, asset.FileSize, asset.FileSha256);
        }

        public async ResourceOperationBase<RawFileLocation> ResolveRawFileAsync(BundleInfo container, AssetInfo asset)
        { await ResourceOperationBase.Yield(); if (m_simulation != null) { await m_simulation.ResolveAsync(container); } return RawLocation(asset); }

        public async ResourceOperationBase<Object> LoadAssetAsync(object bundle, string path, Type type)
        {
            // 保留异步边界，让取消、提前释放等业务路径在模拟模式中也能运行。
            await ResourceOperationBase.Yield();
            return LoadAsset(bundle, path, type);
        }

        public Object LoadAsset(object bundle, string path, Type type)
        {
            Validate(bundle, path);
            Object asset = AssetDatabase.LoadAssetAtPath(path, type);
            return !asset ? throw new IOException($"模拟加载失败，资源缺失或类型不匹配：{path}") : asset;
        }

        public Object[] LoadSubAssets(object bundle, string path, Type type)
        { Validate(bundle, path); return VisibleAssets(path, type); }
        public Object[] LoadAllAssets(object bundle, Type type)
        {
            Token token = Validate(bundle);
            return m_assets[token.Name].OrderBy(path => path, StringComparer.Ordinal).SelectMany(path => VisibleAssets(path, type)).Distinct().ToArray();
        }
        private static Object[] VisibleAssets(string path, Type type)
        {
            return new[] { AssetDatabase.LoadMainAssetAtPath(path) }.Concat(AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                .Where(asset => asset != null && type.IsInstanceOfType(asset)).Distinct().ToArray();
        }

        private Token Validate(object bundle, string path = null)
        {
            var token = (Token)bundle;
            return token.Released
                ? throw new ObjectDisposedException(token.Name)
                : path != null && !m_assets[token.Name].Contains(path) ? throw new InvalidOperationException("模拟资源不属于该包：" + path) : token;
        }
        public async ResourceOperationBase<Object[]> LoadSubAssetsAsync(object bundle, string path, Type type)
        { await ResourceOperationBase.Yield(); return LoadSubAssets(bundle, path, type); }
        public async ResourceOperationBase<Object[]> LoadAllAssetsAsync(object bundle, Type type)
        { await ResourceOperationBase.Yield(); return LoadAllAssets(bundle, type); }

        public void UnloadBundle(object bundle)
        {
            ((Token)bundle).Released = true;
            // AssetDatabase 资源属于编辑器，不能 Destroy/UnloadAsset，否则会影响 Inspector 和其他编辑器工具。
        }
    }

    internal sealed class EditorSceneBackend: ISceneBackend, IControlledSceneBackend
    {
        public ResourceOperationBase<Scene> LoadSceneAsync(string path, LoadSceneMode mode)
        {
            return BeginLoadScene(path, new ResourceSceneLoadOptions(mode), default).Operation;
        }

        public ISceneLoadRequest BeginLoadScene(string path, ResourceSceneLoadOptions options, System.Threading.CancellationToken cancellationToken)
        {
            return new UnitySceneLoadRequest(path, options, () => EditorSceneManager.LoadSceneAsyncInPlayMode(path,
                        new LoadSceneParameters(options.Mode, options.LocalPhysicsMode)), cancellationToken);
        }

        public ResourceOperationBase UnloadSceneAsync(Scene scene)
        {
            return new UnitySceneBackend().UnloadSceneAsync(scene);
        }
    }

    /// <summary>模式选择仅存在当前 Editor 会话，Player 永远不会引用模拟后端。</summary>
    [InitializeOnLoad]
    public static class EditorSimulationSettings
    {
        private const string Key = "ZRAsset.SimulationConfigGuid";
        public static string ConfigPath
        {
            get
            {
                return AssetDatabase.GUIDToAssetPath(SessionState.GetString(Key, ""));
            }
        }

        public static bool IsEnabled
        {
            get
            {
                return !string.IsNullOrEmpty(SessionState.GetString(Key, ""));
            }
        }

        static EditorSimulationSettings()
        {
            Apply();
        }

        public static void Enable(BundleBuildConfig config)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(config));
            if (string.IsNullOrEmpty(guid)) {
                throw new InvalidOperationException("模拟模式需要保存到项目中的打包配置。");
            }

            ResourceBuildAnalyzer.Analyze(config, EditorUserBuildSettings.activeBuildTarget).ThrowIfInvalid();
            SessionState.SetString(Key, guid);
            Apply();
        }

        public static void Disable() { SessionState.EraseString(Key); Apply(); }
        private static void Apply()
        {
            ResourceManager.EditorSimulationFactory = IsEnabled ? CreateSelected : null;
        }

        private static ResourceManager CreateSelected(double delay, ResourceRetryPolicy retry)
        {
            BundleBuildConfig config = AssetDatabase.LoadAssetAtPath<BundleBuildConfig>(ConfigPath);
            return !config
                ? throw new InvalidOperationException("模拟配置已丢失，请在资源工作台重新选择配置或切换真实模式。")
                : EditorResourceFactory.Create(config, delay, retry);
        }
    }
}
