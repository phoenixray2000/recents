using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using Recents.App.Services;
using Recents.App.ViewModels;
using Recents.App.Models;
using Serilog;

using Recents.App.Views;
using Recents.App.Helpers;
using Recents.App.Services.Clipboard;
using System.Threading;

namespace Recents.App;

public partial class MainWindow : Window, IRecentDockWindow, IPreviewCommandHost
{
    private const string InternalReorderFormat = "InternalReorder";
    private const string InternalGroupReorderFormat = "InternalGroupReorder";
    private const string ClipboardItemIdFormat = "Recents.ClipboardItemId";

    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;
    private readonly RecentIndexService _indexService;
    private readonly ClipboardStoreService _clipboardStore;
    private readonly ClipboardActionService _clipboardActions;
    private readonly ClipboardDragDropService _clipboardDragDrop;
    private readonly Func<Task> _rebuildIndexAsync;
    private readonly Func<Task> _restartSourcesAsync;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    // §6.25 预览窗口（单一持久实例）
    private PreviewWindow? _previewWindow;
    private CancellationTokenSource? _previewNavCts;
    private readonly IWindowGroupFocusService _windowGroupFocusService;
    private CancellationTokenSource? _deactivateCts;
    private CancellationTokenSource? _actionHideCts;
    private bool _contextMenuOpen;
    private bool _pendingActionHide;
    private object? _favoritesDragItem;
    private FavoriteDragAdorner? _dragAdorner;
    private AdornerLayer? _favAdornerLayer;

    public MainWindow(
        MainViewModel viewModel,
        SettingsService settings,
        RecentIndexService indexService,
        ClipboardStoreService clipboardStore,
        ClipboardActionService clipboardActions,
        ClipboardDragDropService clipboardDragDrop,
        Func<Task> rebuildIndexAsync,
        Func<Task> restartSourcesAsync)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _indexService = indexService;
        _clipboardStore = clipboardStore;
        _clipboardActions = clipboardActions;
        _clipboardDragDrop = clipboardDragDrop;
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
        ItemsList.SelectionChanged += (s, e) => UpdateStatusHintFromSelection();
        FavoritesList.SelectionChanged += (s, e) => UpdateStatusHintFromSelection();

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

        FileActionService.ActionExecuted += () => { _pendingActionHide = true; ScheduleActionFallbackHide(); };
        ShellService.ActionExecuted += () => { _pendingActionHide = true; ScheduleActionFallbackHide(); };

        // §6.25: 列表选中变化时，如果预览窗口已打开，延迟 100ms 刷新
        ItemsList.SelectionChanged += (s, e) =>
        {
            if (_previewWindow?.IsVisible != true) return;
            SchedulePreviewRefresh(ItemsList.SelectedItem);
        };

        FavoritesList.SelectionChanged += (s, e) =>
        {
            if (_previewWindow?.IsVisible != true) return;
            SchedulePreviewRefresh(FavoritesList.SelectedItem);
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

        bool show = _viewModel.CombinedFavoritesVisibility;
        // Set both drawer visibility and window width in one pass to prevent a one-frame
        // gap where the window is wide but the drawer is still collapsed (or vice versa).
        FavoritesDrawer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Width = show ? _baseWidth + 160 : _baseWidth;
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
                if (_viewModel.IsClipboardMode && ItemsList.SelectedItem is ClipboardItemViewModel clip)
                {
                    _ = _clipboardActions.CopyToClipboardAsync(clip);
                    e.Handled = true;
                    return;
                }

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

            if (FavoritesList.IsFocused)
                TryOpenItem(FavoritesList.SelectedItem);
            else
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

    private void UpdateStatusHintFromSelection()
    {
        var item = GetStatusHintSelection();
        var mode = item switch
        {
            ClipboardItemViewModel or ClipboardFavoriteViewModel => StatusHintService.HintMode.Clipboard,
            RecentItemViewModel => StatusHintService.HintMode.File,
            _ => StatusHintService.HintMode.None
        };
        var canDrag = item switch
        {
            RecentItemViewModel { IsMissing: false } => true,
            ClipboardItemViewModel => true,
            ClipboardFavoriteViewModel => true,
            _ => false
        };

        _viewModel.Status.UpdateHint(mode, canDrag);
    }

    private object? GetStatusHintSelection()
    {
        if (FavoritesList.SelectedItem is not null &&
            (FavoritesList.IsKeyboardFocusWithin || FavoritesList.IsMouseOver || ItemsList.SelectedItem is null))
        {
            return FavoritesList.SelectedItem;
        }

        return ItemsList.SelectedItem ?? FavoritesList.SelectedItem;
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryOpenItem(ItemsList.SelectedItem);
    }

    private void FavoritesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryOpenItem(FavoritesList.SelectedItem);
    }

    private void FavoritesList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var listBoxItem = FindParent<ListBoxItem>(source);
            OpenContextMenuForItem(listBoxItem?.DataContext, listBoxItem);
        }
        e.Handled = true;
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

    private void TryOpenSelectedItem() => TryOpenItem(ItemsList.SelectedItem);

    private void TryOpenItem(object? item, bool openContainingFolder = false)
    {
        if (item is ClipboardItemViewModel clip)
        {
            _ = _clipboardActions.PasteToActiveAppAsync(clip.Item);
            return;
        }
        if (item is ClipboardFavoriteViewModel favorite)
        {
            _ = _clipboardActions.PasteToActiveAppAsync(favorite.Item.ToClipboardItem());
            return;
        }

        TryOpenItem(item as RecentItemViewModel, openContainingFolder);
    }

    private void TryOpenItem(RecentItemViewModel? vm, bool openContainingFolder = false)
    {
        if (vm != null)
        {
            if (openContainingFolder && !vm.Item.IsFolder)
            {
                FileActionService.OpenContainingFolder(vm.DisplayPath);
                return;
            }

            // PRD §6.24：文件夹双击用 Explorer 打开，文件用默认程序打开
            // 现在全部委托给 FileActionService.OpenFile，它会自动处理文件夹激活优化
            FileActionService.OpenFile(vm.DisplayPath);
            
            // 注意：不要在这里直接 HideWindowGroup()，
            // 已统一由 FileActionService.ActionExecuted 中的延迟处理，
            // 确保新建的外部进程能够顺利夺取前台焦点。
        }
    }

    private static bool IsCtrlPressed() =>
        ClipboardPasteGesture.ShouldPastePlainTextOnClick(Keyboard.Modifiers);

    // P0 拖拽支持 (PRD §6.8)
    // B4. 批量拖拽支持 (PRD §6.8)
    private System.Windows.Point? _dragStartPoint;

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryHandleCtrlClickListItem(e))
            return;

        _dragStartPoint = e.GetPosition(null);
    }

    private void FavoritesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryHandleCtrlClickListItem(e))
            return;

        _dragStartPoint = e.GetPosition(null);
    }

    private bool TryHandleCtrlClickListItem(MouseButtonEventArgs e)
    {
        if (!IsCtrlPressed() || e.ChangedButton != MouseButton.Left)
            return false;

        if (e.OriginalSource is not DependencyObject source ||
            FindParent<System.Windows.Controls.Button>(source) != null ||
            IsFavoritesDragHandleSource(source))
        {
            return false;
        }

        var listBoxItem = FindParent<ListBoxItem>(source);
        if (listBoxItem?.DataContext is null)
            return false;

        switch (listBoxItem.DataContext)
        {
            case RecentItemViewModel recent:
                TryOpenItem(recent, openContainingFolder: true);
                e.Handled = true;
                return true;

            case ClipboardItemViewModel { HasPlainText: true } clip:
                _ = _clipboardActions.PastePlainTextToActiveAppAsync(clip.Item);
                e.Handled = true;
                return true;

            case ClipboardFavoriteViewModel { HasPlainText: true } favorite:
                _ = _clipboardActions.PastePlainTextToActiveAppAsync(favorite.Item.ToClipboardItem());
                e.Handled = true;
                return true;

            default:
                return false;
        }
    }

    private static bool IsFavoritesDragHandleSource(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Cursor: not null } element &&
                element.Cursor == System.Windows.Input.Cursors.SizeNS)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void FavoritesDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe &&
            fe.DataContext is RecentItemViewModel or ClipboardFavoriteViewModel or FavoriteGroupViewModel)
        {
            _favoritesDragItem = fe.DataContext;
            _dragStartPoint = e.GetPosition(null);
            e.Handled = false; // let MouseMove fire
        }
    }

    private void ItemsList_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _dragStartPoint.HasValue && ItemsList.SelectedItems.Count > 0)
        {
            var currentPos = e.GetPosition(null);
            if (Math.Abs(currentPos.X - _dragStartPoint.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentPos.Y - _dragStartPoint.Value.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var source = e.OriginalSource as DependencyObject;
                if (source != null && FindParent<System.Windows.Controls.Button>(source) != null) return;

                if (_viewModel.IsClipboardMode && ItemsList.SelectedItems.Count == 1 &&
                    ItemsList.SelectedItem is ClipboardItemViewModel clipVm)
                {
                    var clipboardDataObj = _clipboardDragDrop.CreateDataObject(
                        clipVm.Item,
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    clipboardDataObj.SetData(ClipboardItemIdFormat, clipVm.Item.Id);
                    _dragStartPoint = null;
                    System.Windows.DragDrop.DoDragDrop(ItemsList, clipboardDataObj, System.Windows.DragDropEffects.Copy);
                    return;
                }

                var selectedVms = GetSelectedItems().Where(v => !v.IsMissing).ToList();

                if (selectedVms.Count == 0) return;

                var paths = selectedVms.Select(v => v.DisplayPath).ToArray();
                var dataObj = DragDropService.CreateDataObject(paths);
                
                _dragStartPoint = null; // Reset before drag
                System.Windows.DragDrop.DoDragDrop(ItemsList, dataObj, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
            }
        }
    }

    private void FavoritesList_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_dragStartPoint.HasValue)
            return;

        var currentPos = e.GetPosition(null);
        if (Math.Abs(currentPos.X - _dragStartPoint.Value.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPos.Y - _dragStartPoint.Value.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = e.OriginalSource as DependencyObject;

        if (_viewModel.IsFavoritesEditMode && _favoritesDragItem != null)
        {
            // Edit mode: internal reorder only, no external drop data
            var vm = _favoritesDragItem;
            _favoritesDragItem = null;
            _dragStartPoint = null;

            _favAdornerLayer = AdornerLayer.GetAdornerLayer(FavoritesList);
            _dragAdorner = new FavoriteDragAdorner(FavoritesList, GetFavoriteDisplayName(vm));
            _favAdornerLayer?.Add(_dragAdorner);

            var dataObj = new System.Windows.DataObject();
            dataObj.SetData(
                vm is FavoriteGroupViewModel ? InternalGroupReorderFormat : InternalReorderFormat,
                vm);
            System.Windows.DragDrop.DoDragDrop(FavoritesList, dataObj, System.Windows.DragDropEffects.Move);

            if (_dragAdorner != null) { _favAdornerLayer?.Remove(_dragAdorner); _dragAdorner = null; }
            FavoritesDragLine.Visibility = Visibility.Collapsed;
        }
        else if (!_viewModel.IsFavoritesEditMode)
        {
            // Normal mode: external file drag only, no reorder
            if (source != null && FindParent<System.Windows.Controls.Button>(source) != null) return;
            var selected = FavoritesList.SelectedItem;
            if (selected is null) return;
            if (selected is RecentItemViewModel recent && recent.IsMissing) return;
            _dragStartPoint = null;
            var dataObj = selected switch
            {
                RecentItemViewModel recentVm => DragDropService.CreateDataObject(new[] { recentVm.DisplayPath }),
                ClipboardFavoriteViewModel clipVm => _clipboardDragDrop.CreateDataObject(
                    clipVm.Item.ToClipboardItem(),
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)),
                _ => null
            };
            if (dataObj is null) return;
            System.Windows.DragDrop.DoDragDrop(FavoritesList, dataObj,
                selected is ClipboardFavoriteViewModel ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
    }

    private async void FavoritesList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalGroupReorderFormat))
        {
            if (e.Data.GetData(InternalGroupReorderFormat) is not FavoriteGroupViewModel draggedGroup)
                return;

            var rows = _viewModel.FavoritesView.Cast<object>().ToList();
            var targetRowIndex = GetFavoriteDropIndex(e.GetPosition(FavoritesList));
            var orderedGroups = rows.OfType<FavoriteGroupViewModel>().ToList();
            var targetGroupIndex = CountFavoriteGroupsBefore(rows, targetRowIndex);
            var originalGroupIndex = orderedGroups.IndexOf(draggedGroup);
            orderedGroups.Remove(draggedGroup);
            if (originalGroupIndex >= 0 && originalGroupIndex < targetGroupIndex)
                targetGroupIndex--;

            targetGroupIndex = Math.Clamp(targetGroupIndex, 0, orderedGroups.Count);
            orderedGroups.Insert(targetGroupIndex, draggedGroup);
            _viewModel.ApplyFavoriteGroupOrder(orderedGroups);
            FavoritesDragLine.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(InternalReorderFormat))
        {
            var draggedVm = e.Data.GetData(InternalReorderFormat);
            if (draggedVm == null) return;

            var rows = _viewModel.FavoritesView.Cast<object>().ToList();
            var dropPosition = e.GetPosition(FavoritesList);
            var targetRowIndex = GetFavoriteDropIndex(dropPosition);
            var targetGroupId = GetFavoriteDropGroupId(dropPosition, rows, targetRowIndex);
            var ordered = rows.Where(MainViewModel.IsFavoriteItem).ToList();
            var targetItemIndex = CountFavoriteItemsBefore(rows, targetRowIndex);
            var originalItemIndex = ordered.IndexOf(draggedVm);
            ordered.Remove(draggedVm);
            if (originalItemIndex >= 0 && originalItemIndex < targetItemIndex)
                targetItemIndex--;

            targetItemIndex = Math.Clamp(targetItemIndex, 0, ordered.Count);
            ordered.Insert(targetItemIndex, draggedVm);
            await _viewModel.ApplyUnifiedFavoriteLayoutAsync(ordered, draggedVm, targetGroupId);
            FavoritesDragLine.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }
        else if (e.Data.GetDataPresent(ClipboardItemIdFormat))
        {
            var itemId = e.Data.GetData(ClipboardItemIdFormat) as string;
            if (!string.IsNullOrWhiteSpace(itemId))
                await _clipboardStore.AddToFavoritesAsync(itemId);
            e.Handled = true;
            return;
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files != null)
            {
                foreach (var file in files.Where(p => !IsClipboardDataPath(p)))
                {
                    await _indexService.AddFavoriteByPathAsync(file);
                }
            }
            e.Handled = true;
        }
    }

    private void FavoritesList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ClipboardItemIdFormat))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        if (!e.Data.GetDataPresent(InternalReorderFormat) &&
            !e.Data.GetDataPresent(InternalGroupReorderFormat)) return;
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;

        var listPos = e.GetPosition(FavoritesList);
        UpdateInsertionLine(listPos);

        var adornerPos = e.GetPosition(AdornerLayer.GetAdornerLayer(FavoritesList));
        _dragAdorner?.UpdatePosition(adornerPos);
    }

    private void FavoritesList_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        FavoritesDragLine.Visibility = Visibility.Collapsed;
    }

    private bool IsClipboardDataPath(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var dataRoot = System.IO.Path.GetFullPath(_clipboardStore.DataDirectory)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            return fullPath.Equals(dataRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(dataRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(dataRoot + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateInsertionLine(System.Windows.Point posInList)
    {
        double insertY = 0;
        foreach (var item in FavoritesList.Items)
        {
            var container = FavoritesList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container == null) continue;
            var itemPos = container.TranslatePoint(new System.Windows.Point(0, 0), FavoritesList);
            if (posInList.Y < itemPos.Y + container.ActualHeight / 2)
            {
                insertY = itemPos.Y;
                Canvas.SetTop(FavoritesDragLine, insertY - 1);
                FavoritesDragLine.Visibility = Visibility.Visible;
                return;
            }
            insertY = itemPos.Y + container.ActualHeight;
        }
        Canvas.SetTop(FavoritesDragLine, insertY - 1);
        FavoritesDragLine.Visibility = Visibility.Visible;
    }

    private string? GetFavoriteDropGroupId(System.Windows.Point posInList, IReadOnlyList<object> rows, int targetRowIndex)
    {
        if (GetFavoriteGroupUnderPointer(posInList) is { } directGroup)
            return directGroup.Id;

        if (targetRowIndex > 0 && targetRowIndex - 1 < rows.Count)
        {
            var previous = rows[targetRowIndex - 1];
            if (previous is FavoriteGroupViewModel previousGroup)
                return previousGroup.Id;
            if (MainViewModel.IsFavoriteItem(previous))
                return _viewModel.GetFavoriteGroupId(previous);
        }

        if (targetRowIndex >= 0 && targetRowIndex < rows.Count && MainViewModel.IsFavoriteItem(rows[targetRowIndex]))
            return _viewModel.GetFavoriteGroupId(rows[targetRowIndex]);

        return null;
    }

    private FavoriteGroupViewModel? GetFavoriteGroupUnderPointer(System.Windows.Point posInList)
    {
        foreach (var item in FavoritesList.Items)
        {
            if (item is not FavoriteGroupViewModel group)
                continue;

            if (FavoritesList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                continue;

            var itemPos = container.TranslatePoint(new System.Windows.Point(0, 0), FavoritesList);
            if (posInList.Y >= itemPos.Y && posInList.Y <= itemPos.Y + container.ActualHeight)
                return group;
        }

        return null;
    }

    private static int CountFavoriteItemsBefore(IReadOnlyList<object> rows, int targetRowIndex)
    {
        var count = 0;
        var safeTarget = Math.Clamp(targetRowIndex, 0, rows.Count);
        for (var i = 0; i < safeTarget; i++)
        {
            if (MainViewModel.IsFavoriteItem(rows[i]))
                count++;
        }

        return count;
    }

    private static int CountFavoriteGroupsBefore(IReadOnlyList<object> rows, int targetRowIndex)
    {
        var count = 0;
        var safeTarget = Math.Clamp(targetRowIndex, 0, rows.Count);
        for (var i = 0; i < safeTarget; i++)
        {
            if (rows[i] is FavoriteGroupViewModel)
                count++;
        }

        return count;
    }

    private IEnumerable<RecentItemViewModel> GetSelectedItems() =>
        ItemsList.SelectedItems.OfType<RecentItemViewModel>();

    // 提供给 App 激活窗口时调用的公共方法
    public void ShowAndFocus()
    {
        _clipboardActions.CapturePasteTargetFromForeground();
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
        _pendingActionHide = false;
        _actionHideCts?.Cancel();
        _deactivateCts?.Cancel();
        SaveWindowBounds();
        if (_previewWindow?.IsVisible == true)
            _previewWindow.Hide();
        Hide();
    }

    private void ScheduleActionFallbackHide()
    {
        _actionHideCts?.Cancel();
        _actionHideCts = new CancellationTokenSource();
        var token = _actionHideCts.Token;

        _ = Task.Delay(600, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.Invoke(() =>
            {
                if (_pendingActionHide)
                    HideWindowGroup();
            });
        }, TaskScheduler.Default);
    }

    private void OnRecentDockWindowActivated(object? sender, EventArgs e)
    {
        _deactivateCts?.Cancel();
        // If a folder/file open is pending and the user came back to Recents
        // (e.g. Explorer didn't take foreground), cancel the fallback hide.
        if (_pendingActionHide)
        {
            _pendingActionHide = false;
            _actionHideCts?.Cancel();
        }
    }

    private async void OnRecentDockWindowDeactivated(object? sender, EventArgs e)
    {
        // When an action (open file/folder) was explicitly triggered, always hide
        // when focus moves away — even if HideOnFocusLost is off.  Explorer grabbed
        // the foreground, which is exactly what we wanted; now dismiss Recents.
        if (_pendingActionHide)
        {
            if (_contextMenuOpen)
                return;

            _pendingActionHide = false;
            _actionHideCts?.Cancel();
            HideWindowGroup();
            return;
        }

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

        var viewModel = new SettingsViewModel(_settings, _indexService, _clipboardStore, _restartSourcesAsync, _rebuildIndexAsync);
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
        _viewModel.UpdatePopPasteHotkey(_settings.Current.PopPasteHotkey);
        _viewModel.ItemsView.Refresh();
        _viewModel.ClipboardItemsView.Refresh();
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
            OpenContextMenuForItem(listBoxItem?.DataContext, listBoxItem);
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
            if (_pendingActionHide)
            {
                _pendingActionHide = false;
                _actionHideCts?.Cancel();
                HideWindowGroup();
            }
        };

        menu.IsOpen = true;
    }

    private void OpenClipboardContextMenu(ClipboardItemViewModel vm, UIElement placementTarget)
    {
        var menu = ClipboardContextMenuBuilder.Build(vm);
        menu.PlacementTarget = placementTarget;

        _contextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
        };

        menu.IsOpen = true;
    }

    private void OpenClipboardFavoriteContextMenu(ClipboardFavoriteViewModel vm, UIElement placementTarget)
    {
        var menu = ClipboardContextMenuBuilder.Build(vm);
        menu.PlacementTarget = placementTarget;

        _contextMenuOpen = true;
        menu.Closed += (_, _) => { _contextMenuOpen = false; };
        menu.IsOpen = true;
    }

    private void OpenContextMenuForItem(object? item, UIElement? placementTarget)
    {
        if (placementTarget is null) return;
        switch (item)
        {
            case RecentItemViewModel recent:
                OpenDynamicContextMenu(recent, placementTarget);
                break;
            case ClipboardItemViewModel clip:
                OpenClipboardContextMenu(clip, placementTarget);
                break;
            case ClipboardFavoriteViewModel favorite:
                OpenClipboardFavoriteContextMenu(favorite, placementTarget);
                break;
        }
    }

    private int GetFavoriteDropIndex(System.Windows.Point posInList)
    {
        var items = _viewModel.FavoritesView.Cast<object>().ToList();
        for (var i = 0; i < items.Count; i++)
        {
            if (FavoritesList.ItemContainerGenerator.ContainerFromItem(items[i]) is not FrameworkElement container)
                continue;
            var itemPos = container.TranslatePoint(new System.Windows.Point(0, 0), FavoritesList);
            if (posInList.Y < itemPos.Y + container.ActualHeight / 2)
                return i;
        }

        return items.Count;
    }

    private static string GetFavoriteDisplayName(object? item) => item switch
    {
        RecentItemViewModel recent => recent.FavoriteDisplayName,
        ClipboardFavoriteViewModel clip => clip.DisplayName,
        FavoriteGroupViewModel group => group.Name,
        _ => string.Empty
    };

    private void ClipSubChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is System.Windows.Controls.RadioButton rb)
            _viewModel.ClipboardSubFilter = rb.Tag?.ToString() ?? "All";
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
        if (!_settings.Current.PreviewEnabled)
        {
            System.Windows.MessageBox.Show("Quick preview is disabled because the WebView2 runtime is unavailable.",
                "Preview unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = GetPreviewCandidate();
        var previewKey = GetPreviewKey(item);
        if (previewKey is null) return;

        var pw = EnsurePreviewWindow();

        if (pw.IsVisible && pw.Tag as string == previewKey)
        {
            // 二次 Space → 关闭
            pw.Hide();
        }
        else
        {
            pw.Tag = previewKey;
            pw.PositionRelativeTo(this);
            if (pw.Owner == null) pw.Owner = this;
            pw.Show();
            _ = ShowPreviewForItemAsync(pw, item);
        }
    }

    private void SchedulePreviewRefresh(object? item)
    {
        var previewKey = GetPreviewKey(item);
        if (previewKey is null) return;

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
                    _previewWindow.Tag = previewKey;
                    _ = ShowPreviewForItemAsync(_previewWindow, item);
                }
            });
        }, TaskScheduler.Default);
    }

    private object? GetPreviewCandidate()
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            var focusedList = focused as System.Windows.Controls.ListBox ??
                              FindParent<System.Windows.Controls.ListBox>(focused);
            if (focusedList == FavoritesList)
                return FavoritesList.SelectedItem;
        }

        return ItemsList.SelectedItem ?? FavoritesList.SelectedItem;
    }

    private static string? GetPreviewKey(object? item) => item switch
    {
        RecentItemViewModel recent => recent.DisplayPath,
        ClipboardItemViewModel clip => "clipboard:" + clip.Item.Id,
        ClipboardFavoriteViewModel favorite => "clipboard-favorite:" + favorite.Item.Id,
        _ => null
    };

    private static Task ShowPreviewForItemAsync(PreviewWindow previewWindow, object? item) => item switch
    {
        RecentItemViewModel recent => previewWindow.ShowFileAsync(recent.DisplayPath),
        ClipboardItemViewModel clip => previewWindow.ShowClipboardItemAsync(clip.Item),
        ClipboardFavoriteViewModel favorite => previewWindow.ShowClipboardItemAsync(favorite.Item.ToClipboardItem()),
        _ => Task.CompletedTask
    };

    public void PrewarmPreview()
    {
        if (_settings.Current.PreviewEnabled)
            _ = EnsurePreviewWindow(); // 触发 PreviewWindow 构造 + InitWebView2Async
    }

    private async void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        await Task.Delay(150);
        Dispatcher.Invoke(HideWindowGroup);
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

    private sealed class FavoriteDragAdorner : Adorner
    {
        private readonly FormattedText _text;
        private System.Windows.Point _pos;

        public FavoriteDragAdorner(UIElement adornedElement, string name)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            _text = new FormattedText(
                name,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                System.Windows.Media.Brushes.White,
                VisualTreeHelper.GetDpi(adornedElement).PixelsPerDip);
        }

        public void UpdatePosition(System.Windows.Point adornerLayerPos)
        {
            _pos = adornerLayerPos;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var rect = new Rect(_pos.X + 14, _pos.Y - 14, _text.Width + 16, _text.Height + 8);
            dc.DrawRoundedRectangle(
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(210, 37, 99, 235)),
                null, rect, 6, 6);
            dc.DrawText(_text, new System.Windows.Point(rect.X + 8, rect.Y + 4));
        }
    }
}
