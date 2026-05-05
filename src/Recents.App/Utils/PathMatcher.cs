using Recents.App.Models;
using System.IO;

namespace Recents.App.Utils;

public static class PathMatcher
{
    public static bool MatchesSearch(RecentItem item, string? searchText)
    {
        if (item == null) return false;
        if (string.IsNullOrWhiteSpace(searchText)) return true;

        var tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return true;

        foreach (var token in tokens)
        {
            if (!MatchesToken(item, token))
                return false;
        }

        return true;
    }

    public static bool IsHiddenPath(string path, IEnumerable<string>? hiddenPaths) =>
        hiddenPaths?.Any(hidden => PathsEqual(path, hidden)) == true;

    public static bool IsExcludedByPath(string path, IEnumerable<string>? excludedPaths) =>
        excludedPaths?.Any(excluded => MatchesPathRule(path, excluded)) == true;

    public static bool IsWhitelisted(string path, IEnumerable<string>? whitelistedPaths)
    {
        var rules = whitelistedPaths?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (rules is null || rules.Count == 0) return true;
        return rules.Any(rule => MatchesPathRule(path, rule));
    }

    public static bool ContainsExcludedKeyword(RecentItem item, IEnumerable<string>? keywords)
    {
        if (item == null) return false;
        return keywords?.Any(keyword =>
        {
            if (string.IsNullOrWhiteSpace(keyword)) return false;
            return item.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   item.NormalizedPath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }) == true;
    }

    private static bool MatchesToken(RecentItem item, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;

        if (token.StartsWith('.'))
            return string.Equals(item.Extension, token, StringComparison.OrdinalIgnoreCase);

        if (token.Contains('\\') || token.Contains('/'))
            return NormalizeSeparators(item.NormalizedPath).Contains(NormalizeSeparators(token), StringComparison.OrdinalIgnoreCase);

        return item.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
               item.NormalizedPath.Contains(token, StringComparison.OrdinalIgnoreCase) ||
               item.Extension.TrimStart('.').Contains(token.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPathRule(string path, string rule)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rule)) return false;

        var normalizedPath = NormalizeSeparators(path).TrimEnd('\\');
        var normalizedRule = NormalizeSeparators(rule).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(normalizedRule)) return false;

        if (Path.IsPathRooted(normalizedRule))
        {
            return normalizedPath.Equals(normalizedRule, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(normalizedRule + "\\", StringComparison.OrdinalIgnoreCase);
        }

        var marker = "\\" + normalizedRule + "\\";
        return normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.EndsWith("\\" + normalizedRule, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Equals(normalizedRule, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeSeparators(left).TrimEnd('\\'), NormalizeSeparators(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSeparators(string value) => value.Replace('/', '\\');
}
