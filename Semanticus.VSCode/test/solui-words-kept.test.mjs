// Join 11 solui, U2 (Sol's A6 finding 2) and rule R-B: a draft ends only when the engine accepted exactly that text or the person discarded it.
// A box that holds typed words keeps them when its live value moves, shows the newer value, and a blur is not a decision. Sol's test (sol-a6-spec-buffer.test.mjs) proves the one
// reported path; this file proves every way out of the held state, on the same real NameInput and real draft store.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, settleAll, text } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
const MODEL = 'file:/m/Model.bim';
const HOLD = 'spec|buf|model|name';

async function world() {
  const disk = makeDisk();
  const snap = { version: 1, source: 'manual', spec: { name: 'Original', tables: [], measures: [], relationships: [], timeIntelligence: [] } };
  const pg = makePage({
    './bridge': { rpc: async (method) => method === 'getSpec' ? structuredClone(snap) : {}, onSpecChange: () => () => undefined, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources() {} },
    './connection': { useConnection: () => ({ openConnections() {} }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null },
    './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, MODEL);
  const { SpecView } = pg.load('spec.tsx');
  const view = pg.mount(() => SpecView({ session: { modelName: 'Original' } }));
  await settleAll();
  const editor = find(view.result, (el) => el.type?.name === 'SpecEditor');
  let nameInput = null;
  pg.mount(() => { nameInput = find(editor.type(editor.props), (el) => el.type?.name === 'NameInput' && el.props?.ariaLabel === 'Model name'); return null; });
  const movedWords = find(view.result, (el) => el.type?.name === 'MovedWords');
  assert.ok(nameInput && movedWords, 'the Spec page has the model name box and the moved-words list');
  const commits = [];
  const state = { live: 'Original' };
  const input = pg.mount(() => nameInput.type({ ...nameInput.props, value: state.live, onCommit: (v) => commits.push(v) }));
  const notice = pg.mount(() => movedWords.type(movedWords.props));
  const held = () => pg.drafts.draftStore.get(MODEL, HOLD)?.value;
  const type = async (words) => { input.result.props.onChange({ target: { value: words } }); await settleAll(); };
  const move = async (live) => { state.live = live; input.rerender(); await settleAll(); notice.rerender(); };
  const listed = () => findAll(notice.result, (el) => el.props?.['data-testid'] === 'spec-words-keep-mine').length;
  const click = async (testid) => { find(notice.result, (el) => el.props?.['data-testid'] === testid).props.onClick(); await settleAll(); notice.rerender(); };
  const noticeText = () => JSON.stringify(notice.result);
  return { input, notice, commits, held, type, move, listed, click, noticeText, text };
}

test('words typed in a box stay in it, held, and are listed with the newer value, after the live value moves and the box loses focus', async () => {
  const w = await world();
  await w.type('My unfinished model name');
  await w.move('Assistant name');
  assert.equal(w.input.result.props.value, 'My unfinished model name');
  assert.equal(w.listed(), 1, 'the newer value is shown to the person');
  assert.match(w.noticeText(), /Assistant name/);
  w.input.result.props.onBlur();
  await settleAll();
  assert.equal(w.input.result.props.value, 'My unfinished model name', 'a blur is not a decision');
  assert.equal(w.held(), 'My unfinished model name', 'and it does not end the hold');
  assert.deepEqual(w.commits, [], 'nothing was written');
});

test('Keep mine writes the typed words and ends the hold; Use the new one shows the live value and ends the hold', async () => {
  const keep = await world();
  await keep.type('Mine'); await keep.move('Theirs');
  await keep.click('spec-words-keep-mine');
  assert.deepEqual(keep.commits, ['Mine']);
  assert.equal(keep.held(), undefined);
  assert.equal(keep.listed(), 0);

  const use = await world();
  await use.type('Mine'); await use.move('Theirs');
  await use.click('spec-words-use-new');
  assert.equal(use.input.result.props.value, 'Theirs');
  assert.equal(use.held(), undefined);
  assert.deepEqual(use.commits, [], 'the discarded words were never written');
  assert.equal(use.listed(), 0);
});

test('the live value becoming exactly the typed words ends the hold (the engine accepted that text); Escape discards', async () => {
  const same = await world();
  await same.type('Same words'); await same.move('Same words');
  assert.equal(same.held(), undefined, 'accepted exactly: nothing is left to hold');
  assert.equal(same.input.result.props.value, 'Same words');
  assert.equal(same.listed(), 0);

  const esc = await world();
  await esc.type('Mine'); await esc.move('Theirs');
  esc.input.result.props.onKeyDown({ key: 'Escape', target: { blur: () => esc.input.result.props.onBlur() } });
  await settleAll();
  assert.equal(esc.input.result.props.value, 'Theirs');
  assert.equal(esc.held(), undefined);
});

test('a box with nothing typed still follows the live value, and a plain blur with the live value unmoved still commits and ends the hold', async () => {
  const idle = await world();
  await idle.move('Assistant name');
  assert.equal(idle.input.result.props.value, 'Assistant name');
  assert.equal(idle.listed(), 0);

  const plain = await world();
  await plain.type('Typed');
  plain.input.result.props.onBlur();
  await settleAll();
  assert.deepEqual(plain.commits, ['Typed']);
  assert.equal(plain.held(), undefined);
});
