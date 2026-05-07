using Recents.App.Models;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardActionServiceTests
{
    [Fact]
    public void NormalizeHtmlForWpf_UnwrapsCfHtmlBeforeSetData()
    {
        var cfHtml = BuildCfHtml("<p>Hello <strong>Recents</strong></p>");

        var html = ClipboardActionService.NormalizeHtmlForWpf(cfHtml);

        Assert.Equal("<p>Hello <strong>Recents</strong></p>", html);
        Assert.DoesNotContain("Version:", html);
        Assert.DoesNotContain("StartHTML:", html);
    }

    [Fact]
    public void NormalizeHtmlForClipboard_PreservesExistingCfHtml()
    {
        var cfHtml = BuildCfHtml("<p>Hello <strong>Recents</strong></p>");

        var html = ClipboardActionService.NormalizeHtmlForClipboard(cfHtml);

        Assert.Equal(cfHtml, html);
    }

    [Fact]
    public void NormalizeHtmlForClipboard_WrapsFragmentAsCfHtml()
    {
        var html = ClipboardActionService.NormalizeHtmlForClipboard("<p>你好 <strong>Recents</strong></p>");

        Assert.StartsWith("Version:0.9", html);
        Assert.Contains("StartHTML:", html);
        Assert.Contains("StartFragment:", html);
        Assert.Contains("<!--StartFragment--><p>你好 <strong>Recents</strong></p><!--EndFragment-->", html);

        var parsed = CfHtmlParser.Parse(html);
        Assert.Equal("<p>你好 <strong>Recents</strong></p>", parsed.FragmentHtml);
    }

    [Fact]
    public void CreateDataObject_TextIncludesUnicodeAndLegacyTextFormats()
    {
        var actions = new ClipboardActionService(null!);
        var data = actions.CreateDataObject(new ClipboardItem
        {
            Type = ClipboardPayloadType.Text,
            PlainText = "hello"
        });

        Assert.True(data.GetDataPresent(System.Windows.DataFormats.UnicodeText));
        Assert.True(data.GetDataPresent(System.Windows.DataFormats.Text));
        Assert.Equal("hello", data.GetData(System.Windows.DataFormats.UnicodeText));
        Assert.Equal("hello", data.GetData(System.Windows.DataFormats.Text));
    }

    [Fact]
    public void CreateDataObject_ImageDoesNotExposeFileDropByDefault()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "one-pixel.png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));

        try
        {
            var actions = new ClipboardActionService(null!);
            var data = actions.CreateDataObject(new ClipboardItem
            {
                Type = ClipboardPayloadType.Image,
                ImagePath = imagePath
            });

            Assert.True(data.GetDataPresent(System.Windows.DataFormats.Bitmap));
            Assert.False(data.GetDataPresent(System.Windows.DataFormats.FileDrop));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void HasPlainText_ExtractsTextFromHtmlBlob()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var htmlPath = Path.Combine(directory, "snippet.html");
        File.WriteAllText(htmlPath, "<p>Hello <strong>Recents</strong></p>");

        try
        {
            var actions = new ClipboardActionService(null!);
            var item = new ClipboardItem
            {
                Type = ClipboardPayloadType.Html,
                HtmlBlobPath = htmlPath
            };

            Assert.True(actions.HasPlainText(item));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void HasPlainText_DoesNotTreatRawRtfBlobAsPlainText()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var rtfPath = Path.Combine(directory, "snippet.rtf");
        File.WriteAllText(rtfPath, @"{\rtf1\b Hello}");

        try
        {
            var actions = new ClipboardActionService(null!);
            var item = new ClipboardItem
            {
                Type = ClipboardPayloadType.RichText,
                RtfBlobPath = rtfPath
            };

            Assert.False(actions.HasPlainText(item));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void HasUsableContent_ReturnsFalseForMissingImageBlob()
    {
        var actions = new ClipboardActionService(null!);
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Image,
            ImagePath = Path.Combine(AppContext.BaseDirectory, "missing", Guid.NewGuid().ToString("N") + ".png")
        };

        Assert.False(actions.HasUsableContent(item));
    }

    [Fact]
    public void HasUsableContent_KeepsHtmlWhenPlainTextFallbackExists()
    {
        var actions = new ClipboardActionService(null!);
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Html,
            HtmlBlobPath = Path.Combine(AppContext.BaseDirectory, "missing", "snippet.html"),
            PlainText = "fallback"
        };

        Assert.True(actions.HasUsableContent(item));
    }

    [Fact]
    public void HasUsableContent_FilesRequireAtLeastOneExistingPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "clipboard-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var existingFile = Path.Combine(directory, "existing.txt");
        File.WriteAllText(existingFile, "ok");

        try
        {
            var actions = new ClipboardActionService(null!);
            var item = new ClipboardItem
            {
                Type = ClipboardPayloadType.Files,
                FilePaths =
                [
                    new ClipboardFilePath { Path = Path.Combine(directory, "missing.txt") },
                    new ClipboardFilePath { Path = existingFile }
                ]
            };

            Assert.True(actions.HasUsableContent(item));
            File.Delete(existingFile);
            Assert.False(actions.HasUsableContent(item));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
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
        var startHtml = System.Text.Encoding.UTF8.GetByteCount(placeholderHeader);
        var endHtml = startHtml + System.Text.Encoding.UTF8.GetByteCount(body);
        var startFragment = startHtml + System.Text.Encoding.UTF8.GetByteCount(body[..body.IndexOf(fragment, StringComparison.Ordinal)]);
        var endFragment = startFragment + System.Text.Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment).Replace("\r\n", "\n");
        return header + body;
    }
}
