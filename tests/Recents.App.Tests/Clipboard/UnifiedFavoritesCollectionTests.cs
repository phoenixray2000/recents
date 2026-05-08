using Recents.App.Models;
using Recents.App.Services.Clipboard;
using Recents.App.ViewModels;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class UnifiedFavoritesCollectionTests
{
    [Fact]
    public void OrderFavoritesForDisplay_UsesFavoriteOrderAcrossRecentAndClipboardItems()
    {
        var recent = new RecentItemViewModel(
            new RecentItem
            {
                NormalizedPath = @"C:\work\recent.txt",
                DisplayName = "recent.txt",
                FavoriteOrder = 3,
                FavoriteTime = DateTime.UtcNow.AddMinutes(-3),
            },
            null!,
            null!);
        var clipboardFirst = new ClipboardFavoriteViewModel(
            new ClipboardFavoriteItem
            {
                Id = "clip-first",
                Type = ClipboardPayloadType.Text,
                PreviewText = "clip first",
                FavoriteOrder = 1,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            },
            null!,
            null!);
        var clipboardSecond = new ClipboardFavoriteViewModel(
            new ClipboardFavoriteItem
            {
                Id = "clip-second",
                Type = ClipboardPayloadType.Text,
                PreviewText = "clip second",
                FavoriteOrder = 2,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-2),
            },
            null!,
            null!);

        var ordered = MainViewModel.OrderFavoritesForDisplay([recent, clipboardSecond, clipboardFirst]);

        Assert.Same(clipboardFirst, ordered[0]);
        Assert.Same(clipboardSecond, ordered[1]);
        Assert.Same(recent, ordered[2]);
    }

    [Fact]
    public void FavoriteDisplayName_UsesAliasWithoutChangingSourceNames()
    {
        var recent = new RecentItemViewModel(
            new RecentItem
            {
                NormalizedPath = @"C:\work\contract.docx",
                DisplayName = "contract.docx",
                IsFavorite = true,
                FavoriteAlias = "Client contract",
            },
            null!,
            null!);
        var clipboard = new ClipboardFavoriteViewModel(
            new ClipboardFavoriteItem
            {
                Id = "clip",
                Type = ClipboardPayloadType.Text,
                PreviewText = "actual clipboard text",
                PlainText = "actual clipboard text",
                FavoriteAlias = "Release notes",
            },
            null!,
            new ClipboardActionService(null!));

        Assert.Equal("Client contract", recent.FavoriteDisplayName);
        Assert.Equal("contract.docx", recent.DisplayName);
        Assert.Equal("Release notes", clipboard.DisplayName);
        Assert.Equal("actual clipboard text", clipboard.OriginalDisplayName);
    }

    [Fact]
    public void BuildFavoritesDisplayList_AddsAssignedItemsUnderGroupHeader()
    {
        var recent = CreateRecentFavorite(@"C:\work\contract.docx", "contract.docx", 1);
        var clipboard = CreateClipboardFavorite("clip", "release notes", 2);
        var group = new FavoriteGroup
        {
            Id = "work",
            Name = "Work",
            Order = 1
        };

        var rows = MainViewModel.BuildFavoritesDisplayList(
            [recent, clipboard],
            [group],
            new Dictionary<string, string>
            {
                [MainViewModel.GetFavoriteAssignmentKey(clipboard)!] = group.Id
            });

        Assert.Same(recent, rows[0]);
        var header = Assert.IsType<FavoriteGroupViewModel>(rows[1]);
        Assert.Equal("Work", header.Name);
        Assert.Same(clipboard, rows[2]);
    }

    [Fact]
    public void BuildFavoritesDisplayList_HidesCollapsedGroupItems()
    {
        var recent = CreateRecentFavorite(@"C:\work\contract.docx", "contract.docx", 1);
        var group = new FavoriteGroup
        {
            Id = "work",
            Name = "Work",
            Order = 1,
            IsCollapsed = true
        };

        var rows = MainViewModel.BuildFavoritesDisplayList(
            [recent],
            [group],
            new Dictionary<string, string>
            {
                [MainViewModel.GetFavoriteAssignmentKey(recent)!] = group.Id
            });

        var header = Assert.Single(rows);
        Assert.IsType<FavoriteGroupViewModel>(header);
    }

    [Fact]
    public void BuildFavoritesDisplayList_PutsUngroupedItemsInDefaultGroup()
    {
        var recent = CreateRecentFavorite(@"C:\work\contract.docx", "contract.docx", 1);
        var defaultGroup = new FavoriteGroup
        {
            Id = FavoriteGroup.DefaultGroupId,
            Order = 1
        };

        var rows = MainViewModel.BuildFavoritesDisplayList(
            [recent],
            [defaultGroup],
            new Dictionary<string, string>());

        var header = Assert.IsType<FavoriteGroupViewModel>(rows[0]);
        Assert.True(FavoriteGroup.IsDefaultGroupId(header.Id));
        Assert.Same(recent, rows[1]);
    }

    [Fact]
    public void BuildFavoritesDisplayList_OrdersGroupsByGroupOrder()
    {
        var defaultGroup = new FavoriteGroup
        {
            Id = FavoriteGroup.DefaultGroupId,
            Order = 2
        };
        var workGroup = new FavoriteGroup
        {
            Id = "work",
            Name = "Work",
            Order = 1
        };

        var rows = MainViewModel.BuildFavoritesDisplayList(
            Array.Empty<object>(),
            [defaultGroup, workGroup],
            new Dictionary<string, string>());

        Assert.Equal("work", Assert.IsType<FavoriteGroupViewModel>(rows[0]).Id);
        Assert.Equal(FavoriteGroup.DefaultGroupId, Assert.IsType<FavoriteGroupViewModel>(rows[1]).Id);
    }

    private static RecentItemViewModel CreateRecentFavorite(string path, string displayName, int order) =>
        new(
            new RecentItem
            {
                NormalizedPath = path,
                DisplayName = displayName,
                FavoriteOrder = order,
                FavoriteTime = DateTime.UtcNow.AddMinutes(-order),
                IsFavorite = true
            },
            null!,
            null!);

    private static ClipboardFavoriteViewModel CreateClipboardFavorite(string id, string previewText, int order) =>
        new(
            new ClipboardFavoriteItem
            {
                Id = id,
                Type = ClipboardPayloadType.Text,
                PreviewText = previewText,
                FavoriteOrder = order,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-order)
            },
            null!,
            null!);
}
