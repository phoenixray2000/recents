using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfControl = System.Windows.Controls.Control;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Recents.App.Services;

internal static class FavoritePromptDialogChrome
{
    private static readonly WpfFontFamily FluentIcons = new("Segoe Fluent Icons");

    public static void Apply(Window window, string title, UIElement body)
    {
        window.Title = title;
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = WpfBrushes.Transparent;
        window.FontFamily = new WpfFontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
        window.SetResourceReference(WpfControl.ForegroundProperty, "TextPrimary");

        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.45,
                Color = Colors.Black
            }
        };
        root.SetResourceReference(Border.BackgroundProperty, "BgMain");
        root.SetResourceReference(Border.BorderBrushProperty, "WindowBorderBrush");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Child = grid;

        var titleBar = BuildTitleBar(window, title);
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        var bodyHost = new Border
        {
            Padding = new Thickness(18)
        };
        bodyHost.SetResourceReference(Border.BackgroundProperty, "BgMain");
        bodyHost.Child = body;
        Grid.SetRow(bodyHost, 1);
        grid.Children.Add(bodyHost);

        window.Content = root;
    }

    public static WpfButton CreateButton(string text, RoutedEventHandler click, bool isDefault = false)
    {
        var button = new WpfButton
        {
            Content = text,
            MinWidth = 72,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "ActionButtonStyle");
        button.Click += click;
        return button;
    }

    public static void StyleTextBox(WpfTextBox textBox)
    {
        textBox.SetResourceReference(WpfControl.BackgroundProperty, "BgInput");
        textBox.SetResourceReference(WpfControl.ForegroundProperty, "TextPrimary");
        textBox.SetResourceReference(WpfControl.BorderBrushProperty, "BorderMuted");
        textBox.SetResourceReference(WpfTextBoxBase.CaretBrushProperty, "TextPrimary");
        textBox.BorderThickness = new Thickness(1);
        textBox.Padding = new Thickness(8, 5, 8, 5);
    }

    public static void StyleSecondaryText(TextBlock textBlock)
    {
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
    }

    private static Border BuildTitleBar(Window window, string title)
    {
        var titleBar = new Border
        {
            CornerRadius = new CornerRadius(10, 10, 0, 0)
        };
        titleBar.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            try
            {
                window.DragMove();
            }
            catch
            {
            }
        };

        var grid = new Grid { Margin = new Thickness(12, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.Child = grid;

        var icon = new TextBlock
        {
            Text = "\uE70F",
            FontFamily = FluentIcons,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetColumn(titleText, 1);
        grid.Children.Add(titleText);

        var close = new WpfButton
        {
            Content = "\uE8BB",
            FontFamily = FluentIcons,
            FontSize = 12,
            Width = 30,
            Height = 30,
            Padding = new Thickness(4),
            ToolTip = "Close"
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "ActionButtonStyle");
        close.Click += (_, _) => window.DialogResult = false;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        return titleBar;
    }
}
