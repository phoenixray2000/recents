using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Recents.App.Services;

// PRD §6.6 图标提取工具
public static class FileIconService
{
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
        var shinfo = new SHFILEINFO();
        uint flags = SHGFI_ICON | (isLarge ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        
        // 如果文件不存在，使用属性获取默认图标
        if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
            flags |= SHGFI_USEFILEATTRIBUTES;

        var res = SHGetFileInfo(path, isFolder ? 0x10u : 0x80u, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

        if (shinfo.hIcon != IntPtr.Zero)
        {
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

        return null;
    }
}
