namespace Recents.App.Services.Clipboard;

public interface IClipboardManagedStorage
{
    string ImageDirectory { get; }
    string ThumbnailDirectory { get; }
    string FilesDirectory { get; }
    void WriteThumbnail(System.Windows.Media.Imaging.BitmapSource source, string path, byte[] fallbackPngBytes);
}
