using System.Security.Cryptography;
using System.Text;
using Recents.App.Utils;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardHash
{
    public static string ForText(string text) => Sha256("text:" + text);
    public static string ForHtml(string html) => Sha256("html:" + html);
    public static string ForRtf(string rtf) => Sha256("rtf:" + rtf);
    public static string ForImage(byte[] pngBytes) => Sha256("image:" + Convert.ToHexString(SHA256.HashData(pngBytes)));

    public static string ForFiles(IEnumerable<string> paths)
    {
        var normalized = paths
            .Select(PathNormalizer.Normalize)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Order(StringComparer.OrdinalIgnoreCase);
        return Sha256("files:" + string.Join("\n", normalized));
    }

    private static string Sha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
