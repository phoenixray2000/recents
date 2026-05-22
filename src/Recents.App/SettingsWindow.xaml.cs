using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Recents.App.ViewModels;
using WpfTextBox = System.Windows.Controls.TextBox;

using Recents.App.Views;

namespace Recents.App;

public partial class SettingsWindow : Window, IRecentDockWindow
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            SetShortcutValue(sender, string.Empty);
            return;
        }

        // Ignore modifier keys by themselves
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var sb = new StringBuilder();

        if ((modifiers & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((modifiers & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((modifiers & ModifierKeys.Shift) != 0) sb.Append("Shift+");

        sb.Append(key.ToString());

        SetShortcutValue(sender, sb.ToString());
    }

    private void SetShortcutValue(object sender, string value)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        if (sender is WpfTextBox tb)
        {
            if (string.Equals(tb.Tag as string, "PopPasteHotkey", StringComparison.Ordinal))
            {
                vm.PopPasteHotkey = string.IsNullOrWhiteSpace(value) ? "Alt+Shift+V" : value;
                return;
            }

        }

        vm.Hotkey = string.IsNullOrWhiteSpace(value) ? "Alt+Shift+Z" : value;
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ClipboardWebDavPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
            vm.ClipboardWebDavPassword = box.Password;
    }
}
