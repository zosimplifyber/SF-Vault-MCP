# SF Vault Bridge Claude Desktop extension

A small MCPB extension. Claude Desktop runs `server/SFVaultBridgeRelay.exe` (a .NET 8 console relay in `relay/`, framework-dependent, so it needs the .NET 8 runtime that Inventor 2026 installs). It reads
newline-delimited JSON-RPC on stdin and POSTs each message to the SF Vault Bridge add-in at
`http://localhost:8790/mcp`. The add-in serves the tools through the engineer's own Inventor and Vault session.

- Token: `%APPDATA%\Simplifyber\VaultBridge\token`, sent as `X-Bridge-Token`, re-read on a 401 (one retry).
- `Mcp-Session-Id` from the initialize response is sent on later calls.
- Notifications are forwarded; nothing is written back for them.
- JSON and `text/event-stream` responses are both handled.
- Bridge down: the relay answers `initialize`, returns an empty `tools/list` (logged to stderr), and returns
  `isError` for `tools/call` with "Start Inventor and sign in to Vault; the SF Vault Bridge add-in serves these tools."
- `server/discover` is answered locally with -32601 and never forwarded.
- Diagnostics go to stderr only. Test overrides: `SFVB_MCP_URL`, `SFVB_TOKEN_FILE`.
- `node-relay/` is the original Node relay, kept for reference only (Claude Desktop's built-in Node never ran it on Zak's PC). It is not packed.

## Manifest

`manifest_version` used: **0.3**. Source: Anthropic MCP Bundle spec, `MANIFEST.md` in
https://github.com/anthropics/mcpb (checked 2026-10-14). Server type `binary`, `entry_point` `server/SFVaultBridgeRelay.exe`,
`mcp_config` command `${__dirname}/server/SFVaultBridgeRelay.exe`, args `[]` (spec shows `server/my-server` for binaries; `${__dirname}` works in any command). The static `tools` list mirrors the bridge's
planned tools and is marked `tools_generated`; keep it in step with the add-in's `tools/list`.

## Build and test

```
powershell -File claude-extension\build.ps1 [-Version 1.2.3]   # dist\SF Vault Bridge.mcpb
dotnet test claude-extensionelay.sln
```

`-Version` defaults to `..\inventor-addin\VERSION`, else `1.0.0`. Build uses `npx --no-install @anthropic-ai/mcpb pack`
when installed, else zips manifest.json plus `server/` with .NET ZipFile. The relay is published into the staging folder, not the repo. `dist/` is not committed.
