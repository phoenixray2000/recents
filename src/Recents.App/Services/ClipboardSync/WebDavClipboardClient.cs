using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Recents.App.Models;

namespace Recents.App.Services.ClipboardSync;

internal sealed class WebDavClipboardClient
{
    private static readonly HttpMethod MkCol = new("MKCOL");
    private static readonly HttpMethod PropFind = new("PROPFIND");

    private readonly HttpClient _http;
    private readonly ClipboardWebDavSyncSettings _settings;
    private readonly Uri _baseUri;

    public WebDavClipboardClient(HttpClient http, ClipboardWebDavSyncSettings settings)
    {
        _http = http;
        _settings = settings;
        var url = (settings.RemoteDirectoryUrl ?? string.Empty).Trim();
        if (!url.EndsWith("/", StringComparison.Ordinal))
            url += "/";
        _baseUri = new Uri(url, UriKind.Absolute);
    }

    public async Task<SyncClipboardProfile?> GetProfileAsync(CancellationToken token = default)
    {
        using var response = await SendAsync(HttpMethod.Get, SyncClipboardProfile.RemoteProfileFileName, null, token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SyncClipboardProfile>(json, SyncClipboardProfile.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task PutProfileAsync(SyncClipboardProfile profile, CancellationToken token = default)
    {
        var json = JsonSerializer.Serialize(profile, SyncClipboardProfile.JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Put, SyncClipboardProfile.RemoteProfileFileName, content, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task PutPayloadAsync(string dataName, string localPath, CancellationToken token = default)
    {
        await EnsurePayloadDirectoryAsync(token).ConfigureAwait(false);

        await using var stream = File.OpenRead(localPath);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = stream.Length;
        using var response = await SendAsync(HttpMethod.Put, PayloadRelativePath(dataName), content, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> DownloadPayloadAsync(string dataName, string targetDirectory, CancellationToken token = default)
    {
        Directory.CreateDirectory(targetDirectory);
        using var response = await SendAsync(HttpMethod.Get, PayloadRelativePath(dataName), null, token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var target = Path.Combine(targetDirectory, Path.GetFileName(dataName));
        await using var output = File.Create(target);
        await response.Content.CopyToAsync(output, token).ConfigureAwait(false);
        return target;
    }

    public async Task CleanupPayloadsAsync(CancellationToken token = default)
    {
        try
        {
            using var del = await SendAsync(HttpMethod.Delete, SyncClipboardProfile.RemoteFileDirectoryName + "/", null, token)
                .ConfigureAwait(false);
        }
        catch
        {
        }

        await EnsurePayloadDirectoryAsync(token).ConfigureAwait(false);
    }

    public async Task TestConnectionAsync(CancellationToken token = default)
    {
        var request = new HttpRequestMessage(PropFind, _baseUri);
        request.Headers.Add("Depth", "0");
        ApplyAuthorization(request);
        using var response = await SendRawAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsurePayloadDirectoryAsync(CancellationToken token)
    {
        using var response = await SendAsync(MkCol, SyncClipboardProfile.RemoteFileDirectoryName + "/", null, token)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.OK
            or HttpStatusCode.Created or HttpStatusCode.NoContent)
        {
            return;
        }

        if ((int)response.StatusCode >= 400)
            response.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, HttpContent? content, CancellationToken token)
    {
        var request = new HttpRequestMessage(method, BuildUri(relativePath)) { Content = content };
        ApplyAuthorization(request);
        return SendRawAsync(request, token);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
    }

    private Uri BuildUri(string relativePath) =>
        new(_baseUri, relativePath.Replace("\\", "/", StringComparison.Ordinal));

    private static string PayloadRelativePath(string dataName) =>
        SyncClipboardProfile.RemoteFileDirectoryName + "/" + Uri.EscapeDataString(Path.GetFileName(dataName));

    private void ApplyAuthorization(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_settings.Username))
            return;

        var password = WindowsSecretProtector.UnprotectFromBase64(_settings.ProtectedPassword);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.Username}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }
}
