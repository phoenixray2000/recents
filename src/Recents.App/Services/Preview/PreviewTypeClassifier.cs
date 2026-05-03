using System.IO;
using System.Collections.Generic;

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
