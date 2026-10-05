# 平台与第三方集成

[文档首页](../index.md) · [发布服务](release-service.md) · 下一篇：[诊断](troubleshooting.md)

平台支持需要分别判断代码存在、编译通过、宿主运行、设备性能四个层面。条件编译通过不能证明目标 SDK、图形、音频、文件配额和系统权限都满足发行需求。

## 平台文件能力

Windows 等本地文件平台可以使用磁盘缓存和后台文件工作。Android 首包可能位于 APK 内，路径不能简单当作普通 FileStream 路径。WebGL 使用浏览器/宿主文件能力和持久化桥，不能假设有桌面线程和磁盘语义。

ResourceFileLocation 和 IResourceFileSystem 描述来源及能力。同步读取、Unity 原生加载、缓存持久化等能力应实际检查。自定义来源通过 ResourceSourceOptions 提供工厂和只读检查接口；离线模式下不得触发平台下载。

## PAD 与自定义平台来源

GooglePlayDeliveryProvider 将 Bundle 映射到 asset pack。常见次序是目标缓存、首包/已安装来源、跨版本复用、平台准备、CDN。只读计划不应触发平台下载；PrepareMissing 为 false 时只使用已安装内容。

需要系统确认的状态由宿主 UI 回调处理。缺失或损坏内容可以回退 CDN；取消、超时、权限或交互失败应保持错误语义。取消本消费者等待不应全局取消其他消费者正在使用的 asset pack。

## 样例入口

| 集成 | 文档 | 所有权重点 |
| --- | --- | --- |
| UI Toolkit | [README](../../Samples~/UIToolkit/README.md) | 先拆除视图使用，再释放布局资源 |
| UniTask | [README](../../Samples~/UniTask/README.md) | 桥接等待与框架操作，遵守主线程约束 |
| FairyGUI | [README](../../Samples~/FairyGUI/README.md) | 先删除 GObject，再移除包并释放 Handle |
| Wwise | [README](../../Samples~/Wwise/README.md) | SoundBank 生命周期、异步回调与内存复制语义 |
| 小游戏 | [README](../../Samples~/MiniGame/README.md) | SDK 下载、文件搬运、Bundle 提取和匹配卸载 |

可选宏和程序集引用必须匹配真正安装的 SDK。不能仅因无宏时核心编译通过就宣称适配器可运行。

## WebGL 与小游戏

JS 桥处理下载、进度、取消、持久化和临时文件搬运。不同宿主的下载路径、进度可靠性和配额不同，应通过对应策略声明，而不是把一个浏览器替身当作所有平台。

分块复制能限制每次搬运工作，却不能消除文件系统、浏览器和 Unity 单次调用的不可抢占时间。大清单推荐预热和异步入口；必须在实际浏览器/宿主中检查内存峰值、首屏帧耗时和重启恢复。

## 接入检查

Wwise SDK、真实 UI/音频、PAD、iOS、小游戏宿主及浏览器设备运行仍需要对应环境。实际使用的平台至少验证正常下载、断网、后台切换、取消、配额不足、重启离线、文件损坏和反复加载释放。
