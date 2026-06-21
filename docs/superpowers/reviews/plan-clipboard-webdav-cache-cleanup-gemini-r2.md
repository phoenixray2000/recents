# ai-bridge review evidence
- vendor: gemini
- effort: high
- command: agy --model Gemini 3.1 Pro (High) --print-timeout 15m --add-dir D:\Git\recents --sandbox -p <prompt>
- written: 2026-06-21T12:56:54.739Z

---
[MAJOR] docs/superpowers/plans/2026-06-21-clipboard-webdav-cache-cleanup-plan.md:1635 — In Task 8 Step 3, `CreateFavoriteSnapshot` omits `FilePaths` entries that fail to copy (`.Where(f => f is not null)`). Because `FilePaths` lists map 1:1 by index with `source.FilePaths`, omitting an element shifts the indices of all subsequent elements. Later, `TryRepairFavoriteSnapshotLocked` (Task 8 Step 5) uses an index-based parallel loop (`favorite.FilePaths[idx]` vs `source.FilePaths[idx]`) to repair missing paths. The index shift will cause it to repair the wrong item or corrupt paths using mismatched source paths. → In Task 8 Step 3 (`CreateFavoriteSnapshot`), do not omit failed copies. Return a placeholder to preserve index alignment (e.g., `return new ClipboardFilePath { Path = string.Empty, IsFolder = f.IsFolder, ExistsAtCapture = false };`) instead of `return null;`, and remove `.Where(f => f is not null)`. Also, update the copy-failure test in Task 8 Step 1 to safely handle empty/null paths before calling `Path.GetFullPath(f.Path)` (e.g., using `!string.IsNullOrWhiteSpace`) to prevent `ArgumentException`.

VERDICT: NEEDS-FIX
