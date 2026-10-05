# Wwise SoundBank 所有权桥接

`WwiseSdkBank.LoadAsync(package, address, maximumBytes, token)`，直接绑定 Wwise 2024 的 `AkUnitySoundEngine.LoadBankMemoryCopy` / `UnloadBank` 异步回调重载。安装匹配的 Wwise Unity SDK，为样例程序集添加 SDK API 引用，并启用 `ZRASSET_WWISE_2024`。此源码默认不参与编译，核心包不依赖商业 SDK。

MemoryCopy 路径从 RawFileHandle 有界异步读取，支持归档条目和解密结果。默认上限 64 MiB，加载期间有托管字节数组与 Wwise 副本的内存开销，大型/流式媒体采用下面的路径桥接及项目 I/O。初始化 Wwise 后持续派发 SDK 回调；先加载 Init Bank，应用负责停止事件，最后卸载对应 Bank。入队前取消会退出；入队后必须等 SDK 完成并交付 Bank 所有权，调用方随后 `DisposeAsync`，不能提前解除固定内存。卸载失败允许重试。

需要使用项目实际安装的 Wwise SDK 核对接口与运行行为。参考：[Wwise Bank 加载与内存语义](https://www.audiokinetic.com/library/2024.1.5_8803/?id=soundengine__banks__loading.html&source=SDK)、[Unity C# 接口与异步要求](https://www.audiokinetic.com/en/library/2024.1.1_8691/?id=unity_code.html&source=Unity)。

把 `.bnk` 配置为独立、未加密 RawFile。`SoundBankLease.LoadAsync` 先准备并持有文件，再把绝对路径传给项目 SDK 桥接。归档内部偏移、加密容器和无独立本地路径的 Web URL 会明确拒绝，不能伪装成普通 Bank 文件。

```csharp
SoundBankLease bank = await SoundBankLease.LoadAsync(package, "audio/main.bnk", LoadWwiseBank);
// 播放完成并停止相关事件后：
await bank.DisposeAsync();
```

`LoadWwiseBank` 的类型为：

```csharp
ResourceOperationBase<Func<ResourceOperationBase>> LoadWwiseBank(string absolutePath)
```

实现流程：按项目 Wwise 版本注册该文件目录并调用 Bank 加载 API；SDK 完成回调通过 `OperationCompletionSource<Func<ResourceOperationBase>>` 回到主线程，返回一个真正等待 Bank 卸载完成的委托。失败返回 `IOException`。加载失败时桥接必须撤销已取得的 SDK 所有权。

SDK 开始加载后本示例继续等待所有权交接，不因取消提前释放文件；调用方接回租约后再卸载。卸载失败保留文件句柄，允许再次 `DisposeAsync` 重试。原始文件句柄保护框架缓存和版本，但不会替第三方 SDK 停止音频事件。

Wwise 各版本的 API、初始化、路径解析与流式媒体规则由项目 SDK 适配；本示例不引入未安装的商业 SDK，也不声称已经完成该 SDK 验收。
