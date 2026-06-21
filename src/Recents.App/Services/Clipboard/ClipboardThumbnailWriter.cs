using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardThumbnailWriter
{
    public static void WriteJpegThumbnail(BitmapSource source, string path, byte[] fallbackPngBytes)
    {
        var tempPath = path + ".tmp";
        try
        {
            var scale = System.Math.Min(1.0, 160.0 / System.Math.Max(source.PixelWidth, source.PixelHeight));
            var width = System.Math.Max(1, (int)(source.PixelWidth * scale));
            var height = System.Math.Max(1, (int)(source.PixelHeight * scale));
            var resized = new TransformedBitmap(source, new ScaleTransform(
                (double)width / source.PixelWidth,
                (double)height / source.PixelHeight));
            resized.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using var fs = File.Create(tempPath);
            encoder.Save(fs);
            fs.Close();
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            WriteBytesAtomically(path, fallbackPngBytes);
        }
    }

    private static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
