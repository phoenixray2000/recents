using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class SyncClipboardPayloadFormatsTests
{
    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("photo.gif")]
    [InlineData("photo.bmp")]
    [InlineData("photo.png")]
    [InlineData("PHOTO.JPG")]
    public void IsStandardImageFileName_MatchesSyncClipboardStandardImages(string fileName)
    {
        Assert.True(SyncClipboardPayloadFormats.IsStandardImageFileName(fileName));
        Assert.False(SyncClipboardPayloadFormats.IsComplexImageFileName(fileName));
    }

    [Theory]
    [InlineData("photo.heic")]
    [InlineData("photo.heif")]
    [InlineData("photo.webp")]
    [InlineData("photo.avif")]
    [InlineData("PHOTO.HEIC")]
    public void IsComplexImageFileName_MatchesSyncClipboardComplexImages(string fileName)
    {
        Assert.True(SyncClipboardPayloadFormats.IsComplexImageFileName(fileName));
        Assert.False(SyncClipboardPayloadFormats.IsStandardImageFileName(fileName));
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("ARCHIVE.ZIP")]
    public void IsZipFileName_IsCaseInsensitive(string fileName)
    {
        Assert.True(SyncClipboardPayloadFormats.IsZipFileName(fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("../photo.jpg")]
    [InlineData("nested/photo.jpg")]
    public void SafePayloadFileName_StripsUnsafePathParts(string? fileName)
    {
        var result = SyncClipboardPayloadFormats.SafePayloadFileName(fileName, "fallback.bin");

        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
        Assert.NotEqual(string.Empty, result);
    }
}
