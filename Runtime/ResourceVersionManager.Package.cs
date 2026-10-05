using System;
using System.Threading;

namespace ZRAsset
{
    public sealed partial class ResourceVersionManager
    {
        internal static ResourceVersionManager CreateForPackage(string target, BundleDownloadOptions downloads, string name,
            ResourceInitializationOptions options)
        {
            return new ResourceVersionManager(target, downloads, options.Root, name, options.DecryptionServices,
                options.ManifestKeys, options.ManifestCodec, options.ManifestFormat, true);
        }

        internal static async ResourceOperationBase<ResourceVersionManager> CreateForPackageAsync(string target,
            BundleDownloadOptions downloads, string name, ResourceInitializationOptions options, CancellationToken token)
        {
            var manager = new ResourceVersionManager(target, downloads, options.Root, name, options.DecryptionServices,
                options.ManifestKeys, options.ManifestCodec, options.ManifestFormat, false);
            await manager.RefreshAsync(token);
            return manager;
        }

        internal async ResourceOperationBase<ResourceManager> ActivatePackageAsync(string version, bool rollback,
            ResourceSelection selection, double unloadDelay, ResourceRetryPolicy retryPolicy, ResourceLoadOptions loadOptions,
            IResourceDecryptionServices decryptionServices, Func<ResourceOperationBase> closePrevious, CancellationToken token)
        {
            ResourceManager next = null;
            try {
                async ResourceOperationBase OpenBeforeCommit(ResourceManifest manifest, CancellationToken cancellation)
                {
                    // 创建只建立文件系统和索引，不加载 Unity 对象；失败时旧管理器尚未关闭。
                    next = await ResourceManager.CreateWithDownloadsCoreAsync(manifest, OptionsFor(manifest.PackageVersion),
                        m_builtInRoot, unloadDelay, retryPolicy, loadOptions, decryptionServices ?? m_decryptionServices,
                        m_manifestKeys, m_manifestCodec, m_builtInManifestFileName, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    await closePrevious();
                }

                await ActivateCoreAsyncScheduled(version, token, rollback, selection: selection, beforeCommit: OpenBeforeCommit);
                return next;
            }
            catch (Exception error) {
                try { if (next != null) { await next.DisposeAsync(); } }
                catch (Exception cleanup) { throw ResourceFailure.WithCleanup(error, cleanup); }
                throw;
            }
        }
    }
}
