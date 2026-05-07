using Recents.App.Models;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardBlobNamerTests
{
    [Fact]
    public void Build_UsesFriendlyPrefixTimestampAndHash()
    {
        var name = ClipboardBlobNamer.Build(
            ClipboardPayloadType.Image,
            new DateTime(2026, 5, 7, 1, 2, 3, DateTimeKind.Utc),
            "abcdef123456",
            ".png");

        Assert.StartsWith("Screenshot-", name);
        Assert.EndsWith("-abcdef12.png", name);
    }
}
