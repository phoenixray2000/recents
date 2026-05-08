using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Localization;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Clipboard;

namespace Recents.App.ViewModels;

public partial class ClipboardFavoriteViewModel : ObservableObject
{
    private readonly ClipboardStoreService _store;
    private readonly ClipboardActionService _actions;
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ClipboardFavoriteItem Item { get; }
    public bool IsMissing => !_actions.HasUsableContent(Item.ToClipboardItem());
    public bool HasPlainText => _actions.HasPlainText(Item.ToClipboardItem());
    public bool HasHtml => _actions.HasHtml(Item.ToClipboardItem());
    public bool HasRichText => _actions.HasRichText(Item.ToClipboardItem());
    public bool HasImage => _actions.HasImage(Item.ToClipboardItem());
    public bool HasFiles => _actions.HasFiles(Item.ToClipboardItem());
    public string OriginalDisplayName => IsMissing
        ? $"[{Loc.T("Clipboard_Missing_Snapshot")}] {(string.IsNullOrWhiteSpace(Item.PreviewText) ? TypeLabel : Item.PreviewText)}"
        : string.IsNullOrWhiteSpace(Item.PreviewText) ? TypeLabel : Item.PreviewText;
    public string DisplayName => string.IsNullOrWhiteSpace(Item.FavoriteAlias)
        ? OriginalDisplayName
        : Item.FavoriteAlias!;
    public string DisplayPath => Item.PlainText ?? Item.ImagePath ?? Item.BlobPath ?? TypeLabel;
    public string FavoriteToolTip => string.IsNullOrWhiteSpace(Item.FavoriteAlias)
        ? DisplayPath
        : $"{OriginalDisplayName}{Environment.NewLine}{DisplayPath}";
    public string TypeLabel => Item.Type switch
    {
        ClipboardPayloadType.Text => "Text",
        ClipboardPayloadType.Files => "Files",
        ClipboardPayloadType.Image => "Image",
        ClipboardPayloadType.Html => "HTML",
        ClipboardPayloadType.RichText => "Rich Text",
        _ => "Clipboard"
    };

    public string TypeIcon => Item.Type switch
    {
        ClipboardPayloadType.Text => "\uE8C8",
        ClipboardPayloadType.Files => "\uE8B7",
        ClipboardPayloadType.Image => "\uEB9F",
        ClipboardPayloadType.Html => "\uE736",
        ClipboardPayloadType.RichText => "\uE8D2",
        _ => "\uE946"
    };

    public string Metadata => $"{TypeLabel} · {Item.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    public int FavoriteOrder => Item.FavoriteOrder;
    public Visibility ThumbnailVisibility => Item.Type == ClipboardPayloadType.Image && !string.IsNullOrWhiteSpace(Item.ThumbnailPath)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility IconVisibility => ThumbnailVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnailLoaded) return _thumbnail;
            _thumbnailLoaded = true;
            if (string.IsNullOrWhiteSpace(Item.ThumbnailPath) || !File.Exists(Item.ThumbnailPath))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(Item.ThumbnailPath);
                bitmap.DecodePixelWidth = 48;
                bitmap.EndInit();
                bitmap.Freeze();
                _thumbnail = bitmap;
            }
            catch
            {
                _thumbnail = null;
            }

            return _thumbnail;
        }
    }

    public ClipboardFavoriteViewModel(ClipboardFavoriteItem item, ClipboardStoreService store, ClipboardActionService actions)
    {
        Item = item;
        _store = store;
        _actions = actions;
    }

    private bool CanCopy() => !IsMissing;
    private bool CanCopyPlainText() => HasPlainText;
    private bool CanCopyHtml() => HasHtml;
    private bool CanCopyRichText() => HasRichText;
    private bool CanSaveImageAs() => HasImage;
    private bool CanSaveHtmlAs() => HasHtml;
    private bool CanSaveRtfAs() => HasRichText;
    private bool CanUseFiles() => HasFiles;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task Copy() => await _actions.CopyItemToClipboardAsync(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task PasteToActiveApp() => await _actions.PasteToActiveAppAsync(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanCopyPlainText))]
    private async Task PastePlainTextToActiveApp() => await _actions.PastePlainTextToActiveAppAsync(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanCopyPlainText))]
    private async Task CopyPlainText() => await _actions.CopyPlainTextAsync(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanCopyHtml))]
    private async Task CopyHtml() => await _actions.CopyHtmlAsync(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanCopyRichText))]
    private async Task CopyRichText() => await _actions.CopyRichTextAsync(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanUseFiles))]
    private void OpenFiles() => _actions.OpenFileItem(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanUseFiles))]
    private void RevealFiles() => _actions.RevealFileItem(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanUseFiles))]
    private void CopyFilePaths() => _actions.CopyFilePaths(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanSaveImageAs))]
    private void SaveImageAs() => _actions.SaveImageAs(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanSaveHtmlAs))]
    private void SaveHtmlAs() => _actions.SaveHtmlAs(Item.ToClipboardItem());

    [RelayCommand(CanExecute = nameof(CanSaveRtfAs))]
    private void SaveRtfAs() => _actions.SaveRtfAs(Item.ToClipboardItem());

    [RelayCommand]
    private async Task RemoveFavorite() => await _store.RemoveFavoriteAsync(Item.Id);

    [RelayCommand]
    private async Task RenameFavorite()
    {
        if (!FavoriteAliasPromptService.TryShow(Item.FavoriteAlias, OriginalDisplayName, out var alias))
            return;

        await _store.SetFavoriteAliasAsync(Item.Id, alias);
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        CopyCommand.NotifyCanExecuteChanged();
        PasteToActiveAppCommand.NotifyCanExecuteChanged();
        PastePlainTextToActiveAppCommand.NotifyCanExecuteChanged();
        CopyPlainTextCommand.NotifyCanExecuteChanged();
        CopyHtmlCommand.NotifyCanExecuteChanged();
        CopyRichTextCommand.NotifyCanExecuteChanged();
        OpenFilesCommand.NotifyCanExecuteChanged();
        RevealFilesCommand.NotifyCanExecuteChanged();
        CopyFilePathsCommand.NotifyCanExecuteChanged();
        SaveImageAsCommand.NotifyCanExecuteChanged();
        SaveHtmlAsCommand.NotifyCanExecuteChanged();
        SaveRtfAsCommand.NotifyCanExecuteChanged();
    }
}
