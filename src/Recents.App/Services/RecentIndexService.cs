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
    private readonly System.Threading.Channels.Channel<RecentItem> _mergeQueue;
    private bool _isProcessingQueue = false;
    public event Action? IndexChanged;

    // UI 绑定集合（在 Dispatcher 线程维护，按 RecentTime 倒序）
    public ObservableCollection<RecentItemViewModel> Items { get; } = new();
    
    // 收藏夹集合（独立清单，由 FavoritesRepository 持久化）
    public ObservableCollection<RecentItemViewModel> Favorites { get; } = new();

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

        // Initialize merge queue (PRD optimization)
        _mergeQueue = System.Threading.Channels.Channel.CreateUnbounded<RecentItem>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        StartMergeQueueProcessor();
    }

    // 打开数据库（委托 Repository）
    public void OpenDatabase() => _repo.OpenDatabase();

    // 启动时从 SQLite 灌入内存 (异步优化，防止阻塞 UI)
    public async Task LoadFromDatabaseAsync(int maxItems = 200)
    {
        try
        {
            // 1. 先加载收藏项（独立清单）
            var favorites = await Task.Run(() => _repo.LoadFavorites());
            var favVMs = new List<RecentItemViewModel>();
            foreach (var item in favorites)
            {
                favVMs.Add(new RecentItemViewModel(item, this, _probeService));
            }

            // 2. 加载最近项
            var items = await Task.Run(() => _repo.LoadAll(maxItems));
            var recVMs = new List<RecentItemViewModel>();

            foreach (var item in items)
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

                if (_settingsService.Current.ExcludedExtensions.Any(ext => item.NormalizedPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    _repo.Delete(item.NormalizedPath);
                    continue;
                }

                if (item.IsHidden) continue;

                recVMs.Add(new RecentItemViewModel(item, this, _probeService));
            }

            // 3. 批量更新到 UI 线程并合并索引
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                lock (_mergeLock)
                {
                    // A. 处理收藏
                    foreach (var vm in favVMs)
                    {
                        if (!_index.ContainsKey(vm.Item.NormalizedPath))
                        {
                            _index[vm.Item.NormalizedPath] = vm;
                        }
                        Favorites.Add(_index[vm.Item.NormalizedPath]);
                    }

                    // B. 处理最近
                    foreach (var vm in recVMs)
                    {
                        if (!_index.ContainsKey(vm.Item.NormalizedPath))
                        {
                            _index[vm.Item.NormalizedPath] = vm;
                        }
                        Items.Add(_index[vm.Item.NormalizedPath]);
                    }
                }
                Log.Information("RecentIndexService: 已从数据库恢复 {FavCount} 条收藏, {RecCount} 条最近项", Favorites.Count, Items.Count);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecentIndexService: 从数据库加载失败");
        }
    }

    // 核心融合方法：线程安全，异步非阻塞
    public Task MergeAsync(RecentItem incoming)
    {
        if (incoming == null || string.IsNullOrEmpty(incoming.NormalizedPath)) 
            return Task.CompletedTask;

        // 快速入队，立即返回，不等待处理
        _mergeQueue.Writer.TryWrite(incoming);
        return Task.CompletedTask;
    }

    private void StartMergeQueueProcessor()
    {
        if (_isProcessingQueue) return;
        _isProcessingQueue = true;

        Task.Run(async () =>
        {
            var reader = _mergeQueue.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var incoming))
                {
                    try
                    {
                        await ProcessMergeItemAsync(incoming);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "MergeQueueProcessor: 处理项失败 {Path}", incoming.NormalizedPath);
                    }
                }
            }
        });
    }

    private async Task ProcessMergeItemAsync(RecentItem incoming)
    {
        // A6. 不展示 Recent 文件夹中的 .lnk 本体
        var recentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Recent");
        if (incoming.NormalizedPath.StartsWith(recentDir, StringComparison.OrdinalIgnoreCase) &&
            incoming.NormalizedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return;

        // A7. 用户排除路径过滤
        foreach (var excluded in _settingsService.Current.ExcludedPaths)
        {
            if (incoming.NormalizedPath.Contains(@"\" + excluded + @"\", StringComparison.OrdinalIgnoreCase) ||
                incoming.NormalizedPath.EndsWith(@"\" + excluded, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // A8. 排除扩展名过滤
        if (_settingsService.Current.ExcludedExtensions.Any(ext => incoming.NormalizedPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return;

        if (incoming.Exists == ExistsState.Missing || incoming.IsHidden)
        {
            await RemoveAsync(incoming.NormalizedPath);
            return;
        }

        lock (_mergeLock)
        {
            _index.TryGetValue(incoming.NormalizedPath, out var existing);

            if (existing is null)
            {
                incoming.LastSeenTime = DateTime.UtcNow;
                _repo.UpsertDiscovery(incoming);
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
                    _repo.UpsertDiscovery(existing.Item);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        () => 
                        {
                            existing.Refresh();
                            IndexChanged?.Invoke();
                        });
                    if (recentTimeChanged)
                        ReSortToTop(existing);
                }
            }
            
            Prune();
        }
    }

    private void Prune()
    {
        var max = _settingsService.Current.MaxRecentItems;
        if (Items.Count <= max) return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            lock (_mergeLock)
            {
                while (Items.Count > max)
                {
                    var oldest = Items.Last();
                    Items.RemoveAt(Items.Count - 1);
                    // 如果不在收藏夹，才彻底从内存索引移除
                    if (!Favorites.Contains(oldest))
                    {
                        _index.Remove(oldest.Item.NormalizedPath);
                    }
                }
            }
        });
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
                
                // 从最近列表物理删除
                _repo.Delete(normalized);
                
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => 
                {
                    Items.Remove(vm);
                    // 如果不在收藏夹，才彻底从索引移除
                    if (!Favorites.Contains(vm))
                    {
                        _index.Remove(normalized);
                    }
                });
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
                UpdateFavoriteInternal(vm, isFavorite);
            }
        }).ConfigureAwait(false);
    }

    private void UpdateFavoriteInternal(RecentItemViewModel vm, bool isFavorite)
    {
        vm.Item.IsFavorite = isFavorite;
        if (isFavorite)
        {
            vm.Item.FavoriteTime = DateTime.UtcNow;
            // 计算排序：新加入的排在最后
            int maxOrder = 0;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => {
                maxOrder = Favorites.Count > 0 ? Favorites.Max(v => v.Item.FavoriteOrder) : 0;
            });
            vm.Item.FavoriteOrder = maxOrder + 1;

            _repo.UpsertFavorite(vm.Item);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!Favorites.Contains(vm)) Favorites.Add(vm);
                vm.Refresh();
                IndexChanged?.Invoke();
            });
        }
        else
        {
            _repo.DeleteFavorite(vm.Item.NormalizedPath);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Favorites.Remove(vm);
                vm.Refresh();
                IndexChanged?.Invoke();
            });
        }

        // 也要更新 recent_items 表的状态，保证双边一致
        _repo.Upsert(vm.Item);
    }

    public async Task AddFavoriteByPathAsync(string path)
    {
        var normalized = PathNormalizer.Normalize(path);
        if (string.IsNullOrEmpty(normalized)) return;

        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                if (!_index.TryGetValue(normalized, out var vm))
                {
                    var isDir = Directory.Exists(normalized);
                    var exists = (isDir || File.Exists(normalized)) ? ExistsState.Found : ExistsState.Missing;
                    
                    var item = new RecentItem
                    {
                        NormalizedPath = normalized,
                        DisplayName = Path.GetFileName(normalized),
                        Extension = isDir ? string.Empty : Path.GetExtension(normalized),
                        RecentTime = DateTime.UtcNow,
                        Exists = exists,
                        IsFolder = isDir,
                        Sources = SourceKinds.UserFolderWatch,
                        LastSeenTime = DateTime.UtcNow
                    };
                    item.ClassificationSource = FileTypeClassifier.Classify(item.Extension, item.IsFolder, _settingsService.Current.ClassificationSourceGroups);

                    _repo.UpsertDiscovery(item);
                    var newVm = new RecentItemViewModel(item, this, _probeService);
                    _index[normalized] = newVm;

                    // 加入最近列表
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => InsertSorted(newVm));
                    vm = newVm;
                }

                UpdateFavoriteInternal(vm, true);
            }
        }).ConfigureAwait(false);
    }

    public async Task ReorderFavoritesAsync(string normalizedPath, int targetIndex)
    {
        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                if (!_index.TryGetValue(normalizedPath, out var vm)) return;
                
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    Favorites.Remove(vm);
                    if (targetIndex < 0) targetIndex = 0;
                    if (targetIndex > Favorites.Count) targetIndex = Favorites.Count;
                    Favorites.Insert(targetIndex, vm);

                    // 重新分配序号并持久化
                    for (int i = 0; i < Favorites.Count; i++)
                    {
                        Favorites[i].Item.FavoriteOrder = i + 1;
                        _repo.UpsertFavorite(Favorites[i].Item);
                        // 同时同步到 recent_items
                        _repo.Upsert(Favorites[i].Item);
                    }
                });
            }
        }).ConfigureAwait(false);
    }

    public async Task HideItemAsync(string normalizedPath)
    {
        await Task.Run(() =>
        {
            lock (_mergeLock)
            {
                if (!_index.TryGetValue(normalizedPath, out var vm)) return;
                vm.Item.IsHidden = true;
                _repo.Upsert(vm.Item);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => 
                {
                    Items.Remove(vm);
                    IndexChanged?.Invoke();
                });
                _index.Remove(normalizedPath);
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
