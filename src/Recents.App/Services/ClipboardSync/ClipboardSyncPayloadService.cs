using System.IO;
using System.IO.Compression;
using Recents.App.Models;

namespace Recents.App.Services.ClipboardSync;

internal sealed record ClipboardSyncExport(RecentClipboardProfile Profile, string? PayloadPath);

internal sealed class ClipboardSyncPayloadService
{
    private readonly string _outgoingDirectory;
    private readonly string _incomingDirectory;

    public ClipboardSyncPayloadService(string outgoingDirectory, string incomingDirectory)
    {
        _outgoingDirectory = outgoingDirectory;
        _incomingDirectory = incomingDirectory;
        Directory.CreateDirectory(_outgoingDirectory);
        Directory.CreateDirectory(_incomingDirectory);
    }

    public async Task<ClipboardSyncExport> ExportAsync(
        ClipboardItem item, string deviceId, string deviceName, long maxPayloadBytes)
    {
        var profile = new RecentClipboardProfile
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            UpdatedUtc = DateTime.UtcNow,
            Type = item.Type,
            Hash = item.Hash,
            PreviewText = item.PreviewText,
            PlainText = item.Type is ClipboardPayloadType.Text or ClipboardPayloadType.Html or ClipboardPayloadType.RichText
                ? item.PlainText
                : null,
            SizeBytes = item.SizeBytes
        };

        var payloadPath = item.Type switch
        {
            ClipboardPayloadType.Text => null,
            ClipboardPayloadType.Html => await CopyFirstExistingAsync(profile, item.Hash, ".cfhtml", maxPayloadBytes, item.BlobPath, item.HtmlBlobPath),
            ClipboardPayloadType.RichText => await CopyFirstExistingAsync(profile, item.Hash, ".rtf", maxPayloadBytes, item.RtfBlobPath, item.BlobPath),
            ClipboardPayloadType.Image => await CopyFirstExistingAsync(profile, item.Hash, ".png", maxPayloadBytes, item.ImagePath),
            ClipboardPayloadType.Files => await ZipFilesAsync(profile, item, maxPayloadBytes),
            _ => null
        };

        return new ClipboardSyncExport(profile, payloadPath);
    }

    public async Task<ClipboardItem> ImportAsync(RecentClipboardProfile profile, string? payloadPath)
    {
        var created = profile.UpdatedUtc == default ? DateTime.UtcNow : profile.UpdatedUtc;
        var item = new ClipboardItem
        {
            Type = profile.Type,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = profile.Hash,
            PreviewText = profile.PreviewText,
            PlainText = profile.PlainText,
            SizeBytes = profile.SizeBytes
        };

        if (profile.Type == ClipboardPayloadType.Text)
            return item;

        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return item;

        var itemDirectory = Path.Combine(_incomingDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);

        switch (profile.Type)
        {
            case ClipboardPayloadType.Html:
                item.BlobPath = await CopyIncomingAsync(payloadPath, itemDirectory, "remote.cfhtml");
                item.HtmlBlobPath = item.BlobPath;
                break;
            case ClipboardPayloadType.RichText:
                item.RtfBlobPath = await CopyIncomingAsync(payloadPath, itemDirectory, "remote.rtf");
                break;
            case ClipboardPayloadType.Image:
                item.ImagePath = await CopyIncomingAsync(payloadPath, itemDirectory, "remote.png");
                break;
            case ClipboardPayloadType.Files:
                item.FilePaths = ExtractZip(payloadPath, itemDirectory)
                    .Select(path => new ClipboardFilePath
                    {
                        Path = path,
                        IsFolder = Directory.Exists(path),
                        ExistsAtCapture = true
                    })
                    .ToList();
                item.PlainText = string.Join(Environment.NewLine, item.FilePaths.Select(f => f.Path));
                break;
        }

        return item;
    }

    private async Task<string?> CopyFirstExistingAsync(
        RecentClipboardProfile profile, string hash, string extension, long maxPayloadBytes, params string?[] paths)
    {
        var source = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (source is null)
            return null;

        var length = new FileInfo(source).Length;
        if (length > maxPayloadBytes)
            return null;

        var dataName = $"{SafeName(hash)}{extension}";
        var target = Path.Combine(_outgoingDirectory, dataName);
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(target))
            await input.CopyToAsync(output);

        profile.DataName = dataName;
        profile.SizeBytes = length;
        return target;
    }

    private async Task<string?> ZipFilesAsync(RecentClipboardProfile profile, ClipboardItem item, long maxPayloadBytes)
    {
        var existing = item.FilePaths
            .Select(f => f.Path)
            .Where(File.Exists)
            .ToList();
        if (existing.Count == 0)
            return null;

        var dataName = $"{SafeName(item.Hash)}.zip";
        var target = Path.Combine(_outgoingDirectory, dataName);
        if (File.Exists(target))
            File.Delete(target);

        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var path in existing)
                zip.CreateEntryFromFile(path, Path.GetFileName(path));
        }

        var length = new FileInfo(target).Length;
        if (length > maxPayloadBytes)
        {
            File.Delete(target);
            return null;
        }

        profile.DataName = dataName;
        profile.SizeBytes = length;
        profile.Files = existing.Select(path => new RecentClipboardFileEntry
        {
            RelativePath = Path.GetFileName(path),
            IsFolder = false
        }).ToList();
        await Task.CompletedTask;
        return target;
    }

    private static List<string> ExtractZip(string zipPath, string targetDirectory)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullTarget = Path.GetFullPath(targetDirectory);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
            if (!destination.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !destination.Equals(fullTarget, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            var firstSegment = entry.FullName.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstSegment))
                roots.Add(Path.Combine(targetDirectory, firstSegment));
        }

        return roots.ToList();
    }

    private static async Task<string> CopyIncomingAsync(string source, string targetDirectory, string fileName)
    {
        var target = Path.Combine(targetDirectory, fileName);
        await using var input = File.OpenRead(source);
        await using var output = File.Create(target);
        await input.CopyToAsync(output);
        return target;
    }

    private static string SafeName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant() is { Length: > 0 } safe
            ? safe
            : Guid.NewGuid().ToString("N");
}
