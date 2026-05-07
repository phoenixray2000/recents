using Recents.App.Models;
using Recents.App.Services.Clipboard;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardRepositoryTests
{
    [Fact]
    public void LoadRetainedBlobPaths_KeepsRecentlyDeletedBlobsOnly()
    {
        using var fixture = RepositoryFixture.Create();
        var repo = fixture.Repository;
        var now = DateTime.UtcNow;

        repo.Upsert(NewItem("active", now.AddMinutes(-3), "hash-active", "active.txt"));
        repo.Upsert(NewItem("recently-deleted", now.AddMinutes(-2), "hash-recent", "recent.txt"));
        repo.Upsert(NewItem("expired-deleted", now.AddMinutes(-1), "hash-expired", "expired.txt"));
        repo.SoftDelete("recently-deleted", now.AddHours(-1));
        repo.SoftDelete("expired-deleted", now.AddHours(-25));

        var retained = repo.LoadRetainedBlobPaths(now.AddHours(-24));

        Assert.Contains(retained, p => p.EndsWith("active.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(retained, p => p.EndsWith("recent.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(retained, p => p.EndsWith("expired.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SoftDeleteOverflowAndExpired_DoesNotDeleteFavorites()
    {
        using var fixture = RepositoryFixture.Create();
        var repo = fixture.Repository;
        var now = DateTime.UtcNow;

        var favorite = NewItem("favorite", now.AddDays(-90), "hash-favorite", "favorite.txt");
        favorite.IsFavorite = true;
        repo.Upsert(favorite);
        repo.Upsert(NewItem("old", now.AddDays(-90), "hash-old", "old.txt"));
        repo.Upsert(NewItem("new", now, "hash-new", "new.txt"));

        var deleted = repo.SoftDeleteOverflowAndExpired(1, now.AddDays(-30), now);

        Assert.DoesNotContain("favorite", deleted);
        Assert.Contains("old", deleted);
        Assert.Contains("new", repo.LoadItems(10).Select(i => i.Id));
    }

    [Fact]
    public void LoadItems_OrdersByLastUsedUtcBeforeCreatedUtc()
    {
        using var fixture = RepositoryFixture.Create();
        var repo = fixture.Repository;
        var now = DateTime.UtcNow;

        var olderButUsed = NewItem("older-used", now.AddHours(-4), "hash-older-used", "older-used.txt");
        olderButUsed.LastUsedUtc = now;
        repo.Upsert(NewItem("newer-created", now.AddHours(-1), "hash-newer-created", "newer-created.txt"));
        repo.Upsert(olderButUsed);

        var items = repo.LoadItems(10);

        Assert.Equal("older-used", items[0].Id);
    }

    [Fact]
    public void SoftDeleteOverflowAndExpired_KeepsRecentlyUsedItems()
    {
        using var fixture = RepositoryFixture.Create();
        var repo = fixture.Repository;
        var now = DateTime.UtcNow;

        var oldestButUsed = NewItem("oldest-used", now.AddDays(-90), "hash-oldest-used", "oldest-used.txt");
        oldestButUsed.LastUsedUtc = now;
        repo.Upsert(oldestButUsed);
        repo.Upsert(NewItem("middle", now.AddHours(-3), "hash-middle", "middle.txt"));
        repo.Upsert(NewItem("newest", now.AddHours(-1), "hash-newest", "newest.txt"));

        var deleted = repo.SoftDeleteOverflowAndExpired(2, now.AddDays(-30), now);

        Assert.DoesNotContain("oldest-used", deleted);
        Assert.Contains("middle", deleted);
    }

    [Fact]
    public void Schema_DoesNotCreateUseCountColumn()
    {
        using var fixture = RepositoryFixture.Create();

        using var conn = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(clipboard_items);";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.DoesNotContain("use_count", columns);
    }

    [Fact]
    public void Schema_WritesInitialSchemaVersion()
    {
        using var fixture = RepositoryFixture.Create();

        using var conn = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version ORDER BY version;";

        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void ClearHistory_DoesNotDeleteFavorites()
    {
        using var fixture = RepositoryFixture.Create();
        var repo = fixture.Repository;
        var now = DateTime.UtcNow;

        repo.Upsert(NewItem("source", now, "hash-source", "source.txt"));
        repo.UpsertFavorite(new ClipboardFavoriteItem
        {
            Id = "favorite",
            OriginalItemId = "source",
            Type = ClipboardPayloadType.Text,
            CreatedUtc = now.AddMinutes(1),
            SourceCreatedUtc = now,
            Hash = "hash-source",
            PreviewText = "source",
            PlainText = "source",
            BlobPath = Path.Combine("clipboard", "favorites", "blobs", "source.txt"),
            FavoriteOrder = 1
        });

        repo.ClearHistory();

        Assert.Empty(repo.LoadItems(10));
        Assert.Single(repo.LoadFavorites());
    }

    private static ClipboardItem NewItem(string id, DateTime createdUtc, string hash, string blobName) => new()
    {
        Id = id,
        Type = ClipboardPayloadType.Text,
        CreatedUtc = createdUtc,
        LastUsedUtc = createdUtc,
        Hash = hash,
        PreviewText = id,
        PlainText = id,
        BlobPath = Path.Combine("clipboard", "blobs", blobName),
    };

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _directory;
        public ClipboardRepository Repository { get; }
        public string DbPath { get; }

        private RepositoryFixture(string directory, string dbPath, ClipboardRepository repository)
        {
            _directory = directory;
            DbPath = dbPath;
            Repository = repository;
        }

        public static RepositoryFixture Create()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-test-db", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "clipboard.db");
            var repository = new ClipboardRepository(dbPath);
            repository.OpenDatabase();
            return new RepositoryFixture(directory, dbPath, repository);
        }

        public void Dispose()
        {
            Repository.Dispose();
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
