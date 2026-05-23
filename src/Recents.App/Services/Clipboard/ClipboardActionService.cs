using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Recents.App.Models;
using Recents.App.Localization;
using Recents.App.Services;
using Recents.App.ViewModels;
using Serilog;
using WpfDataFormats = System.Windows.DataFormats;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace Recents.App.Services.Clipboard;

public sealed class ClipboardActionService
{
    private readonly ClipboardStoreService _store;
    private readonly SettingsService? _settings;
    private ClipboardCaptureService? _capture;
    private IntPtr _lastPasteTargetHwnd;

    public ClipboardActionService(ClipboardStoreService store, SettingsService? settings = null)
    {
        _store = store;
        _settings = settings;
    }

    public void AttachCapture(ClipboardCaptureService capture) => _capture = capture;

    public IntPtr CapturePasteTargetFromForeground()
    {
        var target = ClipboardPasteTarget.GetExternalForegroundWindow();
        if (target != IntPtr.Zero)
            _lastPasteTargetHwnd = target;
        return _lastPasteTargetHwnd;
    }

    public async Task CopyToClipboardAsync(ClipboardItemViewModel vm)
    {
        try
        {
            await CopyItemToClipboardAsync(vm.Item);
            vm.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardActionService: copy failed for {Id}", vm.Item.Id);
            System.Windows.MessageBox.Show("Unable to copy this clipboard item back to the clipboard.",
                "Clipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task CopyItemToClipboardAsync(ClipboardItem item)
    {
        var data = CreateDataObject(item);
        _capture?.SuppressNext(item.Hash, TimeSpan.FromSeconds(2));
        System.Windows.Clipboard.SetDataObject(data, true);
        await _store.MarkUsedAsync(item);
    }

    public Task WriteItemToClipboardWithoutHistoryAsync(ClipboardItem item, TimeSpan suppression)
    {
        var data = CreateDataObject(item);
        _capture?.SuppressNext(item.Hash, suppression);
        System.Windows.Clipboard.SetDataObject(data, true);
        return Task.CompletedTask;
    }

    public async Task CopyPlainTextAsync(ClipboardItem item)
    {
        var text = ReadPlainTextForClipboard(item);
        if (string.IsNullOrEmpty(text))
            return;

        await CopyPlainTextToClipboardAsync(item, text);
    }

    private async Task CopyPlainTextToClipboardAsync(ClipboardItem item, string text)
    {
        var data = new System.Windows.DataObject();
        SetPlainTextFormats(data, text);
        _capture?.SuppressNext(item.Hash, TimeSpan.FromSeconds(2));
        System.Windows.Clipboard.SetDataObject(data, true);
        await _store.MarkUsedAsync(item);
    }

    public async Task CopyHtmlAsync(ClipboardItem item)
    {
        var html = ReadHtmlForClipboard(item);
        if (string.IsNullOrWhiteSpace(html))
            return;

        var data = new System.Windows.DataObject();
        data.SetData(WpfDataFormats.Html, html);
        if (!string.IsNullOrWhiteSpace(item.PlainText))
            SetPlainTextFormats(data, item.PlainText);

        _capture?.SuppressNext(item.Hash, TimeSpan.FromSeconds(2));
        System.Windows.Clipboard.SetDataObject(data, true);
        await _store.MarkUsedAsync(item);
    }

    public async Task CopyRichTextAsync(ClipboardItem item)
    {
        var rtf = ReadFirstExisting(item.RtfBlobPath, item.BlobPath);
        if (string.IsNullOrWhiteSpace(rtf))
            return;

        var data = new System.Windows.DataObject();
        data.SetData(WpfDataFormats.Rtf, rtf);
        if (!string.IsNullOrWhiteSpace(item.PlainText))
            SetPlainTextFormats(data, item.PlainText);

        _capture?.SuppressNext(item.Hash, TimeSpan.FromSeconds(2));
        System.Windows.Clipboard.SetDataObject(data, true);
        await _store.MarkUsedAsync(item);
    }

    public async Task PasteToActiveAppAsync(ClipboardItem item)
    {
        await PasteToActiveAppAsync(item, HideMainWindowGroup, respectPopPasteEnterBehavior: false);
    }

    public async Task PastePlainTextToActiveAppAsync(ClipboardItem item)
    {
        await PastePlainTextToActiveAppAsync(item, HideMainWindowGroup, respectPopPasteEnterBehavior: false);
    }

    internal async Task PastePlainTextToActiveAppAsync(
        ClipboardItem item,
        Action prepareForPaste,
        bool respectPopPasteEnterBehavior)
    {
        var text = ReadPlainTextForClipboard(item);
        if (string.IsNullOrEmpty(text))
            return;

        var previous = CapturePreviousClipboard();
        await CopyPlainTextToClipboardAsync(item, text);

        if (respectPopPasteEnterBehavior &&
            string.Equals(_settings?.Current.PopPasteEnterBehavior, "CopyOnly", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        prepareForPaste();
        await Task.Delay(400);
        ClipboardPasteTarget.PasteWithKeystrokeOnly();
        RestorePreviousClipboardLater(previous);
    }

    internal async Task PasteToActiveAppAsync(
        ClipboardItem item,
        Action prepareForPaste,
        bool respectPopPasteEnterBehavior)
    {
        var previous = CapturePreviousClipboard();
        await CopyItemToClipboardAsync(item);

        if (respectPopPasteEnterBehavior &&
            string.Equals(_settings?.Current.PopPasteEnterBehavior, "CopyOnly", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        prepareForPaste();
        await Task.Delay(400);
        ClipboardPasteTarget.PasteWithKeystrokeOnly();
        RestorePreviousClipboardLater(previous);
    }

    private System.Windows.IDataObject? CapturePreviousClipboard()
    {
        if (_settings?.Current.RestoreClipboardAfterPaste != true)
            return null;

        try
        {
            return System.Windows.Clipboard.GetDataObject();
        }
        catch
        {
            return null;
        }
    }

    private static void RestorePreviousClipboardLater(System.Windows.IDataObject? previous)
    {
        if (previous is null)
            return;

        _ = Task.Delay(350).ContinueWith(_ =>
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetDataObject(previous, true);
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ClipboardActionService: previous clipboard restore skipped");
            }
        });
    }

    public System.Windows.DataObject CreateDataObject(ClipboardItem item)
    {
        var data = new System.Windows.DataObject();
        switch (item.Type)
        {
            case ClipboardPayloadType.Files:
                var paths = item.FilePaths
                    .Select(f => f.Path)
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .ToArray();
                if (paths.Length > 0)
                {
                    var collection = new StringCollection();
                    collection.AddRange(paths);
                    data.SetFileDropList(collection);
                }
                if (!string.IsNullOrWhiteSpace(item.PlainText))
                    SetPlainTextFormats(data, item.PlainText);
                break;

            case ClipboardPayloadType.Image:
                if (!string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    var bitmap = LoadBitmap(item.ImagePath);
                    if (bitmap is not null)
                        data.SetImage(bitmap);
                    SetImageCompatibilityFormats(data, item.ImagePath);
                }
                break;

            case ClipboardPayloadType.Html:
                var html = ReadHtmlForClipboard(item);
                if (!string.IsNullOrWhiteSpace(html))
                    data.SetData(WpfDataFormats.Html, html);
                if (!string.IsNullOrWhiteSpace(item.PlainText))
                    SetPlainTextFormats(data, item.PlainText);
                break;

            case ClipboardPayloadType.RichText:
                var rtf = ReadFirstExisting(item.RtfBlobPath, item.BlobPath);
                if (!string.IsNullOrWhiteSpace(rtf))
                    data.SetData(WpfDataFormats.Rtf, rtf);
                if (!string.IsNullOrWhiteSpace(item.PlainText))
                    SetPlainTextFormats(data, item.PlainText);
                break;

            default:
                var text = item.PlainText ?? ReadFirstExisting(item.BlobPath);
                if (text is not null)
                    SetPlainTextFormats(data, text);
                break;
        }

        return data;
    }

    public System.Windows.DataObject? CreateBlobFileDropDataObject(ClipboardItem item)
    {
        var path = item.Type switch
        {
            ClipboardPayloadType.Html => item.HtmlBlobPath ?? item.BlobPath,
            ClipboardPayloadType.RichText => item.RtfBlobPath ?? item.BlobPath,
            ClipboardPayloadType.Image => item.ImagePath,
            ClipboardPayloadType.Text => item.BlobPath,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var collection = new StringCollection { path };
        var data = new System.Windows.DataObject();
        data.SetFileDropList(collection);
        return data;
    }

    public bool HasUsableContent(ClipboardItem item) => item.Type switch
    {
        ClipboardPayloadType.Files => ExistingFilePaths(item).Any(),
        ClipboardPayloadType.Image => ExistingPath(item.ImagePath) is not null,
        ClipboardPayloadType.Html => ExistingPath(item.BlobPath, item.HtmlBlobPath) is not null ||
                                     !string.IsNullOrWhiteSpace(item.PlainText),
        ClipboardPayloadType.RichText => ExistingPath(item.RtfBlobPath, item.BlobPath) is not null ||
                                         !string.IsNullOrWhiteSpace(item.PlainText),
        ClipboardPayloadType.Text => !string.IsNullOrEmpty(item.PlainText) ||
                                     ExistingPath(item.BlobPath) is not null,
        _ => !string.IsNullOrEmpty(item.PlainText) || ExistingPath(item.BlobPath) is not null
    };

    public bool HasPlainText(ClipboardItem item) =>
        !string.IsNullOrEmpty(ReadPlainTextForClipboard(item));

    public bool HasHtml(ClipboardItem item) =>
        item.Type == ClipboardPayloadType.Html && ExistingPath(item.BlobPath, item.HtmlBlobPath) is not null;

    public bool HasRichText(ClipboardItem item) =>
        item.Type == ClipboardPayloadType.RichText && ExistingPath(item.RtfBlobPath, item.BlobPath) is not null;

    public bool HasImage(ClipboardItem item) =>
        item.Type == ClipboardPayloadType.Image && ExistingPath(item.ImagePath) is not null;

    public bool HasFiles(ClipboardItem item) =>
        item.Type == ClipboardPayloadType.Files && ExistingFilePaths(item).Any();

    public void OpenFileItem(ClipboardItem item)
    {
        var path = ExistingFilePaths(item).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(path))
            FileActionService.OpenFile(path);
    }

    public void RevealFileItem(ClipboardItem item)
    {
        var path = ExistingFilePaths(item).FirstOrDefault()
                   ?? ExistingPath(item.ImagePath, item.BlobPath, item.HtmlBlobPath, item.RtfBlobPath);
        if (!string.IsNullOrWhiteSpace(path))
            FileActionService.RevealInExplorer(path);
    }

    public void CopyFilePaths(ClipboardItem item)
    {
        var paths = item.Type == ClipboardPayloadType.Files
            ? ExistingFilePaths(item)
            : new[] { ExistingPath(item.ImagePath, item.BlobPath, item.HtmlBlobPath, item.RtfBlobPath) }
                .OfType<string>();

        FileActionService.CopyPaths(paths);
    }

    public void SaveImageAs(ClipboardItem item) =>
        SaveFileAs(item.ImagePath, "PNG image (*.png)|*.png|All files (*.*)|*.*", ".png");

    public void SaveHtmlAs(ClipboardItem item) =>
        SaveFileAs(item.HtmlBlobPath ?? item.BlobPath, "HTML file (*.html)|*.html|All files (*.*)|*.*", ".html");

    public void SaveRtfAs(ClipboardItem item) =>
        SaveFileAs(item.RtfBlobPath ?? item.BlobPath, "Rich Text Format (*.rtf)|*.rtf|All files (*.*)|*.*", ".rtf");

    private static BitmapSource? LoadBitmap(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void SetImageCompatibilityFormats(System.Windows.DataObject data, string imagePath)
    {
        var collection = new StringCollection { imagePath };
        data.SetFileDropList(collection);
        data.SetData(WpfDataFormats.Html, BuildImageHtml(imagePath));
        data.SetData("QQ_Unicode_RichEdit_Format",
            System.Text.Encoding.UTF8.GetBytes(BuildQqImageFormat(imagePath)));
    }

    private static string BuildImageHtml(string imagePath)
    {
        var uri = new Uri(imagePath);
        return NormalizeHtmlForClipboard($@"<img src=""{uri}"">");
    }

    private const string QqImageFormatTemplate = """
        <QQRichEditFormat>
        <Info version="1001">
        </Info>
        <EditElement type="1" imagebiztype="0" textsummary="" filepath="<<<<<<" shortcut="">
        </EditElement>
        </QQRichEditFormat>
        """;

    private static string BuildQqImageFormat(string imagePath) =>
        QqImageFormatTemplate.Replace("<<<<<<", EscapeXmlAttribute(imagePath), StringComparison.Ordinal);

    private static string EscapeXmlAttribute(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string? ReadFirstExisting(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
        }

        return null;
    }

    private static string? ReadPlainTextForClipboard(ClipboardItem item)
    {
        if (!string.IsNullOrEmpty(item.PlainText))
            return item.PlainText;

        return item.Type switch
        {
            ClipboardPayloadType.Text => ReadFirstExisting(item.BlobPath),
            ClipboardPayloadType.Html => ReadHtmlPlainText(item),
            ClipboardPayloadType.Files => item.FilePaths.Count == 0
                ? null
                : string.Join(Environment.NewLine, item.FilePaths.Select(f => f.Path)),
            _ => null
        };
    }

    private static string? ReadHtmlPlainText(ClipboardItem item)
    {
        var html = ReadFirstExisting(item.HtmlBlobPath, item.BlobPath);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = HtmlSanitizer.ToPlainText(NormalizeHtmlForWpf(html));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static void SetPlainTextFormats(System.Windows.DataObject data, string text)
    {
        data.SetText(text, WpfTextDataFormat.UnicodeText);
        data.SetText(text, WpfTextDataFormat.Text);
        data.SetData(typeof(string), text);
    }

    internal static string NormalizeHtmlForWpf(string html)
    {
        var parsed = CfHtmlParser.Parse(html);
        return LooksLikeCfHtml(html)
            ? FirstNonEmpty(parsed.FragmentHtml, parsed.Html, StripCfHtmlHeaderFallback(html))
            : html;
    }

    internal static string NormalizeHtmlForClipboard(string html)
    {
        if (LooksLikeCfHtml(html) && !string.IsNullOrWhiteSpace(CfHtmlParser.Parse(html).Html))
            return html.TrimStart('\uFEFF');

        return BuildCfHtml(NormalizeHtmlForWpf(html));
    }

    private static string? ReadHtmlForWpf(ClipboardItem item)
    {
        var html = ReadFirstExisting(item.BlobPath, item.HtmlBlobPath);
        return string.IsNullOrWhiteSpace(html) ? null : NormalizeHtmlForWpf(html);
    }

    private static string? ReadHtmlForClipboard(ClipboardItem item)
    {
        var html = ReadFirstExisting(item.BlobPath, item.HtmlBlobPath);
        return string.IsNullOrWhiteSpace(html) ? null : NormalizeHtmlForClipboard(html);
    }

    private static bool LooksLikeCfHtml(string html) =>
        html.TrimStart('\uFEFF').StartsWith("Version:", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string StripCfHtmlHeaderFallback(string html)
    {
        var htmlStart = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlStart >= 0)
            return html[htmlStart..];

        var fragmentStart = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        return fragmentStart >= 0 ? html[fragmentStart..] : html;
    }

    private static string BuildCfHtml(string fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        var body = $"<html><body>{startMarker}{fragment}{endMarker}</body></html>";
        const string headerFormat = """
            Version:0.9
            StartHTML:{0:0000000000}
            EndHTML:{1:0000000000}
            StartFragment:{2:0000000000}
            EndFragment:{3:0000000000}

            """;

        var placeholderHeader = string.Format(headerFormat, 0, 0, 0, 0).Replace("\r\n", "\n");
        var startHtml = System.Text.Encoding.UTF8.GetByteCount(placeholderHeader);
        var endHtml = startHtml + System.Text.Encoding.UTF8.GetByteCount(body);
        var startFragment = startHtml + System.Text.Encoding.UTF8.GetByteCount(body[..body.IndexOf(fragment, StringComparison.Ordinal)]);
        var endFragment = startFragment + System.Text.Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment).Replace("\r\n", "\n");
        return header + body;
    }

    private static string? ExistingPath(params string?[] paths) =>
        paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static IEnumerable<string> ExistingFilePaths(ClipboardItem item) =>
        item.FilePaths
            .Select(f => f.Path)
            .Where(p => File.Exists(p) || Directory.Exists(p));

    private static void SaveFileAs(string? sourcePath, string filter, string defaultExtension)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(sourcePath),
            Filter = filter,
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
            return;

        File.Copy(sourcePath, dialog.FileName, overwrite: true);
    }

    private static void HideMainWindowGroup()
    {
        if (System.Windows.Application.Current?.MainWindow is Recents.App.MainWindow mainWindow)
            mainWindow.HideWindowGroup();
        else
            System.Windows.Application.Current?.MainWindow?.Hide();
    }
}
