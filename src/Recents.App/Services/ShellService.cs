using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Diagnostics;
using System.IO;

namespace Recents.App.Services;

public static class ShellService
{
    #region Open With

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcszClass;
        public int oaifInFlags;
    }

    private const int OAIF_ALLOW_REGISTRATION = 0x00000001;
    private const int OAIF_REGISTER_EXT = 0x00000002;
    private const int OAIF_EXEC = 0x00000004;
    private const int OAIF_FORCE_REGISTRATION = 0x00000008;

    public static void ShowOpenWithDialog(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Alternative approach: rundll32.exe shell32.dll,OpenAs_RunDLL path
        // This is more reliable across Windows versions without complex P/Invoke
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "ShellService: Failed to show Open With dialog for {Path}", path);
        }
    }

    #endregion

    #region Thumbnails (IShellItemImageFactory)

    [ComImport]
    [Guid("bcc18b79-ba16-4aec-af64-3c41514a595e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(
            [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
            [In] int flags,
            [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    private const int SIIGBF_RESIZETOFIT = 0x00000000;
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_MEMORYONLY = 0x00000002;
    private const int SIIGBF_ICONONLY = 0x00000004;
    private const int SIIGBF_THUMBNAILONLY = 0x00000008;
    private const int SIIGBF_INISCALEUP = 0x00000010;

    public static BitmapSource? GetThumbnail(string path, int width, int height)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) && !Directory.Exists(path))
            return null;

        try
        {
            Guid guid = new Guid("bcc18b79-ba16-4aec-af64-3c41514a595e"); // IShellItemImageFactory
            SHCreateItemFromParsingName(path, IntPtr.Zero, guid, out var factory);

            var size = new SIZE { cx = width, cy = height };
            factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT, out IntPtr hBitmap);

            if (hBitmap != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }
        catch
        {
            // Fallback to icon if thumbnail fails
        }
        return null;
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    #endregion
}
