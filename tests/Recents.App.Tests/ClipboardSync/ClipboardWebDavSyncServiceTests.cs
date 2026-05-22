using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class ClipboardWebDavSyncServiceTests
{
    private static SyncClipboardProfile Remote(string hash) =>
        new() { Type = SyncClipboardProfileType.Text, Hash = hash, Text = "text" };

    [Fact]
    public void ShouldApplyRemote_IgnoresNullProfile()
    {
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            null,
            lastSyncedRemoteKey: null));
    }

    [Fact]
    public void ShouldApplyRemote_IgnoresUnknownProfileType()
    {
        var p = new SyncClipboardProfile { Type = SyncClipboardProfileType.Unknown, Hash = "hash" };
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            p, null));
    }

    [Fact]
    public void ShouldApplyRemote_IgnoresSameRemoteContentKey()
    {
        var remote = Remote("same");
        var key = ClipboardWebDavSyncService.RemoteContentKey(remote);

        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            remote,
            lastSyncedRemoteKey: key));
    }

    [Fact]
    public void ShouldApplyRemote_UsesTypeAndTextWhenHashIsMissing()
    {
        var remote = new SyncClipboardProfile { Type = SyncClipboardProfileType.Text, Text = "same", Size = 4 };
        var key = ClipboardWebDavSyncService.RemoteContentKey(remote);

        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            remote,
            lastSyncedRemoteKey: key));
    }

    [Fact]
    public void ShouldApplyRemote_AppliesDifferentRemote()
    {
        Assert.True(ClipboardWebDavSyncService.ShouldApplyRemote(
            Remote("remote-hash"),
            lastSyncedRemoteKey: "Text:local-hash"));
    }
}
