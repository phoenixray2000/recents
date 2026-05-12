using Recents.App.Models;
using Recents.App.ViewModels;
using Xunit;

namespace Recents.App.Tests;

public sealed class SettingsDefaultsTests
{
    [Fact]
    public void ExternalSpacePreviewIsOptInByDefault()
    {
        var settings = new AppSettings();

        Assert.False(settings.ExternalSpacePreviewEnabled);
    }

    [Fact]
    public void SettingsVersionUsesInformationalVersionWhenAvailable()
    {
        var version = SettingsViewModel.GetDisplayVersion("1.2", new Version(1, 2, 0, 0));

        Assert.Equal("1.2", version);
    }
}
