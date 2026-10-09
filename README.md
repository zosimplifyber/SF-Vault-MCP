# Vault MCP Server

An MCP (Model Context Protocol) server that exposes Autodesk Vault REST API operations as tools, enabling AI assistants like Claude to browse, search, generate purchasing sheets from, and submit jobs against an Autodesk Vault server.

## Prerequisites

- Python 3.10+
- An Autodesk Vault server with REST API access enabled
- Vault user credentials with appropriate permissions
- Autodesk Vault Job Processor 2025+ on a reachable machine if you plan to submit publish/sync jobs
- **Autodesk Vault Client (with an activated license) installed on any machine running the SDK / SOAP scripts** — `scripts/vault_sdk.ps1`, the diagnostic probes under `scripts/probes/` (e.g. `probe_edit_items.ps1`, `probe_vault_sdk.ps1`), and any GUI mode that performs writes. These scripts request a `Client` (per-machine) Vault license seat at sign-in and will fail with `VaultLicenseException` if no Vault Client is installed, no seat is available, or the same user is already signed in to Vault Explorer. The core REST server (`app.py` in `sse` / `stdio` mode) does not require this.
- **`AdskLicensingSDK_8.dll` reachable from PowerShell's DLL search path.** The Vault SDK assemblies P/Invoke into this native library to acquire a license seat; without it on `$env:PATH`, every writable login flow fails with `"Failed to acquire a license"` even though a seat is available. The SDK scripts now prepend `C:\Program Files\Autodesk\Autodesk Vault 2025 SDK\bin\x64` (and the matching Vault Client `Explorer\` folder) to `$env:PATH` automatically — but if you set `$env:VAULT_SDK_BIN` to a non-default location, make sure that folder contains `AdskLicensingSDK_8.dll`. (For Vault 2020-era installs the file is named `AdskLicensingSDK_2.dll`; the scripts probe both names.)
- **A Vault user account with the Item Editor role assigned** if you intend to use SDK writes (`update_item_properties`, `update_item_lifecycle_states`, etc.). Read-only operations are unaffected. Have a Vault admin assign the role in **ADMS Console → Users → Roles**.

## Installation

```bash
pip install -r requirements.txt
```

## Configuration

Copy the template, then edit it with your real credentials:

```bash
cp config.json.example config.json
```

`config.json` is gitignored — keep your credentials local. Edit before running:

```json
{
    "vault": {
        "servername": "http://VaultServer",
        "username": "Administrator",
        "password": "your-password-here",
        "database": "Vault"
    },
    "server": {
        "host": "0.0.0.0",
        "port": 8765
    },
    "logging": {
        "level": "INFO",
        "file": "Log/mcp_server.log"
    }
}
```

| Field | Description |
|---|---|
| `vault.servername` | Hostname or IP of your Vault server (include scheme, e.g. `http://`) |
| `vault.username` | Vault login username |
| `vault.password` | Vault login password |
| `vault.database` | Vault database name (e.g. `Vault`, `Inventor`) |
| `server.host` | Bind address for SSE mode (`0.0.0.0` = all interfaces) |
| `server.port` | Port for the SSE HTTP server (default `8765`). Note: avoid `8080` — Autodesk Desktop Connector and other agents squat on it and spam the log with `403` WebSocket-handshake retries. |
| `logging.level` | Log verbosity: `DEBUG`, `INFO`, `WARNING`, or `ERROR` |
| `logging.file` | Path to the rotating log file (relative to project root) |

## Running the Server

### Recommended: launcher dashboard (default)

```bash
python app.py
```

This opens the **Vault Integration launcher** (Tk dashboard) and auto-starts the SSE MCP server on `http://127.0.0.1:8765/sse` inside the same process. From the dashboard you can also launch the Release Workflow wizard, BOM → Purchasing sheet, Property Check, and BOM → Publish Deliverables — all sharing the same Vault session as the MCP server. One sign-in, one audit trail.

The server endpoints:
- Dashboard: opens automatically (no URL — it's a desktop window)
- SSE endpoint for MCP clients: `http://127.0.0.1:8765/sse`
- Messages endpoint: `http://127.0.0.1:8765/messages`

**The launcher must stay open while any MCP client is using the server.** Closing it prompts to confirm because it would disconnect Claude Desktop / Claude Code mid-session.

### Daily startup (Windows)

A startup shortcut at
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Vault MCP Launcher.lnk`
runs `pythonw.exe app.py` at login so the launcher is already up before you open Claude Desktop. To create or recreate it:

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut("$startup\Vault MCP Launcher.lnk")
$sc.TargetPath = 'C:\Users\<you>\AppData\Local\Python\pythoncore-3.14-64\pythonw.exe'
$sc.Arguments = '"C:\path\to\Vault-MCP\app.py"'
$sc.WorkingDirectory = 'C:\path\to\Vault-MCP'
$sc.Save()
```

`pythonw.exe` (rather than `python.exe`) keeps the console window from appearing — only the Tk dashboard is visible. Logs still go to `Log/mcp_server.log`.

### Other run modes

```bash
python app.py --headless              # bare SSE WebServer, no GUI (for unattended hosts)
python app.py --gui                   # launcher dashboard, MCP server NOT auto-started (manual Start button)
python app.py --transport stdio       # stdin/stdout MCP transport (used directly only when bridging proxies)
python app.py --workflow --part-number SF-001717   # skip launcher, open Release Workflow wizard pre-filled
python app.py --config path/to/my_config.json      # custom config path
```

### Property Check

Type a Vault file name and get back every property that is out of compliance. Open it from the launcher dashboard, or run it directly:

```bash
python scripts/check_file_properties.py CD-001659.iam              # one file
python scripts/check_file_properties.py CD-001659.iam --recursive  # + every child in the CAD BOM
python scripts/check_file_properties.py CD-001659.iam --markdown   # Markdown report
python scripts/check_file_properties.py CD-001659.iam --excel      # also write an .xlsx to Log/
python scripts/check_file_properties.py CD-001659.iam -r -x out.xlsx   # BOM walk → named workbook
python scripts/check_file_properties.py                            # no argument → GUI
```

Exit codes: `0` everything passed, `1` at least one failure, `2` no rule set matched the file's category.

`--recursive` grades each child at its **latest** version, not the version the parent assembly pins. A parent often references an older revision of a child, so grading the pinned version would keep reporting failures you have already fixed. If a child's latest version can't be read it reports ERROR rather than being graded on stale data.

**Excel export.** `--excel` (bare) drops a timestamped workbook in your **Downloads** folder (same place the MFG package builder and purchasing sheet land); give it a path to choose the name. In the GUI, **Export to Excel** unlocks once a check succeeds. The workbook has two sheets — **Summary** (one row per file: status and which properties failed) and **Detail** (one row per property checked) — both filterable with frozen headers and colour-coded PASS / FAIL / SKIP rows.

Rules live in [`file_property_rules.json`](file_property_rules.json), keyed by the file's Category Name, and are re-read on every run — edit and re-check, no restart. What's gated:

Categories split into **in-house work** (`Assembly - Engineering`, `Part - Engineering`, `Drawing - Engineering`) and **bought parts** (`Part - Purchased`, `Part - Content Center`) — catalogue hardware and Inventor library files nobody in-house designs, engineers, approves, or bills to a project.

| Property | Required in |
|---|---|
| State | every category |
| Source | every category except `Part - Content Center` |
| Revision | every category except `Part - Purchased` |
| Engineer, Engr Approved By, Project | in-house categories only |
| Designer | in-house categories except `Assembly - Engineering` |
| Vendor | every part and assembly |
| Title, CAD Category, Description (File) | **nowhere** — reported for reference only |

`Engr Approved By` rejects `NOT REVIEWED` on in-house work, where it means the review hasn't happened; bought parts allow it. A category with no rule set at all reports SKIP rather than a misleading pass.

`Description (File)` was gated on the *Vault PDM – Item Description* standard (lowercase keyword nouns; no dimensions, materials, ISO/DIN numbers, or project/customer names) and is currently switched off. Its rule in the JSON carries the exact snippet needed to turn it back on.

Note this checks **files** (iProperties). The item-side equivalent, `scripts/check_item_properties.py`, still backs the Release Workflow's readiness report and uses `item_property_rules.json`.

## Connecting MCP clients

All clients connect to the same SSE endpoint exposed by the running launcher: `http://127.0.0.1:8765/sse`. The launcher must be running first.

### Claude Code

In `~/.claude.json` (user-level) or `.claude/settings.json` (project-level):

```json
{
  "mcpServers": {
    "vault": {
      "type": "sse",
      "url": "http://127.0.0.1:8765/sse"
    }
  }
}
```

For the Wrike `wrike_*` tools, add the [SF-WrikeConnector](https://github.com/zosimplifyber/SF-WrikeConnector) server (its own independent process) instead.

Or via CLI:

```bash
claude mcp add --transport sse vault http://127.0.0.1:8765/sse
```

### Claude Desktop

Claude Desktop's connector currently launches stdio subprocesses, so connecting it to a long-running SSE server requires the [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) bridge. Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "vault-mcp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:8765/sse"]
    }
  }
}
```

Requires Node.js installed (for `npx`). First launch downloads `mcp-remote` from npm (~5–10 s); subsequent launches use the cache.

After editing, fully quit Claude Desktop from the system tray — not just close the window — and reopen.

### Daily startup checklist

1. Launcher running (auto from the Startup-folder shortcut, or run `python app.py`). Confirm the MCP status dot is green.
2. Open Claude Desktop / Claude Code. They reconnect to the running SSE server automatically.
3. Smoke test: any tool call (e.g. `vault_search_items` for a known part number) should succeed.

If the launcher isn't running when a client tries to connect, you'll see "connection refused" or a red connector status. Start the launcher and re-toggle the connector.

## Session expiration / auto re-authentication

Vault Server times out idle REST sessions (default ~30 minutes), after which every call returns `HTTP 401` with the misleading message _"You currently do not have permissions to download this file."_ — this is **not** an ACL problem; it's the Bearer token having been invalidated.

`VaultRestAPI` in `vault_rest_api.py` handles this transparently: it caches the credentials from the last successful `create_session()` call and, when any subsequent `_request()` returns 401, re-authenticates once with the cached credentials and retries the call. The retry is serialized by an `asyncio.Lock` so concurrent 401s only cause one re-sign-in. If the retry also returns 401 (genuine ACL denial or rotated password), the error propagates normally.

In `Log/mcp_server.log` you'll see this on session expiry:

```
... API error 401: {... 'detail': 'You currently do not have permissions...'}
... Session likely expired — re-authenticating as zolech (database: Simplifyber)
... POST .../sessions  ... 200 OK
... Re-authenticated; retrying GET .../items?q=SF-001717
... Response 200
```

The single visible-to-the-user 401 entry above is harmless — the retry on the next line succeeds.

## Available Tools

Read tools return a compact view: the fields a person reads (number, title,
state, revision, quantity) plus the ids the next call needs. Property values
come back as `{name: value}`. Pass `raw=true` to any of them for the untouched
Vault response. For scale: a three-row BOM is about 1,300 characters, against
about 120,000 raw.

| Tool | Description |
|---|---|
| **Server / auth** | |
| `vault_get_server_info` | Get Vault server version and metadata |
| `vault_sign_in` | Authenticate with different credentials |
| `vault_sign_out` | Invalidate the current session |
| **Vaults / folders** | |
| `vault_list_vaults` | List all accessible vaults |
| `vault_get_vault` | Get details for a specific vault |
| `vault_get_folder_contents` | List files and sub-folders in a folder |
| `vault_get_folder` | Get metadata for a specific folder |
| **Files** | |
| `vault_get_file` | Latest version of a file, with its properties |
| `vault_get_file_versions` | List all versions of a file |
| `vault_get_file_download_url` | Signed, time-limited download link for a file version |
| `vault_get_file_where_used` | CAD where-used: assemblies and drawings that reference a file |
| `vault_get_file_items` | The engineering item(s) a CAD file is linked to |
| `vault_search_files` | Keyword search across vault files |
| `vault_advanced_search` | Property search by name, operator and value, across files, items, folders, change orders |
| **Users / groups** | |
| `vault_list_groups` | List all groups in the vault |
| `vault_get_group` | Get details for a specific group |
| `vault_list_users` | List all users in the vault |
| `vault_get_user` | Get details for a specific user |
| **Properties / lifecycles** | |
| `vault_list_property_definitions` | List user-defined property definitions |
| `vault_get_property_definition` | Get a specific property definition |
| `vault_list_lifecycle_definitions` | List lifecycle definitions |
| **Items (engineering BOM)** | |
| `vault_search_items` | Search for engineering/BOM items |
| `vault_get_item` | Get details for a specific engineering item |
| `vault_get_item_version_history` | List all versions of a master item |
| `vault_get_item_change_orders` | List change orders linked to an item |
| `vault_list_change_orders` | List change orders (open only by default) |
| `vault_get_change_order` | One change order plus the items and files on it |
| `vault_list_item_versions` | List item versions, optionally filtered by query |
| `vault_get_item_version` | Get details for a specific item version |
| `vault_get_item_bom` | Multi-level BOM for an item version, with dotted row numbers and quantities |
| `vault_get_item_parents` | Where-used: direct parents with quantity, plus higher assemblies |
| `vault_get_item_associated_files` | Get files associated with an item version |
| `vault_get_bom_by_part_number` | **One-call lookup: part number → item → BOM** |
| `vault_get_cad_bom_by_part_number` | **One-call lookup: part number → CAD assembly BOM** |
| **Purchasing sheets** | |
| `vault_generate_purchasing_sheet` | **End-to-end: part number → BOM → enriched .xlsx** |
| `vault_generate_purchasing_sheet_from_vault_bom` | Build a sheet from an already-fetched Vault BOM payload |
| `vault_generate_purchasing_sheet_from_file` | Build a sheet from a manually-exported BOM file (.xls/.xlsx/.csv) |
| `vault_lookup_purchased_part` | Look up vendor / cost / lead-time for one part number |
| `vault_get_purchased_items_reference_status` | Check the SharePoint reference file is reachable |
| **Jobs** | |
| `vault_get_job_queue_enabled` | Check whether the Vault job queue is enabled |
| `vault_submit_job` | Submit a job to the Vault job queue (see caveats below) |
| `vault_get_job` | Get a job's status and metadata by ID |
| **Files / utilities** | |
| `vault_watermark_pdfs_in_folder` | Download every PDF in a Vault folder, watermark it, save locally |

The `wrike_*` MCP tools (search/create/update tasks, folders, comments, timelogs, metadata) live in [SF-WrikeConnector](https://github.com/zosimplifyber/SF-WrikeConnector) — see that repo's README for the tool list. This project no longer talks to Wrike at all.

## Known issues / caveats

### `vault_submit_job` and the `*.create.*` job family

Submitting `autodesk.vault.pdf.create.idw`, `autodesk.vault.dwf.create.iam`, etc. via the Vault REST API is **structurally limited**: the server normalizes the first character of every `Params` key to lowercase on receive (`FileMasterId` → `fileMasterId`). The stock Inventor JP handlers do exact-match lookups on PascalCase keys, so REST-submitted jobs in this family typically fail with a wrapped `Job param error` (visible only in the JP machine's Application event log under provider `Autodesk Job Processor`).

`vault_submit_job` works reliably for handlers that case-fold their lookups — confirmed working for:
- `autodesk.vault.syncproperties` (with `FileVersionId` / `FileVersionIds`)
- `autodesk.vault.updaterevisionblock.idw`

For PDF / DWF / DXF / STEP publish jobs, prefer one of:
1. **Manual:** right-click the file in Vault Explorer → Update Visualization Attachment, or transition lifecycle state to one whose entry trigger is `*.create.*`.
2. **Programmatic:** PowerShell using the Vault .NET SDK on a JP-host machine — see `scripts/` (work in progress) or call `DocumentService.UpdateFileLifeCycleStates` directly.

The relevant docstring on `vault_submit_job` in `mcp_server.py` documents this in detail.

### Inspecting JP failures

Job Processor errors are not exposed via REST — by default the JP deletes failed jobs from the queue, which surfaces as a 404 `QueuedEventDoesntExist` on `vault_get_job`. To see the real `InnerException`, query the Windows Application event log on the JP machine:

```powershell
Get-WinEvent -FilterHashtable @{
  LogName='Application'; ProviderName='Autodesk Job Processor';
  StartTime=(Get-Date).AddMinutes(-30); Level=2
} | ForEach-Object { ($_.Properties | ForEach-Object { $_.Value }) -join "`n" }
```

## Project layout

```
Vault-MCP/
├── app.py                      # Entry point — config, logging, mode dispatch (sse / stdio / gui / workflow)
├── mcp_server.py               # FastMCP tool definitions (REST tools + SOAP write paths)
├── vault_rest_api.py           # Async Vault REST client
├── bom_purchasing.py           # Purchasing-sheet generation engine
├── mfg_package.py              # Manufacturing-order package builder engine (PDF + STEP + Excel BOM)
├── publish_bom.py              # BOM → Vault publish-job engine (queues PDF/STEP for Make parts)
├── pdf_watermark.py            # PDF watermark helper (RELEASED / FOR REVIEW overlays)
├── file_property_rules.json    # Property-compliance rules for FILES (used by Property Check)
├── item_property_rules.json    # Property-compliance rules for ITEMS (used by readiness reports)
├── config.json.example         # Template (committed) — copy to config.json
├── config.json                 # Live credentials (gitignored)
├── requirements.txt
├── vault_openapi.yml           # Reference: Vault REST API v2 OpenAPI spec (~180 KB)
│
├── gui/                        # Tk GUI front-ends (driven by ``app.py --gui`` / ``--workflow``)
│   ├── __init__.py
│   ├── launcher.py             # Vault Integration launcher dashboard
│   ├── release_workflow.py     # Release Workflow wizard (compliance → sync → release)
│   ├── purchasing.py           # Purchasing-sheet GUI
│   ├── file_property_check.py  # Property Check GUI (file name → compliance report)
│   ├── mfg_package.py          # Manufacturing Package builder GUI
│   ├── publish_bom.py          # BOM → Publish Deliverables GUI (scan, then queue jobs)
│
└── scripts/                    # Helpers and CLI tools used by the GUIs / for one-offs
    ├── vault_sdk.py            # Python wrapper around the Vault .NET SDK (via PowerShell bridge)
    ├── vault_sdk.ps1           # PowerShell .NET SDK bridge (sign-in, lifecycle, property writes)
    ├── vault_soap.py           # Direct legacy SOAP client (used by some workflow steps)
    ├── check_file_properties.py # Property Check — file name in, compliance report out
    ├── check_item_properties.py # Item-side compliance engine (backs the readiness report)
    ├── inventor_automation.py  # Inventor COM automation (open + rebuild + save)
    ├── release_workflow.py     # CLI release workflow (also reachable via ``--gui`` / ``--workflow``)
    └── probes/                 # One-off diagnostic / probe scripts (not part of the server)
        ├── probe_edit_items.ps1            # Reproduce + diagnose EditItems failures
        ├── probe_edit_items_authflags.ps1  # Sweep AuthenticationFlags combos against EditItems
        ├── probe_create_item.ps1           # Create a fresh test item via SDK
        ├── probe_delete_item.ps1           # Delete a test item via SDK
        ├── probe_jobs.py                   # Probe REST job-submission shapes
        ├── probe_pdf_job.py                # Probe PDF publish-job param keys
        ├── probe_vault_sdk.{py,ps1}        # SDK sign-in / read-only probes
        ├── probe_vault_soap.py             # Legacy SOAP probes (ticket extract, lifecycle)
        ├── setup_explorer_trace.ps1        # Capture Vault Explorer SOAP traffic via WCF tracing
        ├── test_license.ps1                # Verify licensing DLL discovery + sign-in
        └── ... (other one-offs)
```

CLI workflows and probes each read `config.json` from the project root, so run them from anywhere:

```bash
python scripts/release_workflow.py SF-001702
python scripts/probes/probe_jobs.py
powershell scripts/probes/setup_explorer_trace.ps1 -Mode Enable
```

## Logs

Runtime logs are written to `Log/mcp_server.log` (rotating, max 5 MB × 5 files). In SSE mode, logs also print to the console. In stdio mode, only the file is written (stdout is reserved for the MCP protocol). The `Log/` directory is gitignored.
