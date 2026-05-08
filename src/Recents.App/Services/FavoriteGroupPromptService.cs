using System.Windows;
using Recents.App.Localization;
using WpfApplication = System.Windows.Application;
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
            MinWidth = 340;

            _textBox = new WpfTextBox
            {
                Text = string.IsNullOrWhiteSpace(currentName) ? Loc.T("Main_Favorites_Group_DefaultName") : currentName,
                MinWidth = 280,
                MaxLength = MaxGroupNameLength,
                Margin = new Thickness(0, 8, 0, 0)
            };
            FavoritePromptDialogChrome.StyleTextBox(_textBox);

            var root = new WpfStackPanel
            {
                Orientation = WpfOrientation.Vertical
            };
            var label = new WpfTextBlock
            {
                Text = Loc.T("Main_Favorites_Group_Label"),
                TextWrapping = TextWrapping.Wrap
            };
            FavoritePromptDialogChrome.StyleSecondaryText(label);
            root.Children.Add(label);
            root.Children.Add(_textBox);

            var buttons = new WpfStackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            buttons.Children.Add(FavoritePromptDialogChrome.CreateButton(Loc.T("Action_Cancel"), (_, _) => DialogResult = false));
            buttons.Children.Add(FavoritePromptDialogChrome.CreateButton(Loc.T("Action_Save"), (_, _) => DialogResult = true, isDefault: true));
            root.Children.Add(buttons);

            FavoritePromptDialogChrome.Apply(this, Title, root);
            Loaded += (_, _) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
        }
    }
}
