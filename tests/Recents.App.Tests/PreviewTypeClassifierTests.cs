using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class PreviewTypeClassifierTests
{
    [Fact]
    public void Classify_HtmlFilesAsRenderedHtmlPreview()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "preview-test.html");
        File.WriteAllText(path, "<html><body><h1>Hello</h1></body></html>");

        try
        {
            var (kind, _) = PreviewTypeClassifier.Classify(path);

            Assert.Equal(PreviewKind.Html, kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".wma", PreviewKind.Audio)]
    [InlineData(".mp4", PreviewKind.Video)]
    [InlineData(".wmv", PreviewKind.Video)]
    public void Classify_CommonWindowsMediaFilesAsMediaPreview(string extension, PreviewKind expected)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "preview-media" + extension);
        File.WriteAllText(path, "placeholder");

        try
        {
            var (kind, _) = PreviewTypeClassifier.Classify(path);

            Assert.Equal(expected, kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".docx")]
    [InlineData(".docm")]
    [InlineData(".xls")]
    [InlineData(".xlsx")]
    [InlineData(".xlsm")]
    [InlineData(".ppt")]
    [InlineData(".pptx")]
    [InlineData(".pptm")]
    [InlineData(".rtf")]
    public void Classify_OfficeFilesAsShellHandlerPreview(string extension)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "preview-office" + extension);
        File.WriteAllText(path, "placeholder");

        try
        {
            var (kind, _) = PreviewTypeClassifier.Classify(path);

            Assert.Equal(PreviewKind.ShellHandler, kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Classify_LargePowerPointFilesAsTooLarge()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "preview-large.pptx");
        using (var stream = File.Create(path))
        {
            stream.SetLength(51L * 1024 * 1024);
        }

        try
        {
            var (kind, _) = PreviewTypeClassifier.Classify(path);

            Assert.Equal(PreviewKind.TooLarge, kind);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
