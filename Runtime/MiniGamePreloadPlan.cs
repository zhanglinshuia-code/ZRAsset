using System;
using System.Linq;

namespace ZRAsset
{
    /// <summary>
    /// 原生 SDK 预下载计划：按清单选择完整依赖闭包，交给宿主 SetPreloadList 等接口。
    /// 提交只表示排入宿主队列，不能作为已下载/已校验/可离线的凭据。
    /// </summary>
    public sealed class MiniGamePreloadPlan
    {
        private readonly string[] m_urls;
        public long TotalBytes { get; }
        public int FileCount { get { return m_urls.Length; } }
        public string[] Urls { get { return (string[])m_urls.Clone(); } }

        public MiniGamePreloadPlan(ResourceManifest manifest, string versionedBaseUrl, ResourceSelection selection = null)
        {
            if (manifest == null) {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (!Uri.TryCreate(versionedBaseUrl, UriKind.Absolute, out Uri root) || root.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment) || !string.IsNullOrEmpty(root.UserInfo)) {
                throw new ArgumentException("原生预下载要求无查询参数的 HTTPS 版本目录。");
            }
            root = new Uri(root.AbsoluteUri.TrimEnd('/') + "/");
            BundleInfo[] bundles = ResourceCatalog.SelectBundles(manifest.Clone(), selection ?? ResourceSelection.All);
            m_urls = bundles.Select(bundle => new Uri(root, Uri.EscapeDataString(bundle.Name)).AbsoluteUri).ToArray();
            TotalBytes = bundles.Sum(bundle => bundle.Size);
        }

        /// <summary>例如传入 WeChatWASM.WX.SetPreloadList；宿主 SDK 版本和初始化由项目控制。</summary>
        public void Submit(Action<string[]> setPreloadList)
        {
            ResourcePackages.CheckThread();
            if (setPreloadList == null) {
                throw new ArgumentNullException(nameof(setPreloadList));
            }

            setPreloadList(Urls);
        }
    }
}
