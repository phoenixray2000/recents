# Plan Review Verdict — clipboard-webdav-cache-cleanup

Arbitrated by orchestrator (Opus 4.8). Panel: GPT 5.5 high + Gemini Pro high (scenario `gpt`).
Evidence: `plan-clipboard-webdav-cache-cleanup-gpt.md`, `plan-clipboard-webdav-cache-cleanup-gemini.md`.

## Convergence trajectory

- **Round 1:** 11 findings (1 BLOCKER, 7 MAJOR, 3 MINOR). Architecture: **settled** — no
  redesign required; all findings are sequencing, test-coverage, grace-window extension,
  and build-convention. Core design (eliminate `incoming/` double-duty → managed storage +
  single reconciliation authority) validated by both vendors.
- **Round 2:** 5 findings (0 BLOCKER, 5 MAJOR, 0 MINOR). Count fell 11→5; architecture still
  settled. Cause = ADDING-machinery side effects of the round-1 fixes (the M5 transport seam
  dropped the duplicate-short-circuit delete + used a wrong type name; M3 tests stayed too
  shallow; the rename was incomplete; M6×m3 interact to corrupt favorite index alignment).
  All grounded in spec/source; no additive scope. → round 3.

## Arbitration — accepted findings (→ planner revision)

### BLOCKER
- **B1 (GPT)** Task 4 commit boundary won't compile: production call
  `ClipboardWebDavSyncService.cs:42` still passes `(string, string)` after the constructor
  becomes `(string, IClipboardManagedStorage)`; Task 4's own verify (`dotnet build`) would
  fail. **Fix:** fold the sync-service construction wiring (Task 6) into Task 4 so the
  constructor change and its sole production call site land in one green commit. No legacy
  overload.

### MAJOR
- **M1 (GPT+Gemini)** Startup ordering. Confirmed: `App.xaml.cs:88` loads the store before
  `:114 Start()`. Legacy-`incoming` deletion in `Start()` happens after load → dangling rows
  not pruned on load (spec §6.7). **Fix:** call `ClipboardWebDavSyncService.DeleteLegacyIncomingDirectory`
  from `App.xaml.cs` *before* `InitializeClipboardStoreAsync()`. Move this into Task 7 with an
  explicit App.xaml.cs sequencing step.
- **M2 (Gemini)** Grace window missing on `images/`/`thumbs/`. Imported not-saved images land
  in `images/` unreferenced; the clipboard apply (`CreateDataObject`) references `ImagePath`
  via file-drop + HTML + QQ compatibility formats, so the file must persist until the next
  clipboard change. The existing flat `DeleteUnreferencedFiles` (no grace) could delete it
  prematurely. (grounding: spec §8.) **Fix:** parameterize `DeleteUnreferencedFiles` with the
  1-day grace cutoff (mtime) and apply to blob/image/thumb dirs; update any existing
  compaction test that assumed immediate deletion of just-written orphans.
- **M3 (GPT)** Apply path / `SaveRemoteItemsToHistory=false` untested (spec §6.2/§8).
  **Fix:** add tests proving import writes on-disk content usable by the apply path and that
  unreferenced content survives until the next compaction grace, for image + file/group.
- **M4 (GPT)** Imported-image thumbnail must be reliably produced (acceptance criterion 3);
  don't silently leave `ThumbnailPath` null for a decodable image. **Fix (Task 5):** ensure
  the standard + converted image paths always write a thumbnail; test standard + converted.
- **M5 (GPT)** Staging delete-after-use `finally` paths untested (only the helpers are).
  Spec §6.1 + criterion 1 depend on the actual deletions. **Fix (Task 7):** introduce a
  minimal testable seam (e.g. injectable/fakeable WebDAV client or extract the
  upload-then-delete / import-then-delete into testable internal methods) and assert
  `outgoing/`+`downloads/` payloads are deleted on success AND on exception/exhausted retry.
- **M6 (GPT)** `CopyManagedFileDropForFavorite` falls back to the source managed `files/`
  path on copy failure → favorite shares source content and loses it on prune/ClearHistory
  (violates spec §6.5 independence). **Fix (Task 9):** on managed-path copy failure, fail/omit
  the favorite content; never return the source managed path. Add a copy-failure test.
- **M7 (GPT)** Verify commands violate `AGENTS.md` (confirmed): must use
  `dotnet build Recents.sln --no-restore`, then publish
  `dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish`,
  and be PowerShell-5.1-compatible (no bash `&&`; use `;`/separate lines). **Fix:** rewrite
  every task's verify block accordingly.

### MINOR
- **m1 (GPT)** `DeleteUnreferencedFileTrees`: remove empty subdirs regardless of grace
  (spec §6.4 "empty subdirs are removed"). Add an empty-subdir assertion.
- **m2 (GPT+Gemini)** Over-flagged `critical` (13/14). **Resolution:** keep `critical` on
  Tasks **2, 3, 4, 5, 7, 8, 9, 10, 11, 14** (foundational interfaces / irreversible
  storage-writes & deletions). Remove `critical` from Tasks **1** (mechanical refactor,
  low blast), **6** (DI wiring, caught immediately by build), **12** (read-only verification
  sweep). Task 13 already non-critical. Rationale: Task 2 (managed-storage interface +
  dirs) and Task 3 (reference query feeding deletion) are genuinely foundational — a wrong
  query deletes live content — so they stay critical despite Gemini's suggestion to drop them.
- **m3 (Gemini)** Task 9 lists `TryRepairFavoriteSnapshotLocked` but never extends it.
  **Fix:** add a step to restore favorite `FilePaths` from the source item when the managed
  copy is missing, OR explicitly scope file-drop favorites out of repair with a one-line note.

## Rejected / no findings
- No additive-scope findings were raised that the spec's Non-goals exclude. Both vendors
  stayed anchored to the spec. No size/TTL janitor, layout-restructure, or new-settings
  proposals appeared.

## Round 2 — accepted findings (→ planner revision, round 3)

All MAJOR; all grounded; all accepted (additive-finding gate: none propose spec-excluded scope).

- **R2-1 (GPT)** Staging leak on the duplicate short-circuit. The M5 seam moved deletion into
  `UploadThenDeleteAsync` (upload path only), so the "remote already matches" early return
  after `ExportAsync` created `export.PayloadPath` leaks `outgoing/` (spec §6.1/AC1). **Fix:**
  delete in a `finally` covering the whole exported-payload lifetime — including the duplicate
  return; add a duplicate-short-circuit deletion test.
- **R2-2 (GPT)** Seam snippet references nonexistent type `ClipboardSyncPayloadExport`; the
  real record is `ClipboardSyncExport` → won't compile. **Fix:** use `ClipboardSyncExport`
  everywhere in the Task 6 seam/tests.
- **R2-3 (GPT)** M3 still not genuinely covered — the added tests only assert
  `ImagePath`/`FilePaths` exist; they never exercise `CreateDataObject` /
  `WriteItemToClipboardWithoutHistoryAsync` nor the `SaveRemoteItemsToHistory=false` branch.
  **Fix:** add image + file/group tests with history NOT ingested that run the apply
  data-object path and assert recent unreferenced managed content survives the grace.
- **R2-4 (GPT)** Self-contradiction: Task 5 keeps helper names `CopyIncomingPayloadAsync` /
  `CopyIncomingAsync`, but its own zero-reference grep expects no `incoming|Incoming`. **Fix:**
  rename those helpers + call sites to managed-storage names before the grep step.
- **R2-5 (Gemini)** M6×m3 interaction corrupts favorites. `CreateFavoriteSnapshot` omitting
  failed-copy `FilePaths` (`.Where(f => f is not null)`) shifts indices, but the index-based
  `TryRepairFavoriteSnapshotLocked` parallel loop assumes 1:1 alignment with
  `source.FilePaths` → repairs the wrong entry. **Fix:** preserve index alignment — return a
  placeholder `ClipboardFilePath` (empty `Path`, `ExistsAtCapture=false`) on copy failure
  instead of omitting; make repair/delete/reconcile/`HasUsableContent` tolerate empty paths;
  guard the copy-failure test against empty path before `Path.GetFullPath`.

## Round 3 — verdicts

- **Gemini: GREEN** (no findings — R2-5 placeholder propagation + all other round-2 fixes verified).
- **GPT: NEEDS-FIX — 1 MAJOR (R3-1):** `CreateFavoriteSnapshot` copies `source.PlainText`, which for
  a `Files` item is the newline-joined source `files/` paths. After `FilePaths` are copied to
  `favorites/files/`, `PlainText` still carries the source managed path → `CreateDataObject` emits it
  as text formats (stale/dead after prune/ClearHistory), violating §6.5 independence. (grounding: §6.5)
  **Fix:** for `Files` favorites, rebuild `PlainText` from the rewritten non-empty favorite paths;
  clear it when all placeholders are empty; add a test that the favorite's data-object text does not
  contain the source `FilesDirectory` path. ACCEPTED (grounded; not additive).

Trajectory 11 → 5 → 1: clean falling convergence, architecture settled throughout.

## Round 3 re-review scope note
R3-1 is a single localized fix to `CreateFavoriteSnapshot` PlainText handling. Gemini already
returned GREEN on the full plan (incl. the favorites area). Round-4 re-review is therefore scoped
to GPT (the finder) to confirm R3-1 resolution + no regression in that spot; Gemini's GREEN stands
for the remainder. Panel-green = GPT GREEN ∧ Gemini GREEN(round 3).

## Round 4 — verdicts
- **GPT: GREEN** (R3-1 resolved — Files-favorite PlainText derived from rewritten non-empty
  favorite paths; no regression; tasks 1-13 contiguous; closing gate terminal).
- **Gemini: GREEN stands** from round 3 (full plan; R3-1 area localized, unaffected).

Full trajectory: **11 → 5 → 1 → 0**. Clean falling convergence; architecture settled every round.

## Decision
- Round 1: NEEDS-FIX → revised.
- Round 2: NEEDS-FIX → revised.
- Round 3: NEEDS-FIX (R3-1) → revised.
- **Round 4: GREEN — panel clean (GPT ∧ Gemini).** Layer-0 plan review PASSED. The plan is
  execution-ready and may enter the managed loop.
