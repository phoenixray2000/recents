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
