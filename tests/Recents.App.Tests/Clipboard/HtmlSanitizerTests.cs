using Recents.App.Services.Clipboard;
using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class HtmlSanitizerTests
{
    [Fact]
    public void SanitizeFragment_RemovesActiveContent()
    {
        var html = """
            <div onclick="alert(0)">Hello<script>alert(1)</script><iframe src="x"></iframe><object></object></div>
            <img src=x onerror="alert(2)">
            <a href="javascript:alert(3)">bad</a>
            <svg><animate onbegin="alert(4)" /></svg>
            """;

        var sanitized = HtmlSanitizer.SanitizeFragment(html);

        Assert.Contains("Hello", sanitized);
        Assert.DoesNotContain("script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToPlainText_DecodesEntitiesAndCollapsesWhitespace()
    {
        var text = HtmlSanitizer.ToPlainText("<p>A&nbsp;&amp;&nbsp;B</p>\r\n<span>C</span>");

        Assert.Equal("A & B C", text);
    }

    [Fact]
    public void RenderClipboardHtml_RendersSanitizedFragmentInSandbox()
    {
        var html = HtmlTemplateEngine.RenderClipboardHtml(
            """<img src=x onerror="alert(1)"><a href="javascript:alert(2)">x</a>""",
            "Clipboard HTML");

        Assert.Contains("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sandbox=\"\"", html);
        Assert.Contains("srcdoc=\"", html);
        Assert.Contains("&lt;img", html);
        Assert.Contains("&lt;a href=&quot;#&quot;", html);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe source preview", html, StringComparison.OrdinalIgnoreCase);
    }
}
