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
    private string _currentNavCategory = "All";

    [ObservableProperty]
    private string _currentChipFilter = "All";

    [ObservableProperty]
    private AppSettings.ViewDensity _currentDensity = AppSettings.ViewDensity.Standard;

    [ObservableProperty]
    private bool _hasItems = true;

    [ObservableProperty]
    private bool _isCompactSidebar;

    // UI 绑定的过滤后视图
    public ICollectionView ItemsView { get; }

    public MainViewModel(RecentIndexService indexService, HotkeyService hotkeyService, StatusHintService statusHint)
    {
        _indexService = indexService;
        _hotkeyService = hotkeyService;
        _statusHint = statusHint;
        _hotkeyService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(HotkeyService.ActiveLabel))
                OnPropertyChanged(nameof(HotkeyDisplay));
        };

        ItemsView = CollectionViewSource.GetDefaultView(_indexService.Items);
        ItemsView.Filter = FilterItem;
        
        // 初始同步计数
        RefreshVisibleCount();
        UpdateHasItems();
        
        _indexService.Items.CollectionChanged += (s, e) => 
        {
            RefreshVisibleCount();
            UpdateHasItems();
        };

        ApplySort();
    }

    partial void OnCurrentNavCategoryChanged(string value) => RefreshItemsView();

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
        CurrentNavCategory = "All";
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

    private bool FilterItem(object obj)
    {
        if (obj is not RecentItemViewModel vm) return false;

        // 1. 顶部 Chip 类型过滤 (ChipFilter) - 优先级高，明确用户意图
        if (CurrentChipFilter != "All")
        {
            if (CurrentChipFilter == "Folders")
            {
                if (!vm.Item.IsFolder) return false;
            }
            else
            {
                // 只有非文件夹才参与类型分类
                if (vm.Item.IsFolder) return false;
                if (vm.Item.ClassificationSource != CurrentChipFilter) return false;
            }
        }
        else
        {
            // Chip 为 All 时，遵循侧边栏导航规则
            if (CurrentNavCategory == "Folders")
            {
                if (!vm.Item.IsFolder) return false;
            }
            else if (CurrentNavCategory == "All")
            {
                // All Files 默认仅显示文件 (PRD §17)
                if (vm.Item.IsFolder) return false;
            }
        }

        // 2. 收藏过滤（如果是收藏视图，必须满足收藏状态）
        if (CurrentNavCategory == "Favorites")
        {
            if (!vm.Item.IsFavorite) return false;
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
}
