using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Sources;
using Forms = System.Windows.Forms;

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
    public IReadOnlyList<string> SortOptions { get; } = new[] { "RecentTime", "DisplayName", "Size", "ClassificationSource" };

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
            MakeSystemSource(SourceKinds.RecentLnk, "Recent shortcuts", @"%APPDATA%\Microsoft\Windows\Recent"),
            MakeSystemSource(SourceKinds.OfficeMru, "Office MRU", @"HKCU\Software\Microsoft\Office\...\User MRU"),
            MakeSystemSource(SourceKinds.OpenSavePidlMru, "Open / Save dialog MRU", @"HKCU\...\Explorer\ComDlg32\OpenSavePidlMRU")
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
        _showFolders = settings.Current.ShowFolders;
        _defaultSort = settings.Current.DefaultSort;
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
    }

    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _hideOnFocusLost;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _hotkey = "Alt+Shift+Z";
    [ObservableProperty] private int _maxRecentItems;
    [ObservableProperty] private bool _showFolders;
    [ObservableProperty] private string _defaultSort = "RecentTime";
    [ObservableProperty] private string _excludedExtensionsText = string.Empty;
    [ObservableProperty] private string _excludedPathsText = string.Empty;
    [ObservableProperty] private string _excludedKeywordsText = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _settingsPath = string.Empty;
    [ObservableProperty] private string _dataPath = string.Empty;
    [ObservableProperty] private string _logPath = string.Empty;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _previewEnabled;

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
    partial void OnShowFoldersChanged(bool value) { _settings.Current.ShowFolders = value; SaveAndNotify(); }
    partial void OnDefaultSortChanged(string value) { _settings.Current.DefaultSort = value; SaveAndNotify(); }
    partial void OnExcludedExtensionsTextChanged(string value) { _settings.Current.ExcludedExtensions = Split(value); SaveAndNotify(); }
    partial void OnExcludedPathsTextChanged(string value) { _settings.Current.ExcludedPaths = Split(value); SaveAndNotify(); }
    partial void OnExcludedKeywordsTextChanged(string value) { _settings.Current.ExcludedKeywords = Split(value); SaveAndNotify(); }
    partial void OnPreviewEnabledChanged(bool value)
    {
        _settings.Current.PreviewEnabled = value;
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
        using var dialog = new Forms.FolderBrowserDialog { Description = "Add a folder source" };
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
        var source = new SourceConfig
        {
            Kind = SourceKinds.UncFolderWatch,
            Path = @"\\server\share",
            DisplayName = "Network path",
            Enabled = false,
            RecentLookbackDays = 30
        };
        _settings.Current.Sources.Add(source);
        CustomSources.Add(source);
        _ = SaveSourcesAndRescanAsync();
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
        
        StatusMessage = "Settings saved";
        try 
        {
            await Task.Delay(2000);
            if (StatusMessage == "Settings saved")
                StatusMessage = "Ready";
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
