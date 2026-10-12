# SF Vault Bridge (Inventor add-in): Design

**Date:** 2026-10-12
**Status:** Draft, awaiting Zak's review

## Problem

Every Vault write from Vault-MCP signs in through the PowerShell SDK bridge,
and Vault refuses that sign-in a license ("Failed to acquire a license") even
with Vault Explorer closed. The McMaster intake (spec
`2026-10-09-mcmaster-intake-design.md`) is built and tested but cannot write.

On 2026-10-12 a throwaway iLogic rule proved that code running **inside
Inventor** can use the Vault add-in's signed-in session to read and write
Vault with no extra seat (`AddFolder $/DESIGNS/PLAYGROUND/ZOlech/intake-probe`,
id 127802). Python cannot call iLogic from outside Inventor 2026 ("Object
does not match target type"), so the code has to live in a compiled add-in
that listens for requests.

## Goal

A small Inventor 2026 add-in, **SF Vault Bridge**, that runs Vault writes and
the STEP import on the engineer's own Inventor session. Today it unblocks
McMaster intake for Zak. Later it carries design review packets and the
purchasing workflow, and reaches the other engineers through Claude Desktop.

## Decisions (Zak, 2026-10-12)

| # | Decision |
|---|---|
| 1 | Build a compiled Inventor add-in, not an iLogic workaround. |
| 2 | First use: intake new supplier parts quickly. |
| 3 | Later projects, each with its own spec: design review packets for Claude, then a purchasing workflow. |
| 4 | Engineers will use Claude Desktop on their own PCs, with Inventor on the same PC. |
| 5 | No screens to click through. Claude is the interface; the add-in works in the background. |

## Phases

| Phase | Delivers | Who |
|---|---|---|
| **1 (this spec)** | Add-in with the write and STEP-import operations McMaster intake needs. Vault-MCP's intake commit calls the add-in instead of the PowerShell SDK and Inventor COM. | Zak, Claude Code |
| 2 | Read operations in the add-in (search, file properties, uses, download), so no Vault password is needed. Vault-MCP packaged as a Claude Desktop extension (.mcpb) that talks to the add-in. | All engineers |
| 3 | Design review packet operations. | All engineers |
| 4 | Purchasing workflow operations. | All engineers |

Phase 1 keeps Vault-MCP's REST reads (Zak's config.json) as they are.

## Approaches considered

- **A. Add-in is a thin bridge; Vault-MCP (Python) stays the brain.**
  Recommended. Intake, spec cards, property rules and the purchasing code are
  already written and reviewed in Python. The add-in only does what must run
  inside Inventor. Phase 2 ships the Python server to engineers as a .mcpb.
- B. The add-in hosts the MCP server itself in C#. One install per engineer,
  but every existing Python tool would have to be rewritten.
- C. iLogic rule run by hand. No build, but a click per part, and nothing to
  grow into phases 2 to 4.

## Architecture

```
Claude Code ──MCP──> Vault-MCP (Python) ──HTTP 127.0.0.1──> SF Vault Bridge (inside Inventor)
                         │                                    ├─ Vault: Inventor's signed-in session
                         └─ REST reads (phase 1 only)         └─ Inventor API: STEP import, iProperties
```

### Add-in (C#, .NET 8, Inventor 2026)

- `ApplicationAddInServer`, loads on Inventor start-up, hidden from the ribbon
  except one status button: "Vault Bridge: connected / not signed in / stopped".
- Listener: `System.Net.HttpListener` on `http://127.0.0.1:8790/` only. Uses
  only the .NET runtime Inventor already ships, so engineers install nothing
  else.
- Auth: a random token created at first start in
  `%APPDATA%\Simplifyber\VaultBridge\token` (readable only by that Windows
  user). Every request sends it in `X-Bridge-Token`. Wrong or missing token:
  401.
- Vault connection: `Connectivity.Application.VaultBase.ConnectionManager.Instance.Connection`
  at request time. Null: the request returns
  `{"error": "not_signed_in", "message": "Sign in to Vault in Inventor (Vault tab, Log In)."}`.
- Threading: requests are handled one at a time. Inventor API calls are
  marshalled to Inventor's UI thread through the `SynchronizationContext`
  captured in `Activate`. Vault web-service calls run on the listener thread.
- References (not copied to output): `Autodesk.Inventor.Interop.dll` from
  Inventor's Bin, and the Vault DLLs from
  `C:\ProgramData\Autodesk\ApplicationPlugins\VaultInventor2026.bundle\Contents`
  (already loaded in Inventor's process by the Vault add-in).

### Operations (phase 1)

All are `POST /op/<name>` with a JSON body; replies are JSON. Shapes match the
existing `scripts/vault_sdk.py` wrappers so the Python side swaps one module.

| Operation | Body | Reply |
|---|---|---|
| `status` | `{}` | `{signed_in, user, vault, server, inventor_version, bridge_version}` |
| `get_numbering_schemes` | `{}` | `{schemes: [{id, name, isDefault}]}` |
| `generate_file_number` | `{schemeId}` | `{number}` |
| `get_folder_by_path` | `{path}` | `{found, id, fullName, isLibrary}` |
| `add_folder` | `{parentPath, name}` | `{id, fullName}` |
| `get_file_categories` | `{}` | `{categories: [{id, name}]}` |
| `add_file` | `{folderPath, localPath, fileName, attachmentFileIds, comment}` | `{id, masterId, name, checkedOut}` |
| `update_file_categories` | `{masterIds, categoryId, comment}` | `{updated: [...]}` |
| `import_step_as_part` | `{stepPath, outIpt, iproperties: {design, custom}}` | `{outIpt}` |

`import_step_as_part` keeps the Task 5 rules: refuse to overwrite, reject a
STEP that opens as an assembly or as a reference model, always close the
document.

Path guard: `add_file.localPath`, `import_step_as_part.stepPath` and
`outIpt` must sit under the Vault workspace root or the user's Downloads
folder; anything else returns `{"error": "path_not_allowed"}`.

### Vault-MCP changes (Python)

- New `scripts/vault_bridge.py`: same function names and returns as the
  intake's use of `vault_sdk`, implemented as HTTP calls to the add-in; reads
  the token file; raises `BridgeUnavailable` with a plain message when
  Inventor is not running, the add-in is not loaded, or Vault is not signed in.
- `mcmaster_intake.py`: the commit uses `vault_bridge` for every write and
  for `import_step_as_part`; the "inventor check" step becomes a `status`
  call. Preview, duplicate guard, CD clash check, verify and tidy are
  unchanged.
- `scripts/vault_sdk.ps1` stays for the older write tools; they move to the
  bridge in phase 2.

## Install

- `inventor-addin/` folder in the Vault-MCP repo: C# project, `.addin`
  manifest, `Install-VaultBridge.ps1`.
- The install script copies the built DLL to
  `%APPDATA%\Simplifyber\VaultBridge\` and the `.addin` file to
  `%APPDATA%\Autodesk\Inventor 2026\Addins\`. No admin rights.
- Build needs the .NET 8 SDK on the build PC only.

## Error handling

- Every operation returns `{"error": code, "message": text}` on failure;
  Python surfaces the message as the intake's failed-step error.
- Add-in exceptions are caught per request and logged to
  `%APPDATA%\Simplifyber\VaultBridge\bridge.log`; Inventor never sees an
  unhandled exception from the add-in.
- Port 8790 in use: the add-in logs it and the status button shows "stopped".

## Testing

- C# unit tests (xUnit) for request routing, token check, path guard and
  JSON shapes, with Vault and Inventor behind interfaces and faked.
- Python unit tests for `vault_bridge.py` against a fake HTTP server, and the
  intake tool tests re-pointed at a fake bridge.
- Live smoke test with Zak: `status`, `add_folder` and `add_file` in
  `$/DESIGNS/PLAYGROUND/ZOlech/intake-probe`, then one real intake (DIN 6796 M8).

## Out of scope (phase 1)

- Read operations, Claude Desktop packaging, other engineers' installs.
- Design review packets, purchasing.
- Dialogs or screens in Inventor.
- Inventor versions other than 2026.
