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

    [ObservableProperty]
    private string _searchText = string.Empty;

    // UI 绑定的过滤后视图
    public ICollectionView ItemsView { get; }

    public MainViewModel(RecentIndexService indexService)
    {
        _indexService = indexService;

        ItemsView = CollectionViewSource.GetDefaultView(_indexService.Items);
        ItemsView.Filter = FilterItem;
    }

    partial void OnSearchTextChanged(string value)
    {
        // 搜索词变化时重新应用过滤
        ItemsView.Refresh();
    }

    private bool FilterItem(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not RecentItemViewModel vm) return false;

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
