import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;

test('a live spec update followed by blur does not delete a model name that the person has not committed', async () => {
  const disk = makeDisk();
  let snap = { version: 1, source: 'manual', spec: { name: 'Original', tables: [], measures: [], relationships: [], timeIntelligence: [] } };
  let changed;
  const pg = makePage({
    './bridge': { rpc: async (method) => method === 'getSpec' ? structuredClone(snap) : {},
      onSpecChange: (fn) => { changed = fn; return () => undefined; }, pickSpecFile: async () => null,
      loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources() {} },
    './connection': { useConnection: () => ({ openConnections() {} }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null },
    './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, 'file:/m/Model.bim');
  const { SpecView } = pg.load('spec.tsx');
  const view = pg.mount(() => SpecView({ session: { modelName: 'Original' } }));
  await settleAll();
  const inputElement = () => {
    const editor = find(view.result, (el) => el.type?.name === 'SpecEditor');
    return find(editor.type(editor.props), (el) => el.type?.name === 'NameInput' && el.props?.ariaLabel === 'Model name');
  };
  const input = pg.mount(() => { const el = inputElement(); return el.type(el.props); });
  input.result.props.onChange({ target: { value: 'My unfinished model name' } });
  await settleAll();
  assert.equal(input.result.props.value, 'My unfinished model name');
  assert.equal(pg.drafts.draftStore.get('file:/m/Model.bim', 'spec|buf|model|name')?.value, 'My unfinished model name');

  // The assistant changes the spec while the box still has focus. This is a real onSpecChange reload.
  snap = { ...snap, version: 2, spec: { ...snap.spec, name: 'Assistant name' } };
  changed();
  await wait(300); await settleAll(); input.rerender();
  input.result.props.onBlur();
  await settleAll();
  const held = pg.drafts.draftStore.get('file:/m/Model.bim', 'spec|buf|model|name')?.value;
  const visible = input.result.props.value;
  assert.ok(held === 'My unfinished model name' || visible === 'My unfinished model name',
    `The typed name must remain visible or held. Visible: ${visible}; held: ${held}`);
});
