using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardCaptureServiceTests
{
    [Fact]
    public void HasMeaningfulHtmlData_ReturnsTrueWhenHtmlHasPlainText()
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, BuildCfHtml("<table><tr><td>Cell</td></tr></table>"));
        data.SetText("Cell", TextDataFormat.UnicodeText);
        data.SetImage(CreateBitmap());

        Assert.True(ClipboardCaptureService.HasMeaningfulHtmlData(data));
    }

    [Fact]
    public void HasMeaningfulHtmlData_ReturnsFalseForImageOnlyHtml()
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, BuildCfHtml("<img src=\"file:///tmp/image.png\">"));
        data.SetImage(CreateBitmap());

        Assert.False(ClipboardCaptureService.HasMeaningfulHtmlData(data));
    }

    [Fact]
    public void ShouldCaptureRtfBeforeImage_RequiresPlainTextWhenImageIsPresent()
    {
        var data = new DataObject();
        data.SetData(DataFormats.Rtf, @"{\rtf1\b Cell}");
        data.SetImage(CreateBitmap());

        Assert.False(ClipboardCaptureService.ShouldCaptureRtfBeforeImage(data));

        data.SetText("Cell", TextDataFormat.UnicodeText);
        Assert.True(ClipboardCaptureService.ShouldCaptureRtfBeforeImage(data));
    }

    [Fact]
    public void TryReadImagePayload_DecodesDeviceIndependentBitmap()
    {
        var data = new DataObject();
        data.SetData("DeviceIndependentBitmap", BuildDibBytes());

        Assert.True(ClipboardCaptureService.HasImageData(data));

        var payload = ClipboardCaptureService.TryReadImagePayload(data);

        Assert.NotNull(payload);
        Assert.Equal(1, payload.Bitmap.PixelWidth);
        Assert.Equal(1, payload.Bitmap.PixelHeight);
        Assert.NotEmpty(payload.PngBytes);
    }

    private static BitmapSource CreateBitmap()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 255, 255, 255, 255 },
            4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] BuildDibBytes()
    {
        var bytes = new byte[44];
        BitConverter.GetBytes(40).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 4);
        BitConverter.GetBytes(1).CopyTo(bytes, 8);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 12);
        BitConverter.GetBytes((short)32).CopyTo(bytes, 14);
        BitConverter.GetBytes(0).CopyTo(bytes, 16);
        BitConverter.GetBytes(4).CopyTo(bytes, 20);
        bytes[40] = 0x11;
        bytes[41] = 0x22;
        bytes[42] = 0x33;
        bytes[43] = 0xFF;
        return bytes;
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
