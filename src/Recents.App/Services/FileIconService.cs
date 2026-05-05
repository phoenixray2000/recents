using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Recents.App.Services;

public static class FileIconService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Recents",
        "icons");

    [StructLayout(LayoutKind.Sequential)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    [DllImport("shell32.dll")]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? GetIcon(string path, bool isFolder, bool isLarge = true)
    {
        var key = BuildCacheKey(path, isFolder, isLarge);
        var cached = LoadCachedIcon(key);
        if (cached != null) return cached;

        var icon = LoadShellIcon(path, isFolder, isLarge);
        if (icon is BitmapSource bitmap)
            SaveCachedIcon(key, bitmap);

        return icon;
    }

    public static void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheDir))
                Directory.Delete(CacheDir, recursive: true);
        }
        catch
        {
            // Settings action should never fail because cache cleanup failed.
        }
    }

    private static ImageSource? LoadShellIcon(string path, bool isFolder, bool isLarge)
    {
        var shinfo = new SHFILEINFO();
        var flags = SHGFI_ICON | (isLarge ? SHGFI_LARGEICON : SHGFI_SMALLICON);

        if (!File.Exists(path) && !Directory.Exists(path))
            flags |= SHGFI_USEFILEATTRIBUTES;

        SHGetFileInfo(path, isFolder ? 0x10u : 0x80u, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

        if (shinfo.hIcon == IntPtr.Zero) return null;

        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static string BuildCacheKey(string path, bool isFolder, bool isLarge)
    {
        var ext = isFolder ? "folder" : Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = "file";
        var dpi = (int)Math.Round(GetDpiScale() * 100);
        var size = isLarge ? "large" : "small";
        return $"{Sanitize(ext)}_{isFolder}_{size}_{dpi}.png";
    }

    private static double GetDpiScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(System.Windows.Application.Current?.MainWindow);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private static BitmapSource? LoadCachedIcon(string key)
    {
        try
        {
            var path = Path.Combine(CacheDir, key);
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCachedIcon(string key, BitmapSource source)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = Path.Combine(CacheDir, key);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // Cache failures should not affect icon display.
        }
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.TrimStart('.').Replace('\\', '_').Replace('/', '_');
    }
}
