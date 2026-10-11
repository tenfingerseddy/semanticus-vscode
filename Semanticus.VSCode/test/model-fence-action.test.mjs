// drafts-sol round 3, J1: the model a person's action STARTED on is the model every request of that action names, however long the action waits (src/modelFence.ts).
// The check's T1: Rename, Delete and about ten more tree commands wait on a prompt and then send a write that named no model; when the assistant opened B in that time the
// write landed on B. The fix takes the name where the action starts and carries it by async context to every request it makes. Real vscode-jsonrpc over in-memory streams,
// and the raw bytes it writes are read, so this is the wire and not a mock of it.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { PassThrough } from 'node:stream';
import test from 'node:test';

const require = createRequire(import.meta.url);
const { createMessageConnection, StreamMessageReader, StreamMessageWriter } = require('vscode-jsonrpc/node');
const fence = require('../out/modelFence.js');
const { FencedMessageWriter, withModelIdentity, personAction, rebindAction, outsideAnyAction, MODEL_IDENTITY_MEMBER, NAMES_MODEL_MEMBER, MODEL_REFUSAL } = fence;

const A = 'file:/m/A.bim';
const B = 'file:/m/B.bim';

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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const named = (c, method) => c.wire.find((m) => m.method === method)?.[MODEL_IDENTITY_MEMBER];

test('J1: an action that waits on a prompt keeps the model it started on, though the host moved to B while the prompt was open', async () => {
  assert.equal(typeof personAction, 'function', 'src/modelFence.ts exports personAction');
  const c = client();
  const host = { identity: A };   // what the host shows: it moves to B the moment B opens
  await personAction([], host.identity, async () => {
    await sleep(10); host.identity = B;       // the person is in the prompt; the assistant opens B; the host is told
    await sleep(10);
    void c.conn.sendRequest('renameObject', 'measure:T/M', 'Revenue', 'human');   // the person confirms: the write goes out
  });
  await c.settle();
  assert.equal(named(c, 'renameObject'), A, 'the write names the model the action started on, not the one the host shows now');
});

test('J1: the name survives a timer, a promise chain and a Promise.all inside the action, and does not leak to a request made after it', async () => {
  const c = client();
  await personAction([], A, async () => {
    await Promise.all([sleep(5).then(() => { void c.conn.sendRequest('one'); }), new Promise((r) => setImmediate(r)).then(() => { void c.conn.sendRequest('two'); })]);   // (a request is not awaited: nothing answers it)
    await new Promise((r) => setTimeout(r, 5)); void c.conn.sendRequest('three');
  });
  void c.conn.sendRequest('after');
  await c.settle();
  for (const m of ['one', 'two', 'three']) assert.equal(named(c, m), A, m);
  assert.equal(named(c, 'after'), undefined, 'a request made after the action carries no model');
});

test('J1: a withModelIdentity(B) nested inside an action started on A still sends A (the earliest capture wins)', async () => {
  const c = client();
  await personAction([], A, async () => {
    await sleep(5);
    await withModelIdentity(B, () => { void c.conn.sendRequest('relayed'); });   // a page message relayed while the command is mid-flight
  });
  await c.settle();
  assert.equal(named(c, 'relayed'), A);
});

test('J1: rebindAction(B) is the one explicit exit: an action that opens B on purpose and then writes sends B', async () => {
  const c = client();
  await personAction([], A, async () => {
    void c.conn.sendRequest('beforeOpen');
    await rebindAction(B, async () => { await sleep(5); void c.conn.sendRequest('afterOpen'); });
    void c.conn.sendRequest('afterRebind');
  });
  await c.settle();
  assert.equal(named(c, 'beforeOpen'), A);
  assert.equal(named(c, 'afterOpen'), B);
  assert.equal(named(c, 'afterRebind'), A, 'the exit is scoped to the function it wraps');
});

test('J1: a tree node carries the model it was fetched for, and that wins over what the host shows at the click', async () => {
  const c = client();
  const node = { ref: 'measure:T/M', name: 'M', kind: 'measure', hasChildren: false, model: A };
  await personAction([node], B, async () => { void c.conn.sendRequest('fromMenu'); });   // a context menu left open on A's tree while the host moved to B
  await c.settle();
  assert.equal(named(c, 'fromMenu'), A);
  // a list of nodes (a multi-select) is read the same way
  await personAction([node, [node, { ...node, ref: 'measure:T/N' }]], B, async () => { void c.conn.sendRequest('fromSelection'); });
  await c.settle();
  assert.equal(named(c, 'fromSelection'), A);
  // no node model: the host's
  await personAction([{ ref: 'measure:T/M' }], B, async () => { void c.conn.sendRequest('hostOnly'); });
  await c.settle();
  assert.equal(named(c, 'hostOnly'), B);
});

test('J1: nodes from two different models refuse the whole action and send nothing', async () => {
  const c = client();
  const refused = [];
  let ran = false;
  await personAction([{ ref: 'measure:T/M', model: A }, { ref: 'measure:T/N', model: B }], A, async () => { ran = true; void c.conn.sendRequest('never'); }, (m) => refused.push(m));
  await personAction([[{ ref: 'measure:T/M', model: A }, { ref: 'measure:T/N', model: B }]], A, async () => { ran = true; void c.conn.sendRequest('never'); }, (m) => refused.push(m));
  await c.settle();
  assert.equal(ran, false, 'the handler never ran');
  assert.equal(c.wire.length, 0, 'nothing was sent');
  assert.deepEqual(refused, [MODEL_REFUSAL, MODEL_REFUSAL], 'the person is told in the engine\'s own sentence');
});

test('J1: outsideAnyAction starts something with no model: a request made from it names none (an engine pipe must not carry the action that started it)', async () => {
  const c = client();
  await personAction([], A, async () => {
    await outsideAnyAction(async () => { await sleep(5); void c.conn.sendRequest('longLived'); });
    void c.conn.sendRequest('inside');
  });
  await c.settle();
  assert.equal(named(c, 'longLived'), undefined);
  assert.equal(c.wire.find((m) => m.method === 'longLived')[NAMES_MODEL_MEMBER], true, 'it still says it names a model on every request, so the engine can tell it named none');
  assert.equal(named(c, 'inside'), A);
});

test('J1: every request carries namesModel, a notification carries neither member, and a page relay outside any action still names its page\'s model', async () => {
  const c = client();
  void c.conn.sendRequest('plain');
  void withModelIdentity(A, () => c.conn.sendRequest('page'));
  c.conn.sendNotification('note');
  await personAction([], A, async () => { c.conn.sendNotification('inAction'); });
  await c.settle();
  assert.equal(c.wire.find((m) => m.method === 'plain')[NAMES_MODEL_MEMBER], true);
  assert.equal(c.wire.find((m) => m.method === 'plain')[MODEL_IDENTITY_MEMBER], undefined);
  assert.equal(c.wire.find((m) => m.method === 'page')[NAMES_MODEL_MEMBER], true);
  assert.equal(named(c, 'page'), A);
  for (const m of ['note', 'inAction']) {
    const msg = c.wire.find((x) => x.method === m);
    assert.equal(msg[NAMES_MODEL_MEMBER], undefined, m + ' is a notification, not a write');
    assert.equal(msg[MODEL_IDENTITY_MEMBER], undefined);
  }
});

test('J1: an action started on a model the engine does not name (no model open, a fallback key) names none, and an inner personAction runs inside the outer scope', async () => {
  const c = client();
  await personAction([], undefined, async () => { void c.conn.sendRequest('noModel'); });
  await personAction([], 'name:Sales', async () => { void c.conn.sendRequest('fallbackKey'); });
  await personAction([], A, async () => {
    await personAction([], B, async () => { void c.conn.sendRequest('nested'); });   // a command that runs another command
  });
  await c.settle();
  assert.equal(named(c, 'noModel'), undefined);
  assert.equal(named(c, 'fallbackKey'), undefined);
  assert.equal(named(c, 'nested'), A);
});

test('J1: the handler\'s result and its error come back through personAction unchanged', async () => {
  assert.equal(await personAction([], A, async () => 42), 42);
  await assert.rejects(personAction([], A, async () => { throw new Error('boom'); }), /boom/);
  assert.equal(personAction([], A, () => 'sync'), 'sync');
});
