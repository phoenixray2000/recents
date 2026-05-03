using System.Windows;
using System.Windows.Controls;
using Recents.App.ViewModels;
using WpfBrush = System.Windows.Media.Brush;

namespace Recents.App.Helpers;

/// <summary>
/// 动态构建 ContextMenu，每次调用都创建全新的 UIElement 实例。
/// 
/// WPF 的 ContextMenu 在独立 Popup HWND 中渲染。当 Popup 关闭时，
/// Visual Tree 被拆除，但 MenuItem 子元素（TextBlock 等）仍持有
/// 旧 Popup 的 Visual Parent 引用。下次 Popup 打开时，WPF 试图
/// 将同一 UIElement 挂载到新 Visual Tree，但旧引用未同步清理，
/// 导致 UIElement 静默失败（渲染为空白）。
/// 
/// 因此，每次右键菜单打开时必须创建全新实例。
/// </summary>
public static class ContextMenuBuilder
{
    private static readonly System.Windows.Media.FontFamily FluentIcons = new("Segoe Fluent Icons");

    public static ContextMenu Build(RecentItemViewModel vm)
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateItem("\uED25", "Open", vm.OpenCommand));
        menu.Items.Add(CreateItem("\uE7BC", "Open With...", vm.OpenWithCommand));
        menu.Items.Add(CreateItem("\uE81D", "Reveal in Explorer", vm.RevealCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("\uE8C8", "Copy Full Path", vm.CopyPathCommand));
        menu.Items.Add(CreateItem("\uE8C8", "Copy File Name", vm.CopyFileNameCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("\uE735", "Toggle Pin", vm.TogglePinCommand));
        menu.Items.Add(CreateItem("\uE894", "Hide from list", vm.HideFromListCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("\uE74D", "Remove Once", vm.RemoveOnceCommand, isDanger: true));

        return menu;
    }

    private static MenuItem CreateItem(
        string icon, string label,
        System.Windows.Input.ICommand command,
        bool isDanger = false)
    {
        var iconColor = isDanger
            ? (WpfBrush)System.Windows.Application.Current.FindResource("ColorDanger")
            : (WpfBrush)System.Windows.Application.Current.FindResource("TextSecondary");

        var textColor = isDanger
            ? iconColor
            : (WpfBrush)System.Windows.Application.Current.FindResource("TextPrimary");

        var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = FluentIcons,
            FontSize = 13,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = iconColor
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textColor
        });

        return new MenuItem
        {
            Header = sp,
            Command = command
        };
    }
}
