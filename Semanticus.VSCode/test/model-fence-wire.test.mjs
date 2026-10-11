// drafts-sol round 2, C1, the extension's half: a request made for a named model carries `modelIdentity` as one top-level member of the JSON-RPC message, on that
// request and on no other. Real vscode-jsonrpc over in-memory streams, and the raw bytes it writes are read, so this is the wire and not a mock of it.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { PassThrough } from 'node:stream';
import test from 'node:test';

const require = createRequire(import.meta.url);
const { createMessageConnection, StreamMessageReader, StreamMessageWriter, ParameterStructures } = require('vscode-jsonrpc/node');
const { FencedMessageWriter, withModelIdentity, fenceIdentity, MODEL_IDENTITY_MEMBER } = require('../out/modelFence.js');

function client() {
  const toEngine = new PassThrough(); const fromEngine = new PassThrough();
  const wire = [];
  let pending = '';
  toEngine.on('data', (chunk) => {
    pending += chunk.toString('utf8');
    for (;;) {   // header-delimited messages
      const head = pending.indexOf('\r\n\r\n'); if (head < 0) return;
      const length = Number(/Content-Length:\s*(\d+)/i.exec(pending.slice(0, head))[1]);
      const body = Buffer.from(pending.slice(head + 4)).subarray(0, length);
      if (body.length < length) return;
      wire.push(JSON.parse(body.toString('utf8')));
      pending = Buffer.from(pending.slice(head + 4)).subarray(length).toString('utf8');
    }
  });
  const conn = createMessageConnection(new StreamMessageReader(fromEngine), new FencedMessageWriter(new StreamMessageWriter(toEngine)));
  conn.listen();
  return { conn, wire, settle: () => new Promise((r) => setTimeout(r, 30)) };
}

test('a request sent inside withModelIdentity carries the model on the wire, as a top-level member beside method and params', async () => {
  const c = client();
  void withModelIdentity('file:/m/A.bim', () => c.conn.sendRequest('setAiInstructions', 'words', null, 'human', ''));
  await c.settle();
  assert.equal(c.wire.length, 1);
  assert.equal(c.wire[0][MODEL_IDENTITY_MEMBER], 'file:/m/A.bim');
  assert.equal(c.wire[0].method, 'setAiInstructions');
  assert.deepEqual(c.wire[0].params, ['words', null, 'human', ''], 'the parameters are untouched: no method signature carries the identity');
});

test('a request sent outside it carries nothing, and the name never leaks to the request after it', async () => {
  const c = client();
  void c.conn.sendRequest('a');
  void withModelIdentity('live:endpoint|db', () => c.conn.sendRequest('b', ParameterStructures.byPosition, 1, 2));
  void c.conn.sendRequest('c');
  void withModelIdentity('desktop:sales|localhost:1|guid', () => c.conn.sendRequest('d'));
  void c.conn.sendRequest('e');
  await c.settle();
  const named = Object.fromEntries(c.wire.map((m) => [m.method, m[MODEL_IDENTITY_MEMBER]]));
  assert.deepEqual(named, { a: undefined, b: 'live:endpoint|db', c: undefined, d: 'desktop:sales|localhost:1|guid', e: undefined });
});

test('a notification is not stamped, and a throwing send restores the state', async () => {
  const c = client();
  withModelIdentity('file:/m/A.bim', () => c.conn.sendNotification('note'));
  assert.throws(() => withModelIdentity('file:/m/A.bim', () => { throw new Error('boom'); }), /boom/);
  void c.conn.sendRequest('after');
  await c.settle();
  assert.equal(c.wire.find((m) => m.method === 'note')[MODEL_IDENTITY_MEMBER], undefined);
  assert.equal(c.wire.find((m) => m.method === 'after')[MODEL_IDENTITY_MEMBER], undefined);
});

test('only an identity in the engine\'s own shape is named: a fallback key a page built for itself would make the engine refuse every write', () => {
  for (const ok of ['file:/m/A.bim', 'live:e|d', 'desktop:s|e|d', 'unsaved:abc']) assert.equal(fenceIdentity(ok), ok);
  for (const no of ['', undefined, null, 'name:Sales', '/m/A.bim', 'C:\\m\\A.bim', 7]) assert.equal(fenceIdentity(no), undefined);
  const c = client();
  void withModelIdentity('name:Sales', () => c.conn.sendRequest('x'));
  return c.settle().then(() => assert.equal(c.wire[0][MODEL_IDENTITY_MEMBER], undefined));
});

// ---- the wiring: every door a page's write goes through names its model ----------------------------------------------------------------------------------------
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const extension = readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'extension.ts'), 'utf8');

test('the engine connection stamps the model on the wire, and the Studio relay, the Properties grid and the schema panel each name theirs', () => {
  assert.match(extension, /createMessageConnection\(new StreamMessageReader\(socket\), new FencedMessageWriter\(new StreamMessageWriter\(socket\)\)\)/, 'the person\'s connection writes through the fence');
  assert.match(extension, /const named = msg\.modelIdentity;[\s\S]{0,400}withModelIdentity\(named, \(\) => caller\.sendRequest\(msg\.method, ParameterStructures\.byPosition, \.\.\.params\)\)/, 'every request a Studio page relays carries the page\'s model');
  assert.match(extension, /withModelIdentity\(named, \(\) => caller\.sendRequest\(msg\.method\)\)/, '...with or without parameters');
  assert.match(extension, /withModelIdentity\(msg\.model, \(\) => conn\.sendRequest<SetResult \| undefined>\(call\.method, \.\.\.call\.args\)\)/, 'the Properties grid names the model its words were typed on');
  assert.match(extension, /withModelIdentity\(openedOn, \(\) => conn!\.sendRequest\('applySchemaUpdate'/, 'the Update Schema panel names the model it was opened on');
  // no other relay of a page's request may skip it: the only `msg.method` sends are the two above
  assert.equal((extension.match(/sendRequest\(msg\.method/g) ?? []).length, 2);
});
