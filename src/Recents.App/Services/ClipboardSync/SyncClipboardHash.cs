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

    public static async Task<string> ForGroupAsync(IReadOnlyList<string> filePaths, CancellationToken token = default)
    {
        var entries = new List<GroupEntry>();
        foreach (var path in filePaths)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                continue;

            var fileName = Path.GetFileName(path);
            var length = new FileInfo(path).Length;
            var contentHash = await ForFileContentAsync(path, token).ConfigureAwait(false);
            entries.Add(new GroupEntry(fileName, length, contentHash.ToUpperInvariant()));
        }

        var ordered = entries
            .Select(entry => new { Entry = entry, Key = Utf8NoBom.GetBytes(entry.Name) })
            .OrderBy(entry => entry.Key, ByteArrayComparer.Instance);

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in ordered)
        {
            var line = $"F|{entry.Entry.Name}|{entry.Entry.Length}|{entry.Entry.ContentHash}\0";
            incremental.AppendData(Utf8NoBom.GetBytes(line));
        }

        return Convert.ToHexString(incremental.GetHashAndReset());
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

    private sealed record GroupEntry(string Name, long Length, string ContentHash);

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
