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
}
