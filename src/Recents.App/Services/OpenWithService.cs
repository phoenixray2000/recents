using System.Diagnostics;
using System.IO;
using Recents.App.Localization;
using Recents.App.Models;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Recents.App.Services;

public class OpenWithService
{
    public const string FolderTypeKey = "__folder";
    public const string NoExtensionTypeKey = "__no_extension";

    private readonly SettingsService _settings;

    public OpenWithService(SettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<OpenWithAppConfig> GetAppsFor(RecentItem item)
    {
        var key = GetTypeKey(item);
        if (!_settings.Current.OpenWithHistory.TryGetValue(key, out var apps))
            return Array.Empty<OpenWithAppConfig>();

        return apps
            .Where(app => !string.IsNullOrWhiteSpace(app.ExecutablePath))
            .OrderByDescending(app => app.LastUsedUtc)
            .Take(GetMaxApps())
            .ToList();
    }

    public void OpenWith(RecentItem item, OpenWithAppConfig app)
    {
        if (string.IsNullOrWhiteSpace(item.NormalizedPath) || string.IsNullOrWhiteSpace(app.ExecutablePath))
            return;

        try
        {
            Start(item, app);
            Remember(item, app);
            FileActionService.NotifyActionExecuted();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "OpenWithService: open with failed {App} {Path}", app.DisplayName, LogPrivacy.Format(item.NormalizedPath));
            System.Windows.MessageBox.Show(Loc.T("Error_OpenFailed_Message", ex.Message), Loc.T("Error_OpenFailed_Title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void ChooseAndOpen(RecentItem item)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("OpenWith_SelectApp_Title"),
            CheckFileExists = true,
            Filter = Loc.T("OpenWith_SelectApp_Filter"),
            InitialDirectory = GetDefaultAppPickerDirectory(),
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        var app = CreateDefaultApp(dialog.FileName, item);
        OpenWith(item, app);
    }

    public string GetTypeKey(RecentItem item)
    {
        if (item.IsFolder) return FolderTypeKey;

        var ext = Path.GetExtension(item.NormalizedPath);
        return string.IsNullOrWhiteSpace(ext)
            ? NoExtensionTypeKey
            : ext.ToLowerInvariant();
    }

    private void Remember(RecentItem item, OpenWithAppConfig app)
    {
        var key = GetTypeKey(item);
        if (!_settings.Current.OpenWithHistory.TryGetValue(key, out var apps))
        {
            apps = new List<OpenWithAppConfig>();
            _settings.Current.OpenWithHistory[key] = apps;
        }

        apps.RemoveAll(existing => string.Equals(existing.ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        app.LastUsedUtc = DateTime.UtcNow;
        apps.Insert(0, Clone(app));

        var max = GetMaxApps();
        if (apps.Count > max)
            apps.RemoveRange(max, apps.Count - max);

        _settings.Save();
    }

    private static OpenWithAppConfig Clone(OpenWithAppConfig app) => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(app.DisplayName) ? GetDisplayName(app.ExecutablePath) : app.DisplayName,
        ExecutablePath = app.ExecutablePath,
        ArgumentsTemplate = app.ArgumentsTemplate,
        WorkingDirectoryTemplate = app.WorkingDirectoryTemplate,
        LastUsedUtc = app.LastUsedUtc,
    };

    private OpenWithAppConfig CreateDefaultApp(string executablePath, RecentItem item)
    {
        var shortcutName = Path.GetFileNameWithoutExtension(executablePath);
        var shortcut = ResolveShortcut(executablePath);
        if (shortcut != null)
        {
            executablePath = shortcut.TargetPath;
        }

        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        var isFolder = item.IsFolder;
        var isTerminal = IsTerminalLike(fileName);
        var shortcutArguments = shortcut?.Arguments?.Trim() ?? string.Empty;
        var defaultArguments = isFolder && isTerminal ? string.Empty : "\"{path}\"";
        var argumentsTemplate = string.IsNullOrWhiteSpace(shortcutArguments)
            ? defaultArguments
            : string.IsNullOrWhiteSpace(defaultArguments)
                ? shortcutArguments
                : $"{shortcutArguments} {defaultArguments}";

        return new OpenWithAppConfig
        {
            DisplayName = !string.IsNullOrWhiteSpace(shortcutName) && shortcut != null
                ? shortcutName
                : GetDisplayName(executablePath),
            ExecutablePath = executablePath,
            ArgumentsTemplate = argumentsTemplate,
            WorkingDirectoryTemplate = !string.IsNullOrWhiteSpace(shortcut?.WorkingDir) ? shortcut.WorkingDir : "{folder}",
            LastUsedUtc = DateTime.UtcNow,
        };
    }

    private static ShellLinkResolver.ResolveResult? ResolveShortcut(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;

        var result = ShellLinkResolver.Resolve(path);
        if (result == null || string.IsNullOrWhiteSpace(result.TargetPath))
            return null;

        return result;
    }

    private static void Start(RecentItem item, OpenWithAppConfig app)
    {
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

        var psi = new ProcessStartInfo
        {
            FileName = app.ExecutablePath,
            Arguments = Expand(app.ArgumentsTemplate, item),
            WorkingDirectory = ResolveWorkingDirectory(app.WorkingDirectoryTemplate, item),
            UseShellExecute = true
        };

        Process.Start(psi);
    }

    private static string ResolveWorkingDirectory(string template, RecentItem item)
    {
        var expanded = Expand(template, item);
        if (Directory.Exists(expanded)) return expanded;

        var folder = GetFolder(item);
        return Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string Expand(string template, RecentItem item)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var path = item.NormalizedPath;
        var folder = GetFolder(item);
        var fileName = Path.GetFileName(path);

        return template
            .Replace("{path}", path, StringComparison.OrdinalIgnoreCase)
            .Replace("{folder}", folder, StringComparison.OrdinalIgnoreCase)
            .Replace("{fileName}", fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFolder(RecentItem item)
    {
        if (item.IsFolder) return item.NormalizedPath;
        return Path.GetDirectoryName(item.NormalizedPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool IsTerminalLike(string fileName)
    {
        return fileName.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("wt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("claude", StringComparison.OrdinalIgnoreCase);
    }

    private int GetMaxApps() => Math.Clamp(_settings.Current.OpenWithMaxAppsPerType, 1, 10);

    private static string GetDefaultAppPickerDirectory()
    {
        const string commonPrograms = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
        if (Directory.Exists(commonPrograms)) return commonPrograms;

        var userPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");
        return Directory.Exists(userPrograms)
            ? userPrograms
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    private static string GetDisplayName(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription;
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName;
        }
        catch
        {
            // Fall back to the executable name.
        }

        return Path.GetFileNameWithoutExtension(executablePath);
    }
}
