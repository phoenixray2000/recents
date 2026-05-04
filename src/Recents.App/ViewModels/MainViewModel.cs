using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Models;
using Recents.App.Services;

namespace Recents.App.ViewModels;

// PRD 搂13 / 搂6.6 涓昏鍥炬ā鍨?
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

    [ObservableProperty]
    private bool _isFavoritesEditMode = false;

    [RelayCommand]
    private void ToggleFavoritesEditMode() => IsFavoritesEditMode = !IsFavoritesEditMode;

    // UI 缁戝畾鐨勮繃婊ゅ悗瑙嗗浘
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

        // 鏀惰棌澶圭幇鍦ㄧ粦瀹氬埌鐙珛鐨?Favorites 闆嗗悎 (PRD 搂6.18 / User Feedback)
        FavoritesView = CollectionViewSource.GetDefaultView(_indexService.Favorites);

        // 鍒濆鍚屾璁℃暟
        RefreshVisibleCount();
        UpdateHasItems();
        UpdateHasFavorites();
        
        ApplySort();

        // Throttled updates (PRD/User Feedback: 鍚姩鍜岄噸鎵椂闃叉 Dispatcher 娣规病瀵艰嚧鐨勯樆濉?
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
        // 鎼滅储璇嶅彉鍖栨椂閲嶆柊搴旂敤杩囨护
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
        // 濡傛灉鍙樻垚浜嗘病鏈夋敹钘忥紝鑷姩閲嶇疆鎵撳紑鐘舵€佷负 true锛堟柟渚夸笅娆℃湁鏀惰棌鏃舵樉绀猴級
        if (!HasFavorites) IsFavoritesDrawerOpen = true;
        OnPropertyChanged(nameof(CombinedFavoritesVisibility));
    }

    partial void OnIsFavoritesDrawerOpenChanged(bool value) => OnPropertyChanged(nameof(CombinedFavoritesVisibility));

    private bool FilterItem(object obj)
    {
        if (obj is not RecentItemViewModel vm) return false;

        // 1. 鏂囦欢澶规帓闄ら€昏緫 (PRD 搂17 / User Request: 鎵€鏈夌殑鏂囦欢澶逛笉瑕佹樉绀哄湪鍏ㄩ儴鏂囦欢涓?
        // 褰撳浜庘€濆叏閮ㄦ枃浠垛€濇爣绛炬椂锛屾枃浠跺す鏃犺鏄惁鏀惰棌閮戒笉鏄剧ず
        if (CurrentChipFilter == "All" && vm.Item.IsFolder) return false;

        // 2. 绯荤粺/闅愯棌鏂囦欢杩囨护锛堟敹钘忓す椤硅眮鍏嶆瑙勫垯锛屼絾浠嶅彈 Chip 绫诲瀷杩囨护锛?
        if (!vm.Item.IsFavorite && ShouldHideBySystemAndHiddenRule(vm.Item, _settingsService.Current)) return false;

        // 3. 椤堕儴 Chip 绫诲瀷杩囨护 (ChipFilter)
        if (CurrentChipFilter == "Folders")
        {
            if (!vm.Item.IsFolder) return false;
        }
        else if (CurrentChipFilter != "All")
        {
            // 鍏朵粬绫诲瀷杩囨护锛堟枃妗ｃ€佸浘鐗囩瓑锛夛紝闈炴枃浠跺す鍙備笌
            if (vm.Item.IsFolder) return false;
            if (vm.Item.ClassificationSource != CurrentChipFilter) return false;
        }


        // 鎼滅储閫昏緫
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var tokens = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;

        // 鎵╁睍鍚嶇簿纭尮閰嶏紙棣栧瓧绗︿负 .锛?
        if (tokens.Length == 1 && tokens[0].StartsWith('.'))
        {
            return string.Equals(vm.Extension, tokens[0], StringComparison.OrdinalIgnoreCase);
        }

        // 璺緞鐗囨鍖归厤锛堝惈 \ 鎴?/锛?
        if (tokens.Length == 1 && (tokens[0].Contains('\\') || tokens[0].Contains('/')))
        {
            return vm.DisplayPath.Contains(tokens[0].Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        // 澶?token AND 妯＄硦鍖归厤
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

