using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private string _currentCategory = "All";

    [ObservableProperty]
    private bool _hasItems = true;

    // UI 绑定的过滤后视图
    public ICollectionView ItemsView { get; }

    public MainViewModel(RecentIndexService indexService, HotkeyService hotkeyService, StatusHintService statusHint)
    {
        _indexService = indexService;
        _hotkeyService = hotkeyService;
        _statusHint = statusHint;

        ItemsView = CollectionViewSource.GetDefaultView(_indexService.Items);
        ItemsView.Filter = FilterItem;
        
        // 初始同步计数
        _statusHint.UpdateCount(_indexService.Items.Count);
        HasItems = _indexService.Items.Count > 0;
        
        _indexService.Items.CollectionChanged += (s, e) => 
        {
            _statusHint.UpdateCount(_indexService.Items.Count);
            HasItems = _indexService.Items.Count > 0;
        };

        ApplySort();
    }

    partial void OnCurrentCategoryChanged(string value) => ItemsView.Refresh();

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
        ItemsView.Refresh();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not RecentItemViewModel vm) return false;

        // B6. 分类过滤
        if (CurrentCategory == "Folders")
        {
            if (!vm.Item.IsFolder) return false;
        }
        else if (CurrentCategory != "All")
        {
            // 只有非文件夹才参与类型分类
            if (vm.Item.IsFolder) return false;
            if (vm.Item.ClassificationSource != CurrentCategory) return false;
        }
        else
        {
            // "All" 模式下默认不显示文件夹，除非是搜索模式
            if (vm.Item.IsFolder && string.IsNullOrEmpty(SearchText)) return false;
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
}
