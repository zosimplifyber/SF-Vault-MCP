# Purchasing Sheet from the Inventor Ribbon: Design

**Date:** 2026-10-12
**Status:** Draft, awaiting Zak's review

## Problem

Making a purchasing sheet takes a manual BOM export from Inventor (structured,
all levels, sorted by Description, renumbered, the right columns, saved as a
file), then running the standalone purchasing tool on that file, then saving
the workbook somewhere people can find it. Each step is a place to get it
wrong, and the result lives on someone's drive.

## Goal

With an assembly open in Inventor, one click on the SF Vault Bridge ribbon
panel produces the Simplifyber purchasing workbook and checks it into Vault
next to the assembly. No export, no file picking, nothing sent anywhere but
Vault.

## Decisions (Zak, 2026-10-12)

| # | Decision |
|---|---|
| 1 | Trigger is a ribbon button, not Claude. |
| 2 | The BOM is read from the open assembly, not exported by hand. |
| 3 | The workbook is checked into Vault in the assembly's folder. Nothing else receives it (no SharePoint list, no email). |
| 4 | Re-running adds a new version of the same Vault file, so Vault keeps the history. |

## Behavior

1. Button **Purchasing Sheet** on the SF Vault Bridge panel (Tools tab), next
   to the status button. Enabled in the Assembly ribbon; in other ribbons it
   shows "Open an assembly first."
2. Click, with an assembly active:
   1. **Checks:** Vault signed in; the assembly is in Vault (has a Vault
      path); the purchasing engine is installed. Any failure shows one
      message naming the problem and stops.
   2. **Reads the BOM** from the open document (unsaved edits included):
      Structured view, all levels. Columns: Item, Part Number, QTY, Unit QTY,
      Description, BOM Structure, REV, Material, Vendor, Vendor Number,
      Source. Part Number is the component's file name stem when its Part
      Number iProperty is empty. Siblings are sorted by Description
      (descending) and renumbered (1, 1.1, 1.2, 2 ...), matching the manual
      export instructions.
   3. **Builds the workbook** by handing that BOM to the existing purchasing
      engine (`bom_purchasing.generate_from_file`) through the purchasing tool
      in a new no-window mode. The purchased-items reference lookup runs as it
      does today. The BOM file carries Vendor and Vendor Number last (from
      the part iProperties); they only fill cells the purchasing list leaves
      blank, and the list wins where it has a value. "Not matched" still
      means "not on the purchasing list". The result also names the list
      source used (`reference`: mslist, excel or none) and a one-sentence
      `reference_note`, which the summary dialog shows.
   4. **Saves** `<assembly name> Purchasing.xlsx` in the assembly's local
      Vault workspace folder.
   5. **Checks it into Vault** in the assembly's Vault folder, category
      Documents: added if new; if it already exists, checked out, replaced and
      checked in as a new version. If someone else has it checked out, stop
      with "<user> has the purchasing sheet checked out."
   6. **Reports** in one message: Vault path, version, number of BOM lines,
      and any parts the engine could not price or match. Buttons: OK / Open
      workbook.
3. While it runs Inventor shows a busy cursor; a typical assembly takes
   seconds.

## Architecture

```
Ribbon button (add-in, UI thread)
  ├─ BomReader      Inventor API  -> BomRow list -> tab-delimited .txt (temp)
  ├─ PurchasingTool runs SFVaultTools.exe purchasing --bom <txt> --assembly <name> --out <dir> --json
  │                 (existing bom_purchasing engine, packaged as today's exe)
  └─ VaultPublisher VDF session    -> add or new version, category Documents
```

### Units

| Unit | Where | Job |
|---|---|---|
| `BomRow`, `BomOrdering` | Core (unit-tested) | Sort siblings by Description desc, renumber, write the tab-delimited file with the exact headers the engine reads. |
| `PurchasingRun` | Core (unit-tested) | Build the tool's command line, parse its JSON result, map failures to plain messages. |
| `BomReader` | Add-in | Walk `AssemblyComponentDefinition.BOM` structured view into `BomRow`s. |
| `VaultPublisher` | Add-in | Map the assembly's local path to its Vault folder through the connection's working folder; add or check out / check in the workbook; set category. |
| `PurchasingButton` | Add-in | Ribbon button, busy cursor, result message. |
| `purchasing_standalone.py purchasing --bom ... --json` (packaged as SFVaultTools.exe) | Python | Headless mode: no window; prints `{"ok", "workbook", "lines", "unmatched": [...], "error"}`. The GUI stays the default. |

The tool's path: `SFVaultTools.exe` installed next to the add-in by
`Install-VaultBridge.ps1` (built from `purchasing_standalone.py` with the
existing PyInstaller spec). Engineers need no Python.

## Error handling

- Every step runs inside the add-in's existing guarantees: nothing escapes to
  Inventor; errors go to `bridge.log` and one message box.
- The temp BOM file is deleted after the run; the workbook stays in the
  workspace (it is the checked-in file).
- If the Vault step fails after the workbook was built, the message says the
  workbook is saved locally at <path> and was not checked in.

## Testing

- Core: ordering and renumbering (ties, empty descriptions, nested levels),
  file writer headers and escaping (tabs, quotes in descriptions), command
  line, JSON result parsing incl. engine error.
- Python: headless mode builds a workbook from a fixture BOM txt
  (tests/fixtures/CD-001608-inventor-bom.txt) and prints the JSON contract.
- Live, with Zak: CD-001887 open, click, workbook in its Vault folder with
  the right lines; click again, version 2; button in a part shows the
  "open an assembly" message.

## Out of scope (v1)

- Attaching the workbook to the assembly (needs a new assembly version;
  revisit after a safe test on a checked-out assembly).
- McMaster live pricing (waits on the API certificate).
- Vault Item BOMs (Item Master is retired).
- Posting BOM lines to the purchasing Microsoft List.
