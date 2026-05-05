using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Localization;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Sources;
using Forms = System.Windows.Forms;
using Interaction = Microsoft.VisualBasic.Interaction;
using ThemeMode = Recents.App.Models.AppSettings.ThemeMode;

namespace Recents.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly RecentIndexService _indexService;
    private readonly Func<Task> _restartSourcesAsync;
    private readonly Func<Task> _rebuildIndexAsync;
    private readonly SemaphoreSlim _sourceSaveLock = new(1, 1);

    public event Action? SettingsChanged;

    public partial class SystemSourceInfo : ObservableObject
    {
        private readonly SettingsViewModel _owner;

        public SystemSourceInfo(SettingsViewModel owner, SourceKinds kind, string name, string location, bool enabled)
        {
            _owner = owner;
            Kind = kind;
            Name = name;
            Location = location;
            _enabled = enabled;
        }

        public SourceKinds Kind { get; }
        public string Name { get; }
        public string KindLabel => Kind.ToString();
        public string Location { get; }

        [ObservableProperty]
        private bool _enabled;

        partial void OnEnabledChanged(bool value) => _owner.SetSystemSourceEnabled(Kind, value);
    }

    public ObservableCollection<SystemSourceInfo> SystemSources { get; }
    public ObservableCollection<SourceConfig> KnownSources { get; }
    public ObservableCollection<SourceConfig> CustomSources { get; }
    public IReadOnlyList<int> MaxRecentItemOptions { get; } = new[] { 100, 200, 500, 1000 };


    public record LanguageOption(string Code, string DisplayName);
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption("",      Loc.T("Lang_Auto")),
        new LanguageOption("en-US", Loc.T("Lang_English")),
        new LanguageOption("zh-CN", Loc.T("Lang_Chinese")),
    };

    public record ThemeOption(ThemeMode Mode, string DisplayName);
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(ThemeMode.FollowSystem, Loc.T("Theme_FollowSystem")),
        new ThemeOption(ThemeMode.Dark,         Loc.T("Theme_Dark")),
        new ThemeOption(ThemeMode.Light,        Loc.T("Theme_Light")),
    };

    public SettingsViewModel(
        SettingsService settings,
        RecentIndexService indexService,
        Func<Task> restartSourcesAsync,
        Func<Task> rebuildIndexAsync)
    {
        _settings = settings;
        _indexService = indexService;
        _restartSourcesAsync = restartSourcesAsync;
        _rebuildIndexAsync = rebuildIndexAsync;
        SystemSources = new ObservableCollection<SystemSourceInfo>
        {
            MakeSystemSource(SourceKinds.RecentLnk, Loc.T("Source_RecentLnk_Name"), @"%APPDATA%\Microsoft\Windows\Recent"),
            MakeSystemSource(SourceKinds.OfficeMru, Loc.T("Source_OfficeMru_Name"), @"HKCU\Software\Microsoft\Office\...\User MRU"),
            MakeSystemSource(SourceKinds.OpenSavePidlMru, Loc.T("Source_OpenSavePidlMru_Name"), @"HKCU\...\Explorer\ComDlg32\OpenSavePidlMRU")
        };
        KnownSources = new ObservableCollection<SourceConfig>(
            settings.Current.Sources.Where(s => s.Kind == SourceKinds.KnownFolderWatch));
        CustomSources = new ObservableCollection<SourceConfig>(
            settings.Current.Sources.Where(s => s.Kind is SourceKinds.UserFolderWatch or SourceKinds.UncFolderWatch));

        _launchAtStartup = settings.Current.LaunchAtStartup;
        _hideOnFocusLost = settings.Current.HideOnFocusLost;
        _alwaysOnTop = settings.Current.AlwaysOnTop;
        _closeToTray = settings.Current.CloseToTray;
        _startMinimized = settings.Current.StartMinimized;
        _hotkey = settings.Current.Hotkey;
        _maxRecentItems = settings.Current.MaxRecentItems;
        _excludedExtensionsText = Join(settings.Current.ExcludedExtensions);
        _excludedPathsText = Join(settings.Current.ExcludedPaths);
        _excludedKeywordsText = Join(settings.Current.ExcludedKeywords);
        _version = typeof(App).Assembly.GetName().Version?.ToString() ?? "Unknown";
        _settingsPath = settings.SettingsPath;
        _dataPath = settings.SettingsDirectory;
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Recents",
            "logs");
        _previewEnabled = settings.Current.PreviewEnabled;
        _showSystemAndHiddenFiles = settings.Current.ShowSystemAndHiddenFiles;
        _selectedLanguage = settings.Current.Language ?? "";
        _selectedTheme = settings.Current.Theme;
    }

    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _hideOnFocusLost;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _hotkey = "Alt+Shift+Z";
    [ObservableProperty] private int _maxRecentItems;
    [ObservableProperty] private string _excludedExtensionsText = string.Empty;
    [ObservableProperty] private string _excludedPathsText = string.Empty;
    [ObservableProperty] private string _excludedKeywordsText = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _settingsPath = string.Empty;
    [ObservableProperty] private string _dataPath = string.Empty;
    [ObservableProperty] private string _logPath = string.Empty;
    [ObservableProperty] private string _statusMessage = Loc.T("Settings_Status_Ready");
    [ObservableProperty] private bool _previewEnabled;
    [ObservableProperty] private bool _showSystemAndHiddenFiles;
    [ObservableProperty] private string _selectedLanguage = "";
    [ObservableProperty] private ThemeMode _selectedTheme = ThemeMode.FollowSystem;

    partial void OnSelectedLanguageChanged(string value)
    {
        _settings.Current.Language = value ?? "";
        LocalizationManager.Instance.SetLanguage(value);
        SaveAndNotify();
    }

    partial void OnSelectedThemeChanged(ThemeMode value)
    {
        _settings.Current.Theme = value;
        ThemeManager.Instance.SetMode(value);
        SaveAndNotify();
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        _settings.Current.LaunchAtStartup = value;
        if (value) StartupService.Enable();
        else StartupService.Disable();
        SaveAndNotify();
    }
    partial void OnHideOnFocusLostChanged(bool value) { _settings.Current.HideOnFocusLost = value; SaveAndNotify(); }
    partial void OnAlwaysOnTopChanged(bool value) { _settings.Current.AlwaysOnTop = value; SaveAndNotify(); }
    partial void OnCloseToTrayChanged(bool value) { _settings.Current.CloseToTray = value; SaveAndNotify(); }
    partial void OnStartMinimizedChanged(bool value) { _settings.Current.StartMinimized = value; SaveAndNotify(); }
    partial void OnHotkeyChanged(string value) { _settings.Current.Hotkey = value; SaveAndNotify(); }
    partial void OnMaxRecentItemsChanged(int value) { _settings.Current.MaxRecentItems = value; SaveAndNotify(); }
    partial void OnExcludedExtensionsTextChanged(string value) { _settings.Current.ExcludedExtensions = Split(value); SaveAndNotify(); }
    partial void OnExcludedPathsTextChanged(string value) { _settings.Current.ExcludedPaths = Split(value); SaveAndNotify(); }
    partial void OnExcludedKeywordsTextChanged(string value) { _settings.Current.ExcludedKeywords = Split(value); SaveAndNotify(); }
    partial void OnPreviewEnabledChanged(bool value)
    {
        _settings.Current.PreviewEnabled = value;
        SaveAndNotify();
    }
    partial void OnShowSystemAndHiddenFilesChanged(bool value)
    {
        _settings.Current.ShowSystemAndHiddenFiles = value;
        SaveAndNotify();
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        Hotkey = "Alt+Shift+Z";
    }

    [RelayCommand]
    private void SaveSources()
    {
        _ = SaveSourcesAndRescanAsync();
    }

    [RelayCommand]
    private void AddFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = Loc.T("Settings_AddFolder_Description") };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;

        var source = new SourceConfig
        {
            Kind = SourceKinds.UserFolderWatch,
            Path = dialog.SelectedPath,
            DisplayName = Path.GetFileName(dialog.SelectedPath.TrimEnd('\\')) ?? dialog.SelectedPath,
            Enabled = true,
            RecentLookbackDays = 30
        };
        _settings.Current.Sources.Add(source);
        CustomSources.Add(source);
        _ = SaveSourcesAndRescanAsync();
    }

    [RelayCommand]
    private void AddNetworkPath()
    {
        var path = Interaction.InputBox("Enter a UNC or mapped network path.", Loc.T("Settings_Btn_AddNetworkPath"), @"\\server\share");
        if (string.IsNullOrWhiteSpace(path)) return;

        path = path.Trim();
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) && !Path.IsPathRooted(path))
        {
            Forms.MessageBox.Show("Enter a UNC path like \\\\server\\share or a mapped drive path.", "Invalid network path",
                Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return;
        }

        var reachable = ProbeDirectory(path, TimeSpan.FromSeconds(3));
        if (reachable != true)
        {
            Forms.MessageBox.Show("This network path did not respond within 3 seconds. It will be added and retried in the background.",
                "Network path unavailable", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }

        var source = new SourceConfig
        {
            Kind = SourceKinds.UncFolderWatch,
            Path = path,
            DisplayName = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path,
            Enabled = true,
            RecentLookbackDays = 7
        };
        _settings.Current.Sources.Add(source);
        CustomSources.Add(source);
        _ = SaveSourcesAndRescanAsync();
    }

    private static bool? ProbeDirectory(string path, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(() => Directory.Exists(path));
            return task.Wait(timeout) ? task.Result : null;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void RemoveSource(SourceConfig? source)
    {
        if (source is null) return;
        _settings.Current.Sources.Remove(source);
        CustomSources.Remove(source);
        _ = SaveSourcesAndRescanAsync();
    }



    [RelayCommand]
    private void ClearIconCache()
    {
        FileIconService.ClearCache();
    }



    [RelayCommand]
    private void OpenDataFolder()
    {
        OpenFolder(DataPath);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LogPath);
        OpenFolder(LogPath);
    }

    private void Save() => _settings.Save();

    private async void SaveAndNotify()
    {
        Save();
        SettingsChanged?.Invoke();
        
        var savedMsg = Loc.T("Settings_Status_Saved");
        StatusMessage = savedMsg;
        try
        {
            await Task.Delay(2000);
            if (StatusMessage == savedMsg)
                StatusMessage = Loc.T("Settings_Status_Ready");
        }
        catch { /* Ignore if task cancelled or other issues */ }
    }

    private SystemSourceInfo MakeSystemSource(SourceKinds kind, string name, string location)
    {
        var enabled = _settings.Current.SystemSources.FirstOrDefault(s => s.Kind == kind)?.Enabled ?? true;
        return new SystemSourceInfo(this, kind, name, location, enabled);
    }

    private void SetSystemSourceEnabled(SourceKinds kind, bool enabled)
    {
        var config = _settings.Current.SystemSources.FirstOrDefault(s => s.Kind == kind);
        if (config is null)
        {
            config = new SystemSourceConfig { Kind = kind };
            _settings.Current.SystemSources.Add(config);
        }

        config.Enabled = enabled;
        _ = SaveSourcesAndRescanAsync();
    }

    [RelayCommand]
    private async Task ClearHiddenItems()
    {
        await _indexService.ClearHiddenItemsAsync();
    }

    [RelayCommand]
    private async Task RebuildIndex()
    {
        await _rebuildIndexAsync();
    }

    private async Task SaveSourcesAndRescanAsync()
    {
        await _sourceSaveLock.WaitAsync();
        try
        {
            SaveAndNotify();
            await _restartSourcesAsync();
        }
        finally
        {
            _sourceSaveLock.Release();
        }
    }

    private static string Join(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    private static List<string> Split(string value) =>
        value.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }
}
