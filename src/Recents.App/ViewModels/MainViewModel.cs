using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Clipboard;
using Recents.App.Utils;

namespace Recents.App.ViewModels;

// PRD §13 / §6.6 主视图模型
public partial class MainViewModel : ObservableObject
{
    private readonly RecentIndexService _indexService;
    private readonly ClipboardStoreService _clipboardStore;
    private readonly HotkeyService _hotkeyService;
    private readonly StatusHintService _statusHint;
    private readonly SettingsService _settingsService;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer;
    private readonly ObservableCollection<object> _unifiedFavorites = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    public string HotkeyDisplay => _hotkeyService.ActiveLabel;
    
    public StatusHintService Status => _statusHint;

    public enum SortOption
    {
        RecentTime,
        DisplayName,
        Size,
        ClassificationSource
    }

    public enum ContentMode
    {
        Recent,
        Clipboard
    }

    [ObservableProperty]
    private ContentMode _currentMode = ContentMode.Recent;

    [ObservableProperty]
    private SortOption _currentSort = SortOption.RecentTime;

    [ObservableProperty]
    private string _currentChipFilter = "All";

    [ObservableProperty]
    private string _clipboardSubFilter = "All";

    [ObservableProperty]
    private AppSettings.ViewDensity _currentDensity = AppSettings.ViewDensity.Standard;

    [ObservableProperty]
    private bool _hasItems = true;

    [ObservableProperty]
    private bool _hasFavorites = false;

    [ObservableProperty]
    private bool _isFavoritesDrawerOpen = true;
    
    [ObservableProperty]
    private bool _isInitializing = true;

    [ObservableProperty]
    private bool _isFavoritesEditMode = false;

    [RelayCommand]
    private void ToggleFavoritesEditMode() => IsFavoritesEditMode = !IsFavoritesEditMode;

    // UI 绑定的过滤后视图
    public ICollectionView ItemsView { get; }
    public ICollectionView ClipboardItemsView { get; }
    public ICollectionView ActiveItemsView => IsClipboardMode ? ClipboardItemsView : ItemsView;
    public ICollectionView FavoritesView { get; }
    public bool IsClipboardMode => CurrentMode == ContentMode.Clipboard;

    public MainViewModel(
        RecentIndexService indexService,
        ClipboardStoreService clipboardStore,
        HotkeyService hotkeyService,
        StatusHintService statusHint,
        SettingsService settingsService)
    {
        _indexService = indexService;
        _clipboardStore = clipboardStore;
        _hotkeyService = hotkeyService;
        _statusHint = statusHint;
        _settingsService = settingsService;
        _hotkeyService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(HotkeyService.ActiveLabel))
                OnPropertyChanged(nameof(HotkeyDisplay));
        };

        ItemsView = CollectionViewSource.GetDefaultView(_indexService.Items);
        ItemsView.Filter = FilterItem;
        ClipboardItemsView = CollectionViewSource.GetDefaultView(_clipboardStore.Items);
        ClipboardItemsView.Filter = FilterClipboardItem;

        FavoritesView = CollectionViewSource.GetDefaultView(_unifiedFavorites);
        FavoritesView.SortDescriptions.Add(new SortDescription("FavoriteOrder", ListSortDirection.Ascending));
        RebuildUnifiedFavorites();

        // 初始同步计数
        RefreshVisibleCount();
        UpdateHasItems();
        UpdateHasFavorites();
        
        ApplySort();

        // Throttled updates (PRD/User Feedback: 启动和重扫时防止 Dispatcher 淹没导致阻塞)
        _updateTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            System.Windows.Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _updateTimer.Tick += (s, e) =>
        {
            _updateTimer.Stop();
            RefreshVisibleCount();
            UpdateHasItems();
            UpdateHasFavorites();
            RebuildUnifiedFavorites();
            FavoritesView.Refresh();
        };

        _indexService.IndexChanged += () =>
        {
            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        };

        _indexService.Items.CollectionChanged += (s, e) =>
        {
            if (IsInitializing && _indexService.Items.Count > 0)
                IsInitializing = false;

            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        };

        _indexService.Favorites.CollectionChanged += (s, e) =>
        {
            RebuildUnifiedFavorites();
            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        };

        _clipboardStore.Items.CollectionChanged += (s, e) =>
        {
            if (IsInitializing && _clipboardStore.Items.Count > 0)
                IsInitializing = false;

            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        };

        _clipboardStore.Favorites.CollectionChanged += (s, e) =>
        {
            RebuildUnifiedFavorites();
            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        };
    }


    partial void OnCurrentModeChanged(ContentMode value)
    {
        OnPropertyChanged(nameof(IsClipboardMode));
        OnPropertyChanged(nameof(ActiveItemsView));
        ApplySort();
        RefreshItemsView();
    }

    partial void OnCurrentChipFilterChanged(string value)
    {
        CurrentMode = value == "Clipboard" ? ContentMode.Clipboard : ContentMode.Recent;
        RefreshItemsView();
    }

    partial void OnClipboardSubFilterChanged(string value) => RefreshItemsView();

    partial void OnCurrentSortChanged(SortOption value) => ApplySort();

    private void ApplySort()
    {
        ItemsView.SortDescriptions.Clear();
        ClipboardItemsView.SortDescriptions.Clear();
        switch (CurrentSort)
        {
            case SortOption.RecentTime:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.RecentTime", ListSortDirection.Descending));
                ClipboardItemsView.SortDescriptions.Add(new SortDescription("Item.LastUsedUtc", ListSortDirection.Descending));
                ClipboardItemsView.SortDescriptions.Add(new SortDescription("Item.CreatedUtc", ListSortDirection.Descending));
                break;
            case SortOption.DisplayName:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.DisplayName", ListSortDirection.Ascending));
                ClipboardItemsView.SortDescriptions.Add(new SortDescription("PreviewText", ListSortDirection.Ascending));
                break;
            case SortOption.Size:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.SizeBytes", ListSortDirection.Descending));
                ClipboardItemsView.SortDescriptions.Add(new SortDescription("Item.SizeBytes", ListSortDirection.Descending));
                break;
            case SortOption.ClassificationSource:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.Extension", ListSortDirection.Ascending));
                ClipboardItemsView.SortDescriptions.Add(new SortDescription("TypeLabel", ListSortDirection.Ascending));
                break;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        // 搜索词变化时重新应用过滤
        RefreshItemsView();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        CurrentChipFilter = "All";
        ClipboardSubFilter = "All";
        RefreshItemsView();
    }

    [RelayCommand]
    private async Task RebuildIndex()
    {
        await _indexService.RebuildAsync();
        RefreshItemsView();
    }

    private void RefreshItemsView()
    {
        ItemsView.Refresh();
        ClipboardItemsView.Refresh();
        OnPropertyChanged(nameof(ActiveItemsView));
        UpdateHasItems();
        RefreshVisibleCount();
    }

    private void RefreshVisibleCount()
    {
        var count = ActiveItemsView.Cast<object>().Count();
        _statusHint.UpdateCount(count);
    }

    private void UpdateHasItems()
    {
        HasItems = ActiveItemsView.Cast<object>().Any();
    }

    public bool CombinedFavoritesVisibility => HasFavorites && IsFavoritesDrawerOpen;

    [RelayCommand]
    private void ToggleFavorites() => IsFavoritesDrawerOpen = !IsFavoritesDrawerOpen;

    private void UpdateHasFavorites()
    {
        HasFavorites = _unifiedFavorites.Any();
        // 如果变成没有收藏，自动重置打开状态为 true，方便下次有收藏时显示
        if (!HasFavorites) IsFavoritesDrawerOpen = true;
        OnPropertyChanged(nameof(CombinedFavoritesVisibility));
    }

    partial void OnIsFavoritesDrawerOpenChanged(bool value) => OnPropertyChanged(nameof(CombinedFavoritesVisibility));

    public async Task ApplyUnifiedFavoriteOrderAsync(IReadOnlyList<object> orderedItems)
    {
        for (var i = 0; i < orderedItems.Count; i++)
        {
            var order = i + 1;
            switch (orderedItems[i])
            {
                case RecentItemViewModel recent:
                    await _indexService.SetFavoriteOrderAsync(recent.Item.NormalizedPath, order);
                    recent.Item.FavoriteOrder = order;
                    recent.Refresh();
                    break;
                case ClipboardFavoriteViewModel clip:
                    await _clipboardStore.SetFavoriteOrderAsync(clip.Item.Id, order);
                    clip.Item.FavoriteOrder = order;
                    clip.Refresh();
                    break;
            }
        }

        RebuildUnifiedFavorites();
        FavoritesView.Refresh();
        UpdateHasFavorites();
    }

    private void RebuildUnifiedFavorites()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RebuildUnifiedFavorites));
            return;
        }

        var merged = OrderFavoritesForDisplay(
            _indexService.Favorites.Cast<object>().Concat(_clipboardStore.Favorites.Cast<object>()));

        _unifiedFavorites.Clear();
        foreach (var item in merged)
            _unifiedFavorites.Add(item);
    }

    internal static List<object> OrderFavoritesForDisplay(IEnumerable<object> favorites) =>
        favorites
            .OrderBy(GetFavoriteOrder)
            .ThenBy(GetFavoriteTieBreaker)
            .ToList();

    private static int GetFavoriteOrder(object item) => item switch
    {
        RecentItemViewModel recent => recent.Item.FavoriteOrder,
        ClipboardFavoriteViewModel clip => clip.Item.FavoriteOrder,
        _ => int.MaxValue
    };

    private static DateTime GetFavoriteTieBreaker(object item) => item switch
    {
        RecentItemViewModel recent => recent.Item.FavoriteTime ?? DateTime.MinValue,
        ClipboardFavoriteViewModel clip => clip.Item.CreatedUtc,
        _ => DateTime.MinValue
    };

    private bool FilterItem(object obj)
    {
        if (obj is not RecentItemViewModel vm) return false;

        // 1. 文件夹排除逻辑 (PRD §17 / User Request: 所有文件夹不显示在全部文件中)
        // 当处于“全部文件”标签时，文件夹无论是否收藏都不显示
        if (CurrentChipFilter == "All" && vm.Item.IsFolder) return false;

        // 2. 系统/隐藏文件过滤（收藏夹项豁免此规则，但仍受 Chip 类型过滤）
        if (!vm.Item.IsFavorite && ShouldHideBySystemAndHiddenRule(vm.Item, _settingsService.Current)) return false;

        // 3. 顶部 Chip 类型过滤 (ChipFilter)
        if (CurrentChipFilter == "Folders")
        {
            if (!vm.Item.IsFolder) return false;
        }
        else if (CurrentChipFilter != "All")
        {
            // 其他类型过滤（文档、图片等），非文件夹参与
            if (vm.Item.IsFolder) return false;
            if (vm.Item.ClassificationSource != CurrentChipFilter) return false;
        }


        return PathMatcher.MatchesSearch(vm.Item, SearchText);
    }

    private bool FilterClipboardItem(object obj)
    {
        if (obj is not ClipboardItemViewModel vm) return false;

        if (ClipboardSubFilter != "All")
        {
            var expected = ClipboardSubFilter == "Images" ? "Image" : ClipboardSubFilter;
            if (!string.Equals(vm.Item.Type.ToString(), expected, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var query = SearchText.Trim();
        if (vm.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(vm.Item.PlainText) &&
            vm.Item.PlainText.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        return vm.Item.FilePaths.Any(f =>
            f.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileName(f.Path).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    public void UpdateHotkey(string hotkey)
    {
        _hotkeyService.UpdateHotkey(hotkey);
    }

    public void UpdatePopPasteHotkey(string hotkey)
    {
        _hotkeyService.UpdatePopPasteHotkey(hotkey);
    }

    private bool ShouldHideBySystemAndHiddenRule(RecentItem item, AppSettings settings)
    {
        if (settings.ShowSystemAndHiddenFiles) return false;

        if (IsUnderDotDirectory(item.NormalizedPath)) return true;

        try
        {
            var attributes = System.IO.File.GetAttributes(item.NormalizedPath);
            if ((attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden ||
                (attributes & System.IO.FileAttributes.System) == System.IO.FileAttributes.System)
            {
                return true;
            }
        }
        catch
        {
            // Ignore if file doesn't exist or no access
        }

        return false;
    }

    private bool IsUnderDotDirectory(string targetPath)
    {
        if (string.IsNullOrEmpty(targetPath)) return false;
        var parts = targetPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith('.')) return true;
        }
        return false;
    }
}

