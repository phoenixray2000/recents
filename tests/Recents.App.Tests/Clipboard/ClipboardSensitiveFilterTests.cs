using Recents.App.Models;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardSensitiveFilterTests
{
    [Fact]
    public void ShouldSkip_MatchesDefaultTokenPattern()
    {
        var filter = new ClipboardSensitiveFilter(new AppSettings());

        Assert.True(filter.ShouldSkip("token = abc"));
        Assert.False(filter.ShouldSkip("ordinary note"));
    }
}
