// Join 11 solui, U2 sweep (rule R-B): the other boxes of the same shape as the Spec name box. keptwords.ts is the shared hook; the Plan row note and a draft's typed answer use it,
// a Primer decision no longer replaces notes open for editing, and a Visual to Query switch puts an edited query away instead of writing over it.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { makeDisk, makePage, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const src = (f) => readFileSync(new URL(`../webview/src/${f}`, import.meta.url), 'utf8');

function box(initial = 'one', key = 'k1') {
  const disk = makeDisk();
  const pg = makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState } });
  const { useKeptText } = pg.load('keptwords.ts');
  const s = { live: initial, key };
  const m = pg.mount(() => useKeptText(s.live, s.key));
  return { s, m, get: () => m.result, move: async (live, k) => { s.live = live; if (k !== undefined) s.key = k; m.rerender(); await settleAll(); m.rerender(); },
    type: async (t) => { m.result.setText(t); await settleAll(); } };
}

test('nothing typed: the box follows the live value', async () => {
  const b = box(); await b.move('two');
  assert.equal(b.get().text, 'two'); assert.equal(b.get().moved, null);
});

test('typed words stay when the live value moves, and the newer value is reported', async () => {
  const b = box(); await b.type('my words'); await b.move('theirs');
  assert.equal(b.get().text, 'my words');
  assert.equal(b.get().moved, 'theirs');
  b.get().useLive(); await settleAll(); b.m.rerender();
  assert.equal(b.get().text, 'theirs'); assert.equal(b.get().moved, null);
});

test('the live value becoming exactly the typed words ends the draft; another row or question starts a new one', async () => {
  const same = box(); await same.type('exact'); await same.move('exact');
  assert.equal(same.get().text, 'exact'); assert.equal(same.get().moved, null);
  const other = box(); await other.type('mine'); await other.move('b-note', 'k2');
  assert.equal(other.get().text, 'b-note', 'a different row is not this draft');
});

test('the sweep is wired: plan note and draft answer use the kept text, Primer decide spares open notes, Visual to Query puts an edited query away', () => {
  assert.match(src('plan-row.tsx'), /useKeptText\(item\.note \?\? '', item\.id(, \{ same: sameTrimmed \})?\)/);   // round 3: the engine trims a note
  assert.doesNotMatch(src('plan-row.tsx'), /useEffect\(\(\) => \{ setNote\(item\.note/);
  assert.match(src('composer-draft.tsx'), /useKeptText\(q\.answer \?\? '', q\.name(, \{ same: sameTrimmed[^)]*)?\)/);   // round 3: the engine trims an answer
  assert.doesNotMatch(src('composer-draft.tsx'), /useEffect\(\(\) => \{ setText\(q\.answer/);
  assert.match(src('knowledge.tsx'), /if \(!editingRef\.current\) setDraft\(result\.primer\.markdown/);
  assert.match(src('daxlab.tsx'), /function switchMode[^\n]*setPutBack\(query\)[^\n]*setQuery\(q\)/);
});
