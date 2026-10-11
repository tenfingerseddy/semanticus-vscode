// W1 (join 11, drafts fix round 2): a published (live-bound) model keeps its typed, unsaved words across an engine restart.
//
// The key a draft is held under used to be `source|liveDatabase`. For a live open `source` is the engine's snapshot, a NEW folder on every open
// (LiveModelExport, Guid.NewGuid), so after a restart the key was different and every draft was orphaned. This file drives the REAL restart path
// (reopenLastModel with restarted: true, extracted from src/extension.ts by the reopen bench) and gives the sessions it produces, WITH a `source`
// (the harness's name: fallback hid the defect), to both key functions: the page's (webview/src/drafts.ts) and the Properties grid host's (src/draftKey.ts).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeBench, LIVE_TARGET, liveSession } from './reopen-behaviour.mjs';
import { makeDisk, makePage } from './helpers/drafts-mount.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ts = createRequire(root + '/package.json')('typescript');
const hostKey = (() => {
  const js = ts.transpileModule(readFileSync(resolve(root, 'src', 'draftKey.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText;
  const m = { exports: {} };
  new Function('module', 'exports', js)(m, m.exports);
  return m.exports.draftModelKeyOf;
})();
const disk = makeDisk();
const pageKey = (s) => makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState } }).drafts.modelKeyOfSession(s);
const both = (s) => { const a = pageKey(s), b = hostKey(s); assert.equal(a, b, 'the page and the Properties grid host key one model the same way'); return a; };

// What the engine reports after an open: a live model comes from a fresh snapshot folder every time.
let opens = 0;
const freshLive = (over = {}) => liveSession({ sessionId: 'live-' + (++opens), source: `/fixture/semanticus-live/${(0x3f9a1c07 + opens * 7919).toString(16)}/Contoso.bim`, ...over });

test('W1 the real restart path (reopenLastModel restarted=true): the same published model is the same key before and after', async () => {
  const bench = makeBench({ pathExists: () => false, sessionAfterOpen: () => freshLive() });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });
  const before = bench.sandbox.currentSession;
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });   // the engine restarts again
  const after = bench.sandbox.currentSession;
  assert.deepEqual(bench.openCalls().map((c) => c.method), ['openLive', 'openLive'], 'a live model reopens through openLive');
  assert.notEqual(before.source, after.source, 'precondition: the engine made a new snapshot folder (the source moved)');
  assert.ok(before.source && after.source, 'precondition: the sessions carry a source (no name: fallback)');
  assert.equal(both(after), both(before), 'one published model, one draft key, whatever the snapshot folder was');
});

test('W1 a different database or endpoint is another model', () => {
  const a = both(freshLive()), db = both(freshLive({ liveDatabase: 'Other' })), ep = both(freshLive({ liveEndpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Other' }));
  assert.notEqual(a, db); assert.notEqual(a, ep); assert.notEqual(db, ep);
  assert.equal(both(freshLive({ liveEndpoint: ' ' + LIVE_TARGET.endpoint + ' ' })), a, 'stray spaces around the endpoint do not make another model');
});

test('W1 a local file is its real path; a live model does not use its snapshot path', () => {
  assert.equal(both({ sessionId: 's1', modelName: 'M', source: '/home/p/Models/Sales.bim' }), '/home/p/Models/Sales.bim');
  assert.equal(both({ sessionId: 's2', modelName: 'M', source: '/home/p/Models/Sales.bim', liveBound: false, liveEndpoint: 'x', liveDatabase: 'y' }), '/home/p/Models/Sales.bim', 'a file session that only carries leftover live fields is still its file');
  assert.ok(!both(freshLive()).includes('semanticus-live'), 'the snapshot folder is not part of a published model\'s key');
  assert.equal(both({ sessionId: 's3', modelName: 'Unsaved' }), 'name:Unsaved', 'an unsaved model of one name is still keyed by its name (a noted limit)');
  assert.equal(both(null), ''); assert.equal(both(undefined), '');
});
