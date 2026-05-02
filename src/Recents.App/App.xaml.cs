using System.Windows;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Sources;
using Recents.App.ViewModels;
using Serilog;
using WpfApp = System.Windows.Application;

namespace Recents.App;

public partial class App : WpfApp
{
    private SingleInstanceService _singleInstance = null!;
    private SettingsService _settings = null!;
    private RecentIndexService _index = null!;
    private HotkeyService _hotkey = null!;
    private TrayService _tray = null!;
    private StatusHintService _statusHint = null!;
    private readonly List<IRecentSource> _sources = new();
    private readonly List<IDisposable> _sourceSubscriptions = new();
    private readonly SemaphoreSlim _sourceRestartLock = new(1, 1);

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Recents\logs\recents.log"),
                    rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("--- Recents Started ---");

            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryClaimInstance())
            {
                Log.Information("Another Recents instance is already running.");
                Shutdown();
                return;
            }

            _settings = new SettingsService();
            _settings.Load();

            _index = new RecentIndexService(_settings);
            _index.OpenDatabase();
            _index.LoadFromDatabase(_settings.Current.MaxRecentItems);

            _hotkey = new HotkeyService();
            _statusHint = new StatusHintService();

            var mainVm = new MainViewModel(_index, _hotkey, _statusHint);
            _tray = new TrayService(() => RestartSourcesAsync(rebuildIndex: true));

            var mainWindow = new MainWindow(
                mainVm,
                _settings,
                _index,
                () => RestartSourcesAsync(rebuildIndex: true),
                () => RestartSourcesAsync(rebuildIndex: false));

            mainWindow.SourceInitialized += (s, ev) => _hotkey.Initialize(mainWindow);
            _hotkey.HotkeyPressed += mainWindow.ShowAndFocus;
            _hotkey.RegistrationFailed += msg => _tray?.ShowBalloon("Hotkey conflict", msg, System.Windows.Forms.ToolTipIcon.Error);
            _singleInstance.ShowWindowRequested += () => Dispatcher.Invoke(mainWindow.ShowAndFocus);

            _tray.SetMainWindow(mainWindow);
            mainWindow.SetTrayService(_tray);

            _ = RestartSourcesAsync(rebuildIndex: false);

            if (!e.Args.Contains("--minimized"))
                mainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Startup Error: {ex.Message}\n\nStack Trace: {ex.StackTrace}", "Recents Startup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log.Fatal(ex, "App startup failed");
            Shutdown();
        }
    }

    private async Task RestartSourcesAsync(bool rebuildIndex)
    {
        await _sourceRestartLock.WaitAsync();
        try
        {
            foreach (var subscription in _sourceSubscriptions)
                subscription.Dispose();
            _sourceSubscriptions.Clear();

            foreach (var source in _sources)
                (source as IDisposable)?.Dispose();
            _sources.Clear();

            if (rebuildIndex)
                await _index.RebuildAsync();

            RegisterSources();

            foreach (var source in _sources)
            {
                _sourceSubscriptions.Add(source.Watch().Subscribe(new RecentChangeObserver(_index)));

                try
                {
                    await source.InitialScanAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Source {Kind} initialization failed", source.Kind);
                }
            }
        }
        finally
        {
            _sourceRestartLock.Release();
        }
    }

    private void RegisterSources()
    {
        foreach (var config in _settings.Current.Sources)
        {
            if (!config.Enabled) continue;

            if (config.Kind == SourceKinds.KnownFolderWatch)
                _sources.Add(new KnownFolderWatchSource(config, _settings.Current));
            else if (config.Kind == SourceKinds.UserFolderWatch)
                _sources.Add(new UserFolderWatchSource(config, _settings.Current));
            else if (config.Kind == SourceKinds.UncFolderWatch)
                _sources.Add(new UncFolderWatchSource(config, _settings.Current));
        }

        if (IsSystemSourceEnabled(SourceKinds.RecentLnk))
            _sources.Add(new RecentLnkSource(new SourceConfig { Enabled = true, RecentLookbackDays = 30 }, _settings.Current));
        if (IsSystemSourceEnabled(SourceKinds.OfficeMru))
            _sources.Add(new OfficeMruSource());
        if (IsSystemSourceEnabled(SourceKinds.OpenSavePidlMru))
            _sources.Add(new OpenSavePidlMruSource());
    }

    private bool IsSystemSourceEnabled(SourceKinds kind) =>
        _settings.Current.SystemSources.FirstOrDefault(s => s.Kind == kind)?.Enabled ?? true;

    private class RecentChangeObserver : IObserver<RecentChange>
    {
        private readonly RecentIndexService _index;

        public RecentChangeObserver(RecentIndexService index) => _index = index;

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public async void OnNext(RecentChange change)
        {
            if (change.Kind == RecentChangeKind.Added)
                await _index.MergeAsync(change.Item);
            else if (change.Kind == RecentChangeKind.Removed)
                await _index.RemoveAsync(change.Item.NormalizedPath);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkey?.Dispose();
        foreach (var subscription in _sourceSubscriptions) subscription.Dispose();
        foreach (var source in _sources) (source as IDisposable)?.Dispose();
        _index?.Dispose();
        _singleInstance?.Dispose();
        _sourceRestartLock.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
