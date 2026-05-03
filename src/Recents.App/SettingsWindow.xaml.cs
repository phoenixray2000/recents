using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Recents.App.ViewModels;

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

        if (DataContext is SettingsViewModel vm)
        {
            vm.Hotkey = sb.ToString();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
