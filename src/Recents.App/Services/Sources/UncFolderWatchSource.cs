using System.IO;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

public sealed class UncFolderWatchSource : IRecentSource, IDisposable
{
    private readonly SourceConfig _config;
    private readonly AppSettings _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private FileSystemWatcher? _watcher;
    private Debouncer? _debouncer;

    public SourceKinds Kind => SourceKinds.UncFolderWatch;

    public UncFolderWatchSource(SourceConfig config, AppSettings settings)
    {
        _config = config;
        _settings = settings;
    }

    public async Task InitialScanAsync(CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.Path)) return;
        if (!Directory.Exists(_config.Path))
        {
            Log.Warning("UncFolderWatch: path unavailable, skipping {Path}", _config.Path);
            return;
        }

        SetupWatcher(_config.Path);
        await Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
            ScanDirectory(_config.Path, cutoff, ct);
        }, ct).ConfigureAwait(false);
    }

    public IObservable<RecentChange> Watch() => _subject;

    private void SetupWatcher(string path)
    {
        _debouncer = new Debouncer(1500, HandleDebouncedChange);
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
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

                var modified = File.GetLastWriteTime(entry);
                var created = File.GetCreationTime(entry);
                if ((created > modified ? created : modified) >= cutoff)
                    HandleDebouncedChange(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UncFolderWatch: scan failed for {Path}", path);
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
        Log.Error(ex, "UncFolderWatch: watcher error for {Path}", _config.Path);
    }

    private RecentItem? CreateItem(string fullPath)
    {
        try
        {
            var normalized = PathNormalizer.Normalize(fullPath);
            if (string.IsNullOrEmpty(normalized)) return null;

            var isDir = Directory.Exists(fullPath);
            if (!isDir && !File.Exists(fullPath)) return null;

            var modified = File.GetLastWriteTime(fullPath);
            var created = File.GetCreationTime(fullPath);
            var extension = isDir ? string.Empty : Path.GetExtension(fullPath).ToLowerInvariant();

            var item = new RecentItem
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

            if (!isDir)
            {
                try { item.SizeBytes = new FileInfo(fullPath).Length; } catch { }
            }

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
