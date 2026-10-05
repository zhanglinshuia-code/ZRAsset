# 快速入门

[文档首页](../index.md) · [安装](installation.md) · 下一篇：[收集与构建](building.md)

本章建立文本资源 → 命名包 → 首包 → Scope 加载 → 释放的最小闭环，不需要 HTTP 服务和 HybridCLR。以下以 Windows Editor / StandaloneWindows64 为例。

## 先运行随包示例

1. 在 Package Manager 中选择 ZRAsset，从 Samples 导入 **Core examples**。
2. 执行 **Tools → ZRAsset → 示例 → 构建并打开快速入门**。菜单会按当前目标平台构建、复制首包并打开示例场景。
3. 点击 Play。场景和 Console 会显示 `AssetBundle 加载成功` / `Hello ZRAsset!`。

示例使用独立包名 `zrasset-quickstart`，包含 [文本资源](../../Samples~/Core/QuickStart/Greeting.txt)、[构建配置](../../Samples~/Core/QuickStart/QuickStartBuildConfig.asset)和[场景](../../Samples~/Core/QuickStart/QuickStart.unity)。切换目标平台后重新执行示例菜单，生成匹配平台的 Bundle。第一次构建需要等待 Unity 完成。

下面介绍如何手动创建自己的 `demo` 包。

## 创建资源和配置

1. 创建 `Assets/GameContent/Greeting.txt`，写入 `Hello ZRAsset`。
2. 在 Project 窗口选择 **Create → ZRAsset → 资源构建配置**。
3. 设置 `PackageName = demo`、`PackageVersion = 1.0`，构建后端 BuiltIn，压缩 Lz4。
4. 在 `Entries` 添加一项：`Address = demo/greeting`，Asset 指向 Greeting.txt，`BundleName = demo.bundle`，FileType 为 AssetBundle。
5. 保存配置，确认地址大小写。


## 构建和首包

选中配置执行 **Tools → ZRAsset → 构建所选资源配置**。默认输出 `Build/ZRAsset/StandaloneWindows64/Packages/demo/1.0/`。

保持选中，执行 **Tools → ZRAsset → 拷贝当前构建到首包目录**。首包位于 `Assets/StreamingAssets/ZRAsset/Packages/demo/`，包含清单和其引用文件。资源版本写在清单中，首包目录不再追加版本层。不要只复制 Bundle，也不要把上层 Build 目录当作初始化根。

## 运行示例

UPM 用户导入 Core examples。在空场景创建 GameObject，添加 `PackageQuickStart`，保持 Package Name 为 demo、Address 为 demo/greeting。进入 Play Mode，Console 应输出 `ZRAsset 加载成功：Hello ZRAsset`。

[PackageQuickStart.cs](../../Samples~/Core/PackageQuickStart.cs) 处理了初始化期间对象被销毁的情况：先取消，等待初始化退出，再按 Scope → Package 顺序关闭。每个示例独占其包名，不要让两个组件同时拥有 demo 包。

## 最小业务调用

下面完整方法从 Unity 主线程调用，一次读取完成后关闭自己拥有的包：

```csharp
using System.Threading;
using UnityEngine;
using ZRAsset;

public static class ReadGreeting
{
    public static async ResourceOperationBase<string> RunAsync(CancellationToken token)
    {
        var package = ResourcePackages.CreatePackage("demo");
        try {
            await package.InitializeAsync(new ResourceInitializationOptions(), token);
            await using (var scope = package.CreateScope("read-greeting")) {
                TextAsset asset = await scope.LoadAsync<TextAsset>("demo/greeting", token);
                return asset.text;
            }
        }
        finally { await package.DisposeAsync(); }
    }
}
```

返回字符串可以脱离 TextAsset 使用。如果返回 Unity 对象并继续使用，Scope 必须继续存活；裸对象引用不会增加框架引用计数。正式游戏由启动模块持有长期 Package，每个界面或阶段创建 Scope，不要为每个图标重复创建包。

## 回收与退出

Scope 关闭归还所有权，默认延迟卸载 5 秒。回收需要持续推进；示例 Update 调用 `package.UnloadUnused()`，应用也可使用高级管理器的 `AutoUnloadUnused`。释放、延迟回收与 Unity 原生内存归还的区别见[生命周期](loading.md)。

| 表现 | 检查 |
| --- | --- |
| 清单不存在 | 是否复制首包、根路径是否准确 |
| 包身份不匹配 | 配置与 CreatePackage 是否都叫 demo |
| Unknown address | 是否按地址重新构建，大小写是否一致 |
| 类型不匹配 | 文本是否作为 AssetBundle 构建，代码是否请求 TextAsset |
| 包已注册 | 是否存在第二个包拥有者或关闭失败 |
| 平台不匹配 | 清单是否来自正确构建目标 |
