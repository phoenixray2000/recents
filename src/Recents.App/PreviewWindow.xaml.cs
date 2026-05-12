using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _mediaTimer;
    private readonly TaskCompletionSource<bool> _webView2ReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _interactionCts;
    private ShellPreviewHost? _shellPreviewHost;
    private bool _mediaIsPlaying;
    private bool _mediaSliderDragging;
    private TimeSpan _mediaDuration = TimeSpan.Zero;
    private bool _hiddenPrewarmActive;
    private double _hiddenPrewarmSavedOpacity = 1.0;
    private bool _isDisposing;
    private bool _allowClose;

    public PreviewWindow(IPreviewCommandHost commandHost, IWindowGroupFocusService windowGroupFocusService)
    {
        InitializeComponent();
        _commandHost = commandHost;
        _windowGroupFocusService = windowGroupFocusService;
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _mediaTimer.Tick += (s, e) => UpdateMediaProgressFromPlayer();

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
        StateChanged += (s, e) => UpdateMaximizeButton();
        ShellPreviewContainer.SizeChanged += (s, e) => UpdateShellPreviewHostSize();
        MediaPlayer.MediaOpened += (s, e) =>
        {
            _mediaDuration = MediaPlayer.NaturalDuration.HasTimeSpan
                ? MediaPlayer.NaturalDuration.TimeSpan
                : TimeSpan.Zero;
            UpdateMediaProgressFromPlayer(force: true);
        };
        MediaPlayer.MediaEnded += (s, e) =>
        {
            _mediaIsPlaying = false;
            _mediaTimer.Stop();
            UpdateMediaPlayPauseButton();
            UpdateMediaProgressFromPlayer(force: true);
        };
        MediaPlayer.MediaFailed += (s, e) =>
        {
            _mediaIsPlaying = false;
            _mediaTimer.Stop();
            UpdateMediaPlayPauseButton();
            MediaPreviewContainer.Visibility = Visibility.Collapsed;
            ClearMediaPreview();
            ShowUnsupportedFallback(
                Path.GetFileName(_currentPath ?? string.Empty),
                Path.GetExtension(_currentPath ?? string.Empty));
        };
        UpdateMaximizeButton();
    }

    // ── WebView2 初始化（预热）──────────────────────────────────────────
    private async void InitWebView2Async()
    {
        try
        {
            Log.Debug("PreviewWindow: WebView2 initialization requested");
            await WebView.EnsureCoreWebView2Async();
            if (_isDisposing)
                return;

            _webView2Ready = true;

            // VirtualHost：把每次预览文件所在目录映射到 https://preview.local/
            // 注意：每次显示不同目录时需要重新映射（在 ShowFileAsync 中处理）

            WebView.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            WebView.PreviewKeyDown += OnPreviewWindowKeyDown;
            _webView2ReadySignal.TrySetResult(true);
            Log.Information("PreviewWindow: WebView2 ready");

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
        catch (Exception ex) when (!_isDisposing && ex.Message.Contains("WebView2 Runtime"))
        {
            LoadingText.Text = "WebView2 Runtime is not installed.\nDownload from: aka.ms/webview2";
            _webView2ReadySignal.TrySetResult(false);
            Log.Warning("WebView2 Runtime missing: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            if (_isDisposing)
                return;

            LoadingText.Text = $"Preview unavailable: {ex.Message}";
            _webView2ReadySignal.TrySetResult(false);
            Log.Warning(ex, "WebView2 init failed");
        }
    }

    public Task PrewarmAsync()
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(PrewarmAsync).Task.Unwrap();

        return PrewarmOnDispatcherAsync();
    }

    private async Task PrewarmOnDispatcherAsync()
    {
        if (_isDisposing || _webView2Ready)
            return;

        var startedHidden = false;
        if (!IsVisible)
        {
            BeginHiddenPrewarm();
            startedHidden = true;
        }

        try
        {
            await WaitForWebView2ReadyAsync(TimeSpan.FromSeconds(12));
        }
        finally
        {
            if (CanHideHiddenPrewarm(
                    startedHidden,
                    _currentPath,
                    _pendingPath,
                    _currentClipboardItem is not null,
                    _pendingClipboardItem is not null))
            {
                EndHiddenPrewarm(hideIfIdle: true);
            }
        }
    }

    private async Task WaitForWebView2ReadyAsync(TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(_webView2ReadySignal.Task, timeoutTask);
        if (completed == timeoutTask)
        {
            Log.Warning("PreviewWindow: WebView2 prewarm timed out after {TimeoutMs}ms", timeout.TotalMilliseconds);
            return;
        }

        var ready = await _webView2ReadySignal.Task;
        Log.Debug("PreviewWindow: WebView2 prewarm completed ready={Ready}", ready);
    }

    private void BeginHiddenPrewarm()
    {
        if (_hiddenPrewarmActive)
            return;

        _hiddenPrewarmActive = true;
        _hiddenPrewarmSavedOpacity = Opacity;
        WindowState = WindowState.Normal;
        Opacity = 0;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = SystemParameters.VirtualScreenLeft - width - 128;
        Top = SystemParameters.VirtualScreenTop - height - 128;

        Log.Debug("PreviewWindow: showing hidden window to prewarm WebView2");
        Show();
    }

    private void EndHiddenPrewarm(bool hideIfIdle)
    {
        if (!_hiddenPrewarmActive)
            return;

        _hiddenPrewarmActive = false;
        Opacity = _hiddenPrewarmSavedOpacity;
        if (hideIfIdle && IsVisible)
            Hide();
    }

    internal static bool CanHideHiddenPrewarm(
        bool startedHidden,
        string? currentPath,
        string? pendingPath,
        bool hasCurrentClipboardItem,
        bool hasPendingClipboardItem) =>
        startedHidden &&
        string.IsNullOrWhiteSpace(currentPath) &&
        string.IsNullOrWhiteSpace(pendingPath) &&
        !hasCurrentClipboardItem &&
        !hasPendingClipboardItem;

    internal static bool RequiresWebView2BeforeRendering(PreviewKind kind) =>
        kind is not (PreviewKind.ShellHandler or PreviewKind.Audio or PreviewKind.Video);

    // ── 对外主接口 ────────────────────────────────────────────────────────
    /// <summary>
    /// 显示指定文件的预览。如果 WebView2 尚未就绪，请求会被缓存直到就绪。
    /// 此方法必须在 UI 线程调用。
    /// </summary>
    public async Task ShowFileAsync(string path)
    {
        if (_isDisposing)
            return;

        var (initialKind, _) = PreviewTypeClassifier.Classify(path);
        if (!_webView2Ready && RequiresWebView2BeforeRendering(initialKind))
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
            // 先重新映射虚拟主机到文件所在目录（WebView2 渲染的图片/HTML 资源需要）
            if (_webView2Ready)
                RemapVirtualHost(path);

            var theme = CurrentHtmlTheme();
            var virtualBase = _webView2Ready ? VirtualHostBase : null;
            doc = await Task.Run(() => PreviewService.PrepareAsync(path, virtualBase, theme).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Preview prepare failed for {Path}", LogPrivacy.Format(path));
            doc = new PreviewDocument(PreviewKind.Unsupported,
                      HtmlTemplateEngine.RenderUnsupported(Path.GetFileName(path), Path.GetExtension(path), CurrentHtmlTheme()),
                      null, Path.GetFileName(path), path);
        }

        try
        {
            // 加载内容（必须回到 UI 线程）
            if (doc.Kind == PreviewKind.ShellHandler)
            {
                ShowShellPreview(path);
            }
            else if (doc.Kind is PreviewKind.Audio or PreviewKind.Video)
            {
                ShowMediaPreview(path, doc.Kind == PreviewKind.Video);
            }
            else if (doc.NavigateUri != null)
            {
                ClearShellPreview();
                ClearMediaPreview();
                ShellPreviewContainer.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
                WebView.Source = doc.NavigateUri;
            }
            else if (doc.Html != null)
            {
                ClearShellPreview();
                ClearMediaPreview();
                ShellPreviewContainer.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
                if (_webView2Ready)
                {
                    WebView.NavigateToString(doc.Html);
                }
                else
                {
                    LoadingText.Text = "Preview is waiting for WebView2 initialization...";
                    LoadingText.Visibility = Visibility.Visible;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Preview load failed for {Path}", LogPrivacy.Format(path));
            ClearShellPreview();
            ClearMediaPreview();
            ShellPreviewContainer.Visibility = Visibility.Collapsed;
            ShowUnsupportedFallback(
                Path.GetFileName(path),
                Path.GetExtension(path));
        }
    }

    public async Task ShowClipboardItemAsync(ClipboardItem item)
    {
        if (_isDisposing)
            return;

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
        ClearShellPreview();
        ClearMediaPreview();
        ShellPreviewContainer.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;

        try
        {
            switch (item.Type)
            {
                case ClipboardPayloadType.Text:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                        item.PlainText ?? ReadFirstExisting(item.BlobPath) ?? string.Empty,
                        "Clipboard text",
                        isCode: false,
                        CurrentHtmlTheme()));
                    break;

                case ClipboardPayloadType.Html:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderClipboardHtml(
                        ReadFirstExisting(item.HtmlBlobPath, item.BlobPath) ??
                        System.Net.WebUtility.HtmlEncode(item.PlainText ?? item.PreviewText),
                        "Clipboard HTML",
                        CurrentHtmlTheme()));
                    break;

                case ClipboardPayloadType.RichText:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                        item.PlainText ?? ReadFirstExisting(item.RtfBlobPath, item.BlobPath) ?? string.Empty,
                        "Clipboard rich text",
                        isCode: false,
                        CurrentHtmlTheme()));
                    break;

                case ClipboardPayloadType.Image:
                    if (!string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath))
                    {
                        RemapVirtualHost(item.ImagePath);
                        WebView.NavigateToString(HtmlTemplateEngine.RenderImage(
                            MakeMediaUri(item.ImagePath),
                            Path.GetFileName(item.ImagePath),
                            CurrentHtmlTheme()));
                    }
                    else
                    {
                        WebView.NavigateToString(HtmlTemplateEngine.RenderMissing(
                            item.ImagePath ?? item.PreviewText,
                            CurrentHtmlTheme()));
                    }
                    break;

                case ClipboardPayloadType.Files:
                    await ShowClipboardFilesAsync(item);
                    break;

                default:
                    WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                        item.PlainText ?? item.PreviewText,
                        "Clipboard item",
                        isCode: false,
                        CurrentHtmlTheme()));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clipboard preview failed for {Id}", item.Id);
            WebView.NavigateToString(HtmlTemplateEngine.RenderText(
                item.PreviewText,
                "Clipboard preview unavailable",
                isCode: false,
                CurrentHtmlTheme()));
        }
    }

    private void ShowShellPreview(string path)
    {
        ClearShellPreview();
        ClearMediaPreview();

        var fileName = Path.GetFileName(path);
        var isVideoFallback = PreviewTypeClassifier.Classify(path).Kind == PreviewKind.Video;
        var clsid = ShellPreviewHandlerResolver.TryResolve(Path.GetExtension(path));
        if (!clsid.HasValue)
        {
            ShellPreviewContainer.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            WebView.NavigateToString(HtmlTemplateEngine.RenderShellHandlerUnavailable(
                fileName,
                "No preview handler is registered for this file type.",
                CurrentHtmlTheme()));
            return;
        }

        WebView.Visibility = Visibility.Collapsed;
        MediaPreviewContainer.Visibility = Visibility.Collapsed;
        ShellPreviewContainer.Visibility = Visibility.Visible;
        LoadingText.Text = "Loading Office preview...";
        LoadingText.Visibility = Visibility.Visible;
        ShellPreviewContainer.UpdateLayout();

        _shellPreviewHost = new ShellPreviewHost(path, clsid.Value)
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
        };
        UpdateShellPreviewHostSize();
        _shellPreviewHost.PreviewFailed += details =>
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_isDisposing)
                            return;

                        ClearShellPreview();
                        ShellPreviewContainer.Visibility = Visibility.Collapsed;
                        LoadingText.Visibility = Visibility.Collapsed;
                        if (isVideoFallback)
                        {
                            ShowMediaPreview(path, isVideo: true);
                        }
                        else
                        {
                            WebView.Visibility = Visibility.Visible;
                            WebView.NavigateToString(HtmlTemplateEngine.RenderShellHandlerUnavailable(
                                fileName,
                                details,
                                CurrentHtmlTheme()));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "PreviewWindow: shell preview failure fallback failed");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PreviewWindow: failed to dispatch shell preview failure");
            }
        };

        ShellPreviewContainer.Children.Add(_shellPreviewHost);
        UpdateShellPreviewHostSize();
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void ShowMediaPreview(string path, bool isVideo)
    {
        ClearShellPreview();
        ClearMediaPreview();

        WebView.Visibility = Visibility.Collapsed;
        ShellPreviewContainer.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Collapsed;
        MediaPreviewContainer.Visibility = Visibility.Visible;

        MediaPlayer.Source = new Uri(path, UriKind.Absolute);
        MediaPlayer.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        MediaAudioPlaceholder.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        MediaAudioNameText.Text = Path.GetFileName(path);
        _mediaDuration = TimeSpan.Zero;
        MediaPositionSlider.Value = 0;
        MediaPositionSlider.Maximum = 1;
        MediaTimeText.Text = "00:00 / 00:00";

        try
        {
            MediaPlayer.Play();
            _mediaIsPlaying = true;
            _mediaTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Media preview failed for {Path}", LogPrivacy.Format(path));
            _mediaIsPlaying = false;
            _mediaTimer.Stop();
            MediaPreviewContainer.Visibility = Visibility.Collapsed;
            ShowUnsupportedFallback(
                Path.GetFileName(path),
                Path.GetExtension(path));
        }

        UpdateMediaPlayPauseButton();
    }

    private void ShowUnsupportedFallback(string fileName, string extension)
    {
        ShellPreviewContainer.Visibility = Visibility.Collapsed;
        MediaPreviewContainer.Visibility = Visibility.Collapsed;

        if (_webView2Ready)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            WebView.NavigateToString(HtmlTemplateEngine.RenderUnsupported(
                fileName,
                extension,
                CurrentHtmlTheme()));
            return;
        }

        WebView.Visibility = Visibility.Collapsed;
        LoadingText.Text = "Preview unavailable.";
        LoadingText.Visibility = Visibility.Visible;
    }

    private void ClearShellPreview()
    {
        if (_shellPreviewHost is null)
            return;

        try
        {
            ShellPreviewContainer.Children.Remove(_shellPreviewHost);
            _shellPreviewHost.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Shell preview cleanup failed");
        }
        finally
        {
            _shellPreviewHost = null;
        }
    }

    private void UpdateShellPreviewHostSize()
    {
        if (_shellPreviewHost is null)
            return;

        var width = ShellPreviewContainer.ActualWidth;
        var height = ShellPreviewContainer.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        _shellPreviewHost.SetViewportSize(width, height);
    }

    private void ClearMediaPreview()
    {
        try
        {
            _mediaTimer.Stop();
            MediaPlayer.Stop();
            MediaPlayer.Source = null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Media preview cleanup failed");
        }

        _mediaIsPlaying = false;
        _mediaDuration = TimeSpan.Zero;
        _mediaSliderDragging = false;
        MediaPositionSlider.Value = 0;
        MediaPositionSlider.Maximum = 1;
        MediaTimeText.Text = "00:00 / 00:00";
        MediaPlayer.Visibility = Visibility.Collapsed;
        MediaAudioPlaceholder.Visibility = Visibility.Collapsed;
        MediaPreviewContainer.Visibility = Visibility.Collapsed;
        UpdateMediaPlayPauseButton();
    }

    private void UpdateMediaPlayPauseButton()
    {
        if (MediaPlayPauseButton is null)
            return;

        MediaPlayPauseButton.Content = _mediaIsPlaying ? "\uE769" : "\uE768";
    }

    private void UpdateMediaProgressFromPlayer(bool force = false)
    {
        if (MediaPlayer.Source is null)
            return;

        var duration = _mediaDuration;
        if (duration <= TimeSpan.Zero && MediaPlayer.NaturalDuration.HasTimeSpan)
        {
            duration = MediaPlayer.NaturalDuration.TimeSpan;
            _mediaDuration = duration;
        }

        if (duration > TimeSpan.Zero)
        {
            MediaPositionSlider.Maximum = duration.TotalSeconds;
            if (!_mediaSliderDragging || force)
                MediaPositionSlider.Value = Math.Min(duration.TotalSeconds, MediaPlayer.Position.TotalSeconds);
        }
        else
        {
            MediaPositionSlider.Maximum = 1;
            if (!_mediaSliderDragging || force)
                MediaPositionSlider.Value = 0;
        }

        MediaTimeText.Text = $"{FormatMediaTime(MediaPlayer.Position)} / {FormatMediaTime(duration)}";
    }

    private static string FormatMediaTime(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return "00:00";

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
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

    public void PositionCenteredOnWorkArea()
    {
        var screen = System.Windows.SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = screen.Left + (screen.Width - width) / 2;
        Top = screen.Top + (screen.Height - height) / 2;
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

        WebView.NavigateToString(HtmlTemplateEngine.RenderClipboardFiles(entries, item.PreviewText, CurrentHtmlTheme()));
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

    private static HtmlPreviewTheme CurrentHtmlTheme() =>
        ThemeManager.Instance.IsDark ? HtmlPreviewTheme.Dark : HtmlPreviewTheme.Light;

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
        try
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
        catch (Exception ex)
        {
            Log.Warning(ex, "PreviewWindow: key handling failed for {Key}", e.Key);
            e.Handled = true;
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
        if (_isDisposing)
            return;

        ClosePreviewContent();
        Hide();
    }

    public void HidePreview()
    {
        if (_isDisposing)
            return;

        ClosePreviewContent();
        Hide();
    }

    private void ClosePreviewContent()
    {
        _currentPath = null;
        _currentClipboardItem = null;
        ClearShellPreview();
        ClearMediaPreview();
    }

    public void DisposeForShutdown()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(DisposeForShutdown);
            return;
        }

        if (_isDisposing)
            return;

        _isDisposing = true;
        _allowClose = true;
        _pendingPath = null;
        _pendingClipboardItem = null;
        _currentPath = null;
        _currentClipboardItem = null;
        ClearShellPreview();
        ClearMediaPreview();

        _interactionCts?.Cancel();
        _interactionCts?.Dispose();
        _interactionCts = null;

        PreviewKeyDown -= OnPreviewWindowKeyDown;

        try
        {
            if (_webView2Ready && WebView.CoreWebView2 is not null)
                WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PreviewWindow: failed to detach WebView2 message handler");
        }

        try
        {
            WebView.PreviewKeyDown -= OnPreviewWindowKeyDown;
            WebView.Visibility = Visibility.Collapsed;
            if (WebView.Parent is System.Windows.Controls.Panel parent)
                parent.Children.Remove(WebView);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PreviewWindow: failed to detach WebView2 visual");
        }

        try
        {
            WebView.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PreviewWindow: WebView2 dispose failed");
        }
        finally
        {
            GC.SuppressFinalize(WebView);
        }

        try
        {
            Close();
        }
        catch (InvalidOperationException ex)
        {
            Log.Debug(ex, "PreviewWindow: close during shutdown ignored");
        }
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

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    public void ShowPreviewWindow()
    {
        if (_isDisposing)
            return;

        EndHiddenPrewarm(hideIfIdle: false);

        var restoreMaximized = NeedsNormalStateBeforeInactiveShow(IsVisible, ShowActivated, WindowState);
        try
        {
            if (restoreMaximized)
                WindowState = WindowState.Normal;

            Show();
            RaiseAboveOwnerWithoutActivation();

            if (restoreMaximized)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (!_isDisposing && IsVisible)
                            WindowState = WindowState.Maximized;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "PreviewWindow: restore maximized state failed");
                    }
                }, DispatcherPriority.Loaded);
            }
        }
        catch (InvalidOperationException ex) when (restoreMaximized)
        {
            Log.Warning(ex, "PreviewWindow: recovered from inactive maximized show");
            WindowState = WindowState.Normal;
            try
            {
                Show();
                RaiseAboveOwnerWithoutActivation();
            }
            catch (Exception showEx)
            {
                Log.Warning(showEx, "PreviewWindow: recovery show failed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PreviewWindow: show failed");
        }
    }

    private void RaiseAboveOwnerWithoutActivation()
    {
        if (!ShouldPulseTopmostAfterInactiveShow(ShowActivated, Topmost))
            return;

        try
        {
            Topmost = true;
            Topmost = false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PreviewWindow: topmost pulse ignored");
        }
    }

    internal static bool NeedsNormalStateBeforeInactiveShow(
        bool isVisible,
        bool showActivated,
        WindowState windowState) =>
        !isVisible && !showActivated && windowState == WindowState.Maximized;

    internal static bool ShouldPulseTopmostAfterInactiveShow(
        bool showActivated,
        bool topmost) =>
        !showActivated && !topmost;

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeBtn is null)
            return;

        MaximizeBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void MediaPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (MediaPlayer.Source is null)
            return;

        if (_mediaIsPlaying)
        {
            MediaPlayer.Pause();
            _mediaIsPlaying = false;
            _mediaTimer.Stop();
        }
        else
        {
            MediaPlayer.Play();
            _mediaIsPlaying = true;
            _mediaTimer.Start();
        }

        UpdateMediaPlayPauseButton();
    }

    private void MediaStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (MediaPlayer.Source is null)
            return;

        MediaPlayer.Stop();
        _mediaIsPlaying = false;
        _mediaTimer.Stop();
        UpdateMediaProgressFromPlayer(force: true);
        UpdateMediaPlayPauseButton();
    }

    private void MediaPositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mediaSliderDragging = true;
    }

    private void MediaPositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (MediaPlayer.Source is null)
        {
            _mediaSliderDragging = false;
            return;
        }

        MediaPlayer.Position = TimeSpan.FromSeconds(Math.Max(0, MediaPositionSlider.Value));
        _mediaSliderDragging = false;
        UpdateMediaProgressFromPlayer(force: true);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ClickCount == 1)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException ex)
            {
                Log.Debug(ex, "PreviewWindow: drag move ignored");
            }
        }
    }

    // 关闭时只隐藏，保留 WebView2 实例（下次 Space 不需要重新初始化）
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        ClosePreview();
    }
}
