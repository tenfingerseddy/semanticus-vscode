// Join 11, the drafts class: the Spec tab's JSON editor keeps what the person typed when the tab goes away.
//
// "Edit JSON" held its text in component state only, so a tab switch, a restarted engine (App keys the page on the session id)
// or a reload closed the editor and dropped the text. It now holds a draft in the shared draft store (webview/src/drafts.ts) under
// `spec|json`, with the spec text it was typed over; the tab opens the JSON editor again on it, asks Keep mine / Use the new one
// when the assistant changed the spec meanwhile, and Apply then names the spec version it read. The draft ends on an Apply the
// engine took, Cancel (or Hide JSON), and Use the new one, never on an unmount. The real spec.tsx runs here (bridge faked).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, text, draftMark, movedBanner, settleAll, wait, heldKeys } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;   // the tab schedules its reload with window.setTimeout
const specOf = (version, tables) => ({ version, source: 'manual', spec: { name: 'M', tables: tables.map((name) => ({ name, role: 'fact', columns: [] })), relationships: [], measures: [], timeIntelligence: [] } });

function makeWorld() {
  const w = { snap: specOf(1, ['Sales']), calls: [], listeners: [] };
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'getSpec') return structuredClone(w.snap);
    if (method === 'setSpec') {
      const [json, , expected] = args;
      if (expected != null && expected !== w.snap.version) throw new Error('This spec changed in the model after you opened it, so your version was not saved.');
      w.snap = { version: w.snap.version + 1, source: 'manual', spec: JSON.parse(json) };
      return structuredClone(w.snap);
    }
    return {};
  };
  return w;
}

function open(disk, world, model = '/m/Model.bim') {
  const stubs = {
    './bridge': { rpc: world.rpc, onSpecChange: (fn) => { world.listeners.push(fn); return () => undefined; }, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined },
    './connection': { useConnection: () => ({ openConnections: () => undefined }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  };
  const pg = makePage(stubs, model);
  const { SpecView } = pg.load('spec.tsx');
  return { pg, SpecView };
}
async function mount({ pg, SpecView }) {
  const h = pg.mount(() => SpecView({ session: { modelName: 'M' } }));
  await settleAll(); await wait(10); await settleAll();
  const btn = (label) => find(h.result, (el) => (el.type?.name === 'Btn' || el.type === 'button') && text(el) === label);
  const box = () => find(h.result, (el) => el.type === 'textarea');
  return {
    h, btn, box,
    openJson: async () => { btn('Edit JSON').props.onClick(); await settleAll(); },
    type: async (value) => { box().props.onChange({ target: { value } }); await settleAll(); },
    apply: async () => { await btn('Apply JSON').props.onClick(); await settleAll(); },
  };
}
const typedSpec = (name) => JSON.stringify({ name, tables: [], relationships: [], measures: [], timeIntelligence: [] }, null, 2);
const sets = (w) => w.calls.filter((c) => c[0] === 'setSpec');

test('Spec JSON: text typed, the tab unmounted (tab switch) and mounted again: the editor opens on the text, marked as a draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openJson(); await a.type(typedSpec('Mine'));
  a.h.unmount();
  const b = await mount(page);
  assert.ok(b.box(), 'the JSON editor is open again');
  assert.equal(b.box().props.value, typedSpec('Mine'), 'with the typed text');
  assert.ok(draftMark(b.h.result), 'the quiet Draft not saved mark shows');
  assert.equal(movedBanner(b.h.result), null, 'nothing moved, so nothing to choose');
});

test('Spec JSON: text typed, the page reloads (an engine restart or a webview reload): the editor opens on the text', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openJson(); await a.type(typedSpec('Mine'));
  a.h.unmount();
  const b = await mount(open(disk, world));
  assert.equal(b.box()?.props.value, typedSpec('Mine'));
  assert.ok(draftMark(b.h.result));
});

test('Spec JSON: the assistant changed the spec meanwhile: the text stays, the choice is asked, and Keep mine applies naming the version it read', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openJson(); await a.type(typedSpec('Mine'));
  a.h.unmount();
  world.snap = specOf(2, ['Sales', 'Orders']);   // set_spec on the MCP door while the tab was away
  const b = await mount(open(disk, world));
  assert.equal(b.box()?.props.value, typedSpec('Mine'), 'the typed text is kept');
  assert.ok(movedBanner(b.h.result), 'the choice is asked');
  await b.apply();
  assert.equal(sets(world).length, 0, 'Apply writes nothing while the choice is open');
  movedBanner(b.h.result).props.onKeepMine(); await settleAll();
  await b.apply();
  const w = sets(world).at(-1);
  assert.ok(w, 'after Keep mine Apply writes');
  assert.equal(JSON.parse(w[1]).name, 'Mine');
  assert.equal(w[3], 2, 'and names the version it read, the assistant\'s');
});

test('Spec JSON: an Apply the engine took, Cancel, Hide JSON and Use the new one each end the draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openJson(); await a.type(typedSpec('Applied'));
  assert.equal(heldKeys(disk).length, 1, 'the typed text is held');
  await a.apply();
  assert.equal(world.snap.spec.name, 'Applied');
  assert.deepEqual(heldKeys(disk), [], 'an Apply the engine took ends the draft');

  await a.openJson(); await a.type(typedSpec('Cancelled'));
  a.btn('Cancel').props.onClick(); await settleAll();
  assert.deepEqual(heldKeys(disk), [], 'Cancel ends the draft');
  // F13 (drafts-sol): Hide JSON only puts the editor away. The words are still the person's, so they stay held; Cancel is the Discard.
  await a.openJson(); await a.type(typedSpec('Hidden'));
  a.btn('Hide JSON').props.onClick(); await settleAll();
  assert.equal(heldKeys(disk).length, 1, 'Hide JSON keeps the typed words held');
  await a.openJson();
  assert.equal(a.box().props.value, typedSpec('Hidden'), 'and Edit JSON opens on them, not on a fresh copy of the structured view');
  a.btn('Cancel').props.onClick(); await settleAll();
  assert.deepEqual(heldKeys(disk), [], 'Cancel is the discard');

  await a.openJson(); await a.type(typedSpec('Mine'));
  a.h.unmount();
  world.snap = specOf(world.snap.version + 1, ['Theirs']);
  const b = await mount(page);
  movedBanner(b.h.result).props.onUseNew(); await settleAll();
  assert.match(b.box().props.value, /Theirs/, 'Use the new one loads the assistant\'s spec');
  assert.deepEqual(heldKeys(disk), [], 'Use the new one ends the draft');
});

test('Spec JSON: a draft for another model is not shown', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openJson(); await a.type(typedSpec('Mine'));
  a.h.unmount();
  const b = await mount(open(disk, world, '/m/Other.bim'));
  assert.equal(b.box(), null, 'the JSON editor stays closed on another model');
  assert.equal(draftMark(b.h.result), null);
});

// ---- W2 (drafts fix round 2): the structured view's inline edits are held like Edit JSON ----------------------------
// Every inline control in the structured view funnels through SpecEditor's `edit` prop (spec.tsx), which is what a control's commit calls.
const editor = (a) => find(a.h.result, (el) => el.type?.name === 'SpecEditor');
const typeName = async (a, name) => { editor(a).props.edit((s) => { s.name = name; }); await settleAll(); };
const structKeys = (disk) => heldKeys(disk).filter((k) => k[1] === 'spec|struct');
const bannerBtn = (a, label) => find(a.h.result, (el) => el.type === 'button' && text(el) === label);

test('Spec structured edit: typed, the tab unmounted (tab switch): the edit is there, with Save and Discard', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await typeName(a, 'Typed name');
  assert.equal(structKeys(disk).length, 1, 'the edit is held in the shared store');
  a.h.unmount();
  const b = await mount(page);
  assert.equal(editor(b).props.spec.name, 'Typed name');
  assert.ok(b.btn('Save') && b.btn('Discard'), 'the tab still shows an unsaved edit');
  assert.equal(sets(world).length, 0, 'nothing was written by coming back');
});

test('Spec structured edit: typed, the page reloads (engine restart or webview reload): the edit is there, and Save names the version it started from', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await typeName(a, 'Typed name');
  a.h.unmount();
  const b = await mount(open(disk, world));
  assert.equal(editor(b).props.spec.name, 'Typed name');
  await b.btn('Save').props.onClick(); await settleAll();
  const w = sets(world).at(-1);
  assert.equal(JSON.parse(w[1]).name, 'Typed name', 'Save writes the typed edit');
  assert.equal(w[3], 1, 'and names the spec version the edit was typed over');
  assert.deepEqual(structKeys(disk), [], 'a Save the engine took ends the draft');
});

test('Spec structured edit: Discard ends the draft; a draft of another model is not shown', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await typeName(a, 'Typed name');
  a.h.unmount();
  const other = await mount(open(disk, world, '/m/Other.bim'));
  assert.equal(editor(other).props.spec.name, 'M', 'another model starts from its own spec');
  assert.equal(other.btn('Save'), null);
  const b = await mount(page);
  b.btn('Discard').props.onClick(); await settleAll();
  assert.equal(editor(b).props.spec.name, 'M');
  assert.deepEqual(structKeys(disk), [], 'Discard ends the draft');
});

test('Spec structured edit: the assistant changed the spec meanwhile: the edit stays, nothing is written, the choice is asked', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await typeName(a, 'Typed name');
  a.h.unmount();
  world.snap = specOf(2, ['Sales', 'Orders']);   // set_spec on the MCP door while the tab was away
  const b = await mount(page);
  assert.equal(editor(b).props.spec.name, 'Typed name', 'the typed edit is kept');
  assert.ok(bannerBtn(b, 'Keep mine') && bannerBtn(b, 'Load the server version'), 'the choice is asked');
  await b.btn('Save').props.onClick(); await settleAll();
  assert.equal(sets(world).length, 0, 'Save writes nothing while the choice is open');
  bannerBtn(b, 'Keep mine').props.onClick(); await settleAll();
  await b.btn('Save').props.onClick(); await settleAll();
  assert.equal(sets(world).at(-1)[3], 2, 'after Keep mine the write names the assistant\'s version');
  assert.deepEqual(structKeys(disk), []);
});

// ---- F9 (drafts-sol): the structured editor's name inputs keep their buffer by model and object, on every change -------------------------------------
// Sol join 10: NameInput held the typed text in its own state until blur or Enter. An engine disconnect replaces the editor with the recovery view and no blur
// comes, so the words were gone after the reconnect despite the shared store. The schema, entity, format-string and folder inputs are the same component.
const mountEl = (pg, el) => pg.mount(() => el.type(el.props));
function walkAll(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walkAll(n, visit)); return; }
  visit(node); walkAll(node.props?.children, visit);
}
const firstEl = (tree, pred) => { let hit = null; walkAll(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
/** The real input of the Table name field: SpecView draws SpecEditor, which draws TableCard, which draws the NameInput. */
async function tableNameInput(o) {
  const view = await mount(o);
  const editor = mountEl(o.pg, firstEl(view.h.result, (el) => el.type?.name === 'SpecEditor')); await settleAll();
  const card = mountEl(o.pg, firstEl(editor.result, (el) => el.type?.name === 'TableCard')); await settleAll();
  const name = mountEl(o.pg, firstEl(card.result, (el) => el.type?.name === 'NameInput' && el.props.ariaLabel === 'Table name'));
  return { view, name, input: () => name.result, unmount: () => { name.unmount(); card.unmount(); editor.unmount(); view.h.unmount(); } };
}

test('F9: a typed table name is held on every change, and is back after the engine went away (no blur)', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = open(disk, world);
  const n1 = await tableNameInput(a);
  n1.input().props.onChange({ target: { value: 'Sales renamed' } }); await settleAll();
  assert.ok(heldKeys(disk).some(([, scope]) => /^spec\|buf/.test(scope)), 'the buffer is in the store before any blur');
  n1.unmount();   // the engine disconnects: the recovery view replaces the editor, no blur fires
  // after the reconnect: a new page over the same persisted state
  const b = open(disk, world);
  const n2 = await tableNameInput(b);
  assert.equal(n2.input().props.value, 'Sales renamed', 'the input opens on what was typed');
});

test('F9: committing (blur) or reverting (Escape) ends the buffer; typing it back to the saved name holds nothing', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = open(disk, world);
  const n = await tableNameInput(a);
  n.input().props.onChange({ target: { value: 'Sales renamed' } }); await settleAll();
  assert.equal(heldKeys(disk).filter(([, scope]) => /^spec\|buf/.test(scope)).length, 1);
  n.input().props.onChange({ target: { value: 'Sales' } }); await settleAll();
  assert.equal(heldKeys(disk).filter(([, scope]) => /^spec\|buf/.test(scope)).length, 0, 'typed back to the saved name: nothing to hold');
  n.input().props.onChange({ target: { value: 'Sales renamed' } }); await settleAll();
  n.input().props.onBlur(); await settleAll();
  assert.equal(heldKeys(disk).filter(([, scope]) => /^spec\|buf/.test(scope)).length, 0, 'a commit ends the buffer (the edit is in the draft spec)');
  n.unmount();
});

// ---- F13 (drafts-sol): one Spec draft for both views -------------------------------------------------------------------------------------------------
// Sol UAT J10-A-004 / J10-R-001: JSON typed, Hide JSON, a structured cell edited, the page left: the JSON words were gone while the mark still said Draft not saved. Hide JSON used to end the
// JSON draft. Now both are held; when both are held the page asks which one Apply JSON may use, and neither silently overwrites the other.
test('F13: JSON typed, Hide JSON, a structured edit, leave and come back: both are held, the JSON opens with the words, and the page asks before Apply', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openJson(); await a.type(typedSpec('Json words'));
  a.btn('Hide JSON').props.onClick(); await settleAll();
  editor(a).props.edit((s) => { s.name = 'Structured name'; }); await settleAll();
  assert.equal(structKeys(disk).length, 1, 'the structured edit is held');
  assert.equal(heldKeys(disk).filter((k) => k[1] === 'spec|json').length, 1, 'and so are the JSON words');
  a.h.unmount();
  const b = await mount(page);
  assert.equal(b.box()?.props.value, typedSpec('Json words'), 'the JSON editor opens on the words');
  const ask = find(b.h.result, (el) => el.props?.['data-testid'] === 'spec-both-held');
  assert.ok(ask, 'both are held: the page asks');
  assert.equal(world.calls.filter((c) => c[0] === 'setSpec').length, 0);
  await b.apply();
  assert.equal(world.calls.filter((c) => c[0] === 'setSpec').length, 0, 'Apply JSON waits for the choice');
  find(b.h.result, (el) => el.props?.testid === 'spec-both-keep-json').props.onClick(); await settleAll();
  await b.apply();
  assert.equal(world.snap.spec.name, 'Json words', 'after the choice Apply writes the JSON words');
});

test('F13: opening Edit JSON with structured edits and no JSON words starts from the structured edits, and a kept JSON draft is never overwritten by opening it again', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  editor(a).props.edit((s) => { s.name = 'Structured name'; }); await settleAll();
  await a.openJson();
  assert.match(a.box().props.value, /Structured name/, 'a fresh JSON editor starts from what the structured view shows');
  await a.type(typedSpec('Json words'));
  a.btn('Hide JSON').props.onClick(); await settleAll();
  editor(a).props.edit((s) => { s.name = 'Another structured name'; }); await settleAll();
  await a.openJson();
  assert.equal(a.box().props.value, typedSpec('Json words'), 'the typed JSON is not replaced by a copy of the structured view');
});
