using Microsoft.Win32;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Recents.App.Services.Sources;

// PRD §6.10 OpenSavePidlMRU (Registry) 数据源。
public class OpenSavePidlMruSource : IRecentSource, IDisposable
{
    private readonly AppSettings _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    public string Name => "Open/Save Dialog MRU";
    public SourceKinds Kind => SourceKinds.OpenSavePidlMru;
    
    public OpenSavePidlMruSource(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task InitialScanAsync(CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            var basePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU";
            var rootKey = Registry.CurrentUser.OpenSubKey(basePath);
            if (rootKey == null) return;

            foreach (var extension in rootKey.GetSubKeyNames())
            {
                if (ct.IsCancellationRequested) break;
                var extKey = rootKey.OpenSubKey(extension);
                if (extKey == null) continue;

                foreach (var valueName in extKey.GetValueNames())
                {
                    if (ct.IsCancellationRequested) break;
                    if (int.TryParse(valueName, out _))
                    {
                        var data = extKey.GetValue(valueName) as byte[];
                        if (data == null || data.Length == 0) continue;

                        var path = GetPathFromPidl(data);
                        if (string.IsNullOrEmpty(path)) continue;

                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        var isDir = Directory.Exists(path);
                        var exists = isDir ? Directory.Exists(path) : File.Exists(path);
                        if (!exists) continue; // A9. 不存在的文件不显示

                        var lwt = File.GetLastWriteTime(path);
                        var item = new RecentItem
                        {
                            NormalizedPath = PathNormalizer.Normalize(path),
                            DisplayName = Path.GetFileName(path),
                            Extension = ext,
                            ClassificationSource = FileTypeClassifier.Classify(ext, isDir, _settings.ClassificationSourceGroups),
                            RecentTime = lwt,
                            Sources = SourceKinds.OpenSavePidlMru,
                            IsFolder = isDir,
                            Exists = ExistsState.Found
                        };
                        _subject.OnNext(new RecentChange(RecentChangeKind.Added, item));
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public IObservable<RecentChange> Watch() => _subject;

    private string? GetPathFromPidl(byte[] pidlData)
    {
        IntPtr pidl = Marshal.AllocCoTaskMem(pidlData.Length);
        try
        {
            Marshal.Copy(pidlData, 0, pidl, pidlData.Length);
            StringBuilder path = new StringBuilder(260);
            if (SHGetPathFromIDList(pidl, path))
            {
                return path.ToString();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OpenSavePidlMruSource: PIDL 解析失败");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
        return null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    public void Dispose()
    {
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
