# MCP Dropbox upload (1.4.0)

**Date:** 2026-10-22. **Asked by:** Zak, after building the Calico Coatings mold-coating package by hand and wanting Claude to push such packages to Dropbox and hand back a link.

## Problem

The bridge could upload only a Partner Package zip, from the ribbon form. Dropbox's own remote MCP server (mcp.dropbox.com) can search, read, make folders and share links but its create-file tool takes inline UTF-8 text up to 5 MB, so PDFs, STEP files and photos cannot go through it. The gap is a generic binary upload that an agent can drive from any Claude session on the engineer's laptop.

## Decisions

- Reuse the existing Dropbox client, PKCE sign-in and DPAPI token store. One app key, one token file, shared by the ribbon and the MCP tools.
- Preview then apply, like every other write: the preview lists every file that would leave the machine with its Dropbox path and size, so the engineer approves the exact file list. One-use `preview_hash`, 10 minutes, refused when a listed file changed size or disappeared.
- Local paths are limited to Downloads, Desktop and Documents (`PathGuard` with its own wording). Limits per upload: 500 files, 2 GB. `~$` lock files, `Thumbs.db`, `.DS_Store`, `desktop.ini`, hidden and system files are skipped.
- Destination: `partner` puts the upload under `/Suppliers/<partner>/` (same `CleanName` rules as Partner Packages) or `dropbox_folder` names any absolute folder; `.` and `..` segments are refused. A folder lands as a subfolder named after it (override with `subfolder`); a single file goes straight in. The shared link is for the uploaded folder, or the file.
- Overwrite mode, as before, so a rerun replaces files and an existing link keeps working. Per-file failures are recorded and the rest continue; the apply result is `ok: false` when any file failed.
- The upload never runs on Inventor's UI thread. `McpTool.UiThread = false` on a Write now means "run the job on a worker thread": `JobManager.RunOrDetach(..., onUi: false)` and `McpServer` dispatches Writes before the UiThread shortcut. Existing writes keep `UiThread = true` and behave as before. A long upload returns a `job_id` for `job_status`.
- The app key is read from settings on each call, so a key pasted in Settings works without restarting Inventor. Not configured: `not_configured`. Not signed in: the preview warns, and the apply opens the browser sign-in (3 minutes) as the ribbon does.

## Pieces

| Piece | Where |
|---|---|
| Contracts, path rules, planner, drift check, folder list | `SFVaultBridge.Core/DropboxUpload.cs` |
| Tools `dropbox_status`, `dropbox_upload_preview`, `dropbox_upload_apply` | `SFVaultBridge.Core/Mcp/DropboxTools.cs` |
| Off-UI jobs | `Jobs.cs` (`RunOrDetach(onUi)`), `McpServer.RunWithTimeout` |
| Catalog wiring | `McpCatalog.Build(..., dropbox, uploadGuard)`; `AddInServer` passes `DropboxUploader` and the Downloads/Desktop/Documents guard |
| Add-in side | `DropboxUploader` (IDropboxUploader), `DropboxClient.UploadManyAsync` |
| Tests | `DropboxToolsTests.cs`: rules, planner, drift, every tool refusal and the happy path, off-UI job and server dispatch |

## Not done

- No MCP sign-out tool; use Settings or delete `dropbox.token`.
- No per-file progress in `job_status` (the bridge log has it).
- Live test waits on the Dropbox app key (Zak creates the app at dropbox.com/developers, see README).
