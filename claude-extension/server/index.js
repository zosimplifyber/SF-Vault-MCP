#!/usr/bin/env node
'use strict';
// SF Vault Bridge relay: stdio MCP (newline-delimited JSON-RPC) -> HTTP POST to the
// add-in's MCP endpoint. Node 18+ built-ins only. Diagnostics go to stderr only.

const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');

const DOWN_MESSAGE = 'Start Inventor and sign in to Vault; the SF Vault Bridge add-in serves these tools.';
const PROTOCOL_VERSION = '2025-06-18';

function defaultTokenFile() {
  const base = process.env.APPDATA || path.join(process.env.USERPROFILE || '.', 'AppData', 'Roaming');
  return path.join(base, 'Simplifyber', 'VaultBridge', 'token');
}

function createRelay(opts = {}) {
  const url = opts.url || process.env.SF_BRIDGE_URL || 'http://localhost:8790/mcp';
  const tokenFile = opts.tokenFile || process.env.SF_BRIDGE_TOKEN_FILE || defaultTokenFile();
  const write = opts.write || ((obj) => process.stdout.write(JSON.stringify(obj) + '\n'));
  const log = opts.log || ((m) => process.stderr.write(`[sf-vault-bridge-relay] ${m}\n`));
  let token = null;
  let sessionId = null;

  function readToken() {
    try {
      token = fs.readFileSync(tokenFile, 'utf8').trim() || null;
    } catch (e) {
      token = null;
      log(`token not readable at ${tokenFile}: ${e.code || e.message}`);
    }
    return token;
  }

  async function post(body) {
    const headers = {
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
    };
    if (token) headers['X-Bridge-Token'] = token;
    if (sessionId) headers['Mcp-Session-Id'] = sessionId;
    return fetch(url, { method: 'POST', headers, body });
  }

  async function parseResponse(res) {
    const text = await res.text();
    const ctype = (res.headers.get('content-type') || '').toLowerCase();
    const out = [];
    if (ctype.includes('text/event-stream')) {
      for (const line of text.split(/\r?\n/)) {
        if (line.startsWith('data:')) {
          const d = line.slice(5).trim();
          if (d) {
            try { out.push(JSON.parse(d)); } catch { log('bad SSE data line'); }
          }
        }
      }
    } else if (text.trim()) {
      try {
        const j = JSON.parse(text);
        if (Array.isArray(j)) out.push(...j); else out.push(j);
      } catch { log('non-JSON response body'); }
    }
    return out;
  }

  function localAnswer(msg) {
    const id = msg.id;
    switch (msg.method) {
      case 'initialize':
        return {
          jsonrpc: '2.0', id, result: {
            protocolVersion: (msg.params && msg.params.protocolVersion) || PROTOCOL_VERSION,
            capabilities: { tools: {} },
            serverInfo: { name: 'sf-vault-bridge-relay', version: opts.version || '1.0.0' },
          },
        };
      case 'tools/list':
        log('bridge unreachable: returning an empty tool list. ' + DOWN_MESSAGE);
        return { jsonrpc: '2.0', id, result: { tools: [] } };
      case 'tools/call':
        return { jsonrpc: '2.0', id, result: { content: [{ type: 'text', text: DOWN_MESSAGE }], isError: true } };
      case 'ping':
        return { jsonrpc: '2.0', id, result: {} };
      default:
        return { jsonrpc: '2.0', id, error: { code: -32000, message: DOWN_MESSAGE } };
    }
  }

  async function handle(line) {
    let msg;
    try { msg = JSON.parse(line); } catch {
      log('ignoring unparseable stdin line');
      return;
    }
    const isNotification = msg.id === undefined || msg.id === null;
    const body = JSON.stringify(msg);
    let res;
    try {
      res = await post(body);
      if (res.status === 401) {
        await res.text().catch(() => {});
        readToken();
        res = await post(body);
      }
    } catch (e) {
      log(`bridge request failed: ${(e.cause && e.cause.code) || e.message}`);
      if (!isNotification) write(localAnswer(msg));
      return;
    }
    const sid = res.headers.get('mcp-session-id');
    if (sid && msg.method === 'initialize') sessionId = sid;
    if (isNotification) {
      await res.text().catch(() => {});
      return;
    }
    if (res.status === 401 || res.status === 403) {
      log(`bridge rejected the token (HTTP ${res.status})`);
      await res.text().catch(() => {});
      write({
        jsonrpc: '2.0', id: msg.id,
        error: { code: -32001, message: `SF Vault Bridge refused the token (HTTP ${res.status}). Restart Inventor so the add-in rewrites it.` },
      });
      return;
    }
    const msgs = await parseResponse(res);
    if (!msgs.length) {
      log(`empty response (HTTP ${res.status})`);
      write({ jsonrpc: '2.0', id: msg.id, error: { code: -32603, message: `SF Vault Bridge returned HTTP ${res.status} with no body.` } });
      return;
    }
    for (const m of msgs) write(m);
  }

  readToken();
  let chain = Promise.resolve();
  return {
    push(line) {
      if (!line.trim()) return chain;
      chain = chain.then(() => handle(line)).catch((e) => log(`internal error: ${e.message}`));
      return chain;
    },
    idle: () => chain,
  };
}

function main() {
  let version = '1.0.0';
  try { version = require('../manifest.json').version; } catch { /* ignore */ }
  const relay = createRelay({ version });
  const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  rl.on('line', (l) => relay.push(l));
  rl.on('close', () => relay.idle().then(() => process.exit(0)));
}

if (require.main === module) main();
module.exports = { createRelay, DOWN_MESSAGE };
