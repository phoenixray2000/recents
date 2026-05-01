using System.IO;
using System.Runtime.InteropServices;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

// PRD §6.3.2 L1 默认已知文件夹监听
public sealed class KnownFolderWatchSource : IRecentSource, IDisposable
{
    private readonly SourceConfig _config;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Debouncer? _debouncer;
    private string? _resolvedPath;

    public SourceKinds Kind => SourceKinds.KnownFolderWatch;

    public KnownFolderWatchSource(SourceConfig config)
    {
        _config = config;
    }

    public async Task InitialScanAsync(CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrEmpty(_config.KnownFolderGuid)) return;

        _resolvedPath = TryGetKnownFolderPath(_config.KnownFolderGuid);
        if (string.IsNullOrEmpty(_resolvedPath))
        {
            Log.Warning("KnownFolderWatch: 无法解析 {Guid} ({Name})", _config.KnownFolderGuid, _config.DisplayName);
            return;
        }

        Log.Information("KnownFolderWatch: 启动 {Name} -> {Path}", _config.DisplayName, _resolvedPath);

        // 1. 设置 watcher
        SetupWatcher(_resolvedPath);

        // 2. 初始扫描
        await Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
            ScanDirectory(_resolvedPath, cutoff, ct);
        }, ct).ConfigureAwait(false);
    }

    public IObservable<RecentChange> Watch() => _subject;

    private void SetupWatcher(string path)
    {
        _debouncer = new Debouncer(1200, HandleDebouncedChange);

        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize    = 64 * 1024,
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };

        watcher.Created += (s, e) => _debouncer.Trigger(e.FullPath);
        watcher.Changed += (s, e) => _debouncer.Trigger(e.FullPath);
        watcher.Renamed += (s, e) => _debouncer.Trigger(e.FullPath);
        watcher.Deleted += (s, e) => HandleDeleted(e.FullPath);
        watcher.Error   += (s, e) => HandleError(e.GetException());

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
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

            foreach (var file in Directory.EnumerateFileSystemEntries(path, "*", opts))
            {
                if (ct.IsCancellationRequested) break;

                // 判断占位符：如果是占位符，跳过读取以避免下载。这里简化为直接查 LastWriteTime
                // (对于占位符，.NET 8 的 File.GetLastWriteTime 实际上不会触发下载，但为了严格遵循 PRD，
                // 我们在收集属性时调用 CloudPlaceholderDetector)
                var cTime = File.GetCreationTime(file);
                var mTime = File.GetLastWriteTime(file);
                var recentTime = cTime > mTime ? cTime : mTime;
                
                if (recentTime >= cutoff)
                {
                    HandleDebouncedChange(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "KnownFolderWatch: 扫描目录异常 {Path}", path);
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
        Log.Error(ex, "KnownFolderWatch: Watcher Error ({Name})，需要全量重扫", _config.DisplayName);
        // PRD §6.19 规定溢出后重扫。
        if (_resolvedPath != null)
        {
            Task.Run(() =>
            {
                var cutoff = DateTime.Now.AddDays(-_config.RecentLookbackDays);
                ScanDirectory(_resolvedPath, cutoff, CancellationToken.None);
            });
        }
    }

    private RecentItem? CreateItem(string fullPath)
    {
        try
        {
            var normalized = PathNormalizer.Normalize(fullPath);
            if (string.IsNullOrEmpty(normalized)) return null;

            var isPlaceholder = CloudPlaceholderDetector.IsPlaceholder(fullPath);
            var isDir = Directory.Exists(fullPath); // 这里仅探测存在性和类型
            
            // 如果不存在，可能是防抖期间被删除了
            if (!isDir && !File.Exists(fullPath)) return null;

            var cTime = File.GetCreationTime(fullPath);
            var mTime = File.GetLastWriteTime(fullPath);
            var recentTime = cTime > mTime ? cTime : mTime;

            var item = new RecentItem
            {
                NormalizedPath     = normalized,
                DisplayName        = Path.GetFileName(fullPath),
                Extension          = isDir ? "" : Path.GetExtension(fullPath).ToLowerInvariant(),
                FileType           = "Other", // 分类逻辑在 ViewModel 或聚合时做，目前填默认
                IsFolder           = isDir,
                Exists             = ExistsState.Exists,
                Sources            = Kind,
                TargetModifiedTime = mTime,
                RecentTime         = recentTime
            };

            if (!isDir && !isPlaceholder)
            {
                item.SizeBytes = new FileInfo(fullPath).Length;
            }

            return item;
        }
        catch
        {
            return null;
        }
    }

    #region P/Invoke SHGetKnownFolderPath

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern string SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken);

    private static string? TryGetKnownFolderPath(string guidString)
    {
        try
        {
            if (Guid.TryParse(guidString, out var guid))
                return SHGetKnownFolderPath(guid, 0, IntPtr.Zero);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debouncer?.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
