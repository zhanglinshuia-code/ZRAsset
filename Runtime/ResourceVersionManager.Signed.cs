using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>签名检查通过后的目标；对外只给清单副本，准备操作始终使用私有的原始快照。</summary>
    public sealed class ResourceSignedUpdateCandidate
    {
        internal readonly string EnvelopeJson;
        internal readonly string ManifestJson;
        internal readonly ResourceReleaseTrustOptions Trust;
        internal readonly string Target;

        public string Version { get; }
        public long Sequence { get; }
        public long ExpiresUtcSeconds { get; }
        public bool IsNewVersion { get; }
        public ResourceManifest Manifest { get { return OwnedManifest.CopyUnchecked(); } }
        internal ResourceManifest OwnedManifest { get; }
        public ResourceOperationBase<ResourceManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        {
            return OwnedManifest.CopySnapshotAsync(cancellationToken);
        }

        // Diff 只用于显示；其数组即使被业务修改也不会参与准备或认证。
        public ResourceVersionDiff Diff { get; }

        internal ResourceSignedUpdateCandidate(string envelope, string manifestJson, ResourceReleaseTrustOptions trust,
            ResourceSignedReleasePayload payload, ResourceVersionDiff diff, bool isNew, ResourceManifest decodedManifest = null, bool takeOwnership = false)
        {
            EnvelopeJson = envelope; ManifestJson = manifestJson; Trust = trust; Target = payload.Release.BuildTarget;
            Version = payload.Release.Version; Sequence = payload.Sequence; ExpiresUtcSeconds = payload.ExpiresUtcSeconds;
            Diff = diff; IsNewVersion = isNew;
            OwnedManifest = decodedManifest == null ? ResourceManifest.FromJson(manifestJson) : takeOwnership ? decodedManifest : decodedManifest.Clone();
        }
    }

    public sealed partial class ResourceVersionManager
    {
        /// <summary>
        /// 商业发布入口：先验证预置公钥签名、有效期与序号策略，再请求同源清单。
        /// HTTPS 请求禁止重定向并限制响应长度；失败不会准备版本或更改活动指针。
        /// </summary>
        public ResourceOperationBase<ResourceSignedUpdateCandidate> FetchSignedReleaseAsync(string releaseUrl,
            ResourceReleaseTrustOptions trust, CancellationToken cancellationToken = default, string expectedPlayerBuildId = null)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return FetchSignedReleaseAsyncScheduled(releaseUrl, trust, cancellationToken, expectedPlayerBuildId);
        }

        private async ResourceOperationBase<ResourceSignedUpdateCandidate> FetchSignedReleaseAsyncScheduled(string releaseUrl,
            ResourceReleaseTrustOptions trust, CancellationToken cancellationToken = default, string expectedPlayerBuildId = null)
        {
            CheckThread();
            if (trust == null) {
                throw new ArgumentNullException(nameof(trust));
            }

            Uri source = trust.ValidateSource(releaseUrl);
            cancellationToken.ThrowIfCancellationRequested();
            await EnterAsync(cancellationToken);
            try {
                var envelope = await ReadBoundedReleaseTextAsync(source, ResourceReleaseAuthentication.MaximumEnvelopeBytes,
                    m_options.RequestTimeoutSeconds, cancellationToken, m_options.NetworkPolicy);
                ResourceSignedReleasePayload payload = ResourceReleaseAuthentication.Verify(envelope, trust, DateTimeOffset.UtcNow);
                if (expectedPlayerBuildId != null && !string.Equals(payload.PlayerBuildId, expectedPlayerBuildId, StringComparison.Ordinal)) {
                    throw new HotUpdate.HotUpdateHostMismatchException(expectedPlayerBuildId, payload.PlayerBuildId);
                }

                ResourceReleaseInfo release = payload.Release;
                CheckAcceptedRelease(payload);
                if (!string.Equals(release.BuildTarget, m_buildTarget, StringComparison.Ordinal)) {
                    throw new InvalidDataException("签名发布的平台与当前平台不一致。");
                }

                if (!Uri.TryCreate(source, release.ManifestUrl, out Uri manifestUri) ||
                    manifestUri.Scheme != source.Scheme || manifestUri.Host != source.Host || manifestUri.Port != source.Port) {
                    throw new InvalidDataException("签名发布的清单必须位于同一来源。");
                }

                trust.ValidateSource(manifestUri.AbsoluteUri);
                var manifestJson = await ReadBoundedReleaseTextAsync(manifestUri, trust.MaximumManifestBytes,
                    m_options.RequestTimeoutSeconds, cancellationToken, m_options.NetworkPolicy);
                if (!string.Equals(await HashAsync(manifestJson, cancellationToken), release.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("目标清单 SHA-256 与签名发布描述不一致。");
                }
                // 慢速下载可能跨过过期时间，因此清单返回后再次检查有效期。
                ResourceReleaseAuthentication.Verify(envelope, trust, DateTimeOffset.UtcNow);
                ResourceManifest manifest = await ParseTargetAsync(manifestJson, release.Version, cancellationToken, true);
                if (string.Equals(release.Version, m_pointer?.Active, StringComparison.Ordinal) &&
                    !await SameManifestAsync(await SerializeAsync(manifest, cancellationToken), await SerializeAsync(m_activeManifest, cancellationToken), cancellationToken)) {
                    throw new InvalidDataException("活动版本号对应了不同清单；发布端必须使用新版本号。");
                }

                ResourceVersionDiff diff = await CompareOwnedAsync(manifest, cancellationToken);
                ResourceReleaseAuthentication.Verify(envelope, trust, DateTimeOffset.UtcNow);
                return new ResourceSignedUpdateCandidate(envelope, manifestJson, trust, payload, diff,
                    !string.Equals(release.Version, m_pointer?.Active, StringComparison.Ordinal), manifest, true);
            }
            finally { Exit(); }
        }

        /// <summary>
        /// 只接受本库验证产生的候选对象，并在真正准备前重新验证签名、有效期与原始清单。
        /// 业务修改 candidate.Manifest 返回的副本不能替换已认证的内容。准备成功后仍显式 ActivateAsync。
        /// </summary>
        public ResourceOperationBase PrepareSignedReleaseAsync(ResourceSignedUpdateCandidate candidate,
            DownloadPriority priority = DownloadPriority.Normal, CancellationToken cancellationToken = default)
        {
            return PrepareSignedSelectionAsync(candidate, ResourceSelection.All, priority, cancellationToken);
        }

        public ResourceOperationBase PrepareSignedSelectionAsync(ResourceSignedUpdateCandidate candidate, ResourceSelection selection,
            DownloadPriority priority = DownloadPriority.Normal, CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (candidate == null) {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (selection == null) {
                throw new ArgumentNullException(nameof(selection));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ResourceSignedReleasePayload payload = ResourceReleaseAuthentication.Verify(candidate.EnvelopeJson, candidate.Trust, DateTimeOffset.UtcNow);
            if (candidate.Target != m_buildTarget || payload.Release.BuildTarget != m_buildTarget || payload.Release.Version != candidate.Version) {
                throw new InvalidDataException("签名候选版本与当前管理器或原始清单不一致。");
            }

            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return PrepareVerifiedAsync();
            async ResourceOperationBase PrepareVerifiedAsync()
            {
                if (!string.Equals(await HashAsync(candidate.ManifestJson, cancellationToken), payload.Release.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("签名候选版本与当前管理器或原始清单不一致。");
                }

                await PrepareCoreAsync(candidate.Version, candidate.OwnedManifest, priority, cancellationToken, candidate, selection: selection, takeOwnership: true);
            }
        }

        [Serializable]
        private sealed class AcceptedRelease
        {
            public int Format = 1;
            public long Sequence;
            public string Version, ManifestSha256;
        }

        // 版本锁内读写。显式本地 Rollback/Activate 不降低网络发布高水位。
        private void CheckAcceptedRelease(ResourceSignedReleasePayload payload)
        {
            var path = StatePath("accepted-release.json");
            if (!File.Exists(path)) {
                return;
            }

            AcceptedRelease accepted = JsonUtility.FromJson<AcceptedRelease>(ReadCheckedState(path));
            if (accepted == null || accepted.Format != 1 || accepted.Sequence < 0 ||
                !DownloadStorage.IsSafeSegment(accepted.Version) || !DownloadStorage.IsSha256(accepted.ManifestSha256)) {
                throw new InvalidDataException("已接受发布记录损坏。");
            }

            if (payload.Sequence < accepted.Sequence || (payload.Sequence == accepted.Sequence &&
                (payload.Release.Version != accepted.Version || !string.Equals(payload.Release.ManifestSha256, accepted.ManifestSha256, StringComparison.OrdinalIgnoreCase)))) {
                throw ResourceFailure.Annotate(new InvalidDataException("网络发布序号低于本机已接受版本，或同序号绑定了不同内容。"), ResourceErrorCode.ReleaseRejected, ResourceStage.CheckRelease);
            }
        }

        private void AcceptRelease(ResourceSignedReleasePayload payload)
        {
            CheckAcceptedRelease(payload);
            WriteCheckedState(StatePath("accepted-release.json"), JsonUtility.ToJson(new AcceptedRelease
            { Sequence = payload.Sequence, Version = payload.Release.Version, ManifestSha256 = payload.Release.ManifestSha256 }));
        }

        /// <summary>接收回调在复制数据之前检查上限，避免 DownloadHandlerBuffer 先无界缓存整个恶意响应。</summary>
        private sealed class BoundedReleaseDownload: DownloadHandlerScript
        {
            private readonly int m_limit;
            private readonly MemoryStream m_body = new();
            internal bool Exceeded { get; private set; }
            internal BoundedReleaseDownload(int limit) : base(new byte[16 * 1024]) { m_limit = limit; }
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength < 0 || m_body.Length + dataLength > m_limit) { Exceeded = true; return false; }
                m_body.Write(data, 0, dataLength);
                return true;
            }
            internal string ReadText()
            {
                try { return new UTF8Encoding(false, true).GetString(m_body.ToArray()); }
                catch (DecoderFallbackException) { throw new InvalidDataException("发布响应必须是有效 UTF-8。"); }
            }
            internal void DisposeBody()
            {
                m_body.Dispose();
            }
        }

        internal static async ResourceOperationBase<string> ReadBoundedReleaseTextAsync(Uri uri, int limit, int timeout,
            CancellationToken cancellationToken, ResourceDownloadPolicy networkPolicy = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handler = new BoundedReleaseDownload(limit);
            using (var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbGET, handler, null)) {
                request.redirectLimit = 0;
                request.timeout = timeout;
                try {
                    networkPolicy?.Configure(request, new ResourceRequestContext(uri.AbsoluteUri, uri.AbsoluteUri, ResourceRequestKind.SignedRelease));
                    request.redirectLimit = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone) {
                        if (cancellationToken.IsCancellationRequested) {
                            request.Abort();
                        }

                        await ResourceOperationBase.Yield();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handler.Exceeded) {
                        throw new InvalidDataException("发布响应超出允许的长度限制。");
                    }
                    // 只接受完整 200；拒绝重定向、部分响应和需要额外处理的 304。
                    return request.result != UnityWebRequest.Result.Success || request.responseCode != 200
                        ? throw ResourceFailure.Annotate(new IOException("签名发布请求失败，HTTP " + request.responseCode + "。"), ResourceFailure.HttpErrorCode(request.responseCode), ResourceStage.CheckRelease, request.responseCode)
                        : handler.ReadText();
                }
                finally { handler.DisposeBody(); }
            }
        }
    }
}
