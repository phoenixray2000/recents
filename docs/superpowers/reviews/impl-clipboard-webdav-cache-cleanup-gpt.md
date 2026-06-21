# ai-bridge review evidence
- vendor: gpt
- effort: high
- command: codex exec --skip-git-repo-check --sandbox danger-full-access --json -c model_reasoning_effort="high" - <stdin-prompt> (cwd=D:\Git\recents)
- written: 2026-06-21T14:21:18.122Z

---
[MAJOR] src/Recents.App/Services/ClipboardSync/ClipboardSyncPayloadService.cs:124 — `ExportAsync` creates `webdav/outgoing/` payload files before any cleanup scope; if `CopyFileAsync`, `CreateGroupArchiveAsync`, or post-write hashing throws, `RunUploadFlowAsync` is never entered and the partial staged file remains until next startup → wrap each export write path in cleanup-on-exception logic, preferably write to temp then move/return only after the payload is complete.

[MAJOR] src/Recents.App/Services/Clipboard/ClipboardStoreService.cs:876 — compaction deletes empty top-level `files/` subdirs with no grace, while file/group import creates the managed subdir before populating it (`ClipboardSyncPayloadService.cs:538-550`); a startup/hourly compaction racing that window can delete the in-progress import directory and fail the remote apply → populate imports in a temp/non-reconciled location and move the completed non-empty subtree into `FilesDirectory`, or synchronize import writes with compaction.

VERDICT: NEEDS-FIX
