# ZRAsset

Unity 6 AssetBundle 资源管理系统，提供资源收集与构建、异步加载、生命周期管理、下载和版本更新。当前包版本为 **0.35.0**。

## 安装

在 Unity Package Manager 中选择 **Install package from git URL**（部分版本显示 **Add package from git URL**），输入：

```text
https://github.com/zhanglinshuia-code/ZRAsset.git#v0.35.0
```

需要 Unity 6000.0 或更新版本，推荐使用 6000.3.11f1。系统需安装 Git。也可克隆仓库后通过 **Install package from disk** 选择根目录的 `package.json`。

也可从 [v0.35.0 Release](https://github.com/zhanglinshuia-code/ZRAsset/releases/tag/v0.35.0) 下载 `com.zrasset.core-0.35.0.tgz`，在 Package Manager 中通过 **Install package from tarball** 安装。

## 开始使用

1. 阅读[安装与依赖](Documentation~/manual/installation.md)。
2. 在 Package Manager 的 Samples 中导入 **Core examples**。
3. 执行 **Tools → ZRAsset → 示例 → 构建并打开快速入门**，点击 Play，场景中会显示真实 AssetBundle 的加载结果。

示例已包含文本资源、构建配置和场景。手动配置和接入自己的资源见[快速入门](Documentation~/manual/quick-start.md)。

推荐使用 `ResourcePackage` 管理命名包、`ResourceScope` 管理界面或场景的资源所有权。资源 API 在 Unity 主线程调用，异步操作支持 await 和协程。

## 使用文档

- [完整使用手册](Documentation~/index.md)
- [资源收集与构建](Documentation~/manual/building.md)
- [加载与生命周期](Documentation~/manual/loading.md)
- [下载与版本更新](Documentation~/manual/updates.md)
- [启动和 HybridCLR](Documentation~/manual/startup.md)
- [平台与第三方集成](Documentation~/manual/platforms.md)
- [诊断与常见问题](Documentation~/manual/troubleshooting.md)
- [API 导航](Documentation~/reference/api.md)和[配置参考](Documentation~/reference/configuration.md)

## 包内容

| 目录 | 用途 |
| --- | --- |
| Runtime | AssetBundle、RawFile、下载、版本和生命周期管理 |
| Editor | 资源收集、构建、加密签名、热更配置和诊断工具 |
| Plugins/WebGL | WebGL 与小游戏下载、持久化桥接 |
| Samples~ | 按需导入的使用示例和第三方集成 |
| Documentation~ | 安装、使用步骤、API 与配置参考 |

Built-in 构建后端可独立使用；SBP、HybridCLR、UniTask 和其他 SDK 按需安装。可选集成的依赖和设置见对应 Samples 的 README。

## 许可证

Copyright 2026 zhanglinshuia-code。项目使用 [Apache License 2.0](LICENSE)。

## 致谢

感谢 **YooAsset** 项目及其作者和贡献者的开源分享，为 Unity 资源管理提供了宝贵的设计思路与实践参考。

- 项目仓库：[tuyoogame/YooAsset](https://github.com/tuyoogame/YooAsset)
