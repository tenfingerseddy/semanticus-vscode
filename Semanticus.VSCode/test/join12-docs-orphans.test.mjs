// Join 12, J-D1: Documentation narrative words held under docs|<object ref>|<section> for an object the assistant renamed or deleted are listed on the Narrative pane with
// Keep mine on it and Discard (they were kept but no page named them). The lister is the shared OrphanDrafts; the ids it treats as live are `<object ref>|<section key>`.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const heldAll = (disk) => JSON.stringify([...disk.map.values()]);

function setup() {
  const disk = makeDisk();
  const pg = makePage({
    './bridge': { rpc: async (m) => { if (m === 'setDocSection') throw new Error("No object 'table:Sales' in the model."); return ''; },   // the engine: the old ref is gone after the rename
       onDidChange: () => () => undefined, exportDoc() {}, printDoc() {}, openExternal() {}, loadState: disk.loadState, saveState: disk.saveState },
    './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection: () => undefined },
  }, MODEL);
  return { disk, pg, D: pg.load('documentation.tsx'), O: pg.load('orphandrafts.tsx') };
}
const outlineOf = (...names) => ({ items: names.map((n) => ({ ref: `table:${n}`, name: n, kind: 'table', sections: [] })) });
const dto = { header: { name: 'M' }, modelNarrative: null, tables: [], measures: [] };

async function heldForSales({ pg, D }) {
  const ed = pg.mount(() => D.SectionEditor({ objRef: 'table:Sales', objName: 'Sales', sectionKey: 'overview', value: '', onSaved: () => undefined })); await settleAll(); ed.rerender();
  find(ed.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'One row per order line.' } }); await settleAll(); ed.rerender();
  return ed;
}

test('J-D1. words held for a table the assistant renamed are listed on the Narrative pane, and Keep mine on it moves them to the renamed table', async () => {
  const s = setup();
  const ed = await heldForSales(s); ed.unmount(); await settleAll();
  const pane = s.pg.mount(() => s.D.NarrativePane({ dto, outline: outlineOf('Revenue'), onSaved: () => undefined })); await settleAll(); pane.rerender();
  const lister = find(pane.result, (el) => el.type?.name === 'OrphanDrafts' && el.props.prefix === 'docs|');
  assert.ok(lister, 'the pane draws the lister for docs|');
  assert.ok([...lister.props.live].includes('table:Revenue|overview') && ![...lister.props.live].some((id) => id.startsWith('table:Sales|')), 'Revenue is live, Sales is not');
  const shown = s.pg.mount(() => s.O.OrphanDrafts(lister.props)); await settleAll(); shown.rerender();
  const row = find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-docs|table:Sales|overview');
  assert.ok(row, 'the held words are listed');
  find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-pick').props.onChange({ target: { value: 'table:Revenue|overview' } }); await settleAll(); shown.rerender();
  find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-keep').props.onClick(); await settleAll();
  const raw = heldAll(s.disk);
  assert.ok(raw.includes('docs|table:Revenue|overview') && !raw.includes('docs|table:Sales|overview'), 'the words moved to the renamed table');
  assert.ok(raw.includes('One row per order line.'));
});

test('J-D1. Discard ends the words; a table that is still there is not listed (control)', async () => {
  const s = setup();
  const ed = await heldForSales(s); ed.unmount(); await settleAll();
  const same = s.pg.mount(() => s.D.NarrativePane({ dto, outline: outlineOf('Sales'), onSaved: () => undefined })); await settleAll(); same.rerender();
  const l0 = find(same.result, (el) => el.type?.name === 'OrphanDrafts');
  const none = s.pg.mount(() => s.O.OrphanDrafts(l0.props)); await settleAll(); none.rerender();
  assert.equal(none.result, null, 'Sales is still there: nothing is listed');
  const gone = s.pg.mount(() => s.O.OrphanDrafts({ ...l0.props, live: ['model|overview'] })); await settleAll(); gone.rerender();
  find(gone.result, (el) => el.props?.['data-testid'] === 'orphan-discard').props.onClick(); await settleAll();
  assert.ok(!heldAll(s.disk).includes('One row per order line.'), 'Discard ended the words');
});
