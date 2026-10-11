// An editor that saves a whole thing must not save a stale copy over the assistant's newer change.
//
// UAT 2 Oct. UAT-F-S06a-1-002 (H4a): the Notes insight editor opened with the OLD text after the assistant changed the
// lesson over MCP, and Save wrote the old text back. UAT-F-S06b-2-007 (H4b): the Validation source-mapping drawer kept the
// OLD mapping after MCP changed it, and Save overwrote it. Both are lost work, and the person was never told.
//
// CLASS: an editor that saves a whole object from a copy it took when it opened. The rule (webview/src/editbase.tsx):
//   the copy is taken from what is live when the editor opens; a live change with nothing typed is followed; a live change
//   with words typed is asked about ("Keep mine" / "Use the new one"); Save reads the live thing again first, and a Save
//   whose starting point is older than the live thing writes nothing until the person chose.
//
// The REAL components run here (their hooks driven by a tiny hook runtime, the bridge faked). Sections:
//   1 the guard itself            2 the Notes insight editor (I1)         3 the source-mapping drawer (I2)
//   4 one surface per family      5 every whole-object save in the app goes through the guard
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = (name) => readFileSync(resolve(root, 'webview', 'src', name), 'utf8');

/** Every element in an unrendered tree (function components stay elements, so the props they were given are readable). */
function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const findAll = (tree, pred) => { const out = []; walk(tree, (el) => { if (pred(el)) out.push(el); }); return out; };
const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('');
const button = (tree, label) => find(tree, (el) => (typeof el.type === 'function' || el.type === 'button') && text(el) === label);
const field = (tree, label) => find(tree, (el) => el.props?.['aria-label'] === label);
const hasBanner = (tree) => !!find(tree, (el) => el.type?.name === 'EditMovedBanner');

/** A runtime holds ONE component's hook slots, so every mounted component gets its own, with its own copy of the modules. */
const fresh = (stubs) => { const rt = makeHookRuntime(); return { rt, load: makeLoader(stubs, rt.React) }; };

// The guard holds typed words in the shared draft store (drafts.ts), which reads the webview's persisted state through the bridge: a bridge with none.
const noState = { './bridge': { loadState: (_k, fallback) => fallback, saveState: () => undefined } };

// ---- 1 the guard ----------------------------------------------------------------------------------------
{
  /** The guard in a tiny component of its own. */
  const guardHost = (props) => {
    const { rt, load } = fresh(noState);
    const { useEditBase } = load('editbase.tsx');
    const h = rt.renderHook(() => useEditBase({ ...props(), reseed: (v) => { props.reseeded(v); h.rerender(); } }));
    return h;
  };
  const { reconcile, sameValue } = fresh(noState).load('editbase.tsx');

  test('guard: reconcile says same, follow or ask, and an equal copy is never a change', () => {
    assert.equal(reconcile('a', 'a', true), 'same');
    assert.equal(reconcile('a', 'b', false), 'follow');
    assert.equal(reconcile('a', 'b', true), 'ask');
    assert.ok(sameValue({ b: 1, a: [1, { y: 2, x: 1 }] }, { a: [1, { x: 1, y: 2 }], b: 1 }), 'key order is not a change');
    assert.ok(sameValue(undefined, null));
  });

  test('guard: follows a live change while nothing is typed, asks once words are typed, and Save refuses until chosen', async () => {
    const s = { live: 'one', draft: 'one', now: 'one' };
    const props = () => ({ current: s.live, draft: s.draft, editing: true, readNow: async () => s.now });
    props.reseeded = (v) => { s.draft = v; };
    const h = guardHost(props);
    await settle();
    s.live = 'two'; s.now = 'two'; h.rerender(); await settle();
    assert.equal(s.draft, 'two', 'a clean editor follows the live change');
    assert.equal(h.result.moved, false);
    s.draft = 'mine'; h.rerender(); await settle();
    s.live = 'three'; s.now = 'three'; h.rerender(); await settle();
    assert.equal(s.draft, 'mine', 'typed words are never replaced');
    assert.equal(h.result.moved, true, 'the person is asked');
    assert.equal(await h.result.beforeSave(), false, 'Save writes nothing while the choice is open');
    h.result.keepMine(); h.rerender(); await settle();
    assert.equal(h.result.moved, false);
    assert.equal(await h.result.beforeSave(), true, 'after Keep mine the next Save writes');
    s.live = 'four'; s.now = 'four'; h.rerender(); await settle();
    assert.equal(h.result.moved, true);
    h.result.useNew(); h.rerender(); await settle();
    assert.equal(s.draft, 'four', 'Use the new one loads it');
    assert.equal(h.result.moved, false);
  });

  test('guard: a Save re-reads, so a change the page has not heard about yet is still caught; the person\'s own save is not a conflict', async () => {
    const s = { live: 'one', draft: 'mine', now: 'two' };
    const props = () => ({ current: s.live, draft: s.draft, editing: true, readNow: async () => s.now });
    props.reseeded = (v) => { s.draft = v; };
    const h = guardHost(props);
    await settle();
    assert.equal(await h.result.beforeSave(), false, 'the page still says one but the engine already holds two');
    h.rerender(); await settle();
    assert.equal(h.result.moved, true);
    s.live = 'mine'; s.now = 'mine'; h.rerender(); await settle();   // my own save lands
    assert.equal(h.result.moved, false, 'live equal to my draft is not a conflict');
    assert.equal(await h.result.beforeSave(), true);
  });

  test('guard: a different scope is a different thing (another table), not a change to this one', async () => {
    const s = { live: { s: 'a' }, draft: { s: 'typed' }, scope: 'T1' };
    const props = () => ({ current: s.live, draft: s.draft, editing: true, scope: s.scope });
    props.reseeded = (v) => { s.draft = v; };
    const h = guardHost(props);
    await settle();
    s.scope = 'T2'; s.live = { s: 'b' }; h.rerender(); await settle();
    assert.deepEqual(s.draft, { s: 'b' }, 'the draft restarts from the new thing');
    assert.equal(h.result.moved, false);
  });
}

// ---- 2 the Notes insight editor (I1, UAT-F-S06a-1-002) ---------------------------------------------------
{
  const calls = [];
  let engineInsight = null;
  const stubs = {
    './bridge': {
      rpc: async (method, ...args) => {
        calls.push([method, ...args]);
        if (method === 'listInsights') return { insights: [engineInsight], skippedCorruptLines: 0 };
        return undefined;
      },
      onActivity: () => () => undefined,
    },
    './pro': { PENDING_ACCESS: '', ProPill: () => null, useFeature: () => ({}) },
    './ui': { PathDisclosure: () => null },
    './docrender': { mdToHtml: (s) => s },
  };
  const record = (text, keys) => ({ id: 'i1', text, kind: 'insight', keys, status: 'approved', score: 3, uses: 0, retrievals: 0, scope: 'model', provenance: { origin: 'agent' } });
  const mount = (rec, withStubs = stubs) => {
    const { rt, load } = fresh(withStubs);
    const { InsightCard } = load('knowledge.tsx');
    const state = { rec };
    const h = rt.renderHook(() => InsightCard({ rec: state.rec, onChanged: () => calls.push(['changed']) }));
    return { h, state };
  };

  test('I1: Edit opens on what the assistant wrote, not on what the card first drew (UAT-F-S06a-1-002)', async () => {
    calls.length = 0;
    const { h, state } = mount(record('Original text', ['sales']));
    await settle();
    // the assistant rewords the lesson and adds a key (edit_insight); the row updates live
    state.rec = record('Assistant text', ['sales', 'ratio']); engineInsight = state.rec; h.rerender(); await settle();
    button(h.result, 'Edit').props.onClick(); await settle();
    assert.equal(field(h.result, 'Insight text').props.value, 'Assistant text', 'the editor opens with the current text');
    assert.equal(field(h.result, 'Match keys').props.value, 'sales, ratio', 'and the current keys');
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    const edit = calls.find((c) => c[0] === 'editInsight');
    assert.ok(edit, 'an unchanged Save still writes');
    assert.equal(edit[2], 'Assistant text', 'Save keeps the assistant\'s text (the old copy is never written back)');
    assert.deepEqual(edit[3], ['sales', 'ratio']);
  });

  test('I1: words typed while the assistant rewrites the lesson are kept, and Save asks before overwriting', async () => {
    calls.length = 0;
    const { h, state } = mount(record('Original text', ['sales']));
    await settle();
    button(h.result, 'Edit').props.onClick(); await settle();
    field(h.result, 'Insight text').props.onChange({ target: { value: 'My wording' } }); await settle();
    state.rec = record('Assistant text', ['sales']); engineInsight = state.rec; h.rerender(); await settle();
    assert.equal(field(h.result, 'Insight text').props.value, 'My wording', 'what the person typed is not replaced');
    assert.ok(hasBanner(h.result), 'the person is told it changed');
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'editInsight'), 'nothing is written while the choice is open');
    find(h.result, (el) => el.type?.name === 'EditMovedBanner').props.onKeepMine(); await settle();
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    assert.equal(calls.find((c) => c[0] === 'editInsight')?.[2], 'My wording', 'after Keep mine the person\'s words are saved');
  });

  test('I1: a Save whose card has not heard of the change yet re-reads and still refuses (a missed live event)', async () => {
    calls.length = 0;
    const { h } = mount(record('Original text', ['sales']));
    await settle();
    button(h.result, 'Edit').props.onClick(); await settle();
    field(h.result, 'Insight text').props.onChange({ target: { value: 'My wording' } }); await settle();
    engineInsight = record('Assistant text', ['sales']);   // the engine moved; the card was never told
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'editInsight'), 'the stale card does not overwrite');
    assert.ok(hasBanner(h.result));
  });

  test('I1: a save that fails keeps the editor open with the typed words', async () => {
    const failing = { ...stubs, './bridge': { ...stubs['./bridge'], rpc: async (m) => { if (m === 'editInsight') throw new Error('engine said no'); return { insights: [engineInsight] }; } } };
    engineInsight = record('Original text', ['sales']);
    const { h } = mount(engineInsight, failing);
    await settle();
    button(h.result, 'Edit').props.onClick(); await settle();
    field(h.result, 'Insight text').props.onChange({ target: { value: 'Words I typed' } }); await settle();
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    assert.equal(field(h.result, 'Insight text')?.props.value, 'Words I typed', 'the words are still in the open editor');
  });
}

// ---- 3 the source-mapping drawer (I2, UAT-F-S06b-2-007) -------------------------------------------------
{
  const calls = [];
  const rows = { current: [] };
  let activity = [];
  const stubs = {
    './bridge': {
      rpc: async (method, ...args) => {
        calls.push([method, ...args]);
        if (method === 'listTableMappings') return { rows: rows.current };
        return undefined;
      },
      onActivity: (fn) => { activity.push(fn); return () => { activity = activity.filter((f) => f !== fn); }; },
    },
    './connection': { useConnection: () => ({}) },
    './objectbrowser': { ObjectBrowser: () => null },
    './surface-escape': { useSurfaceEscape: () => undefined },
    './askdata-model': { checkTitle: () => '' }, './daxeditor': { useDaxModelContext: () => ({ columns: [] }) },
  };
  const row = (entity, source = 's1') => ({ modelTable: 'Sales', source: 'mapped', sqlSourceId: source, schema: 'dbo', entity, canCount: true, editable: true });
  const mount = () => {
    const closed = [];
    const { rt, load } = fresh(stubs);
    const { TableRowCountForm } = load('tests-authoring.tsx');
    const h = rt.renderHook(() => TableRowCountForm({
      sources: [{ id: 's1', name: 'Warehouse' }], adopt: null, table: 'Sales', busy: false, setBusy: () => undefined, setErr: () => undefined,
      onSaved: () => closed.push('saved'), onClose: () => closed.push('closed'), onAddSqlSource: () => undefined,
    }));
    return { h, closed };
  };
  const entityBox = (tree) => find(tree, (el) => el.type === 'input' && el.props.placeholder === 'orders');
  const saveButton = (tree) => find(tree, (el) => typeof el.type === 'function' && el.props?.saveLabel === 'Save mapping');
  const fireActivity = async () => { [...activity].forEach((fn) => fn({ kind: 'set_table_source_mapping' })); await settle(); await settle(); };

  test('I2: the drawer follows a mapping the assistant changed while it was open, and Save keeps it (UAT-F-S06b-2-007)', async () => {
    calls.length = 0; rows.current = [row('Sales')];
    const { h } = mount(); await settle(); await settle();
    assert.equal(entityBox(h.result).props.value, 'Sales', 'the drawer shows the current mapping when it opens');
    rows.current = [row('Sales_S06b_MCP')];   // set_table_source_mapping over MCP
    await fireActivity();
    assert.equal(entityBox(h.result).props.value, 'Sales_S06b_MCP', 'the open drawer shows the assistant\'s mapping');
    saveButton(h.result).props.onSave(); await settle(); await settle();
    const save = calls.find((c) => c[0] === 'setTableSourceMapping');
    assert.equal(save?.[4], 'Sales_S06b_MCP', 'Save does not write the old entity back');
  });

  test('I2: a mapping typed while the assistant changes it is kept, and Save asks first', async () => {
    calls.length = 0; rows.current = [row('Sales')];
    const { h, closed } = mount(); await settle(); await settle();
    entityBox(h.result).props.onChange({ target: { value: 'My_Table' } }); await settle();
    rows.current = [row('Sales_S06b_MCP')];
    await fireActivity();
    assert.equal(entityBox(h.result).props.value, 'My_Table', 'typing is not replaced');
    assert.ok(hasBanner(h.result), 'the person is told the mapping changed');
    saveButton(h.result).props.onSave(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setTableSourceMapping'), 'nothing is written while the choice is open');
    assert.deepEqual(closed, [], 'the drawer stays open');
    find(h.result, (el) => el.type?.name === 'EditMovedBanner').props.onKeepMine(); await settle();
    saveButton(h.result).props.onSave(); await settle(); await settle();
    assert.equal(calls.find((c) => c[0] === 'setTableSourceMapping')?.[4], 'My_Table');
    assert.deepEqual(closed, ['saved', 'closed']);
  });

  test('I2: a Save whose drawer missed the change re-reads and refuses', async () => {
    calls.length = 0; rows.current = [row('Sales')];
    const { h } = mount(); await settle(); await settle();
    entityBox(h.result).props.onChange({ target: { value: 'My_Table' } }); await settle();
    rows.current = [row('Sales_S06b_MCP')];   // no activity event reached the drawer
    saveButton(h.result).props.onSave(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setTableSourceMapping'));
    assert.ok(hasBanner(h.result));
  });
}

// ---- 4 one surface per family ---------------------------------------------------------------------------
// Check drawers (Validation): the saved check is read when the drawer opens, and a check that moved is not overwritten.
{
  const calls = [];
  let live = { id: 'c1', kind: 'measureValue', title: 'Total ties', targetRef: 'measure:Sales/Total', enabled: true, paramsJson: '{"expected":"10"}' };
  let activity = [];
  const stubs = {
    './bridge': {
      rpc: async (method) => { calls.push(method); if (method === 'listTests') return { definitions: [live] }; return []; },
      onActivity: (fn) => { activity.push(fn); return () => { activity = activity.filter((f) => f !== fn); }; },
    },
    './connection': { useConnection: () => ({}) }, './objectbrowser': { ObjectBrowser: () => null },
    './surface-escape': { useSurfaceEscape: () => undefined }, './askdata-model': { checkTitle: () => '' }, './daxeditor': { useDaxModelContext: () => ({ columns: [] }) },
  };
  const drawer = (editing) => {
    const { rt, load } = fresh(stubs);
    const { AuthorDrawer } = load('tests-authoring.tsx');
    return rt.renderHook(() => AuthorDrawer({ shape: 'measureValue', editing, onClose: () => undefined, onSaved: () => undefined }));
  };
  const form = (tree) => find(tree, (el) => ['TrustedAnswerForm', 'CompareWithSourceForm'].includes(el.type?.name));

  test('check drawer: a saved check opens on the live definition, not the row the page drew earlier', async () => {
    const stale = { ...live, paramsJson: '{"expected":"9"}' };
    const h = drawer(stale);
    await settle(); await settle();
    assert.ok(calls.includes('listTests'), 'the drawer reads the check when it opens');
    assert.equal(form(h.result)?.props.editing.paramsJson, '{"expected":"10"}', 'the form is seeded from the live check (the assistant saved it with 10)');
  });

  test('check drawer: a check saved by the assistant while the drawer is open is not overwritten without asking', async () => {
    const h = drawer({ ...live });
    await settle(); await settle();
    // The drawer holds the form's fields (join 11): a typed field is what makes a move worth asking about. A clean drawer follows
    // (drafts-tests-authoring.test.mjs A5); it no longer asks on every move with a placeholder that always reads as typed.
    form(h.result).props.onFields({ expected: '42' }); await settle();
    live = { ...live, paramsJson: '{"expected":"11"}' };
    [...activity].forEach((fn) => fn({ kind: 'save_test' })); await settle(); await settle();
    assert.ok(hasBanner(h.result), 'the person is told');
    assert.equal(form(h.result).props.fields.expected, '42', 'the typed number stays');
    assert.equal(await form(h.result).props.before.before(), false, 'the form\'s Save is held');
    find(h.result, (el) => el.type?.name === 'EditMovedBanner').props.onUseNew(); await settle(); await settle();
    assert.equal(form(h.result).props.editing.paramsJson, '{"expected":"11"}', 'Use the new one reloads the form from the live check');
  });

  // The shell journey (tools/uishot/shell-journey.mjs) went red at 0c153820: `opened` was captured when the drawer
  // mounted, so a second hand-off for another measure reached `editing` and never reached the open form.
  test('check drawer: a second hand-off for another calculation retargets the open new check', async () => {
    let target = { id: '', kind: 'measureValue', title: '', targetRef: 'measure:Sales/Total Sales', enabled: true };
    const { rt, load } = fresh(stubs);
    const { AuthorDrawer } = load('tests-authoring.tsx');
    const h = rt.renderHook(() => AuthorDrawer({ shape: 'measureValue', editing: target, onClose: () => undefined, onSaved: () => undefined }));
    await settle(); await settle();
    assert.equal(form(h.result).props.fields.measureRef, 'measure:Sales/Total Sales');
    target = { ...target, targetRef: 'measure:Sales/Total Cost' }; h.rerender(); await settle(); await settle();
    // Since the drafts redesign the form's fields live in the drawer (`fields`), and `editing` is only where the draft started.
    assert.equal(form(h.result).props.fields.measureRef, 'measure:Sales/Total Cost', 'the open form is handed the new calculation');
  });
}

// Model-detail editors: a calculation item's DAX and a table's row filter (advmodels), a documentation section.
{
  const calls = [];
  const stubs = {
    './bridge': { rpc: async (method, ...args) => { calls.push([method, ...args]); return undefined; }, onDidChange: () => () => undefined },
    './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
    './daxeditor': { DaxField: () => null },
    './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) },
    './ui': { Caret: () => null },
    '@tanstack/react-virtual': { useVirtualizer: () => ({}) },
    './formatMode': { inferFormatMode: () => 'static' },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection: () => undefined },
  };
  test('calculation item: DAX typed while the assistant rewrote the item is kept and not saved over it', async () => {
    const { rt, load } = fresh(stubs);
    const { CalcItemRow } = load('advmodels.tsx');
    const state = { item: { ref: 'calcitem:Time/YTD', name: 'YTD', ordinal: 1, expression: 'SELECTEDMEASURE()' } };
    calls.length = 0;
    const h = rt.renderHook(() => CalcItemRow({ item: state.item, reload: () => undefined, onError: () => undefined }));
    await settle();
    const dax = () => find(h.result, (el) => el.type?.name === 'DaxField');
    dax().props.onChange('CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))'); dax().props.onValidity?.(true); await settle();
    state.item = { ...state.item, expression: 'SELECTEDMEASURE() * 2' }; h.rerender(); await settle();
    assert.equal(dax().props.value, 'CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))', 'the typed DAX stays');
    assert.ok(hasBanner(h.result), 'the person is told');
    button(h.result, 'Save DAX').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setDax'), 'nothing is written over the assistant\'s change');
    find(h.result, (el) => el.type?.name === 'EditMovedBanner').props.onKeepMine(); await settle();
    button(h.result, 'Save DAX').props.onClick(); await settle(); await settle();
    assert.equal(calls.find((c) => c[0] === 'setDax')?.[2], 'CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))');
  });

  test('documentation section: a section the assistant rewrote is followed when clean, and a typed one is not saved over it', async () => {
    const { rt, load } = fresh(stubs);
    const { SectionEditor } = load('documentation.tsx');
    const state = { value: 'Old narrative' };
    calls.length = 0;
    const h = rt.renderHook(() => SectionEditor({ objRef: 'model', objName: 'Model', sectionKey: 'overview', value: state.value, onSaved: () => undefined }));
    await settle();
    const box = () => find(h.result, (el) => el.type === 'textarea');
    state.value = 'Assistant narrative'; h.rerender(); await settle();
    assert.equal(box().props.value, 'Assistant narrative', 'a clean section follows');
    box().props.onChange({ target: { value: 'My narrative' } }); await settle();
    state.value = 'Assistant narrative 2'; h.rerender(); await settle();
    assert.ok(hasBanner(h.result));
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setDocSection'), 'nothing is written over the assistant\'s section');
  });
}

// Notes family, second member: the AI instructions editor (instructions.tsx) is the same editor over the model's instructions.
{
  const calls = [];
  const stubs = {
    './bridge': { rpc: async (method, ...args) => { calls.push([method, ...args]); return method === 'getAiInstructions' ? state.value : undefined; } },
    './hooks': { messageOf: (e) => String(e), useAutoGrow: () => ({ current: null }), useRemote: () => [null, () => undefined] },
    './knowledge': { KnowledgeView: () => null },
  };
  const state = { value: { present: true, instructions: 'Use net sales.', limit: 2000, length: 14 } };
  test('AI instructions: instructions the assistant rewrote while the editor was open are not saved over', async () => {
    calls.length = 0;
    const { rt, load } = fresh(stubs);
    const { AiInstructionsCard } = load('instructions.tsx');
    const props = () => ({ state: { status: 'ready', value: state.value }, onReload: () => calls.push(['reload']), onNotes: () => undefined,
      suggestions: { status: 'ready', value: { suggestions: [] } }, onSuggestionsChanged: () => undefined });
    const h = rt.renderHook(() => AiInstructionsCard(props()));
    await settle();
    button(h.result, 'Edit').props.onClick(); await settle();
    const box = () => find(h.result, (el) => el.type === 'textarea');
    assert.equal(box().props.value, 'Use net sales.', 'the editor opens on the live instructions');
    box().props.onChange({ target: { value: 'Use net sales. Always show the currency.' } }); await settle();
    state.value = { ...state.value, instructions: 'Use gross sales.' }; h.rerender(); await settle();
    assert.ok(hasBanner(h.result), 'the person is told the instructions changed');
    assert.equal(box().props.value, 'Use net sales. Always show the currency.', 'typed words stay');
    button(h.result, 'Save').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setAiInstructions'), 'nothing is written over the assistant\'s instructions');
    find(h.result, (el) => el.type?.name === 'EditMovedBanner').props.onUseNew(); await settle();
    assert.equal(box().props.value, 'Use gross sales.', 'Use the new one loads the live instructions');
  });
}

// Properties format-expression editor (media/propgrid/propgrid.js is plain script: read, not run, here).
test('Properties format-expression editor keeps typed words and says so when the stored expression changed under them', () => {
  const js = readFileSync(resolve(root, 'media', 'propgrid', 'propgrid.js'), 'utf8');
  assert.match(js, /fx\.base = fxProp && !fxProp\.varies/, 'the editor remembers the stored expression it started from');
  assert.match(js, /fx\.moved = !!\(fx\.open && fx\.draft != null/, 'a load that moved the stored expression under typed words is noticed');
  assert.match(js, /Your words are still here\. Apply writes them over the new one\./, 'and said in plain words');
  assert.match(js, /data-fx-usenew/, 'with a way to take the new expression instead');
});

// ---- 5 every whole-object save goes through the guard ----------------------------------------------------
// The surveyed editors: each one's save rpc must be preceded, in the same function, by the guard's beforeSave().
// A new editor that saves a whole object belongs on this list (or says why not in the report).
test('every editor that saves a whole object from a copy taken at open asks the guard first', () => {
  const SURFACES = [
    ['knowledge.tsx', "rpc('editInsight'", 'insight editor'],
    ['knowledge.tsx', "rpc<PrimerDocument>('setPrimer'", 'model notes editor'],
    ['instructions.tsx', "rpc('setAiInstructions'", 'AI instructions editor'],
    ['tests-authoring.tsx', "rpc('setTableSourceMapping'", 'source mapping drawer'],
    ['tests-authoring.tsx', "rpc('saveTest', def, 'human'", 'check drawers (trusted answer, compare with SQL)'],
    ['advmodels.tsx', "rpc('setDax', item.ref", 'calculation item DAX'],
    ['advmodels.tsx', "rpc('setTablePermission', role.name", 'row filter'],
    ['documentation.tsx', "rpc('setDocSection', objRef, sectionKey, draft,", 'documentation section'],
    ['spec.tsx', "rpc<SpecSnapshot>('setSpec', JSON.stringify(parsed)", 'spec JSON editor'],
    ['connectionshub.tsx', "rpc<SqlSourceRecord>('saveSqlSource'", 'SQL source form'],
  ];
  const missing = [];
  for (const [file, call, label] of SURFACES) {
    const text = src(file);
    const at = text.indexOf(call);
    if (at < 0) { missing.push(`${label}: ${file} no longer contains ${call}`); continue; }
    const before = text.slice(Math.max(0, at - 700), at);
    if (!/beforeSave\(\)|before\(\)|before &&/.test(before)) missing.push(`${label}: ${file} saves with ${call} without asking the guard first`);
  }
  assert.deepEqual(missing, [], missing.join('\n'));
  // Whole-set replacements and whole re-saves are built from the live data, never from the page's older list.
  const rules = src('rulesauthor.tsx');
  assert.match(rules, /\(await liveRules\(\)\)\.filter\(\(r\) => String\(r\.id\) !== form\.editingId\)/, 'renaming a rule replaces the live set');
  assert.match(rules, /\(await liveRules\(\)\)\.filter\(\(r\) => String\(r\.id\) !== id\)/, 'removing a rule works from the live set');
  assert.doesNotMatch(rules, /const remaining = rules\.filter/, 'no whole-set replacement is built from the page\'s older copy');
  // toggleEnabled changes one flag: it flips it on the live check, never writes the row's older copy.
  assert.match(src('tests.tsx'), /rpc<TestSuiteInfoW>\('listTests'\)[\s\S]{0,260}saveTest', \{ \.\.\.live, enabled: !def\.enabled \}/,
    'the enable switch flips the flag on the check as it stands now');
});

test('the shared words follow the product copy rules: plain, no em dash, no engine words', () => {
  const t = src('editbase.tsx');
  assert.doesNotMatch(t.slice(t.indexOf('export function EditMovedBanner')), /—/);
  assert.match(t, /Keep mine/);
  assert.match(t, /Use the new one/);
});
