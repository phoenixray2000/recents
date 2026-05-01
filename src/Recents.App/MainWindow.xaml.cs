using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using Recents.App.ViewModels;
using Serilog;

namespace Recents.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        
        // 首次显示时自动聚焦
        Loaded += (s, e) => SearchBox.Focus();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // PRD §6.2 关闭主窗口仅隐藏到托盘
        e.Cancel = true;
        Hide();
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

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // P0 阶段：直接在此处理 Enter，后续可移入 FileActionService
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
            // PRD §6.9: 双击打开
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vm.DisplayPath,
                    UseShellExecute = true
                });
                Hide(); // 打开后自动隐藏主界面
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to open {Path}", vm.DisplayPath);
                System.Windows.MessageBox.Show($"无法打开文件：\n{ex.Message}", "打开失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    // P0 拖拽支持 (PRD §6.8)
    private void ItemsList_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && ItemsList.SelectedItem is RecentItemViewModel vm)
        {
            // 如果 Missing 禁止拖拽
            if (vm.IsMissing) return;

            // TODO: P1 加入 CFSTR_SHELLIDLIST，P0 先使用基本的 FileDrop
            var dataObj = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new string[] { vm.DisplayPath });
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
}
