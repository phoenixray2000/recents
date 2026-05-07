using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardHashTests
{
    [Fact]
    public void ForText_IsStableAndTypeScoped()
    {
        var first = ClipboardHash.ForText("hello");
        var second = ClipboardHash.ForText("hello");
        var html = ClipboardHash.ForHtml("hello");

        Assert.Equal(first, second);
        Assert.NotEqual(first, html);
    }
}
