using System.Text;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class CfHtmlParserTests
{
    [Fact]
    public void Parse_ExtractsFragmentByUtf8ByteOffsets()
    {
        var cfHtml = BuildCfHtml("<p>你好 <strong>Recents</strong></p>");

        var parsed = CfHtmlParser.Parse(cfHtml);

        Assert.Equal("<p>你好 <strong>Recents</strong></p>", parsed.FragmentHtml);
        Assert.Contains("<!--StartFragment-->", parsed.Html);
    }

    [Fact]
    public void Parse_FallsBackToMarkersWhenOffsetsAreMissing()
    {
        const string html = "<html><body><!--StartFragment--><b>Hello</b><!--EndFragment--></body></html>";

        var parsed = CfHtmlParser.Parse(html);

        Assert.Equal("<b>Hello</b>", parsed.FragmentHtml);
    }

    private static string BuildCfHtml(string fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        var body = $"<html><body>{startMarker}{fragment}{endMarker}</body></html>";
        const string headerFormat = """
            Version:0.9
            StartHTML:{0:0000000000}
            EndHTML:{1:0000000000}
            StartFragment:{2:0000000000}
            EndFragment:{3:0000000000}

            """;

        var placeholderHeader = string.Format(headerFormat, 0, 0, 0, 0).Replace("\r\n", "\n");
        var startHtml = Encoding.UTF8.GetByteCount(placeholderHeader);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(body);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(body[..body.IndexOf(fragment, StringComparison.Ordinal)]);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment).Replace("\r\n", "\n");
        return header + body;
    }
}
