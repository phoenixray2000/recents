using System.IO;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

// PRD §6.3.3 L1 用户自定义本地目录监听（不含 UNC 断网重连，那属于 UncFolderWatchSource）
public sealed class UserFolderWatchSource : IRecentSource, IDisposable
{
    private readonly SourceConfig _config;
    private readonly AppSettings  _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private FileSystemWatcher? _watcher;
    private Debouncer?         _debouncer;

    public SourceKinds Kind => SourceKinds.UserFolderWatch;

    public UserFolderWatchSource(SourceConfig config, AppSettings settings)
    {
        _config   = config;
        _settings = settings;
    }

    public async Task InitialScanAsync(CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.Path)) return;

        var path = _config.Path;
        if (!Directory.Exists(path))
        {
            Log.Warning("UserFolderWatch: 目录不存在，跳过 {Path}", path);
            return;
        }

        Log.Information("UserFolderWatch: 启动 {Name} -> {Path}", _config.DisplayName, path);

        SetupWatcher(path);

        await Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
            ScanDirectory(path, cutoff, ct);
        }, ct).ConfigureAwait(false);
    }

    public IObservable<RecentChange> Watch() => _subject;

    private void SetupWatcher(string path)
    {
        _debouncer = new Debouncer(1200, HandleDebouncedChange);

        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize    = 64 * 1024,
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };

        _watcher.Created += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Changed += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Renamed += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Deleted += (s, e) => HandleDeleted(e.FullPath);
        _watcher.Error   += (s, e) => HandleError(e.GetException(), path);

        _watcher.EnableRaisingEvents = true;
    }

    private void ScanDirectory(string path, DateTime cutoff, CancellationToken ct)
    {
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible       = true,
                RecurseSubdirectories    = true,
                ReturnSpecialDirectories = false
            };

            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", opts))
            {
                if (ct.IsCancellationRequested) break;

                var cTime = File.GetCreationTime(entry);
                var mTime = File.GetLastWriteTime(entry);
                if ((cTime > mTime ? cTime : mTime) >= cutoff)
                    HandleDebouncedChange(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UserFolderWatch: 扫描异常 {Path}", path);
        }
    }

    private void HandleDebouncedChange(string fullPath)
    {
        var item = CreateItem(fullPath);
        if (item is null) return;
        _subject.OnNext(new RecentChange(RecentChangeKind.Added, item));
    }

    private void HandleDeleted(string fullPath)
    {
        var item = new RecentItem { NormalizedPath = PathNormalizer.Normalize(fullPath) };
        _subject.OnNext(new RecentChange(RecentChangeKind.Removed, item));
    }

    private void HandleError(Exception ex, string path)
    {
        Log.Error(ex, "UserFolderWatch: Watcher Error，触发重扫 {Path}", path);
        Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
            ScanDirectory(path, cutoff, CancellationToken.None);
        });
    }

    private RecentItem? CreateItem(string fullPath)
    {
        try
        {
            var normalized = PathNormalizer.Normalize(fullPath);
            if (string.IsNullOrEmpty(normalized)) return null;

            var isPlaceholder = CloudPlaceholderDetector.IsPlaceholder(fullPath);
            var isDir         = Directory.Exists(fullPath);
            if (!isDir && !File.Exists(fullPath)) return null;

            var cTime = File.GetCreationTime(fullPath);
            var mTime = File.GetLastWriteTime(fullPath);

            var item = new RecentItem
            {
                NormalizedPath       = normalized,
                DisplayName          = Path.GetFileName(fullPath),
                Extension            = isDir ? "" : Path.GetExtension(fullPath).ToLowerInvariant(),
                ClassificationSource = FileTypeClassifier.Classify(
                    isDir ? "" : Path.GetExtension(fullPath), isDir, _settings.ClassificationSourceGroups),
                IsFolder             = isDir,
                Exists               = ExistsState.Found,
                Sources              = Kind,
                TargetModifiedTime   = mTime,
                RecentTime           = cTime > mTime ? cTime : mTime
            };

            if (!isDir && !isPlaceholder)
                item.SizeBytes = new FileInfo(fullPath).Length;

            return item;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debouncer?.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
