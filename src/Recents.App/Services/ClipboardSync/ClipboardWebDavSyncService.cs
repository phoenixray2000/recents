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
    private readonly string _outgoingDirectory;
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
        _outgoingDirectory = Path.Combine(syncRoot, "outgoing");
        _downloadDirectory = Path.Combine(syncRoot, "downloads");
        _payloads = new ClipboardSyncPayloadService(_outgoingDirectory, _store);

        _capture.ItemCaptured += OnItemCapturedAsync;
    }

    public void Start()
    {
        WipeStagingDirectory(_outgoingDirectory);
        WipeStagingDirectory(_downloadDirectory);

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

            // R2-1: RunUploadFlowAsync owns the OUTGOING delete-after-use finally over the WHOLE
            // exported-payload lifetime (duplicate-check + upload), so the staged payload is
            // deleted on every exit path — duplicate short-circuit, upload success, and throw.
            var transport = new WebDavPayloadTransport(this, item.Hash);
            await RunUploadFlowAsync(transport, export, sync).ConfigureAwait(false);
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

            // DownloadImportThenDeleteAsync owns the downloads/ delete-after-use finally; the raw
            // download is removed whether the consume callback (import + apply) succeeds or throws.
            var transport = new WebDavPayloadTransport(this, null);
            await DownloadImportThenDeleteAsync(
                transport, profile!, _downloadDirectory,
                (p, payloadPath) => ImportAndApplyAsync(p, payloadPath, sync)).ConfigureAwait(false);

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

    private async Task<ClipboardItem> ImportAndApplyAsync(
        SyncClipboardProfile profile, string? payloadPath, ClipboardWebDavSyncSettings sync)
    {
        var item = await _payloads.ImportAsync(profile, payloadPath).ConfigureAwait(false);

        _lastSyncedRemoteKey = RemoteContentKey(profile);
        _lastSyncedLocalHash = item.Hash;

        await InvokeOnDispatcherAsync(() =>
            _actions.WriteItemToClipboardWithoutHistoryAsync(item, TimeSpan.FromSeconds(3))).ConfigureAwait(false);

        if (_settings.Current.EnableClipboardHistory && sync.SaveRemoteItemsToHistory)
            await _store.IngestAsync(item).ConfigureAwait(false);

        return item;
    }

    internal interface IWebDavPayloadTransport
    {
        // True => remote already matches this export => skip the upload (the duplicate
        // short-circuit). In production this wraps GetProfileAsync + RemoteContentKey compare.
        Task<bool> IsRemoteDuplicateAsync(ClipboardSyncExport export);
        Task UploadAsync(ClipboardSyncExport export, ClipboardWebDavSyncSettings sync);
        Task<string?> DownloadAsync(SyncClipboardProfile profile, string downloadDirectory);
    }

    // R2-1: owns the OUTGOING delete-after-use finally over the ENTIRE exported-payload
    // lifetime — the duplicate-check AND the upload. The duplicate short-circuit returns
    // BEFORE UploadAsync, but the finally still deletes export.PayloadPath. This is the single
    // source of truth for deleting the staged outgoing payload; the upload itself is pure.
    internal static async Task RunUploadFlowAsync(
        IWebDavPayloadTransport transport,
        ClipboardSyncExport export,
        ClipboardWebDavSyncSettings sync)
    {
        try
        {
            if (await transport.IsRemoteDuplicateAsync(export).ConfigureAwait(false))
                return; // remote already matches — short-circuit BEFORE upload (R2-1)

            await transport.UploadAsync(export, sync).ConfigureAwait(false);
        }
        finally
        {
            if (export.PayloadPath is not null)
                TryDeleteFile(export.PayloadPath);
        }
    }

    // Owns the downloads/ delete-after-use finally. Deletes the raw download whether
    // ImportAsync (the consume callback) succeeds OR throws.
    internal static async Task<ClipboardItem> DownloadImportThenDeleteAsync(
        IWebDavPayloadTransport transport,
        SyncClipboardProfile profile,
        string downloadDirectory,
        Func<SyncClipboardProfile, string?, Task<ClipboardItem>> consume)
    {
        string? payloadPath = null;
        try
        {
            if (profile.HasData && !string.IsNullOrWhiteSpace(profile.DataName))
                payloadPath = await transport.DownloadAsync(profile, downloadDirectory).ConfigureAwait(false);
            return await consume(profile, payloadPath).ConfigureAwait(false);
        }
        finally
        {
            if (payloadPath is not null)
                TryDeleteFile(payloadPath);
        }
    }

    // Production transport over the live WebDavClipboardClient. IsRemoteDuplicateAsync wraps the
    // GetProfile + RemoteContentKey compare and performs the short-circuit bookkeeping; UploadAsync
    // runs the existing retry loop; DownloadAsync wraps DownloadPayloadAsync.
    private sealed class WebDavPayloadTransport : IWebDavPayloadTransport
    {
        private readonly ClipboardWebDavSyncService _owner;
        private readonly string? _localHash;

        public WebDavPayloadTransport(ClipboardWebDavSyncService owner, string? localHash)
        {
            _owner = owner;
            _localHash = localHash;
        }

        public async Task<bool> IsRemoteDuplicateAsync(ClipboardSyncExport export)
        {
            var remote = await _owner._client!.GetProfileAsync().ConfigureAwait(false);
            if (remote is not null &&
                string.Equals(RemoteContentKey(remote), RemoteContentKey(export.Profile), StringComparison.OrdinalIgnoreCase))
            {
                _owner._lastSyncedRemoteKey = RemoteContentKey(remote);
                _owner._lastSyncedLocalHash = _localHash;
                return true;
            }

            return false;
        }

        public Task UploadAsync(ClipboardSyncExport export, ClipboardWebDavSyncSettings sync)
            => _owner.UploadWithRetryAsync(export, sync);

        public Task<string?> DownloadAsync(SyncClipboardProfile profile, string downloadDirectory)
            => _owner._client!.DownloadPayloadAsync(profile.DataName!, downloadDirectory);
    }

    internal static void WipeStagingDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                return;
            }
            foreach (var file in Directory.EnumerateFiles(directory))
                TryDeleteFile(file);
            foreach (var dir in Directory.EnumerateDirectories(directory))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clipboard WebDAV: failed to wipe staging {Directory}", directory);
        }
    }

    internal static void DeleteLegacyIncomingDirectory(string syncRoot)
    {
        var legacy = Path.Combine(syncRoot, "incoming");
        TryDeleteDirectory(legacy);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Debug(ex, "Clipboard WebDAV: staging file delete failed {Path}", path); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception ex) { Log.Debug(ex, "Clipboard WebDAV: staging dir delete failed {Path}", path); }
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
