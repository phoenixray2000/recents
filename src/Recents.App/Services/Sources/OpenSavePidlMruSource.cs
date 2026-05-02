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
    private readonly SimpleSubject<RecentChange> _subject = new();
    public string Name => "Open/Save Dialog MRU";
    public SourceKinds Kind => SourceKinds.OpenSavePidlMru;

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

                        var item = new RecentItem
                        {
                            NormalizedPath = PathNormalizer.Normalize(path),
                            DisplayName = Path.GetFileName(path),
                            Extension = Path.GetExtension(path),
                            RecentTime = DateTime.UtcNow,
                            Sources = SourceKinds.OpenSavePidlMru,
                            IsFolder = Directory.Exists(path),
                            Exists = ExistsState.Unknown
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
