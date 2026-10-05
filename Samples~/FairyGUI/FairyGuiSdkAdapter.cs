#if ZRASSET_FAIRYGUI_SDK
using System;
using System.Threading;
using FairyGUI;
using FairyGUI.Utils;

namespace ZRAsset.Integrations
{
    /// <summary>FairyGUI Unity 的具体绑定；依赖由 ZRAsset 持有，SDK 不得自行销毁纹理/音频。</summary>
    public static class FairyGuiSdkAdapter
    {
        public static ResourceOperationBase<FairyPackageLease> LoadAsync(ResourcePackage package, string descriptorAddress,
            string expectedPackageId, string expectedPackageName, string assetNamePrefix,
            Func<string, string, string> resolveAddress, CancellationToken token = default)
        {
            return string.IsNullOrEmpty(expectedPackageId) || string.IsNullOrEmpty(expectedPackageName)
                ? throw new ArgumentException("必须提供 FairyGUI 包的 ID 和名称，以防覆盖已有注册。")
                : resolveAddress == null
                ? throw new ArgumentNullException(nameof(resolveAddress))
                : FairyPackageLease.CreateAsync(package, descriptorAddress, (bytes, load) =>
            {
                var header = new ByteBuffer(bytes);
                if (header.ReadUint() != 0x46475549) { throw new InvalidOperationException("FairyGUI 描述格式无效。"); }
                header.ReadInt(); header.ReadBool();
                if (header.ReadString() != expectedPackageId || header.ReadString() != expectedPackageName) {
                    throw new InvalidOperationException("FairyGUI 包身份与配置不一致。");
                }
                if (UIPackage.GetById(expectedPackageId) != null || UIPackage.GetByName(expectedPackageName) != null) {
                    throw new InvalidOperationException("FairyGUI 包已经注册，请复用现有租约。");
                }
                UIPackage ui = UIPackage.AddPackage(bytes, assetNamePrefix ?? "",
                    (string name, string extension, Type type, out DestroyMethod destroyMethod) =>
                    {
                        destroyMethod = DestroyMethod.None;
                        return load(resolveAddress(name, extension), type);
                    }) ?? throw new InvalidOperationException("FairyGUI 无法解析包描述。");
                if (ui.id != expectedPackageId || ui.name != expectedPackageName) {
                    UIPackage.RemovePackage(ui.id);
                    throw new InvalidOperationException("FairyGUI 包身份与配置不一致。");
                }
                return () => UIPackage.RemovePackage(ui.id);
            }, token);
        }
    }
}
#endif
