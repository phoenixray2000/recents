using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Models;
using Recents.App.Services;

namespace Recents.App.ViewModels;

// PRD §13 / §6.6 主视图模型
public partial class MainViewModel : ObservableObject
{
    private readonly RecentIndexService _indexService;
    private readonly HotkeyService _hotkeyService;
    private readonly StatusHintService _statusHint;
    private readonly SettingsService _settingsService;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer;

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

    [ObservableProperty]
    private SortOption _currentSort = SortOption.RecentTime;

    [ObservableProperty]
    private string _currentChipFilter = "All";

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

    // UI 绑定的过滤后视图
    public ICollectionView ItemsView { get; }
    public ICollectionView FavoritesView { get; }

    public MainViewModel(RecentIndexService indexService, HotkeyService hotkeyService, StatusHintService statusHint, SettingsService settingsService)
    {
        _indexService = indexService;
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

        // 收藏夹现在绑定到独立的 Favorites 集合 (PRD §6.18 / User Feedback)
        FavoritesView = CollectionViewSource.GetDefaultView(_indexService.Favorites);

        // 初始同步计数
        RefreshVisibleCount();
        UpdateHasItems();
        UpdateHasFavorites();
        
        ApplySort();

        // Throttled updates (PRD/User Feedback: 启动和重扫时防止 Dispatcher 淹没导致的阻塞)
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
            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        };
    }


    partial void OnCurrentChipFilterChanged(string value) => RefreshItemsView();

    partial void OnCurrentSortChanged(SortOption value) => ApplySort();

    private void ApplySort()
    {
        ItemsView.SortDescriptions.Clear();
        switch (CurrentSort)
        {
            case SortOption.RecentTime:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.RecentTime", ListSortDirection.Descending));
                break;
            case SortOption.DisplayName:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.DisplayName", ListSortDirection.Ascending));
                break;
            case SortOption.Size:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.SizeBytes", ListSortDirection.Descending));
                break;
            case SortOption.ClassificationSource:
                ItemsView.SortDescriptions.Add(new SortDescription("Item.Extension", ListSortDirection.Ascending));
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
        UpdateHasItems();
        RefreshVisibleCount();
    }

    private void RefreshVisibleCount()
    {
        var count = ItemsView.Cast<object>().Count();
        _statusHint.UpdateCount(count);
    }

    private void UpdateHasItems()
    {
        HasItems = ItemsView.Cast<object>().Any();
    }

    public bool CombinedFavoritesVisibility => HasFavorites && IsFavoritesDrawerOpen;

    [RelayCommand]
    private void ToggleFavorites() => IsFavoritesDrawerOpen = !IsFavoritesDrawerOpen;

    private void UpdateHasFavorites()
    {
        HasFavorites = _indexService.Favorites.Any();
        // 如果变成了没有收藏，自动重置打开状态为 true（方便下次有收藏时显示）
        if (!HasFavorites) IsFavoritesDrawerOpen = true;
        OnPropertyChanged(nameof(CombinedFavoritesVisibility));
    }

    partial void OnIsFavoritesDrawerOpenChanged(bool value) => OnPropertyChanged(nameof(CombinedFavoritesVisibility));

    private bool FilterItem(object obj)
    {
        if (obj is not RecentItemViewModel vm) return false;

        // 1. 文件夹排除逻辑 (PRD §17 / User Request: 所有的文件夹不要显示在全部文件中)
        // 当处于“全部文件”标签时，文件夹无论是否收藏都不显示
        if (CurrentChipFilter == "All" && vm.Item.IsFolder) return false;

        // 2. 收藏优先级高于其他过滤规则（系统/隐藏）
        if (vm.Item.IsFavorite) return true;

        if (ShouldHideBySystemAndHiddenRule(vm.Item, _settingsService.Current)) return false;

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


        // 搜索逻辑
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var tokens = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;

        // 扩展名精确匹配（首字符为 .）
        if (tokens.Length == 1 && tokens[0].StartsWith('.'))
        {
            return string.Equals(vm.Extension, tokens[0], StringComparison.OrdinalIgnoreCase);
        }

        // 路径片段匹配（含 \ 或 /）
        if (tokens.Length == 1 && (tokens[0].Contains('\\') || tokens[0].Contains('/')))
        {
            return vm.DisplayPath.Contains(tokens[0].Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        // 多 token AND 模糊匹配
        foreach (var token in tokens)
        {
            bool match = vm.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                         vm.DisplayPath.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (!match) return false;
        }

        return true;
    }
    public void UpdateHotkey(string hotkey)
    {
        _hotkeyService.UpdateHotkey(hotkey);
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
