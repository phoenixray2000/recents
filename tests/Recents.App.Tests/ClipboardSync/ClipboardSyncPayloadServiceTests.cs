using Recents.App.Models;
using Recents.App.Services.ClipboardSync;
using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class ClipboardSyncPayloadServiceTests
{
    [Fact]
    public async Task ExportAsync_TextStoresPlainTextInline()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Text,
            Hash = "hash-text",
            PreviewText = "hello",
            PlainText = "hello"
        };

        var export = await fixture.Service.ExportAsync(item, "device-1", "workstation", 1024 * 1024);

        Assert.Equal(SyncClipboardProfileType.Text, export.Profile.Type);
        Assert.Equal("hello", export.Profile.Text);
        Assert.Equal("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", export.Profile.Hash);
        Assert.False(export.Profile.HasData);
        Assert.Null(export.PayloadPath);
        Assert.Null(export.Profile.DataName);
    }

    [Fact]
    public async Task ExportAsync_HtmlFallsBackToSyncClipboardText()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Html,
            Hash = "recents-html-hash",
            PreviewText = "hello",
            PlainText = "hello"
        };

        var export = await fixture.Service.ExportAsync(item, "device-1", "workstation", 1024 * 1024);

        Assert.Equal(SyncClipboardProfileType.Text, export.Profile.Type);
        Assert.Equal("hello", export.Profile.Text);
        Assert.False(export.Profile.HasData);
        Assert.Null(export.PayloadPath);
    }

    [Fact]
    public async Task ExportAndImportAsync_ImageUsesPayloadFile()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var imagePath = Path.Combine(fixture.SourceDirectory, "image.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));

        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Image,
            Hash = "hash-image",
            PreviewText = "Screenshot 1x1",
            ImagePath = imagePath,
            SizeBytes = new FileInfo(imagePath).Length
        };

        var export = await fixture.Service.ExportAsync(item, "device-1", "workstation", 1024 * 1024);
        var imported = await fixture.Service.ImportAsync(export.Profile, export.PayloadPath);

        Assert.NotNull(export.PayloadPath);
        Assert.Equal(SyncClipboardProfileType.Image, export.Profile.Type);
        Assert.True(export.Profile.HasData);
        Assert.EndsWith(".png", export.Profile.DataName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(imported.ImagePath));
        Assert.Equal(ClipboardPayloadType.Image, imported.Type);
    }

    [Fact]
    public async Task ImportAsync_ImagePreservesStandardRemotePngPayload()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var payloadPath = Path.Combine(fixture.SourceDirectory, "Clipboard 2026年5月23日 22.59.png");
        var remoteBytes = AddPngChunk(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="),
            "caBX",
            Encoding.ASCII.GetBytes("mobile-extra"));
        await File.WriteAllBytesAsync(payloadPath, remoteBytes);

        var imported = await fixture.Service.ImportAsync(new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Image,
            Hash = "remote-image-hash",
            Text = Path.GetFileName(payloadPath),
            HasData = true,
            DataName = Path.GetFileName(payloadPath),
            Size = remoteBytes.Length
        }, payloadPath);

        Assert.Equal(ClipboardPayloadType.Image, imported.Type);
        Assert.NotNull(imported.ImagePath);
        Assert.EndsWith(".png", imported.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, imported.ImageWidth);
        Assert.Equal(1, imported.ImageHeight);

        var preserved = await File.ReadAllBytesAsync(imported.ImagePath);
        Assert.True(ContainsSequence(preserved, Encoding.ASCII.GetBytes("caBX")));
        Assert.Equal(remoteBytes, preserved);
    }

    [Fact]
    public async Task ImportAsync_ImagePreservesStandardRemoteJpegPayload()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var payloadPath = Path.Combine(fixture.SourceDirectory, "photo.jpeg");
        var remoteBytes = CreateJpegBytes();
        await File.WriteAllBytesAsync(payloadPath, remoteBytes);

        var imported = await fixture.Service.ImportAsync(new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Image,
            Hash = "remote-jpeg-image-hash",
            Text = "photo.jpeg",
            HasData = true,
            DataName = "photo.jpeg",
            Size = remoteBytes.Length
        }, payloadPath);

        Assert.Equal(ClipboardPayloadType.Image, imported.Type);
        Assert.NotNull(imported.ImagePath);
        Assert.EndsWith(".jpeg", imported.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, imported.ImageWidth);
        Assert.Equal(1, imported.ImageHeight);

        var preserved = await File.ReadAllBytesAsync(imported.ImagePath);
        Assert.Equal(remoteBytes, preserved);
    }

    [Fact]
    public async Task ImportAsync_ImageNormalizesHeicPayloadWhenFixtureProvided()
    {
        var heicFixture = Environment.GetEnvironmentVariable("RECENTS_HEIC_FIXTURE");
        if (string.IsNullOrWhiteSpace(heicFixture) || !File.Exists(heicFixture))
            return;

        using var fixture = ClipboardSyncPayloadFixture.Create();
        var payloadPath = Path.Combine(fixture.SourceDirectory, "iphone.heic");
        var remoteBytes = await File.ReadAllBytesAsync(heicFixture);
        await File.WriteAllBytesAsync(payloadPath, remoteBytes);

        var imported = await fixture.Service.ImportAsync(new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Image,
            Hash = "remote-heic-hash",
            Text = "iphone.heic",
            HasData = true,
            DataName = "iphone.heic",
            Size = remoteBytes.Length
        }, payloadPath);

        Assert.Equal(ClipboardPayloadType.Image, imported.Type);
        Assert.NotNull(imported.ImagePath);
        Assert.EndsWith(".jpg", imported.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(imported.ImageWidth > 0);
        Assert.True(imported.ImageHeight > 0);

        var converted = await File.ReadAllBytesAsync(imported.ImagePath);
        Assert.True(converted.Length > 2);
        Assert.Equal(0xFF, converted[0]);
        Assert.Equal(0xD8, converted[1]);
        Assert.NotEqual(remoteBytes, converted);
    }

    [Fact]
    public async Task ExportAndImportAsync_SingleFileUsesSyncClipboardFilePayload()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var filePath = Path.Combine(fixture.SourceDirectory, "note.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Files,
            Hash = "recents-files-hash",
            PreviewText = "note.txt",
            PlainText = filePath,
            FilePaths = [new ClipboardFilePath { Path = filePath, ExistsAtCapture = true }]
        };

        var export = await fixture.Service.ExportAsync(item, "device-1", "workstation", 1024 * 1024);
        var imported = await fixture.Service.ImportAsync(export.Profile, export.PayloadPath);

        Assert.NotNull(export.PayloadPath);
        Assert.Equal(SyncClipboardProfileType.File, export.Profile.Type);
        Assert.Equal("note.txt", export.Profile.Text);
        Assert.Equal("note.txt", export.Profile.DataName);
        Assert.True(export.Profile.HasData);
        Assert.False(export.PayloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var importedFile = Assert.Single(imported.FilePaths);
        Assert.True(File.Exists(importedFile.Path));
        Assert.Equal("hello", await File.ReadAllTextAsync(importedFile.Path));
    }

    [Fact]
    public async Task ExportAndImportAsync_FilesUsesZipAndRestores()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var filePath = Path.Combine(fixture.SourceDirectory, "note.txt");
        var otherPath = Path.Combine(fixture.SourceDirectory, "other.txt");
        await File.WriteAllTextAsync(filePath, "hello");
        await File.WriteAllTextAsync(otherPath, "world");

        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Files,
            Hash = "hash-files",
            PreviewText = "note.txt",
            PlainText = filePath,
            FilePaths =
            [
                new ClipboardFilePath { Path = filePath, ExistsAtCapture = true },
                new ClipboardFilePath { Path = otherPath, ExistsAtCapture = true }
            ]
        };

        var export = await fixture.Service.ExportAsync(item, "device-1", "workstation", 1024 * 1024);
        var imported = await fixture.Service.ImportAsync(export.Profile, export.PayloadPath);

        Assert.NotNull(export.PayloadPath);
        Assert.Equal(SyncClipboardProfileType.Group, export.Profile.Type);
        Assert.True(export.Profile.HasData);
        Assert.EndsWith(".zip", export.Profile.DataName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, imported.FilePaths.Count);
        Assert.Contains(imported.FilePaths, file => File.Exists(file.Path) && Path.GetFileName(file.Path) == "note.txt");
        Assert.Contains(imported.FilePaths, file => File.Exists(file.Path) && Path.GetFileName(file.Path) == "other.txt");
    }

    [Fact]
    public async Task ExportAsync_ReturnsNoPayloadWhenOverSizeLimit()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var imagePath = Path.Combine(fixture.SourceDirectory, "big.png");
        await File.WriteAllBytesAsync(imagePath, new byte[4096]);

        var item = new ClipboardItem { Type = ClipboardPayloadType.Image, Hash = "big", ImagePath = imagePath };

        var export = await fixture.Service.ExportAsync(item, "device-1", "workstation", maxPayloadBytes: 1024);

        Assert.Null(export.PayloadPath);
        Assert.Null(export.Profile.DataName);
    }

    [Fact]
    public async Task ExportAsync_ExcludesFolders()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var folder = Path.Combine(fixture.SourceDirectory, "sub");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "inner.txt"), "x");

        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Files,
            Hash = "hash-folder",
            FilePaths = [new ClipboardFilePath { Path = folder, IsFolder = true, ExistsAtCapture = true }]
        };

        var export = await fixture.Service.ExportAsync(item, "device-1", "ws", 1024 * 1024);

        Assert.Null(export.PayloadPath);
        Assert.Null(export.Profile.DataName);
    }

    private sealed class ClipboardSyncPayloadFixture : IDisposable
    {
        private readonly string _root;
        public string SourceDirectory { get; }
        public ClipboardSyncPayloadService Service { get; }

        private ClipboardSyncPayloadFixture(string root)
        {
            _root = root;
            SourceDirectory = Path.Combine(root, "source");
            Directory.CreateDirectory(SourceDirectory);
            Service = new ClipboardSyncPayloadService(
                Path.Combine(root, "outgoing"),
                Path.Combine(root, "incoming"));
        }

        public static ClipboardSyncPayloadFixture Create() =>
            new(Path.Combine(AppContext.BaseDirectory, "clipboard-sync-payload-tests", Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private static byte[] AddPngChunk(byte[] pngBytes, string chunkType, byte[] chunkData)
    {
        var typeBytes = Encoding.ASCII.GetBytes(chunkType);
        var insertAt = 8 + 4 + 4 + BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(8, 4)) + 4;
        using var output = new MemoryStream();
        output.Write(pngBytes, 0, insertAt);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, chunkData.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(chunkData);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, chunkData));
        output.Write(crc);
        output.Write(pngBytes, insertAt, pngBytes.Length - insertAt);
        return output.ToArray();
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static bool ContainsSequence(byte[] bytes, byte[] sequence)
    {
        for (var i = 0; i <= bytes.Length - sequence.Length; i++)
        {
            if (bytes.AsSpan(i, sequence.Length).SequenceEqual(sequence))
                return true;
        }

        return false;
    }

    private static byte[] CreateJpegBytes()
    {
        var pixels = new byte[] { 0x20, 0x40, 0x80, 0xFF };
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            4);
        bitmap.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
}
