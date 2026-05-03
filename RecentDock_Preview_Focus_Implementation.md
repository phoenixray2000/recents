# RecentDock Space 预览焦点与自动隐藏实施文件

## 0. 目标

修复 Space 打开 WebView2 预览后，点击预览窗口导致主窗口失焦并自动隐藏的问题。

核心改动：

```text
把“主窗口失焦自动隐藏”改为“RecentDock 窗口组整体失焦后自动隐藏”。
```

窗口组包括：

```text
MainWindow
WebViewPreviewWindow
SettingsWindow，如当前项目已有
未来其他 RecentDock 自有浮窗
```

本轮不扩展新预览格式，不接入 Shell Preview Handler，不重构预览渲染体系。

---

## 1. 当前问题

当前逻辑大概率类似：

```csharp
private void MainWindow_Deactivated(object sender, EventArgs e)
{
    Hide();
    previewWindow?.Close();
}
```

该逻辑会导致：

```text
用户点击 WebView2 预览窗口
→ MainWindow 失焦
→ MainWindow.Deactivated 触发
→ 主窗口隐藏
→ 预览窗口关闭
```

结果：用户无法在预览窗口中选择文字、滚动 PDF、调整窗口大小或点击媒体控件。

---

## 2. 产品规则

### 2.1 保留规则

```text
HideOnFocusLost 开启时，点击 RecentDock 之外的应用或桌面，主窗口应自动隐藏。
Space 打开预览。
Space 关闭预览。
Esc 关闭预览优先于隐藏主窗口。
↑ / ↓ 切换文件，并刷新预览。
Enter 打开当前文件。
```

### 2.2 新增规则

```text
点击预览窗口不算离开 RecentDock。
点击 WebView2 内容区域不算离开 RecentDock。
调整预览窗口大小不算离开 RecentDock。
拖动预览窗口不算离开 RecentDock。
右键菜单打开期间不触发自动隐藏。
只有整个 RecentDock 窗口组都失焦后，才允许自动隐藏。
```

---

## 3. 实施范围

### 3.1 必做

```text
新增 IRecentDockWindow marker interface
新增 WindowGroupFocusService
PreviewWindow 设置 Owner = MainWindow
MainWindow.Deactivated 改为延迟窗口组失焦判断
PreviewWindow 接管并转发 Space / Esc / ↑ / ↓ / Enter
WebView2 AcceleratorKeyPressed 接管 Space / Esc / ↑ / ↓ / Enter
预览窗口 resize / drag / context menu 期间禁止误隐藏
统一 HideRecentDockWindowGroup 方法
```

### 3.2 不做

```text
不新增 Office 预览
不接入 Shell Preview Handler
不改变 WebView2 预览格式支持范围
不重写主窗口 UI
不重写 Recent 扫描逻辑
不重写 .lnk 解析逻辑
```

---

## 4. 推荐文件变更

新增：

```text
src/RecentDock.App/Services/WindowGroupFocusService.cs
src/RecentDock.App/Services/IWindowGroupFocusService.cs
src/RecentDock.App/Views/IRecentDockWindow.cs
src/RecentDock.App/Services/IPreviewCommandHost.cs
```

修改：

```text
src/RecentDock.App/MainWindow.xaml.cs
src/RecentDock.App/Views/WebViewPreviewWindow.xaml.cs
src/RecentDock.App/Services/PreviewService.cs，如项目已有
```

文件路径按当前项目实际结构调整。

---

## 5. 新增 IRecentDockWindow

文件：

```text
Views/IRecentDockWindow.cs
```

内容：

```csharp
namespace RecentDock.App.Views;

public interface IRecentDockWindow
{
}
```

让 `MainWindow` 实现：

```csharp
public partial class MainWindow : Window, IRecentDockWindow
{
}
```

让 `WebViewPreviewWindow` 实现：

```csharp
public partial class WebViewPreviewWindow : Window, IRecentDockWindow
{
}
```

如果项目已有 `SettingsWindow`，也让它实现：

```csharp
public partial class SettingsWindow : Window, IRecentDockWindow
{
}
```

如果没有 `SettingsWindow`，不要创建空窗口。

---

## 6. 新增 IWindowGroupFocusService

文件：

```text
Services/IWindowGroupFocusService.cs
```

内容：

```csharp
using System.Windows;

namespace RecentDock.App.Services;

public interface IWindowGroupFocusService
{
    bool IsInteractingWithRecentDockWindowGroup { get; set; }

    bool IsRecentDockWindow(Window window);

    bool IsAnyRecentDockWindowActive();

    bool IsMouseOverAnyRecentDockWindow();

    void RegisterWindow(Window window);

    void UnregisterWindow(Window window);

    void CancelPendingHide();

    Task<bool> ShouldHideAfterDeactivatedAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}
```

---

## 7. 新增 WindowGroupFocusService

文件：

```text
Services/WindowGroupFocusService.cs
```

内容：

```csharp
using System.Windows;
using System.Windows.Input;
using RecentDock.App.Views;

namespace RecentDock.App.Services;

public sealed class WindowGroupFocusService : IWindowGroupFocusService
{
    private readonly HashSet<Window> _registeredWindows = new();
    private readonly object _gate = new();

    public bool IsInteractingWithRecentDockWindowGroup { get; set; }

    public bool IsRecentDockWindow(Window window)
    {
        return window is IRecentDockWindow;
    }

    public void RegisterWindow(Window window)
    {
        if (!IsRecentDockWindow(window))
            return;

        lock (_gate)
        {
            _registeredWindows.Add(window);
        }

        window.Closed += (_, _) => UnregisterWindow(window);
    }

    public void UnregisterWindow(Window window)
    {
        lock (_gate)
        {
            _registeredWindows.Remove(window);
        }
    }

    public void CancelPendingHide()
    {
        // Reserved for callers that own CancellationTokenSource.
        // Kept in interface for lifecycle consistency.
    }

    public bool IsAnyRecentDockWindowActive()
    {
        return Application.Current.Windows
            .OfType<Window>()
            .Any(w => IsRecentDockWindow(w) && w.IsVisible && w.IsActive);
    }

    public bool IsMouseOverAnyRecentDockWindow()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (!IsRecentDockWindow(window) || !window.IsVisible)
                continue;

            if (IsMouseInsideWindow(window))
                return true;
        }

        return false;
    }

    public async Task<bool> ShouldHideAfterDeactivatedAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);

        if (IsInteractingWithRecentDockWindowGroup)
            return false;

        if (IsAnyRecentDockWindowActive())
            return false;

        if (IsMouseOverAnyRecentDockWindow())
            return false;

        return true;
    }

    private static bool IsMouseInsideWindow(Window window)
    {
        if (!window.IsVisible)
            return false;

        try
        {
            Point position = Mouse.GetPosition(window);
            return position.X >= 0
                && position.Y >= 0
                && position.X <= window.ActualWidth
                && position.Y <= window.ActualHeight;
        }
        catch
        {
            return false;
        }
    }
}
```

说明：

```text
IsActive 用于判断窗口是否激活。
Mouse.GetPosition(window) 用于判断鼠标是否仍在窗口区域。
150-250ms 延迟用于覆盖 MainWindow 失焦到 PreviewWindow 激活之间的短暂空档。
```

---

## 8. 注册服务

如果项目已有 DI 容器，注册：

```csharp
services.AddSingleton<IWindowGroupFocusService, WindowGroupFocusService>();
```

如果项目没有 DI，先在 `App.xaml.cs` 或 `MainWindow` 中创建单例：

```csharp
public static IWindowGroupFocusService WindowGroupFocusService { get; } =
    new WindowGroupFocusService();
```

不要为了本改动引入大型 DI 重构。

---

## 9. 修改 MainWindow 失焦逻辑

### 9.1 删除或停用旧逻辑

禁止继续使用：

```csharp
private void MainWindow_Deactivated(object sender, EventArgs e)
{
    Hide();
    _previewWindow?.Close();
}
```

### 9.2 增加字段

在 `MainWindow.xaml.cs`：

```csharp
private readonly IWindowGroupFocusService _windowGroupFocusService;
private CancellationTokenSource? _deactivateCts;
```

### 9.3 构造函数注册窗口

```csharp
public MainWindow()
{
    InitializeComponent();

    _windowGroupFocusService = App.WindowGroupFocusService;
    _windowGroupFocusService.RegisterWindow(this);

    Activated += OnRecentDockWindowActivated;
    Deactivated += OnRecentDockWindowDeactivated;
}
```

如果已有构造函数注入，使用现有注入方式，不要重复创建服务实例。

### 9.4 Activated 处理

```csharp
private void OnRecentDockWindowActivated(object? sender, EventArgs e)
{
    _deactivateCts?.Cancel();
}
```

### 9.5 Deactivated 延迟判断

```csharp
private async void OnRecentDockWindowDeactivated(object? sender, EventArgs e)
{
    if (!CurrentSettings.HideOnFocusLost)
        return;

    _deactivateCts?.Cancel();
    _deactivateCts = new CancellationTokenSource();

    try
    {
        bool shouldHide = await _windowGroupFocusService
            .ShouldHideAfterDeactivatedAsync(
                TimeSpan.FromMilliseconds(180),
                _deactivateCts.Token);

        if (shouldHide)
        {
            HideRecentDockWindowGroup();
        }
    }
    catch (OperationCanceledException)
    {
    }
}
```

把 `CurrentSettings.HideOnFocusLost` 替换成项目当前真实设置对象。

### 9.6 统一隐藏窗口组

在 `MainWindow.xaml.cs` 或 `PreviewService`：

```csharp
private void HideRecentDockWindowGroup()
{
    _deactivateCts?.Cancel();

    PreviewService.ClosePreview();
    Hide();
}
```

如果项目中预览窗口由 MainWindow 字段管理：

```csharp
private void HideRecentDockWindowGroup()
{
    _deactivateCts?.Cancel();

    _previewWindow?.Close();
    _previewWindow = null;

    Hide();
}
```

要求：

```text
不能只隐藏 MainWindow 后遗留 PreviewWindow。
不能让 PreviewWindow.Close 导致整个 App 退出。
```

---

## 10. 创建预览窗口时设置 Owner

找到创建 `WebViewPreviewWindow` 的位置，改为：

```csharp
_previewWindow = new WebViewPreviewWindow(commandHost, previewDocument);
_previewWindow.Owner = this;
_windowGroupFocusService.RegisterWindow(_previewWindow);

_previewWindow.Activated += OnRecentDockWindowActivated;
_previewWindow.Deactivated += OnRecentDockWindowDeactivated;
_previewWindow.Closed += (_, _) =>
{
    _windowGroupFocusService.UnregisterWindow(_previewWindow);
    _previewWindow = null;
};

_previewWindow.Show();
```

如果预览窗口由 `PreviewService` 创建，要求 `PreviewService` 接收 `ownerWindow` 和 `IWindowGroupFocusService`。

---

## 11. 新增 IPreviewCommandHost

文件：

```text
Services/IPreviewCommandHost.cs
```

内容：

```csharp
namespace RecentDock.App.Services;

public interface IPreviewCommandHost
{
    void ClosePreview();

    void SelectNextAndRefreshPreview();

    void SelectPreviousAndRefreshPreview();

    void OpenSelectedItem();

    void CopySelectedItemPath();
}
```

由 `MainViewModel` 或 `MainWindow` 实现。

推荐由 `MainViewModel` 实现，避免在预览窗口复制选择逻辑。

---

## 12. PreviewWindow 键盘转发

在 `WebViewPreviewWindow.xaml.cs` 添加字段：

```csharp
private readonly IPreviewCommandHost _commandHost;
private readonly IWindowGroupFocusService _windowGroupFocusService;
```

构造函数：

```csharp
public WebViewPreviewWindow(
    IPreviewCommandHost commandHost,
    IWindowGroupFocusService windowGroupFocusService)
{
    InitializeComponent();

    _commandHost = commandHost;
    _windowGroupFocusService = windowGroupFocusService;

    KeyDown += OnPreviewWindowKeyDown;

    PreviewMouseDown += OnPreviewWindowMouseDown;
    PreviewMouseUp += OnPreviewWindowMouseUp;
    SizeChanged += OnPreviewWindowSizeChanged;
    LocationChanged += OnPreviewWindowLocationChanged;
}
```

键盘处理：

```csharp
private void OnPreviewWindowKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Space)
    {
        _commandHost.ClosePreview();
        e.Handled = true;
        return;
    }

    if (e.Key == Key.Escape)
    {
        _commandHost.ClosePreview();
        e.Handled = true;
        return;
    }

    if (e.Key == Key.Down)
    {
        _commandHost.SelectNextAndRefreshPreview();
        e.Handled = true;
        return;
    }

    if (e.Key == Key.Up)
    {
        _commandHost.SelectPreviousAndRefreshPreview();
        e.Handled = true;
        return;
    }

    if (e.Key == Key.Enter)
    {
        _commandHost.OpenSelectedItem();
        e.Handled = true;
        return;
    }

    if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
    {
        _commandHost.CopySelectedItemPath();
        e.Handled = true;
        return;
    }
}
```

注意：

```text
Ctrl+C 如果 WebView2 内部存在选中文本，后续应允许 WebView2 自己复制文本。
本轮可先复制当前文件路径，后续再做选中文本检测。
```

---

## 13. WebView2 AcceleratorKeyPressed

WebView2 获得焦点后，WPF `KeyDown` 可能收不到 Space、Esc、↑、↓。

在 WebView2 初始化完成后注册：

```csharp
private async Task InitializeWebViewAsync()
{
    await webView.EnsureCoreWebView2Async();
    webView.CoreWebView2.AcceleratorKeyPressed += OnWebViewAcceleratorKeyPressed;
}
```

处理方法：

```csharp
private void OnWebViewAcceleratorKeyPressed(
    object? sender,
    CoreWebView2AcceleratorKeyPressedEventArgs e)
{
    if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown &&
        e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown)
    {
        return;
    }

    Key key = KeyInterop.KeyFromVirtualKey((int)e.VirtualKey);

    if (key == Key.Space)
    {
        Dispatcher.Invoke(() => _commandHost.ClosePreview());
        e.Handled = true;
        return;
    }

    if (key == Key.Escape)
    {
        Dispatcher.Invoke(() => _commandHost.ClosePreview());
        e.Handled = true;
        return;
    }

    if (key == Key.Down)
    {
        Dispatcher.Invoke(() => _commandHost.SelectNextAndRefreshPreview());
        e.Handled = true;
        return;
    }

    if (key == Key.Up)
    {
        Dispatcher.Invoke(() => _commandHost.SelectPreviousAndRefreshPreview());
        e.Handled = true;
        return;
    }

    if (key == Key.Enter)
    {
        Dispatcher.Invoke(() => _commandHost.OpenSelectedItem());
        e.Handled = true;
        return;
    }
}
```

需要引用：

```csharp
using Microsoft.Web.WebView2.Core;
using System.Windows.Input;
```

---

## 14. 预览窗口交互期间防误隐藏

### 14.1 鼠标按下 / 释放

```csharp
private void OnPreviewWindowMouseDown(object sender, MouseButtonEventArgs e)
{
    _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = true;
}

private async void OnPreviewWindowMouseUp(object sender, MouseButtonEventArgs e)
{
    await Task.Delay(200);
    _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = false;
}
```

### 14.2 Resize / Move debounce

字段：

```csharp
private CancellationTokenSource? _interactionCts;
```

方法：

```csharp
private void MarkWindowGroupInteraction()
{
    _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = true;

    _interactionCts?.Cancel();
    _interactionCts = new CancellationTokenSource();
    CancellationToken token = _interactionCts.Token;

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(300, token);
            await Dispatcher.InvokeAsync(() =>
            {
                _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = false;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }, token);
}
```

绑定：

```csharp
private void OnPreviewWindowSizeChanged(object sender, SizeChangedEventArgs e)
{
    MarkWindowGroupInteraction();
}

private void OnPreviewWindowLocationChanged(object? sender, EventArgs e)
{
    MarkWindowGroupInteraction();
}
```

---

## 15. 右键菜单期间防误隐藏

如果 MainWindow 或 PreviewWindow 有 ContextMenu，增加：

```csharp
private async void OnContextMenuClosed(object sender, RoutedEventArgs e)
{
    await Task.Delay(150);
    _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = false;
}

private void OnContextMenuOpened(object sender, RoutedEventArgs e)
{
    _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = true;
}
```

XAML 示例：

```xml
<ContextMenu Opened="OnContextMenuOpened" Closed="OnContextMenuClosed">
    <MenuItem Header="Open" />
</ContextMenu>
```

如果当前右键菜单由代码动态创建，也要绑定相同事件。

---

## 16. 全局快捷键行为确认

保持当前全局快捷键逻辑，但校正以下规则：

```text
窗口组未显示：显示 MainWindow，聚焦搜索框。
MainWindow 显示且预览未打开：隐藏 MainWindow。
MainWindow 显示且预览已打开：关闭预览，保留 MainWindow。
```

推荐实现：

```csharp
private void OnGlobalHotkeyPressed()
{
    if (!IsVisible)
    {
        ShowMainWindowAndFocusSearch();
        return;
    }

    if (PreviewService.IsPreviewOpen)
    {
        PreviewService.ClosePreview();
        Activate();
        FocusSearchBoxOrSelectedList();
        return;
    }

    HideRecentDockWindowGroup();
}
```

---

## 17. MainViewModel 命令实现建议

如果 `MainViewModel` 实现 `IPreviewCommandHost`：

```csharp
public void ClosePreview()
{
    _previewService.ClosePreview();
}

public void SelectNextAndRefreshPreview()
{
    MoveSelection(1);

    if (_previewService.IsPreviewOpen && SelectedItem != null)
    {
        _previewService.ShowOrUpdatePreview(SelectedItem);
    }
}

public void SelectPreviousAndRefreshPreview()
{
    MoveSelection(-1);

    if (_previewService.IsPreviewOpen && SelectedItem != null)
    {
        _previewService.ShowOrUpdatePreview(SelectedItem);
    }
}

public void OpenSelectedItem()
{
    if (SelectedItem == null)
        return;

    _fileActionService.Open(SelectedItem.TargetPath);
}

public void CopySelectedItemPath()
{
    if (SelectedItem == null)
        return;

    Clipboard.SetText(SelectedItem.TargetPath);
}
```

`MoveSelection` 应复用项目现有列表选择逻辑，不要新建第二套选中状态。

---

## 18. 验收清单

### 18.1 HideOnFocusLost 开启

- [ ] 主窗口打开后点击桌面，主窗口隐藏。
- [ ] 主窗口打开后点击其他 App，主窗口隐藏。
- [ ] 主窗口打开后按 Space 打开预览，再点击桌面，主窗口隐藏，预览窗口关闭。
- [ ] 主窗口打开后按 Space 打开预览，再点击其他 App，主窗口隐藏，预览窗口关闭。

### 18.2 预览窗口交互

- [ ] 按 Space 打开预览后，点击预览窗口，主窗口不隐藏。
- [ ] 点击 WebView2 内容区域，主窗口不隐藏。
- [ ] 滚动 PDF 或文本内容，主窗口不隐藏。
- [ ] 选择文本内容，主窗口不隐藏。
- [ ] 调整预览窗口大小，主窗口不隐藏。
- [ ] 拖动预览窗口位置，主窗口不隐藏。

### 18.3 快捷键

- [ ] 主窗口焦点下，Space 关闭预览。
- [ ] 主窗口焦点下，↑ / ↓ 切换文件并刷新预览。
- [ ] 预览窗口焦点下，Space 关闭预览。
- [ ] 预览窗口焦点下，Esc 关闭预览。
- [ ] 预览窗口焦点下，↑ / ↓ 切换文件并刷新预览。
- [ ] WebView2 焦点下，Space 关闭预览。
- [ ] WebView2 焦点下，Esc 关闭预览。
- [ ] WebView2 焦点下，↑ / ↓ 切换文件并刷新预览。
- [ ] WebView2 焦点下，Enter 打开当前文件。

### 18.4 HideOnFocusLost 关闭

- [ ] 点击桌面不自动隐藏。
- [ ] 点击其他 App 不自动隐藏。
- [ ] 预览窗口交互正常。

---

## 19. 常见风险

### 19.1 PreviewWindow Owner 设置后关闭行为异常

如果主窗口隐藏导致 owned window 自动隐藏，确认是否符合当前需求。

本项目要求：

```text
主窗口隐藏时，预览窗口也应关闭或隐藏。
```

### 19.2 WebView2 吃掉快捷键

必须使用：

```csharp
CoreWebView2.AcceleratorKeyPressed
```

只靠 WPF `KeyDown` 不够。

### 19.3 Deactivated 延迟太短

如果仍误隐藏，把延迟从 `180ms` 调整到：

```csharp
TimeSpan.FromMilliseconds(250)
```

不要超过 300ms，避免点击外部应用后窗口残留感明显。

### 19.4 Ctrl+C 冲突

首版可以复制当前文件路径。

后续优化：检测 WebView2 中是否存在选中文本；有选中文本时不拦截 Ctrl+C。

### 19.5 ContextMenu 不是 Window

WPF ContextMenu 通常不属于主窗口视觉树，打开时可能导致窗口失焦。

必须在 ContextMenu Opened / Closed 期间设置：

```csharp
IsInteractingWithRecentDockWindowGroup = true
```

---

## 20. 给 Antigravity 的实施指令

```text
请按本实施文件修改 RecentDock 的 Space 预览焦点和自动隐藏逻辑。

目标：
把 HideOnFocusLost 从 MainWindow 单窗口失焦判断，改成 RecentDock 窗口组整体失焦判断。

必须完成：
1. 新增 IRecentDockWindow marker interface。
2. MainWindow 和 WebViewPreviewWindow 实现 IRecentDockWindow。
3. 新增 IWindowGroupFocusService 和 WindowGroupFocusService。
4. MainWindow.Deactivated 不得再直接 Hide。
5. Deactivated 后延迟 180ms 判断整个窗口组是否仍活跃。
6. 创建 WebViewPreviewWindow 时设置 Owner = MainWindow。
7. PreviewWindow Activated / Deactivated 接入同一套窗口组逻辑。
8. PreviewWindow WPF KeyDown 转发 Space / Esc / ↑ / ↓ / Enter。
9. WebView2 CoreWebView2.AcceleratorKeyPressed 转发 Space / Esc / ↑ / ↓ / Enter。
10. PreviewWindow resize / move / context menu 期间设置 IsInteractingWithRecentDockWindowGroup，避免误隐藏。
11. 新增统一 HideRecentDockWindowGroup，隐藏主窗口时同步关闭预览窗口。
12. 不接入 Shell Preview Handler，不新增预览格式，不重写 UI。

完成后按验收清单逐项验证。
```
