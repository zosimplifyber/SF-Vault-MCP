'use strict';
const test = require('node:test');
const assert = require('node:assert');
const http = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createRelay, DOWN_MESSAGE } = require('../server/index.js');

function fakeServer(handler) {
  const seen = [];
  const srv = http.createServer((req, res) => {
    let b = '';
    req.on('data', (c) => (b += c));
    req.on('end', () => {
      const rec = { headers: req.headers, body: b ? JSON.parse(b) : null };
      seen.push(rec);
      handler(rec, res);
    });
  });
  return new Promise((r) => srv.listen(0, '127.0.0.1', () =>
    r({ srv, seen, url: `http://127.0.0.1:${srv.address().port}/mcp` })));
}
const json = (res, obj, extra = {}) => {
  res.writeHead(200, { 'Content-Type': 'application/json', ...extra });
  res.end(JSON.stringify(obj));
};
function tmpToken(v) {
  const f = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'sfvb-')), 'token');
  fs.writeFileSync(f, v + '\n');
  return f;
}
function make(url, tokenFile) {
  const out = [];
  const relay = createRelay({ url, tokenFile, write: (o) => out.push(o), log: () => {} });
  return { relay, out };
}

test('round trip sends required headers and writes response', async () => {
  const s = await fakeServer((rec, res) => json(res, { jsonrpc: '2.0', id: rec.body.id, result: { ok: 1 } }));
  const { relay, out } = make(s.url, tmpToken('tok1'));
  await relay.push('{"jsonrpc":"2.0","id":1,"method":"tools/list"}');
  s.srv.close();
  assert.deepStrictEqual(out, [{ jsonrpc: '2.0', id: 1, result: { ok: 1 } }]);
  const h = s.seen[0].headers;
  assert.strictEqual(h['content-type'], 'application/json');
  assert.strictEqual(h['accept'], 'application/json, text/event-stream');
  assert.strictEqual(h['x-bridge-token'], 'tok1');
});

test('session id from initialize is carried', async () => {
  const s = await fakeServer((rec, res) => json(res, { jsonrpc: '2.0', id: rec.body.id, result: {} },
    rec.body.method === 'initialize' ? { 'Mcp-Session-Id': 'sess-9' } : {}));
  const { relay } = make(s.url, tmpToken('t'));
  await relay.push('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}');
  await relay.push('{"jsonrpc":"2.0","id":2,"method":"tools/list"}');
  s.srv.close();
  assert.strictEqual(s.seen[0].headers['mcp-session-id'], undefined);
  assert.strictEqual(s.seen[1].headers['mcp-session-id'], 'sess-9');
});

test('401 re-reads the token and retries', async () => {
  const f = tmpToken('old');
  const s = await fakeServer((rec, res) => {
    if (rec.headers['x-bridge-token'] !== 'new') { res.writeHead(401); res.end(); return; }
    json(res, { jsonrpc: '2.0', id: rec.body.id, result: { ok: true } });
  });
  const { relay, out } = make(s.url, f);
  fs.writeFileSync(f, 'new\n');
  await relay.push('{"jsonrpc":"2.0","id":5,"method":"tools/list"}');
  s.srv.close();
  assert.strictEqual(s.seen.length, 2);
  assert.strictEqual(s.seen[1].headers['x-bridge-token'], 'new');
  assert.strictEqual(out[0].result.ok, true);
});

test('notifications are forwarded and nothing is written back', async () => {
  const s = await fakeServer((rec, res) => { res.writeHead(202); res.end(); });
  const { relay, out } = make(s.url, tmpToken('t'));
  await relay.push('{"jsonrpc":"2.0","method":"notifications/initialized"}');
  s.srv.close();
  assert.strictEqual(s.seen.length, 1);
  assert.strictEqual(out.length, 0);
});

test('text/event-stream responses are parsed', async () => {
  const s = await fakeServer((rec, res) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    res.end(`event: message\ndata: ${JSON.stringify({ jsonrpc: '2.0', id: rec.body.id, result: { s: 1 } })}\n\n`);
  });
  const { relay, out } = make(s.url, tmpToken('t'));
  await relay.push('{"jsonrpc":"2.0","id":3,"method":"tools/list"}');
  s.srv.close();
  assert.deepStrictEqual(out, [{ jsonrpc: '2.0', id: 3, result: { s: 1 } }]);
});

test('bridge down answers locally', async () => {
  const s = await fakeServer(() => {});
  const url = s.url;
  await new Promise((r) => s.srv.close(r));
  const { relay, out } = make(url, tmpToken('t'));
  await relay.push('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}');
  await relay.push('{"jsonrpc":"2.0","method":"notifications/initialized"}');
  await relay.push('{"jsonrpc":"2.0","id":2,"method":"tools/list"}');
  await relay.push('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"x"}}');
  assert.strictEqual(out.length, 3);
  assert.strictEqual(out[0].result.serverInfo.name, 'sf-vault-bridge-relay');
  assert.deepStrictEqual(out[1].result.tools, []);
  assert.strictEqual(out[2].result.isError, true);
  assert.strictEqual(out[2].result.content[0].text, DOWN_MESSAGE);
});
