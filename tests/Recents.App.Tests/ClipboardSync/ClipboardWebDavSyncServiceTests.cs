using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class ClipboardWebDavSyncServiceTests
{
    private static RecentClipboardProfile Remote(string device, string hash, DateTime updated) =>
        new() { Schema = RecentClipboardProfile.SchemaVersion, DeviceId = device, Hash = hash, UpdatedUtc = updated };

    [Fact]
    public void ShouldApplyRemote_IgnoresOwnDeviceProfile()
    {
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            Remote("local", "hash", DateTime.UtcNow),
            localDeviceId: "local", lastSyncedHash: null, lastAppliedRemoteUtc: null, lastLocalCaptureUtc: null));
    }

    [Fact]
    public void ShouldApplyRemote_IgnoresSchemaMismatch()
    {
        var p = Remote("remote", "hash", DateTime.UtcNow);
        p.Schema = 999;
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            p, "local", null, null, null));
    }

    [Fact]
    public void ShouldApplyRemote_IgnoresSameHash()
    {
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            Remote("remote", "same", DateTime.UtcNow),
            "local", lastSyncedHash: "same", lastAppliedRemoteUtc: null, lastLocalCaptureUtc: null));
    }

    [Fact]
    public void ShouldApplyRemote_IgnoresNotNewerThanLastApplied()
    {
        var now = DateTime.UtcNow;
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            Remote("remote", "h", now.AddMinutes(-1)),
            "local", null, lastAppliedRemoteUtc: now, lastLocalCaptureUtc: null));
    }

    [Fact]
    public void ShouldApplyRemote_DoesNotClobberNewerLocal()
    {
        var now = DateTime.UtcNow;
        Assert.False(ClipboardWebDavSyncService.ShouldApplyRemote(
            Remote("remote", "h", now.AddMinutes(-1)),
            "local", null, lastAppliedRemoteUtc: null, lastLocalCaptureUtc: now));
    }

    [Fact]
    public void ShouldApplyRemote_AppliesNewerDifferentRemote()
    {
        var now = DateTime.UtcNow;
        Assert.True(ClipboardWebDavSyncService.ShouldApplyRemote(
            Remote("remote", "remote-hash", now),
            "local", lastSyncedHash: "local-hash", lastAppliedRemoteUtc: now.AddMinutes(-5),
            lastLocalCaptureUtc: now.AddMinutes(-10)));
    }
}
