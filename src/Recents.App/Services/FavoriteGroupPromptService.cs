using System.Windows;
using Recents.App.Localization;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Recents.App.Services;

public static class FavoriteGroupPromptService
{
    public const int MaxGroupNameLength = 48;

    public static bool TryShow(string? currentName, out string? name)
    {
        name = null;

        var dialog = new FavoriteGroupDialog(currentName);
        var owner = WpfApplication.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? WpfApplication.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;

        using var _ = ShellService.HoldExternalDialogOpen();
        if (dialog.ShowDialog() != true)
            return false;

        name = Normalize(dialog.GroupName);
        return name is not null;
    }

    public static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;
        return trimmed.Length <= MaxGroupNameLength ? trimmed : trimmed[..MaxGroupNameLength];
    }

    private sealed class FavoriteGroupDialog : Window
    {
        private readonly WpfTextBox _textBox;

        public string GroupName => _textBox.Text;

        public FavoriteGroupDialog(string? currentName)
        {
            Title = Loc.T("Main_Favorites_Group_Title");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            MinWidth = 320;

            _textBox = new WpfTextBox
            {
                Text = string.IsNullOrWhiteSpace(currentName) ? Loc.T("Main_Favorites_Group_DefaultName") : currentName,
                MinWidth = 280,
                MaxLength = MaxGroupNameLength,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var root = new WpfStackPanel
            {
                Margin = new Thickness(18),
                Orientation = WpfOrientation.Vertical
            };
            root.Children.Add(new WpfTextBlock
            {
                Text = Loc.T("Main_Favorites_Group_Label"),
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(_textBox);

            var buttons = new WpfStackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            buttons.Children.Add(CreateButton(Loc.T("Action_Cancel"), (_, _) => DialogResult = false));
            buttons.Children.Add(CreateButton(Loc.T("Action_Save"), (_, _) => DialogResult = true, isDefault: true));
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, _) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
        }

        private static WpfButton CreateButton(string text, RoutedEventHandler click, bool isDefault = false)
        {
            var button = new WpfButton
            {
                Content = text,
                MinWidth = 72,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault
            };
            button.Click += click;
            return button;
        }
    }
}
