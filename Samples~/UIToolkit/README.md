# UI Toolkit

依赖 Unity UIElements 模块（`com.unity.modules.uielements: 1.0.0`）。核心 UPM 包已声明该依赖；直接复制源文件时也需确保安装此模块。此示例不依赖 SBP。

将 UXML 作为 `VisualTreeAsset` 收集到资源包，依赖 USS、纹理、字体随依赖图构建。`ResourceView.CreateAsync(package, address, parent, token)` 克隆并附加到父 VisualElement；`Dispose()` 先从层级移除视图，再释放资源句柄。父节点被清空后仍需显式 Dispose，或者由业务对象统一管理视图租约。

可在 UI Toolkit 面板中配合包 Scope 使用，但不要把尚有视图使用的 `VisualTreeAsset` Handle 提前释放。
