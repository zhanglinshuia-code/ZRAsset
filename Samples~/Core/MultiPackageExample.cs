using System;
using System.IO;
using UnityEngine;

namespace ZRAsset.Samples
{
    /// <summary>两个包保持资源 Handle，分别升级/回滚，并显式关闭。使用本机无签名演示发布。</summary>
    public sealed class MultiPackageExample: MonoBehaviour
    {
        public string ServerRoot = "http://127.0.0.1:8080/";
        private ResourcePackage m_alpha, m_beta;
        private AssetHandle<TextAsset> m_alphaHandle, m_betaHandle;
        private bool m_busy, m_destroyed;
        private string m_message = "点击初始化；需先构建演示文件并启动本机 HTTP 服务。";

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(15, 15, 620, 320), GUI.skin.box);
            GUILayout.Label("ZRAsset 多 Package 演示");
            GUILayout.Label("Alpha: " + Text(m_alphaHandle));
            GUILayout.Label("Beta: " + Text(m_betaHandle));
            GUILayout.Label(m_message);
            GUI.enabled = !m_busy;
            if (m_alpha == null && m_beta == null) {
                if (GUILayout.Button("初始化两个包 v1")) {
                    Execute(InitializeAsync);
                }
            }
            else {
                if (GUILayout.Button("升级 Alpha 到 v2（Beta 保持在用）")) {
                    Execute(() => SwitchAsync(m_alpha, true, "v2"));
                }

                if (GUILayout.Button("回滚 Alpha（Beta 保持在用）")) {
                    Execute(() => SwitchAsync(m_alpha, true, null));
                }

                if (GUILayout.Button("升级 Beta 到 v2（Alpha 保持在用）")) {
                    Execute(() => SwitchAsync(m_beta, false, "v2"));
                }

                if (GUILayout.Button("释放两个包")) {
                    Execute(CloseAsync);
                }
            }
            GUI.enabled = true;
            GUILayout.EndArea();
        }

        private static string Text(AssetHandle<TextAsset> handle)
        {
            return handle == null ? "未加载" :
            handle.Operation.IsDone && !handle.Operation.IsFaulted && !handle.Operation.IsCanceled ? handle.Asset.text : "加载中或失败";
        }

        private async void Execute(Func<ResourceOperationBase> action)
        {
            if (m_busy) {
                return;
            }

            m_busy = true;
            try { await action(); m_message = "操作完成。"; }
            catch (Exception error) { m_message = error.Message; Debug.LogException(error); }
            finally {
                if (m_destroyed) {
                    try { await CloseAsync(); } catch (Exception error) { Debug.LogException(error); }
                }
                m_busy = false;
            }
        }

        private async ResourceOperationBase InitializeAsync()
        {
            try {
                m_alpha = ResourcePackages.CreatePackage("demo_alpha");
                m_beta = ResourcePackages.CreatePackage("demo_beta");
                foreach (ResourcePackage package in new[] { m_alpha, m_beta }) {
                    package.ConfigureUpdates(ResourcePlatform.Current.BuildTarget,
                        new BundleDownloadOptions(ServerRoot.TrimEnd('/') + "/" + package.Name + "/", "initial",
                            Path.Combine(Application.persistentDataPath, "ZRAssetPackageDemo")), "", 0);
                }

                await SwitchAsync(m_alpha, true, "v1");
                await SwitchAsync(m_beta, false, "v1");
            }
            catch { await CloseAsync(); throw; }
        }

        private async ResourceOperationBase SwitchAsync(ResourcePackage package, bool first, string version)
        {
            if (version != null) {
                ResourceUpdateCandidate candidate = await package.Versions.FetchReleaseAsync(ServerRoot.TrimEnd('/') + "/" + package.Name + "/" + version + ".release.json");
                await package.Versions.PrepareAsync(candidate.Version, candidate.Manifest);
            }
            if (first) { m_alphaHandle?.Release(); m_alphaHandle = null; }
            else { m_betaHandle?.Release(); m_betaHandle = null; }
            try {
                if (version == null) {
                    await package.RollbackAsync();
                }
                else {
                    await package.ActivateAsync(version);
                }
            }
            finally {
                if (package.IsInitialized) {
                    AssetHandle<TextAsset> handle = package.LoadAssetAsync<TextAsset>("demo/text");
                    if (first) {
                        m_alphaHandle = handle;
                    }
                    else {
                        m_betaHandle = handle;
                    }

                    await handle.Operation;
                }
            }
        }

        private async ResourceOperationBase CloseAsync()
        {
            m_alphaHandle?.Release(); m_betaHandle?.Release(); m_alphaHandle = m_betaHandle = null;
            if (m_alpha != null) { await m_alpha.DisposeAsync(); m_alpha = null; }
            if (m_beta != null) { await m_beta.DisposeAsync(); m_beta = null; }
        }

        private void Update()
        {
            if (m_busy) {
                return;
            }

            if (m_alpha?.IsInitialized == true) {
                m_alpha.UnloadUnused();
            }

            if (m_beta?.IsInitialized == true) {
                m_beta.UnloadUnused();
            }
        }

        private void OnDestroy()
        {
            m_destroyed = true;
            if (!m_busy) {
                Execute(CloseAsync);
            }
        }
    }
}
