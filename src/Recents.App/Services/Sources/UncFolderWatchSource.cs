using System.IO;
using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

public sealed class UncFolderWatchSource : IRecentSource, IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly SourceConfig _config;
    private readonly AppSettings _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private FileSystemWatcher? _watcher;
    private Debouncer? _debouncer;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
    private bool _retryScheduled;

    public SourceKinds Kind => SourceKinds.UncFolderWatch;

    public UncFolderWatchSource(SourceConfig config, AppSettings settings)
    {
        _config = config;
        _settings = settings;
    }

    public async Task InitialScanAsync(CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.Path)) return;

        if (!IsDirectoryAvailable(_config.Path))
        {
            MarkUnavailable();
            ScheduleReconnect();
            return;
        }

        StartWatchingAndScan(_config.Path, ct);
        await Task.CompletedTask;
    }

    public IObservable<RecentChange> Watch() => _subject;

    private void StartWatchingAndScan(string path, CancellationToken ct)
    {
        lock (_stateLock)
        {
            DisposeWatcher();
            SetupWatcher(path);
            _retryDelay = TimeSpan.FromSeconds(1);
        }

        var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var scanToken = scanCts.Token;
        _ = Task.Run(() =>
        {
            try
            {
                if (scanToken.IsCancellationRequested)
                    return;

                var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
                ScanDirectory(path, cutoff, scanToken);
            }
            finally
            {
                scanCts.Dispose();
            }
        });
    }

    private void SetupWatcher(string path)
    {
        _debouncer = new Debouncer(1500, HandleDebouncedChange);
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        _watcher.Created += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Changed += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Renamed += (s, e) => _debouncer.Trigger(e.FullPath);
        _watcher.Deleted += (s, e) => HandleDeleted(e.FullPath);
        _watcher.Error += (s, e) => HandleError(e.GetException());
        _watcher.EnableRaisingEvents = true;
    }

    private void ScanDirectory(string path, DateTime cutoff, CancellationToken ct)
    {
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            };

            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", opts))
            {
                if (ct.IsCancellationRequested) break;
                var attrs = TryGetAttributes(entry);
                if (attrs == null) continue;

                var modified = TryGetLastWriteTime(entry) ?? DateTime.Now;
                var created = TryGetCreationTime(entry) ?? modified;
                if ((created > modified ? created : modified) >= cutoff)
                    HandleDebouncedChange(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UncFolderWatch: scan failed for {Path}", LogPrivacy.Format(_config.Path));
            MarkUnavailable();
            ScheduleReconnect();
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

    private void HandleError(Exception ex)
    {
        Log.Error(ex, "UncFolderWatch: watcher error for {Path}", LogPrivacy.Format(_config.Path));
        MarkUnavailable();
        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        lock (_stateLock)
        {
            if (_retryScheduled || _cts.IsCancellationRequested) return;
            _retryScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                var delay = _retryDelay;
                try
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (IsDirectoryAvailable(_config.Path))
                {
                    lock (_stateLock) _retryScheduled = false;
                    StartWatchingAndScan(_config.Path, _cts.Token);
                    return;
                }

                _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }, _cts.Token);
    }

    private void MarkUnavailable()
    {
        DisposeWatcher();
        _subject.OnNext(new RecentChange(
            RecentChangeKind.SourceUnavailable,
            new RecentItem { Sources = Kind, NormalizedPath = PathNormalizer.Normalize(_config.Path) }));
    }

    private RecentItem? CreateItem(string fullPath)
    {
        try
        {
            var normalized = PathNormalizer.Normalize(fullPath);
            if (string.IsNullOrEmpty(normalized)) return null;

            var attrs = TryGetAttributes(fullPath);
            if (attrs == null)
            {
                return new RecentItem
                {
                    NormalizedPath = normalized,
                    DisplayName = Path.GetFileName(fullPath),
                    Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                    ClassificationSource = "Other",
                    IsFolder = false,
                    Exists = ExistsState.Unknown,
                    Sources = Kind,
                    RecentTime = DateTime.Now
                };
            }

            var isDir = attrs.Value.HasFlag(FileAttributes.Directory);
            var modified = TryGetLastWriteTime(fullPath) ?? DateTime.Now;
            var created = TryGetCreationTime(fullPath) ?? modified;
            var extension = isDir ? string.Empty : Path.GetExtension(fullPath).ToLowerInvariant();

            return new RecentItem
            {
                NormalizedPath = normalized,
                DisplayName = Path.GetFileName(fullPath),
                Extension = extension,
                ClassificationSource = FileTypeClassifier.Classify(extension, isDir, _settings.ClassificationSourceGroups),
                IsFolder = isDir,
                Exists = ExistsState.Found,
                Sources = Kind,
                TargetModifiedTime = modified,
                RecentTime = created > modified ? created : modified
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDirectoryAvailable(string path) =>
        RunWithTimeout(() => Directory.Exists(path), ProbeTimeout) == true;

    private static FileAttributes? TryGetAttributes(string path) =>
        RunWithTimeout(() => File.GetAttributes(path), ProbeTimeout);

    private static DateTime? TryGetLastWriteTime(string path) =>
        RunWithTimeout(() => File.GetLastWriteTime(path), ProbeTimeout);

    private static DateTime? TryGetCreationTime(string path) =>
        RunWithTimeout(() => File.GetCreationTime(path), ProbeTimeout);

    private static T? RunWithTimeout<T>(Func<T> action, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(action);
            return task.Wait(timeout) ? task.Result : default;
        }
        catch
        {
            return default;
        }
    }

    private void DisposeWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debouncer?.Dispose();
        _debouncer = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        DisposeWatcher();
        _cts.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
