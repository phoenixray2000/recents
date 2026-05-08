using System.IO;
using Microsoft.Data.Sqlite;
using Recents.App.Models;
using Serilog;

namespace Recents.App.Services;

// PRD §6.18 SQLite 持久化层（从 RecentIndexService 拆出，遵循 Global Rule 单文件 ≤400 行）
// 职责：连接管理、Schema 建立（含旧列检测）、增删改查。
// 不含业务逻辑：融合去重、内存索引、UI 通知均在 RecentIndexService 处理。
internal sealed class RecentRepository : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _conn;

    public RecentRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    // 打开连接 + Schema 保证（幂等）；损坏时自动备份重建
    public void OpenDatabase()
    {
        try
        {
            TryOpenOrRebuild();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecentRepository: 数据库打开失败，尝试重建");
            TryRebuild();
        }
    }

    private void TryOpenOrRebuild()
    {
        _conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        _conn.Open();
        EnsureSchema();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar()?.ToString();
        if (result != "ok")
            throw new InvalidDataException($"SQLite integrity_check: {result}");
        Log.Information("RecentRepository: 数据库打开成功 {Path}", LogPrivacy.Format(_dbPath));
    }

    private void TryRebuild()
    {
        _conn?.Dispose();
        _conn = null;
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(_dbPath))
        {
            var bak = _dbPath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_dbPath, bak, overwrite: true);
            Log.Warning("RecentRepository: 损坏的数据库已备份到 {Bak}", LogPrivacy.Format(bak));
        }
        _conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        _conn.Open();
        EnsureSchema();
    }

    // PRD §6.18 Schema —— 字段 category_source（Global Rule：DB 字段统一用 category_source）
    // 若检测到旧版 file_type 列，直接删表重建（PRD §15 开发阶段不考虑迁移）
    private void EnsureSchema()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('recent_items') WHERE name='file_type';";
        var hasOldColumn = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (hasOldColumn)
        {
            Log.Warning("RecentRepository: 检测到旧版 file_type 列，正在重建表...");
            cmd.CommandText = "DROP TABLE IF EXISTS recent_items;";
            cmd.ExecuteNonQuery();
        }

        // 迁移：给 favorites 表补 icon_png 列（旧版 DB 缺少此列）
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('favorites') WHERE name='icon_png';";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            cmd.CommandText = "ALTER TABLE favorites ADD COLUMN icon_png BLOB;";
            try { cmd.ExecuteNonQuery(); } catch { /* table may not exist yet, will be created below */ }
        }

        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('favorites') WHERE name='favorite_alias';";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            cmd.CommandText = "ALTER TABLE favorites ADD COLUMN favorite_alias TEXT;";
            try { cmd.ExecuteNonQuery(); } catch { /* table may not exist yet, will be created below */ }
        }

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS recent_items (
                normalized_path      TEXT PRIMARY KEY,
                display_name         TEXT NOT NULL,
                extension            TEXT,
                category_source      TEXT,
                recent_time          INTEGER NOT NULL,
                target_modified_time INTEGER,
                size_bytes           INTEGER,
                exists_state         INTEGER NOT NULL,
                is_folder            INTEGER NOT NULL,
                is_favorite          INTEGER NOT NULL,
                favorite_time        INTEGER,
                favorite_order       INTEGER NOT NULL DEFAULT 0,
                is_hidden            INTEGER NOT NULL,
                source_kinds         INTEGER NOT NULL,
                icon_cache_key       TEXT,
                last_seen_time       INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_recent_time ON recent_items(recent_time DESC);
            CREATE INDEX IF NOT EXISTS idx_extension   ON recent_items(extension);

            CREATE TABLE IF NOT EXISTS favorites (
                normalized_path      TEXT PRIMARY KEY,
                display_name         TEXT NOT NULL,
                extension            TEXT,
                category_source      TEXT,
                recent_time          INTEGER NOT NULL,
                target_modified_time INTEGER,
                size_bytes           INTEGER,
                exists_state         INTEGER NOT NULL,
                is_folder            INTEGER NOT NULL,
                is_favorite          INTEGER NOT NULL,
                favorite_time        INTEGER,
                favorite_order       INTEGER NOT NULL DEFAULT 0,
                is_hidden            INTEGER NOT NULL,
                source_kinds         INTEGER NOT NULL,
                icon_cache_key       TEXT,
                last_seen_time       INTEGER NOT NULL,
                icon_png             BLOB,
                favorite_alias       TEXT
            );

            -- 迁移逻辑：如果 favorites 表为空，则从 recent_items 导入旧的收藏项（显式列名，兼容列数差异）
            INSERT OR IGNORE INTO favorites
                (normalized_path, display_name, extension, category_source,
                 recent_time, target_modified_time, size_bytes,
                 exists_state, is_folder, is_favorite, favorite_time, favorite_order, is_hidden,
                 source_kinds, icon_cache_key, last_seen_time)
            SELECT normalized_path, display_name, extension, category_source,
                   recent_time, target_modified_time, size_bytes,
                   exists_state, is_folder, is_favorite, favorite_time, favorite_order, is_hidden,
                   source_kinds, icon_cache_key, last_seen_time
            FROM recent_items WHERE is_favorite = 1;
            """;
        cmd.ExecuteNonQuery();

        ApplyCodeToDocumentsMigration();
    }

    private void ApplyCodeToDocumentsMigration()
    {
        using var check = _conn!.CreateCommand();
        const int version = 2026050701;
        check.CommandText = "SELECT COUNT(*) FROM schema_version WHERE version = $version;";
        check.Parameters.AddWithValue("$version", version);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0)
            return;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE recent_items SET category_source = 'Documents' WHERE category_source = 'Code';
            UPDATE favorites SET category_source = 'Documents' WHERE category_source = 'Code';
            INSERT INTO schema_version (version, applied_utc) VALUES ($version, $applied);
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$applied", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Log.Information("RecentRepository: migrated Code classification to Documents");
    }

    // 从 SQLite 加载最多 maxItems 条记录（按 recent_time DESC），供启动时灌入内存
    public IEnumerable<RecentItem> LoadAll(int maxItems = 200)
    {
        if (_conn is null) yield break;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT normalized_path, display_name, extension,
                   category_source AS classification_source,
                   recent_time, target_modified_time, size_bytes,
                   exists_state, is_folder, is_favorite, favorite_time, favorite_order, is_hidden,
                   source_kinds, icon_cache_key, last_seen_time
            FROM recent_items
            ORDER BY recent_time DESC
            LIMIT {maxItems}
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return ReadItem(reader);
    }

    public IEnumerable<RecentItem> LoadFavorites()
    {
        if (_conn is null) yield break;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT normalized_path, display_name, extension, category_source,
                   recent_time, target_modified_time, size_bytes,
                   exists_state, is_folder, is_favorite, favorite_time, favorite_order, is_hidden,
                   source_kinds, icon_cache_key, last_seen_time, icon_png, favorite_alias
            FROM favorites
            ORDER BY favorite_order ASC, favorite_time DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return ReadItem(reader);
    }

    public void Upsert(RecentItem item) => UpsertToTable(item, "recent_items", includeFavoriteAlias: false);

    public void Delete(string normalizedPath)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_items WHERE normalized_path = $path;";
        cmd.Parameters.AddWithValue("$path", normalizedPath);
        cmd.ExecuteNonQuery();
    }

    public void UpsertFavorite(RecentItem item)
    {
        UpsertToTable(item, "favorites", includeFavoriteAlias: true);
    }

    private void UpsertToTable(RecentItem item, string tableName, bool includeFavoriteAlias)
    {
        if (_conn is null) return;
        try
        {
            var aliasColumn = includeFavoriteAlias ? ", favorite_alias" : string.Empty;
            var aliasValue = includeFavoriteAlias ? ", $alias" : string.Empty;
            var aliasUpdate = includeFavoriteAlias ? "favorite_alias       = excluded.favorite_alias," : string.Empty;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {tableName}
                    (normalized_path, display_name, extension, category_source,
                     recent_time, target_modified_time, size_bytes,
                     exists_state, is_folder, is_favorite, favorite_time, favorite_order, is_hidden,
                     source_kinds, icon_cache_key, last_seen_time{aliasColumn})
                VALUES
                    ($path, $name, $ext, $ftype,
                     $rt, $mt, $size,
                     $es, $isf, $isfav, $ftm, $ford, $ish,
                     $sk, $ick, $lst{aliasValue})
                ON CONFLICT(normalized_path) DO UPDATE SET
                    display_name         = excluded.display_name,
                    extension            = excluded.extension,
                    category_source      = excluded.category_source,
                    recent_time          = excluded.recent_time,
                    target_modified_time = excluded.target_modified_time,
                    size_bytes           = excluded.size_bytes,
                    exists_state         = excluded.exists_state,
                    is_folder            = excluded.is_folder,
                    is_favorite          = excluded.is_favorite,
                    favorite_time        = excluded.favorite_time,
                    favorite_order       = excluded.favorite_order,
                    is_hidden            = excluded.is_hidden,
                    source_kinds         = excluded.source_kinds,
                    icon_cache_key       = excluded.icon_cache_key,
                    {aliasUpdate}
                    last_seen_time       = excluded.last_seen_time;
                """;
            cmd.Parameters.AddWithValue("$path",  item.NormalizedPath);
            cmd.Parameters.AddWithValue("$name",  item.DisplayName);
            cmd.Parameters.AddWithValue("$ext",   item.Extension);
            cmd.Parameters.AddWithValue("$ftype", item.ClassificationSource);
            cmd.Parameters.AddWithValue("$rt",    ToEpochMs(item.RecentTime));
            cmd.Parameters.AddWithValue("$mt",    item.TargetModifiedTime.HasValue ? ToEpochMs(item.TargetModifiedTime.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("$size",  item.SizeBytes.HasValue ? item.SizeBytes.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$es",    (int)item.Exists);
            cmd.Parameters.AddWithValue("$isf",   item.IsFolder   ? 1 : 0);
            cmd.Parameters.AddWithValue("$isfav", item.IsFavorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$ftm",   item.FavoriteTime.HasValue ? ToEpochMs(item.FavoriteTime.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("$ford",  item.FavoriteOrder);
            cmd.Parameters.AddWithValue("$ish",   item.IsHidden   ? 1 : 0);
            cmd.Parameters.AddWithValue("$sk",    (int)item.Sources);
            cmd.Parameters.AddWithValue("$ick",   item.IconCacheKey ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$lst",   ToEpochMs(item.LastSeenTime));
            if (includeFavoriteAlias)
                cmd.Parameters.AddWithValue("$alias", item.FavoriteAlias ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecentRepository: UpsertToTable({Table}) 失败 {Path}", tableName, LogPrivacy.Format(item.NormalizedPath));
        }
    }

    // 背景扫描发现项时的 UPSERT：保留收藏状态、隐藏状态，并合并来源位掩码
    public void UpsertDiscovery(RecentItem item)
    {
        if (_conn is null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recent_items
                    (normalized_path, display_name, extension, category_source,
                     recent_time, target_modified_time, size_bytes,
                     exists_state, is_folder, is_favorite, favorite_time, favorite_order, is_hidden,
                     source_kinds, icon_cache_key, last_seen_time)
                VALUES
                    ($path, $name, $ext, $ftype,
                     $rt, $mt, $size,
                     $es, $isf, 0, NULL, 0, 0,
                     $sk, $ick, $lst)
                ON CONFLICT(normalized_path) DO UPDATE SET
                    display_name         = excluded.display_name,
                    extension            = excluded.extension,
                    category_source      = excluded.category_source,
                    recent_time          = MAX(recent_time, excluded.recent_time),
                    target_modified_time = COALESCE(excluded.target_modified_time, target_modified_time),
                    size_bytes           = COALESCE(excluded.size_bytes, size_bytes),
                    exists_state         = excluded.exists_state,
                    is_folder            = excluded.is_folder,
                    source_kinds         = source_kinds | excluded.source_kinds,
                    icon_cache_key       = COALESCE(excluded.icon_cache_key, icon_cache_key),
                    last_seen_time       = excluded.last_seen_time;
                """;
            cmd.Parameters.AddWithValue("$path",  item.NormalizedPath);
            cmd.Parameters.AddWithValue("$name",  item.DisplayName);
            cmd.Parameters.AddWithValue("$ext",   item.Extension);
            cmd.Parameters.AddWithValue("$ftype", item.ClassificationSource);
            cmd.Parameters.AddWithValue("$rt",    ToEpochMs(item.RecentTime));
            cmd.Parameters.AddWithValue("$mt",    item.TargetModifiedTime.HasValue ? ToEpochMs(item.TargetModifiedTime.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("$size",  item.SizeBytes.HasValue ? item.SizeBytes.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$es",    (int)item.Exists);
            cmd.Parameters.AddWithValue("$isf",   item.IsFolder ? 1 : 0);
            cmd.Parameters.AddWithValue("$sk",    (int)item.Sources);
            cmd.Parameters.AddWithValue("$ick",   item.IconCacheKey ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$lst",   ToEpochMs(item.LastSeenTime));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecentRepository: UpsertDiscovery 失败 {Path}", LogPrivacy.Format(item.NormalizedPath));
        }
    }

    public void UpdateFavoriteIcon(string normalizedPath, byte[] iconPng)
    {
        if (_conn is null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE favorites SET icon_png = $icon WHERE normalized_path = $path;";
            cmd.Parameters.AddWithValue("$icon", iconPng);
            cmd.Parameters.AddWithValue("$path", normalizedPath);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecentRepository: UpdateFavoriteIcon 失败 {Path}", LogPrivacy.Format(normalizedPath));
        }
    }

    public void UpdateFavoriteAlias(string normalizedPath, string? alias)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE favorites SET favorite_alias = $alias WHERE normalized_path = $path;";
        cmd.Parameters.AddWithValue("$alias", alias ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$path", normalizedPath);
        cmd.ExecuteNonQuery();
    }

    public void DeleteFavorite(string normalizedPath)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM favorites WHERE normalized_path = $path;";
        cmd.Parameters.AddWithValue("$path", normalizedPath);
        cmd.ExecuteNonQuery();
    }

    // 清空全表（Rebuild Index 使用）
    public void UpdateExistsBySource(SourceKinds source, ExistsState state)
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE recent_items
            SET exists_state = $state
            WHERE (source_kinds & $source) != 0;
            """;
        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$source", (int)source);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAll()
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_items;";
        cmd.ExecuteNonQuery();
    }

    public void ClearHidden()
    {
        if (_conn is null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_items WHERE is_hidden = 1;";
        cmd.ExecuteNonQuery();
    }

    private static RecentItem ReadItem(SqliteDataReader r) => new()
    {
        NormalizedPath       = r.GetString(0),
        DisplayName          = r.GetString(1),
        Extension            = r.IsDBNull(2)  ? string.Empty : r.GetString(2),
        ClassificationSource = r.IsDBNull(3)  ? "Other"      : r.GetString(3),
        RecentTime           = FromEpochMs(r.GetInt64(4)),
        TargetModifiedTime   = r.IsDBNull(5)  ? null         : FromEpochMs(r.GetInt64(5)),
        SizeBytes            = r.IsDBNull(6)  ? null         : r.GetInt64(6),
        Exists               = (ExistsState)r.GetInt32(7),
        IsFolder             = r.GetInt32(8)  != 0,
        IsFavorite           = r.GetInt32(9)  != 0,
        FavoriteTime         = r.IsDBNull(10) ? null : FromEpochMs(r.GetInt64(10)),
        FavoriteOrder        = r.GetInt32(11),
        IsHidden             = r.GetInt32(12) != 0,
        Sources              = (SourceKinds)r.GetInt32(13),
        IconCacheKey         = r.IsDBNull(14) ? null : r.GetString(14),
        LastSeenTime         = FromEpochMs(r.GetInt64(15)),
        IconData             = r.FieldCount > 16 && !r.IsDBNull(16) ? (byte[])r.GetValue(16) : null,
        FavoriteAlias        = r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17) : null,
    };

    private static long     ToEpochMs(DateTime dt) =>
        new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static DateTime FromEpochMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public void Dispose() => _conn?.Dispose();
}
