using System.Globalization;
using System.IO;
using Microsoft.Win32;
using Recents.App.Models;
using Recents.App.Utils;

namespace Recents.App.Services.Sources;

public class OfficeMruSource : IRecentSource, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly SimpleSubject<RecentChange> _subject = new();
    private readonly CancellationTokenSource _pollCts = new();
    private int _pollingStarted;

    public string Name => "Office MRU";
    public SourceKinds Kind => SourceKinds.OfficeMru;

    public OfficeMruSource(AppSettings settings)
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
        using var officeKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
        if (officeKey == null) return;

        foreach (var version in officeKey.GetSubKeyNames())
        {
            if (ct.IsCancellationRequested) break;
            if (!version.Contains('.')) continue;

            using var versionKey = officeKey.OpenSubKey(version);
            if (versionKey == null) continue;

            foreach (var app in versionKey.GetSubKeyNames())
            {
                if (ct.IsCancellationRequested) break;
                using var mruKey = versionKey.OpenSubKey($@"{app}\User MRU");
                if (mruKey == null) continue;

                foreach (var userKeyName in mruKey.GetSubKeyNames())
                {
                    if (ct.IsCancellationRequested) break;
                    using var userKey = mruKey.OpenSubKey($@"{userKeyName}\File MRU");
                    if (userKey == null) continue;
                    ScanFileMruKey(userKey, ct);
                }
            }
        }
    }

    private void ScanFileMruKey(RegistryKey userKey, CancellationToken ct)
    {
        foreach (var valueName in userKey.GetValueNames())
        {
            if (ct.IsCancellationRequested) break;
            if (!valueName.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)) continue;

            var data = userKey.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(data)) continue;

            var filePath = data.Split('*', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) continue;

            var normalized = PathNormalizer.Normalize(filePath);
            if (string.IsNullOrEmpty(normalized)) continue;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var recentTime = TryParseOfficeMruTime(data) ?? File.GetLastWriteTime(filePath);
            var item = new RecentItem
            {
                NormalizedPath = normalized,
                DisplayName = Path.GetFileName(filePath),
                Extension = extension,
                ClassificationSource = FileTypeClassifier.Classify(extension, false, _settings.ClassificationSourceGroups),
                RecentTime = recentTime,
                Sources = Kind,
                IsFolder = false,
                Exists = ExistsState.Found
            };
            _subject.OnNext(new RecentChange(RecentChangeKind.Added, item));
        }
    }

    private static DateTime? TryParseOfficeMruTime(string data)
    {
        var start = data.IndexOf("[T", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        var end = data.IndexOf(']', start);
        if (end <= start + 2) return null;

        var hex = data[(start + 2)..end];
        if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime))
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _pollCts.Cancel();
        _pollCts.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
