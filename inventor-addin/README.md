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
- Ribbon: the **SF Vault Bridge** panel on the Tools tab (Part, Assembly, Drawing, ZeroDoc) shows a dot, refreshed every 5 s.
  Green = Ready (listening, Vault signed in). Amber = Sign in to Vault (listening, not signed in). Red = Stopped (not listening; see bridge.log).
  Hover for details; click for a dialog with an option to open bridge.log.

## Buttons

The Purchasing Sheet, Design Review and Assembly Package buttons sit on the SF Vault Bridge panel. They need a Vault sign-in and an assembly that is already in Vault. A run blocks Inventor until it finishes, and a large review can take minutes. Do not End Task if Windows shows Not Responding; wait for the dialog.

- **Purchasing Sheet**: reads the assembly's BOM and builds the purchasing workbook and checks it into the assembly's Vault folder in the Documents category. The file is also saved next to the assembly. Running it again adds a new version of the same file. If anyone (including you) has the file checked out, nothing is written and you are told who has it.
- **Design Review**: builds a PDF watermarked DRAFT with a cover that lists the assembly, the missing drawings and any drawings that failed to export. It then adds the latest Vault drawing of the assembly and of each component (purchased and standard parts are skipped). The PDF is built in a temp folder, then saved next to the assembly and checked into the same Vault folder (same checkout rule as above). Drawings are downloaded without their references, so Get Latest on the assembly first: a drawing whose models are not in the workspace yet exports with missing views and lands under "Could not export" or shows blanks. Phantom and reference rows are skipped. A drawing that fails is listed on the cover and never stops the package.

### Assembly Package

The fifth large button builds the package to hand to whoever assembles the product. It needs an open assembly that is in Vault, and a run blocks Inventor like Design Review does.

- Contents: a cover, the BOM, the assembly's own drawing, then the latest drawing of each sub-assembly (one per unique `.iam` row, in BOM order). Parts, purchased and Content Center rows, and phantom and reference rows are skipped. There are no vendor PDFs. Drawing choice, dedupe and Get Latest advice are the same as Design Review.
- Release marking: each drawing's Vault lifecycle state is read at export. Every page of a drawing whose state is not Released is stamped diagonal red NOT RELEASED. Every page carries the footer `FOR ASSEMBLY | <assembly> Rev <revision> | <date>`; the revision is the assembly's Vault revision (`-` when it has none). No page is stamped DRAFT.
- Output: `<assembly> Assembly Package.pdf`, saved next to the assembly and checked into the same Vault folder in Documents (a rerun adds a new version, same checkout rule as the other buttons). The finished dialog reads `<pages> pages, <drawings> drawings, <unreleased> not released, <missing> missing, <failed> could not export.` Missing and failed drawings are also listed on the cover.

### Import New Part

The fourth large button files a vendor STEP and its PDF into Vault as a numbered Part - Purchased. Unlike the other two it works with or without a document open; it still needs a Vault sign-in, and a run blocks Inventor until it finishes.

1. Click it and pick the STEP and the PDF in the file dialog (opens in Downloads). Select exactly one of each, or a message says what is missing and the dialog reopens.
2. Fill the form. Vendor lists the folders under `$/DESIGNS/Simplifyber Library` (or New vendor...). Subfolder is optional (New subfolder... accepts `a/b` for nesting). Vendor Number and Description are prefilled from the file names and both are required. Material (from Inventor's active material library) and Material Finish may stay empty. Designer is your Inventor user name; Engr Approved By is always `NOT REVIEWED`. The "Will create" line shows `CD-<next>`; the real number is taken only when you press Import.
3. Import saves `CD-xxxxxx.ipt` and `CD-xxxxxx <Vendor> <Vendor Number>.pdf` in the chosen Vault folder, fills the data card (Part Number, Description, Vendor, Vendor Number, WEB Link, Source = Buy, Material, Material Finish, Designer, Engr Approved By), attaches the PDF to the part and moves your two downloads to `Downloads\Imported\`. McMaster-Carr is written on the data card as `McMASTER-CARR`.
4. A file in Vault with the same Vendor and Vendor Number (ignoring case and spaces) stops the import before anything is written; the message names the existing CD number and folder.
5. If a step fails, the message names the step and lists anything that already reached Vault. Nothing is undone automatically and your downloads stay where they were. Remove or reuse the listed leftovers in Vault, fix the cause, and run it again.

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
```

Part Number is set to the file name unless you pass `--no-part-number`. A file is a bare name or a full `$/` path. Files that are checked out, Released, Content Center, an assembly (.iam is not supported yet), not an .ipt, a local copy that differs from Vault, or open in Inventor with unsaved changes are skipped with the reason. On a failure after checkout the checkout is undone.

## Dropbox upload (Partner Packages)

Partner Packages can upload the zip to `/Suppliers/<Partner>/` in the team Dropbox and make a view-only shared link. The upload code is in the add-in; the Partner Package form does not call it yet.

One-time app setup (Zak, once for the whole team):

1. Go to dropbox.com/developers/apps and choose Create app: Scoped access, Full Dropbox, name "SF Vault Bridge".
2. On the Permissions tab turn on `account_info.read`, `files.content.read`, `files.content.write`, `sharing.read`, `sharing.write`, then Submit.
3. On the Settings tab add the redirect URI `http://localhost:53682/`.
4. Copy the App key into `%APPDATA%\Simplifyber\VaultBridge\settings.json` as `"dropboxAppKey": "<key>"`. No app secret is used. A development app is enough for the team size.

Each engineer signs in once: the first upload opens the browser, they approve, and the add-in stores a refresh token encrypted with Windows DPAPI (current user only) in `%APPDATA%\Simplifyber\VaultBridge\dropbox.token`. The sign-in waits 3 minutes. Access tokens stay in memory and no token is logged.

Sign out of Dropbox by deleting `dropbox.token` (the add-in's sign-out does the same). The next upload asks for sign-in again.

A rerun overwrites the file, so an existing shared link keeps working. If team policy forbids public links the link is team-only, and the result says so. Upload or link failures are warnings; the zip stays in Downloads and Vault.

## Uninstall

Close Inventor, then delete `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge` (DLLs and manifest) and
`%APPDATA%\Simplifyber\VaultBridge` (token and log). The bridge loads only in Inventor 2026.
