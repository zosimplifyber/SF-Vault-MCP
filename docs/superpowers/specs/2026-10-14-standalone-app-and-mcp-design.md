# SF Vault Bridge as a standalone Inventor app with a built-in MCP server: Design

**Date:** 2026-10-14
**Status:** Approved in chat by Zak 2026-10-14 ("Yes, this makes sense! Can you set this up for me?").
**Related:** `2026-10-12-inventor-vault-bridge-design.md` (bridge, token, HttpHost), the ribbon specs of 2026-10-12/13.

## Goal

Engineers install one versioned package from Vault. Inventor loads it as a normal Autodesk app bundle. While Inventor runs, the add-in also serves MCP at `http://localhost:8790/mcp`, and a one-click Claude Desktop extension (`.mcpb`) relays to it, so each engineer's Claude can use Vault and the tools through that engineer's own Inventor/Vault session. No stored Vault passwords, no extra license seats, no separate server processes.

## Decisions (Zak, 2026-10-14)

| # | Decision |
|---|---|
| 1 | Channel: a versioned zip checked into Vault at `$/Tools/SF Vault Bridge/SF Vault Bridge <version>.zip`. |
| 2 | Updates: notify in Inventor (Status button "Update available: <v>"; click downloads the zip through the Vault session and shows how to install). Never auto-install. |
| 3 | Unsigned for now; the install guide shows the one-time unblock in Add-in Manager. |
| 4 | Package contents: add-in + SFVaultTools.exe (prebuilt), team settings, Claude Desktop extension. |
| 5 | MCP runs inside the add-in (Streamable HTTP, JSON responses); the `.mcpb` is a small Node relay run by Claude Desktop's built-in Node. |
| 6 | Writes through MCP are preview-first and need the engineer's confirmation in Claude (tool pairs `*_preview` / `*_apply`, or a `confirm` flag that defaults false). |

## 1. App bundle and versioning (lane A)

- Install location becomes `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge.bundle\` with `PackageContents.xml` (AutodeskProduct Inventor, SeriesMin/Max Ind26, AppVersion, ProductCode GUID fixed across versions, UpgradeCode fixed), `Contents\` holding the DLLs, `.addin` manifest, `tools\SFVaultTools.exe`. The old `ApplicationPlugins\SFVaultBridge\` folder is removed by the installer (after confirming it holds only our files).
- Version: a single source `inventor-addin/VERSION` (start `1.0.0`), stamped into the assemblies (AssemblyInformationalVersion), PackageContents.xml, the zip name, and shown in the Status tooltip and bridge.log start line. `status` op returns `version`.
- `build-release.ps1` (repo, Zak runs or Claude runs): Release build, rebuild SFVaultTools.exe, build the `.mcpb`, assemble `release/SF Vault Bridge <v>/` (bundle, `Install.cmd` + `Install-Package.ps1`, `team-settings.json`, `SF Vault Bridge.mcpb`, `Install guide.pdf`), zip it. Never needs anything on engineer PCs except Inventor 2026 (its .NET 8 runtime runs the add-in).
- `Install.cmd` (engineer double-clicks): checks Inventor 2026 present and not running (lists the PID and asks them to close it), copies the bundle, runs `Unblock-File` on everything copied, merges `team-settings.json` into `%APPDATA%\Simplifyber\VaultBridge\settings.json` (team keys overwrite: processValues, printMaterials, dropboxAppKey, salesTaxRate; personal keys kept: recentPartners), writes an `Uninstall.cmd` next to the bundle, prints next steps (start Inventor, unblock in Add-in Manager once, sign in to Vault, double-click the .mcpb for Claude).
- Install guide PDF: one page, built by the release script (reportlab), plain steps with the unblock instructions.
- Publish: a bridge op `publish_release` (zip path, version) that checks the zip into `$/Tools/SF Vault Bridge/` (creating folders outside the Library root for this op only, category Documents, refuses an existing same-version name), plus a CLI `py -3.14 scripts/vault_bridge.py publish-release <zip>`.
- Update check: on start (after Vault sign-in is seen) and every 6 hours, list `$/Tools/SF Vault Bridge/`, parse versions from `SF Vault Bridge <x.y.z>.zip`, compare with own version (semantic compare, tested in Core). Newer: Status button text/tooltip "Update available: <v>"; clicking Status then offers "Download <v>": acquires the zip to `%USERPROFILE%\Downloads\SF Vault Bridge\` and opens the folder with instructions "Close Inventor, unzip, run Install.cmd". Failure to check is silent (logged once).

## 2. MCP endpoint and Vault read operations (lane B)

- HttpHost gains `POST /mcp` implementing MCP Streamable HTTP with JSON responses (no SSE needed): JSON-RPC 2.0 `initialize` (protocolVersion "2025-06-18", serverInfo name "sf-vault-bridge" + version, capabilities tools), `notifications/initialized` (202), `tools/list`, `tools/call`, `ping`. Same auth as the bridge: the token from `%APPDATA%\Simplifyber\VaultBridge\token` in header `X-Bridge-Token` or `Authorization: Bearer <token>`; localhost only; reject a non-localhost `Origin`. Mcp-Session-Id issued on initialize and accepted on later calls (stateless otherwise).
- Tool calls run on Inventor's UI thread through the existing RunOnUi path with a timeout; results are MCP `content` text (compact JSON) with `isError` on failure.
- New read operations in the bridge (Core contracts + Addin implementation through `VaultFiles` / the Vault web services, read-only), each also exposed as a bridge HTTP op:
  - `vault_search_files(query, limit=25)`: name/property search like the existing REST tool, returns name, folder, state, revision, category, version, file_id.
  - `vault_get_file(file or file_id)`: properties incl. data card, lifecycle, compliance.
  - `vault_where_used(file, levels=1)`, `vault_uses(file)` (children with versions), `vault_file_versions(file, limit)`.
  - `inventor_open_bom()`: the active assembly's structured BOM (same BomReader + enrichment as the Purchasing Sheet), including Source, Process, Cost, State.
  - `bridge_status()`.
- Existing write ops exposed as preview/apply pairs: `fix_data_card_preview` / `fix_data_card_apply` (apply requires `confirm: true` and the same request as a preview made in the last 10 minutes, matched by a hash), McMaster intake `mcmaster_intake_preview` / `mcmaster_intake_commit` (wrap the existing mcmaster_intake flow logic in C# or call the Python through SFVaultTools only if trivial; prefer C# using the existing StepImporter / VaultFiles import path used by Import New Part: inputs vendor, vendor number, description, step and pdf paths in Downloads, subfolder).
- Phase 2 (not this build): outputs (Purchasing Sheet, Design Review, packages, I Approve! preview) as MCP tools need non-UI variants; list them in the README as planned.
- Tool descriptions are written for Claude: what it does, when to use it, that writes need a preview first.

## 3. Claude Desktop extension (lane C)

- `claude-extension/` in the repo: `manifest.json` (MCPB manifest, server type `node`, entry `server/index.js`, display name "SF Vault Bridge", description, author Simplifyber, version = VERSION, tools listed statically from a generated list), `server/index.js`: stdio MCP relay with no npm dependencies (Node 18+ built-ins only): reads newline-delimited JSON-RPC from stdin, POSTs each message to `http://localhost:8790/mcp` with the token read from `%APPDATA%\Simplifyber\VaultBridge\token` (re-read on 401), forwards responses to stdout, keeps the Mcp-Session-Id. If the bridge is not reachable it answers `initialize` itself and returns tool errors "Start Inventor and sign in to Vault; the SF Vault Bridge add-in serves these tools." so Claude explains the situation.
- Packed with `npx @anthropic-ai/mcpb pack` if available on the build PC, else a zip with the `.mcpb` extension built by the release script (the format is a zip with manifest.json at the root); confirm the manifest schema against the MCPB spec and record the version used.
- Node tests (node:test, no deps) for message relay, token handling, bridge-down answers.

## Testing

Core xUnit: version compare and file-name parse, settings merge, MCP JSON-RPC dispatch (initialize, list, call, errors, auth, origin), preview/apply hash matching, read-op result shaping. Python: release script unit bits. Node: relay tests. Live with Zak: install from the release zip on his PC (as an engineer would), Claude Desktop extension, a few read tools, a Fix Data Card preview through Claude.
