using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ZRAsset
{
    /// <summary>主线程有预算的分轮调度。优先级决定每轮顺序，新操作不能抢占尚未走完的一轮。</summary>
    public static class OperationSystem
    {
        private static readonly List<ResourceOperationBase> s_active = new();
        private static readonly List<ResourceOperationBase> s_pending = new();
        private static readonly ConcurrentQueue<Action> s_callbacks = new();
        private static readonly Comparison<ResourceOperationBase> s_compare = Compare;
        private static int s_mainThread;
        private static int s_cursor;
        private static bool s_updating;
        private static bool s_prioritiesDirty;
        private static long s_sequence;

        public static long TickIndex { get; private set; }
        public static double MaxMillisecondsPerUpdate { get; set; } = 5;
        public static int ActiveCount
        {
            get { return s_active.Count + s_pending.Count; }
        }
        public static int PendingCallbackCount
        {
            get { return s_callbacks.Count; }
        }
        internal static bool IsMainThread
        {
            get { return s_mainThread != 0 && Thread.CurrentThread.ManagedThreadId == s_mainThread; }
        }

        public static ResourceOperationDiagnostic[] CaptureDiagnostics()
        {
            CheckThread();
            var result = new List<ResourceOperationDiagnostic>(ActiveCount);
            AddDiagnostics(s_active, result);
            AddDiagnostics(s_pending, result);
            return result.ToArray();
        }

        private static void AddDiagnostics(List<ResourceOperationBase> source, List<ResourceOperationDiagnostic> target)
        {
            foreach (ResourceOperationBase operation in source) {
                if (operation.IsDone) { continue; }
                target.Add(new ResourceOperationDiagnostic
                {
                    Type = operation.GetType().Name,
                    Name = operation.DebugName,
                    State = operation.Status.ToString(),
                    Progress = operation.Progress,
                    Priority = operation.Priority,
                    Id = operation.DiagnosticId,
                    ParentId = operation.ParentDiagnosticId,
                    ElapsedMilliseconds = operation.ElapsedMilliseconds
                });
            }
        }

        public static void Initialize()
        {
            var current = Thread.CurrentThread.ManagedThreadId;
            if (s_mainThread != 0 && s_mainThread != current) {
                throw new InvalidOperationException("OperationSystem 只能从 Unity 主线程初始化。");
            }
            s_mainThread = current;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallPlayerLoop()
        {
            Initialize();
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            var systems = new List<PlayerLoopSystem>(loop.subSystemList ?? Array.Empty<PlayerLoopSystem>());
            systems.RemoveAll(item => item.type == typeof(OperationSystem));
            systems.Add(new PlayerLoopSystem { type = typeof(OperationSystem), updateDelegate = Update });
            loop.subSystemList = systems.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
        }

        internal static void MarkPrioritiesDirty()
        {
            s_prioritiesDirty = true;
        }

        internal static void Post(Action callback)
        {
            s_callbacks.Enqueue(callback ?? throw new ArgumentNullException(nameof(callback)));
        }

        public static T Start<T>(T operation) where T : ResourceOperationBase
        {
            CheckThread();
            if (operation == null) { throw new ArgumentNullException(nameof(operation)); }
            if (operation.IsDone || operation.IsScheduled) { return operation; }
            // 第一次 Tick 前标记，阻止 OnUpdate 重入 Start 导致重复调度。
            operation.IsScheduled = true;
            operation.ScheduleSequence = ++s_sequence;
            operation.Tick();
            if (!operation.IsDone) {
                s_pending.Add(operation);
                s_prioritiesDirty = true;
            }
            else {
                operation.IsScheduled = false;
            }
            return operation;
        }

        public static void Update()
        {
            CheckThread();
            if (s_updating) { return; }
            s_updating = true;
            TickIndex++;
            var started = Stopwatch.GetTimestamp();
            try {
                var count = s_callbacks.Count;
                for (var i = 0; i < count && s_callbacks.TryDequeue(out Action callback); i++) {
                    try { callback(); }
                    catch (Exception error) { UnityEngine.Debug.LogException(error); }
                    if (BudgetExpired(started)) { break; }
                }
                if (s_cursor >= s_active.Count) { BeginRound(); }
                // 每帧最多完成一轮。回调耗尽预算时仍至少推进一个操作。
                while (s_cursor < s_active.Count) {
                    ResourceOperationBase operation = s_active[s_cursor++];
                    operation.Tick();
                    if (BudgetExpired(started)) { break; }
                }
            }
            finally {
                try { ResourceTelemetry.Dispatch(); }
                finally { s_updating = false; }
            }
        }

        private static void BeginRound()
        {
            var write = 0;
            for (var read = 0; read < s_active.Count; read++) {
                ResourceOperationBase operation = s_active[read];
                if (operation.IsDone) { operation.IsScheduled = false; }
                else { s_active[write++] = operation; }
            }
            s_active.RemoveRange(write, s_active.Count - write);
            s_active.AddRange(s_pending);
            s_pending.Clear();
            if (s_prioritiesDirty) {
                s_active.Sort(s_compare);
                s_prioritiesDirty = false;
            }
            s_cursor = 0;
        }

        private static int Compare(ResourceOperationBase left, ResourceOperationBase right)
        {
            var order = ((right.OwnerPackage ?? right.SchedulingPackage)?.PackagePriority ?? 0).CompareTo((left.OwnerPackage ?? left.SchedulingPackage)?.PackagePriority ?? 0);
            if (order == 0) { order = right.Priority.CompareTo(left.Priority); }
            return order == 0 ? left.ScheduleSequence.CompareTo(right.ScheduleSequence) : order;
        }

        private static bool BudgetExpired(long started)
        {
            return MaxMillisecondsPerUpdate > 0 &&
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency >= MaxMillisecondsPerUpdate;
        }

        private static void CheckThread()
        {
            if (!IsMainThread) { throw new InvalidOperationException("请在 Unity 主线程调度操作。"); }
        }
    }
}
