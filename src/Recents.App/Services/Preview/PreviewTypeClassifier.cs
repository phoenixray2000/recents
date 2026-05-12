using System.Collections.Generic;
using System.IO;

namespace Recents.App.Services.Preview;

public enum PreviewKind
{
    Unsupported,
    TooLarge,
    MissingFile,
    Folder,
    Pdf,
    Image,
    Text,
    Html,
    Csv,
    Code,
    Markdown,
    Audio,
    Video,
    ShellHandler,
}

public static class PreviewTypeClassifier
{
    private const long TextLimit = 5L * 1024 * 1024;
    private const long ImageLimit = 100L * 1024 * 1024;
    private const long PowerPointLimit = 50L * 1024 * 1024;

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico" };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".ini", ".conf", ".env" };

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".js", ".jsx", ".tsx", ".py", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".kt",
        ".json", ".xml", ".html", ".htm", ".css", ".scss", ".less",
        ".sql", ".yaml", ".yml", ".toml", ".sh", ".bat", ".ps1",
        ".config", ".props", ".targets",
    };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma" };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mov", ".wmv", ".avi", ".mkv" };

    private static readonly HashSet<string> ShellHandlerExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm",
        ".xls", ".xlsx", ".xlsm",
        ".ppt", ".pptx", ".pptm",
        ".rtf",
    };

    private static readonly HashSet<string> PowerPointExts = new(StringComparer.OrdinalIgnoreCase)
        { ".ppt", ".pptx", ".pptm" };

    public static (PreviewKind Kind, long FileSize) Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (PreviewKind.MissingFile, 0);

        if (Directory.Exists(path))
            return (PreviewKind.Folder, 0);

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

        if (PowerPointExts.Contains(ext) && size > PowerPointLimit)
            return (PreviewKind.TooLarge, size);

        if (ShellHandlerExts.Contains(ext))
            return (PreviewKind.ShellHandler, size);

        if (ext == ".pdf")
            return (PreviewKind.Pdf, size);

        if (ImageExts.Contains(ext))
            return size > ImageLimit ? (PreviewKind.TooLarge, size) : (PreviewKind.Image, size);

        if (AudioExts.Contains(ext))
            return (PreviewKind.Audio, size);

        if (VideoExts.Contains(ext))
            return (PreviewKind.Video, size);

        if (ext == ".csv")
            return size > TextLimit ? (PreviewKind.TooLarge, size) : (PreviewKind.Csv, size);

        if (ext is ".html" or ".htm")
            return size > TextLimit ? (PreviewKind.TooLarge, size) : (PreviewKind.Html, size);

        if (TextExts.Contains(ext))
            return size > TextLimit ? (PreviewKind.TooLarge, size) : (PreviewKind.Text, size);

        if (ext is ".md" or ".markdown")
            return size > TextLimit ? (PreviewKind.TooLarge, size) : (PreviewKind.Markdown, size);

        if (CodeExts.Contains(ext))
            return size > TextLimit ? (PreviewKind.TooLarge, size) : (PreviewKind.Code, size);

        return (PreviewKind.Unsupported, size);
    }
}
