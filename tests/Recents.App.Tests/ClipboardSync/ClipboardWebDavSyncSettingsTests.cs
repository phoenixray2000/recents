using Recents.App.Models;
using Recents.App.Services;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class ClipboardWebDavSyncSettingsTests
{
    [Fact]
    public void Normalize_CreatesStableDeviceIdAndClampsIntervals()
    {
        var settings = new AppSettings
        {
            ClipboardWebDavSync = new ClipboardWebDavSyncSettings
            {
                Enabled = true,
                PollIntervalSeconds = 1,
                TimeoutSeconds = 1,
                RetryTimes = 99
            }
        };

        SettingsService.NormalizeForTests(settings);

        Assert.True(Guid.TryParse(settings.ClipboardWebDavSync.DeviceId, out _));
        Assert.Equal(5, settings.ClipboardWebDavSync.PollIntervalSeconds);
        Assert.Equal(10, settings.ClipboardWebDavSync.TimeoutSeconds);
        Assert.Equal(5, settings.ClipboardWebDavSync.RetryTimes);
    }

    [Fact]
    public void Normalize_AddsTrailingSlashAndTrimsUrl()
    {
        var settings = new AppSettings
        {
            ClipboardWebDavSync = new ClipboardWebDavSyncSettings
            {
                RemoteDirectoryUrl = " https://example.com/dav/recents "
            }
        };

        SettingsService.NormalizeForTests(settings);

        Assert.Equal("https://example.com/dav/recents/", settings.ClipboardWebDavSync.RemoteDirectoryUrl);
    }

    [Fact]
    public void Normalize_PreservesExistingDeviceId()
    {
        var existing = Guid.NewGuid().ToString("D");
        var settings = new AppSettings
        {
            ClipboardWebDavSync = new ClipboardWebDavSyncSettings { DeviceId = existing }
        };

        SettingsService.NormalizeForTests(settings);

        Assert.Equal(existing, settings.ClipboardWebDavSync.DeviceId);
    }
}
