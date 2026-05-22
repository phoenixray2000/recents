using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class WindowsSecretProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsSecret()
    {
        var protectedValue = WindowsSecretProtector.ProtectToBase64("webdav-password");

        Assert.NotEqual("webdav-password", protectedValue);
        Assert.Equal("webdav-password", WindowsSecretProtector.UnprotectFromBase64(protectedValue));
    }

    [Fact]
    public void UnprotectFromBase64_ReturnsEmptyForBlankOrGarbage()
    {
        Assert.Equal(string.Empty, WindowsSecretProtector.UnprotectFromBase64(""));
        Assert.Equal(string.Empty, WindowsSecretProtector.UnprotectFromBase64("not-base64!!"));
    }
}
