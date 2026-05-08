using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Recents.App.Services;
using Recents.App.Services.Clipboard;
using Recents.App.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Recents.App.Views;

public partial class ClipboardPopupWindow : Window, IRecentDockWindow, IPreviewCommandHost
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly ClipboardPasteService _pasteService;
    private readonly ClipboardPopupViewModel _viewModel;
    private PreviewWindow? _previewWindow;
    private CancellationTokenSource? _previewNavCts;
    private bool _accepting;

    public ClipboardPopupWindow(ClipboardPopupViewModel viewModel, ClipboardPasteService pasteService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _pasteService = pasteService;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ApplyNoActivateStyle();

        Loaded += (_, _) =>
        {
            ItemsList.MaxHeight = _viewModel.MaxRows * 58;
            Height = Math.Min(780, 112 + ItemsList.MaxHeight);
            ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
        };
        Closed += (_, _) =>
        {
            ClosePreview();
            if (_previewWindow is not null)
                App.WindowGroupFocusService.UnregisterWindow(_previewWindow);
        };
    }

    public async Task AcceptSelectedAsync(bool pastePlainText = false)
    {
        if (_accepting) return;
        _accepting = true;
        try
        {
            await _pasteService.AcceptAsync(_viewModel.SelectedItem, pastePlainText);
            Close();
        }
        finally
        {
            _accepting = false;
        }
    }

    public void MoveSelection(int delta)
    {
        var count = ItemsList.Items.Count;
        if (count == 0)
            return;

        var next = ItemsList.SelectedIndex < 0 ? 0 : ItemsList.SelectedIndex + delta;
        next = Math.Clamp(next, 0, count - 1);
        ItemsList.SelectedIndex = next;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        SchedulePreviewRefresh();
    }

    public void AppendSearchText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _viewModel.SearchText += text;
        ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
        SchedulePreviewRefresh();
    }

    public void BackspaceSearchText()
    {
        if (string.IsNullOrEmpty(_viewModel.SearchText))
            return;

        _viewModel.SearchText = _viewModel.SearchText[..^1];
        ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
        SchedulePreviewRefresh();
    }

    public void ClearSearchText()
    {
        _viewModel.SearchText = string.Empty;
        ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
        SchedulePreviewRefresh();
    }

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private async void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            await AcceptSelectedAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            HandleSpaceKey();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            ItemsList.Focus();
            if (ItemsList.SelectedIndex < 0 && ItemsList.Items.Count > 0)
                ItemsList.SelectedIndex = 0;
        }
    }

    private void SearchBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up))
            return;

        ItemsList.Focus();
        if (ItemsList.SelectedIndex < 0 && ItemsList.Items.Count > 0)
            ItemsList.SelectedIndex = 0;
        e.Handled = true;
    }

    private async void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await AcceptSelectedAsync();
    }

    private async void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_pasteService.ShouldPastePlainTextOnClick() || e.ChangedButton != MouseButton.Left)
            return;

        if (e.OriginalSource is not DependencyObject source ||
            FindParent<System.Windows.Controls.Button>(source) != null)
        {
            return;
        }

        var listBoxItem = FindParent<ListBoxItem>(source);
        if (listBoxItem?.DataContext is not ClipboardItemViewModel { HasPlainText: true } item)
            return;

        ItemsList.SelectedItem = item;
        e.Handled = true;
        await AcceptSelectedAsync(pastePlainText: true);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_accepting && IsKeyboardFocusWithin)
            Close();
    }

    public void HandleSpaceKey()
    {
        if (!string.IsNullOrEmpty(_viewModel.SearchText))
        {
            AppendSearchText(" ");
            return;
        }

        TogglePreview();
    }

    private void TogglePreview()
    {
        if (!_pasteService.IsPreviewEnabled)
        {
            System.Windows.MessageBox.Show("Quick preview is disabled because the WebView2 runtime is unavailable.",
                "Preview unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = _viewModel.SelectedItem;
        if (item is null)
            return;

        var previewKey = "clipboard:" + item.Item.Id;
        var previewWindow = EnsurePreviewWindow();
        if (previewWindow.IsVisible && previewWindow.Tag as string == previewKey)
        {
            ClosePreview();
            return;
        }

        previewWindow.Tag = previewKey;
        previewWindow.PositionRelativeTo(this);
        if (previewWindow.Owner is null)
            previewWindow.Owner = this;
        previewWindow.Show();
        _ = previewWindow.ShowClipboardItemAsync(item.Item);
    }

    private PreviewWindow EnsurePreviewWindow()
    {
        if (_previewWindow is null)
        {
            _previewWindow = new PreviewWindow(this, App.WindowGroupFocusService);
            App.WindowGroupFocusService.RegisterWindow(_previewWindow);
        }

        return _previewWindow;
    }

    private void SchedulePreviewRefresh()
    {
        if (_previewWindow?.IsVisible != true)
            return;

        _previewNavCts?.Cancel();
        _previewNavCts = new CancellationTokenSource();
        var token = _previewNavCts.Token;

        _ = Task.Delay(100, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.Invoke(() =>
            {
                if (_previewWindow?.IsVisible != true || _viewModel.SelectedItem is null)
                    return;

                _previewWindow.Tag = "clipboard:" + _viewModel.SelectedItem.Item.Id;
                _ = _previewWindow.ShowClipboardItemAsync(_viewModel.SelectedItem.Item);
            });
        }, TaskScheduler.Default);
    }

    public void ClosePreview()
    {
        if (_previewWindow?.IsVisible == true)
        {
            _previewWindow.Hide();
            _previewWindow.Tag = null;
        }
    }

    public void SelectNextAndRefreshPreview() => MoveSelection(1);

    public void SelectPreviousAndRefreshPreview() => MoveSelection(-1);

    public void OpenSelectedItem() => _ = AcceptSelectedAsync();

    public void CopySelectedItemPath()
    {
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject is null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }
}
