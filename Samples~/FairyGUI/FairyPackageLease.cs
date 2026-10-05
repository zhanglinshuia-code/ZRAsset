using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZRAsset.Integrations
{
    /// <summary>对接 UIPackage.AddPackage(bytes, name, loadResource)；删除所有 GObject 后先 RemovePackage，再释放此租约。</summary>
    public sealed class FairyPackageLease: IDisposable
    {
        private readonly ResourcePackage m_package;
        private readonly List<AssetHandle<Object>> m_dependencies = new();
        private Action m_removePackage;
        private bool m_disposed;

        private FairyPackageLease(ResourcePackage package)
        {
            m_package = package;
        }

        public static async ResourceOperationBase<FairyPackageLease> CreateAsync(ResourcePackage package, string descriptorAddress,
            Func<byte[], Func<string, Type, Object>, Action> addPackage, CancellationToken token = default)
        {
            if (package == null) { throw new ArgumentNullException(nameof(package)); }
            if (addPackage == null) { throw new ArgumentNullException(nameof(addPackage)); }
            var lease = new FairyPackageLease(package);
            try {
                using AssetHandle<TextAsset> bytes = package.LoadAssetAsync<TextAsset>(descriptorAddress, token);
                TextAsset descriptor = await bytes.Operation;
                token.ThrowIfCancellationRequested();
                lease.m_removePackage = addPackage(descriptor.bytes, lease.LoadDependency) ?? throw new InvalidOperationException("必须返回 SDK RemovePackage 回调。");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private Object LoadDependency(string address, Type type)
        {
            if (m_disposed) { throw new ObjectDisposedException(nameof(FairyPackageLease)); }
            AssetHandle<Object> handle = m_package.LoadAssetSync(address, type);
            m_dependencies.Add(handle);
            return handle.Asset;
        }

        public void Dispose()
        {
            if (m_disposed) { return; }
            // SDK 删除失败保留资源，调用方可重试，不能让 SDK 持有已卸载纹理。
            m_removePackage?.Invoke();
            m_disposed = true;
            foreach (AssetHandle<Object> handle in m_dependencies) { handle.Release(); }
            m_dependencies.Clear();
        }
    }
}
