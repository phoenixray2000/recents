# Clipboard WebDAV Cache Cleanup — Design Spec

- Date: 2026-06-21
- Branch: `codex-clipboard-webdav-sync`
- Status: Approved for planning
- Scope owner: phoenixray2000

## 1. Problem

The clipboard WebDAV sync cache grows without bound. Under
`%LocalAppData%\Recents\clipboard\webdav\` three directories accumulate forever:

- `outgoing/` — a copy of every file/image/zip staged for upload. Never deleted locally.
- `downloads/` — every raw payload pulled from the server before import. Never deleted.
- `incoming/<hash>/` — the actual content of imported remote items. Only partially
  cleaned (image files deleted when their history row is pruned; empty `<hash>/`
  dirs left behind; **file/group drop content never deleted** because the store
  treats `FilePaths` as external user files).

The local clipboard history store (`blobs/`, `images/`, `thumbs/`, `favorites/…`)
is already bounded by `MaxClipboardItems` (500) + `ClipboardRetentionDays` (30) +
the hourly `CompactOrphanBlobsAsync`. The unbounded growth is confined to `webdav/`.

## 2. Root cause

`incoming/` carries **two conflicting responsibilities**:

1. A transient landing area for "the latest imported item" being applied to the OS clipboard.
2. The permanent backing store for history rows (imported items keep `ImagePath` /
   `FilePaths` pointing into `incoming/`).

Because of (2), `incoming/` cannot simply keep "only the latest" — deleting old
entries would dangle live history rows. And because nothing owns the cleanup of
(1)'s leftovers or the staging dirs, the cache leaks. The fix is to **split these
two responsibilities**: imported content that belongs to history must live in the
store's managed directories (one cleanup authority), leaving `incoming/`
unnecessary entirely.

## 3. Goals / Non-goals

### Goals
- The `webdav/` cache size becomes a bounded consequence of the single existing
  retention policy (`MaxClipboardItems` + `ClipboardRetentionDays`), not a separate
  budget.
- Staging directories (`outgoing/`, `downloads/`) never accumulate.
- Imported remote content (images, files, folders) is cleaned by the **same**
  reconciliation authority that already cleans local history content.
- No regression: remote file/folder items remain re-pasteable from history.
- Hard cut — no compatibility layer for the old `incoming/` layout.

### Non-goals
- Do **not** change the existing local-history retention policy or the
  `blobs/`/`images/`/`thumbs/` layout (already bounded).
- Do **not** introduce a size-budget / TTL "janitor" with its own knobs (rejected:
  it duplicates retention as a second source of truth and can evict referenced
  content).
- Do **not** restructure favorites' existing independent-copy model.
- No new user-facing settings.

## 4. Upstream reference (SyncClipboard) — validation

SyncClipboard (Jeric-X/SyncClipboard) is the protocol's upstream. Its
`HistoryManager` + three Quartz jobs confirm this design:

- Binary content is stored in a **per-record working dir** under
  `…/file/history/<hash>/`; the current clipboard's transient file lives in
  `…/file/` (temp). There is **no `incoming` that doubles as history storage** —
  downloaded content is `Persist()`-ed straight into the per-record dir. This
  validates eliminating `incoming/`'s double duty.
- `CleanupExpiredHistory`: count (`MaxItemCount`) + age (`HistoryRetentionMinutes`)
  pruning, **exempting `Stared`/`Pinned`** records. (We already exempt
  `is_favorite` in `SoftDeleteOverflowAndExpired`.)
- `CleanupOrphanedHistoryFolders`: scans the history folder, keeps dirs whose name
  is a live DB hash, deletes the rest **older than a 7-day creation-time grace**.
  This is exactly our `files/` orphan reconciliation + grace window.
- `ClearDeletedHistoryData`: compacts soft-deleted rows + their data (we already do
  this via the soft-delete queue + `CompactDeletedItems`).

We keep our single hourly `CompactOrphanBlobsAsync` (no Quartz) and our
reconciliation-based model, borrowing upstream's orphan-by-reference + grace idea.
We diverge on one detail: our imported file items get
`Hash = ClipboardHash.ForFiles(localPaths)`, which does **not** equal the import
subdir name, so our `files/` reconciliation is **path-prefix based**, not
hash-name based.

## 5. Directory layout (after)

Under `%LocalAppData%\Recents\clipboard\`:

**Managed durable storage (single cleanup authority = `CompactOrphanBlobsAsync`):**
- `blobs/` — text/html/rtf (local + imported text). Unchanged.
- `images/` — images (local + **imported**). Imported images now land here.
- `thumbs/` — thumbnails (local + **imported**).
- `files/<subdir>/` — **NEW** — imported file/folder content. Local file drops keep
  referencing the user's real paths (unchanged; never copied).
- `favorites/{blobs,images,thumbs,files}/` — independent favorite snapshots;
  `favorites/files/` is **NEW**.

**Transient staging (owned by `ClipboardWebDavSyncService`, never DB-referenced):**
- `webdav/outgoing/` — upload staging; delete-after-use + wipe-on-startup.
- `webdav/downloads/` — download staging; delete-after-use + wipe-on-startup.

**Eliminated (hard cut):** `webdav/incoming/` — no longer created; legacy dir
deleted on startup.

## 6. Detailed design

### 6.1 Staging dirs — `outgoing/` + `downloads/`
Pure transfer staging, never referenced by the DB.

- **Delete-after-use:**
  - `outgoing/`: after `UploadCapturedAsync` completes (success or exhausted
    retries), delete the staged payload file (`export.PayloadPath`) in a `finally`.
    Retries reuse the same file within one call, so it is only disposable once the
    call returns.
  - `downloads/`: after `ImportAsync` consumes the raw payload in `PollOnceAsync`,
    delete the raw download in a `finally`.
- **Wipe-on-startup backstop:** when the sync service starts, empty both dirs to
  sweep anything a crash left mid-operation. Safe by definition — nothing here
  needs to survive a sync operation.

### 6.2 Eliminate `incoming/` — imported content → managed storage
`ClipboardSyncPayloadService.ImportAsync` writes imported content into the store's
**managed** directories instead of `incoming/`:

| Type | Before | After |
|---|---|---|
| Image | `incoming/<hash>/` | `images/` + generated thumbnail in `thumbs/` |
| Text | not persisted | unchanged (`PlainText` only) |
| File | `incoming/<hash>/` | `files/<subdir>/<name>` |
| Group | `incoming/<hash>/` (extract) | `files/<subdir>/…` (extract) |

- Imported images use the **same** naming (`ClipboardBlobNamer.Build` +
  `EnsureUnique`) and the **same** thumbnail generation as local capture (see 6.3),
  setting `ImagePath` / `ThumbnailPath` / `ImageWidth` / `ImageHeight`.
- Imported file/group content is written under a managed `files/` subdir; the
  resulting `ClipboardItem.FilePaths` point there. The subdir name is an
  implementation detail (e.g. profile hash or a fresh id) — reconciliation is
  path-prefix based and naming-agnostic.
- `ClipboardSyncPayloadService`'s constructor changes: it no longer takes an
  `incomingDirectory`; instead it receives the managed directories it must write to
  (`ImageDirectory`, `ThumbnailDirectory`, `FilesDirectory`) — pass the
  `ClipboardStoreService` (or a small interface exposing those paths + the
  thumbnail writer). `outgoingDirectory` (export staging) stays.
- The OS-clipboard apply path (`CreateDataObject`) already reads images from
  `ImagePath` and files from `FilePaths`; both now point into managed storage, so
  apply works whether or not the item is saved to history.

### 6.3 Shared thumbnail helper
Extract `WriteJpegThumbnail` (currently a private static in
`ClipboardCaptureService`) into a shared helper (e.g.
`ClipboardThumbnailWriter`) so local capture and import share **one** thumbnail
implementation (single source of truth). Import decodes the image bytes to a
`BitmapSource` and calls the same helper.

### 6.4 `files/` reconciliation (in `CompactOrphanBlobsAsync`)
Add `files/` cleanup to the existing hourly compaction, mirroring how
`images/`/`blobs/`/`thumbs/` are reconciled — **not** eager per-item deletion.

- **New `StoreService.FilesDirectory`** = `Path.Combine(DataDirectory, "files")`,
  created on init.
- **Reference set:** managed `FilePaths` (those under `FilesDirectory`) of retained
  items — items where `is_deleted = 0 OR deleted_utc >= cutoff` (same retention
  window as `LoadRetainedBlobPaths`). Add a repo method, e.g.
  `LoadRetainedManagedFilePaths(cutoff)` (join `clipboard_files` to retained
  `clipboard_items`).
- **Subtree reconciliation:** a new helper (e.g. `DeleteUnreferencedFileTrees`)
  enumerates top-level subdirs of `files/`; a subdir is **kept** iff some
  referenced path is under it **or** its last-write time is within the grace
  window; otherwise the whole subtree is deleted. Empty subdirs are removed.
- **Grace window:** **1 day** (constant in code). Protects content just imported
  but not yet referenced (e.g. `SaveRemoteItemsToHistory = false`, where the item
  backs the OS clipboard until the next clipboard change but is never ingested).
  Rationale for 1 day vs upstream's 7: ample for the "import then paste" window
  while not hoarding transient junk for a week. Easily tunable.
- **Containment safety:** reconciliation only ever deletes **within** `FilesDirectory`.
  Local file drops reference the user's real paths (outside `FilesDirectory`) and
  are never touched.

### 6.5 Favorites — independent file-drop copies
Favorites are independent snapshots that must survive `ClearHistory` (which wipes
the managed history dirs). Extend the existing copy pattern:

- `CreateFavoriteSnapshot` currently copies blob/image/thumb into
  `favorites/{blobs,images,thumbs}/` but copies `FilePaths` as metadata only.
  Extend it to **copy managed file-drop content** (paths under `FilesDirectory`)
  into a new `FavoriteFilesDirectory` (`favorites/files/`), rewriting the favorite's
  `FilePaths` to point there. Local (user-real-path) file drops are copied as
  before (metadata only — they reference the user's own files).
- `DeleteFavoriteFiles` / `TryRepairFavoriteSnapshotLocked` / the favorite
  reconciliation pass handle `FavoriteFilesDirectory` analogously to the other
  favorite dirs (`CompactOrphanBlobsAsync` already reconciles
  `FavoriteBlobDirectory` / `FavoriteImageDirectory` / `FavoriteThumbnailDirectory`).
- Result: no shared references between `files/` and `favorites/files/`; per-source
  reconciliation stays safe.

### 6.6 `ClearHistory` update
`ClearHistoryAsync` currently wipes `BlobDirectory` / `ImageDirectory` /
`ThumbnailDirectory`. Add `FilesDirectory` to the wipe so "clear history" also
clears imported file content. Favorites (independent copies) are unaffected.

### 6.7 Migration (hard cut)
- On startup, delete the legacy `webdav/incoming/` directory entirely (owned by the
  sync service, which owns `webdav/`).
- History rows still pointing into `incoming/` become dangling; the existing
  `HasUsableContent` check prunes them on load (`LoadFromDatabaseSnapshot`) and via
  `PruneMissingContentLocked` in compaction. No path-rewrite migration, no
  compatibility layer.
- Blast radius is limited to remote-imported image/file items already cached on this
  machine (re-syncable). The WebDAV feature is unreleased (1.3 in prep), so this is
  acceptable.

## 7. Affected components

- `Services/Clipboard/ClipboardStoreService.cs`
  - Add `FilesDirectory`, `FavoriteFilesDirectory` (create on init).
  - Extend `CompactOrphanBlobsAsync`: reconcile `files/` (subtree, grace) and
    `favorites/files/`.
  - Add subtree reconciliation helper.
  - Extend `CreateFavoriteSnapshot` / `DeleteFavoriteFiles` /
    `TryRepairFavoriteSnapshotLocked` for `favorites/files/`.
  - Add `FilesDirectory` to `ClearHistoryAsync` wipe.
- `Services/Clipboard/ClipboardRepository.cs`
  - Add `LoadRetainedManagedFilePaths(cutoff)` (or equivalent).
- `Services/Clipboard/ClipboardCaptureService.cs`
  - Extract `WriteJpegThumbnail` into the shared thumbnail helper; call it.
- `Services/Clipboard/ClipboardThumbnailWriter.cs` (**new**)
  - Shared JPEG thumbnail generation.
- `Services/ClipboardSync/ClipboardSyncPayloadService.cs`
  - Constructor: drop `incomingDirectory`, accept managed dirs / store + thumbnail
    writer.
  - `ImportAsync` (image/file/group): write to managed dirs; generate thumbnails for
    images; remove all `incoming/` usage.
- `Services/ClipboardSync/ClipboardWebDavSyncService.cs`
  - Remove `incoming/` wiring; construct payload service with managed dirs.
  - Delete-after-use for `outgoing/` and `downloads/`.
  - Wipe `outgoing/` + `downloads/` and delete legacy `incoming/` on startup.
- Tests
  - `tests/Recents.App.Tests/ClipboardSync/ClipboardSyncPayloadServiceTests.cs`:
    update construction (hard cut) and assert imports land in managed dirs.
  - New tests for: staging delete-after-use, startup wipe + legacy incoming removal,
    `files/` reconciliation (referenced kept, unreferenced past grace deleted, empty
    subdirs removed, user real paths untouched, recent unreferenced kept by grace),
    favorite file-drop copy survives `ClearHistory`.

## 8. Edge cases

- **`SaveRemoteItemsToHistory = false`:** import still writes content to managed
  `images/` / `files/` so the OS-clipboard apply works; the unreferenced content is
  reclaimed by the next compaction after the grace window.
- **Hash dedup:** history is unique per non-deleted hash (`idx_clipboard_items_hash`
  + `IngestAsync` dedup), so an active `files/` subtree is referenced by at most one
  active item; reconciliation by path-prefix is unambiguous.
- **Item hash ≠ import subdir name:** reconcile by path containment, not by name.
- **Soft-delete window:** retained reference set includes recently soft-deleted
  items (`deleted_utc >= cutoff`) so undo/restore within the window keeps content,
  consistent with the existing blob behavior.
- **Favorite of an imported file drop:** independent copy in `favorites/files/`;
  survives source pruning and `ClearHistory`.

## 9. Testing strategy

- Unit-test the import pipeline against managed dirs (no `incoming/` created).
- Unit-test reconciliation with a temp data dir: seed referenced + unreferenced +
  recent-unreferenced + user-real-path file items; assert correct keep/delete.
- Unit-test staging delete-after-use and startup wipe with a fake/loopback WebDAV
  client.
- Unit-test favorite file-drop copy + `ClearHistory` survival.
- Run the full `tests/Recents.App.Tests` suite green.

## 10. Acceptance criteria

1. After repeated sync of images/files/groups, `webdav/outgoing/` and
   `webdav/downloads/` hold at most in-flight files (≈0 at rest).
2. `webdav/incoming/` no longer exists (created never; legacy removed on startup).
3. Imported images/files in history are re-pasteable and have thumbnails (images).
4. `files/` size tracks live history: pruning an item makes its `files/` subtree
   eligible and the next compaction removes it; unreferenced content past the
   1-day grace is removed; user real paths are never deleted.
5. Favorites of imported file drops survive source pruning and `ClearHistory`.
6. Full test suite passes.

## 11. Decisions recorded

- **Approach:** lifecycle-driven (staging delete-after-use; imported content into
  managed storage; one reconciliation authority). Size/TTL janitor rejected;
  full relocation chosen over reconcile-in-place for `incoming/` per
  single-source-of-truth.
- **Imported file/folder content:** relocated into managed `files/`, kept
  re-pasteable.
- **Orphan grace window:** 1 day (code constant), tunable.
- **Migration:** hard cut — delete legacy `incoming/`, let `HasUsableContent` prune
  dangling rows.
