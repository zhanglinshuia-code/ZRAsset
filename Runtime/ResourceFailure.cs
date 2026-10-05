using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ZRAsset.HotUpdate;

namespace ZRAsset
{
    /// <summary>稳定的业务错误码；不依赖异常消息或本地化文本。</summary>
    public enum ResourceErrorCode
    {
        None = 0, Cancelled = 1, Network = 2, Timeout = 3, InsufficientSpace = 4,
        AccessDenied = 5, Integrity = 6, InvalidManifest = 7, Authentication = 8,
        ReleaseRejected = 9, HostUpgradeRequired = 10, RestartRequired = 11,
        NotFound = 12, InvalidRequest = 13, Storage = 14, Unknown = 15, RemoteRejected = 16
    }

    [Flags]
    public enum ResourceRecoveryAction
    {
        None = 0, Retry = 1, FreeStorage = 2, RepairContent = 4,
        UpdatePlayer = 8, Restart = 16, ContactSupport = 32
    }

    public enum ResourceStage
    {
        None, Download, Verify, Load, CheckRelease, Prepare, Activate, StartCode, HealthCheck, Release
    }

    /// <summary>恢复建议不是授权；离线进入、修复和重启仍由业务显式决定。</summary>
    public readonly struct ResourceFailure
    {
        private const string MetadataKey = "ZRAsset.Failure.v1";
        private const string PrimaryFailureKey = "ZRAsset.PrimaryFailure.v1";

        public ResourceErrorCode Code { get; }
        public ResourceRecoveryAction Recovery { get; }
        public ResourceStage Stage { get; }
        public long ResponseCode { get; }

        public ResourceFailure(ResourceErrorCode code, ResourceStage stage = ResourceStage.None, long responseCode = 0)
        {
            Code = code;
            Stage = stage;
            ResponseCode = responseCode;
            Recovery = code switch
            {
                ResourceErrorCode.Network or ResourceErrorCode.Timeout => ResourceRecoveryAction.Retry,
                ResourceErrorCode.InsufficientSpace => ResourceRecoveryAction.FreeStorage | ResourceRecoveryAction.Retry,
                ResourceErrorCode.Integrity or ResourceErrorCode.NotFound => ResourceRecoveryAction.RepairContent,
                ResourceErrorCode.HostUpgradeRequired => ResourceRecoveryAction.UpdatePlayer,
                ResourceErrorCode.RestartRequired => ResourceRecoveryAction.Restart,
                ResourceErrorCode.None or ResourceErrorCode.Cancelled => ResourceRecoveryAction.None,
                _ => ResourceRecoveryAction.ContactSupport
            };
        }

        /// <summary>保留原异常类型、堆栈与 await 行为，只在失败路径添加结构化信息。</summary>
        internal static T Annotate<T>(T error, ResourceErrorCode code, ResourceStage stage, long responseCode = 0, bool overwrite = false) where T : Exception
        {
            if (overwrite || !error.Data.Contains(MetadataKey)) {
                error.Data[MetadataKey] = new ResourceFailure(code, stage, responseCode);
            }
            return error;
        }

        internal static AggregateException WithCleanup(Exception primary, Exception cleanup)
        {
            var error = new AggregateException("操作失败，清理过程中也发生错误。", primary, cleanup);
            error.Data[PrimaryFailureKey] = primary;
            return error;
        }

        public static ResourceFailure FromException(Exception error, ResourceStage stage = ResourceStage.None)
        {
            if (error == null) {
                return default;
            }
            // 提交边界的标注优先于原始取消/网络错误，避免业务误判为可重试。
            if (error.Data[MetadataKey] is ResourceFailure annotated) {
                return annotated;
            }
            if (error.Data[PrimaryFailureKey] is Exception primary) { return FromException(primary, stage); }
            if (error is HotUpdateRestartRequiredException) {
                return new ResourceFailure(ResourceErrorCode.RestartRequired, stage);
            }
            if (error is HotUpdateHostMismatchException) {
                return new ResourceFailure(ResourceErrorCode.HostUpgradeRequired, stage);
            }
            if (error is OperationCanceledException) {
                return new ResourceFailure(ResourceErrorCode.Cancelled, stage);
            }
            if (error is AggregateException aggregate) {
                // 多个失败不能假定单次重试可以恢复；单个包装保留原来的建议。
                return aggregate.InnerExceptions.Count == 1
                    ? FromException(aggregate.InnerExceptions[0], stage)
                    : new ResourceFailure(ResourceErrorCode.Unknown, stage);
            }
            ResourceErrorCode code = error switch
            {
                ResourceInsufficientSpaceException => ResourceErrorCode.InsufficientSpace,
                TimeoutException => ResourceErrorCode.Timeout,
                UnauthorizedAccessException => ResourceErrorCode.AccessDenied,
                FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => ResourceErrorCode.NotFound,
                InvalidDataException => ResourceErrorCode.Integrity,
                CryptographicException => ResourceErrorCode.Authentication,
                IOException io when ResourceDiskPolicy.IsDiskFull(io) => ResourceErrorCode.InsufficientSpace,
                IOException => ResourceErrorCode.Storage,
                ArgumentException or ObjectDisposedException => ResourceErrorCode.InvalidRequest,
                _ => ResourceErrorCode.Unknown
            };
            return new ResourceFailure(code, stage);
        }

        internal static ResourceErrorCode HttpErrorCode(long responseCode, bool retryable = true)
        {
            return responseCode switch
            {
                401 or 403 => ResourceErrorCode.AccessDenied,
                404 or 410 => ResourceErrorCode.NotFound,
                408 => ResourceErrorCode.Timeout,
                429 => ResourceErrorCode.Network,
                _ when !retryable || (responseCode >= 400 && responseCode < 500) => ResourceErrorCode.RemoteRejected,
                _ => ResourceErrorCode.Network
            };
        }
    }
}
