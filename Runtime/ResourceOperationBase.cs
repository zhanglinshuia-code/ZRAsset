using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ZRAsset
{
    public enum OperationStatus { None, Processing, Succeeded, Failed, Canceled }

    /// <summary>
    /// ZRAsset 操作的共同生命周期。支持协程、完成事件和 await；不创建或调度 .NET Task。
    /// 尚未完成时读取结果会报错，绝不阻塞 Unity 主线程。
    /// </summary>
    [AsyncMethodBuilder(typeof(OperationMethodBuilder))]
    public abstract class ResourceOperationBase: IEnumerator
    {
        private Action<ResourceOperationBase> m_completed;
        private AggregateException m_aggregate;
        private uint m_priority;
        public OperationStatus Status { get; private set; } = OperationStatus.Processing;
        public bool IsDone { get { return Status == OperationStatus.Succeeded || Status == OperationStatus.Failed || Status == OperationStatus.Canceled; } }
        public bool IsCanceled { get { return Status == OperationStatus.Canceled; } }
        public bool IsFaulted { get { return Status == OperationStatus.Failed; } }
        public Exception Error { get; private set; }
        public ResourceFailure Failure { get { return ResourceFailure.FromException(Error); } }
        public AggregateException Exception { get { return IsFaulted ? m_aggregate ??= Error as AggregateException ?? new AggregateException(Error) : null; } }
        public float Progress { get; protected set; }
        public string DebugName { get; set; }
        private static long s_nextDiagnosticId;
        public long DiagnosticId { get; } = Interlocked.Increment(ref s_nextDiagnosticId);
        public long ParentDiagnosticId { get; set; }
        private readonly long m_startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        private long m_finishedTimestamp;
        public double ElapsedMilliseconds { get { return ((IsDone ? m_finishedTimestamp : System.Diagnostics.Stopwatch.GetTimestamp()) - m_startedTimestamp) * 1000d / System.Diagnostics.Stopwatch.Frequency; } }
        public uint Priority
        {
            get { return m_priority; }
            set { m_priority = value; OperationSystem.MarkPrioritiesDirty(); }
        }
        public event Action<ResourceOperationBase> Completed
        {
            add
            {
                if (value == null) {
                    return;
                }
                if (IsDone) {
                    OperationSystem.Post(() => value(this));
                }
                else {
                    m_completed += value;
                }
            }
            remove { m_completed -= value; }
        }

        internal bool IsScheduled { get; set; }
        internal long ScheduleSequence { get; set; }
        internal ResourcePackage OwnerPackage { get; set; }
        internal ResourcePackage SchedulingPackage { get; set; } = ResourceScheduling.Current;

        /// <summary>成功、失败或取消时恰好调用一次，在完成事件之前释放操作持有的临时资源。</summary>
        protected virtual void OnCleanup() { }

        protected abstract void OnUpdate();
        internal void Tick()
        {
            if (IsDone) {
                return;
            }
            try { using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(OwnerPackage ?? SchedulingPackage); OnUpdate(); }
            catch (Exception exception) { Fail(exception); }
        }
        protected void Succeed() { Finish(null); }
        protected void Fail(Exception exception) { Finish(exception ?? new InvalidOperationException("操作失败。")); }
        internal void Finish(Exception exception)
        {
            if (IsDone) {
                return;
            }
            m_finishedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            Error = exception;
            Status = exception == null ? OperationStatus.Succeeded :
                exception is OperationCanceledException ? OperationStatus.Canceled : OperationStatus.Failed;
            Progress = 1;
            try { OnCleanup(); }
            catch (Exception cleanupError) {
                Error = exception == null ? cleanupError : ResourceFailure.WithCleanup(exception, cleanupError);
                Status = OperationStatus.Failed;
            }
            OwnerPackage?.ForgetOperation(this);
            OwnerPackage = null;
            SchedulingPackage = null;
            Action<ResourceOperationBase> callbacks = m_completed;
            m_completed = null;
            if (callbacks != null) {
                OperationSystem.Post(() =>
            {
                foreach (Action<ResourceOperationBase> callback in callbacks.GetInvocationList()) {
                    try { callback(this); }
                    catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
                }
            });
            }
        }
        internal void ThrowIfUnsuccessful()
        {
            if (!IsDone) {
                throw new InvalidOperationException("操作尚未完成，请先 await 或 yield return 操作。");
            }
            if (Error != null) {
                ExceptionDispatchInfo.Capture(Error).Throw();
            }
        }
        public OperationAwaiter GetAwaiter() { return new OperationAwaiter(this); }
        object IEnumerator.Current { get { return null; } }
        bool IEnumerator.MoveNext() { return !IsDone; }
        void IEnumerator.Reset() { throw new NotSupportedException(); }
        public static ResourceOperationBase CompletedOperation { get; } = FromResult(true);
        public static ResourceOperationBase<T> FromResult<T>(T result)
        {
            var operation = new CompletionOperation<T>(); operation.Complete(result); return operation;
        }
        public static ResourceOperationBase FromException(Exception error) { return FromException<bool>(error); }
        public static ResourceOperationBase<T> FromException<T>(Exception error)
        {
            if (error == null) {
                throw new ArgumentNullException(nameof(error));
            }
            var operation = new CompletionOperation<T>(); operation.Finish(error); return operation;
        }
        public static ResourceOperationBase<T> FromCanceled<T>(CancellationToken token) { return FromException<T>(new OperationCanceledException(token)); }
        public static ResourceOperationBase Yield() { return OperationSystem.Start(new DelayOperation(TimeSpan.Zero, default)); }
        public static ResourceOperationBase Delay(int milliseconds, CancellationToken token = default) { return Delay(TimeSpan.FromMilliseconds(milliseconds), token); }
        public static ResourceOperationBase Delay(TimeSpan delay, CancellationToken token = default)
        {
            return delay < TimeSpan.Zero
                ? throw new ArgumentOutOfRangeException(nameof(delay))
                : (ResourceOperationBase)OperationSystem.Start(new DelayOperation(delay, token));
        }
        public static ResourceOperationBase WhenAll(params ResourceOperationBase[] operations) { return WhenAll((IEnumerable<ResourceOperationBase>)operations); }
        public static ResourceOperationBase WhenAll(IEnumerable<ResourceOperationBase> operations) { return OperationSystem.Start(new GroupOperation(Snapshot(operations), false)); }
        public static ResourceOperationBase<T[]> WhenAll<T>(params ResourceOperationBase<T>[] operations) { return WhenAll((IEnumerable<ResourceOperationBase<T>>)operations); }
        public static async ResourceOperationBase<T[]> WhenAll<T>(IEnumerable<ResourceOperationBase<T>> operations)
        {
            ResourceOperationBase<T>[] items = operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations));
            await WhenAll(items.Cast<ResourceOperationBase>());
            var results = new T[items.Length];
            for (var i = 0; i < items.Length; i++) {
                results[i] = items[i].Result;
            }
            return results;
        }
        public static ResourceOperationBase<ResourceOperationBase> WhenAny(params ResourceOperationBase[] operations) { return OperationSystem.Start(new GroupOperation(Snapshot(operations), true)); }
        private static ResourceOperationBase[] Snapshot(IEnumerable<ResourceOperationBase> operations)
        {
            ResourceOperationBase[] items = operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations));
            return items.Any(item => item == null) ? throw new ArgumentException("操作集合不能包含 null。", nameof(operations)) : items;
        }

        /// <summary>只用于纯 System 文件/哈希工作；完成结果由主线程调度器接收。</summary>
        public static ResourceOperationBase<T> Run<T>(Func<T> work)
        {
            if (work == null) {
                throw new ArgumentNullException(nameof(work));
            }
            var completion = new OperationCompletionSource<T>();
#if UNITY_WEBGL && !UNITY_EDITOR
            try { completion.SetResult(work()); } catch (Exception ex) { completion.SetException(ex); }
#else
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { completion.TrySetResult(work()); } catch (Exception ex) { completion.TrySetException(ex); }
            });
#endif
            return completion.Operation;
        }
        public static ResourceOperationBase Run(Action work) { return Run(() => { work(); return true; }); }
    }

    [AsyncMethodBuilder(typeof(OperationMethodBuilder<>))]
    public abstract class ResourceOperationBase<T>: ResourceOperationBase
    {
        private T m_result;
        public T Result { get { ThrowIfUnsuccessful(); return m_result; } }
        protected void Succeed(T value) { if (IsDone) { return; } m_result = value; Succeed(); }
        public new OperationAwaiter<T> GetAwaiter() { return new OperationAwaiter<T>(this); }
    }

    public readonly struct OperationAwaiter: ICriticalNotifyCompletion
    {
        private readonly ResourceOperationBase m_operation;
        internal OperationAwaiter(ResourceOperationBase operation) { m_operation = operation; }
        public bool IsCompleted { get { return m_operation.IsDone; } }
        public void GetResult() { m_operation.ThrowIfUnsuccessful(); }
        public void OnCompleted(Action continuation) { UnsafeOnCompleted(continuation); }
        public void UnsafeOnCompleted(Action continuation) { m_operation.Completed += _ => continuation(); }
    }
    public readonly struct OperationAwaiter<T>: ICriticalNotifyCompletion
    {
        private readonly ResourceOperationBase<T> m_operation;
        internal OperationAwaiter(ResourceOperationBase<T> operation) { m_operation = operation; }
        public bool IsCompleted { get { return m_operation.IsDone; } }
        public T GetResult() { return m_operation.Result; }
        public void OnCompleted(Action continuation) { UnsafeOnCompleted(continuation); }
        public void UnsafeOnCompleted(Action continuation) { m_operation.Completed += _ => continuation(); }
    }

    internal sealed class CompletionOperation<T>: ResourceOperationBase<T>
    {
        protected override void OnUpdate() { }
        internal void Complete(T value) { Succeed(value); }
    }

    /// <summary>外部回调/工作线程只能提交一次结果；状态发布与订阅回调都回到操作线程。</summary>
    public sealed class OperationCompletionSource<T>
    {
        private readonly CompletionOperation<T> m_operation = new CompletionOperation<T>();
        private int m_submitted;
        public ResourceOperationBase<T> Operation { get { return m_operation; } }
        public bool TrySetResult(T result) { return Submit(() => m_operation.Complete(result)); }
        public bool TrySetException(Exception error)
        {
            return error == null ? throw new ArgumentNullException(nameof(error)) : Submit(() => m_operation.Finish(error));
        }
        public bool TrySetCanceled(CancellationToken token = default) { return TrySetException(new OperationCanceledException(token)); }
        public void SetResult(T result) { if (!TrySetResult(result)) { throw new InvalidOperationException("操作已完成。"); } }
        public void SetException(Exception error) { if (!TrySetException(error)) { throw new InvalidOperationException("操作已完成。"); } }
        public void SetCanceled() { if (!TrySetCanceled()) { throw new InvalidOperationException("操作已完成。"); } }
        private bool Submit(Action publish)
        {
            if (Interlocked.CompareExchange(ref m_submitted, 1, 0) != 0) {
                return false;
            }
            if (OperationSystem.IsMainThread) { publish(); }
            else {
                OperationSystem.Post(publish);
            }
            return true;
        }
    }

    // C# 编译器负责生成流程状态机；续体交给 OperationSystem，编译器要求返回属性名为 Task。
    // 使用同一 builder 引用保留首次装箱的状态机，后续 await 不会复制一条新的执行链。
    public sealed class OperationMethodBuilder
    {
        private readonly CompletionOperation<bool> m_operation = new CompletionOperation<bool>();
        private IAsyncStateMachine m_machine;
        private Action m_resume, m_moveNext;
        public static OperationMethodBuilder Create() { return new OperationMethodBuilder(); }
        public ResourceOperationBase Task { get { return m_operation; } }
        public void Start<T>(ref T stateMachine) where T : IAsyncStateMachine { stateMachine.MoveNext(); }
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult() { m_operation.Complete(true); }
        public void SetException(Exception exception) { m_operation.Finish(exception); }
        private Action Continuation<T>(ref T stateMachine) where T : IAsyncStateMachine
        {
            if (m_machine == null) { m_machine = stateMachine; m_moveNext = Resume; m_resume = () => OperationSystem.Post(m_moveNext); }
            return m_resume;
        }
        private void Resume()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(m_operation.SchedulingPackage);
            m_machine.MoveNext();
        }
        public void AwaitOnCompleted<TAwaiter, T>(ref TAwaiter awaiter, ref T stateMachine)
            where TAwaiter : INotifyCompletion where T : IAsyncStateMachine
        { awaiter.OnCompleted(Continuation(ref stateMachine)); }
        public void AwaitUnsafeOnCompleted<TAwaiter, T>(ref TAwaiter awaiter, ref T stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where T : IAsyncStateMachine
        { awaiter.UnsafeOnCompleted(Continuation(ref stateMachine)); }
    }
    public sealed class OperationMethodBuilder<TResult>
    {
        private readonly CompletionOperation<TResult> m_operation = new CompletionOperation<TResult>();
        private IAsyncStateMachine m_machine;
        private Action m_resume, m_moveNext;
        public static OperationMethodBuilder<TResult> Create() { return new OperationMethodBuilder<TResult>(); }
        public ResourceOperationBase<TResult> Task { get { return m_operation; } }
        public void Start<T>(ref T stateMachine) where T : IAsyncStateMachine { stateMachine.MoveNext(); }
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult(TResult result) { m_operation.Complete(result); }
        public void SetException(Exception exception) { m_operation.Finish(exception); }
        private Action Continuation<T>(ref T stateMachine) where T : IAsyncStateMachine
        {
            if (m_machine == null) { m_machine = stateMachine; m_moveNext = Resume; m_resume = () => OperationSystem.Post(m_moveNext); }
            return m_resume;
        }
        private void Resume()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(m_operation.SchedulingPackage);
            m_machine.MoveNext();
        }
        public void AwaitOnCompleted<TAwaiter, T>(ref TAwaiter awaiter, ref T stateMachine)
            where TAwaiter : INotifyCompletion where T : IAsyncStateMachine
        { awaiter.OnCompleted(Continuation(ref stateMachine)); }
        public void AwaitUnsafeOnCompleted<TAwaiter, T>(ref TAwaiter awaiter, ref T stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where T : IAsyncStateMachine
        { awaiter.UnsafeOnCompleted(Continuation(ref stateMachine)); }
    }

    internal sealed class DelayOperation: ResourceOperationBase
    {
        private readonly long m_deadline;
        private readonly long m_nextTick = OperationSystem.TickIndex + 1;
        private readonly CancellationToken m_token;
        internal DelayOperation(TimeSpan delay, CancellationToken token)
        { m_deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency); m_token = token; }
        protected override void OnUpdate()
        {
            m_token.ThrowIfCancellationRequested();
            if (OperationSystem.TickIndex >= m_nextTick && System.Diagnostics.Stopwatch.GetTimestamp() >= m_deadline) {
                Succeed();
            }
        }
    }

    internal sealed class GroupOperation: ResourceOperationBase<ResourceOperationBase>
    {
        private readonly ResourceOperationBase[] m_operations;
        private readonly bool m_any;
        internal GroupOperation(ResourceOperationBase[] operations, bool any)
        {
            if (any && operations.Length == 0) {
                throw new ArgumentException("WhenAny 至少需要一个操作。");
            }
            m_operations = operations; m_any = any;
        }
        protected override void OnUpdate()
        {
            var done = 0;
            Exception firstError = null;
            OperationCanceledException cancellation = null;
            foreach (ResourceOperationBase operation in m_operations) {
                if (!operation.IsDone) {
                    continue;
                }
                if (m_any) { Succeed(operation); return; }
                done++;
                if (operation.IsFaulted && firstError == null) {
                    firstError = operation.Error;
                }
                if (operation.IsCanceled && cancellation == null) {
                    cancellation = (OperationCanceledException)operation.Error;
                }
            }
            Progress = m_operations.Length == 0 ? 1 : (float)done / m_operations.Length;
            // 失败时仍等待其余子操作收尾，保证下载锁、文件句柄和资源引用能安全排空。
            if (done == m_operations.Length) { if (firstError != null || cancellation != null) { Fail(firstError ?? cancellation); } else { Succeed(null); } }
        }
    }
}
