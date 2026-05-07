using System.IO;
using Recents.App.Models;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardBlobNamer
{
    public static string Build(ClipboardPayloadType type, DateTime createdUtc, string hash, string extension)
    {
        var prefix = type switch
        {
            ClipboardPayloadType.Text => "TextSnippet",
            ClipboardPayloadType.Files => "FileDrop",
            ClipboardPayloadType.Image => "Screenshot",
            ClipboardPayloadType.Html => "HtmlSnippet",
            ClipboardPayloadType.RichText => "RichText",
            _ => "Clipboard"
        };

        var stamp = createdUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var safeHash = string.IsNullOrWhiteSpace(hash)
            ? Guid.NewGuid().ToString("N")[..8]
            : hash[..Math.Min(8, hash.Length)];
        extension = extension.StartsWith('.') ? extension : "." + extension;
        return $"{prefix}-{stamp}-{safeHash}{extension}";
    }

    public static string EnsureUnique(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            path = Path.Combine(directory, $"{name}-{i}{ext}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{ext}");
    }
}
