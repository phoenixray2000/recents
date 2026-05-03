using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Recents.App.Services;

public static class FolderActivationHelper
{
    public static bool OpenOrActivateFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;

        if (!Directory.Exists(folderPath))
            return false;

        var target = NormalizeFolderPath(folderPath);

        try
        {
            var shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellAppType);
                dynamic windows = shell?.Windows();

                if (windows != null)
                {
                    foreach (var window in windows)
                    {
                        try
                        {
                            string? locationUrl = window.LocationURL;
                            if (string.IsNullOrWhiteSpace(locationUrl))
                                continue;

                            if (!TryConvertLocationUrlToPath(locationUrl, out var currentPath))
                                continue;

                            var current = NormalizeFolderPath(currentPath);

                            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                            {
                                var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                                NativeMethods.SetForegroundWindow(hwnd);
                                return true;
                            }
                        }
                        catch
                        {
                            // 忽略单个 Explorer 窗口读取失败
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "FolderActivationHelper: ShellWindows enumeration failed.");
        }

        return OpenFolderInExplorer(folderPath);
    }

    private static bool OpenFolderInExplorer(string folderPath)
    {
        try
        {
            // Grant any process (including Explorer's single-instance handler) the right to
            // claim foreground BEFORE we start the process.  Without this, once Recents hides
            // and Windows moves focus to another window, the inherited foreground permission
            // that explorer.exe received at launch is revoked, and its window opens in the
            // background despite Process.Start returning successfully.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FolderActivationHelper: OpenFolderInExplorer failed {Path}", folderPath);
            return false;
        }
    }

    private static string NormalizeFolderPath(string path)
    {
        var full = Path.GetFullPath(path);

        return full
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryConvertLocationUrlToPath(string locationUrl, out string path)
    {
        path = string.Empty;

        try
        {
            if (Uri.TryCreate(locationUrl, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeMethods
{
    public const int SW_RESTORE = 9;

    // Pass to AllowSetForegroundWindow to grant any process foreground rights.
    public const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // Lets the target process (or all processes if ASFW_ANY) call SetForegroundWindow
    // even after the granting process has lost foreground status.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
