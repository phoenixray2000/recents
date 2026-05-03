using System.Windows;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Sources;
using Recents.App.ViewModels;
using Serilog;
using WpfApp = System.Windows.Application;
using ThemeMode = Recents.App.Models.AppSettings.ThemeMode;

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
    
    public static IWindowGroupFocusService WindowGroupFocusService { get; } = new WindowGroupFocusService();

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

            // 应用主题（必须在创建任何窗口前注入，避免闪烁）
            ThemeManager.Instance.Initialize(_settings.Current.Theme);

            // 应用界面语言（空值跟随系统）
            Localization.LocalizationManager.Instance.SetLanguage(_settings.Current.Language);

            _index = new RecentIndexService(_settings);
            _index.OpenDatabase();
            
            _hotkey = new HotkeyService();
            _statusHint = new StatusHintService();
            _statusHint.SetStatus(StatusHintService.AppStatus.Initializing);

            var mainVm = new MainViewModel(_index, _hotkey, _statusHint, _settings);

            _ = _index.LoadFromDatabaseAsync(_settings.Current.MaxRecentItems);

            _tray = new TrayService(() => RestartSourcesAsync(rebuildIndex: true));

            var mainWindow = new MainWindow(
                mainVm,
                _settings,
                _index,
                () => RestartSourcesAsync(rebuildIndex: true),
                () => RestartSourcesAsync(rebuildIndex: false));

            // 强制创建窗口句柄（即使窗口不显示），以确保全局热键能成功注册
            new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();
            _hotkey.Initialize(mainWindow);
            
            _hotkey.HotkeyPressed += mainWindow.ToggleVisibility;
            _hotkey.RegistrationFailed += msg => _tray?.ShowBalloon(Localization.Loc.T("Error_HotkeyConflict"), msg, System.Windows.Forms.ToolTipIcon.Error);
            _singleInstance.ShowWindowRequested += () => Dispatcher.Invoke(mainWindow.ShowAndFocus);

            _tray.SetMainWindow(mainWindow);
            mainWindow.SetTrayService(_tray);

            if (!e.Args.Contains("--minimized") && !_settings.Current.StartMinimized)
                mainWindow.Show();

            // §6.25 预热 PreviewWindow（后台异步，不阻塞启动）
            if (_settings.Current.PreviewEnabled)
            {
                _ = Task.Run(() =>
                {
                    Dispatcher.BeginInvoke(() => mainWindow.PrewarmPreview());
                });
            }

            // 启动后延迟 1 秒再开始扫描，并切换状态
            _ = Task.Delay(1000).ContinueWith(_ => {
                _statusHint.SetStatus(StatusHintService.AppStatus.Indexing);
                RestartSourcesAsync(rebuildIndex: false).ContinueWith(__ => {
                    _statusHint.SetStatus(StatusHintService.AppStatus.Watching);
                    // 扫描结束后，强制关闭初始化状态（即使依然没有文件，也要展示空状态了）
                    Dispatcher.Invoke(() => mainVm.IsInitializing = false);
                });
            });
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

            var tasks = _sources.Select(async source =>
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
            });

            await Task.WhenAll(tasks);
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
            _sources.Add(new OfficeMruSource(_settings.Current));
        if (IsSystemSourceEnabled(SourceKinds.OpenSavePidlMru))
            _sources.Add(new OpenSavePidlMruSource(_settings.Current));
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
