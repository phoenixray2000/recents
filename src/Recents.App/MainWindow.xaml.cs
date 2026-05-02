using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
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
    private readonly TrayService _tray;

    public MainWindow(MainViewModel viewModel, SettingsService settings, TrayService tray)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _tray = tray;
        DataContext = _viewModel;
        
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
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // PRD §6.2 关闭主窗口仅隐藏到托盘
        e.Cancel = true;
        Hide();

        // B1. 首次关闭时显示气泡提示 (PRD §7.8)
        if (!_settings.Current.ClosedToTrayNoticeShown)
        {
            _tray.ShowBalloon("Recents", "The app is still running in the tray.", System.Windows.Forms.ToolTipIcon.Info);
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

    // 处理全局快捷键：Esc 隐藏，Ctrl+F 聚焦搜索，Enter 打开等
    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
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
                if (ItemsList.SelectedItem is RecentItemViewModel selected)
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        FileActionService.CopyFileName(selected.DisplayPath);
                    else
                        FileActionService.CopyPath(selected.DisplayPath);
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.O)
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
            var selectedVms = ItemsList.SelectedItems.Cast<RecentItemViewModel>()
                                     .Where(v => !v.IsMissing)
                                     .ToList();

            if (selectedVms.Count == 0) return;

            var paths = selectedVms.Select(v => v.DisplayPath).ToArray();
            var dataObj = DragDropService.CreateDataObject(paths);
            System.Windows.DragDrop.DoDragDrop(ItemsList, dataObj, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
    }

    // 提供给 App 激活窗口时调用的公共方法
    public void ShowAndFocus()
    {
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        if (sender is System.Windows.Controls.RadioButton rb)
        {
            var category = rb.Content?.ToString() ?? "All";
            // 映射 UI 显示名到内部分类名
            if (category == "Recent Folders") category = "Folders";
            if (category == "Docs") category = "Documents";
            
            _viewModel.CurrentCategory = category;
        }
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
}
