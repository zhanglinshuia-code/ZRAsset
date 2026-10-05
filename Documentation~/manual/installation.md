# 安装与依赖

[文档首页](../index.md) · 下一篇：[快速入门](quick-start.md)

## 环境与版本

包声明 Unity 6000.0 起的版本要求，推荐使用 6000.3.11f1。包声明不代表所有 Unity 补丁和设备都完成测试。核心不依赖 YooAsset、Addressables 或 UniTask。实际依赖以 [package.json](../../package.json) 为准。

## UPM 安装

在 Package Manager 选择 **Install package from git URL**（或 **Add package from git URL**），输入：

```text
https://github.com/zhanglinshuia-code/ZRAsset.git#v0.35.0
```

系统需要安装 Git。也可先克隆仓库，然后选择 **Install package from disk**，指定克隆目录中的 `package.json`。包安装完成后，从 Samples 导入 **Core examples**，其他集成按需导入。

离线安装可从 [v0.35.0 Release](https://github.com/zhanglinshuia-code/ZRAsset/releases/tag/v0.35.0) 下载 `com.zrasset.core-0.35.0.tgz`，选择 **Install package from tarball** 并指定该文件。此方式不需要 Git，但 Unity 仍需具备包声明的依赖。

不要直接修改 `Library/PackageCache`。需要定制时使用自己的克隆目录或嵌入包；同一工程只保留一套 ZRAsset，避免重复程序集。

## 可选依赖

| 组件 | 用途 | 注意事项 |
| --- | --- | --- |
| SBP | 可选构建后端 | 适配程序集通过包版本条件启用，当前以 2.6.1 为条件起点 |
| HybridCLR | IL2CPP 热更代码 | 宿主单独安装匹配包和本地工具链 |
| UniTask | 业务异步桥接 | 先安装依赖，再导入样例 |
| UI Toolkit | 界面布局与资源所有权 | 导入对应样例 |
| FairyGUI、Wwise、小游戏 SDK | 对应 UI、音频、宿主接入 | 按样例 README 配置宏和程序集引用 |

框架不会自动安装商业 SDK 或配置生产密钥。

## 安装完成检查

Console 无编译错误，能创建 `ZRAsset/资源构建配置`，能打开 `Tools/ZRAsset/资源收集`。随后执行快速入门中的真实构建与加载。仅出现菜单不代表资源路径和平台内容已经正确。
