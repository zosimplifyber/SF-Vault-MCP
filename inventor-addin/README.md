# SF Vault Bridge

Inventor 2026 add-in that lets the Vault MCP server work in Vault through
Inventor's signed-in session, so no second Vault licence seat is needed.
Spec: `docs/superpowers/specs/2026-10-12-inventor-vault-bridge-design.md`.

## Install (per Windows user, no admin)

1. Close Inventor.
2. `pwsh -File inventor-addin/Install-VaultBridge.ps1` (needs the .NET 8 SDK on the build PC).
3. Start Inventor and sign in to Vault.

## Check it

- Log: `%APPDATA%\Simplifyber\VaultBridge\bridge.log` should say `started 0.1.0 on http://localhost:8790/`.
- `py -3.14 -c "import sys; sys.path.insert(0, 'scripts'); import vault_bridge; print(vault_bridge.status())"`
- Ribbon: its own **SF Vault Bridge** tab (Part, Assembly, Drawing and the no-document ribbon). The Status panel shows a dot, refreshed every 5 s.
  Green = Ready (listening, Vault signed in). Amber = Sign in to Vault (listening, not signed in). Red = Stopped (not listening; see bridge.log).
  Hover for details; click for a dialog with an option to open bridge.log.

## Buttons

The SF Vault Bridge tab has six panels: Status (the dot), Parts (Import New Part, Fix Data Card), Purchasing (Purchasing Sheet), Review (Design Review, Assembly Package), Manufacturing (Partner Package, 3D Print Package) and Release (I Approve!). All but Import New Part and Fix Data Card need an open assembly. They need a Vault sign-in and an assembly that is already in Vault. A run blocks Inventor until it finishes, and a large review can take minutes. Do not End Task if Windows shows Not Responding; wait for the dialog.

Every BOM the add-in produces (Purchasing tab, Design Review and Assembly Package BOM pages, Partner `BOM.xlsx`, `Print List.xlsx`) has a State column, and rows whose Vault state is not Released are light yellow. In the Design Review BOM, yellow means Normal and not Released, and orange means a purchased part without a vendor drawing.

- **Purchasing Sheet**: reads the assembly's BOM and builds the purchasing workbook and checks it into the assembly's Vault folder in the Documents category. The file is also saved next to the assembly. Running it again adds a new version of the same file. The Price source column says where each price came from (File = the BOM's own Cost, otherwise the purchasing list). Shipping is 0. Sales tax is 7% (setting `salesTaxRate`) unless the part has an HS/HTS Code, which makes it a tariff row left for you to fill in. The add-in reads Estimated Cost and the custom property `HS/HTS Code` from each component and writes them to the BOM file as `Cost` and `HS/HTS Code`. If anyone (including you) has the file checked out, nothing is written and you are told who has it.
- **Design Review**: builds a PDF watermarked DRAFT with a cover that lists the assembly, the missing drawings and any drawings that failed to export. It then adds the latest Vault drawing of the assembly and of each component (purchased and standard parts are skipped). The PDF is built in a temp folder, then saved next to the assembly and checked into the same Vault folder (same checkout rule as above). Drawings are downloaded without their references, so Get Latest on the assembly first: a drawing whose models are not in the workspace yet exports with missing views and lands under "Could not export" or shows blanks. Phantom and reference rows are skipped. A drawing that fails is listed on the cover and never stops the package.

### Assembly Package

The third action button builds the package to hand to whoever assembles the product. It needs an open assembly that is in Vault, and a run blocks Inventor like Design Review does.

- Contents: a cover, the BOM, the assembly's own drawing, then the latest drawing of each sub-assembly (one per unique `.iam` row, in BOM order). Parts, purchased and Content Center rows, and phantom and reference rows are skipped. There are no vendor PDFs. Drawing choice, dedupe and Get Latest advice are the same as Design Review.
- Release marking: each drawing's Vault lifecycle state is read at export. Every page of a drawing whose state is not Released is stamped diagonal red NOT RELEASED. Every page carries the footer `FOR ASSEMBLY | <assembly> Rev <revision> | <date>`; the revision is the assembly's Vault revision (`-` when it has none). No page is stamped DRAFT.
- Output: `<assembly> Assembly Package.pdf`, saved next to the assembly and checked into the same Vault folder in Documents (a rerun adds a new version, same checkout rule as the other buttons). The finished dialog reads `<pages> pages, <drawings> drawings, <unreleased> not released, <missing> missing, <failed> could not export.` Missing and failed drawings are also listed on the cover.

### Partner Package

Builds a zip for a manufacturing partner and checks it into Vault. It needs an open assembly or part that is in Vault. A run blocks Inventor like the other package buttons.

Scope, decided by what is open:
- Assembly, nothing selected: the whole assembly (the form has no grid).
- Assembly with components selected (occurrences and picks inside a sub-assembly; deduped by file): only those parts. A selected sub-assembly is its own row (its STEP and drawing, not its children). Picks that are not Make parts (purchased, Content Center, Phantom, Reference, virtual) are skipped and listed under the grid as `not a Make part: <name>`.
- Part open: that part only.
- Drawing or nothing open: `Open an assembly or a part.`

For a selected or single-part scope the form shows the scope line and a grid (Part Number, Description, Mode files, Quantity, Note). Quantity is an editable whole number of 1 or more; the default is the part's total quantity in the assembly (1 for a single part). A part the chosen Parts mode excludes is greyed with the reason (`not CNC`, `not sheet metal`) and not built; with nothing left, Build is disabled. The release gate, quote override, Get Latest refresh, local-edit block and Dropbox step apply unchanged to every scope. The BOM.xlsx title and the cover name the scope, and the BOM Qty column uses the entered quantities.

Zip names: whole assembly `<assembly> <Partner> <Mode> Package.zip`; single part `<CD> <Partner> <Mode> Package.zip`, checked into the part's own Vault folder; selected parts `<assembly> <Partner> <Mode> Selected Package.zip`, next to the assembly. `Quote` goes before `Package` in quote mode.

Form fields: Partner (required, remembered in the recent list), Parts, the For quote only checkbox (never remembered), the Upload to Dropbox checkbox (enabled only when `dropboxAppKey` is set) and a Sign out of Dropbox button (enabled when signed in). The preview line shows the file counts for the chosen Parts.

- Parts, and the files each gives:
  - All Make parts: STEP for every Make part and Make sub-assembly plus the top assembly, the PDF drawing where one exists, and DXF for sheet metal.
  - CNC: STEP + PDF.
  - Sheet metal: DXF + PDF.
- Selection: the Process property decides when it is set (`CNC`, `Sheet Metal`, `3D Print`; any other value counts as Other make). When Process is blank the fallback is: sheet metal part -> sheet metal; material in the print list -> print; `.ipt` -> machined (CNC); `.iam` -> other make. Fallback picks are listed on the cover and in the BOM Process column as `guessed: <kind>`.
- Skipped: Buy, Phantom, Reference, Content Center and virtual rows, and every child of a purchased sub-assembly.
- Qty is the total in the assembly (all occurrences summed).
- Release gate: the build stops before exporting anything unless every included part's model and drawing are Released on the latest Vault version, and none is checked out, open with unsaved edits, open in Inventor at an older version, or edited locally without a checkout (the local file is newer than its Vault version, so Get Latest would overwrite it). The message lists the first 25 blockers and "and N more"; the full list is copied to the clipboard. Nothing is written. Tick For quote only to build anyway: every PDF page is stamped `FOR QUOTE - NOT FOR PRODUCTION`, and the zip name, cover and BOM title say Quote.
- NOT RELEASED stamp: a normal build never stamps a drawing, because the gate has certified it (open and clean at the latest version is fine). In quote mode every page carries the quote stamp instead, and files exported as they are (open, checked out, or edited locally) are listed on the cover source line and in the result.
- File names: `<CD> Rev <rev>.stp`, `.pdf`, `.dxf` (`-` when there is no revision), unique within each folder.
- Zip: `<assembly> <Partner> <Mode> Package.zip` (Mode is Make, CNC or Sheet Metal; `Quote` is added before `Package` in quote mode). Folders `STEP/`, `PDF/`, `DXF/` (empty ones omitted), plus `BOM.xlsx` and `00 Cover.pdf` at the root.
- Cover lists: Process not set (guessed), Missing drawings, No flat pattern, Could not export (with the reason).
- Output: checked into the assembly's Vault folder in Documents (a rerun adds a new version; any checkout blocks), and always copied to `Downloads\SF Vault Bridge\`. The partner name is added to the recent list in the settings file.

### 3D Print Package

Builds a zip of print files for the 3D printed parts of the open assembly. There is no form and no release gate (internal use).

- Selection: parts with Process = `3D Print`, or with Process blank and a material in the print list.
- Format: 3MF when Inventor has a 3MF translator, otherwise binary high-resolution STL.
- Names: `<CD> Rev <rev> x<qty>.3mf` (or `.stl`), in one folder per material (`No material` when blank).
- `Print List.xlsx` at the root: Part Number, Description, Qty, Material, Process, REV, State, File; unreleased rows yellow.
- Zip `<assembly> 3D Print Package.zip`, checked into Vault and copied to `Downloads\SF Vault Bridge\` like the Partner Package.
- With nothing to print the message is "No 3D print parts found (Process = 3D Print or a print material)." and nothing is exported or written.

### Import New Part

The sixth action button files a vendor STEP and its PDF into Vault as a numbered Part - Purchased. Unlike the others it works with or without a document open; it still needs a Vault sign-in, and a run blocks Inventor until it finishes.

1. Click it and pick the STEP and the PDF in the file dialog (opens in Downloads). Select exactly one of each, or a message says what is missing and the dialog reopens.
2. Fill the form. Vendor lists the folders under `$/DESIGNS/Simplifyber Library` (or New vendor...). Subfolder is optional (New subfolder... accepts `a/b` for nesting). Vendor Number and Description are prefilled from the file names and both are required. Material (from Inventor's active material library) and Material Finish may stay empty. Designer is your Inventor user name; Engr Approved By is always `NOT REVIEWED`. The "Will create" line shows `CD-<next>`; the real number is taken only when you press Import.
3. Import saves `CD-xxxxxx.ipt` and `CD-xxxxxx <Vendor> <Vendor Number>.pdf` in the chosen Vault folder, fills the data card (Part Number, Description, Vendor, Vendor Number, WEB Link, Source = Buy, Material, Material Finish, Designer, Engr Approved By), attaches the PDF to the part and moves your two downloads to `Downloads\Imported\`. McMaster-Carr is written on the data card as `McMASTER-CARR`.
4. A file in Vault with the same Vendor and Vendor Number (ignoring case and spaces) stops the import before anything is written; the message names the existing CD number and folder.
5. If a step fails, the message names the step and lists anything that already reached Vault. Nothing is undone automatically and your downloads stay where they were. Remove or reuse the listed leftovers in Vault, fix the cause, and run it again.

### Fix Data Card

The seventh action button, after Import New Part, fixes the data card of parts already in Vault from a form, with a preview before anything is written. It is the screen for the `fix_data_card` operation below, so every safety rule there applies unchanged: dry run first, Released parts skipped unless you tick the revise box, only `.ipt` files, Content Center never touched, and a checkout is undone on any failure before the check-in. It needs a Vault sign-in.

Which parts:

- A part is open: that part.
- An assembly is open: the components selected in the browser or graphics window, once per file. With nothing selected the message is "Select the parts to fix in the browser, then click Fix Data Card." Sub-assemblies contribute nothing and are listed as ".iam not supported yet".
- A drawing is open, or nothing: "Open a part, or select parts in an assembly."

The form has one row per part. Each field has a grey "now" column (read from Vault's latest version, opened invisibly in Inventor, nothing changed) and an editable "new" column: Part Number, Title, Description, Vendor, Vendor Number, Source, Process, Material Finish, Estimated Cost. Cells that differ from "now" turn yellow. Proposed values:

- Part Number is the file name without the extension.
- Title is `<Part Number>-<Description>` for Source = Buy or a purchased category, otherwise it stays as it is. Title is a Summary Information property.
- Vendor uses the data card spelling (`McMASTER-CARR`).
- Source is a Buy/Make dropdown and Process a dropdown from `processValues` in `settings.json` plus blank; the current value is always kept in the list.
- Estimated Cost is a number, shown with 4 decimals; a blank or non-number is an error on that row.
- Everything else starts as it is now.

Only fields whose new value differs are sent; a row with nothing to change is greyed "no change". Tick **Revise and re-release Released parts** to include Released parts (off by default, never remembered); without it they show "Released: skipped".

Preview runs the dry run with the edited values and gives each row would change, skipped (with the reason) or error. Apply is enabled only after a Preview with no edits since, asks "Check out, edit and check in N parts?" and then fixes the rows that would change, one at a time, so Inventor stays responsive. Each row ends as fixed (new version), skipped with the reason, or error with the step; revised rows also show the revision before and after and the state. If a Released part cannot be returned to Released, later Released rows are skipped. **Copy results** puts a tab-separated summary on the clipboard. After an Apply the form is read-only; close it and click the button again to continue.

### I Approve! (sign off and release)

On the Release panel. With an assembly open it previews, then signs off and releases the assembly's engineering files bottom up, then builds the Purchasing Sheet and Assembly Package and opens Partner Package. It needs a Vault sign-in and an Inventor user name (Tools, Application Options, General); the sign-off name is its "First L." form.

Scope: the top assembly, every Make sub-assembly and every Make part in its BOM, and the drawing of each (the same drawing the packages pick). Purchased, Content Center (category or `Content Center Files` folder), Phantom, Reference and virtual rows are never touched, nor anything under a purchased or reference row. A Make part must have category `Part - Engineering`, an assembly `Assembly - Engineering`.

The preview writes nothing. One row per file in release order (parts, their drawings, sub-assemblies, their drawings, the top assembly, its drawing) with State now, Revision now, Revision after (from the transition's revision bump; "Vault decides" when it cannot be read), Ready / Blocker and Notes. The header says whether Vault's child check is on. A row is:

- **Ready**, or **already Released** (no action; counts as released for its parents).
- **revise and re-release (model changes)**: a drawing that is already Released while its model is released in this run. It goes Released, Work in Progress (new revision), signed, Released, along transitions found by state name, and is always brought back to Released once it reached Work in Progress. If it cannot be revised it is skipped, which holds back the model's parents. After a failed return to Released, later drawings are not revised.
- **skipped** for a blocker: checked out (by anyone, named); open in Inventor with unsaved changes or at an older version; local edits without a checkout; no drawing; wrong category; Material blank or Generic on a part; Property Compliance not compliant; controlled by a change order; no transition from its state to `Released` or you may not use it; a Vault dependency with an engineering category that is not Released and not in this release, such as a skeleton, a derived base part or a component excluded from the BOM ("depends on <file> (not Released)"), whatever the child check says; or a purchased or Content Center child that is not in a Released state while the child check is on ("<child> must be Released first").
- **held back** when an engineering part or sub-assembly anywhere below it, or the drawing of one of them, or (for a drawing) its model, or another row it depends on in Vault, is not Released after the run. A model whose drawing is skipped or fails is still released, but the assemblies above it are held back. A parent is never released over an unreleased engineering child.

Notes (not blockers): you are the file's Designer; Process blank; Designer blank; Property Compliance unreadable.

**I Approve!** asks "Release N files as <First L.>? M will be skipped, K held back." It first gets the latest version of the whole tree (children included) without overwriting files that are open, checked out or edited locally, then for each file, bottom up: check out, set the custom iProperty `Engr Approved By` (added when missing), save, check in with "Approved by <name> (Vault user <login>)", change the state to `Released` along the transition found by state name, and read the state and revision back. Assemblies and drawings are opened from the workspace with every reference found (otherwise that file fails before anything is saved) and only that file is saved. On check-in each child is referenced at the version on disk: a file this run signed or released at its latest version, any other child at the Vault version whose checksum matches the local file. A child that matches no version stops that file before the checkout. If an open document cannot be saved, its old value is put back in memory. Your open documents are never closed or saved, except a file being signed that is open and clean, which is edited in place. A failure undoes the checkout, marks the row failed and holds back its parents; no file is left checked out. A file whose Vault version moved since the preview fails with "run Preview again". Stop finishes the current file and leaves the rest not attempted; losing the Vault sign-in stops the run. Results: released (revision before -> after), already Released, skipped, held back (which child), failed (step and message), not attempted; **Copy results** copies them.

When the top assembly ends Released, closing the form builds the Purchasing Sheet and the Assembly Package (same messages as their buttons) and opens Partner Package for the whole assembly. Otherwise a message says why they were not built.

#### Library lifecycle and Vault's child check

If a lifecycle's transition to `Released` enforces child state ("children must be released"), Vault refuses to release an assembly whose purchased or Content Center parts are not in a released state, and the preview says so on that assembly. Either:

1. Put library parts in a released state: in Vault Explorer, select the purchased and Content Center files, Change Lifecycle Definition to `Library` (its one state is flagged Released). The preview counts any state whose Released flag is set.
2. Or, in Vault Settings, Behaviors, Lifecycles, edit the Engineering lifecycle's transition to `Released`, and clear the child state check on its Criteria/Actions.

Run Preview again afterwards; the header line shows the check per lifecycle.

### Purchasing list sign-in

The Purchasing Sheet prices parts from the purchasing Microsoft List, which needs a one-time sign-in per Windows user. Open the existing Simplifyber BOM Purchasing tool and press its Sign in button (or run `py -3.14 -m purchasing_reference --login` on a PC with Python). The sign-in is kept in `%LOCALAPPDATA%\Simplifyber\purchasing_msal_cache.bin`, outside the exe, so it survives updates and is shared by both tools. The summary dialog says which source was used:

- "Prices from the purchasing Microsoft List." Signed in and working.
- "Not signed in to the purchasing Microsoft List; used purchased items.xlsx. Sign in once with the BOM Purchasing tool's Sign in button." Prices came from the Excel copy, which may be older. Sign in, then run the button again.
- "Prices from purchased items.xlsx." The Excel copy was used by configuration.
- "No purchasing list found; vendor and cost columns are blank."

Vendor and Vendor Number from the part's iProperties fill any cell the list leaves blank. "Not matched" lists purchased parts that are not on the purchasing list, even when their iProperties supplied a vendor.

### Fix data card

Repairs iProperties of parts already in Vault (for example a Part Number that holds the STEP name instead of the CD number) by checking out, editing in Inventor and checking in a new version. Inventor must be open and signed in to Vault. It is a preview unless you pass `--apply`:

```
py -3.14 scripts/vault_bridge.py fix-data-card CD-001885.ipt CD-001897.ipt --set Vendor=McMASTER-CARR --custom Source=Buy
py -3.14 scripts/vault_bridge.py fix-data-card CD-001885.ipt CD-001897.ipt --apply --comment "Data card fix"
py -3.14 scripts/vault_bridge.py fix-data-card CD-001885.ipt --custom Process=CNC
py -3.14 scripts/vault_bridge.py fix-data-card CD-001885.ipt --custom Process=CNC --apply
py -3.14 scripts/vault_bridge.py fix-data-card CD-001885.ipt --summary "Title=CD-001885-Steel Washer"
```

Setting `Process` this way works for `.ipt` only (assemblies are not supported yet). `--summary NAME=VALUE` (request field `summary`) sets Summary Information properties such as `Title`, compared and applied like the Design ones.

Part Number is set to the file name unless you pass `--no-part-number`. A file is a bare name or a full `$/` path. Files that are checked out, Released, Content Center, an assembly (.iam is not supported yet), not an .ipt, a local copy that differs from Vault, or open in Inventor with unsaved changes are skipped with the reason. On a failure after checkout the checkout is undone.

Numeric Design properties (`Cost`) are converted with the invariant culture (`--set Cost=12.50`); a non-number is a per-file error, "Cost must be a number".

Released files are skipped unless you pass `--revise-released`. Then, for a file in the state named `Released`, the add-in moves it to `Work in Progress`, does the normal checkout, edit and check-in, and moves it back to `Released`, looking each transition up by state name in the file's lifecycle definition (via `DocumentServiceExtensions.UpdateFileLifeCycleStates`). Both transitions are checked before anything changes, and the dry run shows `would_revise: true` and the transition names. If the edit fails the file is still returned to `Released`; if it cannot be returned the result is `status: "error", step: "re_release"` with the actual `state_after`. Content Center files are never revised. Results carry `revision_before`, `revision_after`, `state_after`, and `linked_to_item: true` for files linked to an item (the item is not touched).

### Process property

Inventor custom property `Process` says how a Make part is made. Values: `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other`. Matching is case-insensitive and trimmed. It drives part selection in the Partner and 3D Print Packages.

Settings file: `%APPDATA%\Simplifyber\VaultBridge\settings.json`, created with defaults on first use. Keys:

- `processValues`: the allowed Process values (default the six above).
- `printMaterials`: material names that mean "print this part", each matched as a whole word, case-insensitive (default `PLA`, `PETG`, `ABS`, `ASA`, `PA11`, `PA12`, `Nylon`, `TPU`, `PC`, `Resin`, `CF`).
- `recentPartners`: the Partner Package recent list (ten at most, newest first).
- `dropboxAppKey`: the Dropbox app key (see Dropbox upload below; default blank).
- `salesTaxRate`: sales tax rate for the Purchasing Sheet, a fraction from 0 up to but not including 1 (default 0.07).

A missing key falls back to its default. A file that is not valid JSON, or not a JSON object, is logged to bridge.log and defaults are used for that run; the file is not overwritten.

Vault: the `Process` property already exists in Vault (Text, list values as above, Enforce List on, assigned to Part - Engineering and Assembly - Engineering, mapped both ways to the Inventor property `Process` with Create). Steps for reference or to redo it: Vault Explorer > Tools > Administration > Vault Settings > Behaviors > Properties > New.

1. Name `Process`, Type Text, List values `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other` (tick Enforce List).
2. Associations: categories Part - Engineering and Assembly - Engineering.
3. Mapping: Provider Inventor, file types `.ipt` and `.iam`, property `Process` (User Defined), Mapping Direction Read and Write (bidirectional), Create ticked.
4. OK. No re-index is needed for new check-ins.

The Vault `Cost` property is mapped to Inventor's Estimated Cost.

## Dropbox upload (Partner Packages)

Partner Packages can upload the zip to `/Suppliers/<Partner>/` in the team Dropbox and make a view-only shared link. Tick Upload to Dropbox on the Partner Package form. If you are not signed in yet, the sign-in happens before the build starts; if it fails or is cancelled you are asked whether to build without the upload.

One-time app setup (Zak, once for the whole team):

1. Go to dropbox.com/developers/apps and choose Create app: Scoped access, Full Dropbox, name "SF Vault Bridge".
2. On the Permissions tab turn on `account_info.read`, `files.content.read`, `files.content.write`, `sharing.read`, `sharing.write`, then Submit.
3. On the Settings tab add the redirect URI `http://localhost:53682/`.
4. Copy the App key into `%APPDATA%\Simplifyber\VaultBridge\settings.json` as `"dropboxAppKey": "<key>"`. No app secret is used. A development app is enough for the team size.

Each engineer signs in once: the first upload opens the browser, they approve, and the add-in stores a refresh token encrypted with Windows DPAPI (current user only) in `%APPDATA%\Simplifyber\VaultBridge\dropbox.token`. The sign-in waits 3 minutes. Access tokens stay in memory and no token is logged.

Sign out of Dropbox with the form's Sign out button, which asks Dropbox to revoke the token (best effort) and then deletes `dropbox.token`; deleting the file by hand also works. The next upload asks for sign-in again.

A rerun overwrites the file, so an existing shared link keeps working. If team policy forbids public links the link is team-only, and the result says so. The sign-in listener on `localhost:53682` filters by host name, not network interface; a request still needs the one-time `state` value, so this is low risk. Sign-out also asks Dropbox to revoke the token (best effort). Upload or link failures are warnings; the zip stays in Downloads and Vault.

## Uninstall

Close Inventor, then delete `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge` (DLLs and manifest) and
`%APPDATA%\Simplifyber\VaultBridge` (token and log). The bridge loads only in Inventor 2026.
