using System.Diagnostics;
using System.IO;
using System.Windows;
using Recents.App.Localization;

namespace Recents.App.Services;

// PRD §6.9 / §6.10 文件操作。
public class FileActionService
{
    public static event Action? ActionExecuted;

    public static void OpenFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ActionExecuted?.Invoke();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FileActionService: open file failed {Path}", path);
            System.Windows.MessageBox.Show(Loc.T("Error_OpenFailed_Message", ex.Message), Loc.T("Error_OpenFailed_Title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // 如果是文件夹，直接打开；如果是文件，选中它
            string argument = Directory.Exists(path) ? path : $"/select,\"{path}\"";
            Process.Start("explorer.exe", argument);
            ActionExecuted?.Invoke();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FileActionService: 无法定位文件 {Path}", path);
        }
    }

    public static void CopyPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Windows.Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FileActionService: 复制路径失败");
        }
    }

    public static void CopyPaths(IEnumerable<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FileActionService: copy paths failed");
        }
    }

    public static void CopyFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Windows.Clipboard.SetText(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FileActionService: 复制文件名失败");
        }
    }

    public static void CopyFileNames(IEnumerable<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths.Select(Path.GetFileName).Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FileActionService: copy file names failed");
        }
    }
}
