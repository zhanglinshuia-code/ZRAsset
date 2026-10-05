using System;
using System.IO;
using System.Globalization;
using System.Threading;

namespace ZRAsset
{
    /// <summary>重试属于共享 Provider，不能由每个 Handle 重复执行；最多尝试 1 + MaxRetries 次。</summary>
    public sealed class ResourceRetryPolicy
    {
        public int MaxRetries { get; }
        public TimeSpan Delay { get; }
        private static readonly Random s_random = new Random();

        /// <summary>指数退避与 50%~100% 抖动；服务器 Retry-After 是下限，上限 5 分钟。</summary>
        public TimeSpan GetDelay(int retryNumber, string retryAfter = null)
        {
            if (retryNumber < 1) {
                throw new ArgumentOutOfRangeException(nameof(retryNumber));
            }

            double jitter;
            lock (s_random) {
                jitter = 0.5 + (s_random.NextDouble() * 0.5);
            }

            var seconds = Math.Min(60, Delay.TotalSeconds * Math.Pow(2, Math.Min(10, retryNumber - 1))) * jitter;
            double server = 0;
            if (long.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var delta)) {
                server = delta;
            }
            else if (DateTimeOffset.TryParseExact(retryAfter, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when)) {
                server = (when - DateTimeOffset.UtcNow).TotalSeconds;
            }

            return TimeSpan.FromSeconds(Math.Min(300, Math.Max(seconds, server)));
        }

        public ResourceRetryPolicy(int maxRetries = 0, double delaySeconds = 0.25)
        {
            if (maxRetries < 0 || maxRetries > 10) {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            if (double.IsNaN(delaySeconds) || double.IsInfinity(delaySeconds) || delaySeconds < 0 || delaySeconds > 60) {
                throw new ArgumentOutOfRangeException(nameof(delaySeconds));
            }

            MaxRetries = maxRetries;
            Delay = TimeSpan.FromSeconds(delaySeconds);
        }

        internal async ResourceOperationBase<T> ExecuteAsync<T>(Func<ResourceOperationBase<T>> operation)
        {
            for (var attempt = 0; ; attempt++) {
                try { return await operation(); }
                catch (IOException error) when (!(error is ResourceInsufficientSpaceException) && !ResourceDiskPolicy.IsDiskFull(error) && attempt < MaxRetries) {
                    // 即使重试间隔为零也主动让出执行，避免连续失败占满当前帧。
                    if (Delay > TimeSpan.Zero) {
                        await ResourceOperationBase.Delay(GetDelay(attempt + 1));
                    }
                    else {
                        await ResourceOperationBase.Yield();
                    }
                }
            }
        }
    }

    internal static class ResourceOperations
    {
        // 取消只终止当前等待者，底层共享操作继续；由主线程逐帧检查 token。
        public static ResourceOperationBase WaitAsync(ResourceOperationBase source, CancellationToken token)
        {
            return source == null
                ? throw new ArgumentNullException(nameof(source))
                : !token.CanBeCanceled ? source : OperationSystem.Start(new WaitOperation(source, token));
        }
        private sealed class WaitOperation: ResourceOperationBase
        {
            private readonly ResourceOperationBase m_source;
            private readonly CancellationToken m_token;
            internal WaitOperation(ResourceOperationBase source, CancellationToken token)
            { m_source = source; m_token = token; }
            protected override void OnUpdate()
            {
                m_token.ThrowIfCancellationRequested();
                Progress = m_source.Progress;
                if (!m_source.IsDone) {
                    return;
                }

                m_source.GetAwaiter().GetResult();
                Succeed();
            }
        }
    }
}
