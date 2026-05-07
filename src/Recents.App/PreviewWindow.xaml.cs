using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Recents.App.Models;
using Recents.App.Services.Preview;
using Serilog;
using Recents.App.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

using Recents.App.Views;
using System.Threading;

namespace Recents.App;

public partial class PreviewWindow : Window, IRecentDockWindow
{
    // 虚拟主机名称（随机，避免冲突）
    private const string VirtualHostName = "preview.local";
    private const string VirtualHostBase = $"https://{VirtualHostName}/";

    private bool _webView2Ready = false;
    private string? _pendingPath = null;    // 在 WebView2 初始化完成前缓存的路径
    private ClipboardItem? _pendingClipboardItem = null;
    private string? _currentPath = null;
    private ClipboardItem? _currentClipboardItem = null;
    private string? _imageDimensions = null;
    private readonly IPreviewCommandHost _commandHost;
    private readonly IWindowGroupFocusService _windowGroupFocusService;
    private CancellationTokenSource? _interactionCts;

    public PreviewWindow(IPreviewCommandHost commandHost, IWindowGroupFocusService windowGroupFocusService)
    {
        InitializeComponent();
        _commandHost = commandHost;
        _windowGroupFocusService = windowGroupFocusService;

        InitWebView2Async();

        // 键盘：转发到主窗口
        PreviewKeyDown += OnPreviewWindowKeyDown;

        // 交互追踪：防止误隐藏
        PreviewMouseDown += (s, e) => _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = true;
        PreviewMouseUp += async (s, e) =>
        {
            await Task.Delay(200);
            _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = false;
        };
        SizeChanged += (s, e) => MarkWindowGroupInteraction();
        LocationChanged += (s, e) => MarkWindowGroupInteraction();
    }

    // ── WebView2 初始化（预热）──────────────────────────────────────────
    private async void InitWebView2Async()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            _webView2Ready = true;

            // VirtualHost：把每次预览文件所在目录映射到 https://preview.local/
            // 注意：每次显示不同目录时需要重新映射（在 ShowFileAsync 中处理）

            WebView.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            WebView.PreviewKeyDown += OnPreviewWindowKeyDown;

            // 处理预热前积压的请求
            if (_pendingPath != null)
            {
                var path = _pendingPath;
                _pendingPath = null;
                await ShowFileAsync(path);
            }
            else if (_pendingClipboardItem != null)
            {
                var item = _pendingClipboardItem;
                _pendingClipboardItem = null;
                await ShowClipboardItemAsync(item);
            }
        }
        catch (Exception ex) when (ex.Message.Contains("WebView2 Runtime"))
        {
            LoadingText.Text = "WebView2 Runtime is not installed.\nDownload from: aka.ms/webview2";
            Log.Warning("WebView2 Runtime missing: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Preview unavailable: {ex.Message}";
            Log.Warning(ex, "WebView2 init failed");
        }
    }

    // ── 对外主接口 ────────────────────────────────────────────────────────
    /// <summary>
    /// 显示指定文件的预览。如果 WebView2 尚未就绪，请求会被缓存直到就绪。
    /// 此方法必须在 UI 线程调用。
    /// </summary>
    public async Task ShowFileAsync(string path)
    {
        if (!_webView2Ready)
        {
            _pendingPath = path;
            return;
        }

        // 重复路径不重新加载（按↑↓时避免不必要的重渲染）
        if (_currentPath == path) return;
        _currentPath = path;
        _currentClipboardItem = null;
        _imageDimensions = null; // 重置尺寸信息
        UpdateHeader(path);

        // 准备预览文档（I/O 密集，await 到后台）
        PreviewDocument doc;
        try
        {
            // 先重新映射虚拟主机到文件所在目录（图片/媒体需要）
            RemapVirtualHost(path);
            doc = await Task.Run(() => PreviewService.PrepareAsync(path, VirtualHostBase).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Preview prepare failed for {Path}", LogPrivacy.Format(path));
            doc = new PreviewDocument(PreviewKind.Unsupported,
                      HtmlTemplateEngine.RenderUnsupported(Path.GetFileName(path), Path.GetExtension(path)),
                      null, Path.GetFileName(path), path);
        }

        // 加载内容（必须回到 UI 线程）
        if (doc.NavigateUri != null)
        {
            WebView.Source = doc.NavigateUri;
        }
        else if (doc.Html != null)
        {
            WebView.NavigateToString(doc.Html);
        }
    }

    public async Task ShowClipboardItemAsync(ClipboardItem item)
    {
        if (!_webView2Ready)
        {
            _pendingClipboardItem = item;
            return;
        }

        var previewKey = "clipboard:" + item.Id;
        if (_currentPath == previewKey) return;
        _currentPath = previewKey;
        _currentClipboardItem = item;
        _imageDimensions = null;
        UpdateClipboardHeader(item);

        try
        {
            switch (item.Type)
            {
                case ClipboardPayloadType.Text:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                        item.PlainText ?? ReadFirstExisting(item.BlobPath) ?? string.Empty,
                        "Clipboard text",
                        isCode: false));
                    break;

                case ClipboardPayloadType.Html:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderClipboardHtml(
                        ReadFirstExisting(item.HtmlBlobPath, item.BlobPath) ??
                        System.Net.WebUtility.HtmlEncode(item.PlainText ?? item.PreviewText),
                        "Clipboard HTML"));
                    break;

                case ClipboardPayloadType.RichText:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                        item.PlainText ?? ReadFirstExisting(item.RtfBlobPath, item.BlobPath) ?? string.Empty,
                        "Clipboard rich text",
                        isCode: false));
                    break;

                case ClipboardPayloadType.Image:
                    if (!string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath))
                    {
                        RemapVirtualHost(item.ImagePath);
                        WebView.NavigateToString(HtmlTemplateEngine.RenderImage(
                            MakeMediaUri(item.ImagePath),
                            Path.GetFileName(item.ImagePath)));
                    }
                    else
                    {
                        WebView.NavigateToString(HtmlTemplateEngine.RenderMissing(item.ImagePath ?? item.PreviewText));
                    }
                    break;

                case ClipboardPayloadType.Files:
                    await ShowClipboardFilesAsync(item);
                    break;

                default:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                        item.PlainText ?? item.PreviewText,
                        "Clipboard item",
                        isCode: false));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clipboard preview failed for {Id}", item.Id);
            WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                item.PreviewText,
                "Clipboard preview unavailable",
                isCode: false));
        }
    }

    // ── 定位窗口 ──────────────────────────────────────────────────────────
    /// <summary>
    /// 相对于主窗口定位预览窗口（右侧优先，左侧次之，不够则覆盖）。
    /// </summary>
    public void PositionRelativeTo(Window owner)
    {
        var screen   = System.Windows.SystemParameters.WorkArea;
        double rightX = owner.Left + owner.ActualWidth + 8;
        double leftX  = owner.Left - ActualWidth - 8;

        if (rightX + ActualWidth <= screen.Right)
        {
            Left = rightX;
        }
        else if (leftX >= screen.Left)
        {
            Left = leftX;
        }
        else
        {
            // 没有足够空间：覆盖在主窗口上，居中对齐
            Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
        }

        // 垂直：与主窗口顶部对齐，但不超出屏幕底部
        Top = Math.Min(owner.Top, screen.Bottom - ActualHeight - 8);
        Top = Math.Max(screen.Top, Top);
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────
    private void RemapVirtualHost(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath)!;
            // 移除旧映射（如果有）
            try { WebView.CoreWebView2.ClearVirtualHostNameToFolderMapping(VirtualHostName); }
            catch { /* 首次无映射时会失败，忽略 */ }

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName, dir,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VirtualHost remap failed");
        }
    }

    private async Task ShowClipboardFilesAsync(ClipboardItem item)
    {
        var existing = item.FilePaths
            .Where(p => File.Exists(p.Path) || Directory.Exists(p.Path))
            .ToList();

        if (existing.Count == 1)
        {
            await ShowFileAsync(existing[0].Path);
            return;
        }

        var entries = item.FilePaths
            .Select(p => (
                p.Path,
                p.IsFolder,
                Exists: File.Exists(p.Path) || Directory.Exists(p.Path)))
            .ToList();

        WebView.NavigateToString(HtmlTemplateEngine.RenderClipboardFiles(entries, item.PreviewText));
    }

    private static string? ReadFirstExisting(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return File.ReadAllText(path);
        }

        return null;
    }

    private static string MakeMediaUri(string path) =>
        VirtualHostBase.TrimEnd('/') + "/" + Uri.EscapeDataString(Path.GetFileName(path));

    private void UpdateHeader(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            FileNameText.Text = fi.Name;
            string meta = fi.Exists ? FormatSize(fi.Length) : "File not found";
            if (!string.IsNullOrEmpty(_imageDimensions))
                meta += $"  ·  {_imageDimensions}";
            if (fi.Exists)
                meta += $"  ·  {fi.LastWriteTime:yyyy-MM-dd HH:mm}";

            FileMetaText.Text = meta;
            FilePathText.Text = path;

            // 图标（按扩展名简单选）
            FileIconText.Text = Path.GetExtension(path).ToLower() switch
            {
                ".pdf"                   => "\uEAA0", // PDF
                ".png" or ".jpg" or
                ".jpeg" or ".gif" or
                ".bmp" or ".webp" or
                ".svg"                   => "\uEB9F", // Photo
                ".mp3" or ".wav" or
                ".m4a" or ".aac" or
                ".flac"                  => "\uE8D6", // Music
                ".mp4" or ".webm" or
                ".mov"                   => "\uE714", // Video
                ".md" or ".markdown"     => "\uE8A5", // File
                _                        => "\uE8A5", // File
            };
        }
        catch { /* 忽略 IO 异常 */ }
    }

    private void UpdateClipboardHeader(ClipboardItem item)
    {
        FileNameText.Text = item.Type switch
        {
            ClipboardPayloadType.Text => "Clipboard text",
            ClipboardPayloadType.Files => "Clipboard files",
            ClipboardPayloadType.Image => "Clipboard image",
            ClipboardPayloadType.Html => "Clipboard HTML",
            ClipboardPayloadType.RichText => "Clipboard rich text",
            _ => "Clipboard item"
        };

        var parts = new List<string> { item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") };
        if (item.TextLength.HasValue)
            parts.Add($"{item.TextLength.Value:N0} chars");
        if (item.ImageWidth.HasValue && item.ImageHeight.HasValue)
            parts.Add($"{item.ImageWidth} × {item.ImageHeight}");
        else if (item.Type == ClipboardPayloadType.Image && !string.IsNullOrWhiteSpace(_imageDimensions))
            parts.Add(_imageDimensions);
        if (item.SizeBytes.HasValue)
            parts.Add(FormatSize(item.SizeBytes.Value));
        if (!string.IsNullOrWhiteSpace(item.SourceAppName))
            parts.Add(item.SourceAppName);

        FileMetaText.Text = string.Join("  ·  ", parts);
        FilePathText.Text = item.Type == ClipboardPayloadType.Files
            ? string.Join(Environment.NewLine, item.FilePaths.Select(p => p.Path))
            : item.ImagePath ?? item.HtmlBlobPath ?? item.RtfBlobPath ?? item.BlobPath ?? item.PreviewText;

        FileIconText.Text = item.Type switch
        {
            ClipboardPayloadType.Text => "\uE8C8",
            ClipboardPayloadType.Files => "\uE8B7",
            ClipboardPayloadType.Image => "\uEB9F",
            ClipboardPayloadType.Html => "\uE736",
            ClipboardPayloadType.RichText => "\uE8D2",
            _ => "\uE946"
        };
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB" :
                                 $"{bytes / 1024.0:F1} KB";

    private void OnPreviewWindowKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Space || e.Key == Key.Escape)
        {
            _commandHost.ClosePreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            _commandHost.SelectNextAndRefreshPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            _commandHost.SelectPreviousAndRefreshPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _commandHost.OpenSelectedItem();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _commandHost.CopySelectedItemPath();
            e.Handled = true;
            return;
        }
    }


    private void MarkWindowGroupInteraction()
    {
        _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = true;

        _interactionCts?.Cancel();
        _interactionCts = new CancellationTokenSource();
        var token = _interactionCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                await Dispatcher.InvokeAsync(() =>
                {
                    _windowGroupFocusService.IsInteractingWithRecentDockWindowGroup = false;
                });
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void ClosePreview()
    {
        Hide();
        _currentPath = null;
        _currentClipboardItem = null;
    }

    private void OpenCurrentFile() => _commandHost.OpenSelectedItem();

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            // 简单解析 JSON (免得引用 System.Text.Json)
            if (json.Contains("\"dimensions\"") && json.Contains("\"value\""))
            {
                var parts = json.Split("\"value\":\"");
                if (parts.Length > 1)
                {
                    var val = parts[1].Split("\"")[0];
                    _imageDimensions = val;
                    if (_currentClipboardItem is not null)
                        UpdateClipboardHeader(_currentClipboardItem);
                    else if (_currentPath != null)
                        UpdateHeader(_currentPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse WebMessage");
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    // 关闭时只隐藏，保留 WebView2 实例（下次 Space 不需要重新初始化）
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        ClosePreview();
    }
}
