using System.IO;
using Microsoft.Win32;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;

namespace Recents.App.Services.Sources;

// PRD §6.9 Office MRU (Registry) 数据源。
public class OfficeMruSource : IRecentSource, IDisposable
{
    private readonly SimpleSubject<RecentChange> _subject = new();
    public string Name => "Office MRU";
    public SourceKinds Kind => SourceKinds.OfficeMru;

    public async Task InitialScanAsync(CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            var officeKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
            if (officeKey == null) return;

            foreach (var version in officeKey.GetSubKeyNames())
            {
                if (ct.IsCancellationRequested) break;
                if (version.Contains('.'))
                {
                    var versionKey = officeKey.OpenSubKey(version);
                    if (versionKey == null) continue;

                    foreach (var app in versionKey.GetSubKeyNames())
                    {
                        if (ct.IsCancellationRequested) break;
                        var mruKey = versionKey.OpenSubKey($@"{app}\User MRU");
                        if (mruKey == null) continue;

                        foreach (var userKeyName in mruKey.GetSubKeyNames())
                        {
                            if (ct.IsCancellationRequested) break;
                            var userKey = mruKey.OpenSubKey($@"{userKeyName}\File MRU");
                            if (userKey == null) continue;

                            foreach (var valueName in userKey.GetValueNames())
                            {
                                if (ct.IsCancellationRequested) break;
                                if (valueName.StartsWith("Item ", StringComparison.OrdinalIgnoreCase))
                                {
                                    var data = userKey.GetValue(valueName)?.ToString();
                                    if (string.IsNullOrEmpty(data)) continue;

                                    var parts = data.Split('*');
                                    var filePath = parts.Last();

                                    if (string.IsNullOrEmpty(filePath)) continue;

                                    var item = new RecentItem
                                    {
                                        NormalizedPath = PathNormalizer.Normalize(filePath),
                                        DisplayName = Path.GetFileName(filePath),
                                        Extension = Path.GetExtension(filePath),
                                        RecentTime = DateTime.UtcNow,
                                        Sources = SourceKinds.OfficeMru,
                                        IsFolder = false,
                                        Exists = ExistsState.Unknown
                                    };
                                    _subject.OnNext(new RecentChange(RecentChangeKind.Added, item));
                                }
                            }
                        }
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public IObservable<RecentChange> Watch() => _subject;

    public void Dispose()
    {
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
