# 核心示例

首次接入使用 [PackageQuickStart](PackageQuickStart.cs)，步骤见[快速入门](../../Documentation~/manual/quick-start.md)。该示例采用命名 Package 和 Scope。

| 示例 | 用途 |
| --- | --- |
| PackageQuickStart | 推荐入门，命名包、Scope、初始化取消及关闭 |
| ResourceExample | 直接创建 ResourceManager 的兼容/底层演示 |
| LifecycleExample | 底层管理器的实例与场景生命周期 |
| DownloadExample | 下载基础流程 |
| MultiPackageExample | 本机受信任无签名多包演示，不作为生产信任配置 |
| SignedReleaseExample | 签名版本接入参考 |
| HotUpdateBootstrapComponent | 热更启动组件参考 |
| ProductionStartupComponent | 下载确认和业务就绪 UI 接线 |

独立示例通常自己拥有管理器或包；正式应用应由启动模块统一管理长期包，业务模块管理各自 Scope。不要把多个独占同一包名的示例同时挂到场景。
