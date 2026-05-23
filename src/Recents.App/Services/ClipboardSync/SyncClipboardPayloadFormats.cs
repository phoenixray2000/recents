using System.IO;

namespace Recents.App.Services.ClipboardSync;

internal static class SyncClipboardPayloadFormats
{
    private static readonly HashSet<string> StandardImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".png"
    };

    private static readonly HashSet<string> ComplexImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic",
        ".heif",
        ".webp",
        ".avif"
    };

    public static bool IsStandardImageFileName(string? fileName) =>
        HasExtension(fileName, StandardImageExtensions);

    public static bool IsComplexImageFileName(string? fileName) =>
        HasExtension(fileName, ComplexImageExtensions);

    public static bool IsKnownImageFileName(string? fileName) =>
        IsStandardImageFileName(fileName) || IsComplexImageFileName(fileName);

    public static bool IsZipFileName(string? fileName) =>
        string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

    public static string SafePayloadFileName(string? fileName, string fallback)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            safe = fallback;

        safe = Path.GetFileName(safe);
        return string.IsNullOrWhiteSpace(safe) ? "payload.bin" : safe;
    }

    private static bool HasExtension(string? fileName, HashSet<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return extensions.Contains(Path.GetExtension(fileName));
    }
}
