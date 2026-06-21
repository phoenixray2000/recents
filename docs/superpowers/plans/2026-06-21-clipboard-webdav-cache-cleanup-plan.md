# Clipboard WebDAV Cache Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the clipboard WebDAV cache (`webdav/outgoing`, `webdav/downloads`, `webdav/incoming`) a bounded consequence of the single existing history-retention policy by relocating imported content into the store's managed directories and deleting transient staging after use.

**Architecture:** Split `incoming/`'s two responsibilities. Imported remote content (images/files/groups) is written into the store's managed `images/`/`thumbs/`/`files/` directories, which the existing hourly `CompactOrphanBlobsAsync` reconciliation already owns (extended to cover `files/`). Staging dirs `outgoing/`/`downloads/` become delete-after-use plus wipe-on-startup. `incoming/` is eliminated entirely (hard cut, deleted on startup). Favorites gain an independent `favorites/files/` copy so they survive `ClearHistory`.

**Tech Stack:** C# / .NET / WPF, SQLite (`Microsoft.Data.Sqlite`), xUnit (`tests/Recents.App.Tests`), ImageMagick + WPF imaging for image decode/thumbnail.

---

## Build / verify conventions (from `AGENTS.md` — MANDATORY)

Every task's `verify` block and inline `Run:` commands obey these rules:

- **Build:** `dotnet build Recents.sln --no-restore` (NEVER `dotnet build src/Recents.App`).
- **Publish after a green build:** `dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish`. If `Recents.exe` is running, kill it first, then publish to the same `publish` dir.
- **No `dotnet restore`:** none of these tasks change `*.csproj` / `*.sln` / `Directory.Packages.props` / `Directory.Build.props` / `Directory.Build.targets` / `NuGet.config` / `packages.lock.json` or add NuGet packages, so `restore` is never required.
- **Shell:** PowerShell 5.1. NO bash `&&` chaining — put each command on its own line, or chain with `;`. `git grep` zero-reference checks use `git --no-pager grep`.
- **Tests:** keep the `dotnet test tests/Recents.App.Tests --filter "..."` calls verbatim.

---

## Execution model & metadata

Every task below carries a metadata block:

- **complexity:** `low` or `high` (model resolved at execution time — do NOT hardcode model names).
- **critical:** `true` when the task is *irreversible* (cutover / delete / storage-write migration) OR *foundational* (high blast radius: later tasks depend on its interface). Otherwise omitted.
- **verify:** runnable build + publish + specific xUnit filter (see conventions above).
- **spec-check:** which spec acceptance criterion / section the task satisfies.

**Critical task list (after the B1 merge renumbering):** Tasks **2, 3, 4, 5, 6, 7, 8, 9, 10, 13** are `critical`. Non-critical: Task 1 (mechanical thumbnail refactor), Task 11 (read-only zero-reference sweep), Task 12 (read-only full-suite verification walk-through). Task 13 is the terminal cross-vendor review gate.

**Plan base for the closing gate:** record the pre-implementation commit as `<plan-base>` before starting Task 1:

```
git rev-parse HEAD
```

Save the printed SHA; the terminal Task 13 diffs `git diff <plan-base>..HEAD`.

---

## File Structure

Files created or modified, and their single responsibility:

- **Create** `src/Recents.App/Services/Clipboard/ClipboardThumbnailWriter.cs` — single source of truth for JPEG thumbnail generation (extracted from `ClipboardCaptureService`).
- **Modify** `src/Recents.App/Services/Clipboard/ClipboardCaptureService.cs` — delegate thumbnail writing to the shared helper.
- **Modify** `src/Recents.App/Services/Clipboard/ClipboardStoreService.cs` — own `FilesDirectory` + `FavoriteFilesDirectory`; reconcile `files/` (subtree + grace) and `favorites/files/`; parameterize `DeleteUnreferencedFiles` with a grace cutoff (blob/image/thumb); copy file-drop content into favorites; add `FilesDirectory` to `ClearHistory` wipe.
- **Modify** `src/Recents.App/Services/Clipboard/ClipboardRepository.cs` — add `LoadRetainedManagedFilePaths(cutoff)`.
- **Modify** `src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs` — constructor drops `incomingDirectory`, accepts managed dirs + thumbnail writer; `ImportAsync` writes into managed dirs and always generates image thumbnails for decodable images; all `incoming/` usage removed.
- **Modify** `src/Recents.App/Services/ClipboardSync/ClipboardWebDavSyncService.cs` — construct payload service with managed dirs (in the SAME commit as the constructor change); delete-after-use for `outgoing/` (export payload) and `downloads/` (raw download) via testable internal seams; wipe both on `Start()`; expose `DeleteLegacyIncomingDirectory(syncRoot)`.
- **Modify** `src/Recents.App/App.xaml.cs` — call `ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory(syncRoot)` BEFORE `InitializeClipboardStoreAsync()` (before the store loads) so `HasUsableContent` prunes dangling incoming-backed rows on load.
- **Modify** `tests/Recents.App.Tests/ClipboardSync/ClipboardSyncPayloadServiceTests.cs` — update fixture construction (hard cut: no `incoming` arg) and assert imports land in managed dirs with thumbnails (standard + converted).
- **Create** `tests/Recents.App.Tests/Clipboard/ClipboardThumbnailWriterTests.cs` — thumbnail helper unit tests.
- **Modify** `tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs` — `files/` reconciliation, grace-on-image/thumb, `ClearHistory` survival.
- **Modify** `tests/Recents.App.Tests/Clipboard/ClipboardFavoritesTests.cs` — favorite file-drop copy + copy-failure + reconciliation.
- **Create** `tests/Recents.App.Tests/ClipboardSync/ClipboardWebDavStagingTests.cs` — staging delete-after-use (success AND failure) + startup wipe + legacy incoming removal (against a fake WebDAV client seam).

---

## Shared contracts (defined once, referenced by later tasks)

These names are fixed across the plan; later tasks must match them exactly.

- `ClipboardThumbnailWriter.WriteJpegThumbnail(BitmapSource source, string path, byte[] fallbackPngBytes)` — `public static`, in `namespace Recents.App.Services.Clipboard`.
- `ClipboardStoreService.FilesDirectory` — `public string`, `= Path.Combine(DataDirectory, "files")`.
- `ClipboardStoreService.FavoriteFilesDirectory` — `public string`, `= Path.Combine(FavoriteDirectory, "files")`.
- `ClipboardRepository.LoadRetainedManagedFilePaths(DateTime deletedCutoffUtc)` — `public IReadOnlyList<string>`.
- `ClipboardStoreService` constant `FilesGraceWindow = TimeSpan.FromDays(1)` — the SINGLE grace constant, reused by both `files/` subtree reconciliation AND the blob/image/thumb mtime grace cutoff.
- `ClipboardStoreService` private `DeleteUnreferencedFiles(string directory, IEnumerable<string?> referencedPaths, DateTime graceCutoffUtc)` — files newer than `graceCutoffUtc` (by mtime) are kept even when unreferenced.
- `ClipboardSyncPayloadService` constructor: `ClipboardSyncPayloadService(string outgoingDirectory, IClipboardManagedStorage managedStorage)` where `IClipboardManagedStorage` exposes:
  - `string ImageDirectory { get; }`
  - `string ThumbnailDirectory { get; }`
  - `string FilesDirectory { get; }`
  - `void WriteThumbnail(BitmapSource source, string path, byte[] fallbackPngBytes)`
  `ClipboardStoreService` implements `IClipboardManagedStorage` (it already exposes `ImageDirectory`/`ThumbnailDirectory`; `WriteThumbnail` delegates to `ClipboardThumbnailWriter.WriteJpegThumbnail`).
- `ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory(string syncRoot)` — `internal static`; deletes `Path.Combine(syncRoot, "incoming")`. Called from `App.xaml.cs` before store load.
- `ClipboardWebDavSyncService.WipeStagingDirectory(string directory)` — `internal static`; empties a staging dir (keeps the dir).

---

## Task 1: Extract shared thumbnail helper

**complexity:** low

_(Not critical: mechanical refactor, low blast radius — any breakage is caught immediately by the build.)_

**Files:**
- Create: `src/Recents.App/Services/Clipboard/ClipboardThumbnailWriter.cs`
- Create: `tests/Recents.App.Tests/Clipboard/ClipboardThumbnailWriterTests.cs`
- Modify: `src/Recents.App/Services/Clipboard/ClipboardCaptureService.cs:736-760` (private static `WriteJpegThumbnail`) and its call site `:315`

- [ ] **Step 1: Write the failing test**

Create `tests/Recents.App.Tests/Clipboard/ClipboardThumbnailWriterTests.cs`:

```csharp
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardThumbnailWriterTests
{
    [Fact]
    public void WriteJpegThumbnail_WritesDownscaledJpeg()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "thumb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "thumb.jpg");
        try
        {
            var source = BuildBitmap(400, 300);
            ClipboardThumbnailWriter.WriteJpegThumbnail(source, path, fallbackPngBytes: System.Array.Empty<byte>());

            Assert.True(File.Exists(path));
            using var fs = File.OpenRead(path);
            var decoded = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.True(decoded.PixelWidth <= 160 && decoded.PixelHeight <= 160);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteJpegThumbnail_FallsBackToPngBytesOnEncodeFailure()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "thumb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "thumb.jpg");
        try
        {
            // null source cannot be scaled/encoded; helper must fall back to raw bytes.
            var fallback = new byte[] { 1, 2, 3, 4 };
            ClipboardThumbnailWriter.WriteJpegThumbnail(null!, path, fallback);

            Assert.True(File.Exists(path));
            Assert.Equal(fallback, File.ReadAllBytes(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static BitmapSource BuildBitmap(int w, int h)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardThumbnailWriterTests"`
Expected: FAIL with compile error "ClipboardThumbnailWriter does not exist".

- [ ] **Step 3: Create the shared helper**

Create `src/Recents.App/Services/Clipboard/ClipboardThumbnailWriter.cs` by lifting the exact body of `ClipboardCaptureService.WriteJpegThumbnail` (lines 736-760) plus its `WriteBytesAtomically` + `TryDelete` dependencies:

```csharp
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardThumbnailWriter
{
    public static void WriteJpegThumbnail(BitmapSource source, string path, byte[] fallbackPngBytes)
    {
        var tempPath = path + ".tmp";
        try
        {
            var scale = System.Math.Min(1.0, 160.0 / System.Math.Max(source.PixelWidth, source.PixelHeight));
            var width = System.Math.Max(1, (int)(source.PixelWidth * scale));
            var height = System.Math.Max(1, (int)(source.PixelHeight * scale));
            var resized = new TransformedBitmap(source, new ScaleTransform(
                (double)width / source.PixelWidth,
                (double)height / source.PixelHeight));
            resized.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using var fs = File.Create(tempPath);
            encoder.Save(fs);
            fs.Close();
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            WriteBytesAtomically(path, fallbackPngBytes);
        }
    }

    private static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
```

- [ ] **Step 4: Point `ClipboardCaptureService` at the shared helper**

In `ClipboardCaptureService.cs`, replace the call at line 315:

```csharp
        WriteJpegThumbnail(payload.Bitmap, thumbPath, payload.PngBytes);
```

with:

```csharp
        ClipboardThumbnailWriter.WriteJpegThumbnail(payload.Bitmap, thumbPath, payload.PngBytes);
```

Then delete the now-unused private static `WriteJpegThumbnail` method (lines 736-760). Keep `WriteBytesAtomically` and `TryDelete` in `ClipboardCaptureService` if still referenced elsewhere; if `TryDelete` becomes unused after removal, delete it too (verify in Step 6).

- [ ] **Step 5: Run thumbnail tests (now passing)**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardThumbnailWriterTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Build + publish + capture regression**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardCaptureServiceTests"
```
Expected: build SUCCEEDED; publish SUCCEEDED; capture tests PASS.

- [ ] **Step 7: Zero-reference check (no duplicate thumbnail impl)**

Run: `git --no-pager grep -n "WriteJpegThumbnail" -- src/`
Expected: only `ClipboardThumbnailWriter.cs` (definition) and the call site in `ClipboardCaptureService.cs`. No private duplicate remains.

- [ ] **Step 8: Commit**

```
git add src/Recents.App/Services/Clipboard/ClipboardThumbnailWriter.cs src/Recents.App/Services/Clipboard/ClipboardCaptureService.cs tests/Recents.App.Tests/Clipboard/ClipboardThumbnailWriterTests.cs
git commit -m "refactor: extract shared ClipboardThumbnailWriter"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardThumbnailWriterTests|FullyQualifiedName~ClipboardCaptureServiceTests"
```
**spec-check:** Section 6.3 (shared thumbnail helper — single source of truth).

---

## Task 2: Store managed `files/` + `favorites/files/` directories + managed-storage interface

**complexity:** low
**critical:** true  _(foundational — payload service constructor (Task 4) and reconciliation (Task 7) depend on `FilesDirectory`/`FavoriteFilesDirectory` and the `IClipboardManagedStorage` seam)_

**Files:**
- Modify: `src/Recents.App/Services/Clipboard/ClipboardStoreService.cs:27-61` (properties + init), add interface + `WriteThumbnail`.
- Create (inline or separate file) interface `IClipboardManagedStorage`.
- Test: `tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs` (new test).

- [ ] **Step 1: Write the failing test**

Add to `ClipboardStoreServiceTests` (the `ClipboardStoreFixture` already exposes `Store`):

```csharp
    [Fact]
    public void Store_CreatesManagedFilesDirectories()
    {
        using var fixture = ClipboardStoreFixture.Create();
        Assert.True(Directory.Exists(fixture.Store.FilesDirectory));
        Assert.True(Directory.Exists(fixture.Store.FavoriteFilesDirectory));
        Assert.EndsWith(Path.Combine("data", "files"), fixture.Store.FilesDirectory.TrimEnd(Path.DirectorySeparatorChar));
        Assert.EndsWith(Path.Combine("favorites", "files"), fixture.Store.FavoriteFilesDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }
```

(Add `using System.IO;` at top of the test file if not present.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests.Store_CreatesManagedFilesDirectories"`
Expected: FAIL — `FilesDirectory` does not exist.

- [ ] **Step 3: Add directories + interface to the store**

In `ClipboardStoreService.cs`:

1. Declare the interface (top of file, after the using block, before the class) — or in its own file `src/Recents.App/Services/Clipboard/IClipboardManagedStorage.cs`:

```csharp
public interface IClipboardManagedStorage
{
    string ImageDirectory { get; }
    string ThumbnailDirectory { get; }
    string FilesDirectory { get; }
    void WriteThumbnail(System.Windows.Media.Imaging.BitmapSource source, string path, byte[] fallbackPngBytes);
}
```

2. Make the class implement it:

```csharp
public sealed class ClipboardStoreService : IDisposable, IClipboardManagedStorage
```

3. Add the two new public properties next to the existing dir properties (after `ThumbnailDirectory` at line 30 and `FavoriteThumbnailDirectory` at line 34):

```csharp
    public string FilesDirectory { get; }
    public string FavoriteFilesDirectory { get; }
```

4. In the internal constructor, set + create them (after lines 50 and 54 respectively, and add `Directory.CreateDirectory` calls in the block at lines 56-61):

```csharp
        FilesDirectory = Path.Combine(DataDirectory, "files");
        FavoriteFilesDirectory = Path.Combine(FavoriteDirectory, "files");
```
```csharp
        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(FavoriteFilesDirectory);
```

5. Add the `WriteThumbnail` member delegating to the shared helper (anywhere in the class body):

```csharp
    public void WriteThumbnail(System.Windows.Media.Imaging.BitmapSource source, string path, byte[] fallbackPngBytes)
        => ClipboardThumbnailWriter.WriteJpegThumbnail(source, path, fallbackPngBytes);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests.Store_CreatesManagedFilesDirectories"`
Expected: PASS.

- [ ] **Step 5: Build + publish + full store regression**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests"
```
Expected: build SUCCEEDED; publish SUCCEEDED; store tests PASS.

- [ ] **Step 6: Commit**

```
git add src/Recents.App/Services/Clipboard/ tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs
git commit -m "feat: add managed files/ and favorites/files/ directories with managed-storage interface"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests"
```
**spec-check:** Section 5 / 6.4 (managed `files/` directory created on init) + Section 6.5 (`favorites/files/` created) + Section 6.2 (managed-storage seam for import).

---

## Task 3: Repository `LoadRetainedManagedFilePaths`

**complexity:** low
**critical:** true  _(foundational — `files/` reconciliation in Task 7 consumes this reference set; wrong join silently deletes live content)_

**Files:**
- Modify: `src/Recents.App/Services/Clipboard/ClipboardRepository.cs` (add method after `LoadRetainedBlobPaths` at line 463).
- Test: `tests/Recents.App.Tests/Clipboard/ClipboardRepositoryTests.cs`.

- [ ] **Step 1: Write the failing test**

Open `tests/Recents.App.Tests/Clipboard/ClipboardRepositoryTests.cs`, follow its existing fixture/helper pattern (it constructs a `ClipboardRepository` against a temp db; mirror an existing test's setup). Add:

```csharp
    [Fact]
    public void LoadRetainedManagedFilePaths_ReturnsFilePathsForRetainedItemsOnly()
    {
        using var fixture = CreateRepository(); // mirror existing repo-test fixture helper
        var repo = fixture.Repo;

        var live = NewFilesItem("live", "hash-live", new[] { @"C:\data\files\a\x.txt", @"C:\data\files\a\y.txt" });
        var deletedRecent = NewFilesItem("recent", "hash-recent", new[] { @"C:\data\files\b\z.txt" });
        var deletedOld = NewFilesItem("old", "hash-old", new[] { @"C:\data\files\c\old.txt" });

        repo.Upsert(live);
        repo.Upsert(deletedRecent);
        repo.Upsert(deletedOld);
        repo.SoftDelete("recent", DateTime.UtcNow.AddMinutes(-1));
        repo.SoftDelete("old", DateTime.UtcNow.AddDays(-30));

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var retained = repo.LoadRetainedManagedFilePaths(cutoff);

        Assert.Contains(@"C:\data\files\a\x.txt", retained);
        Assert.Contains(@"C:\data\files\a\y.txt", retained);
        Assert.Contains(@"C:\data\files\b\z.txt", retained); // recently deleted, within window
        Assert.DoesNotContain(@"C:\data\files\c\old.txt", retained); // deleted long ago
    }
```

If `ClipboardRepositoryTests` lacks `NewFilesItem`/`CreateRepository` helpers, add small local helpers in the test file that build a `ClipboardItem` of type `Files` with the given `FilePaths` and construct/open a temp-db `ClipboardRepository` (mirror `ClipboardStoreFixture` temp-dir + `OpenDatabase()` pattern). Use real `ClipboardItem`/`ClipboardFilePath` models.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardRepositoryTests.LoadRetainedManagedFilePaths_ReturnsFilePathsForRetainedItemsOnly"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement the repo method**

Add to `ClipboardRepository.cs` after `LoadRetainedBlobPaths` (line 463). Mirror its retention predicate exactly (`is_deleted = 0 OR deleted_utc IS NULL OR deleted_utc >= $cutoff`):

```csharp
    public IReadOnlyList<string> LoadRetainedManagedFilePaths(DateTime deletedCutoffUtc)
    {
        if (_conn is null) return Array.Empty<string>();

        var paths = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.path
            FROM clipboard_files f
            JOIN clipboard_items i ON i.id = f.item_id
            WHERE i.is_deleted = 0
               OR i.deleted_utc IS NULL
               OR i.deleted_utc >= $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", deletedCutoffUtc.ToString("O"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                paths.Add(reader.GetString(0));
        }

        return paths;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardRepositoryTests.LoadRetainedManagedFilePaths_ReturnsFilePathsForRetainedItemsOnly"`
Expected: PASS.

- [ ] **Step 5: Build + publish + repo regression**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardRepositoryTests"
```
Expected: build SUCCEEDED; publish SUCCEEDED; repo tests PASS.

- [ ] **Step 6: Commit**

```
git add src/Recents.App/Services/Clipboard/ClipboardRepository.cs tests/Recents.App.Tests/Clipboard/ClipboardRepositoryTests.cs
git commit -m "feat: add LoadRetainedManagedFilePaths repository query"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardRepositoryTests"
```
**spec-check:** Section 6.4 (reference set: managed `FilePaths` of retained items, same retention window) + Section 8 (soft-delete window inclusion).

---

## Task 4: Payload service constructor cutover + sync-service wiring (one green commit)

**complexity:** high
**critical:** true  _(foundational AND irreversible interface change — the import pipeline, all `ClipboardSyncPayloadServiceTests`, AND the sole production call site depend on this signature; explicitly flagged in the spec)_

> **B1 fix:** the constructor signature change AND its sole production call site
> (`ClipboardWebDavSyncService.cs:42`) change together here, in one commit that builds green.
> There is NO legacy two-arg constructor overload. (The former standalone DI-wiring task
> is merged into this task.)

**Files:**
- Modify: `src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs:13-24` (fields + ctor) — wiring only in this task; import bodies relocated in Task 5.
- Modify: `src/Recents.App/Services/ClipboardSync/ClipboardWebDavSyncService.cs:18,41-45` (production call site + `_outgoingDirectory` field).
- Modify: `src/Recents.App/App.xaml.cs` — only if a genuine compile error surfaces (no functional change expected here; legacy-incoming sequencing is added in Task 6).
- Modify: `tests/Recents.App.Tests/ClipboardSync/ClipboardSyncPayloadServiceTests.cs:480-503` (fixture construction).

This task changes the constructor/field shape, updates the production call site so the app compiles, and updates the test fixture; the actual `ImportAsync` body relocation is Task 5. To avoid a half-built state, the remaining `ImportAsync` helpers are mechanically re-pointed at the injected managed storage (Task 5 finalizes routing).

- [ ] **Step 1: Update the constructor + fields**

Replace lines 13-24 of `ClipboardSyncPayloadService.cs`:

```csharp
internal sealed class ClipboardSyncPayloadService
{
    private readonly string _outgoingDirectory;
    private readonly IClipboardManagedStorage _managed;

    public ClipboardSyncPayloadService(string outgoingDirectory, IClipboardManagedStorage managed)
    {
        _outgoingDirectory = outgoingDirectory;
        _managed = managed;
        Directory.CreateDirectory(_outgoingDirectory);
        Directory.CreateDirectory(_managed.ImageDirectory);
        Directory.CreateDirectory(_managed.ThumbnailDirectory);
        Directory.CreateDirectory(_managed.FilesDirectory);
    }
```

At this step the remaining `ImportAsync` helpers still reference `_incomingDirectory`. To keep the file compiling within this task, do a mechanical rename of every `_incomingDirectory` occurrence to `_managed.FilesDirectory` (Task 5 will properly route images→`ImageDirectory` and add thumbnails). Locations to update (all currently `Path.Combine(_incomingDirectory, SafeName(profile.Hash))`): `ImportImageAsync` (line 269), `ImportGroupAsync` (line 510), `CopyIncomingPayloadAsync` (line 521). This is a transient intermediate; Task 5 finalizes the routing AND renames the `CopyIncoming*` helpers to `CopyManaged*` (R2-4). Task 4's zero-reference check (Step 6) targets only the `_incomingDirectory`/`incomingDirectory` constructor field/param — the method names are still `CopyIncoming*` at the end of Task 4 and are removed/renamed in Task 5.

- [ ] **Step 2: Update the sole production call site (B1)**

In `ClipboardWebDavSyncService.cs`, add a field next to `_downloadDirectory` (line 18):

```csharp
    private readonly string _outgoingDirectory;
```

Replace the construction at lines 41-45:

```csharp
        var syncRoot = Path.Combine(_store.DataDirectory, "webdav");
        _outgoingDirectory = Path.Combine(syncRoot, "outgoing");
        _downloadDirectory = Path.Combine(syncRoot, "downloads");
        _payloads = new ClipboardSyncPayloadService(_outgoingDirectory, _store);
```

`_store` already implements `IClipboardManagedStorage` (Task 2), so it is passed directly. Do NOT create or reference any `incoming` path here (staging wipe + legacy-incoming deletion are added in Task 6).

- [ ] **Step 3: Update the test fixture (hard cut — no incoming arg)**

In `ClipboardSyncPayloadServiceTests.cs`, replace the fixture (lines 480-503) so it builds a managed-storage stand-in over temp dirs and passes it in:

```csharp
    private sealed class ClipboardSyncPayloadFixture : IDisposable
    {
        private readonly string _root;
        public string SourceDirectory { get; }
        public string ImageDirectory { get; }
        public string ThumbnailDirectory { get; }
        public string FilesDirectory { get; }
        public ClipboardSyncPayloadService Service { get; }

        private ClipboardSyncPayloadFixture(string root)
        {
            _root = root;
            SourceDirectory = Path.Combine(root, "source");
            ImageDirectory = Path.Combine(root, "images");
            ThumbnailDirectory = Path.Combine(root, "thumbs");
            FilesDirectory = Path.Combine(root, "files");
            Directory.CreateDirectory(SourceDirectory);
            var managed = new TestManagedStorage(ImageDirectory, ThumbnailDirectory, FilesDirectory);
            Service = new ClipboardSyncPayloadService(Path.Combine(root, "outgoing"), managed);
        }

        public static ClipboardSyncPayloadFixture Create() =>
            new(Path.Combine(AppContext.BaseDirectory, "clipboard-sync-payload-tests", Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private sealed class TestManagedStorage : Recents.App.Services.Clipboard.IClipboardManagedStorage
    {
        public TestManagedStorage(string imageDir, string thumbDir, string filesDir)
        {
            ImageDirectory = imageDir;
            ThumbnailDirectory = thumbDir;
            FilesDirectory = filesDir;
        }

        public string ImageDirectory { get; }
        public string ThumbnailDirectory { get; }
        public string FilesDirectory { get; }

        public void WriteThumbnail(System.Windows.Media.Imaging.BitmapSource source, string path, byte[] fallbackPngBytes)
            => Recents.App.Services.Clipboard.ClipboardThumbnailWriter.WriteJpegThumbnail(source, path, fallbackPngBytes);
    }
```

- [ ] **Step 4: Build + publish to verify the cutover compiles (incl. production call site)**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```
Expected: build SUCCEEDED (no references to a two-arg `(string,string)` constructor remain — the production call site now compiles); publish SUCCEEDED.

- [ ] **Step 5: Run the payload + sync service test suites**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardSyncPayloadServiceTests|FullyQualifiedName~ClipboardWebDavSyncServiceTests"`
Expected: PASS (existing assertions check `File.Exists` / `EndsWith(".png")` / extracted file contents, which still hold under the relocated dirs; sync service still constructs cleanly).

- [ ] **Step 6: Zero-reference check (old ctor / incoming arg gone)**

Run: `git --no-pager grep -n "incomingDirectory\|_incomingDirectory" -- src/ tests/`
Expected: NO matches.

- [ ] **Step 7: Commit**

```
git add src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs src/Recents.App/Services/ClipboardSync/ClipboardWebDavSyncService.cs src/Recents.App/App.xaml.cs tests/Recents.App.Tests/ClipboardSync/ClipboardSyncPayloadServiceTests.cs
git commit -m "refactor!: ClipboardSyncPayloadService takes managed storage; wire sync service, drop incoming dir"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardSyncPayloadServiceTests|FullyQualifiedName~ClipboardWebDavSyncServiceTests"
```
**spec-check:** Section 6.2 (constructor changes: drop `incomingDirectory`, accept managed dirs) + Section 7 (sync service constructs payload service with managed dirs) — foundational interface + its sole production call site land in one green commit.

---

## Task 5: Import to managed dirs (images→`images/`+`thumbs/`, files/groups→`files/`)

**complexity:** high
**critical:** true  _(irreversible storage-write change — imported content now lands in managed dirs reconciled by retention; image thumbnails must always be generated for decodable images)_

**Files:**
- Modify: `src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs` — `ImportImageAsync` (254-283), `ImportIncomingImageAsync`→`ImportManagedImageAsync` (285-318), `ImportFileAsync` (499-503), `ImportGroupAsync` (505-514), `CopyIncomingPayloadAsync`→`CopyManagedPayloadAsync` (516-524), `CopyIncomingAsync`→`CopyManagedAsync` (638-643).
- Modify: `tests/Recents.App.Tests/ClipboardSync/ClipboardSyncPayloadServiceTests.cs` — strengthen import assertions to managed dirs + thumbnail presence (standard + converted) + apply-path / not-saved coverage (M3).

- [ ] **Step 1: Write the failing assertions (managed dirs + thumbnail, standard + converted + not-saved apply path)**

Add/strengthen tests in `ClipboardSyncPayloadServiceTests.cs`:

```csharp
    [Fact]
    public async Task ImportAsync_StandardImageLandsInManagedImagesWithThumbnail()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var payloadPath = Path.Combine(fixture.SourceDirectory, "remote.png");
        await File.WriteAllBytesAsync(payloadPath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));

        var imported = await fixture.Service.ImportAsync(new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Image,
            Hash = "remote-img",
            Text = "remote.png",
            HasData = true,
            DataName = "remote.png",
            Size = new FileInfo(payloadPath).Length
        }, payloadPath);

        Assert.NotNull(imported.ImagePath);
        Assert.StartsWith(fixture.ImageDirectory, Path.GetFullPath(imported.ImagePath!), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(imported.ImagePath));            // apply path reads ImagePath directly
        Assert.NotNull(imported.ThumbnailPath);
        Assert.StartsWith(fixture.ThumbnailDirectory, Path.GetFullPath(imported.ThumbnailPath!), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(imported.ThumbnailPath));        // M4: thumbnail always written for a decodable image
    }

    [Fact]
    public async Task ImportAsync_ConvertedImageAlsoWritesThumbnail()
    {
        // Use an image filename whose extension routes through the complex/convert branch
        // (mirror an existing converted-format test's payload bytes + DataName).
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var (payloadPath, dataName) = WriteConvertibleImagePayload(fixture.SourceDirectory); // local helper mirroring existing convert test
        var imported = await fixture.Service.ImportAsync(new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Image,
            Hash = "remote-converted",
            Text = dataName,
            HasData = true,
            DataName = dataName,
            Size = new FileInfo(payloadPath).Length
        }, payloadPath);

        Assert.NotNull(imported.ImagePath);
        Assert.StartsWith(fixture.ImageDirectory, Path.GetFullPath(imported.ImagePath!), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(imported.ThumbnailPath);
        Assert.True(File.Exists(imported.ThumbnailPath));        // M4: converted path also writes a thumbnail
    }

    [Fact]
    public async Task ImportAsync_GroupLandsUnderManagedFilesDirectory()
    {
        using var fixture = ClipboardSyncPayloadFixture.Create();
        var filePath = Path.Combine(fixture.SourceDirectory, "note.txt");
        var otherPath = Path.Combine(fixture.SourceDirectory, "other.txt");
        await File.WriteAllTextAsync(filePath, "hello");
        await File.WriteAllTextAsync(otherPath, "world");
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Files, Hash = "g",
            FilePaths =
            [
                new ClipboardFilePath { Path = filePath, ExistsAtCapture = true },
                new ClipboardFilePath { Path = otherPath, ExistsAtCapture = true }
            ]
        };
        var export = await fixture.Service.ExportAsync(item, "d", "ws", 1024 * 1024);
        var imported = await fixture.Service.ImportAsync(export.Profile, export.PayloadPath);

        Assert.NotEmpty(imported.FilePaths);
        foreach (var f in imported.FilePaths)
        {
            Assert.StartsWith(fixture.FilesDirectory, Path.GetFullPath(f.Path), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(f.Path));                    // M3: on-disk content usable by the apply path even when not saved to history
        }
    }
```

(`WriteConvertibleImagePayload` mirrors the bytes/DataName an existing convert test already uses — reuse that helper if present rather than inventing new fixtures.)

**R2-3 — genuinely exercise the apply path + grace, history NOT ingested.** The tests above assert the imported paths/thumbnails exist; R2-3 requires actually running `ClipboardActionService.CreateDataObject(item)` over imported content that was created by `ImportAsync` but NEVER passed to `IngestAsync` (the `SaveRemoteItemsToHistory=false` semantics), asserting the data object carries usable content, AND that a subsequent `CompactOrphanBlobsAsync` does NOT delete that just-imported unreferenced content because it is within the 1-day grace. These run against a REAL `ClipboardStoreService` (so the payload service writes into the store's managed dirs and the store's compaction sees them). Construct the payload service over the real store (which implements `IClipboardManagedStorage` after Task 2):

```csharp
    [Fact]
    public async Task ImportedImage_NotIngested_AppliesViaDataObject_AndSurvivesCompactionGrace()
    {
        // Real store so import lands in store.ImageDirectory and store.CompactOrphanBlobsAsync sees it.
        var dir = Path.Combine(AppContext.BaseDirectory, "r2-3-image", Guid.NewGuid().ToString("N"));
        var settings = new SettingsService();
        var store = new ClipboardStoreService(settings, Path.Combine(dir, "data"), Path.Combine(dir, "clipboard.db"));
        var actions = new ClipboardActionService(store);
        store.AttachActions(actions);
        store.OpenDatabase();
        try
        {
            var payloads = new ClipboardSyncPayloadService(Path.Combine(dir, "outgoing"), store);
            var payloadPath = Path.Combine(dir, "remote.png");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(payloadPath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));

            var item = await payloads.ImportAsync(new SyncClipboardProfile
            {
                Type = SyncClipboardProfileType.Image, Hash = "img", Text = "remote.png",
                HasData = true, DataName = "remote.png", Size = new FileInfo(payloadPath).Length
            }, payloadPath);

            // History NOT ingested — item is unreferenced by the DB (mirrors SaveRemoteItemsToHistory=false).
            Assert.True(actions.HasUsableContent(item));
            var data = actions.CreateDataObject(item);
            Assert.True(data.GetDataPresent(System.Windows.DataFormats.Bitmap), "imported image must apply as a bitmap");
            Assert.True(data.GetDataPresent(System.Windows.DataFormats.FileDrop), "imported image must apply via file-drop compatibility");

            // M3/R2-3: the just-imported, unreferenced image must SURVIVE compaction (within 1-day grace).
            await store.CompactOrphanBlobsAsync();
            Assert.True(File.Exists(item.ImagePath!), "recently-imported unreferenced image must survive the grace window");
        }
        finally
        {
            store.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ImportedGroup_NotIngested_AppliesViaFileDrop_AndSurvivesCompactionGrace()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "r2-3-group", Guid.NewGuid().ToString("N"));
        var settings = new SettingsService();
        var store = new ClipboardStoreService(settings, Path.Combine(dir, "data"), Path.Combine(dir, "clipboard.db"));
        var actions = new ClipboardActionService(store);
        store.AttachActions(actions);
        store.OpenDatabase();
        try
        {
            var payloads = new ClipboardSyncPayloadService(Path.Combine(dir, "outgoing"), store);
            var srcDir = Path.Combine(dir, "src");
            Directory.CreateDirectory(srcDir);
            var f1 = Path.Combine(srcDir, "a.txt");
            var f2 = Path.Combine(srcDir, "b.txt");
            await File.WriteAllTextAsync(f1, "alpha");
            await File.WriteAllTextAsync(f2, "beta");
            var sourceItem = new ClipboardItem
            {
                Type = ClipboardPayloadType.Files, Hash = "g",
                FilePaths =
                [
                    new ClipboardFilePath { Path = f1, ExistsAtCapture = true },
                    new ClipboardFilePath { Path = f2, ExistsAtCapture = true }
                ]
            };
            var export = await payloads.ExportAsync(sourceItem, "d", "ws", 1024 * 1024);
            var item = await payloads.ImportAsync(export.Profile, export.PayloadPath);

            // History NOT ingested.
            Assert.True(actions.HasUsableContent(item));
            var data = actions.CreateDataObject(item);
            var drop = data.GetFileDropList();
            Assert.NotNull(drop);
            Assert.True(drop!.Count > 0, "imported group must apply as a non-empty file-drop list");
            foreach (var p in drop!)
                Assert.True(File.Exists(p), "every applied file-drop path must exist on disk even when not saved to history");

            // M3/R2-3: unreferenced imported files must SURVIVE compaction (within 1-day grace).
            await store.CompactOrphanBlobsAsync();
            foreach (var fp in item.FilePaths)
                Assert.True(File.Exists(fp.Path), "recently-imported unreferenced file must survive the grace window");
        }
        finally
        {
            store.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
```

(These tests depend on Task 7's grace; in this task's TDD cycle they may be authored alongside the import work and confirmed green once Task 7's grace lands. If run strictly at Task 5 before Task 5's import routing exists, they fail on the missing managed-dir/thumbnail behavior first, which is the intended red. The compaction-grace assertion is also covered independently in Task 7's `Compact_KeepsRecentlyImportedUnreferencedImageWithinGrace`; the value added here is exercising the real `CreateDataObject` apply path with history NOT ingested — R2-3.)

- [ ] **Step 2: Run to verify the new assertions fail**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardSyncPayloadServiceTests.ImportAsync_StandardImageLandsInManagedImagesWithThumbnail|FullyQualifiedName~ClipboardSyncPayloadServiceTests.ImportAsync_ConvertedImageAlsoWritesThumbnail|FullyQualifiedName~ClipboardSyncPayloadServiceTests.ImportAsync_GroupLandsUnderManagedFilesDirectory"`
Expected: FAIL — `ThumbnailPath` null (no thumbnail generated yet).

- [ ] **Step 3: Route images to `images/` with naming + thumbnail (ALWAYS for decodable images — M4)**

Rewrite `ImportImageAsync` so the image lands in `_managed.ImageDirectory` using `ClipboardBlobNamer` (same as local capture) and a thumbnail is written to `_managed.ThumbnailDirectory` for every decodable image (standard AND converted):

```csharp
    private async Task<ClipboardItem> ImportImageAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        var created = DateTime.UtcNow;
        var item = new ClipboardItem
        {
            Type = ClipboardPayloadType.Image,
            CreatedUtc = created,
            LastUsedUtc = created,
            PreviewText = profile.Text,
            SizeBytes = profile.Size
        };

        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return item;

        var image = await ImportManagedImageAsync(
            payloadPath,
            _managed.ImageDirectory,
            Path.GetFileName(profile.DataName ?? profile.Text ?? "remote.png")).ConfigureAwait(false);

        item.ImagePath = image.Path;
        item.Hash = ClipboardHash.ForImage(image.Bytes);
        item.SizeBytes = image.Bytes.LongLength;
        item.ImageWidth = image.Width;
        item.ImageHeight = image.Height;
        if (image.Width.HasValue && image.Height.HasValue && string.IsNullOrWhiteSpace(item.PreviewText))
            item.PreviewText = $"Screenshot {image.Width}x{image.Height}";

        TryWriteImportedThumbnail(item, image.Bytes, created);
        return item;
    }
```

Replace `ImportIncomingImageAsync` with `ImportManagedImageAsync` — same normalization logic, but target the managed image dir using `ClipboardBlobNamer.Build` + `EnsureUnique` to match local naming, and preserve the source/converted extension. Keep the existing standard/complex/unknown branches; only the target path construction changes. CRITICAL (M4): the standard branch and the converted branch BOTH return decoded bytes so `TryWriteImportedThumbnail` always produces a thumbnail; only the genuinely-undecodable fallback (raw copy) may end up without one.

```csharp
    private static async Task<ImportedImagePayload> ImportManagedImageAsync(
        string payloadPath,
        string imageDirectory,
        string fileName)
    {
        var safeFileName = SyncClipboardPayloadFormats.SafePayloadFileName(fileName, "remote.png");
        var sourceBytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);

        if (SyncClipboardPayloadFormats.IsStandardImageFileName(safeFileName))
        {
            var target = ManagedImageTarget(imageDirectory, safeFileName);
            await WriteBytesAsync(target, sourceBytes).ConfigureAwait(false);
            var dimensions = TryReadImageDimensions(sourceBytes);
            return new ImportedImagePayload(target, sourceBytes, dimensions.Width, dimensions.Height);
        }

        if (SyncClipboardPayloadFormats.IsComplexImageFileName(safeFileName) &&
            TryConvertComplexImageBytes(sourceBytes, safeFileName, out var convertedName, out var convertedBytes, out var convertedWidth, out var convertedHeight))
        {
            var target = ManagedImageTarget(imageDirectory, convertedName);
            await WriteBytesAsync(target, convertedBytes).ConfigureAwait(false);
            return new ImportedImagePayload(target, convertedBytes, convertedWidth, convertedHeight);
        }

        if (TryNormalizeUnknownImageBytes(sourceBytes, safeFileName, out var pngName, out var pngBytes, out var width, out var height))
        {
            var target = ManagedImageTarget(imageDirectory, pngName);
            await WriteBytesAsync(target, pngBytes).ConfigureAwait(false);
            return new ImportedImagePayload(target, pngBytes, width, height);
        }

        var fallback = ClipboardBlobNamer.EnsureUnique(imageDirectory, safeFileName);
        await CopyFileAsync(payloadPath, fallback).ConfigureAwait(false);
        return new ImportedImagePayload(fallback, sourceBytes, null, null);
    }

    private static string ManagedImageTarget(string imageDirectory, string fileName)
        => ClipboardBlobNamer.EnsureUnique(imageDirectory, fileName);
```

Add the thumbnail writer that decodes the stored image bytes to a `BitmapSource` and calls the shared helper via the injected managed storage. For a decodable image the thumbnail is always produced (the `catch` only protects against a genuinely-corrupt fallback copy):

```csharp
    private void TryWriteImportedThumbnail(ClipboardItem item, byte[] imageBytes, DateTime created)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath))
            return;
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            var thumbName = ClipboardBlobNamer.Build(ClipboardPayloadType.Image, created, item.Hash, ".jpg");
            var thumbPath = ClipboardBlobNamer.EnsureUnique(_managed.ThumbnailDirectory, thumbName);
            _managed.WriteThumbnail(frame, thumbPath, imageBytes);
            item.ThumbnailPath = thumbPath;
        }
        catch
        {
            // Only reached for a genuinely-undecodable fallback copy; image remains usable without a thumbnail.
        }
    }
```

(`ClipboardBlobNamer`/`ClipboardHash`/`ClipboardPayloadType` are in `Recents.App.Services.Clipboard` / `Recents.App.Models`, already imported.)

- [ ] **Step 4: Rename the file-drop copy helpers to managed-storage names (R2-4), then route file/group to managed `files/` subdirs**

**R2-4 — explicit rename FIRST.** The legacy helpers `CopyIncomingPayloadAsync` (`ClipboardSyncPayloadService.cs:516`) and `CopyIncomingAsync` (`:638`) contain `Incoming` in their names, which would defeat this task's own `git --no-pager grep -ni "incoming"` zero-reference check (Step 7). Before editing their bodies, rename them and ALL call sites:
- `CopyIncomingPayloadAsync` → `CopyManagedPayloadAsync` (sole call site: `ImportFileAsync` at `:501`).
- `CopyIncomingAsync` → `CopyManagedAsync` (call sites: `ImportManagedImageAsync` fallback branch — formerly `ImportIncomingImageAsync` at `:316` — and the renamed `CopyManagedPayloadAsync`).

Then write the file/group routing under `_managed.FilesDirectory/<subdir>/`, where `<subdir>` is a per-import folder (keep `SafeName(profile.Hash)` for stable naming — reconciliation is path-prefix based per Section 6.4, so the name is an implementation detail):

```csharp
    private async Task<ClipboardItem> ImportGroupAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return FileDropItem(profile, []);

        var itemDirectory = Path.Combine(_managed.FilesDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        var paths = ExtractZip(payloadPath, itemDirectory);
        return FileDropItem(profile, paths);
    }

    private async Task<string?> CopyManagedPayloadAsync(SyncClipboardProfile profile, string? payloadPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            return null;

        var itemDirectory = Path.Combine(_managed.FilesDirectory, SafeName(profile.Hash));
        Directory.CreateDirectory(itemDirectory);
        return await CopyManagedAsync(payloadPath, itemDirectory, Path.GetFileName(fileName)).ConfigureAwait(false);
    }
```

And the renamed copy primitive (body unchanged from `CopyIncomingAsync`):

```csharp
    private static async Task<string> CopyManagedAsync(string source, string targetDirectory, string fileName)
    {
        var target = Path.Combine(targetDirectory, fileName);
        await CopyFileAsync(source, target).ConfigureAwait(false);
        return target;
    }
```

Update `ImportFileAsync` to call `CopyManagedPayloadAsync`:

```csharp
    private async Task<ClipboardItem> ImportFileAsync(SyncClipboardProfile profile, string? payloadPath)
    {
        var path = await CopyManagedPayloadAsync(profile, payloadPath, profile.DataName ?? profile.Text).ConfigureAwait(false);
        return FileDropItem(profile, path is null ? [] : [path]);
    }
```

Remove the now-unused `ImportIncomingImageAsync` method (replaced by `ImportManagedImageAsync` in Step 3; its fallback branch now calls `CopyManagedAsync`). Ensure no `_incomingDirectory` / `_incoming` symbols and no `CopyIncoming*` names remain.

- [ ] **Step 5: Run the new + full payload tests**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardSyncPayloadServiceTests"`
Expected: PASS (including the new standard/converted thumbnail tests, the group not-saved on-disk test, the R2-3 apply-path tests that run `CreateDataObject` with history NOT ingested and assert survival across `CompactOrphanBlobsAsync`, and all pre-existing import/export tests — extension-preservation assertions like `.png`/`.jpeg` still hold because `ManagedImageTarget` keeps the filename and `EnsureUnique` preserves the extension). The two R2-3 tests need Task 7's grace to be green end-to-end; if Task 5 lands before Task 7 in your sequence, gate them behind Task 7 or assert only the apply-path half here and the grace half in Task 7.

- [ ] **Step 6: Build + publish**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```
Expected: build SUCCEEDED; publish SUCCEEDED.

- [ ] **Step 7: Zero-reference check (no incoming usage in import)**

Run: `git --no-pager grep -n "incoming\|Incoming" -- src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs`
Expected: NO matches.

- [ ] **Step 8: Commit**

```
git add src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs tests/Recents.App.Tests/ClipboardSync/ClipboardSyncPayloadServiceTests.cs
git commit -m "feat: import remote content into managed images/thumbs/files dirs with thumbnails"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardSyncPayloadServiceTests"
```
**spec-check:** Section 6.2 table (Image→`images/`+`thumbs/`; File/Group→`files/<subdir>/`) + Acceptance criterion 3 (imported images/files re-pasteable + thumbnails for standard AND converted) + Section 8 (`SaveRemoteItemsToHistory=false` apply path: on-disk content usable) + R2-4 (helpers renamed to managed-storage names so the zero-`incoming` grep passes).

---

## Task 6: Staging delete-after-use + startup wipe + legacy `incoming/` deletion (App-ordered)

**complexity:** high
**critical:** true  _(irreversible — startup deletes legacy `webdav/incoming/` entirely, the spec-mandated hard-cut migration; sequencing relative to store load matters)_

**Files:**
- Modify: `src/Recents.App/Services/ClipboardSync/ClipboardWebDavSyncService.cs` — `Start()` (50-55: wipe staging), `UploadCapturedAsync` (delegate export→duplicate-check→upload to `RunUploadFlowAsync`, which deletes `export.PayloadPath` in a `finally` covering ALL exit paths incl. the duplicate short-circuit — R2-1), `PollOnceAsync` (delete `payloadPath` in `finally` via the download-import seam), add static helpers.
- Modify: `src/Recents.App/App.xaml.cs` — call `ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory(syncRoot)` BEFORE `InitializeClipboardStoreAsync()` (line 88), i.e. before the store loads (M1).
- Create: `tests/Recents.App.Tests/ClipboardSync/ClipboardWebDavStagingTests.cs`.

> **M1:** Legacy-`incoming` deletion must happen BEFORE the store load so `HasUsableContent`
> prunes dangling incoming-backed rows on load (`LoadFromDatabaseSnapshot`) per spec §6.7.
> `App.xaml.cs:88` (`InitializeClipboardStoreAsync`) loads the store; `:114 Start()` runs much
> later. The staging wipe (outgoing/downloads) stays in `Start()`; only the legacy-incoming
> deletion is hoisted into `App.xaml.cs` before store load.
>
> **M5:** The `finally` delete-after-use paths are exercised through a minimal testable seam
> (injectable/fakeable `IWebDavPayloadTransport`) so the actual deletions are asserted on
> success AND on exception/exhausted-retry — the wipe-helper tests are NOT sufficient on their
> own. The seam exposes two internal flow methods: `RunUploadFlowAsync` (owns the outgoing
> `finally` over duplicate-check + upload) and `DownloadImportThenDeleteAsync` (owns the
> downloads `finally`). The transport interface itself does only network I/O — see R2-1.
>
> **R2-1:** The outgoing staged payload (`export.PayloadPath`, created by `ExportAsync` early
> in `UploadCapturedAsync`) must be deleted even when the **duplicate short-circuit** fires
> (`GetProfileAsync` says "remote already matches" → the method returns at
> `ClipboardWebDavSyncService.cs:88`, BEFORE any upload). The deletion therefore lives in a
> `finally` wrapping the WHOLE exported-payload lifetime — the GetProfile duplicate-check AND
> the upload. `UploadCapturedAsync` runs `ExportAsync` then delegates that whole sequence to
> `RunUploadFlowAsync(transport, export, sync)`, whose `try/finally` is the single source of
> truth for deleting the staged outgoing payload on all three exit paths (duplicate
> short-circuit, upload success, exhausted-retry throw). The upload helper itself is pure (no
> delete). A test asserts the `outgoing/` staged file is gone on the duplicate-short-circuit
> path too.
>
> **R2-2:** The export record is named `ClipboardSyncExport` (see
> `ClipboardSyncPayloadService.cs:11`). The seam/tests below use `ClipboardSyncExport`
> everywhere — there is NO `ClipboardSyncPayloadExport` type.

- [ ] **Step 1: Add static staging helpers + a fakeable client seam**

Add to `ClipboardWebDavSyncService.cs`:

```csharp
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
```

Introduce the **testable seam (M5)**. Extract the upload flow and the download-import flow into `internal` methods over a small `IWebDavPayloadTransport` interface (`IsRemoteDuplicateAsync`/`UploadAsync`/`DownloadAsync`) that the real `WebDavClipboardClient` (or a thin adapter) implements and a fake can stand in for. `RunUploadFlowAsync` owns the OUTGOING `try/finally` over the WHOLE export→duplicate-check→upload sequence (R2-1); `DownloadImportThenDeleteAsync` owns the DOWNLOADS `try/finally`. Both let the test drive success and throw (and, for upload, the duplicate short-circuit) without live HTTP:

```csharp
    internal interface IWebDavPayloadTransport
    {
        // True ⇒ remote already matches this export → skip the upload (the duplicate
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
```

(`ClipboardSyncExport` is the existing export record exposing `PayloadPath` — see `ClipboardSyncPayloadService.cs:11`. The `outgoing/` delete-after-use lives in `RunUploadFlowAsync`'s `finally`, which wraps the duplicate-check AND the upload, so it also covers the duplicate short-circuit. `DownloadImportThenDeleteAsync` owns the `downloads/` delete-after-use. `UploadCapturedAsync` runs `ExportAsync` and then delegates the rest to `RunUploadFlowAsync`.)

- [ ] **Step 2: Write the failing tests (wipe + legacy + delete-after-use success AND failure)**

Create `tests/Recents.App.Tests/ClipboardSync/ClipboardWebDavStagingTests.cs`:

```csharp
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
    // RunUploadFlowAsync seam (Step 4) so all three exit paths delete export.PayloadPath:
    //   (a) normal upload success, (b) upload throws (exhausted retry), (c) duplicate
    //   short-circuit (remote already matches → returns BEFORE any upload).
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

    // StubExport / StubSync mirror the real ClipboardSyncExport + ClipboardWebDavSyncSettings
    // constructors — fill required members minimally during implementation.
    // ClipboardSyncExport is a positional record: (SyncClipboardProfile Profile, string? PayloadPath, string LocalHash).
    private static ClipboardSyncExport StubExport(string payloadPath) =>
        new(new SyncClipboardProfile { Type = SyncClipboardProfileType.File, HasData = true, DataName = "outgoing.payload" }, payloadPath, "local-hash");
    private static ClipboardWebDavSyncSettings StubSync() => /* construct minimal sync settings */ throw new NotImplementedException();
}
```

(`StubExport` constructs the real `ClipboardSyncExport` record directly; `StubSync` is the only placeholder — wire it to the real `ClipboardWebDavSyncSettings` constructor when implementing. It MUST be real before the test compiles.)

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardWebDavStagingTests"`
Expected: FAIL — helpers/seam do not exist (until Steps 1 & 4 compile) / behavior absent.

- [ ] **Step 4: Wire the seam into `UploadCapturedAsync` + `PollOnceAsync`; wipe staging in `Start()`**

In `Start()` (lines 50-55), at the top before scheduling the timer, wipe both staging dirs (NOT legacy-incoming — that moves to `App.xaml.cs`):

```csharp
    public void Start()
    {
        WipeStagingDirectory(_outgoingDirectory);
        WipeStagingDirectory(_downloadDirectory);

        var interval = TimeSpan.FromSeconds(_settings.Current.ClipboardWebDavSync.PollIntervalSeconds);
        _pollTimer ??= new System.Threading.Timer(_ => _ = PollOnceAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pollTimer.Change(TimeSpan.Zero, interval);
    }
```

Make the real `WebDavClipboardClient` (or a thin adapter) implement `IWebDavPayloadTransport`:
- `IsRemoteDuplicateAsync(export)` wraps the existing `GetProfileAsync()` + `RemoteContentKey` compare (the current `ClipboardWebDavSyncService.cs:82-89` duplicate short-circuit) and, when it returns `true`, also performs the existing `_lastSyncedRemoteKey`/`_lastSyncedLocalHash` bookkeeping the short-circuit currently does.
- `UploadAsync` runs the existing retry loop (`UploadWithRetryAsync`).
- `DownloadAsync` wraps `DownloadPayloadAsync`.

Route `UploadCapturedAsync`'s WHOLE export→duplicate-check→upload sequence through `RunUploadFlowAsync(transport, export, sync)` (R2-1): `ExportAsync` runs first to produce `export`, then `RunUploadFlowAsync` owns the `try/finally` that deletes `export.PayloadPath` on EVERY exit path — duplicate short-circuit, upload success, and exhausted-retry throw. Do NOT delete the outgoing payload anywhere else. Route `PollOnceAsync`'s download+import through `DownloadImportThenDeleteAsync(..., consume: ImportAndApplyAsync)`, where `consume` performs the existing `ImportAsync` + dispatcher apply + conditional `IngestAsync`. The existing outer `catch`/`finally` (semaphore release) is untouched — `RunUploadFlowAsync`'s `try/finally` lives inside the existing `try`.

- [ ] **Step 5: Sequence legacy-incoming deletion in `App.xaml.cs` BEFORE store load (M1)**

In `App.xaml.cs`, immediately BEFORE `_ = InitializeClipboardStoreAsync();` (line 88) — and after `_clipboardStore` is constructed (line 76, which gives `DataDirectory`) — add:

```csharp
            var clipboardSyncRoot = Path.Combine(_clipboardStore.DataDirectory, "webdav");
            ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory(clipboardSyncRoot);
```

This deletes `webdav/incoming/` before the store load, so `HasUsableContent` prunes the dangling incoming-backed rows during `LoadFromDatabaseSnapshot`. Do NOT call it again from `Start()`.

- [ ] **Step 6: Run staging tests + sync regression**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardWebDavStagingTests|FullyQualifiedName~ClipboardWebDavSyncServiceTests"`
Expected: PASS (wipe, legacy removal, the three outgoing-flow assertions — upload-success, upload-throw, duplicate-short-circuit (R2-1) — and the two download delete-after-use success/exception assertions).

- [ ] **Step 7: Build + publish**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```
Expected: build SUCCEEDED; publish SUCCEEDED.

- [ ] **Step 8: Commit**

```
git add src/Recents.App/Services/ClipboardSync/ClipboardWebDavSyncService.cs src/Recents.App/App.xaml.cs tests/Recents.App.Tests/ClipboardSync/ClipboardWebDavStagingTests.cs
git commit -m "feat: delete-after-use staging + wipe-on-startup + legacy incoming removal before store load"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardWebDavStagingTests|FullyQualifiedName~ClipboardWebDavSyncServiceTests"
```
**spec-check:** Section 6.1 (delete-after-use + wipe-on-startup; finally survives retries/exceptions AND the duplicate short-circuit — R2-1) + Section 6.7 migration (delete legacy `incoming/` before store load so dangling rows prune on load) + Acceptance criteria 1 & 2.

---

## Task 7: `files/` subtree reconciliation + grace on blob/image/thumb (with 1-day grace)

**complexity:** high
**critical:** true  _(irreversible deletion within `FilesDirectory` AND across blob/image/thumb — a wrong containment check or missing grace would delete live or just-imported content)_

**Files:**
- Modify: `src/Recents.App/Services/Clipboard/ClipboardStoreService.cs` — `CompactOrphanBlobsAsync` (487-517), add `FilesGraceWindow` constant + `DeleteUnreferencedFileTrees` helper, and PARAMETERIZE `DeleteUnreferencedFiles` with a grace cutoff (677-697).
- Test: `tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs`.

> **M2:** `DeleteUnreferencedFiles` gains a 1-day grace cutoff (by file mtime) so imported
> not-saved images (referenced by the clipboard's file-drop/HTML/QQ compatibility formats via
> `ImagePath`) are not deleted prematurely. Apply the grace to blob/image/thumb calls. Reuse
> the SAME `FilesGraceWindow` constant.
>
> **m1:** `DeleteUnreferencedFileTrees` removes EMPTY subdirs regardless of grace (spec §6.4).

- [ ] **Step 1: Write the failing reconciliation + grace tests**

Add to `ClipboardStoreServiceTests`:

```csharp
    [Fact]
    public async Task Compact_KeepsReferencedFilesTrees_DeletesUnreferencedPastGrace_KeepsRecent_RemovesEmpty()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        // Referenced subtree: belongs to a live item.
        var refSub = Path.Combine(store.FilesDirectory, "referenced");
        Directory.CreateDirectory(refSub);
        var refFile = Path.Combine(refSub, "keep.txt");
        await File.WriteAllTextAsync(refFile, "keep");

        var item = new ClipboardItem
        {
            Id = "files-item", Type = ClipboardPayloadType.Files,
            Hash = "files-hash", CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "keep.txt", PlainText = refFile,
            FilePaths = [new ClipboardFilePath { Path = refFile, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);

        // Unreferenced + OLD subtree (past 1-day grace): must be deleted.
        var oldSub = Path.Combine(store.FilesDirectory, "orphan-old");
        Directory.CreateDirectory(oldSub);
        var oldFile = Path.Combine(oldSub, "old.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        Directory.SetLastWriteTimeUtc(oldSub, DateTime.UtcNow.AddDays(-3));

        // Unreferenced + RECENT subtree (within grace): must survive.
        var recentSub = Path.Combine(store.FilesDirectory, "orphan-recent");
        Directory.CreateDirectory(recentSub);
        var recentFile = Path.Combine(recentSub, "recent.txt");
        await File.WriteAllTextAsync(recentFile, "recent");

        // Empty subdir (m1): must be removed regardless of grace, even if recent.
        var emptySub = Path.Combine(store.FilesDirectory, "empty-recent");
        Directory.CreateDirectory(emptySub);

        await store.CompactOrphanBlobsAsync();

        Assert.True(Directory.Exists(refSub), "referenced subtree must be kept");
        Assert.True(File.Exists(refFile));
        Assert.False(Directory.Exists(oldSub), "old unreferenced subtree past grace must be deleted");
        Assert.True(Directory.Exists(recentSub), "recent unreferenced subtree within grace must be kept");
        Assert.False(Directory.Exists(emptySub), "empty subdir must be removed regardless of grace (m1)");
    }

    [Fact]
    public async Task Compact_NeverDeletesUserRealPathFiles()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        // A local file drop referencing the user's real path (OUTSIDE FilesDirectory).
        var userDir = Path.Combine(AppContext.BaseDirectory, "user-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        var userFile = Path.Combine(userDir, "mydoc.txt");
        await File.WriteAllTextAsync(userFile, "user content");

        var item = new ClipboardItem
        {
            Id = "local-files", Type = ClipboardPayloadType.Files,
            Hash = "local-hash", CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "mydoc.txt", PlainText = userFile,
            FilePaths = [new ClipboardFilePath { Path = userFile, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);

        await store.CompactOrphanBlobsAsync();

        Assert.True(File.Exists(userFile), "user real path must never be touched by reconciliation");
        try { Directory.Delete(userDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Compact_KeepsRecentlyImportedUnreferencedImageWithinGrace()
    {
        // M2: an imported not-saved image lands in images/ unreferenced; the clipboard apply
        // references ImagePath until the next clipboard change. The 1-day mtime grace must keep it.
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var orphanImg = Path.Combine(store.ImageDirectory, "just-imported.png");
        await File.WriteAllBytesAsync(orphanImg, new byte[] { 1, 2, 3 }); // mtime = now

        await store.CompactOrphanBlobsAsync();

        Assert.True(File.Exists(orphanImg), "recently-imported unreferenced image must survive within the grace window (M2)");
    }
```

NOTE (M2): If an existing compaction test asserts immediate deletion of a just-written orphan blob/image/thumb, UPDATE it — either set the orphan file's mtime into the past (`File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3))`) before compaction, or adjust its expectation to "kept within grace". Find such tests via the run in Step 6 and fix them in this task.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests.Compact_KeepsReferencedFilesTrees_DeletesUnreferencedPastGrace_KeepsRecent_RemovesEmpty|FullyQualifiedName~ClipboardStoreServiceTests.Compact_NeverDeletesUserRealPathFiles|FullyQualifiedName~ClipboardStoreServiceTests.Compact_KeepsRecentlyImportedUnreferencedImageWithinGrace"`
Expected: FAIL — no `files/` reconciliation yet; image grace not applied.

- [ ] **Step 3: Add the constant + parameterize `DeleteUnreferencedFiles` with grace (M2)**

In `ClipboardStoreService.cs`, add the constant near the top of the class:

```csharp
    private static readonly TimeSpan FilesGraceWindow = TimeSpan.FromDays(1);
```

Parameterize the existing flat `DeleteUnreferencedFiles` (677-697) with a grace cutoff (mtime-based keep):

```csharp
    private static void DeleteUnreferencedFiles(string directory, IEnumerable<string?> referencedPaths, DateTime graceCutoffUtc)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var referenced = referencedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (referenced.Contains(Path.GetFullPath(file)))
                    continue;

                DateTime lastWrite;
                try { lastWrite = File.GetLastWriteTimeUtc(file); }
                catch { lastWrite = DateTime.UtcNow; }
                if (lastWrite >= graceCutoffUtc)
                    continue; // within grace — keep just-imported not-yet-referenced content

                TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: compact failed {Directory}", LogPrivacy.Format(directory));
        }
    }
```

- [ ] **Step 4: Add the subtree reconciliation helper (containment-safe; empty-subdir removal — m1)**

```csharp
    private void DeleteUnreferencedFileTrees(IEnumerable<string?> referencedPaths)
    {
        try
        {
            Directory.CreateDirectory(FilesDirectory);
            var filesRoot = Path.GetFullPath(FilesDirectory);

            // Only references that actually live under FilesDirectory matter here;
            // user-real-path drops are outside and must be ignored.
            var referenced = referencedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p!))
                .Where(p => p.StartsWith(filesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var graceCutoff = DateTime.UtcNow - FilesGraceWindow;

            foreach (var subdir in Directory.EnumerateDirectories(FilesDirectory))
            {
                var fullSub = Path.GetFullPath(subdir);
                var prefix = fullSub + Path.DirectorySeparatorChar;

                var isReferenced = referenced.Any(r =>
                    r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r, fullSub, StringComparison.OrdinalIgnoreCase));

                if (isReferenced)
                    continue;

                // m1: empty subdirs are removed regardless of grace.
                var isEmpty = !Directory.EnumerateFileSystemEntries(fullSub).Any();
                if (!isEmpty)
                {
                    DateTime lastWrite;
                    try { lastWrite = Directory.GetLastWriteTimeUtc(fullSub); }
                    catch { lastWrite = DateTime.UtcNow; }

                    if (lastWrite >= graceCutoff)
                        continue; // non-empty + within grace — keep transient just-imported content
                }

                try { Directory.Delete(fullSub, recursive: true); }
                catch (Exception ex) { Log.Warning(ex, "ClipboardStoreService: failed to delete file tree {Dir}", LogPrivacy.Format(fullSub)); }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: files reconciliation failed");
        }
    }
```

- [ ] **Step 5: Call reconciliation + pass grace cutoff from `CompactOrphanBlobsAsync`**

In `CompactOrphanBlobsAsync` (487-517), compute a single grace cutoff and pass it to every `DeleteUnreferencedFiles` call, then add the `files/` subtree pass (using the same `deletedCutoffUtc` for the reference query):

```csharp
                var graceCutoffUtc = DateTime.UtcNow - FilesGraceWindow;
                DeleteUnreferencedFiles(BlobDirectory, retainedNormalPaths, graceCutoffUtc);
                DeleteUnreferencedFiles(ImageDirectory, retainedNormalPaths, graceCutoffUtc);
                DeleteUnreferencedFiles(ThumbnailDirectory, retainedNormalPaths, graceCutoffUtc);
                var favoritePaths = _repo.LoadFavorites();
                DeleteUnreferencedFiles(FavoriteBlobDirectory, favoritePaths.SelectMany(v => new[] { v.BlobPath, v.HtmlBlobPath, v.RtfBlobPath }), graceCutoffUtc);
                DeleteUnreferencedFiles(FavoriteImageDirectory, favoritePaths.Select(v => v.ImagePath), graceCutoffUtc);
                DeleteUnreferencedFiles(FavoriteThumbnailDirectory, favoritePaths.Select(v => v.ThumbnailPath), graceCutoffUtc);

                var retainedManagedFilePaths = _repo.LoadRetainedManagedFilePaths(deletedCutoffUtc);
                DeleteUnreferencedFileTrees(retainedManagedFilePaths);
```

(Favorite copies are durable, but applying the same grace cutoff is harmless — orphan favorite files are reconciled by the dedicated favorite-files pass in Task 9; the flat grace only ever delays, never wrongly deletes, a favorite file.)

- [ ] **Step 6: Run reconciliation tests + full store regression (fix any immediate-delete assumption)**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests"`
Expected: PASS. If a pre-existing test asserted immediate deletion of a just-written orphan, update it per the Step 1 NOTE (past mtime or adjusted expectation) and re-run.

- [ ] **Step 7: Build + publish**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```
Expected: build SUCCEEDED; publish SUCCEEDED.

- [ ] **Step 8: Commit**

```
git add src/Recents.App/Services/Clipboard/ClipboardStoreService.cs tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs
git commit -m "feat: reconcile managed files/ subtrees + 1-day grace on blob/image/thumb in compaction"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests"
```
**spec-check:** Section 6.4 (subtree reconciliation, 1-day grace, containment safety, empty-subdir removal) + Section 8 (grace protects `SaveRemoteItemsToHistory=false` images via mtime) + Acceptance criterion 4.

---

## Task 8: Favorites — copy managed file-drop content into `favorites/files/`

**complexity:** high
**critical:** true  _(irreversible favorite-snapshot write — incorrect copy/rewrite would lose favorite content on source pruning)_

**Files:**
- Modify: `src/Recents.App/Services/Clipboard/ClipboardStoreService.cs` — `CreateFavoriteSnapshot` (519-551), `DeleteFavoriteFiles` (599-607), `TryRepairFavoriteSnapshotLocked` (553-573).
- Test: `tests/Recents.App.Tests/Clipboard/ClipboardFavoritesTests.cs` (or `ClipboardStoreServiceTests`).

> **M6:** On a managed-path copy FAILURE, `CopyManagedFileDropForFavorite` must FAIL (return
> `null`) — it must NEVER return the source managed `files/` path (which would make the
> favorite share, then lose, source content on prune/ClearHistory).
>
> **R2-5 (index alignment):** On copy failure `CreateFavoriteSnapshot` must NOT omit the entry
> (no `.Where(f => f is not null)`). Omitting shifts indices and breaks the index-based parallel
> loop in `TryRepairFavoriteSnapshotLocked`, which assumes 1:1 alignment with `source.FilePaths`.
> Instead emit an index-aligned placeholder `ClipboardFilePath { Path = string.Empty,
> IsFolder = f.IsFolder, ExistsAtCapture = false }`. The placeholder still satisfies M6 (it never
> references the source managed path). EVERY consumer must tolerate an empty path:
> `TryRepairFavoriteSnapshotLocked` (treats empty as missing → repairs by index from source),
> `DeleteFavoriteFiles` (skips empty), the `favorites/files/` reconciliation
> (`DeleteUnreferencedFavoriteFileTrees`, Task 9 — empty paths contribute no referenced subtree,
> which is correct), `HasUsableContent` (a Files favorite with only empty paths → no usable
> content, because `ExistingFilePaths` filters by `File.Exists`/`Directory.Exists`, and `""`
> exists as neither). The copy-failure test guards `Path.GetFullPath` with
> `!string.IsNullOrWhiteSpace`.
>
> **m3:** `TryRepairFavoriteSnapshotLocked` is extended to restore favorite `FilePaths` from the
> source item's managed content when the favorite copy is missing OR an empty placeholder (chosen
> over scoping repair out).

- [ ] **Step 1: Write the failing tests (copy success, copy-failure omission, local metadata, repair)**

Add to `ClipboardFavoritesTests.cs` (follow its existing fixture pattern):

```csharp
    [Fact]
    public async Task Favorite_OfManagedFileDrop_CopiesIntoFavoriteFilesDirectory()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "imported content");

        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt", PlainText = src,
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("imp");

        var fav = Assert.Single(store.Favorites);
        var favPath = Assert.Single(fav.Item.FilePaths).Path;
        Assert.StartsWith(store.FavoriteFilesDirectory, Path.GetFullPath(favPath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(favPath));
        Assert.Equal("imported content", await File.ReadAllTextAsync(favPath));
    }

    [Fact]
    public async Task Favorite_OfLocalFileDrop_KeepsUserRealPathAsMetadata()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var userDir = Path.Combine(AppContext.BaseDirectory, "user-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        var userFile = Path.Combine(userDir, "real.txt");
        await File.WriteAllTextAsync(userFile, "real");

        var item = new ClipboardItem
        {
            Id = "loc", Type = ClipboardPayloadType.Files, Hash = "loc-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "real.txt", PlainText = userFile,
            FilePaths = [new ClipboardFilePath { Path = userFile, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("loc");

        var fav = Assert.Single(store.Favorites);
        Assert.Equal(userFile, Assert.Single(fav.Item.FilePaths).Path); // unchanged metadata
        try { Directory.Delete(userDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Favorite_OfManagedFileDrop_OnCopyFailure_DoesNotReferenceSourceManagedPath()
    {
        // M6: simulate a copy failure (source path under FilesDirectory but file removed before copy)
        // and assert the favorite does NOT end up pointing at the source managed path.
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "x");

        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt", PlainText = src,
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);

        // Delete the source so the copy fails.
        File.Delete(src);

        await store.AddToFavoritesAsync("imp");

        var fav = Assert.Single(store.Favorites);
        var favFilePaths = fav.Item.FilePaths;

        // R2-5: index alignment is preserved — the failed entry is a placeholder, not omitted.
        Assert.Single(favFilePaths);

        var fullSrc = Path.GetFullPath(src);
        foreach (var f in favFilePaths)
        {
            // R2-5: guard against the empty placeholder path before Path.GetFullPath.
            if (string.IsNullOrWhiteSpace(f.Path))
                continue; // placeholder — acceptable; it references nothing
            var full = Path.GetFullPath(f.Path);
            // M6: must NOT reference the source managed path.
            Assert.NotEqual(fullSrc, full, StringComparer.OrdinalIgnoreCase);
            // Any non-empty path must live under FavoriteFilesDirectory (never the source files/ path).
            Assert.StartsWith(store.FavoriteFilesDirectory, full, StringComparison.OrdinalIgnoreCase);
        }

        // R2-5: an all-empty (all-failed) Files favorite has no usable content.
        Assert.False(fixture.Actions.HasUsableContent(fav.Item.ToClipboardItem()),
            "a Files favorite whose only entry is an empty placeholder must report no usable content");
    }

    [Fact]
    public async Task Favorite_OfMixedManagedDrop_PreservesIndexAlignment_WhenOneCopyFails()
    {
        // R2-5: a Files favorite with a mix of empty (failed) + real (copied) paths stays
        // 1:1 with source.FilePaths and HasUsableContent is true (the real path exists).
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var good = Path.Combine(sub, "good.txt");
        var bad = Path.Combine(sub, "bad.txt");
        await File.WriteAllTextAsync(good, "good");
        await File.WriteAllTextAsync(bad, "bad");

        var item = new ClipboardItem
        {
            Id = "mix", Type = ClipboardPayloadType.Files, Hash = "mix-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "mix", PlainText = good,
            FilePaths =
            [
                new ClipboardFilePath { Path = good, ExistsAtCapture = true },
                new ClipboardFilePath { Path = bad, ExistsAtCapture = true }
            ]
        };
        await store.IngestAsync(item);

        // Make only the second copy fail.
        File.Delete(bad);

        await store.AddToFavoritesAsync("mix");

        var fav = Assert.Single(store.Favorites);
        Assert.Equal(2, fav.Item.FilePaths.Count); // R2-5: index-aligned 1:1 with source (2 entries)
        Assert.Contains(fav.Item.FilePaths, f => !string.IsNullOrWhiteSpace(f.Path)
            && Path.GetFullPath(f.Path).StartsWith(Path.GetFullPath(store.FavoriteFilesDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fav.Item.FilePaths, f => string.IsNullOrWhiteSpace(f.Path)); // the failed entry is an empty placeholder
        Assert.True(fixture.Actions.HasUsableContent(fav.Item.ToClipboardItem()),
            "a Files favorite with at least one real copied path still has usable content");
    }

    [Fact]
    public async Task Favorite_OfManagedFileDrop_DataObjectText_DoesNotLeakSourceManagedPath()
    {
        // R3-1: a Files favorite's PlainText (emitted as clipboard text by CreateDataObject) must
        // reflect the rewritten favorites/files/ path, NOT the source files/ path (which dies on
        // prune/ClearHistory). For a Files item, source.PlainText is the newline-joined source paths.
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "imported content");

        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt",
            PlainText = src, // for a Files item, PlainText is the (source) file path(s)
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("imp");

        var fav = Assert.Single(store.Favorites);
        var favItem = fav.Item.ToClipboardItem();

        // The favorite copy lives under FavoriteFilesDirectory.
        var favPath = Assert.Single(favItem.FilePaths).Path;
        Assert.StartsWith(store.FavoriteFilesDirectory, Path.GetFullPath(favPath), StringComparison.OrdinalIgnoreCase);

        // The data object's text formats must NOT contain the source FilesDirectory path...
        var data = fixture.Actions.CreateDataObject(favItem);
        var text = data.GetDataPresent(System.Windows.DataFormats.UnicodeText)
            ? (string?)data.GetData(System.Windows.DataFormats.UnicodeText)
            : null;
        Assert.NotNull(text);
        Assert.DoesNotContain(Path.GetFullPath(store.FilesDirectory), Path.GetFullPath(text!), StringComparison.OrdinalIgnoreCase);
        // ...and DO reflect the rewritten favorites/files/ path.
        Assert.Contains(Path.GetFullPath(favPath), Path.GetFullPath(text!), StringComparison.OrdinalIgnoreCase);
    }
```

(Adjust the exact `DataFormats`/getter to match how `SetPlainTextFormats` registers text in this codebase; the assertion's intent — favorite data-object text excludes the source `FilesDirectory` path and includes the `favorites/files/` path — is what matters.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~Favorite_OfManagedFileDrop_CopiesIntoFavoriteFilesDirectory|FullyQualifiedName~Favorite_OfLocalFileDrop_KeepsUserRealPathAsMetadata|FullyQualifiedName~Favorite_OfManagedFileDrop_OnCopyFailure_DoesNotReferenceSourceManagedPath|FullyQualifiedName~Favorite_OfMixedManagedDrop_PreservesIndexAlignment_WhenOneCopyFails|FullyQualifiedName~Favorite_OfManagedFileDrop_DataObjectText_DoesNotLeakSourceManagedPath"`
Expected: FAIL — managed file-drop favorites still point into `FilesDirectory` (not copied; on failure either fall back to source path or shift indices); and the favorite's `PlainText` still carries the source `files/` path so its data-object text leaks `FilesDirectory` (R3-1).

- [ ] **Step 3: Extend `CreateFavoriteSnapshot` to copy managed file-drop content (placeholder on failure — M6; rewrite `PlainText` for Files favorites — R3-1)**

In `CreateFavoriteSnapshot` (519-551), the rewritten favorite `FilePaths` must be built FIRST, into a local variable, BEFORE the `ClipboardFavoriteItem` object initializer — because for a `Files` favorite the snapshot's `PlainText` must be derived from the rewritten `favorites/files/` paths, not copied from `source.PlainText` (R3-1). For a `Files` item, `source.PlainText` is the newline-joined SOURCE `files/` paths; copying it verbatim would make `CreateDataObject` emit the dead source managed path as clipboard text formats after the source is pruned / `ClearHistory`'d, violating §6.5 independence.

Build the rewritten list first. Paths under `FilesDirectory` are copied into `FavoriteFilesDirectory` and rewritten; user-real paths are kept as metadata; managed-copy FAILURES yield an index-aligned PLACEHOLDER (never the source managed path):

```csharp
        // Build the rewritten favorite FilePaths FIRST (R3-1: PlainText for Files favorites is
        // derived from these rewritten paths, so they must exist before the initializer).
        var favoriteFilePaths = source.FilePaths
            .Select(f =>
            {
                var copied = CopyManagedFileDropForFavorite(f.Path, f.IsFolder, out var isManaged);
                if (isManaged && copied is null)
                {
                    // R2-5/M6: managed copy failed. Do NOT omit (omitting shifts indices and
                    // breaks the index-based parallel loop in TryRepairFavoriteSnapshotLocked).
                    // Emit an index-aligned placeholder that never references the source managed
                    // path; repair (m3) can later fill it from source.FilePaths[idx].
                    return new ClipboardFilePath
                    {
                        Path = string.Empty,
                        IsFolder = f.IsFolder,
                        ExistsAtCapture = false
                    };
                }
                return new ClipboardFilePath
                {
                    Path = copied ?? f.Path, // non-managed (user-real) path: metadata only
                    IsFolder = f.IsFolder,
                    ExistsAtCapture = f.ExistsAtCapture
                };
            })
            .ToList(); // 1:1 with source.FilePaths — no .Where filter, indices preserved (R2-5)

        // R3-1: for a Files favorite, rebuild PlainText from the rewritten NON-EMPTY favorite
        // paths so CreateDataObject never emits the source files/ path as clipboard text. When
        // every entry is an empty placeholder (all copies failed), clear PlainText. For non-Files
        // favorites, keep source.PlainText unchanged (it is the actual text payload, not a path).
        var favoritePlainText = source.Type == ClipboardPayloadType.Files
            ? string.Join(
                  Environment.NewLine,
                  favoriteFilePaths.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)))
            : source.PlainText;
```

Then reference `favoriteFilePaths` and `favoritePlainText` in the object initializer (replacing both the `PlainText = source.PlainText` line and the inline `FilePaths = source.FilePaths.Select(...)` block):

```csharp
            PlainText = favoritePlainText,
            // ...
            FilePaths = favoriteFilePaths,
```

The favorite's `FilePaths` is now index-aligned 1:1 with `source.FilePaths`; every consumer below is made tolerant of an empty `Path`. The favorite's `PlainText` for a Files favorite reflects only the rewritten `favorites/files/` paths (empty when all placeholders are empty), composing cleanly with the placeholder handling: all-empty → empty/null `PlainText`; mixed → only the non-empty copied paths joined.

Add the helper (returns `null` on managed-copy failure; `isManaged` tells the caller whether the source was under `FilesDirectory`):

```csharp
    private string? CopyManagedFileDropForFavorite(string sourcePath, bool isFolder, out bool isManaged)
    {
        isManaged = false;
        if (string.IsNullOrWhiteSpace(sourcePath))
            return sourcePath;

        var full = Path.GetFullPath(sourcePath);
        var filesRoot = Path.GetFullPath(FilesDirectory) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(filesRoot, StringComparison.OrdinalIgnoreCase))
            return sourcePath; // user-real path: metadata only, never copied

        isManaged = true;
        Directory.CreateDirectory(FavoriteFilesDirectory);
        var destSub = Path.Combine(FavoriteFilesDirectory, Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(destSub);
            if (isFolder && Directory.Exists(full))
            {
                var dest = Path.Combine(destSub, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)));
                CopyDirectoryRecursive(full, dest);
                return dest;
            }

            if (File.Exists(full))
            {
                var dest = Path.Combine(destSub, Path.GetFileName(full));
                File.Copy(full, dest, overwrite: true);
                return dest;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: failed to copy managed file drop into favorite");
        }

        // Managed copy failed (source missing or copy threw): clean the empty dest and signal failure.
        try { if (Directory.Exists(destSub)) Directory.Delete(destSub, recursive: true); } catch { }
        return null; // M6: never return the source managed path
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
    }
```

- [ ] **Step 4: Extend `DeleteFavoriteFiles` to clean favorite file-drop subtrees**

`DeleteFavoriteFiles` (599-607) currently deletes blob/image/thumb files. Make it instance and add deletion of any `FilePaths` under `FavoriteFilesDirectory` (delete the per-favorite GUID subtree, never the user's real path):

```csharp
    private void DeleteFavoriteFiles(ClipboardFavoriteItem item)
    {
        foreach (var path in new[] { item.BlobPath, item.HtmlBlobPath, item.RtfBlobPath, item.ImagePath, item.ThumbnailPath }
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteFile(path!);
        }

        var favRoot = Path.GetFullPath(FavoriteFilesDirectory) + Path.DirectorySeparatorChar;
        foreach (var fp in item.FilePaths)
        {
            if (string.IsNullOrWhiteSpace(fp.Path))
                continue;
            var full = Path.GetFullPath(fp.Path);
            if (!full.StartsWith(favRoot, StringComparison.OrdinalIgnoreCase))
                continue; // user-real path: never delete
            var rel = full.Substring(favRoot.Length);
            var firstSeg = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstSeg))
                continue;
            var subtree = Path.Combine(FavoriteFilesDirectory, firstSeg);
            try { if (Directory.Exists(subtree)) Directory.Delete(subtree, recursive: true); }
            catch (Exception ex) { Log.Debug(ex, "ClipboardStoreService: favorite file subtree delete failed {Dir}", LogPrivacy.Format(subtree)); }
        }
    }
```

Drop `static` from the declaration; call sites (`RemoveFavoriteAsync` line 430, `DeleteFavoriteSnapshotLocked` line 640) need no signature change. `PruneMissingContentLocked` goes through `DeleteFavoriteSnapshotLocked`, so it is covered.

- [ ] **Step 5: Extend `TryRepairFavoriteSnapshotLocked` to restore favorite `FilePaths` (m3)**

In `TryRepairFavoriteSnapshotLocked` (553-573), after the existing blob/image/thumb restores, add file-drop restoration: when a favorite's managed `FilePaths` entry is missing (file gone) OR is an empty placeholder (R2-5 copy-failure placeholder) AND the source item still has managed content, re-copy it via `CopyManagedFileDropForFavorite` by index. User-real paths are left as-is. The index-based loop is safe ONLY because `CreateFavoriteSnapshot` now preserves 1:1 alignment (R2-5).

```csharp
        // m3 + R2-5: restore managed file-drop copies from the source item by index when the
        // favorite copy is missing OR is an empty placeholder (copy-failure). Index alignment with
        // source.FilePaths is guaranteed by CreateFavoriteSnapshot (no omission on failure).
        if (favorite.FilePaths.Count > 0)
        {
            var favRoot = Path.GetFullPath(FavoriteFilesDirectory) + Path.DirectorySeparatorChar;
            for (var idx = 0; idx < favorite.FilePaths.Count && idx < source.FilePaths.Count; idx++)
            {
                var favFp = favorite.FilePaths[idx];
                var full = string.IsNullOrWhiteSpace(favFp.Path) ? null : Path.GetFullPath(favFp.Path);
                var isManagedFav = full is not null && full.StartsWith(favRoot, StringComparison.OrdinalIgnoreCase);
                var missing = full is null || (isManagedFav && !File.Exists(full) && !Directory.Exists(full));
                if (!missing)
                    continue;

                var srcFp = source.FilePaths[idx];
                var restored = CopyManagedFileDropForFavorite(srcFp.Path, srcFp.IsFolder, out var srcIsManaged);
                if (srcIsManaged && restored is not null)
                {
                    favFp.Path = restored;
                    changed = true;
                }
            }
        }
```

(`changed` is the existing `ref bool` already used by the blob/image restores; the `if (changed) _repo.UpsertFavorite(favorite);` at line 569-570 persists the restored paths.)

- [ ] **Step 6: Run favorites tests + full store/favorites regression**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardFavoritesTests|FullyQualifiedName~ClipboardStoreServiceTests"`
Expected: PASS (new favorite-copy + copy-failure-omission + local-metadata tests + all existing favorites/store tests).

- [ ] **Step 7: Build + publish**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```
Expected: build SUCCEEDED; publish SUCCEEDED.

- [ ] **Step 8: Commit**

```
git add src/Recents.App/Services/Clipboard/ClipboardStoreService.cs tests/Recents.App.Tests/Clipboard/ClipboardFavoritesTests.cs
git commit -m "feat: copy managed file-drop content into favorites/files snapshots (omit on copy failure; repair from source)"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardFavoritesTests|FullyQualifiedName~ClipboardStoreServiceTests"
```
**spec-check:** Section 6.5 (favorites independent file-drop copies; user-real paths metadata only; copy-failure never shares source; repair restores from source; **R3-1:** Files-favorite `PlainText` rebuilt from rewritten `favorites/files/` paths so the data object never emits the source `files/` path as clipboard text) + Section 8 (favorite of imported file drop) + R2-5 (copy-failure yields an index-aligned empty placeholder; repair/delete/`HasUsableContent` tolerate empty paths; index alignment 1:1 with source).

---

## Task 9: Reconcile `favorites/files/` in `CompactOrphanBlobsAsync`

**complexity:** low
**critical:** true  _(irreversible deletion within `FavoriteFilesDirectory`)_

**Files:**
- Modify: `src/Recents.App/Services/Clipboard/ClipboardStoreService.cs` — `CompactOrphanBlobsAsync` (favorites block).
- Test: `tests/Recents.App.Tests/Clipboard/ClipboardFavoritesTests.cs`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Compact_KeepsLiveFavoriteFileSubtree_DeletesOrphanFavoriteSubtree()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "x");
        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt", PlainText = src,
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("imp");
        var liveFavPath = Assert.Single(Assert.Single(store.Favorites).Item.FilePaths).Path;

        // Orphan favorite subtree (no favorite references it).
        var orphan = Path.Combine(store.FavoriteFilesDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "junk.txt"), "junk");

        await store.CompactOrphanBlobsAsync();

        Assert.True(File.Exists(liveFavPath), "live favorite content must be kept");
        Assert.False(Directory.Exists(orphan), "orphan favorite subtree must be deleted");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~Compact_KeepsLiveFavoriteFileSubtree_DeletesOrphanFavoriteSubtree"`
Expected: FAIL — orphan favorite subtree not deleted.

- [ ] **Step 3: Add `favorites/files/` reconciliation**

In `CompactOrphanBlobsAsync`, after the existing favorite reconciliation, add a subtree pass keyed on the favorites' managed `FilePaths`:

```csharp
                DeleteUnreferencedFavoriteFileTrees(favoritePaths.SelectMany(v => v.FilePaths.Select(f => f.Path)));
```

Add the helper (mirror `DeleteUnreferencedFileTrees`, but NO grace window — favorite copies are durable; only orphaned subtrees go; rooted at `FavoriteFilesDirectory`). R2-5: empty placeholder paths in `referencedPaths` are filtered by the `!string.IsNullOrWhiteSpace(p)` guard, so a failed-copy placeholder simply contributes no referenced subtree (correct — there is nothing to keep):

```csharp
    private void DeleteUnreferencedFavoriteFileTrees(IEnumerable<string?> referencedPaths)
    {
        try
        {
            Directory.CreateDirectory(FavoriteFilesDirectory);
            var favRoot = Path.GetFullPath(FavoriteFilesDirectory);
            var referenced = referencedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p!))
                .Where(p => p.StartsWith(favRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var subdir in Directory.EnumerateDirectories(FavoriteFilesDirectory))
            {
                var prefix = Path.GetFullPath(subdir) + Path.DirectorySeparatorChar;
                var isReferenced = referenced.Any(r => r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (isReferenced)
                    continue;
                try { Directory.Delete(subdir, recursive: true); }
                catch (Exception ex) { Log.Warning(ex, "ClipboardStoreService: favorite file tree delete failed {Dir}", LogPrivacy.Format(subdir)); }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: favorite files reconciliation failed");
        }
    }
```

- [ ] **Step 4: Run the test**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~Compact_KeepsLiveFavoriteFileSubtree_DeletesOrphanFavoriteSubtree"`
Expected: PASS.

- [ ] **Step 5: Build + publish + full store/favorites regression**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardFavoritesTests|FullyQualifiedName~ClipboardStoreServiceTests"
```
Expected: build SUCCEEDED; publish SUCCEEDED; all PASS.

- [ ] **Step 6: Commit**

```
git add src/Recents.App/Services/Clipboard/ClipboardStoreService.cs tests/Recents.App.Tests/Clipboard/ClipboardFavoritesTests.cs
git commit -m "feat: reconcile favorites/files subtrees in compaction"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardFavoritesTests|FullyQualifiedName~ClipboardStoreServiceTests"
```
**spec-check:** Section 6.5 (favorite reconciliation handles `FavoriteFilesDirectory` analogously).

---

## Task 10: `ClearHistory` wipes `FilesDirectory`

**complexity:** low
**critical:** true  _(irreversible content deletion — must wipe imported file content but not favorites)_

**Files:**
- Modify: `src/Recents.App/Services/Clipboard/ClipboardStoreService.cs` — `ClearHistoryAsync` (470-485).
- Test: `tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task ClearHistory_WipesManagedFiles_KeepsFavoriteFiles()
    {
        using var fixture = ClipboardStoreFixture.Create();
        var store = fixture.Store;

        var sub = Path.Combine(store.FilesDirectory, "imported");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "doc.txt");
        await File.WriteAllTextAsync(src, "x");
        var item = new ClipboardItem
        {
            Id = "imp", Type = ClipboardPayloadType.Files, Hash = "imp-hash",
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow,
            PreviewText = "doc.txt", PlainText = src,
            FilePaths = [new ClipboardFilePath { Path = src, ExistsAtCapture = true }]
        };
        await store.IngestAsync(item);
        await store.AddToFavoritesAsync("imp");
        var favPath = Assert.Single(Assert.Single(store.Favorites).Item.FilePaths).Path;

        await store.ClearHistoryAsync();

        Assert.False(File.Exists(src), "managed files/ content must be wiped");
        Assert.True(File.Exists(favPath), "favorite file copy must survive ClearHistory");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClearHistory_WipesManagedFiles_KeepsFavoriteFiles"`
Expected: FAIL — `files/` content survives ClearHistory.

- [ ] **Step 3: Add `FilesDirectory` to the wipe**

In `ClearHistoryAsync` (470-485), after `TryDeleteDirectoryContents(ThumbnailDirectory);` add a subtree-aware wipe of `FilesDirectory` (`TryDeleteDirectoryContents` only enumerates files, so it cannot clear the `files/` subdirectories):

```csharp
                TryDeleteDirectoryContents(BlobDirectory);
                TryDeleteDirectoryContents(ImageDirectory);
                TryDeleteDirectoryContents(ThumbnailDirectory);
                TryDeleteDirectoryTrees(FilesDirectory);
```

Add the helper:

```csharp
    private static void TryDeleteDirectoryTrees(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
                TryDeleteFile(file);
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                try { Directory.Delete(sub, recursive: true); }
                catch (Exception ex) { Log.Warning(ex, "ClipboardStoreService: failed to clear tree {Dir}", LogPrivacy.Format(sub)); }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardStoreService: failed to clear {Directory}", LogPrivacy.Format(directory));
        }
    }
```

- [ ] **Step 4: Run the test**

Run: `dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClearHistory_WipesManagedFiles_KeepsFavoriteFiles"`
Expected: PASS.

- [ ] **Step 5: Build + publish + full store regression**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests"
```
Expected: build SUCCEEDED; publish SUCCEEDED; all PASS.

- [ ] **Step 6: Commit**

```
git add src/Recents.App/Services/Clipboard/ClipboardStoreService.cs tests/Recents.App.Tests/Clipboard/ClipboardStoreServiceTests.cs
git commit -m "feat: ClearHistory wipes managed files/ content"
```

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests --filter "FullyQualifiedName~ClipboardStoreServiceTests"
```
**spec-check:** Section 6.6 (`ClearHistory` adds `FilesDirectory` wipe; favorites unaffected) + Acceptance criterion 5.

---

## Task 11: Whole-repo zero-reference sweep (no lingering `incoming` / old constructor)

**complexity:** low

_(Not critical: read-only verification sweep — no production behavior change; any drift is caught here without storage risk.)_

**Files:** none (verification only).

- [ ] **Step 1: Sweep for any lingering `incoming` references**

Run: `git --no-pager grep -ni "incoming" -- src/ tests/`
Expected: the ONLY matches are the legacy-deletion helper `DeleteLegacyIncomingDirectory` and its `Path.Combine(syncRoot, "incoming")` literal in `ClipboardWebDavSyncService.cs`, the `App.xaml.cs` call to it, plus its test in `ClipboardWebDavStagingTests.cs`. NO `_incomingDirectory`, NO `incomingDirectory` ctor param, NO import-target `incoming` usage.

- [ ] **Step 2: Sweep for the old two-arg payload constructor**

Run: `git --no-pager grep -n "new ClipboardSyncPayloadService(" -- src/ tests/`
Expected: every call passes `(outgoingDirectory, <IClipboardManagedStorage>)` — no call passes a second string path argument.

- [ ] **Step 3: Confirm no duplicate thumbnail / dead helpers**

Run: `git --no-pager grep -n "WriteJpegThumbnail\|ImportIncomingImageAsync\|ImportManagedImageAsync" -- src/`
Expected: `WriteJpegThumbnail` only in `ClipboardThumbnailWriter` + its call sites; `ImportIncomingImageAsync` GONE; `ImportManagedImageAsync` present in the payload service.

- [ ] **Step 4: Build + publish + full suite green**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests
```
Expected: build SUCCEEDED; publish SUCCEEDED; ALL tests PASS.

- [ ] **Step 5: Commit (if any cleanup edits were needed)**

```
git add -A
git commit -m "chore: zero-reference sweep for incoming hard cut"
```
(If nothing changed, skip the commit.)

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests
```
plus the three `git --no-pager grep` checks above.
**spec-check:** Section 6.7 / Section 5 (hard cut — `incoming/` eliminated; no compat layer) + Acceptance criterion 2 & 6.

---

## Task 12: Full-suite verification + spec acceptance walk-through

**complexity:** low

_(Not critical: read-only verification walk-through.)_

**Files:** none (verification only).

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test tests/Recents.App.Tests`
Expected: ALL tests PASS (no skips beyond the env-gated HEIC fixture tests, which return early by design).

- [ ] **Step 2: Walk each spec acceptance criterion against the implementation**

Confirm and note evidence for each:
1. Staging at-rest ≈0 — Task 6 delete-after-use (success+exception seam tests) + wipe-on-startup (`ClipboardWebDavStagingTests`).
2. `webdav/incoming/` gone — Task 6 `DeleteLegacyIncomingDirectory` (App-ordered before store load) + Task 11 sweep.
3. Imported images/files re-pasteable + thumbnails (standard + converted) — Task 5 (`ImportAsync_StandardImageLandsInManagedImagesWithThumbnail`, `ImportAsync_ConvertedImageAlsoWritesThumbnail`, group test).
4. `files/` tracks live history; 1-day grace on files AND blob/image/thumb; empty subdirs removed; user paths safe — Task 7 reconciliation/grace tests.
5. Favorites of imported drops survive prune + `ClearHistory`; copy-failure never shares source; repair restores from source — Tasks 8-10.
6. Full suite passes — Step 1.

- [ ] **Step 3: Confirm clean tree**

```
git status
```
Expected: clean tree (all work already committed).

**verify:** `dotnet test tests/Recents.App.Tests` (full suite).
**spec-check:** Section 10 (all six acceptance criteria) + Section 9 (testing strategy).

---

## Task 13 (CLOSING GATE — MANDATORY, TERMINAL): Whole-implementation cross-vendor review

**complexity:** high
**critical:** true  _(final gate — the entire plan diff is reviewed against the spec before the work is considered done)_

**Files:** none (review only; any fixes loop back through the relevant task's TDD cycle).

- [ ] **Step 1: Produce the full implementation diff**

Run: `git diff <plan-base>..HEAD` (use the SHA recorded at the top of this plan).
Confirm the diff spans exactly the files in the File Structure section and nothing else outside scope.

- [ ] **Step 2: Run cross-vendor adversarial review (`xreview`)**

Invoke the `aibridge:xreview` skill on `git diff <plan-base>..HEAD` with the spec (`docs/superpowers/specs/2026-06-21-clipboard-webdav-cache-cleanup-design.md`) as the acceptance contract. Default to BOTH vendors (GPT + Gemini) in parallel. The review must check, at minimum:
- Hard cut honored: no `incoming/` creation anywhere; legacy deleted on startup BEFORE store load; no compat layer.
- Single cleanup authority: `files/` reconciled only by `CompactOrphanBlobsAsync`, never eager per-item; grace window = 1 day, reused for files AND blob/image/thumb mtime grace.
- Containment safety: reconciliation/favorite-delete never touches paths outside `FilesDirectory` / `FavoriteFilesDirectory` (user-real paths safe).
- Single source of truth: one thumbnail implementation; managed-storage interface is the only import target; thumbnails always produced for decodable images.
- Favorites: managed copy never falls back to the source managed path on failure; repair restores from source.
- Non-goals respected: no size/TTL janitor; `blobs/`/`images/`/`thumbs/` layout + local retention unchanged; no new user settings.
- Staging delete-after-use placed in `finally` (survives retries/exceptions), exercised by the fakeable-transport seam tests.

- [ ] **Step 3: Arbitrate findings into a verdict**

Collect each vendor's raw findings (separate evidence files per the xreview skill), arbitrate into a single verdict. For every actionable finding, fix it by re-entering the owning task's failing-test → implement → green cycle, then re-commit.

- [ ] **Step 4: Loop until green**

Re-run `dotnet test tests/Recents.App.Tests` (full suite) after fixes, then re-run `xreview` on the updated diff. Repeat Steps 1-4 until the cross-vendor review returns no actionable findings AND the full suite is green.

- [ ] **Step 5: Final confirmation**

Run:
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests
```
Expected: build SUCCEEDED; publish SUCCEEDED; ALL tests PASS; xreview clean. Only then is the implementation complete.

**verify:**
```
dotnet build Recents.sln --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
dotnet test tests/Recents.App.Tests
```
plus `aibridge:xreview` returns no actionable findings on `git diff <plan-base>..HEAD`.
**spec-check:** Entire spec (Sections 5-10) — terminal acceptance gate.

---

## Self-Review

**Spec coverage:**
- 6.1 staging lifecycle → Task 6. 6.2 eliminate `incoming/` + constructor + production wiring → Tasks 4-5 (B1: ctor + sole call site in one green commit). 6.3 shared thumbnail → Task 1. 6.4 `files/` reconciliation (+grace, +empty-subdir m1) → Tasks 3 + 7. M2 grace on blob/image/thumb → Task 7. 6.5 favorites file copies (+copy-failure omission M6, +repair m3) + reconciliation → Tasks 8-9. 6.6 `ClearHistory` → Task 10. 6.7 migration hard cut (+App-ordered before store load M1) → Tasks 6 + 11. Non-goals (no janitor, no new settings, layout unchanged) preserved — no task adds a size/TTL budget or settings; only `files/`+`favorites/files/` dirs are introduced.

**Critical flags (m2):** critical on Tasks 2, 3, 4, 5, 6, 7, 8, 9, 10, 13. Non-critical: Task 1 (mechanical refactor), Task 11 (read-only sweep), Task 12 (read-only verification). The former standalone DI-wiring task was merged into Task 4 (B1).

**Build conventions (M7):** every `verify` block and inline `Run:` build/publish command uses `dotnet build Recents.sln --no-restore` followed by `dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish`; no bash `&&`; PowerShell-5.1 line-separated; `git grep` uses `git --no-pager grep`; `dotnet test ... --filter` calls retained; no `dotnet restore` (no package/proj/sln changes).

**Placeholder scan:** No "TBD"/"handle edge cases"/"write tests for the above" — every code step shows code; every test step shows the test (`StubSync`/`WriteConvertibleImagePayload` are the only helpers flagged as wiring to existing real types during implementation; `StubExport` constructs the real `ClipboardSyncExport` record directly).

**Round-3/4 fixes (R2-1..R2-5, R3-1):**
- **R2-1:** Outgoing staged-payload delete moved into `RunUploadFlowAsync`'s `try/finally` covering export→duplicate-check→upload; the duplicate short-circuit, upload success, and exhausted-retry throw all delete `export.PayloadPath`. New test `UploadFlow_DeletesStagedOutgoingPayload_OnDuplicateShortCircuit`. The transport seam no longer deletes the outgoing payload.
- **R2-2:** All seam/test snippets use `ClipboardSyncExport` (the real record at `ClipboardSyncPayloadService.cs:11`); no `ClipboardSyncPayloadExport` remains. `StubExport` constructs the real positional record.
- **R2-3:** Task 5 adds `ImportedImage_NotIngested_AppliesViaDataObject_AndSurvivesCompactionGrace` and `ImportedGroup_NotIngested_AppliesViaFileDrop_AndSurvivesCompactionGrace` — concrete bodies that run `ClipboardActionService.CreateDataObject(item)` over imported content NOT passed to `IngestAsync`, assert usable content (bitmap/file-drop), and assert `CompactOrphanBlobsAsync` keeps the unreferenced content within grace.
- **R2-4:** Task 5 renames `CopyIncomingPayloadAsync`→`CopyManagedPayloadAsync` and `CopyIncomingAsync`→`CopyManagedAsync` (+ all call sites) as an explicit step BEFORE the `git --no-pager grep -ni "incoming"` zero-reference check, which now passes.
- **R2-5:** `CreateFavoriteSnapshot` returns an index-aligned empty-`Path` placeholder on managed-copy failure (no `.Where` filter); `TryRepairFavoriteSnapshotLocked` (by index), `DeleteFavoriteFiles`, `DeleteUnreferencedFavoriteFileTrees`, and the copy-failure test all tolerate empty paths; `HasUsableContent` for an all-empty Files favorite is false (covered by tests). New test `Favorite_OfMixedManagedDrop_PreservesIndexAlignment_WhenOneCopyFails`.
- **R3-1:** Task 8 Step 3 builds the rewritten favorite `FilePaths` into a local FIRST, then derives the snapshot's `PlainText`: for a `Files` favorite, `string.Join(Environment.NewLine, favoriteFilePaths.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)))` (empty/null when all placeholders are empty); for non-Files favorites, `source.PlainText` is kept verbatim. This stops `CreateDataObject` (Files case `SetPlainTextFormats(data, item.PlainText)`) from emitting the dead source `files/` path as clipboard text after prune/`ClearHistory` (§6.5 independence). Composes with R2-5: all-empty → empty PlainText; mixed → only non-empty copied paths joined. New test `Favorite_OfManagedFileDrop_DataObjectText_DoesNotLeakSourceManagedPath`.

**Type consistency:** `FilesDirectory`, `FavoriteFilesDirectory`, `LoadRetainedManagedFilePaths`, `IClipboardManagedStorage`, `ClipboardThumbnailWriter.WriteJpegThumbnail`, `FilesGraceWindow`, `DeleteUnreferencedFiles(..., graceCutoffUtc)`, `DeleteUnreferencedFileTrees`, `DeleteUnreferencedFavoriteFileTrees`, `WipeStagingDirectory`, `DeleteLegacyIncomingDirectory`, `CopyManagedFileDropForFavorite(..., out isManaged)`, `CopyManagedPayloadAsync`, `CopyManagedAsync`, `ImportManagedImageAsync`, `IWebDavPayloadTransport`, `RunUploadFlowAsync`, `DownloadImportThenDeleteAsync`, `ClipboardSyncExport` are used identically wherever referenced.
