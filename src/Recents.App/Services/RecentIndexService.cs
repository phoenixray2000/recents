using System.Collections.ObjectModel;
using System.IO;
using Recents.App.Models;
using Recents.App.Services.Sources;
using Recents.App.Utils;
using Recents.App.ViewModels;
using Serilog;

namespace Recents.App.Services;

// PRD §6.3 / §6.18 最近文件索引服务：内存融合中心（SQLite CRUD 已委托给 RecentRepository）
// 启动时先从 Repository 灌入内存 ObservableCollection 供 UI 立即渲染，
// 各 IRecentSource 扫描结果异步通过 MergeAsync 推入。
public class RecentIndexService : IDisposable
{
    private const int RemovedSourceMask = (1 << 4) | (1 << 5);
    private readonly SettingsService _settingsService;
    private readonly RecentRepository _repo;
    private readonly ExistsProbeService _probeService;
    private readonly object _mergeLock = new();

    // UI 绑定集合（在 Dispatcher 线程维护，按 RecentTime 倒序）
    public ObservableCollection<RecentItemViewModel> Items { get; } = new();

    // 内存索引（NormalizedPath → ViewModel），O(1) 合并查找
    private readonly Dictionary<string, RecentItemViewModel> _index
        = new(StringComparer.OrdinalIgnoreCase);

    public RecentIndexService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _probeService = new ExistsProbeService(this);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Recents");
        Directory.CreateDirectory(dir);
        _repo = new RecentRepository(Path.Combine(dir, "index.db"));
    }

    // 打开数据库（委托 Repository）
    public void OpenDatabase() => _repo.OpenDatabase();

    // 启动时从 SQLite 灌入内存
    public void LoadFromDatabase(int maxItems = 200)
    {
        try
        {
            foreach (var item in _repo.LoadAll(maxItems))
            {
                var activeSources = item.Sources & ~(SourceKinds)RemovedSourceMask;
                if (activeSources == SourceKinds.None)
                {
                    _repo.Delete(item.NormalizedPath);
                    continue;
                }

                if (activeSources != item.Sources)
                {
                    item.Sources = activeSources;
                    _repo.Upsert(item);
                }

                if (item.Exists == ExistsState.Missing || item.IsHidden)
                {
                    if (item.Exists == ExistsState.Missing)
                        _repo.Delete(item.NormalizedPath);
                    continue;
                }

                var vm = new RecentItemViewModel(item, this, _probeService);
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
    public async Task MergeAsync(RecentItem incoming)
    {
        if (string.IsNullOrEmpty(incoming.NormalizedPath)) return;

        // A6. 不展示 Recent 文件夹中的 .lnk 本体（PRD §5.11 系统硬规则）
        var recentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Recent");
        if (incoming.NormalizedPath.StartsWith(recentDir, StringComparison.OrdinalIgnoreCase) &&
            incoming.NormalizedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return;

        // A7. 用户排除路径过滤（ExcludedPaths 前缀匹配）
        foreach (var excluded in _settingsService.Current.ExcludedPaths)
        {
            if (incoming.NormalizedPath.Contains(@"\" + excluded + @"\", StringComparison.OrdinalIgnoreCase) ||
                incoming.NormalizedPath.EndsWith(@"\" + excluded, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Missing or Hidden items are never shown; stale entries are removed from the index.
        if (incoming.Exists == ExistsState.Missing || incoming.IsHidden)
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
                    incoming.LastSeenTime = DateTime.UtcNow;
                    _repo.Upsert(incoming);
                    var vm = new RecentItemViewModel(incoming, this, _probeService);
                    _index[incoming.NormalizedPath] = vm;
                    InsertSorted(vm);
                }
                else
                {
                    var changed = false;
                    var recentTimeChanged = false;

                    if ((existing.Item.Sources & incoming.Sources) != incoming.Sources)
                    { existing.Item.Sources |= incoming.Sources; changed = true; }

                    if (incoming.RecentTime > existing.Item.RecentTime)
                    { existing.Item.RecentTime = incoming.RecentTime; changed = true; recentTimeChanged = true; }

                    if (incoming.SizeBytes.HasValue && existing.Item.SizeBytes != incoming.SizeBytes)
                    { existing.Item.SizeBytes = incoming.SizeBytes; changed = true; }

                    if (existing.Item.IsFolder != incoming.IsFolder)
                    { existing.Item.IsFolder = incoming.IsFolder; changed = true; }

                    if (!string.IsNullOrWhiteSpace(incoming.DisplayName) && existing.Item.DisplayName != incoming.DisplayName)
                    { existing.Item.DisplayName = incoming.DisplayName; changed = true; }

                    if (existing.Item.Extension != incoming.Extension)
                    { existing.Item.Extension = incoming.Extension; changed = true; }

                    if (existing.Item.ClassificationSource != incoming.ClassificationSource)
                    { existing.Item.ClassificationSource = incoming.ClassificationSource; changed = true; }

                    if (incoming.Exists != ExistsState.Unknown)
                        existing.Item.Exists = incoming.Exists;

                    existing.Item.LastSeenTime = DateTime.UtcNow;

                    if (changed)
                    {
                        _repo.Upsert(existing.Item);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                            () => existing.Refresh());
                        if (recentTimeChanged)
                            ReSortToTop(existing);
                    }
                }
            }
        }).ConfigureAwait(false);
    }

    // FileSystemWatcher.Deleted 时调用
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
                _repo.Delete(normalized);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () => Items.Remove(vm));
            }
        }).ConfigureAwait(false);
    }

    public async Task UpdateFavoriteAsync(string normalizedPath, bool isFavorite)
    {
        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                if (!_index.TryGetValue(normalizedPath, out var vm)) return;
                vm.Item.IsFavorite = isFavorite;
                _repo.Upsert(vm.Item);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => vm.Refresh());
            }
        }).ConfigureAwait(false);
    }

    // 重建索引：清空 SQLite + 内存（托盘「Rescan」使用）
    public async Task RebuildAsync()
    {
        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                _repo.DeleteAll();
                _index.Clear();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Items.Clear());
                Log.Information("RecentIndexService: 索引已清空，等待各源重新扫描");
            }
        }).ConfigureAwait(false);
    }

    public async Task ClearHiddenItemsAsync()
    {
        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                _repo.ClearHidden();
                var hiddenPaths = _index.Values.Where(v => v.Item.IsHidden).Select(v => v.Item.NormalizedPath).ToList();
                foreach (var path in hiddenPaths)
                {
                    if (_index.TryGetValue(path, out var vm))
                    {
                        _index.Remove(path);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Items.Remove(vm));
                    }
                }
            }
        }).ConfigureAwait(false);
    }

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

    // RecentTime 更新后移到正确位置
    private void ReSortToTop(RecentItemViewModel vm)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Items.Remove(vm))
                InsertSorted(vm);
        });
    }

    #endregion

    public void Dispose() => _repo.Dispose();
}
