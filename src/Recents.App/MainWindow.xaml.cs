using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using Recents.App.Services;
using Recents.App.ViewModels;
using Recents.App.Models;
using Serilog;

using Recents.App.Views;
using Recents.App.Helpers;
using System.Threading;

namespace Recents.App;

public partial class MainWindow : Window, IRecentDockWindow, IPreviewCommandHost
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;
    private readonly RecentIndexService _indexService;
    private readonly Func<Task> _rebuildIndexAsync;
    private readonly Func<Task> _restartSourcesAsync;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    // §6.25 预览窗口（单一持久实例）
    private PreviewWindow? _previewWindow;
    private CancellationTokenSource? _previewNavCts;
    private readonly IWindowGroupFocusService _windowGroupFocusService;
    private CancellationTokenSource? _deactivateCts;
    private bool _contextMenuOpen;

    public MainWindow(
        MainViewModel viewModel,
        SettingsService settings,
        RecentIndexService indexService,
        Func<Task> rebuildIndexAsync,
        Func<Task> restartSourcesAsync)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _indexService = indexService;
        _rebuildIndexAsync = rebuildIndexAsync;
        _restartSourcesAsync = restartSourcesAsync;
        DataContext = _viewModel;
        _viewModel.CurrentDensity = _settings.Current.CurrentDensity;
        Topmost = _settings.Current.AlwaysOnTop;
        
        // 恢复窗口位置和大小 (PRD §5.0 / 用户需求)
        RestoreWindowBounds();
        
        UpdateResponsiveLayout();
        
        // 首次显示时自动聚焦，同时预热 Segoe Fluent Icons 字体
        // WPF 对非系统字体懒加载，若不预热，ContextMenu Popup 首次渲染时
        // 字形数据可能尚未就绪，导致图标显示为空白
        Loaded += (s, e) =>
        {
            SearchBox.Focus();
            PrewarmSegoeFluentIcons();
        };

        // B2. 动态键盘提示
        ItemsList.SelectionChanged += (s, e) => 
        {
            var hasSelection = ItemsList.SelectedItem != null;
            var selectedItem = ItemsList.SelectedItem as RecentItemViewModel;
            var canDrag = hasSelection && selectedItem != null && !selectedItem.IsMissing;
            _viewModel.Status.UpdateHint(hasSelection, canDrag);
        };

        _windowGroupFocusService = App.WindowGroupFocusService;
        _windowGroupFocusService.RegisterWindow(this);

        Activated += OnRecentDockWindowActivated;
        Deactivated += OnRecentDockWindowDeactivated;

        _viewModel.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(MainViewModel.CombinedFavoritesVisibility))
                UpdateWindowWidth();
        };
        
        UpdateWindowWidth();

        FileActionService.ActionExecuted += () => Dispatcher.BeginInvoke(new Action(HideWindowGroup));
        ShellService.ActionExecuted += () => Dispatcher.BeginInvoke(new Action(HideWindowGroup));

        // §6.25: 列表选中变化时，如果预览窗口已打开，延迟 100ms 刷新
        ItemsList.SelectionChanged += (s, e) =>
        {
            if (_previewWindow?.IsVisible != true) return;
            if (ItemsList.SelectedItem is not RecentItemViewModel vm) return;
            if (vm.Item.IsFolder) return;

            // 取消上一次待发的刷新
            _previewNavCts?.Cancel();
            _previewNavCts = new System.Threading.CancellationTokenSource();
            var token = _previewNavCts.Token;

            _ = Task.Delay(100, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.Invoke(() =>
                {
                    if (_previewWindow?.IsVisible == true)
                    {
                        _previewWindow.Tag = vm.DisplayPath;
                        _ = _previewWindow.ShowFileAsync(vm.DisplayPath);
                    }
                });
            }, TaskScheduler.Default);
        };
    }

    private void RestoreWindowBounds()
    {
        if (_settings.Current.WindowWidth >= MinWidth)
            _baseWidth = _settings.Current.WindowWidth;
        
        if (_settings.Current.WindowHeight >= MinHeight)
            Height = _settings.Current.WindowHeight;

        if (_settings.Current.WindowTop.HasValue && _settings.Current.WindowLeft.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Top = _settings.Current.WindowTop.Value;
            Left = _settings.Current.WindowLeft.Value;
            EnsureWindowInVisibleRange();
        }
    }

    private void SaveWindowBounds()
    {
        _settings.Current.WindowWidth = _baseWidth;
        _settings.Current.WindowHeight = Height;
        _settings.Current.WindowTop = Top;
        _settings.Current.WindowLeft = Left;
        _settings.Save();
    }

    private void EnsureWindowInVisibleRange()
    {
        // 获取当前窗口中心点所在的屏幕，如果完全不可见则取主屏幕
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;

        // 如果窗口完全在虚拟屏幕之外，重置到主屏幕中心
        if (Left + Width < virtualLeft || Left > virtualLeft + virtualWidth ||
            Top + Height < virtualTop || Top > virtualTop + virtualHeight)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        // 确保窗口至少有一部分在工作区内，并且标题栏可抓取
        // 简单处理：确保 Top/Left 不超出当前屏幕边界太远
        // 更稳健的做法是使用 System.Windows.Forms.Screen (需要引用) 或 P/Invoke
        // 这里使用 WPF 的简单方式：限制在虚拟屏幕内
        if (Left < virtualLeft) Left = virtualLeft;
        if (Top < virtualTop) Top = virtualTop;
        if (Left + Width > virtualLeft + virtualWidth) Left = virtualLeft + virtualWidth - Width;
        if (Top + Height > virtualTop + virtualHeight) Top = virtualTop + virtualHeight - Height;
    }

    private double _baseWidth = 600;
    private void UpdateWindowWidth()
    {
        if (WindowState == WindowState.Maximized) return;

        if (_viewModel.CombinedFavoritesVisibility)
        {
            Width = _baseWidth + 280;
        }
        else
        {
            Width = _baseWidth;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged && !_viewModel.CombinedFavoritesVisibility)
        {
            _baseWidth = ActualWidth;
        }
    }

    public void SetTrayService(TrayService tray) => _tray = tray;

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // PRD §6.2 关闭主窗口仅隐藏到托盘
        if (!_settings.Current.CloseToTray)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        e.Cancel = true;
        HideWindowGroup();

        // B1. 首次关闭时显示气泡提示 (PRD §7.8)
        if (!_settings.Current.ClosedToTrayNoticeShown)
        {
            _tray?.ShowBalloon("Recents", "The app is still running in the tray.", System.Windows.Forms.ToolTipIcon.Info);
            _settings.Current.ClosedToTrayNoticeShown = true;
            _settings.Save();
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        // 侧边栏已移除，响应式逻辑后续根据新布局需求重构
    }

    // 处理全局快捷键：Esc 隐藏，Ctrl+F 聚焦搜索，Enter 打开等
    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_previewWindow?.IsVisible == true)
            {
                _previewWindow.Hide();
                e.Handled = true;
                return;
            }
            HideWindowGroup();
            e.Handled = true;
            return;
        }

        // §6.25: Space 切换预览
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            TogglePreview();
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C)
            {
                var selectedItems = GetSelectedItems().ToList();
                if (selectedItems.Count > 0)
                {
                    if ((modifiers & ModifierKeys.Shift) != 0)
                        FileActionService.CopyFileNames(selectedItems.Select(v => v.DisplayPath));
                    else
                        FileActionService.CopyPaths(selectedItems.Select(v => v.DisplayPath));
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.O && (modifiers & ModifierKeys.Shift) == 0)
            {
                if (ItemsList.SelectedItem is RecentItemViewModel selected)
                {
                    FileActionService.RevealInExplorer(selected.DisplayPath);
                    Dispatcher.BeginInvoke(new Action(HideWindowGroup));
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.Key == Key.Enter)
        {
            // 若预览可见，Enter 打开文件并关闭预览
            if (_previewWindow?.IsVisible == true)
                _previewWindow.Hide();
            TryOpenSelectedItem();
            e.Handled = true;
            return;
        }
    }

    private void SearchBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        // 搜索框按上下键，转移焦点到列表
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            ItemsList.Focus();
            if (ItemsList.Items.Count > 0 && ItemsList.SelectedIndex < 0)
                ItemsList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryOpenSelectedItem();
    }

    private void ItemsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb)
        {
            var scrollViewer = FindChild<ScrollViewer>(lb);
            if (scrollViewer != null)
            {
                // 降低滚动速度（灵敏度系数 0.4），并实现平滑偏移
                double delta = e.Delta * 0.4;
                double newOffset = scrollViewer.VerticalOffset - delta;

                if (newOffset < 0) newOffset = 0;
                if (newOffset > scrollViewer.ScrollableHeight) newOffset = scrollViewer.ScrollableHeight;

                scrollViewer.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void TryOpenSelectedItem()
    {
        if (ItemsList.SelectedItem is RecentItemViewModel vm)
        {
            // PRD §6.24：文件夹双击用 Explorer 打开，文件用默认程序打开
            if (vm.Item.IsFolder)
                FileActionService.RevealInExplorer(vm.DisplayPath);
            else
                FileActionService.OpenFile(vm.DisplayPath);
            
            Dispatcher.BeginInvoke(new Action(HideWindowGroup));
        }
    }

    // P0 拖拽支持 (PRD §6.8)
    // B4. 批量拖拽支持 (PRD §6.8)
    private void ItemsList_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && ItemsList.SelectedItems.Count > 0)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindParent<System.Windows.Controls.Button>(source) != null) return;

            var selectedVms = GetSelectedItems().Where(v => !v.IsMissing).ToList();

            if (selectedVms.Count == 0) return;

            var paths = selectedVms.Select(v => v.DisplayPath).ToArray();
            var dataObj = DragDropService.CreateDataObject(paths);
            System.Windows.DragDrop.DoDragDrop(ItemsList, dataObj, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
    }

    private void FavoritesList_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && FavoritesList.SelectedItem != null)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindParent<System.Windows.Controls.Button>(source) != null) return;

            var selected = FavoritesList.SelectedItem as RecentItemViewModel;
            if (selected == null || selected.IsMissing) return;

            // 包含内部排序标志和外部文件路径
            var dataObj = DragDropService.CreateDataObject(new[] { selected.DisplayPath });
            dataObj.SetData("InternalReorder", selected);
            
            System.Windows.DragDrop.DoDragDrop(FavoritesList, dataObj, System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
    }

    private async void FavoritesList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("InternalReorder"))
        {
            var draggedVm = e.Data.GetData("InternalReorder") as RecentItemViewModel;
            if (draggedVm == null) return;

            // 找到放置点对应的项索引
            var pos = e.GetPosition(FavoritesList);
            var result = VisualTreeHelper.HitTest(FavoritesList, pos);
            if (result == null) return;

            var hitItem = FindParent<ListBoxItem>(result.VisualHit);
            if (hitItem != null)
            {
                var targetVm = hitItem.DataContext as RecentItemViewModel;
                if (targetVm != null && targetVm != draggedVm)
                {
                    int targetIndex = _viewModel.FavoritesView.Cast<object>().ToList().IndexOf(targetVm);
                    await _indexService.ReorderFavoritesAsync(draggedVm.Item.NormalizedPath, targetIndex);
                }
            }
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files != null)
            {
                foreach (var file in files)
                {
                    await _indexService.AddFavoriteByPathAsync(file);
                }
            }
        }
    }

    private IEnumerable<RecentItemViewModel> GetSelectedItems() =>
        ItemsList.SelectedItems.Cast<RecentItemViewModel>();

    // 提供给 App 激活窗口时调用的公共方法
    public void ShowAndFocus()
    {
        EnsureWindowInVisibleRange();
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            if (_previewWindow?.IsVisible == true)
            {
                _previewWindow.Hide();
                Activate();
                SearchBox.Focus();
                return;
            }

            if (IsActive)
            {
                HideWindowGroup();
                return;
            }
        }
        
        ShowAndFocus();
    }

    public void HideWindowGroup()
    {
        _deactivateCts?.Cancel();
        SaveWindowBounds();
        if (_previewWindow?.IsVisible == true)
            _previewWindow.Hide();
        Hide();
    }

    private void OnRecentDockWindowActivated(object? sender, EventArgs e)
    {
        _deactivateCts?.Cancel();
    }

    private async void OnRecentDockWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_settings.Current.HideOnFocusLost)
            return;

        // 动态 ContextMenu 运行在独立 Popup 中，会短暂夺取焦点。
        // 若此时触发 Deactivated，应忽略，等菜单关闭后再决定。
        if (_contextMenuOpen)
            return;

        // 若外部对话框（如 Open With）正在展示，不隐藏主窗口
        if (ShellService.IsExternalDialogOpen)
            return;

        _deactivateCts?.Cancel();
        _deactivateCts = new CancellationTokenSource();

        try
        {
            bool shouldHide = await _windowGroupFocusService
                .ShouldHideAfterDeactivatedAsync(
                    TimeSpan.FromMilliseconds(100),
                    _deactivateCts.Token);

            if (shouldHide)
            {
                // 再次检查标记。因为 ShouldHideAfterDeactivatedAsync 内部有 100ms 延迟，
                // 期间 Command 脚本可能已经启动并设置了 IsExternalDialogOpen = true。
                if (ShellService.IsExternalDialogOpen)
                    return;

                HideWindowGroup();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── IPreviewCommandHost ────────────────────────────────────────────────
    public void ClosePreview()
    {
        if (_previewWindow?.IsVisible == true)
        {
            _previewWindow.Hide();
            _previewWindow.Tag = null;
        }
    }

    public void SelectNextAndRefreshPreview()
    {
        if (ItemsList.Items.Count == 0) return;
        int next = ItemsList.SelectedIndex + 1;
        if (next < ItemsList.Items.Count)
            ItemsList.SelectedIndex = next;
        else
            ItemsList.SelectedIndex = 0;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    public void SelectPreviousAndRefreshPreview()
    {
        if (ItemsList.Items.Count == 0) return;
        int prev = ItemsList.SelectedIndex - 1;
        if (prev >= 0)
            ItemsList.SelectedIndex = prev;
        else
            ItemsList.SelectedIndex = ItemsList.Items.Count - 1;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    public void OpenSelectedItem() => TryOpenSelectedItem();

    public void CopySelectedItemPath()
    {
        if (ItemsList.SelectedItem is RecentItemViewModel vm)
        {
            FileActionService.CopyPaths(new[] { vm.DisplayPath });
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        if (sender is System.Windows.Controls.RadioButton rb)
        {
            var filter = rb.Tag?.ToString() ?? rb.Content?.ToString() ?? "All";
            _viewModel.CurrentChipFilter = filter;
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(_settings, _indexService, _restartSourcesAsync, _rebuildIndexAsync);
        viewModel.SettingsChanged += ApplySettings;
        _settingsWindow = new SettingsWindow(viewModel)
        {
            Owner = this
        };
        _windowGroupFocusService.RegisterWindow(_settingsWindow);
        _settingsWindow.Activated += OnRecentDockWindowActivated;
        _settingsWindow.Deactivated += OnRecentDockWindowDeactivated;
        
        _settingsWindow.Closed += (_, _) =>
        {
            _windowGroupFocusService.UnregisterWindow(_settingsWindow);
            viewModel.SettingsChanged -= ApplySettings;
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    private void ApplySettings()
    {
        Topmost = _settings.Current.AlwaysOnTop;
        _viewModel.UpdateHotkey(_settings.Current.Hotkey);
        _viewModel.ItemsView.Refresh();
        _viewModel.FavoritesView.Refresh();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string sortStr)
        {
            if (Enum.TryParse<MainViewModel.SortOption>(sortStr, out var sortOpt))
            {
                _viewModel.CurrentSort = sortOpt;
                SortLabel.Text = mi.Header.ToString();
            }
        }
    }
    private void Density_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string densityStr)
        {
            if (Enum.TryParse<AppSettings.ViewDensity>(densityStr, out var density))
            {
                _viewModel.CurrentDensity = density;
                _settings.Current.CurrentDensity = density;
                _settings.Save();
            }
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var grid = FindParent<Grid>(fe);
            if (grid != null && grid.DataContext is RecentItemViewModel vm)
            {
                OpenDynamicContextMenu(vm, fe);
            }
        }
    }

    /// <summary>
    /// 拦截所有列表项的右键菜单，动态构建全新 ContextMenu。
    /// 每次打开都创建新实例，彻底避免 WPF Visual Parent 复用导致图标消失。
    /// </summary>
    private void ItemsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var listBoxItem = FindParent<ListBoxItem>(source);
            if (listBoxItem?.DataContext is RecentItemViewModel vm)
            {
                OpenDynamicContextMenu(vm, listBoxItem);
            }
        }
        e.Handled = true;
    }

    /// <summary>
    /// 统一入口：构建并打开动态 ContextMenu，同时管理 _contextMenuOpen 标记
    /// 以防止菜单打开期间触发窗口 Deactivated 隐藏逻辑。
    /// </summary>
    private void OpenDynamicContextMenu(RecentItemViewModel vm, UIElement placementTarget)
    {
        var menu = ContextMenuBuilder.Build(vm);
        menu.PlacementTarget = placementTarget;

        _contextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
        };

        menu.IsOpen = true;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }

    private PreviewWindow EnsurePreviewWindow()
    {
        if (_previewWindow == null)
        {
            _previewWindow = new PreviewWindow(this, _windowGroupFocusService);
            _windowGroupFocusService.RegisterWindow(_previewWindow);

            _previewWindow.Activated += OnRecentDockWindowActivated;
            _previewWindow.Deactivated += OnRecentDockWindowDeactivated;
        }
        return _previewWindow;
    }

    private void TogglePreview()
    {
        if (!_settings.Current.PreviewEnabled) return;
        if (ItemsList.SelectedItem is not RecentItemViewModel vm) return;
        if (vm.Item.IsFolder) return;   // §6.25.1: 文件夹不预览

        var pw = EnsurePreviewWindow();

        if (pw.IsVisible && pw.Tag as string == vm.DisplayPath)
        {
            // 二次 Space → 关闭
            pw.Hide();
        }
        else
        {
            pw.Tag = vm.DisplayPath;
            pw.PositionRelativeTo(this);
            if (pw.Owner == null) pw.Owner = this;
            pw.Show();
            _ = pw.ShowFileAsync(vm.DisplayPath);
        }
    }

    public void PrewarmPreview()
    {
        if (_settings.Current.PreviewEnabled)
            _ = EnsurePreviewWindow(); // 触发 PreviewWindow 构造 + InitWebView2Async
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(HideWindowGroup));
    }

    /// <summary>
    /// 强制 WPF 在 Loaded 时同步解析 Segoe Fluent Icons 字形，
    /// 避免 ContextMenu Popup 首次打开时因字体未就绪导致图标空白。
    /// </summary>
    private static void PrewarmSegoeFluentIcons()
    {
        try
        {
            const string glyphs = "\uED25\uE7BC\uE81D\uE8C8\uE735\uE894\uE74D";
            var typeface = new Typeface("Segoe Fluent Icons");
            // FormattedText 会触发字体文件加载 + 字形 shaping，确保完全缓存
            var ft = new FormattedText(
                glyphs,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                14,
                System.Windows.Media.Brushes.Transparent,
                VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow).PixelsPerDip);
            // 触发测量以强制字形加载
            _ = ft.Width;
        }
        catch
        {
            // 字体不可用时静默失败，不影响程序运行
        }
    }
}
