using Microsoft.Win32;
using System.Diagnostics;

namespace Recents.App.Services;

// PRD §6.21 开机自启。
// 写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Recents = "<exe path> --minimized"
public static class StartupService
{
    private const string AppName = "Recents";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Enable()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.SetValue(AppName, $"\"{exePath}\" --minimized");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(AppName, false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }
}
