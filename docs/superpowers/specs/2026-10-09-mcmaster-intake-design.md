# McMaster Part Intake and Part Specs: Design

**Date:** 2026-10-09
**Status:** Draft, awaiting Zak's review

## Problem

Adding a McMaster part to Vault is manual: download the STEP, open it in
Inventor, save it under the next CD- number in the right McMaster-Carr
subfolder, fill in iProperties, check in, set the category. Steps get skipped.
On 2026-10-09 CD-001893 and CD-001886 sat in Vault as **Unclassified**.

The part's dimensions also never reach Vault. A stackup check (thread
engagement, T-slot fit, washer bearing) needs those numbers. On 2026-10-09 the
check for CD-001887 needed hand-pasted McMaster screenshots because no tool
could read them.

## Goal

Zak asks Claude for a part. Claude finds the McMaster part number. Zak
downloads the STEP and PDF into Downloads. Claude previews, Zak approves, and
the part lands in Vault numbered, filed, categorized, rule-checked, with its
PDF and a machine-readable spec card attached. Any MCP client can then pull
specs for a part or a whole assembly.

## Decisions (Zak, 2026-10-09)

| # | Decision |
|---|---|
| 1 | New parts only. Backfill of the existing library is a later, separate tool. |
| 2 | Downloads stay in `%USERPROFILE%\Downloads`. Files match by McMaster part number in the filename. |
| 3 | Nothing writes to Vault without a preview and Zak's yes. |
| 4 | Claude picks an existing McMaster-Carr subfolder. A new subfolder only when nothing fits, shown in the preview. |
| 5 | McMaster number already in Vault (Vendor Number match): stop, return the existing CD number. |
| 6 | New files stay in **Work in Progress**. |
| 7 | The specs tool also covers Content Center parts (ISO/DIN), from standard tables. |
| 8 | PDFs and spec cards live in Vault, attached to the part. RAGFlow copy is out of scope. |
| 9 | Category must be set and verified: **Part - Purchased** on the .ipt, and the file must pass `file_property_rules.json`. |

## Flow

1. **Find.** Claude finds the McMaster part number (web search, or Zak gives
   it). No McMaster API, no scraping. Zak downloads the STEP and PDF.
2. **Preview** (`vault_mcmaster_intake_preview`). Read-only. Claude passes the
   McMaster number, the spec card it read from the PDF, and the iProperty
   values. The tool:
   - finds `*<pn>*.step|.stp` and `*<pn>*.pdf` in Downloads (newest wins;
     error if either is missing);
   - searches Vault for `Vendor Number = <pn>`; if found, returns that file and
     stops (decision 5);
   - validates the spec card against the schema below;
   - resolves the target folder under `$/DESIGNS/Simplifyber Library/McMaster-Carr`;
   - returns everything that will be written, plus a short-lived preview token.
   It does not reserve a CD number. The number is shown as "next available".
3. **Commit** (`vault_mcmaster_intake_commit`, takes the token). Runs only after
   Zak says yes in chat:
   1. Generate the next CD- number from Vault's numbering scheme.
   2. Inventor (COM): import the STEP as a solid part, set iProperties, save as
      `<CD>.ipt` in the matching local Vault workspace folder.
   3. Vault SDK: add the PDF (`<CD> McMaster <pn>.pdf`) and spec card
      (`<CD>.specs.json`) to the same Vault folder, category **Documents**.
   4. Vault SDK: add `<CD>.ipt` with both as **attachments**.
   5. Set the .ipt category to **Part - Purchased**.
   6. Verify: read the file back over REST; category must be Part - Purchased,
      state Work in Progress, and every `file_property_rules.json` rule for
      that category must pass. Report each failure by name.
   7. Move the two Downloads files to `Downloads\McMaster intake done\`.
   If any step fails, stop, report which step, and list what already reached
   Vault so Zak can clean up. No automatic rollback of Vault writes.
4. **Use** (`vault_get_part_specs`). Read-only. Takes a CD number or an
   assembly. For an assembly it walks the CAD BOM and returns one spec card per
   unique purchased or Content Center part, plus the parts with no data.

## iProperties set at intake

| iProperty | Value |
|---|---|
| Part Number | CD number |
| Description | McMaster's product name, as on existing parts (`Fully Threaded T-Slot Nut`) |
| Vendor | `McMASTER-CARR` |
| Vendor Number (custom) | McMaster number |
| WEB Link | McMaster number (matches existing parts) |
| Source (custom) | `Buy` |
| Material | From the spec card |
| Material Finish (custom) | From the spec card |
| Designer | `Zak O.` |
| Engr Approved By (custom) | `NOT REVIEWED` |

Property names follow existing purchased parts (CD-001888, CD-001894). The
implementation reads the mapping from one table in code, not scattered
literals.

## Spec card schema (`<CD>.specs.json`)

Fixed fields so a check can read them without guessing. Units are mm unless
the field says otherwise. Unknown fields are `null`, never omitted.

```json
{
  "schema": 1,
  "cd_number": "CD-001888",
  "source": {"vendor": "McMASTER-CARR", "vendor_number": "90510A313",
             "pdf": "CD-001888 McMaster 90510A313.pdf", "read_on": "2026-10-09"},
  "type": "t_slot_nut",
  "description": "Fully threaded T-slot nut",
  "material": "Steel", "grade": null, "finish": "Black oxide",
  "thread": {"designation": "M8x1.25", "length": 18, "through": true},
  "dimensions": {"slot_width": 16, "head_width": 25, "head_height": 9,
                 "overall_height": 18, "length": 25},
  "ratings": {"max_temp_c": null, "proof_load_kn": null, "hardness": null},
  "notes": []
}
```

`type` is one of a closed list (screw, nut, t_slot_nut, washer, spring_washer,
pin, bearing, other). Each type names its required `dimensions` keys; the
preview rejects a card missing any. `other` requires none.

## Content Center parts

Content Center files carry the standard and size in the filename
(`ISO 4762 - M8 x 45 - Steel.ipt`). `vault_get_part_specs` parses that and
looks the size up in `standard_specs.json`, a table in the repo. First version
covers the families already in Vault plus the ones from the CD-001887 review:
ISO 4762, ISO 7089, ISO 7093, DIN 127, DIN 6796, DIN 508, ISO 4032. Any other
family returns "no data" by name, never a guess.

## Components

| Unit | Job |
|---|---|
| `mcmaster_intake.py` | Preview and commit orchestration, preview tokens, Downloads matching. |
| `part_specs.py` | Spec card schema and validation, `vault_get_part_specs`, Content Center lookup. |
| `standard_specs.json` | ISO/DIN dimension tables. |
| `scripts/inventor_automation.py` | Add `import_step_as_part(step, out_ipt, iprops)`. |
| `scripts/vault_sdk.ps1` / `vault_sdk.py` | Add `GenerateFileNumber`, `AddFile` (with attachments), `UpdateFileCategories`. |
| `mcp_server.py` | Register the three tools. |

## Known risks to check first

- **Numbering scheme.** CD numbers are assumed to come from a Vault numbering
  scheme the SDK can call. If they are hand-typed today, the plan adds a
  "highest CD number + 1" fallback and says so in the preview.
- **Adding files through the SDK.** The bridge has never added a file. First
  plan task is a probe that adds and deletes one test PDF in a scratch folder.
- **Inventor property sync.** Properties written in Inventor before add must
  show in Vault without a Sync Properties job. The verify step catches it if
  not.
- **License seat.** The SDK sign-in fails while Vault Explorer holds the seat.
  The commit error says so in plain words.

## Testing

- Unit: spec card validation per type, Downloads matching (newest wins,
  missing file), filename parsing for Content Center parts, standard table
  lookups, preview token expiry.
- Fakes for REST, SDK and Inventor, so commit ordering and stop-on-failure are
  tested without Vault.
- One live run with Zak on a real part (one of the CD-001887 replacements),
  checked by hand in Vault Explorer: number, folder, category, attachments,
  properties, rule pass.

## Out of scope

- McMaster API, scraping, automatic downloads.
- Backfilling existing library parts.
- Content Center family authoring.
- Releasing parts or creating Vault items.
- RAGFlow ingestion of spec cards.
