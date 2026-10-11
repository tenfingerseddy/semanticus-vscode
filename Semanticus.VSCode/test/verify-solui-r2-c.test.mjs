// Verifier on m11/solui round 2 (rule R-B: words typed in a box are never replaced, remounted away or hidden by ANOTHER writer's change, and a person typing alone
// still saves as before). Two of the swept sites, each driven with the values another writer moves under the box, and each with a "person alone" control:
//   C1. a result grid's filters and its open filter box (grid.tsx) across a new column set, a column that went, and new rows with the same columns;
//   C2. the Spec name boxes on rows drawn by position (spec.tsx NameInput), when the assistant deletes or inserts a row above the one being typed in.
// The fake React renders one component at a time. Where React would REUSE a child (same type, same place, no key), the test drives that child as one instance with
// the props React would hand it, and asserts the place and key really are the same, so the reuse is not assumed.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined;
globalThis.removeEventListener ??= () => undefined;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
globalThis.ResizeObserver ??= class { observe() {} disconnect() {} };
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false || el === true ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
const mountEl = (pg, el) => pg.mount(() => el.type(el.props));

/** Where React would place an element: the chain of (child position, type, key) from the root. Two renders with the same chain reuse the same instance. */
function placeOf(tree, pred) {
  let hit = null;
  const typeName = (el) => (typeof el.type === 'string' ? el.type : el.type?.name ?? String(el.type));
  const visit = (node, path) => {
    if (hit || node == null || typeof node !== 'object') return;
    if (Array.isArray(node)) { node.forEach((n, i) => visit(n, [...path, `#${i}`])); return; }
    const here = [...path, `${typeName(node)}${node.key != null ? `[${node.key}]` : ''}`];
    if (pred(node)) { hit = here.join('/'); return; }
    const kids = node.props?.children;
    if (Array.isArray(kids)) kids.forEach((k, i) => visit(k, [...here, `#${i}`]));
    else visit(kids, here);
  };
  visit(tree, []);
  return hit;
}

// ---- C1: result grid filters ----------------------------------------------------------------------------------------------------------------------
function gridWorld(columns, rows) {
  const pg = makePage({ '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) } }, MODEL);
  const { ResultGrid } = pg.load('grid.tsx');
  const live = { columns, rows };
  const grid = pg.mount(() => ResultGrid({ columns: live.columns, rows: live.rows }));
  const ev = { stopPropagation() {}, currentTarget: { getBoundingClientRect: () => ({ left: 0, bottom: 0 }) } };
  const popEl = () => find(grid.result, (el) => el.type?.name === 'FilterPopover');
  const funnel = (i) => find(grid.result, (el) => el.props?.['data-funnel'] === i);
  const set = async (cols, rs) => { live.columns = cols; live.rows = rs; grid.rerender(); await settleAll(); grid.rerender(); };
  return { pg, grid, ev, popEl, funnel, set, live };
}
/** The popover as one instance React keeps, rendered with the props the grid hands it now. */
function popInstance(w) {
  const pop = w.pg.mount(() => { const el = w.popEl(); return el ? el.type(el.props) : null; });
  const body = () => find(pop.result, (el) => /Body$/.test(el.type?.name ?? ''));
  return { pop, body, type: async (draft) => { body().props.setDraft(draft); await settleAll(); pop.rerender(); } };
}

test('C1a. a value typed in an open filter box (not applied) survives the assistant\'s new column set, and Apply puts it on its column where it now is', async () => {
  const w = gridWorld([{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], [['alpha', 1], ['beta', 9], ['gamma', 4]]);
  await settleAll(); w.grid.rerender();
  w.funnel(0).props.onClick(w.ev); await settleAll(); w.grid.rerender();
  const place = placeOf(w.grid.result, (el) => el.type?.name === 'FilterPopover');
  const p = popInstance(w);
  await p.type({ kind: 'text', op: 'contains', value: 'bet', picked: [] });
  assert.equal(p.body().props.draft.value, 'bet');
  // The assistant's query adds a column in front: Name moves from 0 to 1.
  await w.set([{ name: 'Region', type: 'string' }, { name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], [['x', 'alpha', 1], ['y', 'beta', 9], ['z', 'gamma', 4]]);
  assert.ok(w.popEl(), 'the filter box is still open');
  assert.equal(placeOf(w.grid.result, (el) => el.type?.name === 'FilterPopover'), place, 'the filter box is in the same place with the same key, so React keeps its typed state');
  p.pop.rerender(); await settleAll(); p.pop.rerender();
  assert.equal(p.body().props.draft.value, 'bet', 'the typed value is still in the box');
  assert.equal(w.popEl().props.name, 'Name');
  // The person applies: the filter goes on Name at its new place.
  find(p.pop.result, (el) => el.type === 'button' && /Apply/.test(flat(el))).props.onClick(); await settleAll(); w.grid.rerender();
  assert.equal(w.popEl(), null);
  assert.match(flat(w.grid.result), /1 of 3/, 'one row of three matches "bet" in Name');
  assert.equal(byTestId(w.grid.result, 'grid-filters-aside'), null, 'nothing is set aside: the person applied after the change');
});

test('C1b. a filter box open on a column the assistant\'s query removed stays open with its words, says the column is gone, and Apply sets the filter aside by name', async () => {
  const w = gridWorld([{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], [['alpha', 1], ['beta', 9]]);
  await settleAll(); w.grid.rerender();
  w.funnel(1).props.onClick(w.ev); await settleAll(); w.grid.rerender();
  const p = popInstance(w);
  await p.type({ kind: 'number', op: 'gt', a: 5, b: null });
  await w.set([{ name: 'Name', type: 'string' }], [['alpha'], ['beta']]);
  assert.ok(w.popEl(), 'the box is still open');
  assert.ok(byTestId(w.grid.result, 'grid-filter-col-gone'), 'the page says the column is not in this result now');
  assert.match(flat(byTestId(w.grid.result, 'grid-filter-col-gone')), /Amount/);
  p.pop.rerender(); await settleAll(); p.pop.rerender();
  assert.equal(p.body().props.draft.a, 5, 'the typed number is still in the box');
  find(p.pop.result, (el) => el.type === 'button' && /Apply/.test(flat(el))).props.onClick(); await settleAll(); w.grid.rerender();
  const aside = byTestId(w.grid.result, 'grid-filters-aside');
  assert.ok(aside, 'the applied filter is kept and named');
  assert.match(flat(aside), /Amount/);
  assert.match(flat(w.grid.result), /2 of 2|2 rows|alpha/, 'no filter is used on the rows shown');
});

test('C1c. new rows with the same columns (a refresh) keep an applied filter in use and an open box as it was: nothing set aside, no note', async () => {
  const w = gridWorld([{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], [['alpha', 1], ['beta', 9]]);
  await settleAll(); w.grid.rerender();
  w.funnel(1).props.onClick(w.ev); await settleAll(); w.grid.rerender();
  w.popEl().props.onApply({ kind: 'number', op: 'gt', a: 5, b: null }); await settleAll(); w.grid.rerender();
  assert.match(flat(w.grid.result), /1 of 2/);
  w.funnel(0).props.onClick(w.ev); await settleAll(); w.grid.rerender();
  const p = popInstance(w);
  await p.type({ kind: 'text', op: 'contains', value: 'al', picked: [] });
  await w.set([{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], [['alpha', 1], ['beta', 9], ['delta', 7]]);
  assert.match(flat(w.grid.result), /2 of 3/, 'the Amount filter is still used on the new rows');
  assert.equal(byTestId(w.grid.result, 'grid-filters-aside'), null);
  assert.equal(byTestId(w.grid.result, 'grid-filter-col-gone'), null);
  p.pop.rerender(); await settleAll(); p.pop.rerender();
  assert.equal(p.body().props.draft.value, 'al');
});

test('C1 alone. a person who filters with nothing else changing gets the filter in use at once, and no set-aside or gone note ever shows', async () => {
  const w = gridWorld([{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], [['alpha', 1], ['beta', 9], ['gamma', 4]]);
  await settleAll(); w.grid.rerender();
  w.funnel(0).props.onClick(w.ev); await settleAll(); w.grid.rerender();
  const p = popInstance(w);
  await p.type({ kind: 'text', op: 'contains', value: 'a', picked: [] });
  find(p.pop.result, (el) => el.type === 'button' && /Apply/.test(flat(el))).props.onClick(); await settleAll(); w.grid.rerender();
  assert.equal(w.popEl(), null, 'Apply closes the box');
  assert.match(flat(w.grid.result), /3 rows|3 of 3/, 'every row matches "a"');
  w.funnel(1).props.onClick(w.ev); await settleAll(); w.grid.rerender();
  w.popEl().props.onApply({ kind: 'number', op: 'gt', a: 3, b: null }); await settleAll(); w.grid.rerender();
  assert.match(flat(w.grid.result), /2 of 3/);
  assert.equal(byTestId(w.grid.result, 'grid-filters-aside'), null);
  assert.equal(byTestId(w.grid.result, 'grid-filter-col-gone'), null);
  // Clear all is still the person's way out.
  findAll(w.grid.result, (el) => el.type === 'button' && /Clear all/.test(flat(el)))[0].props.onClick(); await settleAll(); w.grid.rerender();
  assert.doesNotMatch(flat(w.grid.result), / of 3/);
});

// ---- C2: Spec rows drawn by position --------------------------------------------------------------------------------------------------------------
function specWorld(spec) {
  const disk = makeDisk();
  const w = { snap: { version: 1, source: 'manual', spec }, listeners: [], disk, writes: [] };
  const pg = makePage({
    './bridge': { rpc: async (method, ...a) => { w.writes.push([method, ...a]); return method === 'getSpec' ? structuredClone(w.snap) : {}; }, onSpecChange: (f) => { w.listeners.push(f); return () => undefined; }, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources() {} },
    './connection': { useConnection: () => ({ openConnections() {} }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, MODEL);
  w.pg = pg;
  w.spec = pg.load('spec.tsx');
  w.view = pg.mount(() => w.spec.SpecView({ session: { modelName: 'M' } }));
  w.held = (scope) => pg.drafts.draftStore.get(MODEL, scope)?.value;
  w.reload = async (next) => { w.snap = { ...w.snap, version: w.snap.version + 1, spec: next }; w.listeners.forEach((f) => f()); await wait(300); await settleAll(); w.view.rerender(); };
  return w;
}
const table = (name, cols = []) => ({ name, role: 'fact', columns: cols.map((c) => ({ name: c, dataType: 'String' })) });
const specOf = (tables, measures = []) => ({ name: 'M', tables, relationships: [], measures, timeIntelligence: [] });
/** The real tree down to the TableCards: SpecView, SpecEditor, then each TableCard as React keys it (key={ti}). */
async function cards(w) {
  await settleAll(); w.view.rerender();
  const editorEl = find(w.view.result, (el) => el.type?.name === 'SpecEditor');
  const editor = mountEl(w.pg, editorEl); await settleAll(); editor.rerender();
  return { editor, cardEls: () => findAll(editor.result, (el) => el.type?.name === 'TableCard') };
}

test('C2a. words typed in row 2\'s table name stay with that table when the assistant deletes row 1, and never land on the table now drawn in row 2\'s place', async () => {
  const w = specWorld(specOf([table('Sales'), table('Orders'), table('Returns')]));
  const { editor, cardEls } = await cards(w);
  assert.deepEqual(cardEls().map((e) => e.key), ['0', '1', '2'], 'TableCard is keyed by position, so React reuses the card in a slot for whatever table lands there');
  // The NameInput of each slot as React keeps it: slot i is one instance, handed whatever table is in slot i.
  const nameEl = (i) => { const card = mountEl(w.pg, cardEls()[i]); const el = find(card.result, (x) => x.type?.name === 'NameInput' && x.props.ariaLabel === 'Table name'); card.unmount(); return el; };
  const slots = [0, 1, 2].map((i) => { const s = { el: nameEl(i) }; s.box = w.pg.mount(() => s.el.type(s.el.props)); return s; });
  await settleAll();
  slots[1].box.result.props.onChange({ target: { value: 'Orders typed' } }); await settleAll(); slots[1].box.rerender();
  assert.equal(w.held('spec|buf|table:Orders|name'), 'Orders typed');
  // The assistant deletes Sales (no structural edit is pending, so the page takes the new spec).
  await w.reload(specOf([table('Orders'), table('Returns')]));
  editor.unmount();
  const again = await cards(w);
  const next = again.cardEls().map((el) => { const card = mountEl(w.pg, el); const n = find(card.result, (x) => x.type?.name === 'NameInput' && x.props.ariaLabel === 'Table name'); card.unmount(); return n; });
  // Slots 0 and 1 keep their instances; slot 2 unmounts.
  slots[0].el = next[0]; slots[1].el = next[1]; slots[2].box.unmount();
  for (let k = 0; k < 2; k++) { slots[0].box.rerender(); slots[1].box.rerender(); await settleAll(); }
  assert.equal(slots[0].box.result.props.value, 'Orders typed', 'the box now drawn for Orders shows the words typed for Orders');
  assert.equal(slots[1].box.result.props.value, 'Returns', 'the box now drawn for Returns shows Returns, not the words typed for Orders');
  const writesBefore = [];
  const commit = (s) => { const orig = s.el.props.onCommit; s.el = { ...s.el, props: { ...s.el.props, onCommit: (v) => { writesBefore.push([s.el.props.value, v]); orig(v); } } }; s.box.rerender(); s.box.result.props.onBlur(); };
  commit(slots[1]); await settleAll();
  assert.deepEqual(writesBefore, [], 'a blur on the Returns box writes nothing');
  commit(slots[0]); await settleAll();
  assert.deepEqual(writesBefore, [['Orders', 'Orders typed']], 'the words go to Orders, the table they were typed for');
});

test('C2b. words typed in a table name stay with that table when the assistant inserts a table above it (the box in its slot is handed another table)', async () => {
  const w = specWorld(specOf([table('Sales'), table('Orders')]));
  const { cardEls } = await cards(w);
  const nameOf = (el) => { const card = mountEl(w.pg, el); const n = find(card.result, (x) => x.type?.name === 'NameInput' && x.props.ariaLabel === 'Table name'); card.unmount(); return n; };
  const s1 = { el: nameOf(cardEls()[1]) };
  s1.box = w.pg.mount(() => s1.el.type(s1.el.props));
  await settleAll();
  s1.box.result.props.onChange({ target: { value: 'Orders 2' } }); await settleAll(); s1.box.rerender();
  await w.reload(specOf([table('Budget'), table('Sales'), table('Orders')]));
  const again = await cards(w);
  s1.el = nameOf(again.cardEls()[1]);   // slot 1 now draws Sales
  s1.box.rerender(); await settleAll(); s1.box.rerender();
  assert.equal(s1.box.result.props.value, 'Sales', 'slot 1 shows Sales, the table now in it');
  // Slot 2 is a NEW box for Orders: it opens on the words held for Orders.
  const s2el = nameOf(again.cardEls()[2]);
  const s2 = w.pg.mount(() => s2el.type(s2el.props)); await settleAll(); s2.rerender();
  assert.equal(s2.result.props.value, 'Orders 2', 'the new box for Orders opens on the words typed for Orders');
  assert.equal(byTestId(w.view.result, 'spec-words-moved'), null);
});

test('C2c. the assistant renames the very table whose name box holds typed words: the words are not on the renamed box, and the page lists them (not lost)', async () => {
  const w = specWorld(specOf([table('Sales'), table('Orders')]));
  const { cardEls } = await cards(w);
  const nameOf = (el) => { const card = mountEl(w.pg, el); const n = find(card.result, (x) => x.type?.name === 'NameInput' && x.props.ariaLabel === 'Table name'); card.unmount(); return n; };
  const s0 = { el: nameOf(cardEls()[0]) };
  s0.box = w.pg.mount(() => s0.el.type(s0.el.props));
  await settleAll();
  s0.box.result.props.onChange({ target: { value: 'Sales EU' } }); await settleAll(); s0.box.rerender();
  await w.reload(specOf([table('Revenue'), table('Orders')]));   // the assistant renamed Sales to Revenue
  const again = await cards(w);
  s0.el = nameOf(again.cardEls()[0]);
  s0.box.rerender(); await settleAll(); s0.box.rerender();
  assert.equal(w.held('spec|buf|table:Sales|name'), 'Sales EU', 'the words stay held under the table they were typed for');
  const orphansEl = find(w.view.result, (el) => el.type?.name === 'OrphanDrafts');
  assert.ok(orphansEl, 'the page draws the list of words whose box is gone');
  const orphans = mountEl(w.pg, orphansEl); await settleAll(); orphans.rerender();
  const row = byTestId(orphans.result, 'orphan-spec|buf|table:Sales|name');
  assert.ok(row, 'the words typed for Sales are listed');
  assert.match(flat(row), /Sales EU/);
  // What the box shows now: either the renamed table (the words are listed above) or the words with a moved note. Both keep the words in sight.
  const v = s0.box.result.props.value;
  assert.ok(v === 'Revenue' || v === 'Sales EU', `the box shows ${v}`);
});

test('C2 alone. a person renaming a table with nothing else changing: one commit with the words, the hold ends, no moved note, no listed words', async () => {
  const w = specWorld(specOf([table('Sales'), table('Orders')]));
  const { cardEls, editor } = await cards(w);
  const card = mountEl(w.pg, cardEls()[1]); await settleAll();
  const el = find(card.result, (x) => x.type?.name === 'NameInput' && x.props.ariaLabel === 'Table name');
  const commits = [];
  const live = { el: { ...el, props: { ...el.props, onCommit: (v) => { commits.push(v); el.props.onCommit(v); } } } };
  const box = w.pg.mount(() => live.el.type(live.el.props)); await settleAll();
  box.result.props.onChange({ target: { value: 'Orders EU' } }); await settleAll(); box.rerender();
  box.result.props.onBlur(); await settleAll();
  assert.deepEqual(commits, ['Orders EU'], 'the blur commits the words once');
  assert.equal(w.held('spec|buf|table:Orders|name'), undefined, 'the hold ends on the commit');
  // React hands the box the renamed table (the draft is now dirty with the rename).
  w.view.rerender(); await settleAll(); w.view.rerender(); editor.unmount();
  const again = await cards(w);
  const card2 = mountEl(w.pg, again.cardEls()[1]); await settleAll();
  const el2 = find(card2.result, (x) => x.type?.name === 'NameInput' && x.props.ariaLabel === 'Table name');
  assert.equal(el2.props.value, 'Orders EU', 'the spec on the page holds the new name');
  live.el = el2; box.rerender(); await settleAll(); box.rerender();
  assert.equal(box.result.props.value, 'Orders EU');
  assert.equal(box.result.props.style?.boxShadow, undefined, 'no moved outline');
  w.view.rerender(); await settleAll(); w.view.rerender();
  const moved = find(w.view.result, (x) => x.type?.name === 'MovedWords');
  const mv = mountEl(w.pg, moved); await settleAll();
  assert.equal(mv.result, null, 'no moved note');
  const orphans = mountEl(w.pg, find(w.view.result, (x) => x.type?.name === 'OrphanDrafts')); await settleAll(); orphans.rerender();
  assert.equal(findAll(orphans.result, (x) => /^orphan-/.test(x.props?.['data-testid'] ?? '')).length, 0, 'no words are listed as having no box');
});

// ---- C3: the Properties grid, an assistant rename of the selected object (propgrid.js:107, left NOT FIXED by the lane) ------------------------------
// The REAL media/propgrid/propgrid.js in the fake page the propgrid drafts test uses (copied here so that file stays as it is). The host's messages are the ones
// extension.ts sends on a rename it did not make: model/didChange -> propGrid.refresh() pushes the OLD refs, getObjectProperties fails for the gone ref and is
// caught to [], so a 'load' comes with the old key and no rows; then the tree selection moves (to the renamed node, or to nothing and the model) and a 'load'
// comes with a NEW key, which line 107 reads as a new selection.
const gridSource = readFileSync(join(webview, '..', 'media', 'propgrid', 'propgrid.js'), 'utf8');
// ---- a fake page ---------------------------------------------------------------------------------------------
const unesc = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');
const TAGS = 'input|textarea|select|button|div|span|label|code|em|datalist|option|optgroup';
function attrsOf(text) {
  const out = {};
  for (const m of text.matchAll(/([a-zA-Z][\w-]*)(?:="([^"]*)")?/g)) out[m[1]] = m[2] === undefined ? '' : unesc(m[2]);
  return out;
}
function makeEl(page, tag, attrs, inner) {
  const el = {
    tagName: tag.toUpperCase(), attrs, listeners: {}, style: {}, isConnected: true,
    type: tag === 'input' ? (attrs.type || 'text') : tag,
    value: tag === 'textarea' ? unesc(inner ?? '') : (attrs.value ?? ''),
    checked: 'checked' in attrs,
    innerHTML: inner ?? '',
    classList: { contains: (c) => (attrs.class || '').split(/\s+/).includes(c) },
    getAttribute: (n) => (n in attrs ? attrs[n] : null),
    hasAttribute: (n) => n in attrs,
    addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); },
    fire(type) { for (const fn of this.listeners[type] || []) fn({ preventDefault() {}, stopPropagation() {}, key: '', target: this }); },
    click() { this.fire('click'); },
    focus() { page.document.activeElement = this; },
    select() {}, scrollIntoView() {}, setSelectionRange() {}, remove() {},
    closest: () => null,
  };
  return el;
}
function parseMarkup(page, html) {
  const els = [];
  const re = new RegExp('<(' + TAGS + ')\\b([^>]*)>', 'g');
  for (const m of html.matchAll(re)) {
    const tag = m[1], attrs = attrsOf(m[2]);
    let inner;
    if (tag === 'textarea' || tag === 'div' || tag === 'span') {
      const end = html.indexOf('</' + tag + '>', m.index + m[0].length);
      if (end >= 0) inner = html.slice(m.index + m[0].length, end);
    }
    els.push(makeEl(page, tag, attrs, inner));
  }
  return els;
}
// A compound selector: tag, #id, .class and [attr] / [attr="v"] parts, no combinators (all the grid asks for).
function matches(el, sel) {
  const tag = sel.match(/^[a-z]+/);
  if (tag && el.tagName !== tag[0].toUpperCase()) return false;
  for (const m of sel.matchAll(/#([\w-]+)/g)) if (el.attrs.id !== m[1]) return false;
  for (const m of sel.replace(/\[[^\]]*\]/g, '').matchAll(/\.([\w-]+)/g)) if (!el.classList.contains(m[1])) return false;
  for (const m of sel.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)) { if (!(m[1] in el.attrs)) return false; if (m[2] !== undefined && el.attrs[m[1]] !== m[2]) return false; }
  return true;
}

/** One webview page over a persisted-state box. A new page over the same box is a view reload. */
function makeGridPage(box) {
  const posted = [];
  const listeners = { window: {}, document: {} };
  const page = {};
  let els = [];
  const body = { tagName: 'BODY', classList: { contains: () => false }, getAttribute: () => null };
  const rootEl = {
    html: '',
    set innerHTML(v) { this.html = v; for (const e of els) e.isConnected = false; els = parseMarkup(page, v); },
    get innerHTML() { return this.html; },
    querySelector: (sel) => els.find((e) => matches(e, sel)) || null,
    querySelectorAll: (sel) => els.filter((e) => matches(e, sel)),
  };
  page.document = {
    activeElement: body, body, documentElement: {},
    getElementById: (id) => (id === 'root' ? rootEl : els.find((e) => e.attrs.id === id) || null),
    querySelectorAll: () => [],
    addEventListener: (type, fn) => { (listeners.document[type] ??= []).push(fn); },
    createElement: () => makeEl(page, 'textarea', {}, ''),
  };
  const window = { addEventListener: (type, fn) => { (listeners.window[type] ??= []).push(fn); } };
  const api = {
    postMessage: (m) => posted.push(m),
    // VS Code hands the state back as a copy of what was set: it is serialized, never the same object.
    getState: () => (box.state === undefined ? undefined : JSON.parse(JSON.stringify(box.state))),
    setState: (s) => { box.state = JSON.parse(JSON.stringify(s)); return s; },
  };
  new Function('acquireVsCodeApi', 'document', 'window', 'navigator', 'getComputedStyle', 'Element', gridSource)(
    () => api, page.document, window, { platform: 'Linux' }, () => ({}), class {});
  const q = (sel) => rootEl.querySelector(sel);
  return {
    posted, document: page.document, root: rootEl, q,
    send: (m) => (listeners.window.message || []).forEach((fn) => fn({ data: m })),
    sets: () => posted.filter((m) => m.type === 'set'),
    html: () => rootEl.html,
    box: (name) => q('[data-prop="' + name + '"]'),
    /** Type into a property box (an input event per gesture, as the page listens). */
    type(name, text) { const el = q('[data-prop="' + name + '"]'); assert.ok(el, 'no box for ' + name); el.focus(); el.value = text; el.fire('input'); return el; },
    /** Leave the box: the change event the browser fires on blur. */
    change(name) { const el = q('[data-prop="' + name + '"]'); el.fire('change'); },
    click(sel) { const el = q(sel); assert.ok(el, 'nothing matches ' + sel); el.click(); },
    typeFx(text) { const ta = q('.fx-ta'); assert.ok(ta, 'the format editor is not open'); ta.focus(); ta.value = text; ta.fire('input'); },
    markOn: (name) => { const s = rootEl.querySelector('[data-draft-for="' + name + '"]'); return !!s && /Draft not saved/.test(s.innerHTML); },
  };
}


const FILE = '/m/Sales.SemanticModel';
const GP = (name, kind, value, extra = {}) => ({ name, displayName: name, category: 'Basic', kind, value, options: [], readOnly: false, varies: false, ...extra });
const measureLoad = (key, name, desc = 'Sum of sales.') => ({ type: 'load', key, model: FILE, name, multi: false, props: [GP('Name', 'string', name), GP('Description', 'string', desc), GP('DisplayFolder', 'string', 'KPIs')] });
const WORDS = 'Net sales after returns, in AUD. Excludes intercompany.';

// DEFECT: lost work with no notice. Words typed in the Properties grid for the selected measure leave the person's sight when the assistant renames that measure:
// nothing on the grid names them, no list holds them, and the next load is of the renamed object (or the model), which shows its stored description.
test('C3. words typed in the Properties grid stay in sight (or are named) when the assistant renames the selected object', () => {
  const box = {};
  const page = makeGridPage(box);
  page.send(measureLoad('["measure:Sales/Total Sales"]', 'Total Sales'));
  page.type('Description', WORDS);
  assert.ok(page.markOn('Description'));
  // The assistant renames Total Sales to Revenue over MCP. refresh() pushes the old ref: its properties read fails, so the load has no rows.
  page.send({ type: 'load', key: '["measure:Sales/Total Sales"]', model: FILE, name: 'Total Sales', multi: false, props: [] });
  // The tree shows the renamed measure and the grid follows it.
  page.send(measureLoad('["measure:Sales/Revenue"]', 'Revenue'));
  const held = Object.entries((box.state && box.state['propgrid.drafts']) || {});
  assert.ok(held.some(([k, v]) => k.includes('Total Sales') && v.value === WORDS), 'the words are still in the grid\'s persisted state, under the old ref');
  const shown = page.html().includes(WORDS) || page.root.querySelectorAll('input').some((e) => e.value === WORDS) || page.root.querySelectorAll('textarea').some((e) => e.value === WORDS);
  assert.ok(shown || /not on screen|renamed|kept for|words for/i.test(page.html()),
    'the words typed for Total Sales are nowhere on the grid after the assistant renamed it to Revenue, and nothing says they exist: the Description box shows "' + page.box('Description').value + '"');
});

test('C3 recovery. the held words come back only when an object with the OLD ref is shown again (for example after an Undo of the rename), with no write', () => {
  const box = {};
  const page = makeGridPage(box);
  page.send(measureLoad('["measure:Sales/Total Sales"]', 'Total Sales'));
  page.type('Description', WORDS);
  page.send({ type: 'load', key: '["measure:Sales/Total Sales"]', model: FILE, name: 'Total Sales', multi: false, props: [] });
  page.send(measureLoad('["measure:Sales/Revenue"]', 'Revenue'));
  assert.notEqual(page.box('Description').value, WORDS);
  page.send({ type: 'empty' });   // even a restart does not bring them to the renamed object
  page.send(measureLoad('["measure:Sales/Revenue"]', 'Revenue'));
  assert.notEqual(page.box('Description').value, WORDS);
  // Undo of the rename, then the person selects the measure under its old name.
  page.send(measureLoad('["measure:Sales/Total Sales"]', 'Total Sales'));
  assert.equal(page.box('Description').value, WORDS);
  assert.deepEqual(page.sets(), []);
});

test('C3 alone. a person typing a description with nothing else changing: the change event writes once, the load that shows it ends the draft', () => {
  const box = {};
  const page = makeGridPage(box);
  page.send(measureLoad('["measure:Sales/Total Sales"]', 'Total Sales'));
  page.type('Description', WORDS);
  page.change('Description');
  assert.equal(page.sets().length, 1);
  assert.equal(page.sets()[0].value, WORDS);
  page.send(measureLoad('["measure:Sales/Total Sales"]', 'Total Sales', WORDS));
  assert.ok(!page.markOn('Description'), 'no draft mark after the engine took the words');
  assert.equal(Object.keys((box.state && box.state['propgrid.drafts']) || {}).length, 0, 'nothing is held');
});

// ---- C4: a whole tab swapped out by an engine outage (EngineDown) or a licence check (FeatureChecking), App.tsx:777-785, "left by design" ---------------
// The tab's components unmount; when the engine is back they mount fresh. Words on the shared draft store come back; words in plain component state do not.
// Each test types, unmounts the component (the swap), mounts it again (the tab back) and reads the box.
function advPage() {
  const disk = makeDisk();
  const group = { ref: 'table:Time Intelligence', name: 'Time Intelligence', precedence: 10, items: [] };
  const pg = makePage({
    './bridge': { rpc: async (method) => (method === 'listCalculationGroups' ? [structuredClone(group)] : method === 'listRoles' ? [{ name: 'Readers', members: [], tableFilters: [] }] : {}), onDidChange: () => () => undefined,
      loadState: disk.loadState, saveState: disk.saveState, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}) },
    './daxeditor': { DaxField: function DaxField() { return null; } }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, MODEL);
  const adv = pg.load('advmodels.tsx');
  return { pg, adv, group, disk };
}
async function calcCard(a) {
  const panel = a.pg.mount(() => a.adv.CalcGroupsPanel());
  await settleAll(); panel.rerender();
  const el = find(panel.result, (x) => x.type?.name === 'CalcGroupCard');
  panel.unmount();
  const card = mountEl(a.pg, el); await settleAll();
  return card;
}

// DEFECT: (outage class; not another writer, so outside the lane's R-B brief; item for the join): a new calculation item's name and DAX, typed and not added,
// are gone when the engine goes away and comes back. Nothing holds or names them.
test('C4a. a new calculation item typed in the Add item form survives the tab being swapped out by an engine outage and back', { todo: 'item for the join: an outage or licence swap unmounts the tab; not another writer, outside the lane brief' }, async () => {
  const a = advPage();
  let card = await calcCard(a);
  find(card.result, (x) => x.type?.name === 'MiniButton' && /\+ Item/.test(flat(x))).props.onClick(); await settleAll();
  find(card.result, (x) => x.type === 'input' && /item name/.test(x.props?.placeholder ?? '')).props.onChange({ target: { value: 'YTD' } });
  find(card.result, (x) => x.type?.name === 'DaxField').props.onChange('CALCULATE ( SELECTEDMEASURE (), DATESYTD ( \'Date\'[Date] ) )');
  await settleAll();
  assert.equal(find(card.result, (x) => x.type === 'input' && /item name/.test(x.props?.placeholder ?? '')).props.value, 'YTD');
  card.unmount();   // EngineDown replaces the tab
  card = await calcCard(a);   // the engine is back: the tab mounts fresh
  const box = find(card.result, (x) => x.type === 'input' && /item name/.test(x.props?.placeholder ?? ''));
  assert.ok(box && box.props.value === 'YTD', 'the Add item form with "YTD" and its DAX is gone after the outage');
});

// DEFECT: (outage class, as above): digits typed in the precedence box (useKeptText holds them in component state only).
test('C4b. digits typed in the calc group precedence box survive the tab being swapped out and back', { todo: 'item for the join: an outage or licence swap unmounts the tab; not another writer, outside the lane brief' }, async () => {
  const a = advPage();
  let card = await calcCard(a);
  find(card.result, (x) => x.type === 'input' && x.props?.inputMode === 'numeric').props.onChange({ target: { value: '20' } }); await settleAll();
  card.unmount();
  card = await calcCard(a);
  assert.equal(find(card.result, (x) => x.type === 'input' && x.props?.inputMode === 'numeric').props.value, '20', 'the typed precedence 20 is gone after the outage');
});

test('C4c. control: an RLS row filter typed and not saved (useEditBase, held on the draft store) comes back after the tab is swapped out and back', async () => {
  const a = advPage();
  const role = { name: 'Readers', members: [], tableFilters: [] };
  const tableT = { ref: 'table:Sales', name: 'Sales', columns: [], measures: [] };
  const mountRow = () => a.pg.mount(() => a.adv.TableSecurityRow({ role, table: tableT, reload() {}, onError() {} }));
  let row = mountRow(); await settleAll(); row.rerender();
  const field = () => find(row.result, (x) => x.type?.name === 'DaxField');
  assert.ok(field(), 'the row filter box is drawn');
  field().props.onChange('[Region] = "EU"'); await settleAll(); row.rerender();
  row.unmount();
  row = mountRow(); await settleAll(); row.rerender();
  assert.equal(field().props.value, '[Region] = "EU"', 'the held row filter comes back');
});
