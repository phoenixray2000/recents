using System.IO;
using System.Net.Http;
using System.Threading;
using Recents.App.Models;
using Recents.App.Services.Clipboard;
using Serilog;
using WpfApplication = System.Windows.Application;

namespace Recents.App.Services.ClipboardSync;

internal sealed class ClipboardWebDavSyncService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ClipboardCaptureService _capture;
    private readonly ClipboardStoreService _store;
    private readonly ClipboardActionService _actions;
    private readonly ClipboardSyncPayloadService _payloads;
    private readonly string _downloadDirectory;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private System.Threading.Timer? _pollTimer;
    private HttpClient? _httpClient;
    private WebDavClipboardClient? _client;
    private string _clientFingerprint = string.Empty;

    private string? _lastSyncedRemoteKey;
    private string? _lastSyncedLocalHash;
    private int _consecutiveFailures;

    public ClipboardWebDavSyncService(
        SettingsService settings,
        ClipboardCaptureService capture,
        ClipboardStoreService store,
        ClipboardActionService actions)
    {
        _settings = settings;
        _capture = capture;
        _store = store;
        _actions = actions;

        var syncRoot = Path.Combine(_store.DataDirectory, "webdav");
        _payloads = new ClipboardSyncPayloadService(
            Path.Combine(syncRoot, "outgoing"),
            Path.Combine(syncRoot, "incoming"));
        _downloadDirectory = Path.Combine(syncRoot, "downloads");

        _capture.ItemCaptured += OnItemCapturedAsync;
    }

    public void Start()
    {
        var interval = TimeSpan.FromSeconds(_settings.Current.ClipboardWebDavSync.PollIntervalSeconds);
        _pollTimer ??= new System.Threading.Timer(_ => _ = PollOnceAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pollTimer.Change(TimeSpan.Zero, interval);
    }

    public void Stop() => _pollTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private Task OnItemCapturedAsync(ClipboardItem item)
    {
        if (!IsConfigured())
            return Task.CompletedTask;

        _ = Task.Run(() => UploadCapturedAsync(item));
        return Task.CompletedTask;
    }

    private async Task UploadCapturedAsync(ClipboardItem item)
    {
        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureClient();

            if (string.Equals(item.Hash, _lastSyncedLocalHash, StringComparison.OrdinalIgnoreCase))
                return;

            var sync = _settings.Current.ClipboardWebDavSync;
            var export = await _payloads.ExportAsync(item, sync.DeviceId, sync.DeviceName, sync.MaxPayloadBytes)
                .ConfigureAwait(false);

            var remote = await _client!.GetProfileAsync().ConfigureAwait(false);
            if (remote is not null &&
                string.Equals(RemoteContentKey(remote), RemoteContentKey(export.Profile), StringComparison.OrdinalIgnoreCase))
            {
                _lastSyncedRemoteKey = RemoteContentKey(remote);
                _lastSyncedLocalHash = item.Hash;
                return;
            }

            await UploadWithRetryAsync(export, sync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clipboard WebDAV upload failed");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task UploadWithRetryAsync(ClipboardSyncExport export, ClipboardWebDavSyncSettings sync)
    {
        if (export.Profile.HasData && export.PayloadPath is null)
        {
            Log.Information("Clipboard item not synced: payload missing or over size limit ({Type})", export.Profile.Type);
            return;
        }

        for (var attempt = 0; attempt <= sync.RetryTimes; attempt++)
        {
            try
            {
                if (sync.DeletePreviousFilesOnPush)
                    await _client!.CleanupPayloadsAsync().ConfigureAwait(false);

                if (export.PayloadPath is not null && export.Profile.DataName is not null)
                    await _client!.PutPayloadAsync(export.Profile.DataName, export.PayloadPath).ConfigureAwait(false);

                await _client!.PutProfileAsync(export.Profile).ConfigureAwait(false);
                _lastSyncedRemoteKey = RemoteContentKey(export.Profile);
                _lastSyncedLocalHash = export.LocalHash;
                _consecutiveFailures = 0;
                return;
            }
            catch when (attempt < sync.RetryTimes)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
    }

    private async Task PollOnceAsync()
    {
        if (!IsConfigured())
            return;
        if (!await _syncGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            EnsureClient();
            var sync = _settings.Current.ClipboardWebDavSync;

            var profile = await _client!.GetProfileAsync().ConfigureAwait(false);
            if (!ShouldApplyRemote(profile, _lastSyncedRemoteKey))
            {
                _consecutiveFailures = 0;
                return;
            }

            string? payloadPath = null;
            if (profile!.HasData && !string.IsNullOrWhiteSpace(profile.DataName))
                payloadPath = await _client.DownloadPayloadAsync(profile.DataName, _downloadDirectory).ConfigureAwait(false);

            var item = await _payloads.ImportAsync(profile, payloadPath).ConfigureAwait(false);

            _lastSyncedRemoteKey = RemoteContentKey(profile);
            _lastSyncedLocalHash = item.Hash;

            await InvokeOnDispatcherAsync(() =>
                _actions.WriteItemToClipboardWithoutHistoryAsync(item, TimeSpan.FromSeconds(3))).ConfigureAwait(false);

            if (_settings.Current.EnableClipboardHistory && sync.SaveRemoteItemsToHistory)
                await _store.IngestAsync(item).ConfigureAwait(false);

            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Log.Warning(ex, "Clipboard WebDAV poll failed ({Count})", _consecutiveFailures);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public static bool ShouldApplyRemote(
        SyncClipboardProfile? profile,
        string? lastSyncedRemoteKey)
    {
        if (profile is null)
            return false;
        if (profile.Type is SyncClipboardProfileType.Unknown or SyncClipboardProfileType.None)
            return false;
        if (!string.IsNullOrWhiteSpace(lastSyncedRemoteKey) &&
            string.Equals(RemoteContentKey(profile), lastSyncedRemoteKey, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static string RemoteContentKey(SyncClipboardProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Hash)
            ? $"{profile.Type}:{profile.Hash}"
            : $"{profile.Type}:{profile.Text}:{profile.DataName}:{profile.Size}";

    private bool IsConfigured()
    {
        var sync = _settings.Current.ClipboardWebDavSync;
        return sync.Enabled &&
               Uri.TryCreate(sync.RemoteDirectoryUrl, UriKind.Absolute, out _) &&
               !string.IsNullOrWhiteSpace(sync.DeviceId);
    }

    private void EnsureClient()
    {
        var sync = _settings.Current.ClipboardWebDavSync;
        var fingerprint = string.Join('|',
            sync.RemoteDirectoryUrl, sync.Username, sync.ProtectedPassword,
            sync.IgnoreCertificateErrors, sync.TimeoutSeconds);
        if (_client is not null && fingerprint == _clientFingerprint)
            return;

        _httpClient?.Dispose();
        var handler = new HttpClientHandler();
        if (sync.IgnoreCertificateErrors)
            handler.ServerCertificateCustomValidationCallback = delegate { return true; };

        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client = new WebDavClipboardClient(_httpClient, sync);
        _clientFingerprint = fingerprint;
    }

    private static Task InvokeOnDispatcherAsync(Func<Task> action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return action();

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    public void Dispose()
    {
        Stop();
        _capture.ItemCaptured -= OnItemCapturedAsync;
        _pollTimer?.Dispose();
        _httpClient?.Dispose();
        _syncGate.Dispose();
    }
}
