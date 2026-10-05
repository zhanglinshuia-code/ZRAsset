# 微信小游戏 SDK 适配

导入本示例后，安装并初始化官方微信 Unity/团结转换 SDK，使用 SDK 导出的 `WEIXINMINIGAME` 或 `UNITY_WECHATMINIGAME` 宏。`WechatResourceAdapter.InitializePackage(package, manifest, versionedRoot, network, retry)` 完成文件系统、原生预下载和 Bundle 加载器接线。远端根目录要求 HTTPS，版本目录不可变。

`WXAssetBundle.GetAssetBundle`、`DownloadHandlerWXAssetBundle.assetBundle` 和 `WXUnload` 来自同一 SDK；不使用普通 `Unload` 替代 WX 卸载。预下载使用 `wechatminigame-preload` 请求头，无正文下载处理器。默认缓存查询保守返回 false，避免每包同步调用 `WX.GetCachePath`；成功表示 SDK 请求完成，不声称能精确获得平台缓存或字节进度。

SDK 提供的 DLL 必须允许本程序集引用；如果 SDK 改为 asmdef 源码程序集，将实际 SDK 程序集名添加到本示例 asmdef 的 references。核心 ZRAsset 不强制依赖该 SDK。

加密 Bundle、RawFile 和 Archive 走现有文件下载/持久化桥和本地加载流程，不经过 WX 原生 Bundle URL 接口。其他宿主实现 `IMiniGamePlatformStrategy` 的三个方法即可配套其 SDK，请勿把某个平台的 Bundle 交给另一平台卸载。现有 wx/tt/my/qg/ks 文件桥继续可用。

UnityWebRequest 预下载与 Bundle 加载支持网络配置回调。JS 原生文件传输只支持静态 Headers，并按凭据来源过滤；不能执行 UnityWebRequest 回调，传入这类回调会明确报错。需要动态鉴权时，在发起该批下载前生成网络策略。

接口参考：[YooAsset 微信官方适配源码](https://github.com/tuyoogame/YooAsset/tree/yoo3/Assets/YooAsset/Samples~/Mini%20Game/Runtime/WechatFileSystem)、[微信官方 SDK](https://github.com/wechat-miniprogram/minigame-tuanjie-transform-sdk)。目标设备验收范围见[平台说明](../../Documentation~/manual/platforms.md)。
