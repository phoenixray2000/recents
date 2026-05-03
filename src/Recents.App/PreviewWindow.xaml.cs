using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Recents.App.Services.Preview;
using Serilog;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Recents.App;

public partial class PreviewWindow : Window
{
    // 虚拟主机名称（随机，避免冲突）
    private const string VirtualHostName = "preview.local";
    private const string VirtualHostBase = $"https://{VirtualHostName}/";

    private bool _webView2Ready = false;
    private string? _pendingPath = null;    // 在 WebView2 初始化完成前缓存的路径
    private string? _currentPath = null;
    private string? _imageDimensions = null;

    public PreviewWindow()
    {
        InitializeComponent();
        InitWebView2Async();

        // 键盘：Esc 关闭，Enter 打开文件
        PreviewKeyDown += OnPreviewKeyDown;
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

            // 处理预热前积压的请求
            if (_pendingPath != null)
            {
                var path = _pendingPath;
                _pendingPath = null;
                await ShowFileAsync(path);
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
            Log.Warning(ex, "Preview prepare failed for {Path}", path);
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

    private static string FormatSize(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB" :
                                 $"{bytes / 1024.0:F1} KB";

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClosePreview(); e.Handled = true; }
        if (e.Key == Key.Enter)  { OpenCurrentFile(); e.Handled = true; }
    }

    private void ClosePreview()
    {
        Hide();
        _currentPath = null;
    }

    private void OpenCurrentFile()
    {
        if (_currentPath != null)
            Services.FileActionService.OpenFile(_currentPath);
    }

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
                    if (_currentPath != null) UpdateHeader(_currentPath);
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
