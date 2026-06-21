using System.Text;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardFavoritesTests
{
    [Fact]
    public async Task FavoriteSnapshot_RemainsUsableAfterSourceItemAndBlobAreDeleted()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;
        var sourceBlob = Path.Combine(store.BlobDirectory, "source.txt");
        File.WriteAllText(sourceBlob, "snapshot text", Encoding.UTF8);
        var item = NewTextItem("source", "hash-source", sourceBlob);

        await store.IngestAsync(item);
        await store.AddToFavoritesAsync(item.Id);

        var favorite = Assert.Single(store.Favorites).Item;
        Assert.NotEqual(sourceBlob, favorite.BlobPath);
        Assert.True(File.Exists(favorite.BlobPath));

        await store.DeleteAsync(item.Id);
        File.Delete(sourceBlob);

        Assert.Empty(store.Items);
        Assert.True(fixture.Actions.HasUsableContent(favorite.ToClipboardItem()));
        Assert.True(File.Exists(favorite.BlobPath));
    }

    [Fact]
    public async Task LoadFromDatabase_RestoresMissingImageFavoriteSnapshotFromSourceItem()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;
        var sourceImage = Path.Combine(store.ImageDirectory, "source.png");
        var sourceThumb = Path.Combine(store.ThumbnailDirectory, "source.jpg");
        File.WriteAllBytes(sourceImage, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(sourceThumb, new byte[] { 4, 5, 6 });
        var item = NewImageItem("image-source", "hash-image", sourceImage, sourceThumb);

        await store.IngestAsync(item);
        await store.AddToFavoritesAsync(item.Id);

        var brokenFavorite = Assert.Single(store.Favorites).Item;
        File.Delete(brokenFavorite.ImagePath!);
        File.Delete(brokenFavorite.ThumbnailPath!);

        Assert.False(fixture.Actions.HasUsableContent(brokenFavorite.ToClipboardItem()));

        store.LoadFromDatabase();

        var restoredFavorite = Assert.Single(store.Favorites).Item;
        Assert.True(File.Exists(restoredFavorite.ImagePath));
        Assert.True(File.Exists(restoredFavorite.ThumbnailPath));
        Assert.True(fixture.Actions.HasUsableContent(restoredFavorite.ToClipboardItem()));
    }

    [Fact]
    public async Task Favorite_OfManagedFileDrop_CopiesIntoFavoriteFilesDirectory()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "imported content");

        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt", PlainText = src,
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("imp");

        var fav = Assert.Single(store.Favorites);
        var favPath = Assert.Single(fav.Item.FilePaths).Path;
        Assert.StartsWith(store.FavoriteFilesDirectory, Path.GetFullPath(favPath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(favPath));
        Assert.Equal("imported content", await File.ReadAllTextAsync(favPath));
    }

    [Fact]
    public async Task Favorite_OfLocalFileDrop_KeepsUserRealPathAsMetadata()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var userDir = Path.Combine(AppContext.BaseDirectory, "user-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        var userFile = Path.Combine(userDir, "real.txt");
        await File.WriteAllTextAsync(userFile, "real");

        var item = new ClipboardItem
        {
            Id = "loc", Type = ClipboardPayloadType.Files, Hash = "loc-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "real.txt", PlainText = userFile,
            FilePaths = [new ClipboardFilePath { Path = userFile, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("loc");

        var fav = Assert.Single(store.Favorites);
        Assert.Equal(userFile, Assert.Single(fav.Item.FilePaths).Path); // unchanged metadata
        try { Directory.Delete(userDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Favorite_OfManagedFileDrop_OnCopyFailure_DoesNotReferenceSourceManagedPath()
    {
        // M6: simulate a copy failure (source path under FilesDirectory but file removed before copy)
        // and assert the favorite does NOT end up pointing at the source managed path.
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "x");

        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt", PlainText = src,
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);

        // Delete the source so the copy fails.
        File.Delete(src);

        await store.AddToFavoritesAsync("imp");

        var fav = Assert.Single(store.Favorites);
        var favFilePaths = fav.Item.FilePaths;

        // R2-5: index alignment is preserved — the failed entry is a placeholder, not omitted.
        Assert.Single(favFilePaths);

        var fullSrc = Path.GetFullPath(src);
        foreach (var f in favFilePaths)
        {
            // R2-5: guard against the empty placeholder path before Path.GetFullPath.
            if (string.IsNullOrWhiteSpace(f.Path))
                continue; // placeholder — acceptable; it references nothing
            var full = Path.GetFullPath(f.Path);
            // M6: must NOT reference the source managed path.
            Assert.NotEqual(fullSrc, full, StringComparer.OrdinalIgnoreCase);
            // Any non-empty path must live under FavoriteFilesDirectory (never the source files/ path).
            Assert.StartsWith(store.FavoriteFilesDirectory, full, StringComparison.OrdinalIgnoreCase);
        }

        // R2-5: an all-empty (all-failed) Files favorite has no usable content.
        Assert.False(fixture.Actions.HasUsableContent(fav.Item.ToClipboardItem()),
            "a Files favorite whose only entry is an empty placeholder must report no usable content");
    }

    [Fact]
    public async Task Favorite_OfMixedManagedDrop_PreservesIndexAlignment_WhenOneCopyFails()
    {
        // R2-5: a Files favorite with a mix of empty (failed) + real (copied) paths stays
        // 1:1 with source.FilePaths and HasUsableContent is true (the real path exists).
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var good = Path.Combine(sub, "good.txt");
        var bad = Path.Combine(sub, "bad.txt");
        await File.WriteAllTextAsync(good, "good");
        await File.WriteAllTextAsync(bad, "bad");

        var item = new ClipboardItem
        {
            Id = "mix", Type = ClipboardPayloadType.Files, Hash = "mix-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "mix", PlainText = good,
            FilePaths =
            [
                new ClipboardFilePath { Path = good, ExistsAtCapture = true },
                new ClipboardFilePath { Path = bad, ExistsAtCapture = true }
            ]
        };
        await store.IngestAsync(item);

        // Make only the second copy fail.
        File.Delete(bad);

        await store.AddToFavoritesAsync("mix");

        var fav = Assert.Single(store.Favorites);
        Assert.Equal(2, fav.Item.FilePaths.Count); // R2-5: index-aligned 1:1 with source (2 entries)
        Assert.Contains(fav.Item.FilePaths, f => !string.IsNullOrWhiteSpace(f.Path)
            && Path.GetFullPath(f.Path).StartsWith(Path.GetFullPath(store.FavoriteFilesDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fav.Item.FilePaths, f => string.IsNullOrWhiteSpace(f.Path)); // the failed entry is an empty placeholder
        Assert.True(fixture.Actions.HasUsableContent(fav.Item.ToClipboardItem()),
            "a Files favorite with at least one real copied path still has usable content");
    }

    [Fact]
    public async Task Favorite_OfManagedFileDrop_DataObjectText_DoesNotLeakSourceManagedPath()
    {
        // R3-1: a Files favorite's PlainText (emitted as clipboard text by CreateDataObject) must
        // reflect the rewritten favorites/files/ path, NOT the source files/ path (which dies on
        // prune/ClearHistory). For a Files item, source.PlainText is the newline-joined source paths.
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "imported content");

        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt",
            PlainText = src, // for a Files item, PlainText is the (source) file path(s)
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("imp");

        var fav = Assert.Single(store.Favorites);
        var favItem = fav.Item.ToClipboardItem();

        // The favorite copy lives under FavoriteFilesDirectory.
        var favPath = Assert.Single(favItem.FilePaths).Path;
        Assert.StartsWith(store.FavoriteFilesDirectory, Path.GetFullPath(favPath), StringComparison.OrdinalIgnoreCase);

        // The data object's text formats must NOT contain the source FilesDirectory path...
        var data = fixture.Actions.CreateDataObject(favItem);
        var text = data.GetDataPresent(System.Windows.DataFormats.UnicodeText)
            ? (string?)data.GetData(System.Windows.DataFormats.UnicodeText)
            : null;
        Assert.NotNull(text);
        Assert.DoesNotContain(Path.GetFullPath(store.FilesDirectory), Path.GetFullPath(text!), StringComparison.OrdinalIgnoreCase);
        // ...and DO reflect the rewritten favorites/files/ path.
        Assert.Contains(Path.GetFullPath(favPath), Path.GetFullPath(text!), StringComparison.OrdinalIgnoreCase);
    }

    private static ClipboardItem NewTextItem(string id, string hash, string blobPath) => new()
    {
        Id = id,
        Type = ClipboardPayloadType.Text,
        CreatedUtc = DateTime.UtcNow,
        LastUsedUtc = DateTime.UtcNow,
        Hash = hash,
        PreviewText = id,
        PlainText = "snapshot text",
        BlobPath = blobPath,
    };

    private static ClipboardItem NewImageItem(string id, string hash, string imagePath, string thumbnailPath) => new()
    {
        Id = id,
        Type = ClipboardPayloadType.Image,
        CreatedUtc = DateTime.UtcNow,
        LastUsedUtc = DateTime.UtcNow,
        Hash = hash,
        PreviewText = "image",
        ImagePath = imagePath,
        ThumbnailPath = thumbnailPath,
        ImageWidth = 10,
        ImageHeight = 10,
        SizeBytes = 3,
    };

    private sealed class ClipboardStoreFixture : IDisposable
    {
        private readonly string _directory;
        public ClipboardStoreService Store { get; }
        public ClipboardActionService Actions { get; }

        private ClipboardStoreFixture(string directory, ClipboardStoreService store, ClipboardActionService actions)
        {
            _directory = directory;
            Store = store;
            Actions = actions;
        }

        public static ClipboardStoreFixture Create()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-favorite-tests", Guid.NewGuid().ToString("N"));
            var settings = new SettingsService();
            var store = new ClipboardStoreService(settings, Path.Combine(directory, "data"), Path.Combine(directory, "clipboard.db"));
            var actions = new ClipboardActionService(store);
            store.AttachActions(actions);
            store.OpenDatabase();
            return new ClipboardStoreFixture(directory, store, actions);
        }

        public void Dispose()
        {
            Store.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
