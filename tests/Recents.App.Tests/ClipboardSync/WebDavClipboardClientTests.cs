using System.Net;
using System.Text;
using Recents.App.Models;
using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class WebDavClipboardClientTests
{
    private static WebDavClipboardClient Make(RecordingHandler handler, string url, string user = "", string protectedPwd = "") =>
        new(new HttpClient(handler), new ClipboardWebDavSyncSettings
        {
            RemoteDirectoryUrl = url,
            Username = user,
            ProtectedPassword = protectedPwd,
            TimeoutSeconds = 30
        });

    [Fact]
    public async Task PutProfileAsync_WritesFixedNameWithAuthAndContentLength()
    {
        var handler = new RecordingHandler();
        var client = Make(handler, "https://example.com/dav/recents/", "ray",
            WindowsSecretProtector.ProtectToBase64("secret"));

        await client.PutProfileAsync(new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Text, Hash = "hash", Text = "hi"
        });

        var put = Assert.Single(handler.Records, r => r.Method == "PUT" &&
            r.Uri == "https://example.com/dav/recents/SyncClipboard.json");
        Assert.Equal("Basic", put.AuthScheme);
        Assert.NotNull(put.ContentLength);
        Assert.True(put.ContentLength > 0);
    }

    [Fact]
    public async Task PutProfileAsync_ResolvesCorrectlyWhenBaseUrlMissingTrailingSlash()
    {
        var handler = new RecordingHandler();
        var client = Make(handler, "https://example.com/dav/recents");

        await client.PutProfileAsync(new SyncClipboardProfile { Type = SyncClipboardProfileType.Text, Hash = "h" });

        Assert.Contains(handler.Records, r =>
            r.Uri == "https://example.com/dav/recents/SyncClipboard.json");
    }

    [Fact]
    public async Task PutPayloadAsync_CreatesFileDirAndUploadsWithContentLength()
    {
        var handler = new RecordingHandler();
        var client = Make(handler, "https://example.com/dav/recents/");
        var payload = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(payload, "payload-bytes");

        try { await client.PutPayloadAsync("abc.txt", payload); }
        finally { File.Delete(payload); }

        Assert.Contains(handler.Records, r => r.Method == "MKCOL" &&
            r.Uri == "https://example.com/dav/recents/file/");
        var put = Assert.Single(handler.Records, r => r.Method == "PUT" &&
            r.Uri == "https://example.com/dav/recents/file/abc.txt");
        Assert.NotNull(put.ContentLength);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsNullOnNotFound()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.NotFound };
        var client = Make(handler, "https://example.com/dav/recents/");

        Assert.Null(await client.GetProfileAsync());
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsNullOnCorruptJson()
    {
        var handler = new RecordingHandler { Body = "{ this is not json" };
        var client = Make(handler, "https://example.com/dav/recents/");

        Assert.Null(await client.GetProfileAsync());
    }

    private sealed record Record(string Method, string Uri, string? AuthScheme, long? ContentLength);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Record> Records { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            long? len = null;
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync();
                len = request.Content.Headers.ContentLength;
            }
            Records.Add(new Record(request.Method.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme, len));

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            };
        }
    }
}
