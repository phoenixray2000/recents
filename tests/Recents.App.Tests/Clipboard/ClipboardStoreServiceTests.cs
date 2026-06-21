using System.IO;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Clipboard;
using Recents.App.ViewModels;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardStoreServiceTests
{
    [Fact]
    public async Task IngestAsync_DeduplicatesByHashAndMovesExistingItem()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var first = NewTextItem("first", "same-hash", DateTime.UtcNow.AddMinutes(-5));
        var duplicate = NewTextItem("duplicate", "same-hash", DateTime.UtcNow);

        await fixture.Store.IngestAsync(first);
        await fixture.Store.IngestAsync(duplicate);

        var vm = Assert.Single(fixture.Store.Items);
        Assert.Equal("first", vm.Item.Id);
        Assert.True(vm.Item.LastUsedUtc > first.CreatedUtc);
    }

    [Fact]
    public async Task IngestAsync_PrunesOverflowButKeepsFavorites()
    {
        using var fixture = ClipboardStoreFixture.Create(maxItems: 1);
        var favorite = NewTextItem("favorite", "hash-favorite", DateTime.UtcNow.AddMinutes(-20));
        var old = NewTextItem("old", "hash-old", DateTime.UtcNow.AddMinutes(-10));
        var newest = NewTextItem("newest", "hash-newest", DateTime.UtcNow);

        await fixture.Store.IngestAsync(favorite);
        await fixture.Store.AddToFavoritesAsync(favorite.Id);
        await fixture.Store.IngestAsync(old);
        await fixture.Store.IngestAsync(newest);

        Assert.Contains(fixture.Store.Items, vm => vm.Item.Id == "favorite");
        Assert.Contains(fixture.Store.Items, vm => vm.Item.Id == "newest");
        Assert.DoesNotContain(fixture.Store.Items, vm => vm.Item.Id == "old");
    }

    [Fact]
    public async Task AddToFavoritesAsync_DoesNotDuplicateFavoriteWithSameClipboardHash()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var first = NewTextItem("first", "same-hash", DateTime.UtcNow.AddMinutes(-5));
        var replacement = NewTextItem("replacement", "same-hash", DateTime.UtcNow);

        await fixture.Store.IngestAsync(first);
        await fixture.Store.AddToFavoritesAsync(first.Id);
        await fixture.Store.DeleteAsync(first.Id);
        await fixture.Store.IngestAsync(replacement);
        await fixture.Store.AddToFavoritesAsync(replacement.Id);

        Assert.Single(fixture.Store.Favorites);
        var replacementVm = Assert.Single(fixture.Store.Items);
        Assert.Equal("replacement", replacementVm.Item.Id);
        Assert.True(replacementVm.Item.IsFavorite);

        await fixture.Store.ToggleFavoriteAsync(replacement.Id);

        Assert.Empty(fixture.Store.Favorites);
        Assert.False(replacementVm.Item.IsFavorite);
    }

    [Theory]
    [InlineData(ClipboardPayloadType.Image, "Images", true)]
    [InlineData(ClipboardPayloadType.Text, "Text", true)]
    [InlineData(ClipboardPayloadType.Text, "Images", false)]
    [InlineData(ClipboardPayloadType.Image, "Text", false)]
    [InlineData(ClipboardPayloadType.Html, "All", true)]
    public void ClipboardTypeMatchesFilter_MapsSubFilterTagsToPayloadTypes(
        ClipboardPayloadType type,
        string subFilter,
        bool expected)
    {
        Assert.Equal(expected, MainViewModel.ClipboardTypeMatchesFilter(type, subFilter));
    }

    [Fact]
    public void Store_CreatesManagedFilesDirectories()
    {
        using var fixture = ClipboardStoreFixture.Create();
        Assert.True(Directory.Exists(fixture.Store.FilesDirectory));
        Assert.True(Directory.Exists(fixture.Store.FavoriteFilesDirectory));
        Assert.EndsWith(Path.Combine("data", "files"), fixture.Store.FilesDirectory.TrimEnd(Path.DirectorySeparatorChar));
        Assert.EndsWith(Path.Combine("favorites", "files"), fixture.Store.FavoriteFilesDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Compact_KeepsReferencedFilesTrees_DeletesUnreferencedPastGrace_KeepsRecent_RemovesEmptyPastGrace_KeepsEmptyRecent()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        // Referenced subtree: belongs to a live item.
        var refSub = Path.Combine(store.FilesDirectory, "referenced");
        Directory.CreateDirectory(refSub);
        var refFile = Path.Combine(refSub, "keep.txt");
        await File.WriteAllTextAsync(refFile, "keep");

        var item = new ClipboardItem
        {
            Id = "files-item", Type = ClipboardPayloadType.Files,
            Hash = "files-hash", CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "keep.txt", PlainText = refFile,
            FilePaths = [new ClipboardFilePath { Path = refFile, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);

        // Unreferenced + OLD subtree (past 1-day grace): must be deleted.
        var oldSub = Path.Combine(store.FilesDirectory, "orphan-old");
        Directory.CreateDirectory(oldSub);
        var oldFile = Path.Combine(oldSub, "old.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        Directory.SetLastWriteTimeUtc(oldSub, DateTime.UtcNow.AddDays(-3));

        // Unreferenced + RECENT subtree (within grace): must survive.
        var recentSub = Path.Combine(store.FilesDirectory, "orphan-recent");
        Directory.CreateDirectory(recentSub);
        var recentFile = Path.Combine(recentSub, "recent.txt");
        await File.WriteAllTextAsync(recentFile, "recent");

        // Empty + OLD subdir (past grace): must be removed (spec §6.4 — empty subdirs removed after grace).
        var emptyOldSub = Path.Combine(store.FilesDirectory, "empty-old");
        Directory.CreateDirectory(emptyOldSub);
        Directory.SetLastWriteTimeUtc(emptyOldSub, DateTime.UtcNow.AddDays(-3));

        // Empty + RECENT subdir (C2): an in-progress import does CreateDirectory THEN populates;
        // a recent empty dir must be KEPT so compaction never races and deletes a live import dir.
        var emptyRecentSub = Path.Combine(store.FilesDirectory, "empty-recent");
        Directory.CreateDirectory(emptyRecentSub);

        await store.CompactOrphanBlobsAsync();

        Assert.True(Directory.Exists(refSub), "referenced subtree must be kept");
        Assert.True(File.Exists(refFile));
        Assert.False(Directory.Exists(oldSub), "old unreferenced subtree past grace must be deleted");
        Assert.True(Directory.Exists(recentSub), "recent unreferenced subtree within grace must be kept");
        Assert.False(Directory.Exists(emptyOldSub), "empty subdir past grace must be removed (spec §6.4)");
        Assert.True(Directory.Exists(emptyRecentSub), "recent empty subdir (in-progress import) must be kept (C2 race closed)");
    }

    [Fact]
    public async Task Compact_NeverDeletesUserRealPathFiles()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        // A local file drop referencing the user's real path (OUTSIDE FilesDirectory).
        var userDir = Path.Combine(AppContext.BaseDirectory, "user-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        var userFile = Path.Combine(userDir, "mydoc.txt");
        await File.WriteAllTextAsync(userFile, "user content");

        var item = new ClipboardItem
        {
            Id = "local-files", Type = ClipboardPayloadType.Files,
            Hash = "local-hash", CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "mydoc.txt", PlainText = userFile,
            FilePaths = [new ClipboardFilePath { Path = userFile, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);

        await store.CompactOrphanBlobsAsync();

        Assert.True(File.Exists(userFile), "user real path must never be touched by reconciliation");
        try { Directory.Delete(userDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Compact_KeepsRecentlyImportedUnreferencedImageWithinGrace()
    {
        // M2: an imported not-saved image lands in images/ unreferenced; the clipboard apply
        // references ImagePath until the next clipboard change. The 1-day mtime grace must keep it.
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var orphanImg = Path.Combine(store.ImageDirectory, "just-imported.png");
        await File.WriteAllBytesAsync(orphanImg, new byte[] { 1, 2, 3 }); // mtime = now

        await store.CompactOrphanBlobsAsync();

        Assert.True(File.Exists(orphanImg), "recently-imported unreferenced image must survive within the grace window (M2)");
    }

    private static ClipboardItem NewTextItem(string id, string hash, DateTime createdUtc) => new()
    {
        Id = id,
        Type = ClipboardPayloadType.Text,
        CreatedUtc = createdUtc,
        LastUsedUtc = createdUtc,
        Hash = hash,
        PreviewText = id,
        PlainText = id,
    };

    [Fact]
    public async Task ClearHistory_WipesManagedFiles_KeepsFavoriteFiles()
    {
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
        await store.AddToFavoritesAsync("imp");
        var favPath = Assert.Single(Assert.Single(store.Favorites).Item.FilePaths).Path;

        await store.ClearHistoryAsync();

        Assert.False(File.Exists(src), "managed files/ content must be wiped");
        Assert.True(File.Exists(favPath), "favorite file copy must survive ClearHistory");
    }

    private sealed class ClipboardStoreFixture : IDisposable
    {
        private readonly string _directory;
        public ClipboardStoreService Store { get; }

        private ClipboardStoreFixture(string directory, ClipboardStoreService store)
        {
            _directory = directory;
            Store = store;
        }

        public static ClipboardStoreFixture Create(int maxItems = 500)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-store-tests", Guid.NewGuid().ToString("N"));
            var settings = new SettingsService();
            settings.Current.MaxClipboardItems = maxItems;
            settings.Current.ClipboardRetentionDays = 365;
            var store = new ClipboardStoreService(settings, Path.Combine(directory, "data"), Path.Combine(directory, "clipboard.db"));
            var actions = new ClipboardActionService(store);
            store.AttachActions(actions);
            store.OpenDatabase();
            return new ClipboardStoreFixture(directory, store);
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
