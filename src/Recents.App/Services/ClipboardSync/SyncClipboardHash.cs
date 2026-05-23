using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Recents.App.Services.ClipboardSync;

internal static class SyncClipboardHash
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static string ForText(string text) => Sha256Hex(Utf8NoBom.GetBytes(text));

    public static async Task<string> ForFileAsync(string filePath, string dataName, CancellationToken token = default)
    {
        var contentHash = await ForFileContentAsync(filePath, token).ConfigureAwait(false);
        return Sha256Hex(Utf8NoBom.GetBytes($"{dataName}|{contentHash.ToUpperInvariant()}"));
    }

    public static async Task<string> ForGroupAsync(IReadOnlyList<string> inputPaths, CancellationToken token = default)
    {
        var entries = new List<GroupEntry>();
        if (inputPaths.Count == 0)
            return Sha256Hex([]);

        var root = ResolveRootPath(inputPaths[0]);
        foreach (var path in inputPaths)
        {
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                AddDirectoryEntry(entries, root, path);
                foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                    AddDirectoryEntry(entries, root, directory);
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    await AddFileEntryAsync(entries, root, file, token).ConfigureAwait(false);
            }
            else if (File.Exists(path))
            {
                await AddFileEntryAsync(entries, root, path, token).ConfigureAwait(false);
            }
        }

        var ordered = entries
            .Select(entry => new { Entry = entry, Key = Utf8NoBom.GetBytes(entry.Name) })
            .OrderBy(entry => entry.Key, ByteArrayComparer.Instance);

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in ordered)
        {
            var line = entry.Entry.IsDirectory
                ? entry.Entry.Name
                : $"{entry.Entry.Name}|{entry.Entry.Length}|{entry.Entry.ContentHash}";
            incremental.AppendData(Utf8NoBom.GetBytes(line));
        }

        return Convert.ToHexString(incremental.GetHashAndReset());
    }

    private static string ResolveRootPath(string firstPath)
    {
        var basePath = Directory.Exists(firstPath)
            ? Path.TrimEndingDirectorySeparator(firstPath)
            : firstPath;
        return Path.GetDirectoryName(basePath) ?? Path.GetPathRoot(basePath) ?? Directory.GetCurrentDirectory();
    }

    private static void AddDirectoryEntry(List<GroupEntry> entries, string root, string path)
    {
        var name = BuildEntryName(root, path, isDirectory: true);
        if (!string.IsNullOrWhiteSpace(name))
            entries.Add(new GroupEntry(name, true, 0, string.Empty));
    }

    private static async Task AddFileEntryAsync(List<GroupEntry> entries, string root, string path, CancellationToken token)
    {
        var name = BuildEntryName(root, path, isDirectory: false);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var length = new FileInfo(path).Length;
        var contentHash = await ForFileContentAsync(path, token).ConfigureAwait(false);
        entries.Add(new GroupEntry(name, false, length, contentHash.ToUpperInvariant()));
    }

    private static string BuildEntryName(string root, string path, bool isDirectory)
    {
        var relative = Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/');
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return string.Empty;

        return isDirectory ? relative + "/" : relative;
    }

    private static async Task<string> ForFileContentAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record GroupEntry(string Name, bool IsDirectory, long Length, string ContentHash);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = x[i].CompareTo(y[i]);
                if (diff != 0)
                    return diff;
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
