# SF Vault Bridge Claude Desktop extension

A small MCPB extension. Claude Desktop runs `server/index.js` with its built-in Node. The script reads
newline-delimited JSON-RPC on stdin and POSTs each message to the SF Vault Bridge add-in at
`http://localhost:8790/mcp`. The add-in serves the tools through the engineer's own Inventor and Vault session.

- Token: `%APPDATA%\Simplifyber\VaultBridge\token`, sent as `X-Bridge-Token`, re-read on a 401 (one retry).
- `Mcp-Session-Id` from the initialize response is sent on later calls.
- Notifications are forwarded; nothing is written back for them.
- JSON and `text/event-stream` responses are both handled.
- Bridge down: the relay answers `initialize`, returns an empty `tools/list` (logged to stderr), and returns
  `isError` for `tools/call` with "Start Inventor and sign in to Vault; the SF Vault Bridge add-in serves these tools."
- Diagnostics go to stderr only. No npm dependencies (Node 18+ built-ins).
- Test overrides: `SF_BRIDGE_URL`, `SF_BRIDGE_TOKEN_FILE`.

## Manifest

`manifest_version` used: **0.3**. Source: Anthropic MCP Bundle spec, `MANIFEST.md` in
https://github.com/anthropics/mcpb (checked 2026-10-14). Server type `node`, `entry_point` `server/index.js`,
`mcp_config` command `node` with `${__dirname}/server/index.js`. The static `tools` list mirrors the bridge's
planned tools and is marked `tools_generated`; keep it in step with the add-in's `tools/list`.

## Build and test

```
powershell -File claude-extension\build.ps1 [-Version 1.2.3]   # dist\SF Vault Bridge.mcpb
node --test "claude-extension/test/*.test.js"   # Node 24 does not accept a bare directory
```

`-Version` defaults to `..\inventor-addin\VERSION`, else `1.0.0`. Build uses `npx --no-install @anthropic-ai/mcpb pack`
when installed, else zips manifest.json plus `server/` with .NET ZipFile. `dist/` is not committed.
