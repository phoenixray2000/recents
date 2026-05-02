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
using Serilog;

namespace Recents.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;
    private readonly RecentIndexService _indexService;
    private readonly Func<Task> _rebuildIndexAsync;
    private readonly Func<Task> _restartSourcesAsync;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;

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
        Topmost = _settings.Current.AlwaysOnTop;
        UpdateResponsiveLayout();
        
        // 首次显示时自动聚焦
        Loaded += (s, e) => SearchBox.Focus();

        // B2. 动态键盘提示
        ItemsList.SelectionChanged += (s, e) => 
        {
            var hasSelection = ItemsList.SelectedItem != null;
            var selectedItem = ItemsList.SelectedItem as RecentItemViewModel;
            var canDrag = hasSelection && selectedItem != null && !selectedItem.IsMissing;
            _viewModel.Status.UpdateHint(hasSelection, canDrag);
        };

        Deactivated += (_, _) =>
        {
            if (_settings.Current.HideOnFocusLost && _settingsWindow is null)
                Hide();
        };
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
        Hide();

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
        var compact = ActualWidth > 0 && ActualWidth < 900;
        _viewModel.IsCompactSidebar = compact;
        SidebarColumn.Width = new GridLength(compact ? 64 : 220);
        LogoText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    // 处理全局快捷键：Esc 隐藏，Ctrl+F 聚焦搜索，Enter 打开等
    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
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
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.Key == Key.Enter)
        {
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

    private void TryOpenSelectedItem()
    {
        if (ItemsList.SelectedItem is RecentItemViewModel vm)
        {
            // PRD §6.24：文件夹双击用 Explorer 打开，文件用默认程序打开
            if (vm.Item.IsFolder)
                FileActionService.RevealInExplorer(vm.DisplayPath);
            else
                FileActionService.OpenFile(vm.DisplayPath);
            Hide();
        }
    }

    // P0 拖拽支持 (PRD §6.8)
    // B4. 批量拖拽支持 (PRD §6.8)
    private void ItemsList_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && ItemsList.SelectedItems.Count > 0)
        {
            var selectedVms = GetSelectedItems().Where(v => !v.IsMissing).ToList();

            if (selectedVms.Count == 0) return;

            var paths = selectedVms.Select(v => v.DisplayPath).ToArray();
            var dataObj = DragDropService.CreateDataObject(paths);
            System.Windows.DragDrop.DoDragDrop(ItemsList, dataObj, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
    }

    private IEnumerable<RecentItemViewModel> GetSelectedItems() =>
        ItemsList.SelectedItems.Cast<RecentItemViewModel>();

    // 提供给 App 激活窗口时调用的公共方法
    public void ShowAndFocus()
    {
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ToggleVisibility()
    {
        if (IsVisible && IsActive)
        {
            Hide();
        }
        else
        {
            ShowAndFocus();
        }
    }
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        if (sender is System.Windows.Controls.RadioButton rb)
        {
            var category = rb.Content?.ToString() ?? "All";
            
            // 映射 UI 显示名到内部分类名 (Sidebar only)
            if (category == "Recent Folders") category = "Folders";
            if (category == "All Files") category = "All";
            
            if (category == "Settings")
            {
                OpenSettings();
                rb.IsChecked = false;
                return;
            }

            _viewModel.CurrentNavCategory = category;
        }
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
        _settingsWindow.Closed += (_, _) =>
        {
            viewModel.SettingsChanged -= ApplySettings;
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    private void ApplySettings()
    {
        Topmost = _settings.Current.AlwaysOnTop;
        _viewModel.UpdateHotkey(_settings.Current.Hotkey);
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
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var grid = FindParent<Grid>(fe);
            if (grid?.ContextMenu != null)
            {
                grid.ContextMenu.PlacementTarget = fe;
                grid.ContextMenu.IsOpen = true;
            }
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }
}
