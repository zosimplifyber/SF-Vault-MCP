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

The Purchasing Sheet and Design Review buttons sit on the SF Vault Bridge panel. They need a Vault sign-in and an assembly that is already in Vault. A run blocks Inventor until it finishes, and a large review can take minutes. Do not End Task if Windows shows Not Responding; wait for the dialog.

- **Purchasing Sheet**: reads the assembly's BOM and builds the purchasing workbook and checks it into the assembly's Vault folder in the Documents category. The file is also saved next to the assembly. Running it again adds a new version of the same file. If anyone (including you) has the file checked out, nothing is written and you are told who has it.
- **Design Review**: builds a PDF watermarked DRAFT with a cover that lists the assembly, the missing drawings and any drawings that failed to export. It then adds the latest Vault drawing of the assembly and of each component (purchased and standard parts are skipped). The PDF is built in a temp folder, then saved next to the assembly and checked into the same Vault folder (same checkout rule as above). Drawings are downloaded without their references, so Get Latest on the assembly first: a drawing whose models are not in the workspace yet exports with missing views and lands under "Could not export" or shows blanks. Phantom and reference rows are skipped. A drawing that fails is listed on the cover and never stops the package.

### Import New Part

The third large button files a vendor STEP and its PDF into Vault as a numbered Part - Purchased. Unlike the other two it works with or without a document open; it still needs a Vault sign-in, and a run blocks Inventor until it finishes.

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

## Uninstall

Close Inventor, then delete `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge` (DLLs and manifest) and
`%APPDATA%\Simplifyber\VaultBridge` (token and log). The bridge loads only in Inventor 2026.
