using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Recents.App.Models;
using Recents.App.Services.Clipboard;

namespace Recents.App.Services.ClipboardSync;

internal sealed record ClipboardSyncExport(SyncClipboardProfile Profile, string? PayloadPath, string LocalHash);

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
        _ = deviceId;
        _ = deviceName;

        return item.Type switch
        {
            ClipboardPayloadType.Text or ClipboardPayloadType.Html or ClipboardPayloadType.RichText =>
                ExportTextLike(item),
            ClipboardPayloadType.Image =>
                await ExportSinglePayloadAsync(item, SyncClipboardProfileType.Image, item.ImagePath, maxPayloadBytes).ConfigureAwait(false),
            ClipboardPayloadType.Files =>
                await ExportFilesAsync(item, maxPayloadBytes).ConfigureAwait(false),
            _ => new ClipboardSyncExport(new SyncClipboardProfile
            {
                Type = SyncClipboardProfileType.Unknown,
                Hash = item.Hash,
                Text = item.PreviewText
            }, null, item.Hash)
        };
    }

    public async Task<ClipboardItem> ImportAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        return profile.Type switch
        {
            SyncClipboardProfileType.Text => await ImportTextAsync(profile, payloadPath).ConfigureAwait(false),
            SyncClipboardProfileType.Image => await ImportImageAsync(profile, payloadPath).ConfigureAwait(false),
            SyncClipboardProfileType.File => await ImportFileAsync(profile, payloadPath).ConfigureAwait(false),
            SyncClipboardProfileType.Group => await ImportGroupAsync(profile, payloadPath).ConfigureAwait(false),
            _ => new ClipboardItem
            {
                Type = ClipboardPayloadType.Unknown,
                Hash = profile.Hash,
                PreviewText = profile.Text,
                SizeBytes = profile.Size
            }
        };
    }

    private static ClipboardSyncExport ExportTextLike(ClipboardItem item)
    {
        var text = item.PlainText ?? item.PreviewText ?? string.Empty;
        var profile = new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Text,
            Hash = SyncClipboardHash.ForText(text),
            Text = text,
            HasData = false,
            Size = text.Length
        };

        return new ClipboardSyncExport(profile, null, item.Hash);
    }

    private async Task<ClipboardSyncExport> ExportSinglePayloadAsync(
        ClipboardItem item,
        SyncClipboardProfileType type,
        string? sourcePath,
        long maxPayloadBytes)
    {
        var profile = new SyncClipboardProfile
        {
            Type = type,
            Text = item.PreviewText ?? string.Empty,
            HasData = true,
            Size = item.SizeBytes ?? 0
        };

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new ClipboardSyncExport(profile, null, item.Hash);

        var length = new FileInfo(sourcePath).Length;
        if (length > maxPayloadBytes)
            return new ClipboardSyncExport(profile, null, item.Hash);

        var dataName = Path.GetFileName(sourcePath);
        var target = Path.Combine(_outgoingDirectory, dataName);
        await CopyFileAsync(sourcePath, target).ConfigureAwait(false);

        profile.DataName = dataName;
        profile.Text = dataName;
        profile.Hash = await SyncClipboardHash.ForFileAsync(target, dataName).ConfigureAwait(false);
        profile.Size = length;

        return new ClipboardSyncExport(profile, target, item.Hash);
    }

    private async Task<ClipboardSyncExport> ExportFilesAsync(ClipboardItem item, long maxPayloadBytes)
    {
        var existing = item.FilePaths
            .Select(f => f.Path)
            .Where(File.Exists)
            .ToList();

        if (existing.Count == 0)
        {
            return new ClipboardSyncExport(new SyncClipboardProfile
            {
                Type = SyncClipboardProfileType.Group,
                Text = item.PreviewText ?? string.Empty,
                HasData = true
            }, null, item.Hash);
        }

        if (existing.Count == 1)
        {
            var source = existing[0];
            var length = new FileInfo(source).Length;
            var dataName = Path.GetFileName(source);
            var profile = new SyncClipboardProfile
            {
                Type = SyncClipboardProfileType.File,
                Text = dataName,
                HasData = true,
                DataName = length <= maxPayloadBytes ? dataName : null,
                Hash = length <= maxPayloadBytes
                    ? await SyncClipboardHash.ForFileAsync(source, dataName).ConfigureAwait(false)
                    : string.Empty,
                Size = length
            };

            if (length > maxPayloadBytes)
                return new ClipboardSyncExport(profile, null, item.Hash);

            var target = Path.Combine(_outgoingDirectory, dataName);
            await CopyFileAsync(source, target).ConfigureAwait(false);
            return new ClipboardSyncExport(profile, target, item.Hash);
        }

        var zipDataName = $"{SafeName(item.Hash)}.zip";
        var zipTarget = Path.Combine(_outgoingDirectory, zipDataName);
        if (File.Exists(zipTarget))
            File.Delete(zipTarget);

        using (var zip = ZipFile.Open(zipTarget, ZipArchiveMode.Create))
        {
            foreach (var path in existing)
                zip.CreateEntryFromFile(path, Path.GetFileName(path));
        }

        var zipLength = new FileInfo(zipTarget).Length;
        if (zipLength > maxPayloadBytes)
        {
            File.Delete(zipTarget);
            return new ClipboardSyncExport(new SyncClipboardProfile
            {
                Type = SyncClipboardProfileType.Group,
                Text = string.Join('\n', existing.Select(Path.GetFileName)),
                HasData = true
            }, null, item.Hash);
        }

        var groupProfile = new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Group,
            Hash = await SyncClipboardHash.ForGroupAsync(existing).ConfigureAwait(false),
            Text = string.Join('\n', existing.Select(Path.GetFileName)),
            HasData = true,
            DataName = zipDataName,
            Size = existing.Sum(path => new FileInfo(path).Length)
        };

        return new ClipboardSyncExport(groupProfile, zipTarget, item.Hash);
    }

    private async Task<ClipboardItem> ImportTextAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        var text = profile.Text;
        if (profile.HasData && !string.IsNullOrWhiteSpace(payloadPath) && File.Exists(payloadPath))
            text = await File.ReadAllTextAsync(payloadPath).ConfigureAwait(false);

        var created = DateTime.UtcNow;
        return new ClipboardItem
        {
            Type = ClipboardPayloadType.Text,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = ClipboardHash.ForText(text),
            PreviewText = text,
            PlainText = text,
            SizeBytes = profile.Size
        };
    }

    private async Task<ClipboardItem> ImportImageAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        var created = DateTime.UtcNow;
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Image,
            CreatedUtc = created,
            LastUsedUtc = created,
            PreviewText = profile.Text,
            SizeBytes = profile.Size
        };

        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return item;

        var itemDirectory = Path.Combine(_incomingDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        var image = await ImportIncomingImageAsync(
            payloadPath,
            itemDirectory,
            Path.GetFileName(profile.DataName ?? profile.Text ?? "remote.png")).ConfigureAwait(false);
        item.ImagePath = image.Path;
        item.Hash = ClipboardHash.ForImage(image.Bytes);
        item.SizeBytes = image.Bytes.LongLength;
        item.ImageWidth = image.Width;
        item.ImageHeight = image.Height;
        if (image.Width.HasValue && image.Height.HasValue && string.IsNullOrWhiteSpace(item.PreviewText))
            item.PreviewText = $"Screenshot {image.Width}x{image.Height}";
        return item;
    }

    private static async Task<ImportedImagePayload> ImportIncomingImageAsync(
        string payloadPath,
        string itemDirectory,
        string fileName)
    {
        var sourceBytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
        if (TryNormalizeImageBytes(sourceBytes, out var pngBytes, out var width, out var height))
        {
            var normalizedName = NormalizedImageFileName(fileName);
            var target = Path.Combine(itemDirectory, normalizedName);
            await WriteBytesAsync(target, pngBytes).ConfigureAwait(false);
            return new ImportedImagePayload(target, pngBytes, width, height);
        }

        var copied = await CopyIncomingAsync(payloadPath, itemDirectory, Path.GetFileName(fileName)).ConfigureAwait(false);
        return new ImportedImagePayload(copied, sourceBytes, null, null);
    }

    private static bool TryNormalizeImageBytes(
        byte[] sourceBytes,
        out byte[] pngBytes,
        out int width,
        out int height)
    {
        if (TryNormalizeImageBytesWithWpf(sourceBytes, out pngBytes, out width, out height))
            return true;

        return TryNormalizeImageBytesWithMagick(sourceBytes, out pngBytes, out width, out height);
    }

    private static bool TryNormalizeImageBytesWithWpf(
        byte[] sourceBytes,
        out byte[] pngBytes,
        out int width,
        out int height)
    {
        pngBytes = [];
        width = 0;
        height = 0;

        try
        {
            using var input = new MemoryStream(sourceBytes);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;

            var clean = CopyPixelsWithoutMetadata(frame);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(clean));
            using var output = new MemoryStream();
            encoder.Save(output);
            pngBytes = output.ToArray();
            return pngBytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeImageBytesWithMagick(
        byte[] sourceBytes,
        out byte[] pngBytes,
        out int width,
        out int height)
    {
        pngBytes = [];
        width = 0;
        height = 0;

        try
        {
            using var image = new MagickImage(sourceBytes);
            image.AutoOrient();
            image.Strip();
            width = checked((int)image.Width);
            height = checked((int)image.Height);
            pngBytes = image.ToByteArray(MagickFormat.Png);
            return pngBytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource CopyPixelsWithoutMetadata(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();

        var stride = checked((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            bitmap.DpiX,
            bitmap.DpiY,
            bitmap.Format,
            null,
            pixels,
            stride);
        copy.Freeze();
        return copy;
    }

    private static string NormalizedImageFileName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "remote";

        return baseName + ".png";
    }

    private async Task<ClipboardItem> ImportFileAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        var path = await CopyIncomingPayloadAsync(profile, payloadPath, profile.DataName ?? profile.Text).ConfigureAwait(false);
        return FileDropItem(profile, path is null ? [] : [path]);
    }

    private async Task<ClipboardItem> ImportGroupAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return FileDropItem(profile, []);

        var itemDirectory = Path.Combine(_incomingDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        var paths = ExtractZip(payloadPath, itemDirectory);
        return FileDropItem(profile, paths);
    }

    private async Task<string?> CopyIncomingPayloadAsync(SyncClipboardProfile profile, string? payloadPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return null;

        var itemDirectory = Path.Combine(_incomingDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        return await CopyIncomingAsync(payloadPath, itemDirectory, Path.GetFileName(fileName)).ConfigureAwait(false);
    }

    private static ClipboardItem FileDropItem(SyncClipboardProfile profile, IReadOnlyList<string> paths)
    {
        var created = DateTime.UtcNow;
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Files,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = paths.Count == 0 ? profile.Hash : ClipboardHash.ForFiles(paths),
            PreviewText = profile.Text,
            PlainText = string.Join(Environment.NewLine, paths),
            SizeBytes = profile.Size,
            FilePaths = paths.Select(path => new ClipboardFilePath
            {
                Path = path,
                IsFolder = Directory.Exists(path),
                ExistsAtCapture = true
            }).ToList()
        };
        return item;
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
        await CopyFileAsync(source, target).ConfigureAwait(false);
        return target;
    }

    private static async Task CopyFileAsync(string source, string target)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(target);
        await input.CopyToAsync(output);
    }

    private static async Task WriteBytesAsync(string target, byte[] bytes)
    {
        await using var output = File.Create(target);
        await output.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string SafeName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant() is { Length: > 0 } safe
            ? safe
            : Guid.NewGuid().ToString("N");

    private sealed record ImportedImagePayload(string Path, byte[] Bytes, int? Width, int? Height);
}
