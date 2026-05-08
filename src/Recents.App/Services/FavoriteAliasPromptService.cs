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

public static class FavoriteAliasPromptService
{
    public const int MaxAliasLength = 120;

    public static bool TryShow(string? currentAlias, string fallbackName, out string? alias)
    {
        alias = null;

        var dialog = new FavoriteAliasDialog(currentAlias, fallbackName);
        var owner = WpfApplication.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? WpfApplication.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;

        using var _ = ShellService.HoldExternalDialogOpen();
        if (dialog.ShowDialog() != true)
            return false;

        alias = Normalize(dialog.AliasText);
        return true;
    }

    public static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;
        return trimmed.Length <= MaxAliasLength ? trimmed : trimmed[..MaxAliasLength];
    }

    private sealed class FavoriteAliasDialog : Window
    {
        private readonly WpfTextBox _textBox;

        public string AliasText => _textBox.Text;

        public FavoriteAliasDialog(string? currentAlias, string fallbackName)
        {
            Title = Loc.T("Main_Favorites_Rename_Title");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            MinWidth = 320;

            _textBox = new WpfTextBox
            {
                Text = string.IsNullOrWhiteSpace(currentAlias) ? fallbackName : currentAlias,
                MinWidth = 280,
                MaxLength = MaxAliasLength,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var root = new WpfStackPanel
            {
                Margin = new Thickness(18),
                Orientation = WpfOrientation.Vertical
            };
            root.Children.Add(new WpfTextBlock
            {
                Text = Loc.T("Main_Favorites_Rename_Label"),
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(_textBox);

            var buttons = new WpfStackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            buttons.Children.Add(CreateButton(Loc.T("Main_Favorites_Rename_Clear"), (_, _) =>
            {
                _textBox.Clear();
                DialogResult = true;
            }));
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
