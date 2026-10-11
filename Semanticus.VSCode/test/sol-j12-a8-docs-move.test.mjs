// A8: drive the Documentation orphan control and the already mounted target.
// The moved paragraph must enter its editor before a new keystroke can replace it.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const WORDS = 'One row per order line. Returns have negative quantities.';
const byId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

test('A8: moving orphan documentation into the open section does not let typing erase it', async () => {
  const disk = makeDisk();
  const pg = makePage({
    './bridge': { loadState: disk.loadState, saveState: disk.saveState,
      rpc: async (method, ref) => {
        if (method === 'getDocSection') return '';
        if (method === 'setDocSection' && ref === 'table:Sales') throw new Error("No object 'table:Sales' in the model.");
        return {};
      },
      onDidChange: () => () => undefined, exportDoc() {}, printDoc() {}, openExternal() {} },
    './hooks': { useFillHeight() {}, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection() {} },
  }, MODEL);
  const D = pg.load('documentation.tsx');
  const O = pg.load('orphandrafts.tsx');
  const dto = { header: { name: 'M' }, modelNarrative: null, tables: [], measures: [] };
  const outline = { items: [{ ref: 'table:Revenue', name: 'Revenue', kind: 'table', sections: [] }] };
  const original = pg.mount(() => D.SectionEditor({ objRef: 'table:Sales', objName: 'Sales', sectionKey: 'overview', value: '', onSaved() {} }));
  find(original.result, (el) => el.type === 'textarea').props.onChange({ target: { value: WORDS } });
  await settleAll(); original.rerender();
  // Another writer renamed Sales. The old editor leaves the page and its
  // autosave is refused because its old ref is gone. Its words stay held.
  original.unmount();
  await settleAll();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'docs|table:Sales|overview')?.value, WORDS);
  const pane = pg.mount(() => D.NarrativePane({ dto, outline, onSaved() {} }));
  find(pane.result, (el) => el.type === 'select').props.onChange({ target: { value: 'table:Revenue' } });
  await settleAll(); pane.rerender();
  const editorElement = () => find(pane.result, (el) => el.type?.name === 'SectionEditor' && el.props.sectionKey === 'overview');
  let editorKey = editorElement().key;
  let editor = pg.mount(() => editorElement().type(editorElement().props));
  await settleAll(); editor.rerender();
  assert.equal(find(editor.result, (el) => el.type === 'textarea').props.value, '');
  const listerElement = () => find(pane.result, (el) => el.type?.name === 'OrphanDrafts');
  const lister = pg.mount(() => O.OrphanDrafts(listerElement().props));
  byId(lister.result, 'orphan-pick').props.onChange({ target: { value: 'table:Revenue|overview' } });
  await settleAll(); lister.rerender();
  byId(lister.result, 'orphan-keep').props.onClick();
  await settleAll(); pane.rerender();
  // Follow React's identity rule if the fix redraws the target with a new key.
  if (editorElement().key !== editorKey) {
    editor.unmount();
    editorKey = editorElement().key;
    editor = pg.mount(() => editorElement().type(editorElement().props));
  }
  editor.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'docs|table:Revenue|overview')?.value, WORDS,
    'the move itself kept the paragraph under the target');
  const box = find(editor.result, (el) => el.type === 'textarea');
  // Type at the end of what the target actually shows, as a person does.
  box.props.onChange({ target: { value: box.props.value + ' Reviewed.' } });
  await settleAll(); editor.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'docs|table:Revenue|overview')?.value, WORDS + ' Reviewed.',
    'the paragraph moved onto this section must survive the next keystroke');
});
