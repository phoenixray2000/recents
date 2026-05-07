using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Localization;
using Recents.App.Models;
using Recents.App.Services.Clipboard;

namespace Recents.App.ViewModels;

public partial class ClipboardItemViewModel : ObservableObject
{
    private readonly ClipboardStoreService _store;
    private readonly ClipboardActionService _actions;
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ClipboardItem Item { get; }
    public string Id => Item.Id;
    public ClipboardPayloadType Type => Item.Type;
    public string PreviewText => string.IsNullOrWhiteSpace(Item.PreviewText) ? "(empty)" : Item.PreviewText;
    public string DisplayText => IsMissing ? $"[{Loc.T("Clipboard_Missing_Blob")}] {PreviewText}" : PreviewText;
    public bool IsMissing => !_actions.HasUsableContent(Item);
    public bool HasPlainText => _actions.HasPlainText(Item);
    public bool HasHtml => _actions.HasHtml(Item);
    public bool HasRichText => _actions.HasRichText(Item);
    public bool HasImage => _actions.HasImage(Item);
    public bool HasFiles => _actions.HasFiles(Item);
    public string TypeLabel => Item.Type switch
    {
        ClipboardPayloadType.Text => "Text",
        ClipboardPayloadType.Files => "Files",
        ClipboardPayloadType.Image => "Image",
        ClipboardPayloadType.Html => "HTML",
        ClipboardPayloadType.RichText => "Rich Text",
        _ => "Unknown"
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

    public string CreatedDisplay => Item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Metadata
    {
        get
        {
            var parts = new List<string> { CreatedDisplay };
            if (Item.Type == ClipboardPayloadType.Text && Item.TextLength.HasValue)
                parts.Add($"{Item.TextLength.Value:N0} chars");
            if (Item.Type == ClipboardPayloadType.Files)
                parts.Add($"{Item.FilePaths.Count:N0} path(s)");
            if (Item.Type == ClipboardPayloadType.Image && Item.ImageWidth.HasValue && Item.ImageHeight.HasValue)
                parts.Add($"{Item.ImageWidth}x{Item.ImageHeight}");
            if (Item.SizeBytes.HasValue)
                parts.Add(FormatBytes(Item.SizeBytes.Value));
            if (!string.IsNullOrWhiteSpace(Item.SourceAppName))
                parts.Add(Item.SourceAppName);
            return string.Join(" · ", parts);
        }
    }

    public Visibility ThumbnailVisibility => Item.Type == ClipboardPayloadType.Image && !string.IsNullOrWhiteSpace(Item.ThumbnailPath)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility IconVisibility => ThumbnailVisibility == Visibility.Visible
        ? Visibility.Collapsed
        : Visibility.Visible;

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
                bitmap.DecodePixelWidth = 96;
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

    public ClipboardItemViewModel(ClipboardItem item, ClipboardStoreService store, ClipboardActionService actions)
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
    private async Task Copy() => await _actions.CopyToClipboardAsync(this);

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task PasteToActiveApp() => await _actions.PasteToActiveAppAsync(Item);

    [RelayCommand(CanExecute = nameof(CanCopyPlainText))]
    private async Task PastePlainTextToActiveApp() => await _actions.PastePlainTextToActiveAppAsync(Item);

    [RelayCommand(CanExecute = nameof(CanCopyPlainText))]
    private async Task CopyPlainText() => await _actions.CopyPlainTextAsync(Item);

    [RelayCommand(CanExecute = nameof(CanCopyHtml))]
    private async Task CopyHtml() => await _actions.CopyHtmlAsync(Item);

    [RelayCommand(CanExecute = nameof(CanCopyRichText))]
    private async Task CopyRichText() => await _actions.CopyRichTextAsync(Item);

    [RelayCommand(CanExecute = nameof(CanUseFiles))]
    private void OpenFiles() => _actions.OpenFileItem(Item);

    [RelayCommand(CanExecute = nameof(CanUseFiles))]
    private void RevealFiles() => _actions.RevealFileItem(Item);

    [RelayCommand(CanExecute = nameof(CanUseFiles))]
    private void CopyFilePaths() => _actions.CopyFilePaths(Item);

    [RelayCommand(CanExecute = nameof(CanSaveImageAs))]
    private void SaveImageAs() => _actions.SaveImageAs(Item);

    [RelayCommand(CanExecute = nameof(CanSaveHtmlAs))]
    private void SaveHtmlAs() => _actions.SaveHtmlAs(Item);

    [RelayCommand(CanExecute = nameof(CanSaveRtfAs))]
    private void SaveRtfAs() => _actions.SaveRtfAs(Item);

    [RelayCommand]
    private async Task Delete() => await _store.DeleteAsync(Item.Id);

    [RelayCommand]
    private async Task ToggleFavorite() => await _store.ToggleFavoriteAsync(Item.Id);

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

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}
