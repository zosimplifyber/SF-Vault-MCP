# I Approve! (sign off and release): Design

**Date:** 2026-10-13
**Status:** Approved in chat by Zak 2026-10-13 ("ok, I approve the approve design").
**Related:** `2026-10-13-ribbon-manufacturing-packages-design.md` (packages, release gate), the fix_data_card op (DataCardFix / DataCardHost), `scripts/release_workflow.py` (older out-of-process attempt, blocked by the license refusal).

## Goal

One button, **I Approve!**, on a new **Release** panel of the SF Vault Bridge tab. With an assembly open it previews, then signs off and releases the assembly's engineering parts, Make sub-assemblies, the top assembly and their drawings, bottom up, and then builds the Purchasing Sheet and Assembly Package and opens the Partner Package form.

## Decisions (Zak, 2026-10-13)

| # | Decision |
|---|---|
| 1 | Sign-off = **Engr Approved By** set to the current user's "First L." name (ImportRules.DesignerName of Inventor's user name). Anyone may approve; warn (not block) when the approver equals the file's Designer. |
| 2 | Scope: the top assembly, every Make sub-assembly and every engineering part in its BOM (Source Make per the existing SourceRules / Vault category; category `Part - Engineering` or `Assembly - Engineering`), plus each one's drawing (same drawing match as the packages, DrawingFinder / ReviewRules.PickDrawings first pick). Never touch purchased (`Part - Purchased`), Content Center (`Part - Content Center` or under `$/Content Center Files`), Phantom, Reference or virtual rows. |
| 3 | Release what's clean: files with blockers are listed and skipped; an assembly is **held back** when any engineering part or sub-assembly below it is not Released after this run (never release a parent over an unreleased engineering child). |
| 4 | After the release: rebuild the Purchasing Sheet, build the Assembly Package, then open the Partner Package form (Zak picks partner, mode, Dropbox). |
| 5 | Vault items and a second signature are out of scope. |

## Preview (no writes)

Form (DPI-safe, same patterns as FixDataCardForm): one row per file in release order, columns File, Kind (Part/Sub-assembly/Top/Drawing), State now, Revision now, Revision after (from the transition's bump rule if readable, else "Vault decides"), Ready / Blocker, Notes (warnings).

Blockers (row skipped; its parents held back):
- checked out by anyone (name them); open in Inventor with unsaved changes; local edits without checkout (existing LocalCopy rule);
- Make part or sub-assembly with no drawing ("no drawing");
- property compliance not compliant (read Property Compliance; unreadable counts as a warning, not a blocker);
- Material blank or "Generic" on a part; Process blank is a warning only;
- no transition from the file's current state to `Released` in its lifecycle definition, or the user may not use it;
- the transition requires released children (child-state control) and a non-engineering child (purchased / Content Center) is not in a released state: blocker on that parent "<child> must be Released first (Vault's child check is on for this lifecycle)". The preview header states once whether the child check is on, so Zak can switch library parts to a released lifecycle (README steps) or turn the check off.

Already Released at the latest version: row "already Released" (no action; counts as released for parents).

Warnings: approver is the file's Designer; Process blank; Designer blank.

## I Approve! (writes)

Confirmation: "Release N files as <First L.>? M will be skipped, K held back." Then, bottom up (parts, their drawings, sub-assemblies, their drawings, top assembly, its drawing), per file:
1. **Get Latest** for the whole tree first (children included) through the existing safe helpers (never overwrite local edits; those are blockers).
2. **Sign off**: check out, set custom iProperty `Engr Approved By` = approver (add the property when missing), save, check in (comment "Approved by <name>"), through Inventor's session. This generalises the fix_data_card edit to `.iam` and `.idw`/`.dwg` for this flow only: the documents are opened from the user's workspace with references resolved (the tree was just made latest), never closing or saving documents the user had open except the target file itself when it is open and clean (then edit it in place). On failure: undo checkout, mark the row failed, hold back its parents.
3. **Release**: change the lifecycle state to `Released` via the looked-up transition (DocumentServiceExtensions.UpdateFileLifeCycleStates, as in the revise path), re-read state and revision. Failure: row failed, parents held back. Never leave a file checked out.
4. Stop button between files (rows not reached: "not attempted"); a session loss stops the run.

Result: per row released (revision before -> after), already Released, skipped (blocker), held back (which child), failed (step + message), not attempted; "Copy results".

Then, if at least the top assembly is Released: run the Purchasing Sheet and Assembly Package flows (existing code paths, same messages), then open the Partner Package form. If the top assembly is not Released, skip these and say why.

## Architecture

- Core (unit-tested): `ReleasePlan` (build ordered rows from the BOM tree + drawings + per-file facts; blockers; hold-back propagation after results; confirmation counts; approver name and self-approval warning; revision-after text), `ReleaseFacts` record (category, state, revision, lifecycle has-transition, child-check flag, child states, compliance, checkout user, local copy decision, open/dirty, material, process, designer, drawing found).
- Add-in: `ReleaseHost` (Vault reads: lifecycle definitions/transitions incl. child-state control flag, allowed transitions, compliance; the sign-off edit for ipt/iam/idw; state change; re-read), `ApproveForm`, ribbon button `I Approve!` (icon: check mark in a seal, flat 16/32, Icons.ApproveSeal) on a new `Release` panel (RibbonStatus.Groups gains `Release` after `Manufacturing`), README section with the Library-lifecycle and child-check steps.
- Reuse: BomReader/BomEnricher, DrawingFinder, LocalCopy, VaultFiles, DataCardHost patterns (checkout/undo/check-in), the revise path's transition lookup by state name.

## Testing

Core: ordering (bottom up, drawings after their model), hold-back propagation through two levels, every blocker, already-Released rows, child-check on/off with library children, self-approval warning, counts. Add-in: build-verified; live pilot with Zak on a small assembly first (one part + its drawing + the assembly), then CD-001887.
