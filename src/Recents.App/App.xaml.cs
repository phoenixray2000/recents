using System.Windows;
using Microsoft.Web.WebView2.Core;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Clipboard;
using Recents.App.Services.ClipboardSync;
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
    private ClipboardStoreService _clipboardStore = null!;
    private ClipboardActionService _clipboardActions = null!;
    private ClipboardDragDropService _clipboardDragDrop = null!;
    private ClipboardCaptureService _clipboardCapture = null!;
    private ClipboardPasteService _clipboardPaste = null!;
    private ClipboardWebDavSyncService? _clipboardWebDavSync;
    private HotkeyService _hotkey = null!;
    private TrayService _tray = null!;
    private StatusHintService _statusHint = null!;
    private readonly List<IRecentSource> _sources = new();
    private readonly List<IDisposable> _sourceSubscriptions = new();
    private readonly SemaphoreSlim _sourceRestartLock = new(1, 1);
    private readonly object _sourceRestartStateLock = new();
    private CancellationTokenSource? _sourceRestartCts;
    private int _sourceRestartGeneration;
    
    public static IWindowGroupFocusService WindowGroupFocusService { get; } = new WindowGroupFocusService();

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
            var startupCommand = StartupCommand.Parse(e.Args);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Recents\logs\recents.log"),
                    rollingInterval: RollingInterval.Day)
                .CreateLogger();
            InstallUnhandledExceptionLogging();

            Log.Information("--- Recents Started ---");

            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryClaimInstance(startupCommand.ToSingleInstanceCommand()))
            {
                Log.Information("Another Recents instance is already running.");
                Shutdown();
                return;
            }

            _settings = new SettingsService();
            _settings.Load();
            LogPrivacy.VerboseLogging = _settings.Current.VerboseLogging;
            DisablePreviewIfWebView2Missing();

            // 应用主题（必须在创建任何窗口前注入，避免闪烁）
            ThemeManager.Instance.Initialize(_settings.Current.Theme);

            // 应用界面语言（空值跟随系统）
            Localization.LocalizationManager.Instance.SetLanguage(_settings.Current.Language);

            _index = new RecentIndexService(_settings);
            _index.OpenDatabase();
            _clipboardStore = new ClipboardStoreService(_settings);
            _clipboardActions = new ClipboardActionService(_clipboardStore, _settings);
            _clipboardDragDrop = new ClipboardDragDropService(_clipboardActions);
            ClipboardPasteTarget.StartTracking();
            _clipboardStore.AttachActions(_clipboardActions);
            _clipboardStore.OpenDatabase();
            
            _hotkey = new HotkeyService();
            _statusHint = new StatusHintService();
            _statusHint.SetStatus(StatusHintService.AppStatus.Initializing);

            var mainVm = new MainViewModel(_index, _clipboardStore, _hotkey, _statusHint, _settings);
            _ = InitializeClipboardStoreAsync();

            _ = _index.LoadFromDatabaseAsync(_settings.Current.MaxRecentItems);

            _tray = new TrayService(() => RestartSourcesAsync(rebuildIndex: true));

            var mainWindow = new MainWindow(
                mainVm,
                _settings,
                _index,
                _clipboardStore,
                _clipboardActions,
                _clipboardDragDrop,
                () => RestartSourcesAsync(rebuildIndex: true),
                () => RestartSourcesAsync(rebuildIndex: false),
                CancelSourceRestart);

            // 强制创建窗口句柄（即使窗口不显示），以确保全局热键能成功注册
            new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();
            _hotkey.Initialize(mainWindow);
            mainWindow.RefreshExternalSpacePreview();
            _clipboardCapture = new ClipboardCaptureService(_settings, _clipboardStore, _statusHint);
            _clipboardActions.AttachCapture(_clipboardCapture);
            _clipboardCapture.Initialize(mainWindow);
            _clipboardWebDavSync = new ClipboardWebDavSyncService(
                _settings, _clipboardCapture, _clipboardStore, _clipboardActions);
            _clipboardWebDavSync.Start();
            _clipboardPaste = new ClipboardPasteService(_settings, _clipboardStore, _clipboardActions, WindowGroupFocusService, _statusHint);
            _hotkey.UpdatePopPasteHotkey(_settings.Current.PopPasteHotkey);
            
            _hotkey.HotkeyPressed += action =>
            {
                if (action == HotkeyService.HotkeyAction.Toggle)
                    mainWindow.ToggleVisibility();
                else if (action == HotkeyService.HotkeyAction.PopPaste)
                    _clipboardPaste.ShowPopup();
            };
            _hotkey.RegistrationFailed += msg => _tray?.ShowBalloon(Localization.Loc.T("Error_HotkeyConflict"), msg, System.Windows.Forms.ToolTipIcon.Error);
            _singleInstance.ShowWindowRequested += () => Dispatcher.Invoke(mainWindow.ShowAndFocus);
            _singleInstance.CommandRequested += command => Dispatcher.Invoke(() =>
            {
                if (command.Kind == SingleInstanceCommandKind.PreviewPath &&
                    !string.IsNullOrWhiteSpace(command.Path))
                {
                    mainWindow.ShowExternalPreviewPath(command.Path);
                }
            });

            _tray.SetMainWindow(mainWindow);
            mainWindow.SetTrayService(_tray);

            if (startupCommand.Kind == StartupCommandKind.PreviewPath &&
                !string.IsNullOrWhiteSpace(startupCommand.Path))
            {
                Dispatcher.BeginInvoke(() => mainWindow.ShowExternalPreviewPath(startupCommand.Path));
            }
            else if (!e.Args.Contains("--minimized") && !_settings.Current.StartMinimized)
            {
                mainWindow.Show();
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

    private void InstallUnhandledExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled app domain exception");
            else
                Log.Fatal("Unhandled app domain exception: {ExceptionObject}", args.ExceptionObject);
        };
    }

    private async Task InitializeClipboardStoreAsync()
    {
        try
        {
            await _clipboardStore.LoadFromDatabaseAsync();
            await _clipboardStore.CompactOrphanBlobsAsync();
            _clipboardStore.StartMaintenanceTimer();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clipboard store initialization failed");
        }
    }

    private async Task RestartSourcesAsync(bool rebuildIndex)
    {
        var (restartCts, generation) = CreateSourceRestartToken();
        var token = restartCts.Token;

        await _sourceRestartLock.WaitAsync();
        try
        {
            if (token.IsCancellationRequested || !IsCurrentSourceRestart(generation))
                return;

            foreach (var subscription in _sourceSubscriptions)
                subscription.Dispose();
            _sourceSubscriptions.Clear();

            foreach (var source in _sources)
                (source as IDisposable)?.Dispose();
            _sources.Clear();

            if (rebuildIndex)
                await _index.RebuildAsync();

            if (token.IsCancellationRequested || !IsCurrentSourceRestart(generation))
                return;

            RegisterSources();

            var tasks = new List<Task>();
            foreach (var source in _sources)
            {
                _sourceSubscriptions.Add(source.Watch().Subscribe(
                    new RecentChangeObserver(_index, generation, IsCurrentSourceRestart, token)));
                tasks.Add(InitialScanSourceAsync(source, generation, token));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            _sourceRestartLock.Release();
            DisposeStaleSourceRestartToken(restartCts);
        }
    }

    private async Task InitialScanSourceAsync(IRecentSource source, int generation, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested || !IsCurrentSourceRestart(generation))
                return;

            await source.InitialScanAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Debug("Source {Kind} initialization cancelled", source.Kind);
        }
        catch (Exception ex)
        {
            if (IsCurrentSourceRestart(generation) && !token.IsCancellationRequested)
                Log.Error(ex, "Source {Kind} initialization failed", source.Kind);
        }
    }

    private (CancellationTokenSource Cts, int Generation) CreateSourceRestartToken()
    {
        CancellationTokenSource? previous;
        var next = new CancellationTokenSource();
        int generation;

        lock (_sourceRestartStateLock)
        {
            previous = _sourceRestartCts;
            _sourceRestartCts = next;
            generation = ++_sourceRestartGeneration;
        }

        previous?.Cancel();
        return (next, generation);
    }

    private void CancelSourceRestart()
    {
        lock (_sourceRestartStateLock)
        {
            _sourceRestartGeneration++;
            _sourceRestartCts?.Cancel();
        }
    }

    private bool IsCurrentSourceRestart(int generation) =>
        Volatile.Read(ref _sourceRestartGeneration) == generation;

    private void DisposeStaleSourceRestartToken(CancellationTokenSource restartCts)
    {
        lock (_sourceRestartStateLock)
        {
            if (ReferenceEquals(_sourceRestartCts, restartCts))
                return;
        }

        restartCts.Dispose();
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

    private void DisablePreviewIfWebView2Missing()
    {
        if (!_settings.Current.PreviewEnabled) return;

        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            _settings.Current.PreviewEnabled = false;
            _settings.Save();
            Log.Warning(ex, "WebView2 Runtime unavailable; preview disabled");
        }
    }

    private class RecentChangeObserver : IObserver<RecentChange>
    {
        private readonly RecentIndexService _index;
        private readonly int _generation;
        private readonly Func<int, bool> _isCurrentGeneration;
        private readonly CancellationToken _token;

        public RecentChangeObserver(
            RecentIndexService index,
            int generation,
            Func<int, bool> isCurrentGeneration,
            CancellationToken token)
        {
            _index = index;
            _generation = generation;
            _isCurrentGeneration = isCurrentGeneration;
            _token = token;
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public async void OnNext(RecentChange change)
        {
            if (_token.IsCancellationRequested || !_isCurrentGeneration(_generation))
                return;

            if (change.Kind == RecentChangeKind.Added)
                await _index.MergeAsync(change.Item, _token);
            else if (change.Kind == RecentChangeKind.Removed)
                await _index.RemoveAsync(change.Item.NormalizedPath);
            else if (change.Kind == RecentChangeKind.SourceUnavailable)
                await _index.MarkSourceExistsStateAsync(change.Item.Sources, ExistsState.Unknown);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (WpfApp.Current.MainWindow is Recents.App.MainWindow mainWindow)
            mainWindow.DisposePreviewForShutdown();

        _tray?.Dispose();
        _clipboardPaste?.Dispose();
        ClipboardPasteTarget.StopTracking();
        _clipboardWebDavSync?.Dispose();
        _clipboardCapture?.Dispose();
        _hotkey?.Dispose();
        CancelSourceRestart();
        foreach (var subscription in _sourceSubscriptions) subscription.Dispose();
        foreach (var source in _sources) (source as IDisposable)?.Dispose();
        _sourceRestartCts?.Dispose();
        _index?.Dispose();
        _clipboardStore?.Dispose();
        _singleInstance?.Dispose();
        _sourceRestartLock.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
