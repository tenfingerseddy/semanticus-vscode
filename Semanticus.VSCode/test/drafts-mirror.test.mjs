// Round 3 (join 11, drafts): closing the Studio panel drops the webview's own state (VS Code serialises it only while the panel lives), and a panel cannot ask before it closes.
// So every change to the draft store is also handed to the extension host (workspaceState, like Author's workflowHeld), and a Studio page opened afresh reads the host's copy
// when its own state is empty. Proven here at the store and at the host's source; tools/uishot/drafts-drive.mjs scene `panel-close-reopen` drives the real page.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeDisk, makePage, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
function page({ disk = makeDisk(), kept = null, posted = [] } = {}) {
  const pg = makePage({ './bridge': { postToHost: (m) => { posted.push(m); }, keptDraftsByTheExtension: () => kept, loadState: disk.loadState, saveState: disk.saveState } }, 'file:/m/Sales.bim');
  return { pg, disk, posted, store: pg.drafts.draftStore };
}

test('a change is handed to the host as the whole store, and the last change always arrives (a short throttle, then a trailing post)', async () => {
  const p = page();
  p.store.set('file:/m/Sales.bim', 'instructions', '', { value: 'one', base: 'was' });
  assert.equal(p.posted.length, 1);
  assert.equal(p.posted[0].type, 'draftsHeld');
  assert.equal(Object.keys(p.posted[0].value).length, 1);
  p.store.set('file:/m/Sales.bim', 'instructions', '', { value: 'one two', base: 'was' });
  p.store.set('file:/m/Sales.bim', 'instructions', '', { value: 'one two three', base: 'was' });
  await wait(450);
  const last = p.posted.at(-1).value;
  assert.equal(Object.values(last)[0].value, 'one two three', 'the host\'s copy ends on the last words typed');
  assert.ok(p.posted.length <= 3, 'a burst of keystrokes is not one message each');
});

test('Discard of the last draft hands the host an empty store, so the words do not come back', async () => {
  const p = page();
  p.store.set('file:/m/Sales.bim', 'instructions', '', { value: 'one', base: 'was' });
  await wait(300);
  p.store.clear('file:/m/Sales.bim', 'instructions', '');
  await wait(300);
  assert.deepEqual(p.posted.at(-1).value, {});
});

test('a Studio page opened afresh (its own state is empty) gets the words back from the host\'s copy', async () => {
  const first = page();
  first.store.set('file:/m/Sales.bim', 'instructions', '', { value: 'typed before the panel closed', base: 'was', modelLabel: 'Sales' });
  await wait(300);
  const hostCopy = first.posted.at(-1).value;
  const reopened = page({ disk: makeDisk(), kept: hostCopy });   // a new webview: nothing in its own state
  const held = reopened.pg.drafts.heldDraft('instructions', '');
  assert.equal(held?.value, 'typed before the panel closed');
  assert.equal(held?.base, 'was');
});

test('the page\'s own state wins when it holds words (a reload), and a stray host value that is not a draft is ignored', async () => {
  const disk = makeDisk();
  const a = page({ disk });
  a.store.set('file:/m/Sales.bim', 'instructions', '', { value: 'own', base: 'was' });
  const stale = { [JSON.stringify(['file:/m/Sales.bim', 'instructions', ''])]: { value: 'host', base: 'was' }, junk: 'nope', other: { value: 1 } };
  const b = page({ disk, kept: stale });
  assert.equal(b.pg.drafts.heldDraft('instructions', '')?.value, 'own');
  const c = page({ disk: makeDisk(), kept: stale });
  assert.equal(c.pg.drafts.heldDraft('instructions', '')?.value, 'host');
  assert.equal(Object.keys(c.store.snapshot()).length, 1, 'only entries that are drafts are read');
});

test('the host keeps the copy for the workspace and gives it to the next Studio page it opens', () => {
  const ext = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
  assert.match(ext, /const DRAFTS_HELD_KEY = 'semanticus\.studioDrafts'/);
  assert.match(ext, /msg\?\.type === 'draftsHeld'\) \{ if \(!isStandalone && isPlainRecord\(msg\.value\)\) void extCtx\?\.workspaceState\.update\(DRAFTS_HELD_KEY, msg\.value\)/, 'it stores what the page hands over');
  assert.match(ext, /window\.__semanticusHeldDrafts=\$\{scriptJson\(heldDrafts\)\}/, 'and hands it to the Studio page it opens, escaped like the workflows copy');
  assert.match(ext, /const heldDrafts = initialView \? undefined : context\.workspaceState\.get\(DRAFTS_HELD_KEY\)/, 'the standalone Connections panel gets none');
  const bridge = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
  assert.match(bridge, /__semanticusHeldDrafts/);
  assert.match(bridge, /type: 'workflowHeld' \| 'draftsHeld'/);
});

// ---- Kane's fallback (5 Oct): built, not switched on ------------------------------------------------------------------------------
test('the fallback switch is OFF: words from the host\'s copy are attached to their editors (the redesign)', async () => {
  const p = page();
  assert.equal(p.pg.drafts.REATTACH_WORDS_AFTER_RESTART, true);
});

test('with the switch flipped: words from the host\'s copy are read-only (listed with Copy and Discard), never attached; the page\'s own state still re-attaches', async () => {
  const hostCopy = { [JSON.stringify(['desktop:contoso sales', 'instructions', ''])]: { value: 'typed before VS Code closed', base: 'was', modelLabel: 'Contoso Sales' } };
  const { drafts } = makePage({ './bridge': { loadState: makeDisk().loadState, saveState: () => undefined } }, 'desktop:contoso sales');
  // What the page would load with the switch off, given an empty own state and the host's copy.
  const loaded = drafts.loadHeldDrafts(null, hostCopy, false);
  const store = drafts.createDraftStore({ load: () => loaded, save: () => undefined });
  assert.equal(store.get('desktop:contoso sales', 'instructions', ''), null, 'the editor of the very same model does not find them');
  const list = store.otherModels('desktop:contoso sales');
  assert.equal(list.length, 1, 'they are listed');
  assert.match(list[0].label, /Contoso Sales \(from before VS Code restarted\)/);
  assert.equal(list[0].drafts[0].draft.value, 'typed before VS Code closed', 'the words are all there for Copy');
  store.clearModel(list[0].modelKey);
  assert.equal(store.otherModels('desktop:contoso sales').length, 0, 'Discard clears them');
  // The page's own state (a tab switch, a reload, an engine restart) is not touched by the switch.
  const own = { [JSON.stringify(['desktop:contoso sales', 'instructions', ''])]: { value: 'own words', base: 'was' } };
  const kept = drafts.loadHeldDrafts(own, hostCopy, false);
  assert.equal(Object.values(kept)[0].value, 'own words');
  assert.ok(Object.keys(kept)[0].includes('desktop:contoso sales') && !Object.keys(kept)[0].includes('restored|'));
});
