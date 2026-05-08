using Microsoft.Data.Sqlite;
using Recents.App.Models;
using Recents.App.Services;
using Xunit;

namespace Recents.App.Tests;

public sealed class RecentRepositoryTests
{
    [Fact]
    public void UpsertFavorite_PersistsFavoriteAlias()
    {
        using var fixture = RepositoryFixture.Create();
        var repo = fixture.Repository;

        repo.UpsertFavorite(new RecentItem
        {
            NormalizedPath = @"C:\work\contract.docx",
            DisplayName = "contract.docx",
            Extension = ".docx",
            ClassificationSource = "Documents",
            RecentTime = DateTime.UtcNow,
            Exists = ExistsState.Found,
            IsFavorite = true,
            FavoriteTime = DateTime.UtcNow,
            FavoriteOrder = 1,
            Sources = SourceKinds.UserFolderWatch,
            LastSeenTime = DateTime.UtcNow,
            FavoriteAlias = "Client contract"
        });

        Assert.Equal("Client contract", Assert.Single(repo.LoadFavorites()).FavoriteAlias);

        repo.UpdateFavoriteAlias(@"C:\work\contract.docx", null);

        Assert.Null(Assert.Single(repo.LoadFavorites()).FavoriteAlias);
    }

    [Fact]
    public void Schema_AddsFavoriteAliasColumnToFavorites()
    {
        using var fixture = RepositoryFixture.Create();

        using var conn = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(favorites);";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("favorite_alias", columns);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _directory;
        public RecentRepository Repository { get; }
        public string DbPath { get; }

        private RepositoryFixture(string directory, string dbPath, RecentRepository repository)
        {
            _directory = directory;
            DbPath = dbPath;
            Repository = repository;
        }

        public static RepositoryFixture Create()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "recent-test-db", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "index.db");
            var repository = new RecentRepository(dbPath);
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
