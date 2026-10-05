using System;
using System.Threading;
using UnityEngine.UIElements;

namespace ZRAsset.Integrations
{
    /// <summary>视图从父节点移除后才释放 UXML 和关联资源；加载失败自动交回句柄。</summary>
    public sealed class ResourceView: IDisposable
    {
        private AssetHandle<VisualTreeAsset> m_asset;
        public TemplateContainer Root { get; private set; }

        public static async ResourceOperationBase<ResourceView> CreateAsync(ResourcePackage package, string address,
            VisualElement parent, CancellationToken token = default)
        {
            if (parent == null) { throw new ArgumentNullException(nameof(parent)); }
            var view = new ResourceView();
            try {
                view.m_asset = package.LoadAssetAsync<VisualTreeAsset>(address, token);
                VisualTreeAsset tree = await view.m_asset.Operation;
                token.ThrowIfCancellationRequested();
                view.Root = tree.CloneTree();
                parent.Add(view.Root);
                return view;
            }
            catch { view.Dispose(); throw; }
        }

        public void Dispose()
        {
            Root?.RemoveFromHierarchy();
            Root = null;
            m_asset?.Release();
            m_asset = null;
        }
    }
}
