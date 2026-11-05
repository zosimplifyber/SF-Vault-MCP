# Vault check-out, check-in, undo and state change as MCP tools (1.5.0)

**Date:** 2026-11-05. **Asked by:** Zak, so Claude can run the check-out, edit, check-in and release cycle on Vault files through the add-in (the SDK sign-in outside Inventor cannot write).

## Decisions

- Eight tools in four preview/apply pairs, same contract as every other write: preview changes nothing and returns a one-use `preview_hash` (10 minutes); apply needs `preview_hash` and `confirm: true`, refuses when a file changed in Vault or locally since the preview, and runs as a job (`job_status`).
- All-or-nothing preview: any blocker on any file means no `preview_hash` is issued. Fix the blocker or drop the file and preview again.
- Files are named by `files`: bare names (`CD-001958.ipt`) or `$/` paths. At most 50 per call. An ambiguous or missing name is a blocker.
- Nothing cascades. State change takes `include_children` (default false) to move Dependency children first, then parents.

## Check out

Latest version to the working folder, local copy writable (`AcquireFiles` Checkout + Download, the same call the data-card fix uses). Refused when anyone else holds it, or you already do. Local copy that differs from Vault (edited, or an older version) is a blocker until `local_copy` says `keep` (check out and keep the local file; check-in uploads it) or `replace` (overwrite with Vault's copy; refused when the file is open in Inventor). Nothing is overwritten silently.

**Open in Inventor.** A document opened from a read-only file stays read-only in Inventor after the file becomes writable. After the checkout the add-in tries `Document.Close` then `Documents.Open` (visible, re-activated) when the document is clean and `reopen_in_inventor` is true (default). If Inventor keeps the document because an open assembly references it, the result says so: close and reopen the assembly. This was chosen from the Inventor API; Vault's own add-in internals were not decompiled and the behaviour is on the live checklist.

## Check in (the associations decision)

Check-in carries forward the file associations of the version being replaced (`GetFileAssociationLitesByIds`, Actual algorithm, Dependency and Attachment, library files included, then `FileAssocParam` on `CheckinFile`). This is the code the data-card fix and the I Approve! sign-off already run live on .ipt, .iam and .idw. Each Dependency child is bound to the version that matches the file on disk (`ChooseOnDisk`), so a drawing keeps pointing at its model and an assembly at its children. If a child cannot be matched to a Vault version, nothing is checked in.

Rejected: driving Vault's own Inventor add-in check-in. It is UI driven (modal dialogs), has no automation entry point, and would block Inventor's thread for a person.

Limit of carry-forward: references added or removed in Inventor since the checkout are not in the old associations. For a document open in Inventor the preview compares its direct references with the associations and, if one is new, blocks with "use Vault's Check In in Inventor". Closed files cannot be checked this way; the preview notes that.

Other blockers: not checked out by you, open with unsaved changes in Inventor ("save first"), local file missing, local file identical to the checked-out version (use undo instead). A comment is required. `keep_checked_out` is false by default.

## Undo check-out

Discards local edits. Blocker unless `discard_local_changes` is true when the local copy differs or the Inventor document is dirty. Not your checkout: refused. After undo, an open Inventor document is stale; close it without saving.

## Change state

Uses the file's own lifecycle definition by state name (same lookup as I Approve!, `UpdateFileLifeCycleStates`). Preview lists current state, target, whether the transition exists and the user may take it, the revision after, and blockers: checked out, no transition, target missing (the allowed states are listed), children not released when the transition enforces a child check (rows in the same `include_children` plan count as released first). Vault still decides; its refusal is returned in words.

**Leaving Released (Zak's requirement).** A file whose current state has `ReleasedState` true (Released, Library's released state, any name) moving to a state without that flag sets top-level `leaving_released: true`, a `leaving_released_files` list (file, revision, from, to) and a warning line `LEAVING RELEASED: <file> rev <r> moves from <state> to <state>`. The apply result repeats `leaving_released` and the list. Moving into Released needs nothing special.

## Pieces

| Piece | Where |
|---|---|
| Contracts, rules, evaluation | `SFVaultBridge.Core/VaultFileOps.cs` |
| 8 tools | `SFVaultBridge.Core/Mcp/VaultFileOpsTools.cs` |
| Vault and Inventor side | `SFVaultBridge.Addin/VaultFileOpsHost.cs` (reuses `DataCardHost` checkout, associations, `UndoIfStillMine`, `FindTransition`) |
| Wiring | `McpCatalog.Build(..., fileOps)`, `AddInServer` |
| Tests | `VaultFileOpsTests.cs` with a fake host |

## Build note

Vault 2026.4's client assemblies are .NET 10, so the add-in now targets `net10.0-windows` (Inventor's add-in loader is .NET 10) and builds with the .NET 10 SDK (`%USERPROFILE%\.dotnet-sdk10`). Core and tests stay net8.0.
