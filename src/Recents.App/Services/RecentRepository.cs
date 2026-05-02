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
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        EnsureSchema();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar()?.ToString();
        if (result != "ok")
            throw new InvalidDataException($"SQLite integrity_check: {result}");
        Log.Information("RecentRepository: 数据库打开成功 {Path}", _dbPath);
    }

    private void TryRebuild()
    {
        _conn?.Dispose();
        _conn = null;
        if (File.Exists(_dbPath))
        {
            var bak = _dbPath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_dbPath, bak, overwrite: true);
            Log.Warning("RecentRepository: 损坏的数据库已备份到 {Bak}", bak);
        }
        _conn = new SqliteConnection($"Data Source={_dbPath}");
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

        cmd.CommandText = """
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
                is_hidden            INTEGER NOT NULL,
                source_kinds         INTEGER NOT NULL,
                icon_cache_key       TEXT,
                last_seen_time       INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_recent_time ON recent_items(recent_time DESC);
            CREATE INDEX IF NOT EXISTS idx_extension   ON recent_items(extension);
            """;
        cmd.ExecuteNonQuery();
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
                   exists_state, is_folder, is_favorite, is_hidden,
                   source_kinds, icon_cache_key, last_seen_time
            FROM recent_items
            ORDER BY recent_time DESC
            LIMIT {maxItems}
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return ReadItem(reader);
    }

    // UPSERT 一条记录
    public void Upsert(RecentItem item)
    {
        if (_conn is null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recent_items
                    (normalized_path, display_name, extension, category_source,
                     recent_time, target_modified_time, size_bytes,
                     exists_state, is_folder, is_favorite, is_hidden,
                     source_kinds, icon_cache_key, last_seen_time)
                VALUES
                    ($path, $name, $ext, $ftype,
                     $rt, $mt, $size,
                     $es, $isf, $isfav, $ish,
                     $sk, $ick, $lst)
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
                    is_hidden            = excluded.is_hidden,
                    source_kinds         = excluded.source_kinds,
                    icon_cache_key       = excluded.icon_cache_key,
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
            cmd.Parameters.AddWithValue("$ish",   item.IsHidden   ? 1 : 0);
            cmd.Parameters.AddWithValue("$sk",    (int)item.Sources);
            cmd.Parameters.AddWithValue("$ick",   item.IconCacheKey ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$lst",   ToEpochMs(item.LastSeenTime));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecentRepository: Upsert 失败 {Path}", item.NormalizedPath);
        }
    }

    // 删除单条记录
    public void Delete(string normalizedPath)
    {
        if (_conn is null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM recent_items WHERE normalized_path = $path;";
            cmd.Parameters.AddWithValue("$path", normalizedPath);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecentRepository: Delete 失败 {Path}", normalizedPath);
        }
    }

    // 清空全表（Rebuild Index 使用）
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
        IsHidden             = r.GetInt32(10) != 0,
        Sources              = (SourceKinds)r.GetInt32(11),
        IconCacheKey         = r.IsDBNull(12) ? null : r.GetString(12),
        LastSeenTime         = FromEpochMs(r.GetInt64(13)),
    };

    private static long     ToEpochMs(DateTime dt) =>
        new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static DateTime FromEpochMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public void Dispose() => _conn?.Dispose();
}
