using System.IO;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

// PRD §6.3.4 L1 Recent 文件夹监听
public sealed class RecentLnkSource : IRecentSource, IDisposable
{
    private readonly SourceConfig _config;
    private readonly AppSettings _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private FileSystemWatcher? _watcher;
    private Debouncer? _debouncer;
    private readonly string _recentDir;

    public SourceKinds Kind => SourceKinds.RecentLnk;

    public RecentLnkSource(SourceConfig config, AppSettings settings)
    {
        _config    = config;
        _settings  = settings;
        _recentDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");
    }

    public async Task InitialScanAsync(CancellationToken ct)
    {
        if (!_config.Enabled || !Directory.Exists(_recentDir)) return;

        Log.Information("RecentLnkSource: 启动 -> {Path}", _recentDir);

        SetupWatcher();

        await Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
            ScanDirectory(cutoff, ct);
        }, ct).ConfigureAwait(false);
    }

    public IObservable<RecentChange> Watch() => _subject;

    private void SetupWatcher()
    {
        _debouncer = new Debouncer(1000, HandleDebouncedChange);

        _watcher = new FileSystemWatcher(_recentDir, "*.lnk")
        {
            IncludeSubdirectories = true,
            InternalBufferSize    = 64 * 1024,
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        _watcher.Created += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Changed += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Renamed += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Deleted += (s, e) => HandleDeleted(e.FullPath);
        _watcher.Error   += (s, e) => HandleError();

        _watcher.EnableRaisingEvents = true;
    }

    private void ScanDirectory(DateTime cutoff, CancellationToken ct)
    {
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
            };

            foreach (var file in Directory.EnumerateFiles(_recentDir, "*.lnk", opts))
            {
                if (ct.IsCancellationRequested) break;

                // PRD §6.3.4 跳过 AutomaticDestinations 和 CustomDestinations 子目录
                if (file.Contains(@"\AutomaticDestinations\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains(@"\CustomDestinations\",    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lwt = File.GetLastWriteTime(file);
                if (lwt >= cutoff)
                {
                    HandleDebouncedChange(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecentLnkSource: 扫描目录异常");
        }
    }

    private void HandleDebouncedChange(string lnkPath)
    {
        if (lnkPath.Contains(@"\AutomaticDestinations\", StringComparison.OrdinalIgnoreCase) ||
            lnkPath.Contains(@"\CustomDestinations\",    StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var res = ShellLinkResolver.Resolve(lnkPath);
        if (res is null || string.IsNullOrWhiteSpace(res.TargetPath)) return;

        var normalized = PathNormalizer.Normalize(res.TargetPath);
        if (string.IsNullOrEmpty(normalized)) return;

        // A6. 不展示 Recent 文件夹中的 .lnk 本体 (PRD §6.14)
        if (normalized.StartsWith(_recentDir, StringComparison.OrdinalIgnoreCase) && 
            normalized.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isDir = Directory.Exists(res.TargetPath);

        var item = new RecentItem
        {
            NormalizedPath     = normalized,
            DisplayName        = Path.GetFileName(res.TargetPath),
            Extension          = isDir ? "" : Path.GetExtension(res.TargetPath).ToLowerInvariant(),
            ClassificationSource = FileTypeClassifier.Classify(isDir ? "" : Path.GetExtension(res.TargetPath), isDir, _settings.ClassificationSourceGroups),
            IsFolder           = isDir,
            Exists             = isDir || File.Exists(res.TargetPath) ? ExistsState.Exists : ExistsState.Missing,
            Sources            = Kind,
            TargetModifiedTime = res.LastWriteTime,
            RecentTime         = res.LastWriteTime // PRD: RecentTime 取 .lnk 的 LastWriteTime
        };

        if (!isDir && item.Exists == ExistsState.Exists)
        {
            try { item.SizeBytes = new FileInfo(res.TargetPath).Length; } catch { }
        }

        _subject.OnNext(new RecentChange(RecentChangeKind.Added, item));
    }

    private void HandleDeleted(string lnkPath)
    {
        // 只有删除 Recent .lnk 时，由于我们无法反向解出原始路径，
        // 在目前的机制下，Lnk 消失不代表原文件被删，
        // 我们不发送 Removed 事件去强删 SQLite，只做增量追加。
        // 如果要支持「清理 Recent 目录后界面同步消失」，需要缓存 lnk -> 原始路径的映射，
        // 首版 P0 简化处理：保留原记录。
    }

    private void HandleError()
    {
        Log.Error("RecentLnkSource: Watcher Error，需要重扫");
        Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
            ScanDirectory(cutoff, CancellationToken.None);
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debouncer?.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
