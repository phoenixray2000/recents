# ai-bridge review evidence
- vendor: gpt
- effort: high
- command: codex exec --skip-git-repo-check --sandbox danger-full-access --json -c model_reasoning_effort="high" - <stdin-prompt> (cwd=D:\Git\recents)
- written: 2026-06-21T12:55:05.129Z

---
[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:1197 — `UploadThenDeleteAsync` only deletes after the upload path, but the duplicate-key short-circuit stays before upload after `ExportAsync` has created `export.PayloadPath`, so `outgoing/` can still leak staged files (grounding: SPEC §6.1 / AC1) → put deletion in a `finally` covering the whole exported-payload lifetime, including duplicate returns, and add a duplicate-short-circuit deletion test.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:988 — The seam uses nonexistent `ClipboardSyncPayloadExport`; the current record is `ClipboardSyncExport`, so Task 6 snippets/tests will not compile → replace all `ClipboardSyncPayloadExport` references/stubs with `ClipboardSyncExport`, or explicitly rename the record and update every reference.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:656 — M3 is not genuinely covered: the planned tests only assert `ImagePath`/`FilePaths` exist, but do not exercise `CreateDataObject`/`WriteItemToClipboardWithoutHistoryAsync` or the `SaveRemoteItemsToHistory=false` branch → add image and file/group tests with history disabled/not ingested that run the apply data-object path and assert recent unreferenced managed content survives grace.

[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:872 — Task 5 still instructs keeping `CopyIncomingPayloadAsync` / `CopyIncomingAsync` names, while its zero-reference check expects no `incoming|Incoming` matches, so following the plan makes its own verification fail → rename those helpers and all call sites to managed-storage names before the grep step.

VERDICT: NEEDS-FIX
