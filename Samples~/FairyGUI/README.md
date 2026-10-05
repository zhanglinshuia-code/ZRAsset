# FairyGUI 桥接

`FairyGuiSdkAdapter.LoadAsync` 的实际 SDK 绑定。安装 FairyGUI，在样例 asmdef 添加 `FairyGUI` 引用，并启用 `ZRASSET_FAIRYGUI_SDK`。传入描述地址、预期 SDK 包 ID/名称、资源名前缀以及 `(name, extension) => address` 映射。适配器在注册前验证描述中的包身份并拒绝已有注册，设置 `DestroyMethod.None`，移除 SDK 包后才归还依赖 Handle。

注册包期间不允许另一接入路径同时修改 FairyGUI 全局包目录。

`FairyPackageLease` 不依赖 FairyGUI 程序集，通过委托适配项目中的 SDK。描述文件 `_fui.bytes` 作为 TextAsset，纹理/音频以 `name + extension` 对应业务地址。先通过 `PrepareSelectionAsync` 准备 UI 包所有依赖，SDK 的同步资源回调不能下载远端文件。

在引用 FairyGUI 和本示例的业务程序集里接线：

```csharp
var lease = await FairyPackageLease.CreateAsync(package, "ui/Main_fui.bytes", (bytes, load) =>
{
    object Load(string name, string extension, Type type, out FairyGUI.DestroyMethod destroyMethod)
    {
        destroyMethod = FairyGUI.DestroyMethod.None;
        return load("ui/" + name + extension, type);
    }
    FairyGUI.UIPackage ui = FairyGUI.UIPackage.AddPackage(bytes, "Main", Load);
    if (ui == null) { throw new InvalidOperationException("FairyGUI package import failed."); }
    return () => FairyGUI.UIPackage.RemovePackage(ui.id);
});
```

先销毁使用该包的所有 GObject，再 `lease.Dispose()`。SDK 不直接销毁底层 Unity 纹理；租约负责引用与释放。每个 SDK 包只建立一个租约，多个视图在上层共用它。注册委托如果在部分注册后抛错，须自行撤销 SDK 部分注册。

签名依据：[FairyGUI UIPackage 源码](https://github.com/fairygui/FairyGUI-unity/blob/master/Assets/Scripts/UI/UIPackage.cs)。示例不代表已经在项目的 FairyGUI 版本或导出资源上完成集成验收。
