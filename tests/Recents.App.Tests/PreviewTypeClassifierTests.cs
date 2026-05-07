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
}
