using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Recents.App.Models;
using Serilog;

namespace Recents.App.Services.Clipboard;

internal sealed class ClipboardRepository : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _conn;

    public ClipboardRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    public void OpenDatabase()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            _conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            _conn.Open();
            EnsureSchema();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = cmd.ExecuteScalar()?.ToString();
            if (result != "ok")
                throw new InvalidDataException($"SQLite integrity_check: {result}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ClipboardRepository: database open failed, rebuilding");
            TryRebuild();
        }
    }

    private void TryRebuild()
    {
        _conn?.Dispose();
        _conn = null;
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            var bak = _dbPath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_dbPath, bak, overwrite: true);
        }

        _conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS schema_version (
              version INTEGER PRIMARY KEY,
              applied_utc TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_version (version, applied_utc)
            VALUES (1, datetime('now'));

            CREATE TABLE IF NOT EXISTS clipboard_items (
              id              TEXT PRIMARY KEY,
              type            TEXT NOT NULL,
              created_utc     TEXT NOT NULL,
              last_used_utc   TEXT,
              hash            TEXT NOT NULL,
              preview_text    TEXT,
              plain_text      TEXT,
              text_length     INTEGER,
              blob_path       TEXT,
              html_blob_path  TEXT,
              rtf_blob_path   TEXT,
              image_path      TEXT,
              thumbnail_path  TEXT,
              image_width     INTEGER,
              image_height    INTEGER,
              size_bytes      INTEGER,
              source_app_name TEXT,
              source_app_path TEXT,
              is_favorite     INTEGER NOT NULL DEFAULT 0,
              is_deleted      INTEGER NOT NULL DEFAULT 0,
              deleted_utc     TEXT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_clipboard_items_hash
                ON clipboard_items(hash) WHERE is_deleted = 0;
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_created
                ON clipboard_items(created_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_last_used
                ON clipboard_items(last_used_utc DESC, created_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_deleted
                ON clipboard_items(is_deleted, deleted_utc);

            CREATE TABLE IF NOT EXISTS clipboard_files (
              item_id   TEXT NOT NULL,
              ordinal   INTEGER NOT NULL,
              path      TEXT NOT NULL,
              is_folder INTEGER NOT NULL DEFAULT 0,
              exists_at_capture INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY (item_id, ordinal),
              FOREIGN KEY (item_id) REFERENCES clipboard_items(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS clipboard_favorites (
              id                TEXT PRIMARY KEY,
              original_item_id  TEXT,
              type              TEXT NOT NULL,
              created_utc       TEXT NOT NULL,
              source_created_utc TEXT,
              hash              TEXT NOT NULL,
              preview_text      TEXT,
              plain_text        TEXT,
              text_length       INTEGER,
              file_paths_json   TEXT,
              blob_path         TEXT,
              html_blob_path    TEXT,
              rtf_blob_path     TEXT,
              image_path        TEXT,
              thumbnail_path    TEXT,
              image_width       INTEGER,
              image_height      INTEGER,
              size_bytes        INTEGER,
              source_app_name   TEXT,
              source_app_path   TEXT,
              favorite_order    INTEGER NOT NULL DEFAULT 0,
              favorite_alias    TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_clipboard_favorites_order
                ON clipboard_favorites(favorite_order);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn("clipboard_favorites", "favorite_alias", "TEXT");
    }

    private void EnsureColumn(string tableName, string columnName, string definition)
    {
        using var check = _conn!.CreateCommand();
        check.CommandText = $"SELECT count(*) FROM pragma_table_info('{tableName}') WHERE name=$name;";
        check.Parameters.AddWithValue("$name", columnName);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0)
            return;

        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    public IReadOnlyList<ClipboardItem> LoadItems(int maxItems)
    {
        if (_conn is null) return Array.Empty<ClipboardItem>();
        var items = new List<ClipboardItem>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, type, created_utc, last_used_utc, hash, preview_text, plain_text, text_length,
                   blob_path, html_blob_path, rtf_blob_path, image_path, thumbnail_path,
                   image_width, image_height, size_bytes, source_app_name, source_app_path,
                   is_favorite, is_deleted, deleted_utc
            FROM clipboard_items
            WHERE is_deleted = 0
            ORDER BY COALESCE(last_used_utc, created_utc) DESC, created_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", maxItems);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(ReadItem(reader));

        foreach (var item in items)
            item.FilePaths = LoadFiles(item.Id);

        return items;
    }

    public IReadOnlyList<ClipboardFavoriteItem> LoadFavorites()
    {
        if (_conn is null) return Array.Empty<ClipboardFavoriteItem>();
        var favorites = new List<ClipboardFavoriteItem>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, original_item_id, type, created_utc, source_created_utc, hash,
                   preview_text, plain_text, text_length, file_paths_json,
                   blob_path, html_blob_path, rtf_blob_path, image_path, thumbnail_path,
                   image_width, image_height, size_bytes, source_app_name, source_app_path,
                   favorite_order, favorite_alias
            FROM clipboard_favorites
            ORDER BY favorite_order ASC, created_utc DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            favorites.Add(ReadFavorite(reader));
        return favorites;
    }

    public ClipboardItem? FindByHash(string hash)
    {
        if (_conn is null) return null;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, type, created_utc, last_used_utc, hash, preview_text, plain_text, text_length,
                   blob_path, html_blob_path, rtf_blob_path, image_path, thumbnail_path,
                   image_width, image_height, size_bytes, source_app_name, source_app_path,
                   is_favorite, is_deleted, deleted_utc
            FROM clipboard_items
            WHERE hash = $hash AND is_deleted = 0
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var item = ReadItem(reader);
        item.FilePaths = LoadFiles(item.Id);
        return item;
    }

    public void Upsert(ClipboardItem item)
    {
        if (_conn is null) return;
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO clipboard_items
                (id, type, created_utc, last_used_utc, hash, preview_text, plain_text, text_length,
                 blob_path, html_blob_path, rtf_blob_path, image_path, thumbnail_path,
                 image_width, image_height, size_bytes, source_app_name, source_app_path,
                 is_favorite, is_deleted, deleted_utc)
            VALUES
                ($id, $type, $created, $lastUsed, $hash, $preview, $plain, $textLength,
                 $blob, $htmlBlob, $rtfBlob, $image, $thumb,
                 $imageWidth, $imageHeight, $size, $sourceApp, $sourcePath,
                 $favorite, $deleted, $deletedUtc)
            ON CONFLICT(id) DO UPDATE SET
                 type = excluded.type,
                 created_utc = excluded.created_utc,
                 last_used_utc = excluded.last_used_utc,
                 hash = excluded.hash,
                 preview_text = excluded.preview_text,
                 plain_text = excluded.plain_text,
                 text_length = excluded.text_length,
                 blob_path = excluded.blob_path,
                 html_blob_path = excluded.html_blob_path,
                 rtf_blob_path = excluded.rtf_blob_path,
                 image_path = excluded.image_path,
                 thumbnail_path = excluded.thumbnail_path,
                 image_width = excluded.image_width,
                 image_height = excluded.image_height,
                 size_bytes = excluded.size_bytes,
                 source_app_name = excluded.source_app_name,
                 source_app_path = excluded.source_app_path,
                 is_favorite = excluded.is_favorite,
                 is_deleted = excluded.is_deleted,
                 deleted_utc = excluded.deleted_utc;
            """;
        AddItemParameters(cmd, item);
        cmd.ExecuteNonQuery();

        using var deleteFiles = _conn.CreateCommand();
        deleteFiles.Transaction = tx;
        deleteFiles.CommandText = "DELETE FROM clipboard_files WHERE item_id = $id;";
        deleteFiles.Parameters.AddWithValue("$id", item.Id);
        deleteFiles.ExecuteNonQuery();

        for (var i = 0; i < item.FilePaths.Count; i++)
        {
            using var fileCmd = _conn.CreateCommand();
            fileCmd.Transaction = tx;
            fileCmd.CommandText = """
                INSERT INTO clipboard_files (item_id, ordinal, path, is_folder, exists_at_capture)
                VALUES ($id, $ordinal, $path, $isFolder, $exists);
                """;
            fileCmd.Parameters.AddWithValue("$id", item.Id);
            fileCmd.Parameters.AddWithValue("$ordinal", i);
            fileCmd.Parameters.AddWithValue("$path", item.FilePaths[i].Path);
            fileCmd.Parameters.AddWithValue("$isFolder", item.FilePaths[i].IsFolder ? 1 : 0);
            fileCmd.Parameters.AddWithValue("$exists", item.FilePaths[i].ExistsAtCapture ? 1 : 0);
            fileCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void MarkUsed(string id, DateTime lastUsedUtc)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE clipboard_items
            SET last_used_utc = $lastUsed
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lastUsed", lastUsedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void MarkFavorite(string id, bool favorite)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clipboard_items SET is_favorite = $favorite WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void UpsertFavorite(ClipboardFavoriteItem item)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clipboard_favorites
                (id, original_item_id, type, created_utc, source_created_utc, hash,
                 preview_text, plain_text, text_length, file_paths_json,
                 blob_path, html_blob_path, rtf_blob_path, image_path, thumbnail_path,
                 image_width, image_height, size_bytes, source_app_name, source_app_path,
                 favorite_order, favorite_alias)
            VALUES
                ($id, $original, $type, $created, $sourceCreated, $hash,
                 $preview, $plain, $textLength, $files,
                 $blob, $htmlBlob, $rtfBlob, $image, $thumb,
                 $imageWidth, $imageHeight, $size, $sourceApp, $sourcePath,
                 $order, $alias)
            ON CONFLICT(id) DO UPDATE SET
                 original_item_id = excluded.original_item_id,
                 type = excluded.type,
                 created_utc = excluded.created_utc,
                 source_created_utc = excluded.source_created_utc,
                 hash = excluded.hash,
                 preview_text = excluded.preview_text,
                 plain_text = excluded.plain_text,
                 text_length = excluded.text_length,
                 file_paths_json = excluded.file_paths_json,
                 blob_path = excluded.blob_path,
                 html_blob_path = excluded.html_blob_path,
                 rtf_blob_path = excluded.rtf_blob_path,
                 image_path = excluded.image_path,
                 thumbnail_path = excluded.thumbnail_path,
                 image_width = excluded.image_width,
                 image_height = excluded.image_height,
                 size_bytes = excluded.size_bytes,
                 source_app_name = excluded.source_app_name,
                 source_app_path = excluded.source_app_path,
                 favorite_order = excluded.favorite_order,
                 favorite_alias = excluded.favorite_alias;
            """;
        AddFavoriteParameters(cmd, item);
        cmd.ExecuteNonQuery();
    }

    public void DeleteFavorite(string favoriteId)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clipboard_favorites WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", favoriteId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteFavoriteByOriginalItemId(string itemId)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clipboard_favorites WHERE original_item_id = $id;";
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateFavoriteOrder(string favoriteId, int order)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clipboard_favorites SET favorite_order = $order WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", favoriteId);
        cmd.Parameters.AddWithValue("$order", order);
        cmd.ExecuteNonQuery();
    }

    public void UpdateFavoriteAlias(string favoriteId, string? alias)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clipboard_favorites SET favorite_alias = $alias WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", favoriteId);
        cmd.Parameters.AddWithValue("$alias", alias ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SoftDelete(string id, DateTime deletedUtc)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE clipboard_items
            SET is_deleted = 1, deleted_utc = $deleted
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$deleted", deletedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void ClearHistory()
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clipboard_files; DELETE FROM clipboard_items;";
        cmd.ExecuteNonQuery();
    }

    public void CompactDeletedItems(DateTime cutoffUtc)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM clipboard_files
            WHERE item_id IN (
                SELECT id FROM clipboard_items
                WHERE is_deleted = 1 AND deleted_utc < $cutoff
            );
            DELETE FROM clipboard_items
            WHERE is_deleted = 1 AND deleted_utc < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> LoadRetainedBlobPaths(DateTime deletedCutoffUtc)
    {
        if (_conn is null) return Array.Empty<string>();

        var paths = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT blob_path, html_blob_path, rtf_blob_path, image_path, thumbnail_path
            FROM clipboard_items
            WHERE is_deleted = 0
               OR deleted_utc IS NULL
               OR deleted_utc >= $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", deletedCutoffUtc.ToString("O"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.IsDBNull(i))
                    paths.Add(reader.GetString(i));
            }
        }

        return paths;
    }

    public IReadOnlyList<string> SoftDeleteOverflowAndExpired(int maxItems, DateTime retentionCutoffUtc, DateTime deletedUtc)
    {
        if (_conn is null) return Array.Empty<string>();

        maxItems = Math.Max(1, maxItems);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var expired = _conn.CreateCommand();
        expired.CommandText = """
            SELECT id
            FROM clipboard_items
            WHERE is_deleted = 0
              AND is_favorite = 0
              AND COALESCE(last_used_utc, created_utc) < $cutoff;
            """;
        expired.Parameters.AddWithValue("$cutoff", retentionCutoffUtc.ToString("O"));
        using (var reader = expired.ExecuteReader())
        {
            while (reader.Read())
                ids.Add(reader.GetString(0));
        }

        using var overflow = _conn.CreateCommand();
        overflow.CommandText = """
            SELECT id
            FROM clipboard_items
            WHERE is_deleted = 0
              AND is_favorite = 0
            ORDER BY COALESCE(last_used_utc, created_utc) DESC, created_utc DESC
            LIMIT -1 OFFSET $max;
            """;
        overflow.Parameters.AddWithValue("$max", maxItems);
        using (var reader = overflow.ExecuteReader())
        {
            while (reader.Read())
                ids.Add(reader.GetString(0));
        }

        if (ids.Count == 0)
            return Array.Empty<string>();

        using var tx = _conn.BeginTransaction();
        foreach (var id in ids)
        {
            using var update = _conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE clipboard_items
                SET is_deleted = 1,
                    deleted_utc = $deleted
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$deleted", deletedUtc.ToString("O"));
            update.ExecuteNonQuery();
        }
        tx.Commit();

        return ids.ToList();
    }

    private List<ClipboardFilePath> LoadFiles(string id)
    {
        var files = new List<ClipboardFilePath>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT path, is_folder, exists_at_capture
            FROM clipboard_files
            WHERE item_id = $id
            ORDER BY ordinal ASC;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(new ClipboardFilePath
            {
                Path = reader.GetString(0),
                IsFolder = reader.GetInt32(1) != 0,
                ExistsAtCapture = reader.GetInt32(2) != 0
            });
        }

        return files;
    }

    private static ClipboardItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Type = Enum.TryParse<ClipboardPayloadType>(r.GetString(1), out var type) ? type : ClipboardPayloadType.Unknown,
        CreatedUtc = DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastUsedUtc = r.IsDBNull(3) ? DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind) : DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Hash = r.GetString(4),
        PreviewText = r.IsDBNull(5) ? string.Empty : r.GetString(5),
        PlainText = r.IsDBNull(6) ? null : r.GetString(6),
        TextLength = r.IsDBNull(7) ? null : r.GetInt32(7),
        BlobPath = r.IsDBNull(8) ? null : r.GetString(8),
        HtmlBlobPath = r.IsDBNull(9) ? null : r.GetString(9),
        RtfBlobPath = r.IsDBNull(10) ? null : r.GetString(10),
        ImagePath = r.IsDBNull(11) ? null : r.GetString(11),
        ThumbnailPath = r.IsDBNull(12) ? null : r.GetString(12),
        ImageWidth = r.IsDBNull(13) ? null : r.GetInt32(13),
        ImageHeight = r.IsDBNull(14) ? null : r.GetInt32(14),
        SizeBytes = r.IsDBNull(15) ? null : r.GetInt64(15),
        SourceAppName = r.IsDBNull(16) ? null : r.GetString(16),
        SourceAppPath = r.IsDBNull(17) ? null : r.GetString(17),
        IsFavorite = r.GetInt32(18) != 0,
        IsDeleted = r.GetInt32(19) != 0,
        DeletedUtc = r.IsDBNull(20) ? null : DateTime.Parse(r.GetString(20), null, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private static ClipboardFavoriteItem ReadFavorite(SqliteDataReader r)
    {
        var filesJson = r.IsDBNull(9) ? null : r.GetString(9);
        return new ClipboardFavoriteItem
        {
            Id = r.GetString(0),
            OriginalItemId = r.IsDBNull(1) ? null : r.GetString(1),
            Type = Enum.TryParse<ClipboardPayloadType>(r.GetString(2), out var type) ? type : ClipboardPayloadType.Unknown,
            CreatedUtc = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            SourceCreatedUtc = r.IsDBNull(4) ? DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind) : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Hash = r.GetString(5),
            PreviewText = r.IsDBNull(6) ? string.Empty : r.GetString(6),
            PlainText = r.IsDBNull(7) ? null : r.GetString(7),
            TextLength = r.IsDBNull(8) ? null : r.GetInt32(8),
            FilePaths = string.IsNullOrWhiteSpace(filesJson)
                ? new List<ClipboardFilePath>()
                : JsonSerializer.Deserialize<List<ClipboardFilePath>>(filesJson) ?? new List<ClipboardFilePath>(),
            BlobPath = r.IsDBNull(10) ? null : r.GetString(10),
            HtmlBlobPath = r.IsDBNull(11) ? null : r.GetString(11),
            RtfBlobPath = r.IsDBNull(12) ? null : r.GetString(12),
            ImagePath = r.IsDBNull(13) ? null : r.GetString(13),
            ThumbnailPath = r.IsDBNull(14) ? null : r.GetString(14),
            ImageWidth = r.IsDBNull(15) ? null : r.GetInt32(15),
            ImageHeight = r.IsDBNull(16) ? null : r.GetInt32(16),
            SizeBytes = r.IsDBNull(17) ? null : r.GetInt64(17),
            SourceAppName = r.IsDBNull(18) ? null : r.GetString(18),
            SourceAppPath = r.IsDBNull(19) ? null : r.GetString(19),
            FavoriteOrder = r.GetInt32(20),
            FavoriteAlias = r.FieldCount > 21 && !r.IsDBNull(21) ? r.GetString(21) : null,
        };
    }

    private static void AddItemParameters(SqliteCommand cmd, ClipboardItem item)
    {
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$type", item.Type.ToString());
        cmd.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$lastUsed", item.LastUsedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$preview", item.PreviewText);
        cmd.Parameters.AddWithValue("$plain", item.PlainText ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$textLength", item.TextLength ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$blob", item.BlobPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$htmlBlob", item.HtmlBlobPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rtfBlob", item.RtfBlobPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$image", item.ImagePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", item.ThumbnailPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$imageWidth", item.ImageWidth ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$imageHeight", item.ImageHeight ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$size", item.SizeBytes ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$sourceApp", item.SourceAppName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$sourcePath", item.SourceAppPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$favorite", item.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted", item.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$deletedUtc", item.DeletedUtc?.ToString("O") ?? (object)DBNull.Value);
    }

    private static void AddFavoriteParameters(SqliteCommand cmd, ClipboardFavoriteItem item)
    {
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$original", item.OriginalItemId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$type", item.Type.ToString());
        cmd.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$sourceCreated", item.SourceCreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$preview", item.PreviewText);
        cmd.Parameters.AddWithValue("$plain", item.PlainText ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$textLength", item.TextLength ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$files", JsonSerializer.Serialize(item.FilePaths));
        cmd.Parameters.AddWithValue("$blob", item.BlobPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$htmlBlob", item.HtmlBlobPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rtfBlob", item.RtfBlobPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$image", item.ImagePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", item.ThumbnailPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$imageWidth", item.ImageWidth ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$imageHeight", item.ImageHeight ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$size", item.SizeBytes ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$sourceApp", item.SourceAppName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$sourcePath", item.SourceAppPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$order", item.FavoriteOrder);
        cmd.Parameters.AddWithValue("$alias", item.FavoriteAlias ?? (object)DBNull.Value);
    }

    public void Dispose() => _conn?.Dispose();
}
