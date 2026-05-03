# Space-bar Quick Preview — 详细实施计划

> 对应 PRD §6.25。目标：按空格键 <200 ms 显示预览，无焦点抢占，单一持久窗口。  
> 本文档是可直接照做的编码手册，每步给出完整代码或精确的 diff 位置。

---

## 目录

1. [NuGet 依赖](#step-1-nuget-依赖)
2. [PreviewKind 枚举 + PreviewTypeClassifier](#step-2-previewkind--previewtypeclassifier)
3. [HtmlTemplateEngine（各类型渲染器）](#step-3-htmltemplateengine)
4. [PreviewService（异步内容准备）](#step-4-previewservice)
5. [PreviewWindow.xaml + PreviewWindow.xaml.cs](#step-5-previewwindow)
6. [MainWindow 集成（Space / Esc / ↑↓）](#step-6-mainwindow-集成)
7. [AppSettings + PreviewEnabled 开关](#step-7-appsettings--设置页开关)
8. [App.xaml.cs 预热](#step-8-appxamlcs-预热)
9. [验证清单](#step-9-验证清单)

---

## Step 1: NuGet 依赖

**文件**: `src/Recents.App/Recents.App.csproj`

在 `<ItemGroup>` 中追加两行：

```xml
<!-- §6.25 空格预览：WebView2 宿主控件 -->
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.2903.40" />
<!-- §6.25 Markdown → HTML 转换 -->
<PackageReference Include="Markdig" Version="0.40.0" />
```

> **注意**：WebView2 Runtime 在 Win10 21H1+ / Win11 上随 Edge 预装，无需额外部署。
> 如果用户机器没有运行时，`EnsureCoreWebView2Async()` 会抛出 `WebView2RuntimeNotFoundException`，
> 在 Step 4 / Step 5 中统一处理降级。

---

## Step 2: PreviewKind + PreviewTypeClassifier

**新建文件**: `src/Recents.App/Services/Preview/PreviewTypeClassifier.cs`

```csharp
namespace Recents.App.Services.Preview;

/// <summary>预览内容类型枚举</summary>
public enum PreviewKind
{
    Unsupported,   // 不支持预览（Office、压缩包、PSD…）
    TooLarge,      // 文件超过大小限制
    MissingFile,   // 文件不存在
    Pdf,
    Image,         // png/jpg/jpeg/gif/bmp/webp/svg
    Text,          // txt/log/ini
    Csv,
    Code,          // cs/ts/js/py/json/xml/html/css/sql/yaml…
    Markdown,
    Audio,         // mp3/wav/m4a/aac/flac
    Video,         // mp4/webm/mov
}

public static class PreviewTypeClassifier
{
    // 大小限制（字节）
    private const long TextLimit     = 5L  * 1024 * 1024;  //   5 MB
    private const long ImageLimit    = 100L * 1024 * 1024; // 100 MB
    private const long PdfLimit      = 500L * 1024 * 1024; // 500 MB
    // 音视频不限（流式打开）

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg" };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".ini" };

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".js", ".jsx", ".tsx", ".py", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".kt",
        ".json", ".xml", ".html", ".htm", ".css", ".scss", ".less",
        ".sql", ".yaml", ".yml", ".toml", ".sh", ".bat", ".ps1",
        ".config", ".props", ".targets",
    };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg" };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mov" };

    /// <summary>
    /// 对给定文件路径进行分类，返回 (Kind, 文件长度)。
    /// 不抛异常；IO 错误 → MissingFile。
    /// </summary>
    public static (PreviewKind Kind, long FileSize) Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (PreviewKind.MissingFile, 0);

        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists) return (PreviewKind.MissingFile, 0);
        }
        catch
        {
            return (PreviewKind.MissingFile, 0);
        }

        var ext = fi.Extension.ToLowerInvariant();
        var size = fi.Length;

        if (ext == ".pdf")
            return size > PdfLimit
                ? (PreviewKind.TooLarge, size)
                : (PreviewKind.Pdf, size);

        if (ImageExts.Contains(ext))
            return size > ImageLimit
                ? (PreviewKind.TooLarge, size)
                : (PreviewKind.Image, size);

        if (AudioExts.Contains(ext))
            return (PreviewKind.Audio, size);   // 无大小限制

        if (VideoExts.Contains(ext))
            return (PreviewKind.Video, size);   // 无大小限制

        if (ext == ".csv")
            return size > TextLimit
                ? (PreviewKind.TooLarge, size)
                : (PreviewKind.Csv, size);

        if (TextExts.Contains(ext))
            return size > TextLimit
                ? (PreviewKind.TooLarge, size)
                : (PreviewKind.Text, size);

        if (ext is ".md" or ".markdown")
            return size > TextLimit
                ? (PreviewKind.TooLarge, size)
                : (PreviewKind.Markdown, size);

        if (CodeExts.Contains(ext))
            return size > TextLimit
                ? (PreviewKind.TooLarge, size)
                : (PreviewKind.Code, size);

        return (PreviewKind.Unsupported, size);
    }
}
```

---

## Step 3: HtmlTemplateEngine

**新建文件**: `src/Recents.App/Services/Preview/HtmlTemplateEngine.cs`

这个类负责将文件内容转换成可传给 `NavigateToString` 的完整 HTML 字符串。  
PDF / 音视频 / 图片直接用 `file://` URI，文本/代码/Markdown 用 `NavigateToString`。

```csharp
using Markdig;
using System.IO;
using System.Net;
using System.Text;

namespace Recents.App.Services.Preview;

public static class HtmlTemplateEngine
{
    // ── 公共 CSS 变量（与 PRD §7.4 颜色 token 对齐）──────────────────────
    private const string BaseStyle = """
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
        :root {
            --bg:        #1e1e2e;
            --surface:   #252535;
            --border:    #3a3a5c;
            --text:      #cdd6f4;
            --muted:     #6c7086;
            --accent:    #89b4fa;
            --code-bg:   #181825;
            --success:   #a6e3a1;
            --warning:   #f9e2af;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        html, body { width: 100%; height: 100%; overflow: auto; }
        body {
            background: var(--bg);
            color: var(--text);
            font-family: "Segoe UI", system-ui, sans-serif;
            font-size: 14px;
            line-height: 1.6;
        }
        a { color: var(--accent); }
        </style>
        """;

    // ── 图片 ──────────────────────────────────────────────────────────────
    /// <param name="virtualUri">虚拟主机 URI，格式 https://preview.local/… 或 file://…</param>
    public static string RenderImage(string virtualUri, string fileName)
    {
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body { display:flex; flex-direction:column; align-items:center;
                   justify-content:center; min-height:100vh; padding:16px; }
            .img-wrap { max-width:100%; overflow:auto; cursor:grab; }
            img { max-width:100%; max-height:80vh; object-fit:contain;
                  display:block; border-radius:4px; }
            .meta { margin-top:10px; color:var(--muted); font-size:12px; }
            </style>
            </head><body>
            <div class="img-wrap">
              <img src="{{virtualUri}}" alt="{{WebUtility.HtmlEncode(fileName)}}"
                   onerror="this.parentNode.innerHTML='<p style=color:var(--warning)>Image failed to load.</p>'">
            </div>
            <div class="meta" id="meta">{{WebUtility.HtmlEncode(fileName)}}</div>
            <script>
            const img = document.querySelector('img');
            const meta = document.getElementById('meta');
            img.onload = () => {
              meta.textContent = '{{WebUtility.HtmlEncode(fileName)}} — ' + img.naturalWidth + ' × ' + img.naturalHeight;
            };
            // 简单缩放（Ctrl+滚轮）
            let scale = 1;
            document.addEventListener('wheel', e => {
              if (!e.ctrlKey) return;
              e.preventDefault();
              scale = Math.max(0.1, Math.min(10, scale + (e.deltaY < 0 ? 0.1 : -0.1)));
              img.style.transform = 'scale(' + scale + ')';
              img.style.transformOrigin = 'top center';
            }, { passive: false });
            </script>
            </body></html>
            """;
    }

    // ── 文本 / 代码（无语法高亮，P0 阶段）──────────────────────────────
    public static string RenderText(string content, string fileName, bool isCode)
    {
        var escaped = WebUtility.HtmlEncode(content);
        var lang = isCode ? Path.GetExtension(fileName).TrimStart('.') : "";
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body { padding: 0; }
            .toolbar {
                position: sticky; top: 0; background: var(--surface);
                border-bottom: 1px solid var(--border);
                padding: 6px 12px; display:flex; gap:8px; align-items:center;
                font-size: 12px; color: var(--muted); z-index:10;
            }
            .toolbar span { flex:1; }
            button {
                background: transparent; border: 1px solid var(--border);
                color: var(--text); border-radius:4px; padding:2px 8px;
                cursor:pointer; font-size:11px;
            }
            button:hover { background: var(--border); }
            pre {
                margin: 0; padding: 12px 16px;
                font-family: "Cascadia Code","Consolas","Fira Mono",monospace;
                font-size: 13px; line-height: 1.5; white-space: pre-wrap;
                word-break: break-word; background: var(--code-bg); color: var(--text);
                min-height: calc(100vh - 36px);
            }
            </style>
            </head><body>
            <div class="toolbar">
              <span>{{WebUtility.HtmlEncode(fileName)}}{{(string.IsNullOrEmpty(lang) ? "" : $" · {lang}")}}</span>
              <button onclick="navigator.clipboard.writeText(document.querySelector('pre').innerText)">Copy</button>
            </div>
            <pre>{{escaped}}</pre>
            </body></html>
            """;
    }

    // ── CSV（表格视图，最多 500 行）─────────────────────────────────────
    public static string RenderCsv(string content, string fileName)
    {
        var rows = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Take(500).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head>" + BaseStyle + """
            <style>
            body { padding: 0; overflow-x: auto; }
            .toolbar { position:sticky;top:0;background:var(--surface);
                border-bottom:1px solid var(--border);padding:6px 12px;
                font-size:12px;color:var(--muted);z-index:10; }
            table { border-collapse:collapse; width:100%; font-size:13px; }
            th { background:var(--surface);border:1px solid var(--border);
                 padding:4px 8px;text-align:left;font-weight:600;color:var(--accent);
                 position:sticky;top:36px; }
            td { border:1px solid var(--border);padding:4px 8px; }
            tr:nth-child(even) { background:var(--surface); }
            </style></head><body>
            """);
        sb.AppendLine($"<div class='toolbar'>{WebUtility.HtmlEncode(fileName)} · {rows.Count} rows shown (max 500)</div>");
        sb.AppendLine("<table>");
        bool isHeader = true;
        foreach (var raw in rows)
        {
            var cells = ParseCsvLine(raw);
            sb.Append(isHeader ? "<thead><tr>" : "<tr>");
            foreach (var cell in cells)
            {
                var tag = isHeader ? "th" : "td";
                sb.Append($"<{tag}>{WebUtility.HtmlEncode(cell)}</{tag}>");
            }
            sb.AppendLine(isHeader ? "</tr></thead><tbody>" : "</tr>");
            isHeader = false;
        }
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string[] ParseCsvLine(string line)
    {
        // 极简 CSV 解析：处理带引号的字段
        var result = new List<string>();
        bool inQuote = false;
        var current = new StringBuilder();
        foreach (char ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; }
            else if (ch == ',' && !inQuote) { result.Add(current.ToString()); current.Clear(); }
            else { current.Append(ch); }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    // ── Markdown ─────────────────────────────────────────────────────────
    public static string RenderMarkdown(string content, string fileName)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()   // 表格、删除线、任务列表等
            .Build();
        var body = Markdown.ToHtml(content, pipeline);
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            .md { max-width: 820px; margin: 0 auto; padding: 24px 32px; }
            h1,h2,h3,h4 { color:var(--accent); margin:1em 0 .4em; }
            h1 { font-size:1.6em; border-bottom:1px solid var(--border); padding-bottom:.3em; }
            h2 { font-size:1.3em; }
            p  { margin: .5em 0; }
            blockquote { border-left:3px solid var(--accent);
                         margin-left:0;padding-left:12px;color:var(--muted); }
            pre, code { font-family:"Cascadia Code","Consolas",monospace; }
            pre  { background:var(--code-bg);border-radius:6px;padding:12px;overflow-x:auto; }
            code { background:var(--code-bg);border-radius:3px;padding:2px 4px;font-size:.9em; }
            pre code { background:none;padding:0; }
            table { border-collapse:collapse;width:100%; }
            th,td { border:1px solid var(--border);padding:6px 10px;text-align:left; }
            th { background:var(--surface);color:var(--accent); }
            tr:nth-child(even) { background:var(--surface); }
            input[type=checkbox] { margin-right:4px; }
            hr { border:none;border-top:1px solid var(--border);margin:1.5em 0; }
            img { max-width:100%; }
            </style>
            </head><body>
            <div class="md">{{body}}</div>
            </body></html>
            """;
    }

    // ── 媒体（音频 / 视频）─────────────────────────────────────────────
    public static string RenderMedia(string virtualUri, string fileName, bool isVideo)
    {
        var tag = isVideo ? "video" : "audio";
        var attrs = isVideo
            ? """controls autoplay style="max-width:100%;max-height:80vh;border-radius:6px;""""
            : """controls autoplay style="width:100%;""""";
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body { display:flex;flex-direction:column;align-items:center;
                   justify-content:center;min-height:100vh;gap:12px; }
            .name { color:var(--muted);font-size:13px; }
            </style>
            </head><body>
            <{{tag}} {{attrs}} src="{{virtualUri}}"
              onerror="this.outerHTML='<p style=color:var(--warning)>Cannot play this file format.</p>'">
            </{{tag}}>
            <div class="name">{{WebUtility.HtmlEncode(fileName)}}</div>
            </body></html>
            """;
    }

    // ── 不支持 ───────────────────────────────────────────────────────────
    public static string RenderUnsupported(string fileName, string ext) => $$"""
        <!DOCTYPE html><html><head>{{BaseStyle}}
        <style>body{display:flex;align-items:center;justify-content:center;
        min-height:100vh;flex-direction:column;gap:8px;}
        .icon{font-family:"Segoe Fluent Icons";font-size:48px;color:var(--muted);}
        .msg{color:var(--muted);font-size:14px;}
        .hint{color:var(--muted);font-size:12px;margin-top:4px;}
        </style></head><body>
        <div class="icon">&#xE8A5;</div>
        <div class="msg">This file type ({{WebUtility.HtmlEncode(ext)}}) cannot be previewed.</div>
        <div class="hint">Press <b>Enter</b> to open with the default app.</div>
        </body></html>
        """;

    public static string RenderTooLarge(string fileName, long size) => $$"""
        <!DOCTYPE html><html><head>{{BaseStyle}}
        <style>body{display:flex;align-items:center;justify-content:center;
        min-height:100vh;flex-direction:column;gap:8px;}
        .icon{font-family:"Segoe Fluent Icons";font-size:48px;color:var(--warning);}
        .msg{color:var(--muted);font-size:14px;}
        </style></head><body>
        <div class="icon">&#xE898;</div>
        <div class="msg">File is too large to preview ({{FormatSize(size)}}).</div>
        <div class="hint" style="color:var(--muted);font-size:12px;">Press <b>Enter</b> to open.</div>
        </body></html>
        """;

    public static string RenderMissing(string path) => $$"""
        <!DOCTYPE html><html><head>{{BaseStyle}}
        <style>body{display:flex;align-items:center;justify-content:center;
        min-height:100vh;flex-direction:column;gap:8px;}
        .icon{font-family:"Segoe Fluent Icons";font-size:48px;color:var(--warning);}
        .msg{color:var(--muted);font-size:14px;}
        </style></head><body>
        <div class="icon">&#xE7BA;</div>
        <div class="msg">File not found.</div>
        </body></html>
        """;

    private static string FormatSize(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB" :
                                 $"{bytes / 1024.0:F1} KB";
}
```

---

## Step 4: PreviewService

**新建文件**: `src/Recents.App/Services/Preview/PreviewService.cs`

PreviewService 是唯一对外接口，负责分类 → 读文件 → 交给 Engine。  
调用方只需 `await PreviewService.PrepareAsync(path)` 拿到 `PreviewDocument`。

```csharp
using System.IO;
using System.Text;

namespace Recents.App.Services.Preview;

/// <summary>
/// 描述一次预览请求的结果。
/// NavigateUri 非 null  → 用 webView.Source 加载（PDF / 已映射的图片音视频）
/// Html       非 null  → 用 webView.NavigateToString(Html) 加载
/// </summary>
public record PreviewDocument(
    PreviewKind Kind,
    string?     Html,
    Uri?        NavigateUri,
    string      Title,
    string      FilePath
);

public static class PreviewService
{
    /// <summary>
    /// 异步准备预览内容。不抛异常；错误情况以 Kind=Unsupported/MissingFile 返回。
    /// </summary>
    /// <param name="path">文件绝对路径</param>
    /// <param name="virtualBase">
    /// WebView2 虚拟主机根 URI，格式 "https://preview.local/"。
    /// 仅对图片 / 音视频有效（用于构建 src URL）。
    /// 若为 null，则退回到 file:// URI（受浏览器安全策略限制）。
    /// </param>
    public static async Task<PreviewDocument> PrepareAsync(
        string path,
        string? virtualBase = null)
    {
        var fileName = Path.GetFileName(path);
        var ext      = Path.GetExtension(path).ToLowerInvariant();
        var title    = fileName;

        var (kind, size) = PreviewTypeClassifier.Classify(path);

        return kind switch
        {
            PreviewKind.MissingFile  => new(kind, HtmlTemplateEngine.RenderMissing(path), null, title, path),
            PreviewKind.TooLarge     => new(kind, HtmlTemplateEngine.RenderTooLarge(fileName, size), null, title, path),
            PreviewKind.Unsupported  => new(kind, HtmlTemplateEngine.RenderUnsupported(fileName, ext), null, title, path),

            PreviewKind.Pdf          => new(kind, null, MakeFileUri(path), title, path),

            PreviewKind.Image        => new(kind,
                                           HtmlTemplateEngine.RenderImage(MakeMediaUri(path, virtualBase), fileName),
                                           null, title, path),

            PreviewKind.Audio        => new(kind,
                                           HtmlTemplateEngine.RenderMedia(MakeMediaUri(path, virtualBase), fileName, false),
                                           null, title, path),

            PreviewKind.Video        => new(kind,
                                           HtmlTemplateEngine.RenderMedia(MakeMediaUri(path, virtualBase), fileName, true),
                                           null, title, path),

            PreviewKind.Text or
            PreviewKind.Code         => new(kind,
                                           HtmlTemplateEngine.RenderText(await ReadTextSafeAsync(path), fileName, kind == PreviewKind.Code),
                                           null, title, path),

            PreviewKind.Csv          => new(kind,
                                           HtmlTemplateEngine.RenderCsv(await ReadTextSafeAsync(path), fileName),
                                           null, title, path),

            PreviewKind.Markdown     => new(kind,
                                           HtmlTemplateEngine.RenderMarkdown(await ReadTextSafeAsync(path), fileName),
                                           null, title, path),

            _                        => new(PreviewKind.Unsupported,
                                           HtmlTemplateEngine.RenderUnsupported(fileName, ext),
                                           null, title, path),
        };
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private static Uri MakeFileUri(string path) =>
        new Uri(path, UriKind.Absolute);

    /// <summary>
    /// 媒体资源 URI。优先用虚拟主机（/dir/file 映射），退回 file://
    /// </summary>
    private static string MakeMediaUri(string path, string? virtualBase)
    {
        if (!string.IsNullOrEmpty(virtualBase))
        {
            // 取文件名（VirtualHost 只映射到目录，所以 URI = base + filename）
            var fileName = Path.GetFileName(path);
            return virtualBase.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);
        }
        // 退回 file://（对图片大多 OK；音视频跨域可能有问题）
        return new Uri(path, UriKind.Absolute).AbsoluteUri;
    }

    private static async Task<string> ReadTextSafeAsync(string path)
    {
        try
        {
            // 先 UTF-8，失败再 GBK（中文文件常见编码）
            try
            {
                return await File.ReadAllTextAsync(path, new UTF8Encoding(false, true));
            }
            catch (DecoderFallbackException)
            {
                var gbk = Encoding.GetEncoding(936); // GBK
                return await File.ReadAllTextAsync(path, gbk);
            }
        }
        catch (Exception ex)
        {
            return $"// Failed to read file: {ex.Message}";
        }
    }
}
```

---

## Step 5: PreviewWindow

### 5-A: PreviewWindow.xaml

**新建文件**: `src/Recents.App/PreviewWindow.xaml`

```xml
<Window x:Class="Recents.App.PreviewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
        Title="Preview"
        Width="860" Height="640"
        MinWidth="400" MinHeight="300"
        WindowStyle="None"
        AllowsTransparency="True"
        ShowInTaskbar="False"
        ShowActivated="False"
        Background="Transparent"
        ResizeMode="CanResizeWithGrip">

    <!-- 外层阴影容器 -->
    <Border Margin="8"
            Background="#1e1e2e"
            CornerRadius="10"
            BorderBrush="#3a3a5c"
            BorderThickness="1">
        <Border.Effect>
            <DropShadowEffect BlurRadius="20" ShadowDepth="0" Opacity="0.6" Color="#000000"/>
        </Border.Effect>

        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="40"/>   <!-- 标题栏 -->
                <RowDefinition Height="*"/>    <!-- WebView2 内容区 -->
                <RowDefinition Height="28"/>   <!-- 底部状态栏 -->
            </Grid.RowDefinitions>

            <!-- ── 标题栏（可拖动）──────────────────────────── -->
            <Border Grid.Row="0"
                    Background="#252535"
                    CornerRadius="10,10,0,0"
                    MouseLeftButtonDown="TitleBar_MouseLeftButtonDown">
                <Grid Margin="12,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>

                    <!-- 文件图标（Fluent Icon 占位）-->
                    <TextBlock Grid.Column="0"
                               x:Name="FileIconText"
                               FontFamily="Segoe Fluent Icons"
                               FontSize="16"
                               Text="&#xE8A5;"
                               Foreground="#89b4fa"
                               VerticalAlignment="Center"
                               Margin="0,0,8,0"/>

                    <!-- 文件名 + 元信息 -->
                    <StackPanel Grid.Column="1" VerticalAlignment="Center">
                        <TextBlock x:Name="FileNameText"
                                   Text="—"
                                   Foreground="#cdd6f4"
                                   FontSize="13"
                                   FontWeight="SemiBold"
                                   TextTrimming="CharacterEllipsis"/>
                        <TextBlock x:Name="FileMetaText"
                                   Text=""
                                   Foreground="#6c7086"
                                   FontSize="11"/>
                    </StackPanel>

                    <!-- 关闭按钮 -->
                    <Button Grid.Column="2"
                            x:Name="CloseBtn"
                            Content="&#xE8BB;"
                            FontFamily="Segoe Fluent Icons"
                            FontSize="14"
                            Foreground="#6c7086"
                            Background="Transparent"
                            BorderThickness="0"
                            Padding="6,4"
                            Cursor="Hand"
                            Click="CloseBtn_Click">
                        <Button.Style>
                            <Style TargetType="Button">
                                <Setter Property="Template">
                                    <Setter.Value>
                                        <ControlTemplate TargetType="Button">
                                            <Border x:Name="bd" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
                                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="bd" Property="Background" Value="#c0392b"/>
                                                    <Setter Property="Foreground" Value="White"/>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </Setter.Value>
                                </Setter>
                            </Style>
                        </Button.Style>
                    </Button>
                </Grid>
            </Border>

            <!-- ── WebView2 内容区 ────────────────────────────── -->
            <Grid Grid.Row="1">
                <!-- 加载中指示器（WebView2 初始化时可见）-->
                <TextBlock x:Name="LoadingText"
                           Text="Loading…"
                           Foreground="#6c7086"
                           FontSize="14"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center"
                           Visibility="Visible"/>

                <wv2:WebView2 x:Name="WebView"
                              Visibility="Collapsed"
                              DefaultBackgroundColor="Transparent"/>
            </Grid>

            <!-- ── 底部状态栏 ──────────────────────────────────── -->
            <Border Grid.Row="2"
                    Background="#252535"
                    CornerRadius="0,0,10,10"
                    BorderBrush="#3a3a5c"
                    BorderThickness="0,1,0,0">
                <Grid Margin="12,0">
                    <TextBlock x:Name="FilePathText"
                               Foreground="#6c7086"
                               FontSize="11"
                               VerticalAlignment="Center"
                               TextTrimming="CharacterEllipsis"
                               HorizontalAlignment="Left"/>
                    <TextBlock Text="Enter to Open · Esc to Close"
                               Foreground="#4a4a6a"
                               FontSize="11"
                               VerticalAlignment="Center"
                               HorizontalAlignment="Right"/>
                </Grid>
            </Border>
        </Grid>
    </Border>
</Window>
```

### 5-B: PreviewWindow.xaml.cs

**新建文件**: `src/Recents.App/PreviewWindow.xaml.cs`

```csharp
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Recents.App.Services.Preview;
using Serilog;

namespace Recents.App;

public partial class PreviewWindow : Window
{
    // 虚拟主机名称（随机，避免冲突）
    private const string VirtualHostName = "preview.local";
    private const string VirtualHostBase = $"https://{VirtualHostName}/";

    private bool _webView2Ready = false;
    private string? _pendingPath = null;    // 在 WebView2 初始化完成前缓存的路径
    private string? _currentPath = null;

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
            FileMetaText.Text = fi.Exists
                ? $"{FormatSize(fi.Length)}  ·  {fi.LastWriteTime:yyyy-MM-dd HH:mm}"
                : "File not found";
            FilePathText.Text = path;

            // 图标（按扩展名简单选）
            FileIconText.Text = Path.GetExtension(path).ToLower() switch
            {
                ".pdf"                   => "",
                ".png" or ".jpg" or
                ".jpeg" or ".gif" or
                ".bmp" or ".webp" or
                ".svg"                   => "",
                ".mp3" or ".wav" or
                ".m4a" or ".aac" or
                ".flac"                  => "",
                ".mp4" or ".webm" or
                ".mov"                   => "",
                ".md" or ".markdown"     => "",
                _                        => "",
            };
        }
        catch { /* 忽略 IO 异常 */ }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB" :
                                 $"{bytes / 1024.0:F1} KB";

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
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
```

---

## Step 6: MainWindow 集成

**修改文件**: `src/Recents.App/MainWindow.xaml.cs`

### 6-A: 字段声明（加在现有字段附近）

```csharp
// §6.25 预览窗口（单一持久实例）
private PreviewWindow? _previewWindow;
private System.Threading.CancellationTokenSource? _previewNavCts;
```

### 6-B: 构造函数末尾追加预热调用

```csharp
// §6.25 预热 PreviewWindow（提前初始化 WebView2）
if (_settings.Current.PreviewEnabled)
    EnsurePreviewWindow();
```

### 6-C: 追加 EnsurePreviewWindow 私有方法

```csharp
private PreviewWindow EnsurePreviewWindow()
{
    if (_previewWindow == null)
    {
        _previewWindow = new PreviewWindow();
        // 不设 Owner，避免关闭主窗口时连带销毁
    }
    return _previewWindow;
}
```

### 6-D: 修改 Window_PreviewKeyDown

在方法中 `if (e.Key == Key.Escape)` 块**前**插入预览关闭逻辑，
在 `if (e.Key == Key.Enter)` 块**前**插入预览打开逻辑：

```csharp
private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
{
    // §6.25: Esc 优先关闭预览（若预览可见），否则隐藏主窗口
    if (e.Key == Key.Escape)
    {
        if (_previewWindow?.IsVisible == true)
        {
            _previewWindow.Hide();
            e.Handled = true;
            return;
        }
        HideWindow();
        e.Handled = true;
        return;
    }

    // §6.25: Space 切换预览
    if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
    {
        TogglePreview();
        e.Handled = true;
        return;
    }

    var modifiers = Keyboard.Modifiers;
    if ((modifiers & ModifierKeys.Control) != 0)
    {
        // … 现有 Ctrl 快捷键不变 …
    }

    if (e.Key == Key.Enter)
    {
        // 若预览可见，Enter 打开文件并关闭预览
        if (_previewWindow?.IsVisible == true)
            _previewWindow.Hide();
        TryOpenSelectedItem();
        e.Handled = true;
        return;
    }
}
```

### 6-E: TogglePreview 方法

```csharp
private void TogglePreview()
{
    if (!_settings.Current.PreviewEnabled) return;
    if (ItemsList.SelectedItem is not RecentItemViewModel vm) return;
    if (vm.Item.IsFolder) return;   // §6.25.1: 文件夹不预览

    var pw = EnsurePreviewWindow();

    if (pw.IsVisible && pw.Tag as string == vm.DisplayPath)
    {
        // 二次 Space → 关闭
        pw.Hide();
    }
    else
    {
        pw.Tag = vm.DisplayPath;
        pw.PositionRelativeTo(this);
        pw.Show();
        _ = pw.ShowFileAsync(vm.DisplayPath);
    }
}
```

### 6-F: 选中项变化时刷新预览（含防抖）

在构造函数中追加：

```csharp
// §6.25: 列表选中变化时，如果预览窗口已打开，延迟 100ms 刷新
ItemsList.SelectionChanged += (s, e) =>
{
    if (_previewWindow?.IsVisible != true) return;
    if (ItemsList.SelectedItem is not RecentItemViewModel vm) return;
    if (vm.Item.IsFolder) return;

    // 取消上一次待发的刷新
    _previewNavCts?.Cancel();
    _previewNavCts = new System.Threading.CancellationTokenSource();
    var token = _previewNavCts.Token;

    _ = Task.Delay(100, token).ContinueWith(t =>
    {
        if (t.IsCanceled) return;
        Dispatcher.Invoke(() =>
        {
            if (_previewWindow?.IsVisible == true)
            {
                _previewWindow.Tag = vm.DisplayPath;
                _ = _previewWindow.ShowFileAsync(vm.DisplayPath);
            }
        });
    }, TaskScheduler.Default);
};
```

---

## Step 7: AppSettings + 设置页开关

### 7-A: AppSettings.cs

在 `// General` 块中追加一行：

```csharp
// §6.25 空格预览
public bool PreviewEnabled { get; set; } = true;
```

### 7-B: SettingsViewModel.cs

在现有 `[ObservableProperty]` 字段列表末尾追加：

```csharp
[ObservableProperty] private bool _previewEnabled;
```

在构造函数末尾追加：

```csharp
_previewEnabled = settings.Current.PreviewEnabled;
```

追加 partial 回调：

```csharp
partial void OnPreviewEnabledChanged(bool value)
{
    _settings.Current.PreviewEnabled = value;
    SaveAndNotify();
}
```

### 7-C: SettingsWindow.xaml

在 **General** 设置分组中、`ShowFolders` 开关下方，追加一个 `ToggleSwitch`：

```xml
<!-- §6.25 空格预览开关 -->
<Grid Margin="0,8,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <StackPanel>
        <TextBlock Text="Quick preview (Space)"
                   Foreground="#cdd6f4" FontSize="13"/>
        <TextBlock Text="Press Space to preview files with WebView2"
                   Foreground="#6c7086" FontSize="11"/>
    </StackPanel>
    <CheckBox Grid.Column="1"
              IsChecked="{Binding PreviewEnabled}"
              VerticalAlignment="Center"/>
</Grid>
```

> 若设置页使用自定义 `ToggleSwitch` 样式，参照同页其他开关的写法替换 `CheckBox`。

---

## Step 8: App.xaml.cs 预热

**修改文件**: `src/Recents.App/App.xaml.cs`（或 `Program.cs` / 入口处）

在主窗口显示**之后**（`mainWindow.Show()` 或 `mainWindow.ShowAndFocus()` 之后），
追加预热调用，让 WebView2 在用户第一次按 Space 之前完成初始化：

```csharp
// §6.25 预热 PreviewWindow（后台异步，不阻塞启动）
if (settings.Current.PreviewEnabled)
{
    _ = Task.Run(() =>
    {
        mainWindow.Dispatcher.BeginInvoke(() => mainWindow.PrewarmPreview());
    });
}
```

在 `MainWindow.xaml.cs` 中添加对应的公共预热方法：

```csharp
/// <summary>由 App.xaml.cs 在启动完成后调用，提前初始化 WebView2。</summary>
public void PrewarmPreview()
{
    if (_settings.Current.PreviewEnabled)
        _ = EnsurePreviewWindow(); // 触发 PreviewWindow 构造 + InitWebView2Async
}
```

---

## Step 9: 验证清单

按以下顺序手动验证，全部通过后功能完整。

| # | 场景 | 期望结果 |
|---|------|----------|
| T1 | 选中 `.png` 文件，按 Space | 预览窗口在主窗右侧出现，显示图片，<200ms |
| T2 | 预览打开时再按 Space | 预览窗口关闭 |
| T3 | 预览打开时按 Esc | 预览窗口关闭，主窗口保持 |
| T4 | 无预览时按 Esc | 主窗口隐藏到托盘 |
| T5 | 预览打开时按 Enter | 文件用默认应用打开，预览关闭 |
| T6 | 预览打开时按 ↑ / ↓ | 100ms 后预览自动刷新为新选中文件 |
| T7 | 选中文件夹，按 Space | 无响应（不显示预览） |
| T8 | 选中 `.docx` 文件，按 Space | 显示"不支持预览"提示 |
| T9 | 选中超过 5MB 的 `.txt`，按 Space | 显示"File is too large"提示 |
| T10 | 选中 `.pdf` 文件，按 Space | Edge PDF 查看器加载，可滚动和搜索 |
| T11 | 选中 `.md` 文件，按 Space | Markdown 渲染为 HTML，有表格/代码块样式 |
| T12 | 选中 `.csv` 文件，按 Space | 表格视图，最多 500 行 |
| T13 | 选中 `.mp3`，按 Space | HTML5 audio 控件，可播放 |
| T14 | 选中 `.mp4`，按 Space | HTML5 video 控件，可播放 |
| T15 | 预览窗口可拖拽到任意位置 | 标题栏拖拽正常 |
| T16 | 设置页关闭 PreviewEnabled | Space 无响应 |
| T17 | 主窗口隐藏时（Hide），再次 Show | 预览窗口不随主窗口一起显示（ShowInTaskbar=False + 无 Owner）|
| T18 | 多显示器：主窗口在副屏右侧 | 预览窗口出现在主窗口左侧（右侧空间不足时）|
| T19 | WebView2 Runtime 未安装 | 预览区显示安装提示，不崩溃 |
| T20 | 反复 Space 切换 20+ 次 | 内存无明显增长（WebView2 单实例复用）|

---

## 文件变更汇总

| 操作 | 文件 |
|------|------|
| 修改 | `src/Recents.App/Recents.App.csproj` — 添加 NuGet 包 |
| 新建 | `src/Recents.App/Services/Preview/PreviewTypeClassifier.cs` |
| 新建 | `src/Recents.App/Services/Preview/HtmlTemplateEngine.cs` |
| 新建 | `src/Recents.App/Services/Preview/PreviewService.cs` |
| 新建 | `src/Recents.App/PreviewWindow.xaml` |
| 新建 | `src/Recents.App/PreviewWindow.xaml.cs` |
| 修改 | `src/Recents.App/MainWindow.xaml.cs` — Space/Esc/Enter 处理 + 预热 |
| 修改 | `src/Recents.App/Models/AppSettings.cs` — `PreviewEnabled` |
| 修改 | `src/Recents.App/ViewModels/SettingsViewModel.cs` — `PreviewEnabled` |
| 修改 | `src/Recents.App/Views/SettingsWindow.xaml` — 开关 UI |
| 修改 | `src/Recents.App/App.xaml.cs` — 启动时预热 |

总计：**4 新建文件 + 7 处修改**。

---

## 关键注意事项

1. **`ShowActivated="False"`**：预览窗口不抢焦点，主窗口快捷键持续有效。
2. **`SetVirtualHostNameToFolderMapping`** 每次需先 `ClearVirtualHostNameToFolderMapping` 再重新映射，因为每个文件可能在不同目录。
3. **预热时机**：`EnsureCoreWebView2Async()` 是异步的，必须在构造函数中立即触发（`async void InitWebView2Async`）；第一次 Space 请求如果初始化尚未完成，`_pendingPath` 缓存机制保证不丢失。
4. **`Markdig.UseAdvancedExtensions()`** 包含表格和任务列表扩展，不需要单独引用其他 Markdig 包。
5. **P0 不引入 highlight.js** 等外部 JS 库，代码块仅做等宽字体展示；语法高亮留作 P1 迭代。
6. **`OnClosing` 拦截**：`PreviewWindow.OnClosing` cancel 关闭事件，改为 `Hide()`，确保 WebView2 实例全程复用。
