// Round 3 (join 11, drafts): a client reads WHICH MODEL a session is from the engine, verbatim (SessionInfo.modelIdentity, LocalEngine.ModelIdentityOf).
// The C# proof is Semanticus.Tests/DraftModelIdentityTests.cs (the pure function, two snapshot folders of one published model, a cold start that opens the cached
// snapshot as a file, a Desktop model across a restart, two unsaved models). This file proves the three consumers use the field and nothing else:
// the page (webview/src/drafts.ts), the Properties grid host (src/draftKey.ts) and the places that hand the key over (App.tsx, extension.ts).
// When the engine sends no field (an engine from before it) the older derivation is the fallback; V1 (verify-drafts-check.test.mjs) runs on that path.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
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

test('the engine\'s modelIdentity is the key, verbatim, whatever the other fields say', () => {
  // A path that rotates and a session id that restarts: neither is read once the engine names the model.
  const a = { sessionId: 's1', modelName: 'Sales', source: '/tmp/semanticus-live/aaaa1111/Sales.bim', liveBound: true, liveEndpoint: 'localhost:51234', liveDatabase: 'guid-one', modelIdentity: 'desktop:contoso sales|localhost:51234|guid-one' };
  const again = { sessionId: 's1', modelName: 'Sales', source: '/tmp/semanticus-live/bbbb2222/Sales.bim', liveBound: true, liveEndpoint: 'localhost:51234', liveDatabase: 'guid-one', modelIdentity: 'desktop:contoso sales|localhost:51234|guid-one' };
  assert.equal(both(a), 'desktop:contoso sales|localhost:51234|guid-one');
  assert.equal(both(a), both(again), 'a Desktop model across an engine restart (the same instance, a new snapshot folder) is one key');
});

test('F2 (drafts-sol): a Desktop model on another instance is another key, even with the same window stem (West/Sales.pbix and East/Sales.pbix)', () => {
  // The round-3 rule let the stem stand for the model; Sol (join 10) showed two files share one. Words typed before Desktop started again are kept read-only.
  const west = { sessionId: 's1', modelName: 'Sales', modelIdentity: 'desktop:sales|localhost:51234|guid-west' };
  const east = { sessionId: 's2', modelName: 'Sales', modelIdentity: 'desktop:sales|localhost:60321|guid-east' };
  assert.notEqual(both(west), both(east));
});

test('two different models are two keys even when every other field is the same (the session id restarts at s1 in every engine process)', () => {
  const one = { sessionId: 's1', modelName: 'Untitled', modelIdentity: 'unsaved:11111111111111111111111111111111' };
  const two = { sessionId: 's1', modelName: 'Untitled', modelIdentity: 'unsaved:22222222222222222222222222222222' };
  assert.notEqual(both(one), both(two));
});

test('a file session that is the cached snapshot of a published model has the key it had while it was open live (the cold start)', () => {
  const live = { sessionId: 's1', modelName: 'Sales', source: '/tmp/semanticus-live/aaaa1111/Sales.bim', liveBound: true, liveEndpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Finance', liveDatabase: 'Sales', modelIdentity: 'live:powerbi://api.powerbi.com/v1.0/myorg/finance|sales' };
  const cold = { sessionId: 's1', modelName: 'Sales', source: '/tmp/semanticus-live/aaaa1111/Sales.bim', liveBound: false, modelIdentity: 'live:powerbi://api.powerbi.com/v1.0/myorg/finance|sales' };
  assert.equal(both(cold), both(live));
});

test('no field from the engine (version skew): the older derivation applies', () => {
  assert.equal(both({ modelName: 'M', source: '/m/Sales.bim' }), '/m/Sales.bim');
  assert.equal(both({ modelName: 'M', source: '/tmp/x/Sales.bim', liveBound: true, liveEndpoint: ' ep ', liveDatabase: 'db' }), 'live:ep|db');
  assert.equal(both({ modelName: 'M' }), 'name:M');
  assert.equal(both({ modelName: 'M', modelIdentity: '' , source: '/m/a.bim' }), '/m/a.bim', 'an empty field is no field');
  assert.equal(both(null), '');
});

test('the places that hand the key over use the one function and nothing else', () => {
  const app = readFileSync(resolve(root, 'webview', 'src', 'App.tsx'), 'utf8');
  assert.match(app, /setDraftModel\(modelKeyOfSession\(session\), session\.modelLabel(, session\.sessionId)?\)/, 'App.tsx files the open model under the engine\'s identity and names it');
  const ext = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
  assert.match(ext, /model: draftModelKeyOf\(info\)/, 'the Properties grid load message carries the engine\'s identity');
  assert.match(ext, /modelIdentity\?: string; modelLabel\?: string/, 'the extension\'s SessionInfo carries the field');
  const grid = readFileSync(resolve(root, 'media', 'propgrid', 'propgrid.js'), 'utf8');
  assert.match(grid, /const model = typeof m\.model === 'string' \? m\.model : ''/, 'the grid uses the string it was sent');
  const conn = readFileSync(resolve(root, 'webview', 'src', 'connection.tsx'), 'utf8');
  assert.match(conn, /modelIdentity\?: string;/);
  // Neither key function reads the snapshot path or the session id when the engine has named the model.
  const keyFns = readFileSync(resolve(root, 'src', 'draftKey.ts'), 'utf8');
  assert.ok(keyFns.indexOf('s.modelIdentity') < keyFns.indexOf('s.source'), 'the engine\'s field is read before the older fields');
});
