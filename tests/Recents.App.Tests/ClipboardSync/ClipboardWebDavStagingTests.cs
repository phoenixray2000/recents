using System;
using System.IO;
using System.Threading.Tasks;
using Recents.App.Models;
using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class ClipboardWebDavStagingTests
{
    [Fact]
    public void WipeStagingDirectory_RemovesFilesAndSubdirsAndKeepsDir()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "a.bin"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "b.bin"), "y");

        ClipboardWebDavSyncService.WipeStagingDirectory(dir);

        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public void WipeStagingDirectory_CreatesMissingDirectory()
    {
        var dir = Path.Combine(NewTempDir(), "downloads");
        ClipboardWebDavSyncService.WipeStagingDirectory(dir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteLegacyIncomingDirectory_RemovesIncomingTree()
    {
        var syncRoot = NewTempDir();
        var incoming = Path.Combine(syncRoot, "incoming", "deadbeef");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "old.txt"), "z");

        ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory(syncRoot);

        Assert.False(Directory.Exists(Path.Combine(syncRoot, "incoming")));
    }

    // R2-1: the OUTGOING delete-after-use is owned by the finally wrapping the whole exported-
    // payload lifetime inside UploadCapturedAsync. These tests drive the extracted internal
    // RunUploadFlowAsync seam so all three exit paths delete export.PayloadPath:
    //   (a) normal upload success, (b) upload throws (exhausted retry), (c) duplicate
    //   short-circuit (remote already matches -> returns BEFORE any upload).
    [Fact]
    public async Task UploadFlow_DeletesStagedOutgoingPayload_OnUploadSuccess()
    {
        var staged = NewStagedFile("outgoing");
        var export = StubExport(staged);
        var transport = new FakeTransport { RemoteIsDuplicate = false }; // upload runs + succeeds
        await ClipboardWebDavSyncService.RunUploadFlowAsync(transport, export, StubSync());
        Assert.False(File.Exists(staged), "staged outgoing payload must be deleted after a successful upload");
        Assert.True(transport.Uploaded, "upload should have run on the non-duplicate path");
    }

    [Fact]
    public async Task UploadFlow_DeletesStagedOutgoingPayload_OnUploadException()
    {
        var staged = NewStagedFile("outgoing");
        var export = StubExport(staged);
        var transport = new FakeTransport { RemoteIsDuplicate = false, ThrowOnUpload = true }; // exhausted retry surfaces as throw
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardWebDavSyncService.RunUploadFlowAsync(transport, export, StubSync()));
        Assert.False(File.Exists(staged), "staged outgoing payload must be deleted even when upload throws");
    }

    [Fact]
    public async Task UploadFlow_DeletesStagedOutgoingPayload_OnDuplicateShortCircuit()
    {
        // R2-1: ExportAsync already created export.PayloadPath; the remote-matches short-circuit
        // returns before any upload. The finally must STILL delete the staged outgoing payload.
        var staged = NewStagedFile("outgoing");
        var export = StubExport(staged);
        var transport = new FakeTransport { RemoteIsDuplicate = true }; // GetProfile says "already matches"
        await ClipboardWebDavSyncService.RunUploadFlowAsync(transport, export, StubSync());
        Assert.False(File.Exists(staged), "staged outgoing payload must be deleted on the duplicate short-circuit too");
        Assert.False(transport.Uploaded, "upload must NOT run on the duplicate short-circuit path");
    }

    [Fact]
    public async Task DownloadImportThenDelete_DeletesRawDownload_OnSuccess()
    {
        var downloadDir = NewTempDir();
        var raw = Path.Combine(downloadDir, "raw.bin");
        File.WriteAllText(raw, "payload");
        var transport = new FakeTransport { DownloadResult = raw };
        var profile = new SyncClipboardProfile { HasData = true, DataName = "raw.bin" };

        await ClipboardWebDavSyncService.DownloadImportThenDeleteAsync(
            transport, profile, downloadDir,
            (p, path) => Task.FromResult(new ClipboardItem()));

        Assert.False(File.Exists(raw), "raw download must be deleted after ImportAsync consumes it");
    }

    [Fact]
    public async Task DownloadImportThenDelete_DeletesRawDownload_OnImportException()
    {
        var downloadDir = NewTempDir();
        var raw = Path.Combine(downloadDir, "raw.bin");
        File.WriteAllText(raw, "payload");
        var transport = new FakeTransport { DownloadResult = raw };
        var profile = new SyncClipboardProfile { HasData = true, DataName = "raw.bin" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardWebDavSyncService.DownloadImportThenDeleteAsync(
                transport, profile, downloadDir,
                (p, path) => throw new InvalidOperationException("import boom")));

        Assert.False(File.Exists(raw), "raw download must be deleted even when ImportAsync throws");
    }

    private sealed class FakeTransport : ClipboardWebDavSyncService.IWebDavPayloadTransport
    {
        public bool ThrowOnUpload { get; set; }
        public bool RemoteIsDuplicate { get; set; }   // drives the GetProfile short-circuit (R2-1)
        public bool Uploaded { get; private set; }
        public string? DownloadResult { get; set; }

        // RunUploadFlowAsync calls IsRemoteDuplicateAsync(export) first; when true it returns
        // before UploadAsync (mirrors the production GetProfile remote-matches short-circuit).
        public Task<bool> IsRemoteDuplicateAsync(ClipboardSyncExport export)
            => Task.FromResult(RemoteIsDuplicate);

        public Task UploadAsync(ClipboardSyncExport export, ClipboardWebDavSyncSettings sync)
        {
            Uploaded = true;
            return ThrowOnUpload ? throw new InvalidOperationException("upload boom") : Task.CompletedTask;
        }

        public Task<string?> DownloadAsync(SyncClipboardProfile profile, string downloadDirectory)
            => Task.FromResult(DownloadResult);
    }

    private static string NewStagedFile(string name)
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, name + ".payload");
        File.WriteAllText(path, "staged");
        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "webdav-staging-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // StubExport / StubSync mirror the real ClipboardSyncExport + ClipboardWebDavSyncSettings.
    // ClipboardSyncExport is a positional record: (SyncClipboardProfile Profile, string? PayloadPath, string LocalHash).
    private static ClipboardSyncExport StubExport(string payloadPath) =>
        new(new SyncClipboardProfile { Type = SyncClipboardProfileType.File, HasData = true, DataName = "outgoing.payload" }, payloadPath, "local-hash");

    private static ClipboardWebDavSyncSettings StubSync() => new();
}
