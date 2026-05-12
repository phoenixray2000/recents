using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class HtmlTemplateEngineThemeTests
{
    [Fact]
    public void RenderTextUsesLightThemePaletteWhenRequested()
    {
        var html = HtmlTemplateEngine.RenderText("hello", "note.txt", isCode: false, HtmlPreviewTheme.Light);

        Assert.Contains("--bg:        #FFFFFF;", html);
        Assert.Contains("--text:      #111827;", html);
        Assert.Contains("word-break: break-word; background: var(--bg);", html);
        Assert.DoesNotContain("--bg:        #101216;", html);
    }

    [Fact]
    public void RenderCodeUsesCodeBackgroundInLightTheme()
    {
        var html = HtmlTemplateEngine.RenderText("const x = 1;", "app.js", isCode: true, HtmlPreviewTheme.Light);

        Assert.Contains("background: var(--code-bg);", html);
    }

    [Fact]
    public void RenderMarkdownUsesLightThemePaletteWhenRequested()
    {
        var html = HtmlTemplateEngine.RenderMarkdown("# Title", "note.md", HtmlPreviewTheme.Light);

        Assert.Contains("--bg:        #FFFFFF;", html);
        Assert.Contains("--text:      #111827;", html);
        Assert.Contains("<h1", html);
    }
}
