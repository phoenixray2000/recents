using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Recents.App.Services.Clipboard;
using Recents.App.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Recents.App.Views;

public partial class ClipboardPopupWindow : Window, IRecentDockWindow
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
    }

    public void AppendSearchText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _viewModel.SearchText += text;
        ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
    }

    public void BackspaceSearchText()
    {
        if (string.IsNullOrEmpty(_viewModel.SearchText))
            return;

        _viewModel.SearchText = _viewModel.SearchText[..^1];
        ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
    }

    public void ClearSearchText()
    {
        _viewModel.SearchText = string.Empty;
        ItemsList.SelectedIndex = ItemsList.Items.Count > 0 ? 0 : -1;
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
        await AcceptSelectedAsync(_pasteService.ShouldPastePlainTextOnDoubleClick());
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_accepting && IsKeyboardFocusWithin)
            Close();
    }
}
