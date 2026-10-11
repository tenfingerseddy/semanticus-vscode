// Round 3 (join 11, drafts): the safety net. Words held for a model that is not the open one stay in the store, and one quiet line says so:
// "Words kept for N other models", each named by the engine's ModelLabel, with Copy (the words to the clipboard) and Discard (drop them).
// The list only READS: it never attaches the words to an editor, never makes an engine call, and never writes anything to a model.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, text, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
function world(openKey = 'file:/m/Open.bim', openLabel = 'Open') {
  const disk = makeDisk(), rpcs = [], copied = [];
  const pg = makePage({ './bridge': { rpc: async (...a) => { rpcs.push(a); }, copyText: async (t) => { copied.push(t); return true; }, loadState: disk.loadState, saveState: disk.saveState } }, openKey);
  pg.drafts.setDraftModel(openKey, openLabel);
  const { OtherModelWords } = pg.load('othermodels.tsx');
  return { disk, pg, rpcs, copied, store: pg.drafts.draftStore, mount: () => pg.mount(() => OtherModelWords()) };
}
const heldFor = (w, key, label, scope, field, value) => { w.pg.drafts.setDraftModel(key, label); w.store.set(key, scope, field, { value, base: 'was' }); };
const restoreOpen = (w) => w.pg.drafts.setDraftModel('file:/m/Open.bim', 'Open');
const toggle = (r) => find(r.result, (el) => el.props?.['data-testid'] === 'other-model-words-toggle');
const rows = (r) => findAll(r.result, (el) => el.props?.['data-testid'] === 'other-model-row');
const btn = (r, id) => find(r.result, (el) => el.props?.['data-testid'] === id);

test('nothing to say when only the open model holds words', async () => {
  const w = world();
  w.store.set('file:/m/Open.bim', 'instructions', '', { value: 'mine', base: 'was' });
  const r = w.mount(); await settleAll();
  assert.equal(r.result, null);
});

test('the line counts the OTHER models and names each by its label; the open model is not in it', async () => {
  const w = world();
  w.store.set('file:/m/Open.bim', 'instructions', '', { value: 'mine', base: 'was' });
  heldFor(w, 'desktop:contoso sales', 'Contoso Sales', 'instructions', '', 'Words for Contoso.');
  heldFor(w, 'live:powerbi://x/finance|sales', 'Sales', 'notes', '', 'Words for Finance.');
  heldFor(w, 'live:powerbi://x/finance|sales', 'Sales', 'instructions', '', 'More words for Finance.');
  restoreOpen(w);
  const r = w.mount(); await settleAll();
  assert.match(text(toggle(r)), /Words kept for 2 other models/);
  assert.equal(rows(r).length, 0, 'the list is closed until asked for');
  toggle(r).props.onClick(); await settleAll();
  assert.equal(rows(r).length, 2);
  const shown = JSON.stringify(r.result);
  assert.match(shown, /Contoso Sales/); assert.match(shown, /one piece of words/);
  assert.match(shown, /Sales/); assert.match(shown, /2 pieces of words/);
  assert.doesNotMatch(shown, /Open\b/, 'the open model is not listed');
});

test('a model held before labels existed is named from its key', async () => {
  const w = world();
  w.store.set('/old/Path/Budget.bim', 'instructions', '', { value: 'x', base: 'y' });
  w.store.set('unsaved:abcd', 'instructions', '', { value: 'x', base: 'y' });
  const r = w.mount(); await settleAll(); toggle(r).props.onClick(); await settleAll();
  const shown = JSON.stringify(r.result);
  assert.match(shown, /Budget/); assert.match(shown, /an unsaved model/);
});

test('Copy puts the words on the clipboard and nothing else happens', async () => {
  const w = world();
  heldFor(w, 'desktop:contoso sales', 'Contoso Sales', 'calcitem|calcitem:Time/YTD', 'expression', 'CALCULATE(1)'); restoreOpen(w);
  w.store.setLabel('desktop:contoso sales', 'calcitem|calcitem:Time/YTD', 'expression', 'Time / YTD');
  const r = w.mount(); await settleAll(); toggle(r).props.onClick(); await settleAll();
  const before = JSON.stringify(w.disk.map.get('studio.drafts'));
  btn(r, 'other-model-copy').props.onClick(); await settleAll();
  assert.equal(w.copied.length, 1);
  assert.match(w.copied[0], /Time \/ YTD/); assert.match(w.copied[0], /CALCULATE\(1\)/);
  assert.equal(w.rpcs.length, 0, 'no engine call');
  assert.equal(JSON.stringify(w.disk.map.get('studio.drafts')), before, 'Copy keeps the words');
  assert.match(JSON.stringify(r.result), /Copied/);
});

test('Discard drops that model\'s words and no one else\'s; the line goes when none are left', async () => {
  const w = world();
  heldFor(w, 'desktop:a', 'A', 'instructions', '', 'a words');
  heldFor(w, 'desktop:b', 'B', 'instructions', '', 'b words'); restoreOpen(w);
  w.store.set('file:/m/Open.bim', 'instructions', '', { value: 'open words', base: 'was' });
  const r = w.mount(); await settleAll(); toggle(r).props.onClick(); await settleAll();
  const discards = findAll(r.result, (el) => el.props?.['data-testid'] === 'other-model-discard');
  discards[0].props.onClick(); await settleAll();
  assert.equal(w.store.get('desktop:a', 'instructions', ''), null);
  assert.ok(w.store.get('desktop:b', 'instructions', ''), 'the other model\'s words stay');
  assert.ok(w.store.get('file:/m/Open.bim', 'instructions', ''), 'and the open model\'s');
  assert.equal(w.rpcs.length, 0);
  const left = findAll(r.result, (el) => el.props?.['data-testid'] === 'other-model-discard');
  left[0].props.onClick(); await settleAll();
  assert.equal(r.result, null, 'nothing kept for other models: no line');
});

test('words kept for another model are never attached to an editor of the open one', async () => {
  const w = world();
  heldFor(w, 'desktop:a', 'A', 'instructions', '', 'a words'); restoreOpen(w);
  assert.equal(w.pg.drafts.heldDraft('instructions', ''), undefined, 'the open model has no draft under that scope');
  assert.equal(w.store.any('instructions', 'file:/m/Open.bim'), false);
  const r = w.mount(); await settleAll(); toggle(r).props.onClick(); await settleAll();
  assert.equal(w.rpcs.length, 0, 'showing the list makes no engine call');
});

test('the draft remembers the model\'s label when it is typed, so the line can name it later', async () => {
  const w = world('desktop:contoso sales', 'Contoso Sales');
  w.store.set('desktop:contoso sales', 'instructions', '', { value: 'x', base: 'y' });
  assert.equal(w.store.get('desktop:contoso sales', 'instructions', '').modelLabel, 'Contoso Sales');
  w.pg.drafts.setDraftModel('file:/m/Other.bim', 'Other');
  const list = w.store.otherModels('file:/m/Other.bim');
  assert.equal(list.length, 1); assert.equal(list[0].label, 'Contoso Sales');
});
