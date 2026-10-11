// Join 11 solui, fix round 3 (one rule for the whole class of lost typed words). The rule has four triggers and each has a test here:
//   T1 the live value moves (another writer, or the engine's own echo)   -> M1 the kept text compares the way the engine does (trimmed for a field the engine trims)
//   T2 a rename or delete re-keys the object the words were typed for    -> M2 every held key has a lister (the calc group card pairs 1:1 and moves its words; the rest lists)
//   T3 the box is drawn anew (a remount)                                 -> M3 the words are held in the draft store and the box opens on them
//   T4 a refusal or a failed write                                       -> M4 a box ends its words only when the engine said OK AND the box still holds the words it sent
// The verifier's red tests (verify-solui-r2-{a,b,c}) stay as they are; this file adds the hook itself, the composer and Plan row refusals, and the calc group card's held form.
import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false || el === true ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
const heldAll = (disk) => JSON.stringify([...disk.map.values()]);
const bridgeOf = (disk, more = {}) => ({ loadState: disk.loadState, saveState: disk.saveState, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}), copyText: async () => true, ...more });

// ---- M1 and M3: the hook ----------------------------------------------------------------------------------------------------------------------------
function hookBox(initial, key, options, disk = makeDisk()) {
  const pg = makePage({ './bridge': bridgeOf(disk) }, MODEL);
  const { useKeptText, sameTrimmed } = pg.load('keptwords.ts');
  const s = { live: initial, key };
  const opts = typeof options === 'function' ? options(sameTrimmed) : options;
  const mount = () => pg.mount(() => useKeptText(s.live, s.key, opts));
  const m = mount();
  return { s, pg, disk, mount, m, get: () => m.result, sameTrimmed,
    move: async (live, k) => { s.live = live; if (k !== undefined) s.key = k; m.rerender(); await settleAll(); m.rerender(); },
    type: async (t) => { m.result.setText(t); await settleAll(); m.rerender(); } };
}

test('M1 same: a box whose engine trims treats the trimmed echo of its own words as the same, and shows no moved value', async () => {
  const b = hookBox('old', 'k', (same) => ({ same }));
  await b.type('check Q3 first ');
  await b.move('check Q3 first');   // the engine stored it trimmed
  assert.equal(b.get().moved, null, 'the person\'s own save is not another writer\'s change');
  assert.equal(b.get().text, 'check Q3 first');
  // The exact rule (the default) still treats a different value as moved.
  const e = hookBox('old', 'k');
  await e.type('mine '); await e.move('mine');
  assert.equal(e.get().moved, 'mine', 'the default compare is exact, as before');
});

test('M3 hold: typed words are mirrored into the draft store with the value they were typed over, and a new box opens on them', async () => {
  const disk = makeDisk();
  const b = hookBox('ten', 'k', { hold: { scope: 'calcgroup|table:T', field: 'precedence' } }, disk);
  await b.type('twenty');
  const held = b.pg.drafts.draftStore.get(MODEL, 'calcgroup|table:T', 'precedence');
  assert.deepEqual([held?.value, held?.base], ['twenty', 'ten']);
  b.m.unmount();
  const again = b.mount();
  await settleAll(); again.rerender();
  assert.equal(again.result.text, 'twenty', 'the box opens on the held words');
  assert.equal(again.result.moved, null, 'nothing moved while the box was away');
  b.s.live = 'thirty'; again.rerender(); await settleAll(); again.rerender();
  assert.equal(again.result.text, 'twenty');
  assert.equal(again.result.moved, 'thirty', 'a value that moved while the words were away is named');
});

test('M3 hold: the hold ends when the box is the live value again (the engine took the words) and when the person takes the newer value', async () => {
  const b = hookBox('ten', 'k', { hold: { scope: 's|1', field: 'f' } });
  const store = () => b.pg.drafts.draftStore.get(MODEL, 's|1', 'f');
  await b.type('twenty'); assert.ok(store());
  await b.move('twenty');   // the engine took exactly those words
  assert.equal(store(), null);
  await b.type('twenty five'); assert.ok(store());
  await b.move('thirty');
  b.get().useLive(); await settleAll(); b.m.rerender();
  assert.equal(store(), null, 'Use the new one ends the hold');
  assert.equal(b.get().text, 'thirty');
});

test('M3 hold: another scope is another draft, and words typed back to the live value hold nothing', async () => {
  const b = hookBox('a', 'k1', { hold: { scope: 's|1', field: 'f' } });
  await b.type('mine');
  assert.ok(b.pg.drafts.draftStore.get(MODEL, 's|1', 'f'));
  await b.type('a');
  assert.equal(b.pg.drafts.draftStore.get(MODEL, 's|1', 'f'), null, 'words typed back to the live value are not a draft');
});

// ---- M1: the composer Question ----------------------------------------------------------------------------------------------------------------------------
// The engine's frozen draft scenes are a private contract file, left out of the public source mirror. The private repo
// (marked by its mirror manifest) always reads them, so a missing file still fails there; the mirror skips these tests.
const privateRepo = existsSync(join(webview, '..', '..', 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
const scenes = privateRepo ? JSON.parse(readFileSync(join(webview, '..', '..', 'docs', 'redesign', 'contracts', 'draft-view.json'), 'utf8')).scenes : null; // mirror-optional: private contract file
const sceneTest = scenes ? test : (name) => test(name, { skip: 'the frozen draft scenes are not in the public source' }, () => {});
function draftPage(disk = makeDisk()) {
  const pg = makePage({ './bridge': bridgeOf(disk, { rpc: async () => null, onDraftChange: () => () => {}, onPlanChange: () => () => {} }), './workflow-run': { WorkflowRunDetail: () => null } }, MODEL);
  return { disk, pg, D: pg.load('composer-draft.tsx') };
}
const handlers = { onEdit() {}, onRun() {}, onApprove() {}, onUndo() {}, onGo() {}, onAskWhy() {}, onCopy() {} };

sceneTest('M1 the Question: an answer saved with trailing spaces (the engine trims it) shows no "was answered" banner', async () => {
  const { pg, D } = draftPage();
  const view = pg.mount(() => D.DraftBody({ view: scenes.from_words_step_picked, busy: null, ...handlers }));
  const q = findAll(view.result, (el) => el.type?.name === 'Question').find((x) => x.props.q.name === 'add-measure-expected');
  assert.ok(q, 'the text question is drawn');
  const live = { q: q.props.q };
  const card = pg.mount(() => q.type({ ...q.props, q: live.q }));
  find(card.result, (el) => el.type === 'input' && el.props?.['aria-label'] === live.q.question).props.onChange({ target: { value: '1500000 ' } });
  await settleAll(); card.rerender();
  live.q = { ...live.q, answer: '1500000' };   // what the engine stored: trimmed
  card.rerender(); await settleAll(); card.rerender();
  assert.equal(byTestId(card.result, 'cd-q-moved'), null, 'a trailing space is not another writer\'s answer');
  // A different answer from another writer is still named.
  find(card.result, (el) => el.type === 'input' && el.props?.['aria-label'] === live.q.question).props.onChange({ target: { value: '2000000' } });
  await settleAll(); card.rerender();
  live.q = { ...live.q, answer: '1700000' }; card.rerender(); await settleAll(); card.rerender();
  assert.ok(byTestId(card.result, 'cd-q-moved'));
});

sceneTest('M3 the Question: a typed answer is held under the draft, so a row that moves between sections (a remount) opens on it', async () => {
  const { pg, D } = draftPage();
  const view = pg.mount(() => D.DraftBody({ view: scenes.from_words_step_picked, busy: null, ...handlers }));
  const q = findAll(view.result, (el) => el.type?.name === 'Question').find((x) => x.props.q.name === 'add-measure-expected');
  const label = q.props.q.question;
  const first = pg.mount(() => q.type(q.props));
  find(first.result, (el) => el.type === 'input' && el.props?.['aria-label'] === label).props.onChange({ target: { value: '1500000' } });
  await settleAll(); first.rerender();
  first.unmount();
  const again = pg.mount(() => q.type(q.props));
  await settleAll(); again.rerender();
  assert.equal(find(again.result, (el) => el.type === 'input' && el.props?.['aria-label'] === label).props.value, '1500000');
});

// ---- M4: the composer's two boxes ---------------------------------------------------------------------------------------------------------------------------
sceneTest('M4 Add more words refused: the words stay in the box, and the box is not cleared before the answer', async () => {
  const { pg, D, disk } = draftPage();
  const live = { v: scenes.from_words_menu };
  let answer;
  const body = pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers, onEdit: () => new Promise((r) => { answer = r; }) }));
  byTestId(body.result, 'cd-refine').props.onChange({ target: { value: 'and the returns table' } }); await settleAll(); body.rerender();
  find(body.result, (el) => el.type === 'form' && el.props?.className === 'cd-refine').props.onSubmit({ preventDefault() {} }); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine').props.value, 'and the returns table', 'the box still holds the words while the answer is on its way');
  answer(false); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine').props.value, 'and the returns table', 'a refusal leaves the words');
  assert.ok(heldAll(disk).includes('and the returns table'), 'and they are still held');
  // Sent again and taken: the box empties.
  find(body.result, (el) => el.type === 'form' && el.props?.className === 'cd-refine').props.onSubmit({ preventDefault() {} }); await settleAll();
  answer(true); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine').props.value, '');
  assert.ok(!heldAll(disk).includes('and the returns table'));
});

sceneTest('M4 Add more words taken: words typed while the write was on its way are not cleared', async () => {
  const { pg, D } = draftPage();
  let answer;
  const body = pg.mount(() => D.DraftBody({ view: scenes.from_words_menu, busy: null, ...handlers, onEdit: () => new Promise((r) => { answer = r; }) }));
  byTestId(body.result, 'cd-refine').props.onChange({ target: { value: 'only 2024' } }); await settleAll(); body.rerender();
  find(body.result, (el) => el.type === 'form' && el.props?.className === 'cd-refine').props.onSubmit({ preventDefault() {} });
  byTestId(body.result, 'cd-refine').props.onChange({ target: { value: 'only 2024 and 2025' } }); await settleAll(); body.rerender();
  answer(true); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine').props.value, 'only 2024 and 2025', 'the box holds more than was sent, so the words stay');
});

sceneTest('M4 Write it yourself taken: words typed while the write was on its way stay in the writer', async () => {
  const { pg, D } = draftPage();
  let answer;
  const body = pg.mount(() => D.DraftBody({ view: scenes.started_waiting_for_your_assistant, busy: null, ...handlers, onEdit: () => new Promise((r) => { answer = r; }) }));
  byTestId(body.result, 'cd-write').props.onClick(); body.rerender();
  find(body.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'DIVIDE ( 1, 2 )' } }); await settleAll(); body.rerender();
  byTestId(body.result, 'cd-writer').props.onSubmit({ preventDefault() {} });
  find(body.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'DIVIDE ( 1, 3 )' } }); await settleAll(); body.rerender();
  answer(true); await settleAll(); body.rerender();
  assert.ok(byTestId(body.result, 'cd-writer'), 'the writer stays open over the newer words');
  assert.equal(find(body.result, (el) => el.type === 'textarea').props.value, 'DIVIDE ( 1, 3 )');
});

sceneTest('M4 the page\'s call answers false on a refusal and true when the engine took the press', async () => {
  const disk = makeDisk();
  const calls = [];
  let refuse = true;
  const pg = makePage({ './bridge': bridgeOf(disk, { rpc: async (method, ...args) => { calls.push(method); if (method === 'getDraft') return scenes.started_waiting_for_your_assistant; if (refuse) throw new Error('Not written: it did not pass its check. Change it and write it again.'); return scenes.started_waiting_for_your_assistant; },
    onDraftChange: () => () => {}, onPlanChange: () => () => {} }), './workflow-run': { WorkflowRunDetail: () => null } }, MODEL);
  const D = pg.load('composer-draft.tsx');
  const view = pg.mount(() => D.ComposerDraft({ draftId: scenes.started_waiting_for_your_assistant.draftId, onGo() {} }));
  await settleAll(); view.rerender();
  const body = find(view.result, (el) => el.type?.name === 'DraftBody');
  assert.ok(body, 'the draft is drawn');
  assert.equal(await body.props.onEdit('write', { step: 'return-rate', blank: 'formula', text: 'x' }), false, 'a refusal answers false');
  view.rerender();
  assert.match(flat(byTestId(view.result, 'cd-error')), /Change it and write it again/);
  refuse = false;
  assert.equal(await body.props.onEdit('write', { step: 'return-rate', blank: 'formula', text: 'x' }), true, 'a taken press answers true');
});

// ---- M4: a Plan row ---------------------------------------------------------------------------------------------------------------------------------------
function planRowPage() {
  const disk = makeDisk();
  const pg = makePage({ './bridge': bridgeOf(disk), './pro': { upgradeToPro: () => undefined, useTier: () => 'pro', PENDING_ACCESS: '' }, './toolrow': { anchorUnder: () => ({}) } }, MODEL);
  return { disk, pg, R: pg.load('plan-row.tsx') };
}
const task = (over = {}) => ({ id: 'pt-0000000b', title: 'Check the totals', source: 'you', who: 'you', doer: 'you', stateWord: 'on_it', claim: { claimedBy: 'you' }, note: 'old note', ...over });
const mountRow = (pg, R, live, onAction) => pg.mount(() => R.PlanRowDetail({ item: live.item, items: [live.item], busy: null, onAction, onGo() {}, onDoer() {}, onNote() {} }));
// Round 1 (K5): Could not and the Mark done reply form are gone from the row. Their two tests (M4 Could not refused, M4 Mark done
// refused) tested only that removed UI, so they are removed with it. Mark done is one press on the row and sends no reply.

test('M1 the Plan note: a trailing space is not a change (Save stays off) and holds no draft', async () => {
  const { pg, R } = planRowPage();
  const live = { item: task({ note: 'old note' }) };
  const row = mountRow(pg, R, live, () => undefined);
  await settleAll(); row.rerender();
  byTestId(row.result, 'plan-note').props.onChange({ target: { value: 'old note ' } }); await settleAll(); row.rerender();
  const save = find(row.result, (el) => el.type === 'button' && el.props?.type === 'submit' && flat(el) === 'Save note');
  assert.equal(save.props.disabled, true, 'a trailing space only is not a change to save');
  assert.equal(pg.drafts.draftStore.get(MODEL, 'planrow|pt-0000000b', 'note'), null, 'words that are the live note but for a trailing space are not a draft');
});

test('M4 the Plan wires the answer: the page\'s onAction answers true when the engine took Mark done, and false when it refused', () => {
  const src = readFileSync(join(webview, 'src', 'plan.tsx'), 'utf8');
  assert.match(src, /case 'done': return controlOk\(/);
  assert.doesNotMatch(src, /case 'couldnot'/, 'Could not is gone from the page (the engine verb stays)');
  assert.match(src, /return \{ ok: false, value: null \}/, 'a refusal is not an empty answer');
  assert.match(readFileSync(join(webview, 'src', 'plan-row.tsx'), 'utf8'), /export type RowActionHandler = [^\n]*void \| Promise<boolean>/);
});

// ---- M2 and M3: the calc group card ---------------------------------------------------------------------------------------------------------------------------
function advPage(world) {
  const disk = makeDisk();
  world.listeners = []; world.calls = [];
  const pg = makePage({
    './bridge': { rpc: async (method, ...args) => { world.calls.push([method, ...args]); return structuredClone(world.answer(method, ...args) ?? {}); },
      onDidChange: (f) => { world.listeners.push(f); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, MODEL);
  return { pg, disk, adv: pg.load('advmodels.tsx') };
}
const group = (name, items, precedence = 10) => ({ ref: `table:${name}`, name, precedence, items: items.map((n, i) => ({ ref: `calcitem:${name}/${n}`, name: n, ordinal: i, expression: 'x' })) });
const calcWorld = (groups) => { const w = { groups }; w.answer = (m) => (m === 'listCalculationGroups' ? w.groups : {}); return w; };
const cardOf = (view, name) => findAll(view.result, (el) => el.type?.name === 'CalcGroupCard').find((c) => c.props.group.name === name);
const reloadAll = async (world, view) => { world.listeners.forEach((f) => f()); await wait(420); await settleAll(); view.rerender(); await settleAll(); view.rerender(); };
const mountCard = (pg, el, group) => pg.mount(() => el.type({ ...el.props, group: group ?? el.props.group }));
const openItemForm = async (card) => {
  find(card.result, (el) => el.type?.name === 'MiniButton' && flat(el) === '+ Item').props.onClick(); await settleAll(); card.rerender();
};
const nameBox = (card) => find(card.result, (el) => el.type === 'input' && /item name/.test(el.props?.placeholder ?? ''));
const exprField = (card) => find(card.result, (el) => el.props?.ariaLabel === 'New calculation item expression');

test('M3 the + Item form and the precedence digits are held under the group, and a card drawn anew opens on them', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD'])]);
  const { pg, adv, disk } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const card = mountCard(pg, cardOf(view, 'Time Intelligence'));
  await openItemForm(card);
  nameBox(card).props.onChange({ target: { value: 'Prior quarter' } }); await settleAll(); card.rerender();
  exprField(card).props.onChange('CALCULATE ( SELECTEDMEASURE (), PARALLELPERIOD ( \'Date\'[Date], -1, QUARTER ) )'); await settleAll(); card.rerender();
  find(card.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric').props.onChange({ target: { value: '20' } }); await settleAll(); card.rerender();
  const held = pg.drafts.draftStore.scopes(MODEL, 'calcgroup|');
  assert.deepEqual(held.map((h) => h.scope), ['calcgroup|table:Time Intelligence']);
  assert.deepEqual(held[0].fields.map((f) => f.field).sort(), ['item', 'precedence']);
  // The tab is swapped out and back (or the card is drawn anew): the form is open with the words, and the digits are in their box.
  card.unmount();
  const again = mountCard(pg, cardOf(view, 'Time Intelligence')); await settleAll(); again.rerender();
  assert.equal(nameBox(again)?.props.value, 'Prior quarter', 'the + Item form opens on its words');
  assert.match(exprField(again).props.value, /PARALLELPERIOD/);
  assert.equal(find(again.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric').props.value, '20');
  // Cancel is the person's own decision: it ends the form's words.
  find(again.result, (el) => el.type?.name === 'MiniButton' && flat(el) === 'Cancel').props.onClick(); await settleAll(); again.rerender();
  assert.ok(!heldAll(disk).includes('Prior quarter'));
});

test('M4 an item add the engine took ends the form; an add the engine refused keeps it with its words', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD'])]);
  let refuse = true;
  const base = w.answer;
  w.answer = (m, ...a) => { if (m === 'createCalculationItem') { if (refuse) throw new Error('That name is taken.'); return 'calcitem:Time Intelligence/Prior'; } return base(m, ...a); };
  const { pg, adv, disk } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const card = mountCard(pg, cardOf(view, 'Time Intelligence'));
  await openItemForm(card);
  nameBox(card).props.onChange({ target: { value: 'Prior' } }); await settleAll(); card.rerender();
  const add = () => find(card.result, (el) => el.type?.name === 'MiniButton' && flat(el) === 'Add item');
  add().props.onClick(); await settleAll(); card.rerender();
  assert.equal(nameBox(card)?.props.value, 'Prior', 'a refused add leaves the form');
  assert.ok(heldAll(disk).includes('Prior'));
  refuse = false;
  add().props.onClick(); await settleAll(); card.rerender();
  assert.equal(nameBox(card), null, 'an add the engine took closes the form');
  assert.ok(!heldAll(disk).includes('Prior'));
});

test('M2 a group renamed together with its item: the card keeps its key and the words move to the new ref (nothing is left under the old one)', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD']), group('Currency', ['USD'])]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const before = cardOf(view, 'Time Intelligence').key;
  const card = mountCard(pg, cardOf(view, 'Time Intelligence'));
  await openItemForm(card);
  nameBox(card).props.onChange({ target: { value: 'Prior' } }); await settleAll(); card.rerender();
  w.groups = [group('Time Intel', ['Year to date']), group('Currency', ['USD'])];
  await reloadAll(w, view);
  assert.equal(cardOf(view, 'Time Intel').key, before);
  const scopes = pg.drafts.draftStore.scopes(MODEL, 'calcgroup|').map((s) => s.scope);
  assert.deepEqual(scopes, ['calcgroup|table:Time Intel'], 'the words moved with the card');
  const renamed = mountCard(pg, cardOf(view, 'Time Intel')); await settleAll(); renamed.rerender();
  assert.equal(nameBox(renamed)?.props.value, 'Prior', 'and the card reads them under its new ref');
});

test('M2 a group deleted while words are typed on it: the words are listed (not lost) and can move to a group that is here', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD']), group('Currency', ['USD'])]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const card = mountCard(pg, cardOf(view, 'Time Intelligence'));
  await openItemForm(card);
  nameBox(card).props.onChange({ target: { value: 'Prior' } }); await settleAll(); card.rerender();
  w.groups = [group('Currency', ['USD'])];
  await reloadAll(w, view);
  const orphan = find(view.result, (el) => el.type?.name === 'OrphanDrafts' && el.props.prefix === 'calcgroup|');
  assert.ok(orphan, 'the panel lists words kept for a group that is not here');
  const O = pg.load('orphandrafts.tsx');
  assert.equal(O.orphanScopes(orphan.props.prefix, orphan.props.live, orphan.props.keep).length, 1);
  assert.deepEqual(orphan.props.targetsFor().map((t) => t.id), ['table:Currency'], 'the words can be moved to a group that is here');
  assert.equal(orphan.props.nameOf('table:Time Intelligence'), 'Time Intelligence');
});
