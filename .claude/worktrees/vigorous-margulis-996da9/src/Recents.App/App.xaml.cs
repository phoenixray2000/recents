using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. 初始化日志
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Recents\logs\recents.log"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("--- Recents Started ---");

        // 2. 单实例检测
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryClaimInstance())
        {
            Log.Information("已有实例运行，发送显示信号后退出");
            Shutdown();
            return;
        }

        // 3. 加载设置
        _settings = new SettingsService();
        _settings.Load();

        // 4. 打开索引数据库 + 预加载内存
        _index = new RecentIndexService(_settings);
        _index.OpenDatabase();
        _index.LoadFromDatabase(_settings.Current.MaxRecentItems);

        // 5. 热键
        _hotkey = new HotkeyService();

        // 6. 构建主窗口 & ViewModel
        _statusHint = new StatusHintService();
        var mainVm = new MainViewModel(_index, _hotkey, _statusHint);
        var mainWindow = new MainWindow(mainVm, _settings, _tray);

        mainWindow.SourceInitialized += (s, ev) => _hotkey.Initialize(mainWindow);
        _hotkey.HotkeyPressed += mainWindow.ShowAndFocus;
        _hotkey.RegistrationFailed += msg => _tray?.ShowBalloon("热键冲突", msg, System.Windows.Forms.ToolTipIcon.Error);

        _singleInstance.ShowWindowRequested += () => Dispatcher.Invoke(mainWindow.ShowAndFocus);

        // 7. 托盘
        _tray = new TrayService(mainWindow, _index);

        // 8. 启动后台增量扫描 L1 数据源
        StartSources();

        // 9. 根据参数决定是否显示主窗口
        bool minimized = e.Args.Contains("--minimized");
        if (!minimized)
        {
            mainWindow.Show();
        }
    }

    private void StartSources()
    {
        // 注册数据源
        foreach (var config in _settings.Current.Sources)
        {
            if (!config.Enabled) continue;

            if (config.Kind == Recents.App.Models.SourceKinds.KnownFolderWatch)
                _sources.Add(new KnownFolderWatchSource(config, _settings.Current));

            // PRD §6.3.3 L1 用户自定义本地目录
            if (config.Kind == Recents.App.Models.SourceKinds.UserFolderWatch)
                _sources.Add(new UserFolderWatchSource(config, _settings.Current));
        }

        // 加上系统 Recent.lnk 源
        _sources.Add(new RecentLnkSource(new Recents.App.Services.Sources.SourceConfig { Enabled = true, RecentLookbackDays = 30 }, _settings.Current));

        // 异步初始扫描并订阅变更
        Task.Run(async () =>
        {
            foreach (var source in _sources)
            {
                source.Watch().Subscribe(new RecentChangeObserver(_index));

                try
                {
                    await source.InitialScanAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Source {Kind} 初始化失败", source.Kind);
                }
            }
        });
    }

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
        foreach (var s in _sources) (s as IDisposable)?.Dispose();
        _index?.Dispose();
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
