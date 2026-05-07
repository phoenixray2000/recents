using System.Text.RegularExpressions;

namespace Recents.App.Services.Clipboard;

internal static class HtmlSanitizer
{
    public static string SanitizeFragment(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var cleaned = Regex.Replace(
            html,
            @"<\s*(script|iframe|object|embed|svg|math)\b[^>]*>.*?<\s*/\s*\1\s*>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(200));

        cleaned = Regex.Replace(
            cleaned,
            @"<\s*(script|iframe|object|embed|svg|math|meta|link)\b[^>]*/?\s*>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(200));

        cleaned = Regex.Replace(
            cleaned,
            @"\s+on[a-z0-9_-]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
            string.Empty,
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(200));

        cleaned = Regex.Replace(
            cleaned,
            @"\s(src|href)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
            match =>
            {
                var attr = match.Groups[1].Value;
                var raw = match.Groups[2].Value.Trim().Trim('"', '\'');
                if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return $" {attr}=\"#\"";
                }

                return match.Value;
            },
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(200));

        return cleaned;
    }

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = Regex.Replace(html, @"<[^>]+>", " ", RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return text.Trim();
    }
}
