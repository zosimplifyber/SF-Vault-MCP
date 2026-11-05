# Vault check-out, check-in, undo, state change (1.5.0): ledger

Spec: docs/superpowers/specs/2026-11-05-vault-checkout-state-design.md. Plan: docs/superpowers/plans/2026-11-05-vault-checkout-state.md.
Branch feat/vault-checkout-state, worktree C:\Users\ZakOlech\Documents\GitHub\Vault-MCP-checkout.

## Status

- Core rules, 8 tools, 27 new C# tests (1101 C# total, 1048 py): done.
- Add-in host `VaultFileOpsHost.cs` compiles against the real Vault 2026.4 and Inventor 2026 assemblies. NOT run against a live Vault.
- Release zip `release\SF Vault Bridge 1.5.0.zip` built (not installed: Inventor was reopened).
- Build trap: the add-in now targets net10.0-windows. Build with the .NET 10 SDK: `$env:PATH='C:\Users\ZakOlech\.dotnet-sdk10;'+$env:PATH; pwsh -File build-release.ps1`. Tests: `cd inventor-addin; dotnet test SFVaultBridge.Core.Tests` (SDK 8).
- SFVaultTools.exe was copied from the intake worktree (Python sources unchanged since 1.4.1).

## Unverified guesses to watch in the live run

1. Reopen of an open Inventor document after checkout (Close then Documents.Open): does `IsModifiable` flip? Vault's own add-in was not decompiled.
2. `UndoCheckoutFile` leaving local edits in place; the host forces a Get Latest afterwards for closed files.
3. `CheckinFile` with carried-forward associations on .idw and .iam (same call as the sign-off, which is proven).
4. `NoOverwrite` checkout of a file whose local copy exists (kept) still clears read-only.

## Live test checklist (after Zak installs 1.5.0, Inventor restarted, signed in to Vault)

Use ONLY a scratch part in `$/DESIGNS/PLAYGROUND/ZOlech/` (make one with a throwaway .ipt, and a throwaway .idw drawing of it in the same folder). Never a real CD- part.

1. `bridge_status` shows 1.5.0 and the tool count rose by 8.
2. Check out: `vault_checkout_preview` on the scratch .ipt, apply. Confirm in Vault Explorer it shows checked out to you and the local file is writable.
3. Checked-out refusal: preview checkout again; expect "already checked out to you".
4. Open the scratch .ipt in Inventor (read-only copy first, then repeat step 2 with it open): expect the result note to say reopened and writable; edit a feature, Save.
5. Dirty refusal: edit again WITHOUT saving; `vault_checkin_preview` must block with "save it in Inventor first". Save, preview again.
6. Check in with a comment: preview shows associations_kept; apply; version rises by 1; comment visible in Vault history.
7. Associations: check out and in the scratch .idw and an .iam of the part (if any); in Vault Explorer confirm the drawing still lists the part under Uses, and Where Used on the part still lists the drawing.
8. Unchanged refusal: check out, do not edit, `vault_checkin_preview` must say identical; then `vault_undo_checkout_preview` and apply; file not checked out, local copy matches Vault.
9. Undo with edits: check out, edit and save, undo preview must block until `discard_local_changes` true; apply; local copy reverts.
10. Local copy differs: edit a closed scratch file locally without checkout (clear read-only), `vault_checkout_preview` must block and name `local_copy`; try keep and replace on separate runs.
11. Someone else's checkout (optional, needs a second user): preview refuses and names them.
12. State change into Released: scratch part Work in Progress to Released; preview shows revision_after, no leaving_released; apply.
13. RELEASED TO WORK IN PROGRESS: preview Released to Work in Progress on the same scratch part. Expect top-level `leaving_released: true`, `leaving_released_files` with the file and its revision, and a warning line starting "LEAVING RELEASED". Apply; the result repeats `leaving_released: true`. Repeat with the scratch drawing.
14. Library lifecycle (optional): a scratch file in the Library lifecycle's released state to Library Edit gives the same flag.
15. include_children: scratch assembly with the scratch part, both Work in Progress: preview Released with include_children true lists the part first; apply; both Released.
16. Not allowed / checked out: preview a state change on a checked-out file; expect the checked-out blocker; a bogus state name lists the lifecycle's states.
17. Clean up: put scratch files back to Work in Progress, check everything in, leave nothing checked out.

## Install (when Zak says, Inventor closed)

Back up `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge.bundle` to a dated .bak folder first, then run Install.cmd from the release zip. Restore the backup to return to 1.4.1.
