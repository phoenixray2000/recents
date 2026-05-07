using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using Recents.App.Models;
using Recents.App.ViewModels;
using Serilog;
using WpfApplication = System.Windows.Application;

namespace Recents.App.Services.Clipboard;

public sealed class ClipboardStoreService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ClipboardRepository _repo;
    private readonly Dictionary<string, ClipboardItemViewModel> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClipboardItemViewModel> _byHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClipboardFavoriteViewModel> _favoritesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClipboardFavoriteViewModel> _favoritesByOriginalId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ClipboardBlobDeletionQueue _deletionQueue = new();
    private readonly System.Windows.Threading.DispatcherTimer _maintenanceTimer;
    private ClipboardActionService? _actions;

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();
    public ObservableCollection<ClipboardFavoriteViewModel> Favorites { get; } = new();
    public string DataDirectory { get; }
    public string BlobDirectory { get; }
    public string ImageDirectory { get; }
    public string ThumbnailDirectory { get; }
    public string FavoriteDirectory { get; }
    public string FavoriteBlobDirectory { get; }
    public string FavoriteImageDirectory { get; }
    public string FavoriteThumbnailDirectory { get; }

    public ClipboardStoreService(SettingsService settings)
        : this(
            settings,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recents", "clipboard"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recents", "clipboard.db"))
    {
    }

    internal ClipboardStoreService(SettingsService settings, string dataDirectory, string dbPath)
    {
        _settings = settings;
        DataDirectory = dataDirectory;
        BlobDirectory = Path.Combine(DataDirectory, "blobs");
        ImageDirectory = Path.Combine(DataDirectory, "images");
        ThumbnailDirectory = Path.Combine(DataDirectory, "thumbs");
        FavoriteDirectory = Path.Combine(DataDirectory, "favorites");
        FavoriteBlobDirectory = Path.Combine(FavoriteDirectory, "blobs");
        FavoriteImageDirectory = Path.Combine(FavoriteDirectory, "images");
        FavoriteThumbnailDirectory = Path.Combine(FavoriteDirectory, "thumbs");

        Directory.CreateDirectory(BlobDirectory);
        Directory.CreateDirectory(ImageDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
        Directory.CreateDirectory(FavoriteBlobDirectory);
        Directory.CreateDirectory(FavoriteImageDirectory);
        Directory.CreateDirectory(FavoriteThumbnailDirectory);

        _repo = new ClipboardRepository(dbPath);
        BindingOperations.EnableCollectionSynchronization(Items, _sync);
        BindingOperations.EnableCollectionSynchronization(Favorites, _sync);

        _maintenanceTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(1)
        };
        _maintenanceTimer.Tick += async (_, _) => await CompactOrphanBlobsAsync();
    }

    public void AttachActions(ClipboardActionService actions) => _actions = actions;

    public void OpenDatabase()
    {
        _repo.OpenDatabase();
    }

    public void LoadFromDatabase()
    {
        if (_actions is null)
            throw new InvalidOperationException("Clipboard actions must be attached before loading items.");

        var snapshot = LoadFromDatabaseSnapshot();
        ApplyLoadedSnapshot(snapshot);
    }

    public async Task LoadFromDatabaseAsync()
    {
        if (_actions is null)
            throw new InvalidOperationException("Clipboard actions must be attached before loading items.");

        var snapshot = await Task.Run(LoadFromDatabaseSnapshot);
        await InvokeOnDispatcherAsync(() => ApplyLoadedSnapshot(snapshot));
    }

    public void StartMaintenanceTimer()
    {
        if (!_maintenanceTimer.IsEnabled)
            _maintenanceTimer.Start();
    }

    private LoadedClipboardSnapshot LoadFromDatabaseSnapshot()
    {
        var actions = _actions ?? throw new InvalidOperationException("Clipboard actions must be attached before loading items.");
        var items = new List<ClipboardItem>();
        var favorites = new List<ClipboardFavoriteItem>();

        lock (_sync)
        {
            _repo.SoftDeleteOverflowAndExpired(
                _settings.Current.MaxClipboardItems,
                DateTime.UtcNow.AddDays(-_settings.Current.ClipboardRetentionDays),
                DateTime.UtcNow);
            _repo.CompactDeletedItems(_deletionQueue.CutoffUtc);

            var nowUtc = DateTime.UtcNow;
            foreach (var item in _repo.LoadItems(_settings.Current.MaxClipboardItems))
            {
                if (!actions.HasUsableContent(item))
                {
                    item.IsDeleted = true;
                    item.DeletedUtc = nowUtc;
                    _repo.SoftDelete(item.Id, nowUtc);
                    DeleteItemFiles(item);
                    continue;
                }

                items.Add(item);
            }

            var seenFavoriteOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var favorite in _repo.LoadFavorites())
            {
                if (!actions.HasUsableContent(favorite.ToClipboardItem()))
                {
                    DeleteFavoriteSnapshotLocked(favorite);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(favorite.OriginalItemId) &&
                    !seenFavoriteOrigins.Add(favorite.OriginalItemId))
                {
                    DeleteFavoriteSnapshotLocked(favorite);
                    continue;
                }

                favorites.Add(favorite);
            }
        }

        return new LoadedClipboardSnapshot(items, favorites);
    }

    private void ApplyLoadedSnapshot(LoadedClipboardSnapshot snapshot)
    {
        lock (_sync)
        {
            Items.Clear();
            Favorites.Clear();
            _byId.Clear();
            _byHash.Clear();
            _favoritesById.Clear();
            _favoritesByOriginalId.Clear();

            foreach (var item in snapshot.Items)
            {
                var vm = new ClipboardItemViewModel(item, this, _actions!);
                _byId[item.Id] = vm;
                _byHash[item.Hash] = vm;
                Items.Add(vm);
            }

            foreach (var favorite in snapshot.Favorites)
            {
                var vm = new ClipboardFavoriteViewModel(favorite, this, _actions!);
                _favoritesById[favorite.Id] = vm;
                if (!string.IsNullOrWhiteSpace(favorite.OriginalItemId))
                {
                    _favoritesByOriginalId[favorite.OriginalItemId] = vm;
                    if (_byId.TryGetValue(favorite.OriginalItemId, out var source))
                        source.Item.IsFavorite = true;
                }
                Favorites.Add(vm);
            }
        }
    }

    public async Task IngestAsync(ClipboardItem item)
    {
        if (_actions is null)
            throw new InvalidOperationException("Clipboard actions must be attached before ingesting items.");
        if (string.IsNullOrWhiteSpace(item.Hash))
            return;

        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (_byHash.TryGetValue(item.Hash, out var existing))
                {
                    existing.Item.LastUsedUtc = DateTime.UtcNow;
                    _repo.MarkUsed(existing.Item.Id, existing.Item.LastUsedUtc);
                    Dispatch(() =>
                    {
                        Items.Remove(existing);
                        Items.Insert(0, existing);
                        existing.Refresh();
                    });
                    return;
                }

                item.CreatedUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc;
                item.LastUsedUtc = item.LastUsedUtc == default ? item.CreatedUtc : item.LastUsedUtc;
                _repo.Upsert(item);
                var vm = new ClipboardItemViewModel(item, this, _actions);
                _byId[item.Id] = vm;
                _byHash[item.Hash] = vm;
                var prunedIds = _repo.SoftDeleteOverflowAndExpired(
                    _settings.Current.MaxClipboardItems,
                    DateTime.UtcNow.AddDays(-_settings.Current.ClipboardRetentionDays),
                    DateTime.UtcNow);
                var prunedItems = RemovePrunedItemsFromIndexes(prunedIds);

                Dispatch(() =>
                {
                    Items.Insert(0, vm);
                    foreach (var pruned in prunedItems)
                        Items.Remove(pruned);
                });
            }
        });
    }

    public async Task MarkUsedAsync(ClipboardItem item)
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                item.LastUsedUtc = DateTime.UtcNow;
                _repo.MarkUsed(item.Id, item.LastUsedUtc);
            }
        });
    }

    public async Task DeleteAsync(string id)
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_byId.TryGetValue(id, out var vm)) return;
                vm.Item.IsDeleted = true;
                vm.Item.DeletedUtc = DateTime.UtcNow;
                _repo.SoftDelete(id, vm.Item.DeletedUtc.Value);
                _byId.Remove(id);
                _byHash.Remove(vm.Item.Hash);
                Dispatch(() => Items.Remove(vm));
            }
        });
    }

    public async Task ToggleFavoriteAsync(string id)
    {
        if (_favoritesByOriginalId.ContainsKey(id))
            await RemoveFavoriteByOriginalItemIdAsync(id);
        else
            await AddToFavoritesAsync(id);
    }

    public async Task AddToFavoritesAsync(string id)
    {
        if (_actions is null)
            throw new InvalidOperationException("Clipboard actions must be attached before adding favorites.");

        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_byId.TryGetValue(id, out var vm)) return;
                if (_favoritesByOriginalId.ContainsKey(id)) return;

                var favorite = CreateFavoriteSnapshot(vm.Item);
                _repo.UpsertFavorite(favorite);
                _repo.MarkFavorite(id, true);
                vm.Item.IsFavorite = true;

                var favVm = new ClipboardFavoriteViewModel(favorite, this, _actions);
                _favoritesById[favorite.Id] = favVm;
                _favoritesByOriginalId[id] = favVm;

                Dispatch(() =>
                {
                    Favorites.Add(favVm);
                    vm.Refresh();
                });
            }
        });
    }

    public async Task RemoveFavoriteAsync(string favoriteId)
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_favoritesById.TryGetValue(favoriteId, out var favVm)) return;
                _repo.DeleteFavorite(favoriteId);
                _favoritesById.Remove(favoriteId);
                if (!string.IsNullOrWhiteSpace(favVm.Item.OriginalItemId))
                {
                    _favoritesByOriginalId.Remove(favVm.Item.OriginalItemId);
                    _repo.MarkFavorite(favVm.Item.OriginalItemId, false);
                    if (_byId.TryGetValue(favVm.Item.OriginalItemId, out var source))
                        source.Item.IsFavorite = false;
                }

                DeleteFavoriteFiles(favVm.Item);
                Dispatch(() =>
                {
                    Favorites.Remove(favVm);
                    if (favVm.Item.OriginalItemId is not null && _byId.TryGetValue(favVm.Item.OriginalItemId, out var source))
                        source.Refresh();
                });
            }
        });
    }

    private async Task RemoveFavoriteByOriginalItemIdAsync(string itemId)
    {
        if (!_favoritesByOriginalId.TryGetValue(itemId, out var favVm))
            return;
        await RemoveFavoriteAsync(favVm.Item.Id);
    }

    public async Task SetFavoriteOrderAsync(string favoriteId, int order)
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_favoritesById.TryGetValue(favoriteId, out var vm)) return;
                vm.Item.FavoriteOrder = order;
                _repo.UpdateFavoriteOrder(favoriteId, order);
                Dispatch(vm.Refresh);
            }
        });
    }

    public async Task ClearHistoryAsync()
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                _repo.ClearHistory();
                _byId.Clear();
                _byHash.Clear();
                TryDeleteDirectoryContents(BlobDirectory);
                TryDeleteDirectoryContents(ImageDirectory);
                TryDeleteDirectoryContents(ThumbnailDirectory);
                Dispatch(() => Items.Clear());
            }
        });
    }

    public async Task CompactOrphanBlobsAsync()
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                var (removedItems, removedFavorites) = PruneMissingContentLocked();
                var deletedCutoffUtc = _deletionQueue.CutoffUtc;
                var retainedNormalPaths = _repo.LoadRetainedBlobPaths(deletedCutoffUtc);
                _repo.CompactDeletedItems(deletedCutoffUtc);
                DeleteUnreferencedFiles(BlobDirectory, retainedNormalPaths);
                DeleteUnreferencedFiles(ImageDirectory, retainedNormalPaths);
                DeleteUnreferencedFiles(ThumbnailDirectory, retainedNormalPaths);
                DeleteUnreferencedFiles(FavoriteBlobDirectory, _favoritesById.Values.SelectMany(v => new[] { v.Item.BlobPath, v.Item.HtmlBlobPath, v.Item.RtfBlobPath }));
                DeleteUnreferencedFiles(FavoriteImageDirectory, _favoritesById.Values.Select(v => v.Item.ImagePath));
                DeleteUnreferencedFiles(FavoriteThumbnailDirectory, _favoritesById.Values.Select(v => v.Item.ThumbnailPath));

                if (removedItems.Count > 0 || removedFavorites.Count > 0)
                {
                    Dispatch(() =>
                    {
                        foreach (var item in removedItems)
                            Items.Remove(item);
                        foreach (var favorite in removedFavorites)
                            Favorites.Remove(favorite);
                    });
                }
            }
        });
    }

    private ClipboardFavoriteItem CreateFavoriteSnapshot(ClipboardItem source)
    {
        var order = Favorites.Count > 0 ? Favorites.Max(v => v.Item.FavoriteOrder) + 1 : 1;
        return new ClipboardFavoriteItem
        {
            Id = Guid.NewGuid().ToString("N"),
            OriginalItemId = source.Id,
            Type = source.Type,
            CreatedUtc = DateTime.UtcNow,
            SourceCreatedUtc = source.CreatedUtc,
            Hash = source.Hash,
            PreviewText = source.PreviewText,
            PlainText = source.PlainText,
            TextLength = source.TextLength,
            FilePaths = source.FilePaths.Select(f => new ClipboardFilePath
            {
                Path = f.Path,
                IsFolder = f.IsFolder,
                ExistsAtCapture = f.ExistsAtCapture
            }).ToList(),
            BlobPath = CopySnapshotFile(source.BlobPath, FavoriteBlobDirectory),
            HtmlBlobPath = CopySnapshotFile(source.HtmlBlobPath, FavoriteBlobDirectory),
            RtfBlobPath = CopySnapshotFile(source.RtfBlobPath, FavoriteBlobDirectory),
            ImagePath = CopySnapshotFile(source.ImagePath, FavoriteImageDirectory),
            ThumbnailPath = CopySnapshotFile(source.ThumbnailPath, FavoriteThumbnailDirectory),
            ImageWidth = source.ImageWidth,
            ImageHeight = source.ImageHeight,
            SizeBytes = source.SizeBytes,
            SourceAppName = source.SourceAppName,
            SourceAppPath = source.SourceAppPath,
            FavoriteOrder = order
        };
    }

    private static string? CopySnapshotFile(string? sourcePath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return null;

        Directory.CreateDirectory(targetDirectory);
        var target = ClipboardBlobNamer.EnsureUnique(targetDirectory, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target);
        return target;
    }

    private static void DeleteFavoriteFiles(ClipboardFavoriteItem item)
    {
        foreach (var path in new[] { item.BlobPath, item.HtmlBlobPath, item.RtfBlobPath, item.ImagePath, item.ThumbnailPath }
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteFile(path!);
        }
    }

    private static void DeleteItemFiles(ClipboardItem item)
    {
        foreach (var path in new[] { item.BlobPath, item.HtmlBlobPath, item.RtfBlobPath, item.ImagePath, item.ThumbnailPath }
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteFile(path!);
        }
    }

    private void DeleteFavoriteSnapshotLocked(ClipboardFavoriteItem favorite)
    {
        _repo.DeleteFavorite(favorite.Id);
        if (!string.IsNullOrWhiteSpace(favorite.OriginalItemId))
        {
            _favoritesByOriginalId.Remove(favorite.OriginalItemId);
            _repo.MarkFavorite(favorite.OriginalItemId, false);
            if (_byId.TryGetValue(favorite.OriginalItemId, out var source))
            {
                source.Item.IsFavorite = false;
                Dispatch(source.Refresh);
            }
        }

        _favoritesById.Remove(favorite.Id);
        DeleteFavoriteFiles(favorite);
    }

    private (List<ClipboardItemViewModel> Items, List<ClipboardFavoriteViewModel> Favorites) PruneMissingContentLocked()
    {
        var removedItems = new List<ClipboardItemViewModel>();
        var removedFavorites = new List<ClipboardFavoriteViewModel>();
        if (_actions is null)
            return (removedItems, removedFavorites);

        var deletedUtc = DateTime.UtcNow;
        foreach (var vm in Items.ToList())
        {
            if (_actions.HasUsableContent(vm.Item))
                continue;

            vm.Item.IsDeleted = true;
            vm.Item.DeletedUtc = deletedUtc;
            _repo.SoftDelete(vm.Item.Id, deletedUtc);
            _byId.Remove(vm.Item.Id);
            _byHash.Remove(vm.Item.Hash);
            DeleteItemFiles(vm.Item);
            removedItems.Add(vm);
        }

        foreach (var vm in Favorites.ToList())
        {
            if (_actions.HasUsableContent(vm.Item.ToClipboardItem()))
                continue;

            DeleteFavoriteSnapshotLocked(vm.Item);
            removedFavorites.Add(vm);
        }

        return (removedItems, removedFavorites);
    }

    private static void DeleteUnreferencedFiles(string directory, IEnumerable<string?> referencedPaths)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var referenced = referencedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!referenced.Contains(Path.GetFullPath(file)))
                    TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: compact failed {Directory}", LogPrivacy.Format(directory));
        }
    }

    private List<ClipboardItemViewModel> RemovePrunedItemsFromIndexes(IReadOnlyList<string> prunedIds)
    {
        var removed = new List<ClipboardItemViewModel>();
        if (prunedIds.Count == 0)
            return removed;

        foreach (var id in prunedIds)
        {
            if (!_byId.TryGetValue(id, out var vm))
                continue;

            _byId.Remove(id);
            _byHash.Remove(vm.Item.Hash);
            removed.Add(vm);
        }

        return removed;
    }

    private static void TryDeleteDirectoryContents(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: failed to clear {Directory}", LogPrivacy.Format(directory));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClipboardStoreService: deferred file delete {Path}", LogPrivacy.Format(path));
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private static Task InvokeOnDispatcherAsync(Action action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        _maintenanceTimer.Stop();
        _repo.Dispose();
    }

    private sealed record LoadedClipboardSnapshot(
        IReadOnlyList<ClipboardItem> Items,
        IReadOnlyList<ClipboardFavoriteItem> Favorites);
}
