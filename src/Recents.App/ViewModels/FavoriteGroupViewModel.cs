using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Localization;
using Recents.App.Models;

namespace Recents.App.ViewModels;

public partial class FavoriteGroupViewModel : ObservableObject
{
    private readonly MainViewModel? _owner;
    private int _itemCount;

    public FavoriteGroup Group { get; }
    public string Id => Group.Id;
    public string Name => string.IsNullOrWhiteSpace(Group.Name) && FavoriteGroup.IsDefaultGroupId(Group.Id)
        ? Loc.T("Main_Favorites_Group_DefaultGroupName")
        : Group.Name;
    public int Order => Group.Order;
    public bool IsCollapsed => Group.IsCollapsed;
    public string CollapseIcon => Group.IsCollapsed ? "\uE76C" : "\uE70D";
    public string ItemCountText => string.Format(Loc.T("Main_Favorites_Group_Count"), _itemCount);
    public bool CanDelete => !FavoriteGroup.IsDefaultGroupId(Group.Id);

    public FavoriteGroupViewModel(FavoriteGroup group, int itemCount, MainViewModel? owner = null)
    {
        Group = group;
        _itemCount = itemCount;
        _owner = owner;
    }

    public void Refresh(int itemCount)
    {
        _itemCount = itemCount;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void ToggleCollapsed() => _owner?.ToggleFavoriteGroupCollapsed(this);

    [RelayCommand]
    private void Rename() => _owner?.RenameFavoriteGroup(this);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() => _owner?.DeleteFavoriteGroup(this);
}
