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

Both buttons sit on the SF Vault Bridge panel. They need a Vault sign-in and an assembly that is already in Vault. A run blocks Inventor until it finishes, and a large review can take minutes. Do not End Task if Windows shows Not Responding; wait for the dialog.

- **Purchasing Sheet**: reads the assembly's BOM and builds the purchasing workbook and checks it into the assembly's Vault folder in the Documents category. The file is also saved next to the assembly. Running it again adds a new version of the same file. If anyone (including you) has the file checked out, nothing is written and you are told who has it.
- **Design Review**: builds a PDF watermarked DRAFT with a cover that lists the assembly, the missing drawings and any drawings that failed to export. It then adds the latest Vault drawing of the assembly and of each component (purchased and standard parts are skipped). The PDF is built in a temp folder, then saved next to the assembly and checked into the same Vault folder (same checkout rule as above). Drawings are downloaded without their references, so Get Latest on the assembly first: a drawing whose models are not in the workspace yet exports with missing views and lands under "Could not export" or shows blanks. Phantom and reference rows are skipped. A drawing that fails is listed on the cover and never stops the package.

## Uninstall

Close Inventor, then delete `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge` (DLLs and manifest) and
`%APPDATA%\Simplifyber\VaultBridge` (token and log). The bridge loads only in Inventor 2026.
