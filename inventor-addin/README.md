# SF Vault Bridge

Inventor 2026 add-in that lets the Vault MCP server work in Vault through
Inventor's signed-in session, so no second Vault licence seat is needed.
Spec: `docs/superpowers/specs/2026-10-12-inventor-vault-bridge-design.md`.

## Install (per Windows user, no admin)

Engineers: unzip `SF Vault Bridge <version>.zip` (from Vault `$/Tools/SF Vault Bridge/`), close Inventor, double-click `Install.cmd`, start Inventor, allow the add-in once in Add-In Manager (unsigned), sign in to Vault. The package installs `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge.bundle` and merges `team-settings.json` into `%APPDATA%\Simplifyber\VaultBridge\settings.json` (team keys overwrite, `recentPartners` stays). `Uninstall SF Vault Bridge.cmd` is written next to the bundle.

Dev loop (this PC):

1. Close Inventor.
2. `pwsh -File inventor-addin/Install-VaultBridge.ps1` (needs the .NET 8 SDK). It builds, stages the bundle and runs `Install-Package.ps1` on it.
3. Start Inventor and sign in to Vault.

Release: edit `inventor-addin/VERSION` (single source for assemblies, PackageContents.xml, zip name, Status tooltip, bridge.log), run `pwsh -File build-release.ps1` (add `-ExtensionDir <folder with build.ps1>` for the Claude Desktop extension; without it the step logs `EXTENSION NOT BUILT`). Output: `release/SF Vault Bridge <version>.zip`. Publish with the add-in running and signed in: `py -3.14 scripts/vault_bridge.py publish-release "release/SF Vault Bridge <version>.zip"` (refuses a version already in Vault). Running add-ins check `$/Tools/SF Vault Bridge/` on start and every 6 hours and show "Update available: <version>" on the Status button; clicking it downloads the zip to `Downloads\SF Vault Bridge`. Nothing installs automatically.

## Check it

- Log: `%APPDATA%\Simplifyber\VaultBridge\bridge.log` should say `started <version> on http://localhost:8790/`.
- `py -3.14 -c "import sys; sys.path.insert(0, 'scripts'); import vault_bridge; print(vault_bridge.status())"`
- Ribbon: its own **SF Vault Bridge** tab (Part, Assembly, Drawing and the no-document ribbon). The Status panel shows a dot, refreshed every 5 s.
  Green = Ready (listening, Vault signed in). Amber = Sign in to Vault (listening, not signed in). Red = Stopped (not listening; see bridge.log).
  Hover for details; click for a dialog with an option to open bridge.log.

## Buttons

The SF Vault Bridge tab has six panels: Status (the dot and Settings), Parts (Import New Part, Fix Data Card), Purchasing (Purchasing Sheet), Review (Design Review), Manufacturing (Assembly Package for the in-house assemblers and maintenance, Partner Package, 3D Print Package) and Release (I Approve!). Settings, Import New Part and Fix Data Card need no open assembly; the others do. They need a Vault sign-in and an assembly that is already in Vault. A run blocks Inventor until it finishes, and a large review can take minutes. Do not End Task if Windows shows Not Responding; wait for the dialog.

Every BOM the add-in produces (Purchasing tab, Design Review and Assembly Package BOM pages, Partner `BOM.xlsx`, `Print List.xlsx`) has a State column, and rows whose Vault state is not Released are light yellow. In the Design Review BOM, yellow means Normal and not Released, and orange means a purchased part without a vendor drawing.

- **Purchasing Sheet**: reads the assembly's BOM and builds the purchasing workbook and checks it into the assembly's Vault folder in the Documents category. The file is also saved next to the assembly. Running it again adds a new version of the same file. The Price source column says where each price came from (McMaster = a live McMaster-Carr price, File = the BOM's own Cost, otherwise the purchasing list; see McMaster live prices below). Shipping is 0. Sales tax is 7% (setting `salesTaxRate`) unless the part has an HS/HTS Code, which makes it a tariff row left for you to fill in. The add-in reads Estimated Cost and the custom property `HS/HTS Code` from each component and writes them to the BOM file as `Cost` and `HS/HTS Code`. If anyone (including you) has the file checked out, nothing is written and you are told who has it.
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

### McMaster-Carr CAD stays out of supplier packages

McMaster-Carr's API terms (version 2026-09-17, Use of CAD Models) allow its CAD only to decide to buy and to buy from McMaster. They forbid giving it to suppliers and using it for 3D printing or fabrication. So the Partner Package (every mode and scope) and the 3D Print Package never export the STEP, DXF, STL/3MF or drawing PDF of McMaster CAD. A part is McMaster CAD when its Vendor is McMaster (McMaster-Carr, McMASTER-CARR and McMaster Carr all match) or its Vault path is under `$/DESIGNS/Simplifyber Library/McMaster-Carr/`; this also catches old SF-numbered library parts filed as Part - Engineering. The Partner Package BOM.xlsx still lists these parts with part number, vendor number and quantity, and the note "Buy from McMaster-Carr; CAD not supplied"; the cover and the result message list them under "Left out: McMaster-Carr CAD (supplier terms)". Assembly STEPs are exported with the McMaster components suppressed, in an invisible copy that is never saved; an assembly you have open in Inventor is not touched, so its STEP is left out and listed as "assembly STEP left out: contains McMaster-Carr CAD" (close it and rebuild to get it). If a McMaster part name still shows in the exported STEP, the STEP is deleted and listed the same way. The Assembly Package and Design Review are internal and are not affected.

### Import New Part

The sixth action button files a vendor STEP and its PDF into Vault as a numbered Part - Purchased. Unlike the others it works with or without a document open; it still needs a Vault sign-in, and a run blocks Inventor until it finishes.

1. Click it. The form opens straight away. Get the files one of two ways:
   - **McMaster number**: type a McMaster-Carr part number (for example `91251A054`) at the top and press Fetch (or Enter). See "Import from a McMaster number" below.
   - **Choose files...**: pick the STEP and the PDF in the file dialog (opens in Downloads). Select exactly one of each, or a message says what is missing and the dialog reopens. Blank Vendor Number, Description and Vendor are prefilled from the file names.
2. Fill the form. Vendor lists the folders under `$/DESIGNS/Simplifyber Library` (or New vendor...). Subfolder is optional (New subfolder... accepts `a/b` for nesting). Vendor Number and Description are prefilled from the file names and both are required. Material (from Inventor's active material library) and Material Finish may stay empty. Designer is your Inventor user name; Engr Approved By is always `NOT REVIEWED`. The "Will create" line shows `CD-<next>`; the real number is taken only when you press Import.
3. Import saves `CD-xxxxxx.ipt` and `CD-xxxxxx <Vendor> <Vendor Number>.pdf` in the chosen Vault folder, fills the data card (Part Number, Description, Vendor, Vendor Number, WEB Link, Source = Buy, Material, Material Finish, Designer, Engr Approved By), attaches the PDF to the part and moves your two downloads to `Downloads\Imported\`. McMaster-Carr is written on the data card as `McMASTER-CARR`.
4. A file in Vault with the same Vendor and Vendor Number (ignoring case and spaces) stops the import before anything is written; the message names the existing CD number and folder.
5. If a step fails, the message names the step and lists anything that already reached Vault. Nothing is undone automatically and your downloads stay where they were. Remove or reuse the listed leftovers in Vault, fix the cause, and run it again.

#### Import from a McMaster number

Fetch uses the McMaster MCP server (Settings, McMaster). If you are not signed in, it opens the browser sign-in first. It then:

1. reads the product (`get_product`) and its price (`get_price`, one unit);
2. downloads the `3-D STEP` and the `2-D PDF` (`get_cad`; the closest STEP or 2-D PDF format when those exact names are missing) to `Downloads\SF Vault Bridge\McMaster\<PN>\<PN>_<family>.step` and `.pdf`;
   Each download must look like what it claims (a STEP starts `ISO-10303-21`, a PDF `%PDF`), so an error page is never imported; a failed download leaves no partial file.
3. fills the form: Vendor `McMaster-Carr`, Vendor Number, Description = the McMaster family, Comments = the McMaster description, Material and Material Finish (below), and shows the family, description and price line under the box.

Material: finish words in McMaster's Material (Black-Oxide, Zinc-Plated, Passivated, 18-8, Anodized and similar) go to Material Finish; with none it is `As Finished`. What is left is matched to Inventor's active material library: Alloy Steel or Steel gives `Steel`; stainless gives a library entry with the same grade (316), else `Stainless Steel`, else the first stainless entry; an aluminum grade gives `Aluminum 6061` when the library has that exact grade, else the first Aluminum entry; anything else gives the first library name containing the material word. With no match Material is left empty with a hint, and Import asks for it.

Cost: when McMaster sells by the pack, Estimated Cost is the pack price divided by Quantity per Package (for 91251A054, $4.68 / 50 = $0.0936); when it sells by the each, it is the unit price. Any other unit (feet, rolls) leaves Estimated Cost blank with a note. The custom iProperty `Pack Size` (a whole number) is set at import: the pieces per pack, or 1. With no price, or a pack with no Quantity per Package, Estimated Cost is left blank and the form says so. Both go to the part only while Vendor Number still names the fetched part.

McMaster's terms allow its CAD in our own Vault for deciding to buy and buying. The package guard above still keeps it out of everything sent to suppliers. After the import the two downloads move to `Downloads\Imported\` like any other import.

### Fix Data Card

The seventh action button, after Import New Part, fixes the data card of parts already in Vault from a form, with a preview before anything is written. It is the screen for the `fix_data_card` operation below, so every safety rule there applies unchanged: dry run first, Released parts skipped unless you tick the revise box, only `.ipt` files, Content Center never touched, and a checkout is undone on any failure before the check-in. It needs a Vault sign-in.

Which parts:

- A part is open: that part.
- An assembly is open: the components selected in the browser or graphics window, once per file. With nothing selected the message is "Select the parts to fix in the browser, then click Fix Data Card." Sub-assemblies contribute nothing and are listed as ".iam not supported yet".
- A drawing is open, or nothing: "Open a part, or select parts in an assembly."

The form has one row per part. Each field has a grey "now" column (read from Vault's latest version, opened invisibly in Inventor, nothing changed) and an editable "new" column: Part Number, Title, Description, Vendor, Vendor Number, Source, Process, Material Finish, Estimated Cost. Cells that differ from "now" turn yellow. Proposed values:

- Part Number is the file name without the extension.
- Title is `<Part Number> - <Description>` for Source = Buy or a purchased category, otherwise it stays as it is. Title is a Summary Information property.
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
- **revise and re-release (model changes)**: a drawing that is already Released while its model is released in this run. It goes Released, Work in Progress (new revision), signed, Released, along transitions found by state name. It returns to Released only after a successful sign-off; if the sign-off fails it is left in Work in Progress at its new revision ("fix and re-run") and the model's parents are held back. If it cannot be revised it is skipped, which also holds back the model's parents. After a failed return to Released, later drawings are not revised.
- **skipped** for a blocker: checked out (by anyone, named); open in Inventor with unsaved changes or at an older version (also for already-Released files and for any child loaded in Inventor; close and reopen the assembly); local edits without a checkout; no drawing; wrong category; Material blank or Generic on a part; Property Compliance not compliant; controlled by a change order; no transition from its state to `Released` or you may not use it; a Vault dependency with an engineering category that is not Released and not in this release, such as a skeleton, a derived base part or a component excluded from the BOM ("depends on <file> (not Released)"), whatever the child check says; or a purchased or Content Center child that is not in a Released state while the child check is on ("<child> must be Released first").
- **held back** when an engineering part or sub-assembly anywhere below it, or the drawing of one of them, or (for a drawing) its model, or another row it depends on in Vault, is not Released after the run. A model whose drawing is skipped or fails is still released, but the assemblies above it are held back. A parent is never released over an unreleased engineering child.

Notes (not blockers): you are the file's Designer; Process blank; Designer blank; Property Compliance unreadable.

**I Approve!** asks "Release N files as <First L.>? M will be skipped, K held back." It first gets the latest version of the whole tree (children included) without overwriting files that are open, checked out or edited locally, then for each file, bottom up: check out, set the custom iProperty `Engr Approved By` (added when missing), save, check in with "Approved by <name> (Vault user <login>)", change the state to `Released` along the transition found by state name, and read the state and revision back. Assemblies and drawings are opened from the workspace with every reference found (otherwise that file fails before anything is saved) and only that file is saved. On check-in each child is referenced at the version on disk: a file this run signed or released at its latest version, any other child at the Vault version whose checksum matches the local file. A child that matches no version stops that file before the checkout with "your copy of <file> differs from Vault (Content Center parts regenerated locally do this); Get Latest or check it in, then re-run". If an open document cannot be saved, its old value is put back in memory. Your open documents are never closed or saved, except a file being signed that is open and clean, which is edited in place. A failure undoes the checkout, marks the row failed and holds back its parents; no file is left checked out. A file whose Vault version moved since the preview fails with "run Preview again". Stop finishes the current file and leaves the rest not attempted; losing the Vault sign-in stops the run. Results: released (revision before -> after), already Released, skipped, held back (which child), failed (step and message), not attempted; **Copy results** copies them.

When the top assembly and its drawing end Released, closing the form builds the Purchasing Sheet and the Assembly Package (same messages as their buttons) and opens Partner Package for the whole assembly. Otherwise a message says why they were not built.

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

### McMaster live prices

For McMaster rows (Vendor spelled any McMaster way, or a part under `$/DESIGNS/Simplifyber Library/McMaster-Carr/`) the Purchasing Sheet gets today's price from the McMaster MCP server before it builds the sheet. If you are not signed in, a small window starts the browser sign-in; Cancel there builds the sheet with the list and file prices.

- One `enrich_bom` call prices every unique McMaster part number (the Vendor Number). If the server refuses it (for example the daily subscription budget is used up), each part is priced on its own with `get_price`, so parts the server already holds still price. A part sold by the pack needs its pack size: `get_product`'s live Quantity per Package, else the part's `Pack Size` iProperty. A part sold by any unit other than each or pack (feet, rolls) is not priced live.
- Each live price goes to the BOM file as `McMaster Pack Price`, `Pack Size` and `McMaster Price Date`. On the sheet the row's Price source is `McMaster`, Cost Per is the pack price / Pack Size, Packs to Buy is Qty / Pack Size rounded up, and Sub Total is Packs to Buy x pack price + shipping + tax (sales tax unless the part has an HS/HTS Code). McMaster wins over File and the list. The By Vendor tab buys the same way: for a McMaster row its Line Total is whole packs (the summed quantity / Pack Size, rounded up) x the pack price, and Unit Cost shows to 4 decimals.
- A part that fails (not signed in, cancelled, budget, not found, no pack size, server down) keeps its File or list price. Row 2 of the sheet and the result message say how many McMaster prices were live and how many fell back, for example `McMaster: 4 live prices (2026-10-14), 1 fell back to list or file price`. Details are in `bridge.log`.

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

### Attach McMaster PDF

Older McMaster parts in Vault often have no vendor drawing PDF. This attaches it to an existing part without re-importing it. It needs Inventor open, a Vault sign-in and, for the download, the McMaster sign-in (the browser opens on first use, the same one Import New Part's Fetch uses). It is a preview unless you pass `--apply`:

```
py -3.14 scripts/vault_bridge.py attach-vendor-pdf CD-001897.ipt CD-001885.ipt
py -3.14 scripts/vault_bridge.py attach-vendor-pdf CD-001897.ipt CD-001885.ipt --apply --revise-released
py -3.14 scripts/vault_bridge.py attach-vendor-pdf CD-001162.ipt --source "C:/Users/you/Downloads/drawing.pdf" --apply
```

For each part it reads Vendor and Vendor Number from Vault and names the PDF `<CD> McMaster-Carr <VendorNumber>.pdf`, the same rule Import New Part uses. `--source mcmaster` (the default) downloads the McMaster 2-D PDF (`get_cad`, content checked to start `%PDF`); `--source <path>` attaches a PDF you already have under Downloads, to one part. The preview shows what would be downloaded, added and attached and writes nothing, not even a download.

A part is skipped, with the reason, when:

- it already has an attachment with that name, or any PDF attachment whose name contains the vendor number;
- it has no Vendor Number, or (for the McMaster source) is not a McMaster part;
- it is a Content Center file (never touched, with or without `--revise-released`), is not an `.ipt`, is checked out, has a local copy that differs from Vault, or is open in Inventor with unsaved changes;
- it is released (any state with the released flag, such as `Library`) and `--revise-released` was not passed.

The PDF is added beside the part in its Vault folder with category Documents. If a file with that name is already in the folder but not attached, that file is attached instead of adding a duplicate (the preview says `attach_existing`). The part then gets a new version: it is checked out, and checked in again with every existing dependency and attachment re-passed exactly (the same association re-pass Fix Data Card uses) plus the PDF as an attachment; a failure before the check-in undoes the checkout. A released part goes through its edit state the same way Fix Data Card revises one, chosen by lifecycle flags in the part's own lifecycle and verified by state id: `Library` to `Library Edit`, check in, back to `Library`. The PDF is added before the part is revised, so a failed add never moves the part, and a failed attach returns the part to its original state. Rerunning after a failure is safe: a PDF that already reached Vault is attached, not added twice.

In the Fix Data Card form, tick the **Attach McMaster PDF** box on the rows you want and press the **Attach McMaster PDF** button. It checks those parts (reading Vault only), lists what will be downloaded and added, and asks before writing. Tick **Revise and re-release Released parts** first for released parts. The same operation is the MCP pair `attach_vendor_pdf_preview` (arguments `file`, `source`, `revise_released`, returns a `preview_hash`) and `attach_vendor_pdf_apply` (needs the `preview_hash` and `confirm: true` within 10 minutes).

### Add a material to the shared library

Adds a material to the team's Inventor material library, `SF Materials.adsklib` in Vault (`$/VAULT STANDARDS/DESIGN DATA/Materials`), which Inventor's active project loads. It needs Inventor open and a Vault sign-in. The checked-in library is read-only on disk, so this does the checkout, edit and check-in in one step.

First run, once: confirm the value names against a material that already exists. Nothing is written.

```
py -3.14 scripts/vault_bridge.py list-material-properties "Nylon 12" --library "SF Materials"
```

It dumps every asset value of that material (name, type, unit, value), following the physical and appearance assets it references. The names `add-material` writes are `structural_Density` (kg/m3), `structural_Young_modulus` (kPa), `structural_Poisson_ratio`, `structural_Minimum_yield_stress` (kPa), `structural_Minimum_tensile_strength` (kPa), `structural_Thermal_expansion_coefficient` (um/m/C), `structural_Thermal_conductivity` (W/(m K)) and `structural_Specific_heat` (J/(g C)), read from the schemas inside the library file. If the dump shows different names or units, stop and fix `MaterialRules.Properties` before an apply.

Then preview, and apply:

```
py -3.14 scripts/vault_bridge.py add-material --library "$/VAULT STANDARDS/DESIGN DATA/Materials/SF Materials.adsklib" --name "Nylon 12 (PA12, MJF)" --json scripts/materials/nylon12_pa12_mjf.json
py -3.14 scripts/vault_bridge.py add-material --library "$/VAULT STANDARDS/DESIGN DATA/Materials/SF Materials.adsklib" --name "Nylon 12 (PA12, MJF)" --json scripts/materials/nylon12_pa12_mjf.json --apply
```

The JSON has `name`, `description`, `material_class` (Plastic, Metal, ...) and one `{"value": 1.01, "unit": "g/cm3"}` per property: `density`, `youngs_modulus` and `poissons_ratio` are required; `yield_strength`, `tensile_strength`, `thermal_expansion`, `thermal_conductivity` and `specific_heat` are optional. Units are always explicit and converted to what Inventor stores (for example 1800 MPa becomes 1,800,000 kPa, 1.0e-4 1/C becomes 100 um/m/C); a unit that does not fit the property, or a value outside Inventor's allowed range, is refused with the field name. `elongation` is accepted but Inventor has no value for it, so the preview lists it as not stored.

The preview writes nothing. It finds the library in Vault, checks the local copy is byte-identical to Vault's latest version (otherwise it stops: check in or discard local changes first), checks whether the material already exists in the copy Inventor has loaded (it never opens or changes a library in a preview; if Inventor does not have it loaded the apply checks again), and shows each value as it will be written. A library checked out by someone else, or in a released state without `--revise-released`, is skipped with the reason.

The apply, on Inventor's UI thread:

1. Copies the local library file to `%APPDATA%\Simplifyber\VaultBridge\backups\<name>-<timestamp>.adsklib`.
2. Builds the material in a temporary hidden part document (`Document.Assets.Add(kAssetTypeMaterial, "Generic", ...)`), sets the description, class and values, and tries to add a matte plastic appearance from the active appearance library. A name or value Inventor refuses stops here, before Vault is touched.
3. Closes the library in Inventor (`AssetLibrary.Remove`, which only unloads it), checks it out of Vault with the Vault API, opens it again with `AssetLibraries.Open` and checks `AssetLibrary.IsReadOnly` is false.
4. `Asset.CopyTo(library)`, reads every value back, closes the library (the Inventor asset API has no Save; the file is written as the asset is copied in), checks it in with the comment `Add material <name>` (category and lifecycle are not changed), reopens it from the checked-in file and confirms the material is there. The temporary document is closed without saving, and Inventor's library list and active library are put back.

Any failure after the checkout undoes the checkout, puts the backup back if the local file changed, reloads the library in Inventor and names the failed step (`backup`, `build`, `checkout`, `open`, `copy`, `verify`, `checkin`). The library is never left checked out. A failure after the check-in succeeds is a warning, not an undo. Run `add-material` again after a failure; a material that is already there is reported as no change.

MCP: `list_material_properties` (read), `add_library_material_preview` (arguments `library`, `name`, `values`, `revise_released`, `comment`; returns a `preview_hash`) and `add_library_material_apply` (needs the `preview_hash` and `confirm: true` within 10 minutes).

Status: built and unit tested, never run against Inventor or Vault. The Inventor asset API calls compile against Inventor 2026, but the first live run must start with `list-material-properties`, then a preview, then the apply, with the backups folder in mind.

### Process property

Inventor custom property `Process` says how a Make part is made. Values: `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other`. Matching is case-insensitive and trimmed. It drives part selection in the Partner and 3D Print Packages.

Settings file: `%APPDATA%\Simplifyber\VaultBridge\settings.json`, created with defaults on first use. Keys:

- `processValues`: the allowed Process values (default the six above).
- `printMaterials`: material names that mean "print this part", each matched as a whole word, case-insensitive (default `PLA`, `PETG`, `ABS`, `ASA`, `PA11`, `PA12`, `Nylon`, `TPU`, `PC`, `Resin`, `CF`).
- `recentPartners`: the Partner Package recent list (ten at most, newest first).
- `dropboxAppKey`: the Dropbox app key (see Dropbox upload below; default blank).
- `salesTaxRate`: sales tax rate for the Purchasing Sheet, a fraction from 0 to 1 (default 0.07).
- `userOverrides`: team keys you changed in the Settings window; the team merge leaves them alone (see Settings).

A missing key falls back to its default. A file that is not valid JSON, or not a JSON object, is logged to bridge.log and defaults are used for that run; the file is not overwritten.

Vault: the `Process` property already exists in Vault (Text, list values as above, Enforce List on, assigned to Part - Engineering and Assembly - Engineering, mapped both ways to the Inventor property `Process` with Create). Steps for reference or to redo it: Vault Explorer > Tools > Administration > Vault Settings > Behaviors > Properties > New.

1. Name `Process`, Type Text, List values `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other` (tick Enforce List).
2. Associations: categories Part - Engineering and Assembly - Engineering.
3. Mapping: Provider Inventor, file types `.ipt` and `.iam`, property `Process` (User Defined), Mapping Direction Read and Write (bidirectional), Create ticked.
4. OK. No re-index is needed for new check-ins.

The Vault `Cost` property is mapped to Inventor's Estimated Cost.

## Settings

The gear button on the Status panel opens the Settings window (no open document needed). Save validates, then writes `%APPDATA%\Simplifyber\VaultBridge\settings.json`; Cancel changes nothing.

- **General**: the installed version, Check for updates now (the same Vault release-folder check as the status button, so you must be signed in to Vault; the result shows in the window and the status button picks it up) and Open log folder.
- **Dropbox**: the app key, Sign in and Sign out, and a signed in or not line. Sign in is enabled only when a key is set and opens your browser once. Changing the key signs the old account out when you save (a token belongs to one app key).
- **McMaster**: the McMaster MCP server URL (`mcmasterServerUrl`, default `https://mcmaster.simplifyber-app.com/mcp`; a team key, so team settings set it), Sign in, Sign out and a signed in or not line. See McMaster sign-in below.
- **Purchasing and packages**: sales tax as a percentage from 0 to 100 (stored as a fraction in `salesTaxRate`, so 8.25 is 0.0825), and the Process values, print materials and recent partners lists. Process values and print materials can be added to, removed from and moved up or down; each needs at least one value, entries are trimmed, and a duplicate in any letter case is refused or dropped. Recent partners has Remove selected and Clear all.
- **Claude**: the MCP endpoint (`http://localhost:8790/mcp`), the path of the token file, Copy setup info (copies the endpoint and the token file path, never the token) and Open Claude extension (selects the `.mcpb` next to the installed add-in, else the newest one under `Downloads\SF Vault Bridge`, else opens that folder or says where to get it).

Saved values apply to the next button click with no restart: every command reads `settings.json` when it starts, including the Dropbox app key.

Team settings never overwrite your changes. Save adds each team key whose value you changed (`processValues`, `printMaterials`, `dropboxAppKey`, `salesTaxRate`, `mcmasterServerUrl`) to `userOverrides` in `settings.json`. The installer's team merge (and `SettingsMerge`) skips every key listed there and still fills the others. Edit `userOverrides` by hand (or delete a name from it) to take team values again. Keys the window does not know about are kept as they are.

## Dropbox upload (Partner Packages and MCP)

Partner Packages can upload the zip to `/Suppliers/<Partner>/` in the team Dropbox and make a view-only shared link. Tick Upload to Dropbox on the Partner Package form. If you are not signed in yet, the sign-in happens before the build starts; if it fails or is cancelled you are asked whether to build without the upload.

Any other file or folder can be uploaded through the MCP tools (1.4.0): `dropbox_upload_preview` lists exactly which files under Downloads, Desktop or Documents would be sent, with their Dropbox paths and sizes (at most 500 files and 2 GB per upload; Office lock files and desktop metadata are skipped), and `dropbox_upload_apply` sends them after the engineer approves the list and returns the shared link for the folder (or the file). A folder lands as a subfolder of the target named after it; an upload reruns as an overwrite, so an existing link keeps working. The upload runs as a job on a worker thread, never on Inventor's UI thread, and a long one returns a job_id for `job_status`. The same app key, sign-in and token file serve both paths; a key pasted in Settings is picked up on the next call without restarting Inventor.

One-time app setup (Zak, once for the whole team):

1. Go to dropbox.com/developers/apps and choose Create app: Scoped access, Full Dropbox, name "SF Vault Bridge".
2. On the Permissions tab turn on `account_info.read`, `files.content.read`, `files.content.write`, `sharing.read`, `sharing.write`, then Submit.
3. On the Settings tab add the redirect URI `http://localhost:53682/`.
4. Copy the App key into `%APPDATA%\Simplifyber\VaultBridge\settings.json` as `"dropboxAppKey": "<key>"`. No app secret is used. A development app is enough for the team size.

Each engineer signs in once: the first upload opens the browser, they approve, and the add-in stores a refresh token encrypted with Windows DPAPI (current user only) in `%APPDATA%\Simplifyber\VaultBridge\dropbox.token`. The sign-in waits 3 minutes. Access tokens stay in memory and no token is logged.

Sign out of Dropbox with the form's Sign out button, which asks Dropbox to revoke the token (best effort) and then deletes `dropbox.token`; deleting the file by hand also works. The next upload asks for sign-in again.

A rerun overwrites the file, so an existing shared link keeps working. If team policy forbids public links the link is team-only, and the result says so. The sign-in listener on `localhost:53682` filters by host name, not network interface; a request still needs the one-time `state` value, so this is low risk. Sign-out also asks Dropbox to revoke the token (best effort). Upload or link failures are warnings; the zip stays in Downloads and Vault.

## McMaster sign-in

The McMaster MCP server sits behind Cloudflare Access, so each engineer signs in once with their own Simplifyber account. The add-in is an OAuth 2.1 public client of Access's managed OAuth:

1. It finds the sign-in server from the McMaster server itself: the 401 answer's `WWW-Authenticate` names the protected resource metadata (falling back to `/.well-known/oauth-protected-resource`), which names the authorization server, whose `/.well-known/oauth-authorization-server` metadata gives the endpoints. Discovery is pinned: the metadata hint is used only when it is https on the server's own origin; the resource must be the server URL itself or its origin; the issuer and every endpoint (authorization, token, registration, revocation) must be https on the issuer's origin; PKCE S256 is required. Redirects are never followed on any sign-in, MCP or download call (a 3xx is an error).
2. It registers itself (dynamic client registration) with a loopback redirect `http://127.0.0.1:<random free port>/callback`, opens the system browser with PKCE S256 and a random `state`, and waits 3 minutes for the redirect. Each connection to the listener gets 10 seconds to send its request, so an idle browser preconnect never holds up the real one.
3. It keeps the refresh token (and the registered client id) encrypted with Windows DPAPI for the current user in `%APPDATA%\Simplifyber\VaultBridge\mcmaster.token`. Access tokens stay in memory and are refreshed on their own; no token, code or verifier is ever logged.

Sign out in Settings asks Access to revoke the refresh token (best effort) and deletes the file; deleting the file by hand also works. A sign-in belongs to one server URL: changing the URL in Settings needs a new sign-in. If Access refuses the refresh (expired token, or the registered client was removed), the stored sign-in is deleted and the next Fetch or Purchasing Sheet starts a fresh sign-in on its own. A server fault (5xx) keeps the sign-in.

## MCP endpoint

While Inventor runs, the add-in also serves MCP (Streamable HTTP, JSON responses) at `POST http://localhost:8790/mcp`. Auth is the bridge token (`X-Bridge-Token` or `Authorization: Bearer <token>`); only localhost origins are accepted. `Mcp-Session-Id` is issued on initialize and accepted afterwards (no per-session state). Requests are served concurrently; only loopback peers with a localhost Host header are accepted, token and Origin are checked before the body is read, and bodies are capped at 1 MB. Inventor work runs on the UI thread; a read that cannot start within 90 s is cancelled and never runs. The Origin check accepts any localhost port, so the token is the real barrier. Writes run as jobs: the call waits up to 45 s, then returns a job_id to poll with job_status (jobs are kept for 1 hour; one write at a time).

| Tool | Kind | What it does |
|---|---|---|
| `bridge_status` | read | Vault sign-in, Inventor and add-in versions, active document |
| `vault_search_files` | read | Search latest files by name, Description, Title, Part Number, Vendor Number |
| `vault_get_file` | read | One file: category, state, revision, data card, compliance |
| `vault_where_used` | read | Parents of a file, 1 to 5 levels |
| `vault_uses` | read | Children with referenced versions, 1 to 5 levels |
| `vault_file_versions` | read | Version history, newest first |
| `inventor_open_bom` | read | Structured BOM of the active assembly with Vault facts |
| `fix_data_card_preview` | preview | Dry run of Fix Data Card (writes nothing) |
| `fix_data_card_apply` | write | Applies it; needs `confirm: true` and a matching preview in the last 10 minutes |
| `attach_vendor_pdf_preview` | preview | Plans attaching the McMaster drawing PDF to an existing part (writes and downloads nothing), returns `preview_hash` |
| `attach_vendor_pdf_apply` | write | Downloads, adds and attaches it; needs `preview_hash` and `confirm: true` within 10 minutes |
| `list_material_properties` | read | Asset values (name, type, unit, value) of a material loaded in Inventor |
| `add_library_material_preview` | preview | Plans adding a material to the shared Inventor material library (writes nothing), returns `preview_hash` |
| `add_library_material_apply` | write | Backs up, checks the library out, adds the material, checks it in and verifies; needs `preview_hash` and `confirm: true` within 10 minutes |
| `mcmaster_intake_preview` | preview | Plans a vendor part import from Downloads (STEP + PDF), returns a token |
| `mcmaster_intake_commit` | write | Imports it via Import New Part; needs the token and `confirm: true` |
| `vault_list_folder` | read | List a folder's subfolders and files (state, revision, category) |
| `vault_find_folder` | read | Check a folder path exists; returns folder_id |
| `vault_advanced_search` | read | Files by property conditions (AND, or OR with `any`), optional folder |
| `vault_job_queue` | read | Recent jobs with type, status, submitter, created, file |
| `vault_job_queue_enabled` | read | Whether the Vault job queue is on |
| `vault_submit_pdf_job_preview` | preview | Plans the standard PDF job for a drawing's latest version, returns `preview_hash` |
| `vault_submit_pdf_job_apply` | write | Queues the previewed PDF job; needs `preview_hash` and `confirm: true` within 10 minutes |
| `vault_property_definitions` | read | Property names, types and usage (filter, entity) |
| `vault_categories` | read | Categories and what they apply to |
| `vault_lifecycles` | read | Lifecycle definitions with states (released/obsolete flags) and transitions |
| `vault_revision_schemes` | read | Revision schemes and their label sequences |
| `vault_users` | read | Users (active only by default) |
| `vault_groups` | read | Groups with member users and child groups |
| `dropbox_status` | read | Whether a Dropbox app key is set and this user is signed in |
| `dropbox_upload_preview` | preview | Lists every file a local file or folder upload would send (sizes, Dropbox paths, total), returns `preview_hash` |
| `dropbox_upload_apply` | write | Uploads the previewed files to `/Suppliers/<partner>/` (or an explicit folder) and returns a view-only shared link; needs `preview_hash` and `confirm: true` within 10 minutes; runs off the UI thread as a job |
| `job_status` | read | State and result of a write that was still running (job_id), optional cancel before it starts |

The PDF job is the standard Vault drawing job `Autodesk.Vault.PDF.Create.idw` / `.dwg` with `FileVersionId` and `UpdateViewOption=False` (same as `vault_publish_pdf_from_drawing` and `publish_bom.py`); the preview hash binds file version and job type, so a newer drawing version since the preview is refused. `vault_advanced_search` runs `starts_with` as a contains search and filters the hits afterwards.

The read tools are also bridge ops (`POST /op/<name>`, same arguments). Planned (phase 2, needs non-UI variants): Purchasing Sheet, Design Review, packages and I Approve! preview as MCP tools.

## Uninstall

Close Inventor, then delete `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge` (DLLs and manifest) and
`%APPDATA%\Simplifyber\VaultBridge` (token and log). The bridge loads only in Inventor 2026.
