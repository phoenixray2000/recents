using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Recents.App.Services.Clipboard;

namespace Recents.App.ViewModels;

public partial class ClipboardPopupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ClipboardItemViewModel? _selectedItem;

    public ICollectionView ItemsView { get; }
    public int MaxRows { get; }

    public ClipboardPopupViewModel(ClipboardStoreService store, int maxRows = 8)
    {
        MaxRows = Math.Clamp(maxRows, 3, 30);
        ItemsView = new ListCollectionView(store.Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Clear();
        ItemsView.SortDescriptions.Add(new SortDescription("Item.LastUsedUtc", ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription("Item.CreatedUtc", ListSortDirection.Descending));
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
        SelectedItem = ItemsView.Cast<object>().OfType<ClipboardItemViewModel>().FirstOrDefault();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not ClipboardItemViewModel vm)
            return false;

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
}
