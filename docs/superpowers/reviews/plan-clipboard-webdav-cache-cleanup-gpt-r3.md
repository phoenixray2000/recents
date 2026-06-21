# ai-bridge review evidence
- vendor: gpt
- effort: high
- command: codex exec --skip-git-repo-check --sandbox danger-full-access --json -c model_reasoning_effort="high" - <stdin-prompt> (cwd=D:\Git\recents)
- written: 2026-06-21T13:13:26.471Z

---
[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:1885 — The favorite file-drop rewrite only updates `FilePaths`; it leaves `CreateFavoriteSnapshot`'s existing `PlainText = source.PlainText` copy unchanged, so a favorite of managed imported files can still paste/export stale `files/` source paths as text after `FilePaths` were copied to `favorites/files/`, violating the independent snapshot model (grounding: spec §6.5) → build the favorite file path list first and use the rewritten non-empty favorite paths for `PlainText` on `Files` favorites; for all-empty placeholders clear `PlainText`, and add tests that `CreateDataObject`/text formats do not contain the source `FilesDirectory` path.

VERDICT: NEEDS-FIX
