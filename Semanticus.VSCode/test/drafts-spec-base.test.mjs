// Round 3 (join 11, drafts): the Spec structured edit is held over the CONTENT of the spec it was typed over, never a version number.
// A spec version is a per-session counter in the engine's memory (Spec.cs SpecStore); a restarted engine starts again at 0, so the first spec set there is "v1" again
// and a version match proved nothing (V4 in verify-drafts-check-2.test.mjs). After a restart the engine holds no spec until someone sets one.
//   same content, any version      no ask, and Save names the version of the read whose content matched
//   different content, same version  the ask; Save writes nothing
//   no spec at all (the engine restarted, empty)  the page says so, never "changed elsewhere (now v0)"; Keep mine then saves over the empty spec
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, text, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
const specOf = (version, tables) => ({ version, source: 'manual', spec: tables && { name: 'M', tables: tables.map((name) => ({ name, role: 'fact', columns: [] })), relationships: [], measures: [], timeIntelligence: [] } });
function world(version, tables) {
  const w = { snap: specOf(version, tables), calls: [] };
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
const page = (disk, w) => {
  const pg = makePage({
    './bridge': { rpc: w.rpc, onSpecChange: () => () => undefined, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined }, './connection': { useConnection: () => ({ openConnections: () => undefined }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, '/m/Model.bim');
  return { pg, SpecView: pg.load('spec.tsx').SpecView };
};
async function mount({ pg, SpecView }) {
  const h = pg.mount(() => SpecView({ session: { modelName: 'M' } }));
  await settleAll(); await wait(10); await settleAll();
  return { h, btn: (label) => find(h.result, (el) => (el.type?.name === 'Btn' || el.type === 'button') && text(el) === label),
    editor: () => find(h.result, (el) => el.type?.name === 'SpecEditor'), asked: () => find(h.result, (el) => el.type === 'button' && text(el) === 'Keep mine'),
    page: () => JSON.stringify(h.result) };
}
const typeName = async (m) => { m.editor().props.edit((s) => { s.name = 'Typed name'; }); await settleAll(); };
const sets = (w) => w.calls.filter((c) => c[0] === 'setSpec');

test('after a restart that left an EQUAL spec (any version number): no ask, and Save names the version of the read whose content matched', async () => {
  const disk = makeDisk();
  const a = await mount(page(disk, world(7, ['Sales']))); await typeName(a); a.h.unmount();
  const restarted = world(1, ['Sales']);   // the same spec content, a different counter: the new engine had it set again
  const b = await mount(page(disk, restarted));
  assert.equal(b.editor().props.spec.name, 'Typed name', 'the held edit is shown');
  assert.equal(b.asked(), null, 'the content is the one it was typed over: nothing to ask');
  await b.btn('Save').props.onClick(); await settleAll();
  assert.equal(sets(restarted).length, 1);
  assert.equal(sets(restarted)[0][3], 1, 'the save names the version of the read that matched (1), not the old engine\'s 7');
  assert.equal(JSON.parse(sets(restarted)[0][1]).name, 'Typed name');
});

test('after a restart that left a DIFFERENT spec, even at the same version number: the ask is shown and Save writes nothing', async () => {
  const disk = makeDisk();
  const a = await mount(page(disk, world(1, ['Sales']))); await typeName(a); a.h.unmount();
  const other = world(1, ['Orders']);
  const b = await mount(page(disk, other));
  assert.ok(b.asked(), 'Keep mine / Load the server version is asked');
  assert.match(b.page(), /changed elsewhere \(now v1\)/);
  await b.btn('Save').props.onClick(); await settleAll();
  assert.equal(sets(other).length, 0, 'nothing was written over the spec the person never saw');
  assert.equal(b.editor().props.spec.name, 'Typed name', 'and the words are kept');
});

test('after a restart with NO spec held: the page says the model holds none, not "changed elsewhere (now v0)"; Keep mine then saves', async () => {
  const disk = makeDisk();
  const a = await mount(page(disk, world(3, ['Sales']))); await typeName(a); a.h.unmount();
  const empty = world(0, null);
  const b = await mount(page(disk, empty));
  assert.ok(b.asked(), 'the choice is asked');
  assert.match(b.page(), /The model holds no spec now/, 'and says what happened');
  assert.doesNotMatch(b.page(), /now v0/);
  await b.btn('Save').props.onClick(); await settleAll();
  assert.equal(sets(empty).length, 0, 'Save waits for the choice');
  b.asked().props.onClick(); await settleAll();
  await b.btn('Save').props.onClick(); await settleAll();
  assert.equal(sets(empty).length, 1, 'after Keep mine the save goes through');
  assert.equal(sets(empty)[0][3], 0, 'naming the version of the read that matched');
});

test('the held draft stores the content it was typed over, no version', async () => {
  const disk = makeDisk();
  const a = await mount(page(disk, world(5, ['Sales']))); await typeName(a);
  const held = Object.values(disk.map.get('studio.drafts') ?? {}).find((d) => d.value?.spec);
  assert.ok(held, 'a struct draft is held');
  assert.equal(typeof held.base.content, 'string');
  assert.equal(held.base.version, undefined, 'no version is held as a base');
  assert.equal(held.value.version, undefined);
});
