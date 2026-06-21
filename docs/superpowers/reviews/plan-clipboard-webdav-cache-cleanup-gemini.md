# ai-bridge review evidence
- vendor: gemini
- effort: high
- command: agy --model Gemini 3.1 Pro (High) --print-timeout 15m --add-dir D:\Git\recents --sandbox -p <prompt>
- written: 2026-06-21T12:37:30.748Z

---
[MAJOR] src/Recents.App/Services/Clipboard/ClipboardStoreService.cs:498 — The plan correctly adds a 1-day grace window for `files/` but completely misses applying this grace window to `images/` and `thumbs/` in `CompactOrphanBlobsAsync`. Since imported images now land in `images/`, an imported image where `SaveRemoteItemsToHistory = false` will be immediately deleted by `DeleteUnreferencedFiles` on the next compaction before the user can paste it. (grounding: Spec Section 8 "import still writes content to managed images/ / files/ ... unreferenced content is reclaimed by the next compaction after the grace window.") → Update `DeleteUnreferencedFiles` to accept and apply the 1-day grace window (just like `DeleteUnreferencedFileTrees`) so `images/` and `thumbs/` are protected.
[MAJOR] src/Recents.App/App.xaml.cs:88 — Startup ordering bug. The plan places `DeleteLegacyIncomingDirectory` in `ClipboardWebDavSyncService.Start()` (Task 7), which runs concurrently with or *after* `InitializeClipboardStoreAsync()` in `App.xaml.cs`. The spec requires the legacy dir deleted so `HasUsableContent` prunes dangling rows on load; this means deletion must happen deterministically *before* the store loads, otherwise rows stay in memory but their files unexpectedly disappear. → In `App.xaml.cs`, call the static `ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory` *before* `InitializeClipboardStoreAsync()`.
[MINOR] src/Recents.App/Services/Clipboard/ClipboardStoreService.cs:553 — The plan lists `TryRepairFavoriteSnapshotLocked` under "Files:" to be modified (Task 9), but completely omits the implementation steps to actually extend it. If a favorite's file-drop snapshot is lost, it won't be repaired from the original source item. → Add a step in Task 9 to update `TryRepairFavoriteSnapshotLocked` to iterate and restore `favorite.FilePaths` using the original `source.FilePaths`.
[MINOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:71 — Over-flagging of `critical: true`. The spec states the critical flag should only be used for genuinely irreversible or foundational tasks due to high cross-vendor review costs. Tasks 1, 2, 3, 6, and 12 are routine refactorings, interface additions, DI wiring, and read-only sweeps. → Remove the `critical: true` flag from Tasks 1, 2, 3, 6, and 12.

VERDICT: NEEDS-FIX
