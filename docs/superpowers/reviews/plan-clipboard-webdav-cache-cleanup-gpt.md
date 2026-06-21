# ai-bridge review evidence
- vendor: gpt
- effort: high
- command: codex exec --skip-git-repo-check --sandbox danger-full-access --json -c model_reasoning_effort="high" - <stdin-prompt> (cwd=D:\Git\recents)
- written: 2026-06-21T12:32:15.530Z

---
[BLOCKER] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:456 — Task 4 changes `ClipboardSyncPayloadService` to `(string, IClipboardManagedStorage)` but explicitly modifies only the payload service/tests; the production call in `src/Recents.App/Services/ClipboardSync/ClipboardWebDavSyncService.cs:42` still passes two strings, so the Task 4 commit boundary cannot compile/green → move the sync-service constructor wiring from Task 6 into Task 4, or otherwise update every production constructor call in the same task without adding a legacy overload.

[MAJOR] src/Recents.App/App.xaml.cs:88 — The plan does not cover startup ordering: current startup fires `InitializeClipboardStoreAsync()` before `_clipboardWebDavSync.Start()` at line 114, so `LoadFromDatabaseAsync` can prune before legacy `webdav/incoming` is deleted; spec §6.7 relies on deletion first so `HasUsableContent` prunes dangling incoming-backed rows on load → add a sequencing task that deletes legacy incoming before store load, or immediately reruns load/prune/compaction after deletion.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:574 — The import tests only call `ImportAsync`; they never verify the apply path where `PollOnceAsync` imports, `CreateDataObject` reads `ImagePath`/`FilePaths`, and `SaveRemoteItemsToHistory=false` skips ingestion, despite spec §6.2/§8 requiring apply to work without history → add fake sync/apply tests for image and file/group with `SaveRemoteItemsToHistory=false`, including cleanup timing for unreferenced managed paths.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:719 — `TryWriteImportedThumbnail` swallows decode failures and leaves `ThumbnailPath` null, but spec §6.2 and acceptance criterion 3 require imported images to have thumbnails → define a fallback path that still writes/sets a thumbnail for successfully imported image payloads, and test standard, converted, and decode-fallback cases.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:901 — Task 7’s runnable tests cover only `WipeStagingDirectory` and `DeleteLegacyIncomingDirectory`; they do not prove the actual `UploadCapturedAsync` and `PollOnceAsync` `finally` paths delete `outgoing`/`downloads` payloads required by spec §6.1 → add a fake WebDAV/client seam or equivalent tests that assert staged upload and downloaded raw payload files are deleted on success and exception/exhausted retry.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:1363 — `CopyManagedFileDropForFavorite` catches copy failures and returns the original managed `files/` path, so a favorite can still share source content and later lose it during `ClearHistory` or source pruning, violating spec §6.5’s independent favorite copy contract → for managed paths, fail/abort the favorite snapshot or omit the unusable favorite content; never fall back to the source managed path, and add a copy-failure test.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:244 — The verify contracts repeatedly use `dotnet build src/Recents.App` / bare `dotnet test` and terminal `&&`, but project rules require `dotnet build Recents.sln --no-restore` and then fixed-directory publish after every successful build; the plan can miss solution/publish failures and is not PowerShell 5.1 runnable as written → replace verify gates with PowerShell-compatible `dotnet build Recents.sln --no-restore`, filtered tests, then `dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish`.

[MINOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:1198 — `DeleteUnreferencedFileTrees` keeps recent empty subdirectories for the full grace window, but spec §6.4 says empty subdirs are removed → delete empty subdirs regardless of grace and add a specific empty-subdir assertion.

[MINOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:70 — `critical: true` is over-applied under the plan’s own definition; routine/verification tasks such as Task 1, Task 6, and Task 12 are not irreversible storage cuts and do not justify expensive per-task cross-vendor review → reserve `critical` for genuinely irreversible/foundational tasks such as Task 2/4/5/7/8/9/10/11/14, with Task 3 only if the reference-query deletion semantics will be cross-reviewed.

VERDICT: NEEDS-FIX
