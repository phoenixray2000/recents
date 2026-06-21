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
    private readonly IClipboardManagedStorage _managed;

    public ClipboardSyncPayloadService(string outgoingDirectory, IClipboardManagedStorage managed)
    {
        _outgoingDirectory = outgoingDirectory;
        _managed = managed;
        Directory.CreateDirectory(_outgoingDirectory);
        Directory.CreateDirectory(_managed.ImageDirectory);
        Directory.CreateDirectory(_managed.ThumbnailDirectory);
        Directory.CreateDirectory(_managed.FilesDirectory);
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
        if (ShouldImportTextPayloadAsImage(profile, payloadPath))
            return await ImportImageAsync(profile, payloadPath).ConfigureAwait(false);

        return profile.Type switch
        {
            SyncClipboardProfileType.Text => await ImportTextAsync(profile, payloadPath).ConfigureAwait(false),
            SyncClipboardProfileType.Image => await ImportImageAsync(profile, payloadPath).ConfigureAwait(false),
            SyncClipboardProfileType.File when SyncClipboardPayloadFormats.IsStandardImageFileName(profile.DataName ?? profile.Text) =>
                await ImportImageAsync(profile, payloadPath).ConfigureAwait(false),
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

    private static bool ShouldImportTextPayloadAsImage(SyncClipboardProfile profile, string? payloadPath)
    {
        if (profile.Type != SyncClipboardProfileType.Text ||
            !profile.HasData ||
            string.IsNullOrWhiteSpace(payloadPath) ||
            !File.Exists(payloadPath))
        {
            return false;
        }

        return SyncClipboardPayloadFormats.IsKnownImageFileName(profile.DataName ?? profile.Text);
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

    private async Task<ClipboardSyncExport?> TryExportComplexImageFileAsync(
        ClipboardItem item,
        string sourcePath,
        long maxPayloadBytes)
    {
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);
        if (!TryConvertComplexImageBytes(sourceBytes, Path.GetFileName(sourcePath), out var convertedName, out var convertedBytes, out _, out _))
            return null;

        if (convertedBytes.LongLength > maxPayloadBytes)
        {
            return new ClipboardSyncExport(new SyncClipboardProfile
            {
                Type = SyncClipboardProfileType.Image,
                Text = convertedName,
                HasData = true,
                Size = convertedBytes.LongLength
            }, null, item.Hash);
        }

        var target = Path.Combine(_outgoingDirectory, convertedName);
        await WriteBytesAsync(target, convertedBytes).ConfigureAwait(false);

        var profile = new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Image,
            Text = convertedName,
            HasData = true,
            DataName = convertedName,
            Hash = await SyncClipboardHash.ForFileAsync(target, convertedName).ConfigureAwait(false),
            Size = convertedBytes.LongLength
        };

        return new ClipboardSyncExport(profile, target, item.Hash);
    }

    private async Task<ClipboardSyncExport> ExportFilesAsync(ClipboardItem item, long maxPayloadBytes)
    {
        var existing = item.FilePaths
            .Select(f => f.Path)
            .Where(path => File.Exists(path) || Directory.Exists(path))
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

        if (existing.Count == 1 && File.Exists(existing[0]))
        {
            var source = existing[0];
            var dataName = Path.GetFileName(source);

            if (SyncClipboardPayloadFormats.IsStandardImageFileName(dataName))
                return await ExportSinglePayloadAsync(item, SyncClipboardProfileType.Image, source, maxPayloadBytes).ConfigureAwait(false);

            if (SyncClipboardPayloadFormats.IsComplexImageFileName(dataName) &&
                await TryExportComplexImageFileAsync(item, source, maxPayloadBytes).ConfigureAwait(false) is { } convertedImage)
            {
                return convertedImage;
            }

            return await ExportSinglePayloadAsync(item, SyncClipboardProfileType.File, source, maxPayloadBytes).ConfigureAwait(false);
        }

        var zipDataName = $"{SafeName(item.Hash)}.zip";
        var zipTarget = Path.Combine(_outgoingDirectory, zipDataName);
        if (File.Exists(zipTarget))
            File.Delete(zipTarget);

        await CreateGroupArchiveAsync(existing, zipTarget).ConfigureAwait(false);

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
            Size = existing.Sum(GetPathPayloadSize)
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

        var image = await ImportManagedImageAsync(
            payloadPath,
            _managed.ImageDirectory,
            Path.GetFileName(profile.DataName ?? profile.Text ?? "remote.png")).ConfigureAwait(false);

        item.ImagePath = image.Path;
        item.Hash = ClipboardHash.ForImage(image.Bytes);
        item.SizeBytes = image.Bytes.LongLength;
        item.ImageWidth = image.Width;
        item.ImageHeight = image.Height;
        if (image.Width.HasValue && image.Height.HasValue && string.IsNullOrWhiteSpace(item.PreviewText))
            item.PreviewText = $"Screenshot {image.Width}x{image.Height}";

        TryWriteImportedThumbnail(item, image.Bytes, created);
        return item;
    }

    private static async Task<ImportedImagePayload> ImportManagedImageAsync(
        string payloadPath,
        string imageDirectory,
        string fileName)
    {
        var safeFileName = SyncClipboardPayloadFormats.SafePayloadFileName(fileName, "remote.png");
        var sourceBytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);

        if (SyncClipboardPayloadFormats.IsStandardImageFileName(safeFileName))
        {
            var target = ManagedImageTarget(imageDirectory, safeFileName);
            await WriteBytesAsync(target, sourceBytes).ConfigureAwait(false);
            var dimensions = TryReadImageDimensions(sourceBytes);
            return new ImportedImagePayload(target, sourceBytes, dimensions.Width, dimensions.Height);
        }

        if (SyncClipboardPayloadFormats.IsComplexImageFileName(safeFileName) &&
            TryConvertComplexImageBytes(sourceBytes, safeFileName, out var convertedName, out var convertedBytes, out var convertedWidth, out var convertedHeight))
        {
            var target = ManagedImageTarget(imageDirectory, convertedName);
            await WriteBytesAsync(target, convertedBytes).ConfigureAwait(false);
            return new ImportedImagePayload(target, convertedBytes, convertedWidth, convertedHeight);
        }

        if (TryNormalizeUnknownImageBytes(sourceBytes, safeFileName, out var pngName, out var pngBytes, out var width, out var height))
        {
            var target = ManagedImageTarget(imageDirectory, pngName);
            await WriteBytesAsync(target, pngBytes).ConfigureAwait(false);
            return new ImportedImagePayload(target, pngBytes, width, height);
        }

        var fallback = ClipboardBlobNamer.EnsureUnique(imageDirectory, safeFileName);
        await CopyManagedAsync(payloadPath, imageDirectory, Path.GetFileName(fallback)).ConfigureAwait(false);
        return new ImportedImagePayload(fallback, sourceBytes, null, null);
    }

    private static string ManagedImageTarget(string imageDirectory, string fileName)
        => ClipboardBlobNamer.EnsureUnique(imageDirectory, fileName);

    private void TryWriteImportedThumbnail(ClipboardItem item, byte[] imageBytes, DateTime created)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath))
            return;
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            var thumbName = ClipboardBlobNamer.Build(ClipboardPayloadType.Image, created, item.Hash, ".jpg");
            var thumbPath = ClipboardBlobNamer.EnsureUnique(_managed.ThumbnailDirectory, thumbName);
            _managed.WriteThumbnail(frame, thumbPath, imageBytes);
            item.ThumbnailPath = thumbPath;
        }
        catch
        {
            // Only reached for a genuinely-undecodable fallback copy; image remains usable without a thumbnail.
        }
    }

    private static (int? Width, int? Height) TryReadImageDimensions(byte[] sourceBytes)
    {
        try
        {
            using var input = new MemoryStream(sourceBytes);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            try
            {
                using var image = new MagickImage(sourceBytes);
                return (checked((int)image.Width), checked((int)image.Height));
            }
            catch
            {
                return (null, null);
            }
        }
    }

    private static bool TryConvertComplexImageBytes(
        byte[] sourceBytes,
        string sourceFileName,
        out string convertedName,
        out byte[] convertedBytes,
        out int width,
        out int height)
    {
        convertedName = string.Empty;
        convertedBytes = [];
        width = 0;
        height = 0;

        try
        {
            using var images = new MagickImageCollection(sourceBytes);
            if (images.Count == 0)
                return false;

            var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "remote";

            if (images.Count >= 2)
            {
                images.Coalesce();
                width = checked((int)images[0].Width);
                height = checked((int)images[0].Height);
                using var output = new MemoryStream();
                images.Write(output, MagickFormat.Gif);
                convertedBytes = output.ToArray();
                convertedName = baseName + ".gif";
                return convertedBytes.Length > 0;
            }

            using var image = new MagickImage(sourceBytes);
            image.AutoOrient();
            image.Strip();
            width = checked((int)image.Width);
            height = checked((int)image.Height);
            convertedBytes = image.ToByteArray(MagickFormat.Jpeg);
            convertedName = baseName + ".jpg";
            return convertedBytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeUnknownImageBytes(
        byte[] sourceBytes,
        string sourceFileName,
        out string pngName,
        out byte[] pngBytes,
        out int width,
        out int height)
    {
        pngName = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(pngName))
            pngName = "remote";
        pngName += ".png";
        pngBytes = [];
        width = 0;
        height = 0;

        if (TryNormalizeUnknownImageBytesWithWpf(sourceBytes, out pngBytes, out width, out height))
            return true;

        return TryNormalizeUnknownImageBytesWithMagick(sourceBytes, out pngBytes, out width, out height);
    }

    private static bool TryNormalizeUnknownImageBytesWithWpf(
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

    private static bool TryNormalizeUnknownImageBytesWithMagick(
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

    private async Task<ClipboardItem> ImportFileAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        var path = await CopyManagedPayloadAsync(profile, payloadPath, profile.DataName ?? profile.Text).ConfigureAwait(false);
        return FileDropItem(profile, path is null ? [] : [path]);
    }

    private async Task<ClipboardItem> ImportGroupAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return FileDropItem(profile, []);

        var itemDirectory = Path.Combine(_managed.FilesDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        var paths = ExtractZip(payloadPath, itemDirectory);
        return FileDropItem(profile, paths);
    }

    private async Task<string?> CopyManagedPayloadAsync(SyncClipboardProfile profile, string? payloadPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return null;

        var itemDirectory = Path.Combine(_managed.FilesDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        return await CopyManagedAsync(payloadPath, itemDirectory, Path.GetFileName(fileName)).ConfigureAwait(false);
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
        var fullTarget = Path.GetFullPath(targetDirectory + Path.DirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(entryName))
                continue;

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entryName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry escapes target directory: {entry.FullName}");

            var firstSegment = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstSegment))
                roots.Add(Path.Combine(targetDirectory, firstSegment));

            if (entryName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            entry.ExtractToFile(destination, overwrite: true);
        }

        return roots.ToList();
    }

    private static async Task CreateGroupArchiveAsync(IReadOnlyList<string> paths, string zipTarget)
    {
        await using var file = new FileStream(zipTarget, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: System.Text.Encoding.UTF8);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var rootName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(rootName))
                    continue;

                zip.CreateEntry(rootName + "/");

                foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(path, directory).Replace(Path.DirectorySeparatorChar, '/');
                    zip.CreateEntry(rootName + "/" + relative + "/");
                }

                foreach (var filePath in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(path, filePath).Replace(Path.DirectorySeparatorChar, '/');
                    await AddFileToArchiveAsync(zip, rootName + "/" + relative, filePath).ConfigureAwait(false);
                }
            }
            else if (File.Exists(path))
            {
                await AddFileToArchiveAsync(zip, Path.GetFileName(path), path).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddFileToArchiveAsync(ZipArchive archive, string entryName, string sourcePath)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await source.CopyToAsync(entryStream).ConfigureAwait(false);
    }

    private static long GetPathPayloadSize(string path)
    {
        if (File.Exists(path))
            return new FileInfo(path).Length;

        if (!Directory.Exists(path))
            return 0;

        return Directory
            .GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static async Task<string> CopyManagedAsync(string source, string targetDirectory, string fileName)
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
