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
