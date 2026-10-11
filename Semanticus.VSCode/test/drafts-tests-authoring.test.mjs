// Typed, unsaved check fields outlive the check drawer (join 11, the drafts class; Kane, 4 Oct).
//
// The Validation (Tests) page has two editors that save from a copy the person typed:
//   A/B  the check drawer (AuthorDrawer), on a SAVED check: a trusted answer (TrustedAnswerForm) and a check against SQL
//        (CompareWithSourceForm). Its fields are one plain value (the draft) held by the model file + `tests-authoring|<check id>`.
//   C    the same drawer on a NEW check: held by `tests-authoring|new|<kind>`, offered when New check of that kind opens again.
//   D    the table mapping form (TableRowCountForm, the drawer's second guard): held by `tests-authoring|mapping|<table>`.
// The Tests tab stays mounted on a tab switch, but the drawer closes when the list or the route changes, and the whole page remounts
// on a restarted engine. So for each editor: (1) type, close, open again: the words are back and "Draft not saved" shows; (2) the
// same over a reload (a fresh module set over the same persisted state); (3) the saved value moved meanwhile: the words stay, Keep
// mine / Use the new one is asked, nothing is written until chosen, and the Save after Keep mine names the value it read; (4) a save
// the engine took, Discard and Use the new one end the draft; (5) another model or another object shows none of it.
//
// The REAL components run here (hooks driven by the tiny hook runtime, the bridge faked). The drawer renders its form as an element;
// a second runtime renders that element's real form over the drawer's props, so the test types into the real inputs and presses
// the real Save. Where it matters, the real React renders the drawer to markup too (the first render a person sees).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('');
const button = (tree, label) => find(tree, (el) => el.type === 'button' && text(el) === label);
const mark = (tree) => !!find(tree, (el) => el.type?.name === 'DraftMark' && el.props.show);
const movedBanner = (tree) => find(tree, (el) => el.type?.name === 'EditMovedBanner');
const heldBanner = (tree) => find(tree, (el) => el.type?.name === 'HeldDraftBanner');
/** The input or textarea under the Field with this label. */
const inField = (tree, label) => find(find(tree, (el) => el.type?.name === 'Field' && el.props.label === label), (el) => el.type === 'input' || el.type === 'textarea');
const type = (el, value) => el.props.onChange({ target: { value } });
const flush = async () => { for (let i = 0; i < 4; i++) await settle(); };

const MODEL = '/models/Sales.bim';
const TOTAL = 'measure:Sales/Total';
const fence = (d) => JSON.stringify({ title: d.title ?? null, kind: d.kind ?? null, targetRef: d.targetRef ?? null, paramsJson: d.paramsJson ?? null, enabled: d.enabled ?? true, budgetMs: d.budgetMs ?? null });
const trusted = (expectedValue, extra = {}) => ({ id: 'c1', kind: 'measureValue', title: 'Total is ' + expectedValue, targetRef: TOTAL, enabled: true,
  paramsJson: JSON.stringify({ measureRef: TOTAL, expectedValue }), ...extra });
const againstSql = (sql) => ({ id: 'c2', kind: 'measureReconcile', title: 'Total ties to source SQL', targetRef: TOTAL, enabled: true,
  paramsJson: JSON.stringify({ measureRef: TOTAL, sql, blankPolicy: 'zero', sqlSourceId: 's1', sqlSourceName: 'Warehouse', groupBy: [] }) });
const mapping = (entity, table = 'Sales') => ({ modelTable: table, source: 'mapping', sqlSourceId: 's1', sqlSourceName: 'Warehouse', schema: 'dbo', entity, canCount: true, editable: true });

/** The engine behind the bridge: the saved checks, the table mappings, what was called, and who listens for activity. */
function makeEngine() {
  const e = { defs: [trusted('10'), againstSql('SELECT SUM(x) FROM s')], rows: [mapping('orders'), mapping('items', 'Orders')], calls: [], activity: [], refuse: false };
  e.bridge = {
    rpc: async (method, ...args) => {
      e.calls.push([method, ...args]);
      if (method === 'listTests') return { definitions: e.defs.map((d) => ({ ...d })) };
      if (method === 'listMeasures') return [{ ref: TOTAL, name: 'Total', table: 'Sales' }];
      if (method === 'listSqlSources') return [{ id: 's1', name: 'Warehouse' }];
      if (method === 'listTableMappings') return { rows: e.rows.map((r) => ({ ...r })) };
      if (method === 'saveTest') {
        if (e.hold) await e.hold.promise;   // drafts-sol F5: the engine is busy, the save waits
        if (e.refuse) throw new Error('The disk is full.');
        const [def] = args;
        e.defs = def.id ? e.defs.map((d) => (d.id === def.id ? { ...def } : d)) : [...e.defs, { ...def, id: 'new1' }];
        return def;
      }
      if (method === 'setTableSourceMapping') {
        if (e.hold) await e.hold.promise;
        const [table, sqlSourceId, schema, entity] = args;
        e.rows = e.rows.map((r) => (r.modelTable === table ? { ...r, sqlSourceId, schema, entity } : r));
        return true;
      }
      return undefined;
    },
    onActivity: (fn) => { e.activity.push(fn); return () => { e.activity = e.activity.filter((f) => f !== fn); }; },
    onDidChange: () => () => undefined,
  };
  return e;
}
const OTHER_STUBS = {
  './connection': { useConnection: () => ({}) }, './objectbrowser': { ObjectBrowser: () => null },
  './surface-escape': { useSurfaceEscape: () => undefined }, './daxeditor': { useDaxModelContext: () => ({ columns: [] }) },
};

/** One webview: its persisted state and the draft store read from it. `reload` is a fresh page over the same state (a restart or a reload). */
function makeWorld(engine = makeEngine(), model = MODEL) {
  const persisted = {};
  const state = {
    loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f),
    saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); },
  };
  let drafts;
  const boot = () => { drafts = makeLoader({ './bridge': state })('drafts'); drafts.setDraftModel(model); };
  boot();
  const stubs = () => ({ ...OTHER_STUBS, './bridge': { ...engine.bridge, ...state }, './drafts': drafts });
  return {
    engine, persisted,
    get drafts() { return drafts; },
    reload() { boot(); },
    setModel(m) { drafts.setDraftModel(m); },
    held: () => Object.keys(drafts.draftStore.snapshot()).length,
    /** The check drawer on the page. `form()` is the form element the drawer drew; `view()` renders that element's real form. */
    openDrawer(props) {
      const rt = makeHookRuntime();
      const { AuthorDrawer } = makeLoader(stubs(), rt.React)('tests-authoring.tsx');
      const h = rt.renderHook(() => AuthorDrawer({ onClose: () => engine.calls.push(['closed']), onSaved: () => engine.calls.push(['saved']), ...props }));
      const formEl = () => find(h.result, (el) => ['TrustedAnswerForm', 'CompareWithSourceForm'].includes(el.type?.name));
      const rt2 = makeHookRuntime();
      const forms = makeLoader(stubs(), rt2.React)('tests-authoring.tsx');
      let fv = null;
      const view = () => {
        if (!formEl()) return null;
        if (!fv) fv = rt2.renderHook(() => { const el = formEl(); return el ? forms[el.type.name](el.props) : null; });
        else fv.rerender();
        return fv.result;
      };
      return { tree: () => h.result, form: formEl, view, unmount: () => { rt.unmount(); if (fv) rt2.unmount(); } };
    },
    /** The table mapping form, as Map source on a table row opens it (or New check > Table row count, with no table). */
    openMapping(table) {
      const rt = makeHookRuntime();
      const { TableRowCountForm } = makeLoader(stubs(), rt.React)('tests-authoring.tsx');
      const h = rt.renderHook(() => TableRowCountForm({
        sources: [{ id: 's1', name: 'Warehouse' }], adopt: null, table, busy: false, setBusy: () => undefined, setErr: (m) => { if (m) engine.calls.push(['err', m]); },
        onSaved: () => engine.calls.push(['saved']), onClose: () => engine.calls.push(['closed']), onAddSqlSource: () => undefined,
      }));
      return { tree: () => h.result, unmount: () => rt.unmount() };
    },
    /** The drawer as the real React draws it first (no effects run): what a person sees the moment it opens. */
    markup(props) {
      const React = req('react');
      const { renderToStaticMarkup } = req('react-dom/server');
      const { AuthorDrawer } = makeLoader(stubs())('tests-authoring.tsx');
      return renderToStaticMarkup(React.createElement(AuthorDrawer, { onClose: () => undefined, onSaved: () => undefined, ...props }));
    },
  };
}
const saveOf = (w) => w.engine.calls.filter((c) => c[0] === 'saveTest');
const mappingSaves = (w) => w.engine.calls.filter((c) => c[0] === 'setTableSourceMapping');
const footerSave = (view) => find(view, (el) => el.type?.name === 'Footer').props.onSave();
const assistantSaves = async (w, def) => { w.engine.defs = w.engine.defs.map((d) => (d.id === def.id ? def : d)); [...w.engine.activity].forEach((fn) => fn({ kind: 'save_test' })); await flush(); };

// ---- A the check drawer on a saved trusted answer -----------------------------------------------------------------------
{
  const open = (w) => w.openDrawer({ shape: 'measureValue', editing: trusted('10') });
  const answer = (d) => inField(d.view(), 'The number you trust');

  test('A1 saved trusted answer: typed, closed and opened again, the number is back and "Draft not saved" shows', async () => {
    const w = makeWorld();
    const a = open(w); await flush();
    assert.equal(answer(a).props.value, '10', 'the drawer opens on the saved check');
    assert.equal(mark(a.tree()), false, 'nothing typed: no mark');
    type(answer(a), '42'); await flush();
    assert.equal(mark(a.tree()), true, 'typed and not saved: the mark shows');
    a.unmount();
    assert.equal(w.held(), 1, 'closing the drawer does not end the draft');
    const b = open(w); await flush();
    assert.equal(answer(b).props.value, '42', 'the typed number is back');
    assert.equal(mark(b.tree()), true);
    assert.equal(movedBanner(b.tree()), null, 'nothing moved: no banner');
    const html = w.markup({ shape: 'measureValue', editing: trusted('10') });
    assert.match(html, /value="42"/, 'the very first render shows the typed number, not the saved one');
    assert.match(html, /Draft not saved/);
  });

  test('A2 saved trusted answer: a reload or a restarted engine keeps the typed number', async () => {
    const w = makeWorld();
    const a = open(w); await flush();
    type(answer(a), '42'); await flush();
    a.unmount();
    w.reload();
    const b = open(w); await flush();
    assert.equal(answer(b).props.value, '42');
    assert.equal(mark(b.tree()), true);
  });

  test('A3 saved trusted answer: the assistant saved the check meanwhile; the words stay, the choice is asked, and Save names what it read', async () => {
    const w = makeWorld();
    const a = open(w); await flush();
    type(answer(a), '42'); await flush();
    a.unmount();
    const theirs = trusted('11', { title: 'Total is 11 (assistant)' });
    w.engine.defs = w.engine.defs.map((d) => (d.id === 'c1' ? theirs : d));
    w.reload();
    const b = open(w); await flush();
    assert.equal(answer(b).props.value, '42', 'the typed number is kept');
    assert.ok(movedBanner(b.tree()), 'Keep mine / Use the new one is shown');
    footerSave(b.view()); await flush();
    assert.equal(saveOf(w).length, 0, 'nothing is written until the person chose');
    movedBanner(b.tree()).props.onKeepMine(); await flush();
    footerSave(b.view()); await flush();
    const [, def, , expected] = saveOf(w)[0];
    assert.equal(JSON.parse(def.paramsJson).expectedValue, '42', 'Keep mine writes the typed number');
    assert.equal(expected, fence(theirs), 'and the write names the check as the Save read it (the engine fences on it)');
    assert.equal(w.held(), 0, 'a save the engine took ends the draft');
  });

  test('A4 saved trusted answer: Use the new one and Discard end the draft; a save the engine refused keeps it', async () => {
    const w = makeWorld();
    let d = open(w); await flush();
    type(answer(d), '42'); await flush();
    await assistantSaves(w, trusted('11'));
    assert.ok(movedBanner(d.tree()));
    movedBanner(d.tree()).props.onUseNew(); await flush();
    assert.equal(answer(d).props.value, '11', 'Use the new one loads the assistant\'s check');
    assert.equal(w.held(), 0, 'and ends the draft');
    type(answer(d), '43'); await flush();
    assert.equal(w.held(), 1);
    button(d.tree(), 'Discard draft').props.onClick(); await flush();
    assert.equal(answer(d).props.value, '11', 'Discard puts the saved check back');
    assert.equal(w.held(), 0, 'and ends the draft');
    assert.equal(mark(d.tree()), false);
    type(answer(d), '44'); await flush();
    w.engine.refuse = true;
    footerSave(d.view()); await flush();
    assert.equal(w.held(), 1, 'a save the engine refused keeps the typed number');
    d.unmount();
    d = open(w); await flush();
    assert.equal(answer(d).props.value, '44');
  });

  test('A5 saved trusted answer: a clean drawer follows the assistant\'s save (the real comparison, not a dummy that always asks)', async () => {
    const w = makeWorld();
    const a = open(w); await flush();
    await assistantSaves(w, trusted('11'));
    assert.equal(movedBanner(a.tree()), null, 'nothing typed: nothing to ask');
    assert.equal(answer(a).props.value, '11', 'the drawer follows the check');
    assert.equal(w.held(), 0);
  });

  test('A6 saved trusted answer: a draft is not shown on another model or on another check', async () => {
    const w = makeWorld();
    w.engine.defs.push(trusted('7', { id: 'c3' }));
    const a = open(w); await flush();
    type(answer(a), '42'); await flush();
    a.unmount();
    const other = w.openDrawer({ shape: 'measureValue', editing: trusted('7', { id: 'c3' }) }); await flush();
    assert.equal(answer(other).props.value, '7', 'another check has its own fields');
    assert.equal(mark(other.tree()), false);
    other.unmount();
    w.setModel('/models/Other.bim');
    const b = open(w); await flush();
    assert.equal(answer(b).props.value, '10', 'another model never sees the draft');
    assert.equal(mark(b.tree()), false);
  });
}

// ---- B the check drawer on a saved check against SQL --------------------------------------------------------------------
{
  const open = (w) => w.openDrawer({ shape: 'measureReconcile', editing: againstSql('SELECT SUM(x) FROM s') });
  const sqlBox = (d) => inField(d.view(), 'The SQL you accept as the truth');

  test('B1 saved check against SQL: typed SQL survives closing the drawer and a reload, with the mark', async () => {
    const w = makeWorld();
    const a = open(w); await flush();
    assert.equal(sqlBox(a).props.value, 'SELECT SUM(x) FROM s');
    type(sqlBox(a), 'SELECT SUM(amount) FROM sales'); await flush();
    a.unmount();
    const b = open(w); await flush();
    assert.equal(sqlBox(b).props.value, 'SELECT SUM(amount) FROM sales', 'back after closing');
    assert.equal(mark(b.tree()), true);
    b.unmount();
    w.reload();
    const c = open(w); await flush();
    assert.equal(sqlBox(c).props.value, 'SELECT SUM(amount) FROM sales', 'back after a reload');
    assert.match(w.markup({ shape: 'measureReconcile', editing: againstSql('SELECT SUM(x) FROM s') }), /SELECT SUM\(amount\) FROM sales/);
  });

  test('B2 saved check against SQL: the assistant changed it meanwhile; asked, Keep mine writes over what the Save read, and the draft ends', async () => {
    const w = makeWorld();
    const a = open(w); await flush();
    type(sqlBox(a), 'SELECT SUM(amount) FROM sales'); await flush();
    a.unmount();
    const theirs = againstSql('SELECT SUM(net) FROM sales');
    w.engine.defs = w.engine.defs.map((d) => (d.id === 'c2' ? theirs : d));
    const b = open(w); await flush();
    assert.equal(sqlBox(b).props.value, 'SELECT SUM(amount) FROM sales');
    assert.ok(movedBanner(b.tree()));
    footerSave(b.view()); await flush();
    assert.equal(saveOf(w).length, 0, 'nothing written before the choice');
    movedBanner(b.tree()).props.onKeepMine(); await flush();
    footerSave(b.view()); await flush();
    const [, def, , expected] = saveOf(w)[0];
    assert.equal(JSON.parse(def.paramsJson).sql, 'SELECT SUM(amount) FROM sales');
    assert.equal(expected, fence(theirs));
    assert.equal(w.held(), 0);
  });
}

// ---- C the check drawer on a NEW check ----------------------------------------------------------------------------------
{
  const openNew = (w, shape = 'measureValue', editing = null) => w.openDrawer({ shape, editing });
  const answer = (d) => inField(d.view(), 'The number you trust');

  test('C1 new check: what was typed is offered when New check of that kind opens again, and Discard clears it', async () => {
    const w = makeWorld();
    const a = openNew(w); await flush();
    type(answer(a), '5'); await flush();
    assert.equal(mark(a.tree()), true);
    a.unmount();
    assert.ok(w.drafts.draftStore.get(MODEL, 'tests-authoring|new|measureValue', ''), 'held under the new-check scope of its kind');
    const b = openNew(w); await flush();
    assert.equal(answer(b).props.value, '5', 'the draft of the new check is offered');
    assert.equal(mark(b.tree()), true);
    assert.equal(heldBanner(b.tree()), null, 'opened the same way: no question');
    button(b.tree(), 'Discard draft').props.onClick(); await flush();
    assert.equal(answer(b).props.value, '', 'Discard starts the new check again');
    assert.equal(w.held(), 0);
    b.unmount();
    const c = openNew(w); await flush();
    assert.equal(answer(c).props.value, '');
  });

  test('C2 new check: a reload keeps it; another kind and another model do not show it', async () => {
    const w = makeWorld();
    const a = openNew(w); await flush();
    type(answer(a), '5'); await flush();
    a.unmount();
    w.reload();
    const b = openNew(w); await flush();
    assert.equal(answer(b).props.value, '5');
    b.unmount();
    const sql = openNew(w, 'measureReconcile'); await flush();
    assert.equal(mark(sql.tree()), false, 'a new check of another kind has no draft');
    sql.unmount();
    w.setModel('/models/Other.bim');
    const c = openNew(w); await flush();
    assert.equal(answer(c).props.value, '');
    assert.equal(mark(c.tree()), false);
  });

  test('C3 new check: switching kind inside the drawer keeps each kind\'s typed fields', async () => {
    const w = makeWorld();
    const d = openNew(w); await flush();
    type(answer(d), '5'); await flush();
    find(d.tree(), (el) => el.type?.name === 'KindChooser').props.onPick('measureReconcile'); await flush();
    assert.equal(mark(d.tree()), false, 'the other kind starts clean');
    find(d.tree(), (el) => el.type?.name === 'KindChooser').props.onPick('measureValue'); await flush();
    assert.equal(find(d.tree(), (el) => el.type?.name === 'TrustedAnswerForm').props.fields.expected, '5', 'back on the first kind, its number is still there');
    assert.equal(mark(d.tree()), true);
  });

  test('C4 new check: a save the engine took ends the draft', async () => {
    const w = makeWorld();
    const d = openNew(w); await flush();
    find(d.view(), (el) => el.type?.name === 'MeasureSelect').props.onChange(TOTAL); await flush();
    type(answer(d), '5'); await flush();
    assert.equal(w.held(), 1);
    footerSave(d.view()); await flush();
    assert.equal(saveOf(w).length, 1);
    assert.equal(w.held(), 0);
  });

  test('C5 new check: a hand-off into a kind that holds a draft asks before it replaces anything', async () => {
    const w = makeWorld();
    const a = openNew(w); await flush();
    type(answer(a), '5'); await flush();
    a.unmount();
    const handed = { id: '', kind: 'measureValue', title: '', targetRef: TOTAL, enabled: true };
    const b = openNew(w, 'measureValue', handed); await flush();
    assert.equal(answer(b).props.value, '5', 'the held number is not replaced by the hand-off');
    assert.ok(heldBanner(b.tree()), 'the person chooses');
    heldBanner(b.tree()).props.onUseNew(); await flush();
    assert.equal(find(b.tree(), (el) => el.type?.name === 'TrustedAnswerForm').props.fields.measureRef, TOTAL, 'Use the new one takes the hand-off');
    assert.equal(answer(b).props.value, '');
    assert.equal(w.held(), 0, 'and ends the old draft');
  });
}

// ---- D the table mapping form (the drawer's second guard) ---------------------------------------------------------------
{
  const entity = (m) => find(m.tree(), (el) => el.type === 'input' && el.props.placeholder === 'orders');
  const saveMapping = (m) => find(m.tree(), (el) => el.type?.name === 'Footer' && el.props.saveLabel === 'Save mapping').props.onSave();

  test('D1 mapping: a typed source table survives closing the drawer and a reload, with the mark', async () => {
    const w = makeWorld();
    const a = w.openMapping('Sales'); await flush();
    assert.equal(entity(a).props.value, 'orders');
    type(entity(a), 'orders_2026'); await flush();
    assert.equal(mark(a.tree()), true);
    a.unmount();
    const b = w.openMapping('Sales'); await flush();
    assert.equal(entity(b).props.value, 'orders_2026');
    assert.equal(mark(b.tree()), true);
    b.unmount();
    w.reload();
    const c = w.openMapping('Sales'); await flush();
    assert.equal(entity(c).props.value, 'orders_2026');
  });

  test('D2 mapping: New check > Table row count opens on the table that holds a draft', async () => {
    const w = makeWorld();
    const a = w.openMapping('Sales'); await flush();
    type(entity(a), 'orders_2026'); await flush();
    a.unmount();
    const b = w.openMapping(''); await flush();
    assert.equal(find(b.tree(), (el) => el.type === 'select').props.value, 'Sales', 'the table with the draft is picked');
    assert.equal(entity(b).props.value, 'orders_2026');
  });

  test('D3 mapping: the assistant changed it meanwhile; asked, nothing written until chosen, Keep mine names the mapping the Save read', async () => {
    const w = makeWorld();
    const a = w.openMapping('Sales'); await flush();
    type(entity(a), 'orders_2026'); await flush();
    a.unmount();
    w.engine.rows = w.engine.rows.map((r) => (r.modelTable === 'Sales' ? mapping('orders_mcp') : r));
    w.reload();
    const b = w.openMapping('Sales'); await flush();
    assert.equal(entity(b).props.value, 'orders_2026', 'the typed name is kept');
    assert.ok(movedBanner(b.tree()));
    saveMapping(b); await flush();
    assert.equal(mappingSaves(w).length, 0);
    movedBanner(b.tree()).props.onKeepMine(); await flush();
    saveMapping(b); await flush();
    const [, , , , ent, , expected] = mappingSaves(w)[0];
    assert.equal(ent, 'orders_2026');
    assert.equal(expected, JSON.stringify({ sqlSourceId: 's1', schema: 'dbo', entity: 'orders_mcp' }), 'the write names the assistant\'s mapping it read');
    assert.equal(w.held(), 0, 'a save the engine took ends the draft');
  });

  test('D4 mapping: Use the new one and Discard end the draft', async () => {
    const w = makeWorld();
    const m = w.openMapping('Sales'); await flush();
    type(entity(m), 'orders_2026'); await flush();
    w.engine.rows = w.engine.rows.map((r) => (r.modelTable === 'Sales' ? mapping('orders_mcp') : r));
    [...w.engine.activity].forEach((fn) => fn({ kind: 'set_table_source_mapping' })); await flush();
    movedBanner(m.tree()).props.onUseNew(); await flush();
    assert.equal(entity(m).props.value, 'orders_mcp');
    assert.equal(w.held(), 0);
    type(entity(m), 'orders_x'); await flush();
    assert.equal(w.held(), 1);
    button(m.tree(), 'Discard draft').props.onClick(); await flush();
    assert.equal(entity(m).props.value, 'orders_mcp');
    assert.equal(w.held(), 0);
  });

  test('D5 mapping: another table and another model do not show the draft', async () => {
    const w = makeWorld();
    const a = w.openMapping('Sales'); await flush();
    type(entity(a), 'orders_2026'); await flush();
    a.unmount();
    const other = w.openMapping('Orders'); await flush();
    assert.equal(entity(other).props.value, 'items');
    assert.equal(mark(other.tree()), false);
    other.unmount();
    w.setModel('/models/Other.bim');
    const b = w.openMapping('Sales'); await flush();
    assert.equal(entity(b).props.value, 'orders');
    assert.equal(mark(b.tree()), false);
  });
}

// ---- F5 (drafts-sol, R-B): a save the engine took ends only the words it carried ------------------------------------------------------
// Sol join 10: with the engine busy, the fields stayed editable; the success callback cleared whatever the draft held NOW and closed the drawer, so words typed after
// Save was pressed were lost (the engine had only the earlier ones). The draft ends only when it still holds exactly what was sent; otherwise it stays, over what was saved.
{
  const gate = () => { let resolve; const promise = new Promise((r) => { resolve = r; }); return { promise, resolve }; };
  const answer = (d) => inField(d.view(), 'The number you trust');
  const entityBox = (m) => inField(m.tree(), 'Table in the source');

  test('F5 trusted answer: words typed while Save is pending are kept, and the drawer stays open on them', async () => {
    const w = makeWorld();
    const a = w.openDrawer({ shape: 'measureValue', editing: trusted('10') }); await flush();
    type(answer(a), '11'); await flush();
    w.engine.hold = gate();
    const saving = footerSave(a.view()); await flush();
    type(answer(a), '12'); await flush();
    w.engine.hold.resolve(); w.engine.hold = null; await saving; await flush();
    assert.match(saveOf(w).at(-1)[1].paramsJson, /"expectedValue":"11"/, 'the engine took only 11');
    assert.equal(answer(a).props.value, '12', 'the box still shows what was typed after');
    assert.equal(w.held(), 1, 'and the draft holding 12 was not cleared by the earlier save');
    assert.ok(!w.engine.calls.some((c) => c[0] === 'closed'), 'the drawer did not close over words it never sent');
  });

  test('F5 trusted answer: a save that owns the text ends the draft and closes the drawer (the ordinary save is unchanged)', async () => {
    const w = makeWorld();
    const a = w.openDrawer({ shape: 'measureValue', editing: trusted('10') }); await flush();
    type(answer(a), '11'); await flush();
    await footerSave(a.view()); await flush();
    assert.equal(w.held(), 0); assert.ok(w.engine.calls.some((c) => c[0] === 'closed'));
  });

  test('F5 trusted answer: a refused save keeps the draft and the drawer', async () => {
    const w = makeWorld();
    const a = w.openDrawer({ shape: 'measureValue', editing: trusted('10') }); await flush();
    type(answer(a), '11'); await flush();
    w.engine.refuse = true;
    await footerSave(a.view()); await flush();
    assert.equal(w.held(), 1); assert.ok(!w.engine.calls.some((c) => c[0] === 'closed'));
  });

  test('F5 table mapping: words typed while Save is pending are kept, and the form stays open on them', async () => {
    const w = makeWorld();
    const m = w.openMapping('Sales'); await flush();
    type(entityBox(m), 'orders_11'); await flush();
    w.engine.hold = gate();
    const saving = find(m.tree(), (el) => el.type?.name === 'Footer').props.onSave(); await flush();
    type(entityBox(m), 'orders_12'); await flush();
    w.engine.hold.resolve(); w.engine.hold = null; await saving; await flush();
    assert.equal(mappingSaves(w).at(-1)[4], 'orders_11', 'the engine took only the earlier name');
    assert.equal(w.held(), 1, 'the later name is still held');
    assert.ok(!w.engine.calls.some((c) => c[0] === 'closed'));
  });
}
