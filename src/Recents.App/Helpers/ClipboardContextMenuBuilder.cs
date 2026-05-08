using System.Windows;
using System.Windows.Controls;
using Recents.App.Models;
using Recents.App.Localization;
using Recents.App.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfApplication = System.Windows.Application;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Recents.App.Helpers;

public static class ClipboardContextMenuBuilder
{
    private static readonly System.Windows.Media.FontFamily FluentIcons = new("Segoe Fluent Icons");

    public static ContextMenu Build(ClipboardItemViewModel vm)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_Copy"), vm.CopyCommand));
        menu.Items.Add(CreateItem("\uE77F", Loc.T("Clipboard_Action_PasteToActiveApp"), vm.PasteToActiveAppCommand));
        menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_PastePlainTextToActiveApp"), vm.PastePlainTextToActiveAppCommand));
        AddTypeItems(menu, vm.Item.Type,
            vm.CopyCommand,
            vm.CopyPlainTextCommand,
            vm.CopyHtmlCommand,
            vm.CopyRichTextCommand,
            vm.OpenFilesCommand,
            vm.RevealFilesCommand,
            vm.CopyFilePathsCommand,
            vm.SaveImageAsCommand,
            vm.SaveHtmlAsCommand,
            vm.SaveRtfAsCommand);
        menu.Items.Add(CreateItem("\uE735", Loc.T("Clipboard_Action_ToggleFavorite"), vm.ToggleFavoriteCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("\uE74D", Loc.T("Clipboard_Action_Delete"), vm.DeleteCommand, isDanger: true));
        return menu;
    }

    public static ContextMenu Build(ClipboardFavoriteViewModel vm)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_Copy"), vm.CopyCommand));
        menu.Items.Add(CreateItem("\uE77F", Loc.T("Clipboard_Action_PasteToActiveApp"), vm.PasteToActiveAppCommand));
        menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_PastePlainTextToActiveApp"), vm.PastePlainTextToActiveAppCommand));
        AddTypeItems(menu, vm.Item.Type,
            vm.CopyCommand,
            vm.CopyPlainTextCommand,
            vm.CopyHtmlCommand,
            vm.CopyRichTextCommand,
            vm.OpenFilesCommand,
            vm.RevealFilesCommand,
            vm.CopyFilePathsCommand,
            vm.SaveImageAsCommand,
            vm.SaveHtmlAsCommand,
            vm.SaveRtfAsCommand);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("\uE70F", Loc.T("Main_Favorites_Rename"), vm.RenameFavoriteCommand));
        menu.Items.Add(CreateItem("\uE735", Loc.T("Main_Favorites_Unpin"), vm.RemoveFavoriteCommand, isDanger: true));
        return menu;
    }

    private static void AddTypeItems(
        ContextMenu menu,
        ClipboardPayloadType type,
        System.Windows.Input.ICommand copy,
        System.Windows.Input.ICommand copyPlainText,
        System.Windows.Input.ICommand copyHtml,
        System.Windows.Input.ICommand copyRichText,
        System.Windows.Input.ICommand openFiles,
        System.Windows.Input.ICommand revealFiles,
        System.Windows.Input.ICommand copyFilePaths,
        System.Windows.Input.ICommand saveImageAs,
        System.Windows.Input.ICommand saveHtmlAs,
        System.Windows.Input.ICommand saveRtfAs)
    {
        switch (type)
        {
            case ClipboardPayloadType.Text:
                menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_CopyPlainText"), copyPlainText));
                break;

            case ClipboardPayloadType.Files:
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateItem("\uE8E5", Loc.T("Clipboard_Action_Open"), openFiles));
                menu.Items.Add(CreateItem("\uE81D", Loc.T("Clipboard_Action_Reveal"), revealFiles));
                menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_CopyPath"), copyFilePaths));
                break;

            case ClipboardPayloadType.Image:
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateItem("\uEB9F", Loc.T("Clipboard_Action_CopyImage"), copy));
                menu.Items.Add(CreateItem("\uE74E", Loc.T("Clipboard_Action_SaveImageAs"), saveImageAs));
                break;

            case ClipboardPayloadType.Html:
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateItem("\uE736", Loc.T("Clipboard_Action_CopyHtml"), copyHtml));
                menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_CopyPlainText"), copyPlainText));
                menu.Items.Add(CreateItem("\uE74E", Loc.T("Clipboard_Action_SaveHtmlAs"), saveHtmlAs));
                break;

            case ClipboardPayloadType.RichText:
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateItem("\uE8D2", Loc.T("Clipboard_Action_CopyRichText"), copyRichText));
                menu.Items.Add(CreateItem("\uE8C8", Loc.T("Clipboard_Action_CopyPlainText"), copyPlainText));
                menu.Items.Add(CreateItem("\uE74E", Loc.T("Clipboard_Action_SaveRtfAs"), saveRtfAs));
                break;
        }
    }

    private static MenuItem CreateItem(string icon, string label, System.Windows.Input.ICommand command, bool isDanger = false)
    {
        return new MenuItem
        {
            Header = CreateHeader(icon, label, isDanger),
            Command = command
        };
    }

    private static StackPanel CreateHeader(string icon, string label, bool isDanger)
    {
        var iconColor = isDanger
            ? (WpfBrush)WpfApplication.Current.FindResource("ColorDanger")
            : (WpfBrush)WpfApplication.Current.FindResource("TextSecondary");

        var textColor = isDanger
            ? iconColor
            : (WpfBrush)WpfApplication.Current.FindResource("TextPrimary");

        var sp = new StackPanel { Orientation = WpfOrientation.Horizontal };
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
        return sp;
    }
}
