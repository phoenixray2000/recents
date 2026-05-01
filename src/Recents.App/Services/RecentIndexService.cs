using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Data.Sqlite;
using Recents.App.Models;
using Recents.App.Services.Sources;
using Recents.App.Utils;
using Recents.App.ViewModels;
using Serilog;

namespace Recents.App.Services;

// PRD §6.3 / §6.18 最近文件索引服务：SQLite 持久化 + 内存融合中心
// 启动时先从 SQLite 灌入内存 ObservableCollection 供 UI 立即渲染，
// 各 IRecentSource 扫描结果异步通过 MergeAsync 推入。
public class RecentIndexService : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _conn;

    // UI 绑定集合（在 Dispatcher 线程维护，按 RecentTime 倒序）
    public ObservableCollection<RecentItemViewModel> Items { get; } = new();

    // 内存索引（NormalizedPath → ViewModel），用于快速 O(1) 合并查找
    private readonly Dictionary<string, RecentItemViewModel> _index = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _mergeLock = new();

    public RecentIndexService()
    {
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Recents");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "index.db");
    }

    // 打开 SQLite，创建 schema（幂等），若损坏则备份重建
    public void OpenDatabase()
    {
        try
        {
            TryOpenOrRebuild();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecentIndexService: 数据库打开失败，尝试重建");
            TryRebuild();
        }
    }

    private void TryOpenOrRebuild()
    {
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        EnsureSchema();
        // 快速完整性校验
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar()?.ToString();
        if (result != "ok")
            throw new InvalidDataException($"SQLite integrity_check: {result}");
        Log.Information("RecentIndexService: 数据库打开成功 {Path}", _dbPath);
    }

    private void TryRebuild()
    {
        _conn?.Dispose();
        _conn = null;
        if (File.Exists(_dbPath))
        {
            var bak = _dbPath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_dbPath, bak, overwrite: true);
            Log.Warning("RecentIndexService: 损坏的数据库已备份到 {Bak}", bak);
        }
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    // 创建 schema（严格按 PRD §6.18）
    private void EnsureSchema()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS recent_items (
                normalized_path     TEXT PRIMARY KEY,
                display_name        TEXT NOT NULL,
                extension           TEXT,
                file_type           TEXT,
                recent_time         INTEGER NOT NULL,
                target_modified_time INTEGER,
                size_bytes          INTEGER,
                exists_state        INTEGER NOT NULL,
                is_folder           INTEGER NOT NULL,
                is_favorite         INTEGER NOT NULL,
                is_hidden           INTEGER NOT NULL,
                source_kinds        INTEGER NOT NULL,
                icon_cache_key      TEXT,
                last_seen_time      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_recent_time ON recent_items(recent_time DESC);
            CREATE INDEX IF NOT EXISTS idx_extension   ON recent_items(extension);
            """;
        cmd.ExecuteNonQuery();
    }

    // 启动时从 SQLite 把所有记录灌入内存（在调用线程执行，应在 UI 准备好前调用）
    public void LoadFromDatabase(int maxItems = 200)
    {
        if (_conn is null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT normalized_path, display_name, extension, file_type,
                       recent_time, target_modified_time, size_bytes,
                       exists_state, is_folder, is_favorite, is_hidden,
                       source_kinds, icon_cache_key, last_seen_time
                FROM recent_items
                WHERE is_folder = 0 AND exists_state != 0
                ORDER BY recent_time DESC
                LIMIT {maxItems}
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadItem(reader);
                var vm   = new RecentItemViewModel(item);
                _index[item.NormalizedPath] = vm;
                Items.Add(vm);
            }
            Log.Information("RecentIndexService: 从缓存加载 {Count} 条", Items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecentIndexService: 从数据库加载失败");
        }
    }

    // 核心融合方法：线程安全，可从任意线程调用
    // NormalizedPath 查找 → SourceKinds |= → RecentTime = max → 写 SQLite + 通知 UI
    public async Task MergeAsync(RecentItem incoming)
    {
        if (string.IsNullOrEmpty(incoming.NormalizedPath)) return;

        // 1. 不跟踪单个文件夹
        // 2. 不跟踪不存在的文件
        if (incoming.IsFolder || incoming.Exists == ExistsState.Missing)
        {
            await RemoveAsync(incoming.NormalizedPath);
            return;
        }

        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                _index.TryGetValue(incoming.NormalizedPath, out var existing);

                if (existing is null)
                {
                    // 新条目
                    incoming.LastSeenTime = DateTime.UtcNow;
                    UpsertDb(incoming);
                    var vm = new RecentItemViewModel(incoming);
                    _index[incoming.NormalizedPath] = vm;
                    InsertSorted(vm);
                }
                else
                {
                    var changed = false;
                    var recentTimeChanged = false;

                    if ((existing.Item.Sources & incoming.Sources) != incoming.Sources)
                    {
                        existing.Item.Sources |= incoming.Sources;
                        changed = true;
                    }
                    if (incoming.RecentTime > existing.Item.RecentTime)
                    {
                        existing.Item.RecentTime = incoming.RecentTime;
                        changed = true;
                        recentTimeChanged = true;
                    }
                    if (incoming.SizeBytes.HasValue && existing.Item.SizeBytes != incoming.SizeBytes)
                    {
                        existing.Item.SizeBytes = incoming.SizeBytes;
                        changed = true;
                    }
                    if (incoming.Exists != ExistsState.Unknown)
                        existing.Item.Exists = incoming.Exists;

                    existing.Item.LastSeenTime = DateTime.UtcNow;

                    if (changed)
                    {
                        UpsertDb(existing.Item);
                        // 通知 ViewModel 更新（需回到 UI 线程）
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                            () => existing.Refresh());
                        // 如果 RecentTime 变了，重新排序（简单策略：移到顶部）
                        if (recentTimeChanged)
                            ReSortToTop(existing);
                    }
                }
            }
        }).ConfigureAwait(false);
    }

    // 从索引移除一条记录（FileSystemWatcher.Deleted 时调用）
    public async Task RemoveAsync(string rawPath)
    {
        var normalized = PathNormalizer.Normalize(rawPath);
        if (string.IsNullOrEmpty(normalized)) return;

        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                if (!_index.TryGetValue(normalized, out var vm)) return;
                _index.Remove(normalized);
                DeleteDb(normalized);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () => Items.Remove(vm));
            }
        }).ConfigureAwait(false);
    }

    // 重建索引：清空 SQLite 和内存（供托盘「重新扫描」使用）
    public async Task RebuildAsync()
    {
        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                if (_conn is null) return;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM recent_items;";
                cmd.ExecuteNonQuery();
                _index.Clear();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Items.Clear());
                Log.Information("RecentIndexService: 索引已清空，等待各源重新扫描");
            }
        }).ConfigureAwait(false);
    }

    #region SQLite CRUD

    private void UpsertDb(RecentItem item)
    {
        if (_conn is null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recent_items
                    (normalized_path, display_name, extension, file_type,
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
                    file_type            = excluded.file_type,
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
            cmd.Parameters.AddWithValue("$ftype", item.FileType);
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
            Log.Warning(ex, "RecentIndexService: UpsertDb 失败 {Path}", item.NormalizedPath);
        }
    }

    private void DeleteDb(string normalizedPath)
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
            Log.Warning(ex, "RecentIndexService: DeleteDb 失败 {Path}", normalizedPath);
        }
    }

    private static RecentItem ReadItem(SqliteDataReader r) => new()
    {
        NormalizedPath      = r.GetString(0),
        DisplayName         = r.GetString(1),
        Extension           = r.IsDBNull(2)  ? string.Empty : r.GetString(2),
        FileType            = r.IsDBNull(3)  ? "Other"      : r.GetString(3),
        RecentTime          = FromEpochMs(r.GetInt64(4)),
        TargetModifiedTime  = r.IsDBNull(5)  ? null         : FromEpochMs(r.GetInt64(5)),
        SizeBytes           = r.IsDBNull(6)  ? null         : r.GetInt64(6),
        Exists              = (ExistsState)r.GetInt32(7),
        IsFolder            = r.GetInt32(8)  != 0,
        IsFavorite          = r.GetInt32(9)  != 0,
        IsHidden            = r.GetInt32(10) != 0,
        Sources             = (SourceKinds)r.GetInt32(11),
        IconCacheKey        = r.IsDBNull(12) ? null         : r.GetString(12),
        LastSeenTime        = FromEpochMs(r.GetInt64(13)),
    };

    private static long     ToEpochMs(DateTime dt) =>
        new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static DateTime FromEpochMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    #endregion

    #region 内存集合维护

    // 按 RecentTime 倒序插入（保持集合有序）
    private void InsertSorted(RecentItemViewModel vm)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            int pos = 0;
            while (pos < Items.Count && Items[pos].Item.RecentTime >= vm.Item.RecentTime)
                pos++;
            Items.Insert(pos, vm);
        });
    }

    // 把某 ViewModel 移到应有的位置（RecentTime 更新后调用）
    private void ReSortToTop(RecentItemViewModel vm)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Items.Remove(vm))
                InsertSorted(vm);
        });
    }

    #endregion

    public void Dispose()
    {
        _conn?.Dispose();
    }
}
