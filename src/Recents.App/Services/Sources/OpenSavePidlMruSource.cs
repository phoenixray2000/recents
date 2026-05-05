using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

public class OpenSavePidlMruSource : IRecentSource, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private readonly CancellationTokenSource _pollCts = new();
    private int _pollingStarted;

    public string Name => "Open/Save Dialog MRU";
    public SourceKinds Kind => SourceKinds.OpenSavePidlMru;

    public OpenSavePidlMruSource(AppSettings settings)
    {
        _settings = settings;
    }

    public Task InitialScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    public IObservable<RecentChange> Watch()
    {
        if (Interlocked.Exchange(ref _pollingStarted, 1) == 0)
            _ = Task.Run(() => PollLoopAsync(_pollCts.Token));

        return _subject;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                Scan(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Scan(CancellationToken ct)
    {
        const string basePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU";
        using var rootKey = Registry.CurrentUser.OpenSubKey(basePath);
        if (rootKey == null) return;

        foreach (var extension in rootKey.GetSubKeyNames())
        {
            if (ct.IsCancellationRequested) break;
            using var extKey = rootKey.OpenSubKey(extension);
            if (extKey == null) continue;

            foreach (var valueName in extKey.GetValueNames())
            {
                if (ct.IsCancellationRequested) break;
                if (!int.TryParse(valueName, out _)) continue;

                var data = extKey.GetValue(valueName) as byte[];
                if (data == null || data.Length == 0) continue;

                var path = GetPathFromPidl(data);
                if (string.IsNullOrEmpty(path)) continue;

                var isDir = Directory.Exists(path);
                var exists = isDir || File.Exists(path);
                if (!exists) continue;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                var lwt = isDir ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                var item = new RecentItem
                {
                    NormalizedPath = PathNormalizer.Normalize(path),
                    DisplayName = Path.GetFileName(path),
                    Extension = ext,
                    ClassificationSource = FileTypeClassifier.Classify(ext, isDir, _settings.ClassificationSourceGroups),
                    RecentTime = lwt,
                    Sources = Kind,
                    IsFolder = isDir,
                    Exists = ExistsState.Found
                };
                _subject.OnNext(new RecentChange(RecentChangeKind.Added, item));
            }
        }
    }

    private string? GetPathFromPidl(byte[] pidlData)
    {
        var pidl = Marshal.AllocCoTaskMem(pidlData.Length);
        try
        {
            Marshal.Copy(pidlData, 0, pidl, pidlData.Length);
            var path = new StringBuilder(32767);
            return SHGetPathFromIDList(pidl, path) ? path.ToString() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OpenSavePidlMruSource: PIDL parse failed");
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    public void Dispose()
    {
        _pollCts.Cancel();
        _pollCts.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
