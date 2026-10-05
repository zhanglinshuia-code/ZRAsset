using System;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>Unity 原生请求作为操作节点逐帧推进；等待过程没有每帧续体分配。</summary>
    internal static class UnityOperations
    {
        public static ResourceOperationBase WaitAsync(AsyncOperation operation)
        {
            return operation == null
                ? throw new ArgumentNullException(nameof(operation))
                : (ResourceOperationBase)OperationSystem.Start(new UnityRequestOperation(operation));
        }

        private sealed class UnityRequestOperation: ResourceOperationBase
        {
            private readonly AsyncOperation m_request;
            internal UnityRequestOperation(AsyncOperation request)
            {
                m_request = request;
            }

            protected override void OnUpdate()
            {
                Progress = m_request.progress;
                if (m_request.isDone) {
                    Succeed();
                }
            }
        }
    }
}
