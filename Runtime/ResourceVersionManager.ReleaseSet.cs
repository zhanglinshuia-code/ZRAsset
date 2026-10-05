using System.Threading;

namespace ZRAsset
{
    public sealed partial class ResourceVersionManager
    {
        // 联合发布只有集合指针是提交点，成员版本管理器只准备和校验文件。
        internal async ResourceOperationBase<ResourceManager> OpenPreparedAsync(ResourceManifest manifest, CancellationToken token)
        {
            await ActivateCoreAsync(manifest.PackageVersion, token, verifyOnly: true);
            token.ThrowIfCancellationRequested();
            return await ResourceManager.CreateWithDownloadsCoreAsync(manifest, OptionsFor(manifest.PackageVersion),
                m_builtInRoot, 5, null, null, m_decryptionServices, m_manifestKeys, m_manifestCodec,
                m_builtInManifestFileName, token);
        }
    }
}
