using System.Text;
using System.Text.RegularExpressions;

namespace Recents.App.Services.Clipboard;

internal sealed record CfHtmlDocument(string RawHtml, string Html, string FragmentHtml);

internal static class CfHtmlParser
{
    private const string StartFragmentMarker = "<!--StartFragment-->";
    private const string EndFragmentMarker = "<!--EndFragment-->";

    public static CfHtmlDocument Parse(string? cfHtml)
    {
        if (string.IsNullOrWhiteSpace(cfHtml))
            return new CfHtmlDocument(string.Empty, string.Empty, string.Empty);

        var html = TryExtractByOffsets(cfHtml, "StartHTML", "EndHTML")
            ?? StripHeader(cfHtml);
        var fragment = TryExtractByOffsets(cfHtml, "StartFragment", "EndFragment")
            ?? TryExtractBetweenMarkers(html)
            ?? html;

        return new CfHtmlDocument(cfHtml, TrimBom(html), TrimBom(fragment));
    }

    private static string? TryExtractByOffsets(string text, string startKey, string endKey)
    {
        if (!TryReadOffset(text, startKey, out var start) ||
            !TryReadOffset(text, endKey, out var end) ||
            start < 0 ||
            end <= start)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (start >= bytes.Length)
            return null;

        end = Math.Min(end, bytes.Length);
        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    private static bool TryReadOffset(string text, string key, out int offset)
    {
        var match = Regex.Match(
            text,
            $@"(?im)^{Regex.Escape(key)}:(-?\d+)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return int.TryParse(match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty, out offset);
    }

    private static string StripHeader(string text)
    {
        var candidates = new[]
        {
            "<!doctype",
            "<html",
            "<body",
            StartFragmentMarker
        };

        var index = candidates
            .Select(candidate => text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        return text[index..];
    }

    private static string? TryExtractBetweenMarkers(string html)
    {
        var start = html.IndexOf(StartFragmentMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += StartFragmentMarker.Length;
        var end = html.IndexOf(EndFragmentMarker, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0 || end <= start)
            return null;

        return html[start..end];
    }

    private static string TrimBom(string value) => value.TrimStart('\uFEFF');
}
