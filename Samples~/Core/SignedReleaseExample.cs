using System.Threading;

namespace ZRAsset.Samples
{
    /// <summary>V8 启动示例：公钥由主包可信配置提供，不能从同一远端发布文件下载后直接信任。</summary>
    public static class SignedReleaseExample
    {
        /// <summary>
        /// 在业务资源尚未使用、或旧管理器已经完整关闭时调用。
        /// 失败直接交给启动界面处理；调用方可显式选择仍已激活的离线版本。
        /// </summary>
        public static async ResourceOperationBase<ResourceManager> UpdateAndOpenAsync(ResourceVersionManager versions,
            string signedReleaseUrl, ResourceReleaseTrustOptions trust, CancellationToken cancellationToken = default)
        {
            ResourceSignedUpdateCandidate candidate = await versions.FetchSignedReleaseAsync(signedReleaseUrl, trust, cancellationToken);
            // 只从候选的私有认证快照准备，避免外部修改显示清单后替换发布内容。
            await versions.PrepareSignedReleaseAsync(candidate, DownloadPriority.High, cancellationToken);
            await versions.ActivateAsync(candidate.Version, cancellationToken);
            ResourceManager resources = await versions.CreateActiveManagerAsync();
            resources.DiagnosticName = "活动资源 " + candidate.Version;
            return resources;
        }
    }
}
