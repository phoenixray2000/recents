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
