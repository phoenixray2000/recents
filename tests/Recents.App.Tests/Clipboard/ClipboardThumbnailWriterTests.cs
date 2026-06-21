using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardThumbnailWriterTests
{
    [Fact]
    public void WriteJpegThumbnail_WritesDownscaledJpeg()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "thumb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "thumb.jpg");
        try
        {
            var source = BuildBitmap(400, 300);
            ClipboardThumbnailWriter.WriteJpegThumbnail(source, path, fallbackPngBytes: System.Array.Empty<byte>());

            Assert.True(File.Exists(path));
            using var fs = File.OpenRead(path);
            var decoded = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.True(decoded.PixelWidth <= 160 && decoded.PixelHeight <= 160);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteJpegThumbnail_FallsBackToPngBytesOnEncodeFailure()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "thumb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "thumb.jpg");
        try
        {
            // null source cannot be scaled/encoded; helper must fall back to raw bytes.
            var fallback = new byte[] { 1, 2, 3, 4 };
            ClipboardThumbnailWriter.WriteJpegThumbnail(null!, path, fallback);

            Assert.True(File.Exists(path));
            Assert.Equal(fallback, File.ReadAllBytes(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static BitmapSource BuildBitmap(int w, int h)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
