using Recents.App.Models;
using Recents.App.Services.ClipboardSync;
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
}
