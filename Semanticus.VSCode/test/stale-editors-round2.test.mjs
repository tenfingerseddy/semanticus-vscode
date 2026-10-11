// The stale-copy class, second round (m11/uat-ui, 2 Oct): the sweep of every webview save that writes a whole thing from a
// copy taken earlier, and the two lost-typed-work items found beside it.
//
// First round (stale-editors.test.mjs): the editors that hold a copy open. The verifier then found two more of the same
// class that the round left alone, and asked for one more pass over the whole webview:
//   1 the incremental refresh policy form (mcode.tsx)   2 the custom rule editor (rulesauthor.tsx)
//   3 the Spec tab's Save (spec.tsx): it showed a notice but still wrote the whole draft over the assistant's newer spec
//   4 team rules (workflows.tsx): the controls hand over the whole list of required workflows from the page's older copy
//   5 the chosen-reports list (lineage.tsx): the same shape, a whole list built from the page's older copy
// Lost typed work, same round:
//   6 Documentation: switching away from a section the assistant rewrote dropped the typed words after the banner
//   7 Studio's Ctrl+S saved the model while a Studio field that commits on blur still held the word in hand
// The REAL components run (hooks driven by a tiny hook runtime, the bridge faked), as in stale-editors.test.mjs.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, settle } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = (name) => readFileSync(resolve(root, 'webview', 'src', name), 'utf8');

/** The shared helper's hook runtime with one change: useMemo and useCallback keep their value while their dependencies are
 *  equal, as React does (the policy form lists its derived arrays as effect dependencies). */
function makeHookRuntime() {
  const slots = [];
  let cursor = 0, current = null, queue = [], rendering = false, again = false;
  const same = (a, b) => !!a && !!b && a.length === b.length && a.every((d, k) => Object.is(d, b[k]));
  const React = {
    useState(init) {
      const i = cursor++;
      if (!(i in slots)) slots[i] = { v: typeof init === 'function' ? init() : init };
      const s = slots[i];
      return [s.v, (n) => { const nv = typeof n === 'function' ? n(s.v) : n; if (Object.is(nv, s.v)) return; s.v = nv; render(); }];
    },
    useRef(init) { const i = cursor++; if (!(i in slots)) slots[i] = { current: init }; return slots[i]; },
    useCallback(f, deps) { return React.useMemo(() => f, deps); },
    memo(component) { return component; },
    useMemo(f, deps) {
      const i = cursor++;
      const s = slots[i];
      if (s && deps && same(deps, s.deps)) return s.v;
      slots[i] = { v: f(), deps };
      return slots[i].v;
    },
    useEffect(f, deps) {
      const i = cursor++;
      const s = (slots[i] ??= { deps: undefined, first: true });
      const changed = s.first || !deps || !s.deps || deps.some((d, k) => !Object.is(d, s.deps[k]));
      if (changed) queue.push(() => { s.first = false; s.deps = deps; if (typeof s.cleanup === 'function') s.cleanup(); s.cleanup = f(); });
    },
  };
  function render() {
    if (rendering) { again = true; return; }
    rendering = true;
    let guard = 0;
    try {
      do { if (++guard > 200) throw new Error('render loop'); again = false; cursor = 0; queue = []; current.result = current.fn(); for (const e of queue) e(); } while (again);
    } finally { rendering = false; }
  }
  return {
    React,
    renderHook(fn) { current = { fn, result: null, rerender: () => render() }; render(); return current; },
    /** Unmount: run every effect's cleanup, as React does when the component leaves the page. */
    unmount() { for (const s of slots) if (s && typeof s.cleanup === 'function') { s.cleanup(); s.cleanup = undefined; } },
  };
}

globalThis.window ??= globalThis;   // the components schedule with window.setTimeout
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const findAll = (tree, pred) => { const out = []; walk(tree, (el) => { if (pred(el)) out.push(el); }); return out; };
const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('').trim();
const fresh = (stubs) => { const rt = makeHookRuntime(); return { rt, load: makeLoader(stubs, rt.React) }; };
const banner = (tree) => find(tree, (el) => el.type?.name === 'EditMovedBanner');
const settleAll = async () => { for (let i = 0; i < 6; i++) await settle(); };

// ---- 1 the incremental refresh policy form ---------------------------------------------------------------------------
{
  const docModel = () => ({
    header: { compatibilityLevel: 1600 },
    tables: [{ ref: 'table:Sales', name: 'Sales', isCalculated: false, partitions: [{ name: 'Sales', mode: 'Import', sourceType: 'M', source: 'let S = Sql.Database("h","d"), T = Table.SelectRows(S, each [OrderDate] >= RangeStart and [OrderDate] < RangeEnd) in T' }] }],
    columns: [{ table: 'Sales', name: 'OrderDate', dataType: 'DateTime', sourceColumn: 'OrderDate', isCalculated: false }],
    expressions: [
      { name: 'RangeStart', kind: 'M', expression: '#datetime(2020,1,1,0,0,0) meta [IsParameterQuery=true]' },
      { name: 'RangeEnd', kind: 'M', expression: '#datetime(2021,1,1,0,0,0) meta [IsParameterQuery=true]' },
    ],
  });
  const policy = (incrementalPeriods, incrementalGranularity, rollingWindowPeriods = 5) => ({
    table: 'Sales', enabled: true, policyType: 'Basic', mode: 'Import', rollingWindowGranularity: 'Year', rollingWindowPeriods,
    incrementalGranularity, incrementalPeriods, incrementalPeriodsOffset: 0, sourceExpression: null, pollingExpression: null, wiredDateField: 'OrderDate',
  });
  async function openForm() {
    const calls = [], changed = [];
    const world = { engine: policy(10, 'Day') };
    const stubs = {
      './bridge': {
        rpc: async (method, ...args) => { calls.push([method, ...args]); if (method === 'getDocModel') return docModel(); if (method === 'getIncrementalRefreshPolicy') return world.engine; return {}; },
        onDidChange: (fn) => { changed.push(fn); return () => undefined; },
        loadState: (k, d) => (k.includes('table') ? 'Sales' : d), saveState: () => undefined,
      },
      './meditor': { MEditor: () => null }, './mlang': { formatM: async (s) => ({ ok: true, text: s }) }, './manalysis': { mDiagnostics: async () => [] },
      './connection': { useConnection: () => ({ conn: null }) }, './mtransform': { gen: {}, mQuoteIdent: (s) => s },
      './pqtransforms': { SamplePreview: () => null, AppliedStepsPanel: () => null, sourceMName: (c) => c.sourceColumn ?? c.name },
    };
    const { rt, load } = fresh(stubs);
    const { MCodeView } = load('mcode.tsx');
    const h = rt.renderHook(() => MCodeView({}));
    await settleAll(); await wait(50);
    find(h.result, (el) => el.type === 'button' && /^Configure/.test(text(el))).props.onClick(); await settle();
    const nums = () => findAll(h.result, (el) => el.type?.name === 'Num');
    const assistantSets = async (next) => { world.engine = next; changed.forEach((fn) => fn({ revision: 2, origin: 'agent' })); await wait(400); await settleAll(); };
    const saveBtn = () => find(h.result, (el) => el.type === 'button' && text(el) === 'Save policy');
    const write = () => calls.find((c) => c[0] === 'setIncrementalRefreshPolicy');
    return { h, nums, assistantSets, saveBtn, write, calls, world };
  }

  test('policy form: a clean form follows the refresh window the assistant set, and Save writes it', async () => {
    const f = await openForm();
    assert.equal(f.nums()[1].props.value, 10);
    await f.assistantSets(policy(3, 'Month'));
    assert.equal(f.nums()[1].props.value, 3, 'the form moved to the assistant\'s window');
    assert.ok(!banner(f.h.result), 'nothing to ask: no words were typed');
  });

  test('policy form: typed words stay, the person is asked, and Save writes nothing until they choose', async () => {
    const f = await openForm();
    f.nums()[0].props.onChange(7); await settle();
    await f.assistantSets(policy(3, 'Month'));
    assert.ok(banner(f.h.result), 'the banner is shown');
    assert.equal(f.nums()[0].props.value, 7, 'the typed store window stays');
    await f.saveBtn().props.onClick(); await settleAll();
    assert.equal(f.write(), undefined, 'Save policy wrote nothing over the assistant\'s window');
  });

  test('policy form: Use the new one loads the assistant\'s policy; Keep mine lets the next Save write the form', async () => {
    const f = await openForm();
    f.nums()[0].props.onChange(7); await settle();
    await f.assistantSets(policy(3, 'Month'));
    banner(f.h.result).props.onUseNew(); await settleAll();
    assert.deepEqual([f.nums()[0].props.value, f.nums()[1].props.value], [5, 3], 'the new policy is in the form');
    assert.ok(!banner(f.h.result));

    const g = await openForm();
    g.nums()[0].props.onChange(7); await settle();
    await g.assistantSets(policy(3, 'Month'));
    banner(g.h.result).props.onKeepMine(); await settleAll();
    await g.saveBtn().props.onClick(); await settleAll();
    const w = g.write();
    assert.ok(w, 'after Keep mine the Save writes');
    assert.deepEqual([w[3], w[4]], [7, 'Year'], 'the person\'s own store window');
  });

  test('policy form: a Save whose page missed the live event still re-reads and refuses', async () => {
    const f = await openForm();
    f.nums()[0].props.onChange(7); await settle();
    f.world.engine = policy(3, 'Month');   // the engine changes; no event reaches the page
    await f.saveBtn().props.onClick(); await settleAll();
    assert.equal(f.write(), undefined, 'nothing is written over the assistant\'s window');
    assert.ok(banner(f.h.result), 'the person is asked');
  });
}

// ---- 2 the custom rule editor ------------------------------------------------------------------------------------------
{
  const rule = (expression, extra = {}) => ({ id: 'ORG-DESC', name: 'Measures need descriptions', category: 'Descriptions', severity: 'Medium', scope: 'Measure', expression, message: 'needs a description', ...extra });
  async function openRule() {
    const calls = [], changed = [];
    const world = { live: [rule('string.IsNullOrEmpty(Description)')] };
    const stubs = {
      './bridge': {
        rpc: async (method, ...args) => {
          calls.push([method, ...args]);
          if (method === 'getCustomRules') return { bpa: [], readiness: world.live.map((r) => ({ ...r })), readinessCategories: ['Descriptions'], scopes: ['Measure'] };
          if (method === 'validateRule') return { kind: 'readiness', ruleCount: 1, allValid: true, rules: [{ valid: true, errors: [], applicable: 3, violations: 1, dormant: false, sample: [] }] };
          return {};
        },
        onDidChange: (fn) => { changed.push(fn); return () => undefined; },
      },
      './sendtask': { RuleCheckBox: () => null },
    };
    const { rt, load } = fresh(stubs);
    const { CustomRulesPanel } = load('rulesauthor.tsx');
    const h = rt.renderHook(() => CustomRulesPanel({ kind: 'readiness' }));
    await settleAll();
    find(h.result, (el) => el.type === 'button' && el.props.title === 'Expand').props.onClick(); await settle();
    find(h.result, (el) => el.type?.name === 'MiniBtn' && text(el) === 'Edit').props.onClick(); await settle();
    const input = (placeholder) => find(h.result, (el) => (el.type === 'input' || el.type === 'textarea') && el.props.placeholder === placeholder);
    const assistantSets = async (next) => { world.live = next; changed.forEach((fn) => fn({ revision: 2, origin: 'agent' })); await wait(650); await settleAll(); };
    const save = () => find(h.result, (el) => el.type?.name === 'Btn' && el.props.primary);
    const written = () => calls.filter((c) => c[0] === 'loadReadinessRules');
    return { h, input, assistantSets, save, written, world, calls };
  }

  test('rule editor: a clean form follows a rule the assistant rewrote', async () => {
    const r = await openRule();
    await r.assistantSets([rule('string.IsNullOrWhiteSpace(Description)')]);
    assert.equal(r.input('string.IsNullOrEmpty(Description)').props.value, 'string.IsNullOrWhiteSpace(Description)');
    assert.ok(!banner(r.h.result));
  });

  test('rule editor: typed words stay, the person is asked, Save writes nothing; Use the new one loads the assistant\'s rule', async () => {
    const r = await openRule();
    r.input('%object% needs a description').props.onChange({ target: { value: 'has no description yet' } }); await settle();
    await r.assistantSets([rule('string.IsNullOrWhiteSpace(Description) and not IsHidden')]);
    assert.ok(banner(r.h.result), 'the banner is shown');
    assert.equal(r.input('%object% needs a description').props.value, 'has no description yet', 'typed words stay');
    r.save().props.onClick(); await settleAll();
    assert.equal(r.written().length, 0, 'nothing is written over the assistant\'s rule');
    banner(r.h.result).props.onUseNew(); await settleAll();
    assert.equal(r.input('string.IsNullOrEmpty(Description)').props.value, 'string.IsNullOrWhiteSpace(Description) and not IsHidden');
    assert.equal(r.input('%object% needs a description').props.value, 'needs a description');
  });

  test('rule editor: Keep mine lets the next Save write the person\'s rule', async () => {
    const r = await openRule();
    r.input('%object% needs a description').props.onChange({ target: { value: 'has no description yet' } }); await settle();
    await r.assistantSets([rule('string.IsNullOrWhiteSpace(Description)')]);
    banner(r.h.result).props.onKeepMine(); await settle();
    r.save().props.onClick(); await settleAll();
    assert.equal(r.written().length, 1);
    assert.equal(JSON.parse(r.written()[0][1])[0].Message, 'has no description yet');
  });

  test('rule editor: a rule the assistant removed while the form was open is not written back without a word', async () => {
    const r = await openRule();
    r.input('%object% needs a description').props.onChange({ target: { value: 'has no description yet' } }); await settle();
    r.world.live = [];   // removed, and the page has not heard
    r.save().props.onClick(); await settleAll();
    assert.equal(r.written().length, 0, 'nothing is written');
    const alert = find(r.h.result, (el) => el.props?.role === 'alert' && /removed/.test(JSON.stringify(el.props.children)));
    assert.ok(alert, 'the person is told the rule is gone');
  });

  test('rule editor: a new rule whose id the assistant has just taken is refused, from the live list', async () => {
    const r = await openRule();
    // Cancel the edit and start a new rule, then the assistant adds the same id the person is about to use.
    find(r.h.result, (el) => el.type?.name === 'Btn' && text(el) === 'Cancel').props.onClick(); await settle();
    find(r.h.result, (el) => el.type?.name === 'Btn' && text(el) === 'New rule').props.onClick(); await settle();
    const tpl = find(r.h.result, (el) => el.type === 'button' && /Measures missing a description/.test(JSON.stringify(el.props.children)));
    tpl.props.onClick(); await settle();
    const id = find(r.h.result, (el) => el.type === 'input' && el.props.placeholder === 'ORG-MY-RULE');
    id.props.onChange({ target: { value: 'ORG-NEW' } }); await wait(650); await settleAll();
    r.world.live = [...r.world.live, rule('Name.Contains("_")', { id: 'ORG-NEW' })];   // taken by the assistant; the page has not heard
    r.save().props.onClick(); await settleAll();
    assert.equal(r.written().length, 0, 'the assistant\'s rule is not merged over');
  });
}

// ---- 3 the Spec tab's Save ----------------------------------------------------------------------------------------------
{
  const specOf = (version, tables) => ({ version, source: 'manual', spec: { name: 'M', tables, relationships: [], measures: [], timeIntelligence: [] } });
  const table = (name) => ({ name, role: 'fact', columns: [] });
  async function openSpec() {
    const calls = [], listeners = [];
    const world = { snap: specOf(1, [table('Sales')]) };
    const stubs = {
      './bridge': {
        rpc: async (method, ...args) => { calls.push([method, ...args]); if (method === 'getSpec') return world.snap; if (method === 'setSpec') return { ...world.snap, version: world.snap.version + 1 }; return {}; },
        onSpecChange: (fn) => { listeners.push(fn); return () => undefined; }, pickSpecFile: async () => null,
      },
      './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined },
      './connection': { useConnection: () => ({ openConnections: () => undefined }) },
      './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
    };
    const { rt, load } = fresh(stubs);
    const { SpecView } = load('spec.tsx');
    const h = rt.renderHook(() => SpecView({ session: { modelName: 'M' } }));
    await settleAll(); await wait(20); await settleAll();
    return { h, calls, world, hear: async () => { listeners.forEach((fn) => fn()); await wait(300); await settleAll(); } };
  }
  const findText = (tree, label) => find(tree, (el) => (el.type?.name === 'Btn' || el.type === 'button') && text(el) === label);

  test('spec: Save re-reads the spec, and a draft taken from an older version writes nothing until the person chose', async () => {
    const s = await openSpec();
    // The person edits through the one mutator every inline control uses (here: a table's role).
    const edit = () => find(s.h.result, (el) => el.type?.name === 'SpecEditor').props.edit((sp) => { sp.tables[0].role = 'dimension'; });
    edit(); await settle();
    // The assistant refines the same spec (set_spec over MCP).
    s.world.snap = specOf(2, [table('Sales'), table('Orders')]);
    await s.hear();
    const saveBtn = findText(s.h.result, 'Save');
    assert.ok(saveBtn, 'Save is on the page');
    await saveBtn.props.onClick(); await settleAll();
    assert.ok(!s.calls.some((c) => c[0] === 'setSpec'), 'nothing is written over the assistant\'s spec');
    assert.ok(findText(s.h.result, 'Keep mine'), 'the person is offered Keep mine');
    findText(s.h.result, 'Keep mine').props.onClick(); await settleAll();
    await findText(s.h.result, 'Save').props.onClick(); await settleAll();
    assert.ok(s.calls.some((c) => c[0] === 'setSpec'), 'after Keep mine the Save writes');
  });

  test('spec: a Save whose page missed the live event still reads the live version and refuses', async () => {
    const s = await openSpec();
    find(s.h.result, (el) => el.type?.name === 'SpecEditor').props.edit((sp) => { sp.tables[0].role = 'dimension'; }); await settle();
    s.world.snap = specOf(2, [table('Sales'), table('Orders')]);   // no event reaches the page
    await findText(s.h.result, 'Save').props.onClick(); await settleAll();
    assert.ok(!s.calls.some((c) => c[0] === 'setSpec'), 'nothing is written');
  });
}

// ---- 4 team rules and 5 the chosen reports: the person's change is applied to the live list -------------------------------
test('team rule: the person\'s change is applied to the rule as it stands now, never the page\'s older list', () => {
  const { bindingChange } = fresh({}).load('workflows-model.ts');
  // The page drew [a]; the assistant added b. The person adds c: the rule must carry a, b and c.
  assert.deepEqual(bindingChange(['a'], { require: ['a', 'b'], mode: 'hard' }, ['a', 'c'], 'hard'), { require: ['a', 'b', 'c'], mode: 'hard' });
  // The person stops requiring a; the assistant's b stays, in the assistant's mode.
  assert.deepEqual(bindingChange(['a'], { require: ['a', 'b'], mode: 'warn' }, [], 'off'), { require: ['b'], mode: 'warn' });
  // The assistant already dropped a: the person's Remove leaves nothing required, so the rule is off.
  assert.deepEqual(bindingChange(['a'], { require: [], mode: 'off' }, [], 'off'), { require: [], mode: 'off' });
  // No live rule at all: the person's addition is the whole rule.
  assert.deepEqual(bindingChange([], undefined, ['a'], 'hard'), { require: ['a'], mode: 'hard' });
  // Nothing moved under the page: the result is exactly what the control handed over.
  assert.deepEqual(bindingChange(['a'], { require: ['a'], mode: 'hard' }, ['a', 'b'], 'hard'), { require: ['a', 'b'], mode: 'hard' });
  assert.match(src('workflows.tsx'), /getWorkflowPolicy'\)\)\.bindings\.find[\s\S]{0,200}M\.bindingChange\(shown, live, require, mode\)[\s\S]{0,200}setWorkflowBinding', op, change\.require, change\.mode/,
    'the page reads the rule again and sends the merged list');
});

test('chosen reports: the person\'s change is applied to the list as it stands now', () => {
  const { scopeChange } = fresh({}).load('lineage-model.ts');
  const r = (id) => ({ id, kind: 'local', name: id, state: 'notChecked' });
  const ids = (list) => list.map((x) => x.id);
  // The page drew [a]; the assistant chose b. The person adds c.
  assert.deepEqual(ids(scopeChange([r('a')], [r('a'), r('b')], [r('a'), r('c')])), ['a', 'b', 'c']);
  // The person unticks a; the assistant's b stays.
  assert.deepEqual(ids(scopeChange([r('a')], [r('a'), r('b')], [])), ['b']);
  // The assistant already took a off: the person's removal of a is a no-op, and a is not put back.
  assert.deepEqual(ids(scopeChange([r('a')], [], [])), []);
  // The assistant already chose c: it is not added twice.
  assert.deepEqual(ids(scopeChange([r('a')], [r('a'), r('c')], [r('a'), r('c')])), ['a', 'c']);
  assert.match(src('lineage.tsx'), /listReportScope'\)\)\.reports \?\? \[\];\s+next = M\.scopeChange\(scope\.reports, live, next\);[\s\S]{0,200}setReportScope/,
    'Save re-reads the list and sends the merged one');
});

// ---- 6 Documentation: switching away keeps the words when the section moved ----------------------------------------------------
{
  const calls = [];
  const liveSection = { value: undefined };   // what getDocSection answers: the section as it stands now
  const stubs = {
    './bridge': { rpc: async (method, ...args) => { calls.push([method, ...args]); return method === 'getDocSection' ? liveSection.value : undefined; }, onDidChange: () => () => undefined, exportDoc: () => undefined, printDoc: () => undefined, openExternal: () => undefined },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection: () => undefined },
    './hooks': { useAutoGrow: () => ({ current: null }), useFillHeight: () => ({ current: null }), usePersistedState: (_k, d) => [d, () => undefined] },
  };
  const MODEL = '/models/Sales.bim';
  const mount = (loaded, value) => {
    const rt = loaded.rt, state = { value };
    const h = rt.renderHook(() => loaded.mod.SectionEditor({ objRef: 'model', objName: 'Model', sectionKey: 'overview', value: state.value, onSaved: () => undefined }));
    return { h, state, rt };
  };
  // Typed words are held by the model's file in the shared draft store (drafts.ts), so a test sets the model and reads the store through the same loader.
  const open = (value) => { const { rt, load } = fresh(stubs); const mod = load('documentation.tsx'); const drafts = load('drafts'); drafts.setDraftModel(MODEL); return { ...mount({ rt, mod }, value), mod, drafts }; };
  const box = (h) => find(h.result, (el) => el.type === 'textarea');
  const held = (drafts) => drafts.draftStore.get(MODEL, 'docs|model|overview', '');

  test('documentation: switching away while the section moved under a typed draft keeps the words and does not write', async () => {
    calls.length = 0;
    const a = open('Old narrative'); await settle();
    box(a.h).props.onChange({ target: { value: 'My narrative' } }); await settle();
    a.state.value = 'Assistant narrative'; a.h.rerender(); await settle();
    assert.ok(banner(a.h.result), 'the person was told');
    a.rt.unmount();   // the picker moved to another object
    assert.ok(!calls.some((c) => c[0] === 'setDocSection'), 'nothing is written over the assistant\'s section');
    assert.deepEqual(held(a.drafts), { value: 'My narrative', base: 'Old narrative' }, 'the words are kept, not dropped, over the words they started from');
  });

  test('documentation: the kept words are there when the section opens again, with the choice, and Use the new one drops them', async () => {
    const first = open('Old narrative'); await settle();
    box(first.h).props.onChange({ target: { value: 'My narrative' } }); await settle();
    first.rt.unmount();
    const { rt, load } = fresh(stubs); const mod = load('documentation.tsx');
    // a second page over the same persisted state: the assistant has written since
    load('drafts').draftStore.set(MODEL, 'docs|model|overview', '', held(first.drafts)); load('drafts').setDraftModel(MODEL);
    const h = rt.renderHook(() => mod.SectionEditor({ objRef: 'model', objName: 'Model', sectionKey: 'overview', value: 'Assistant narrative', onSaved: () => undefined })); await settle();
    assert.equal(box(h).props.value, 'My narrative', 'the editor opens on the kept words');
    assert.ok(banner(h.result), 'and offers the choice, because the section moved while they were away');
    banner(h.result).props.onUseNew(); await settle();
    assert.equal(box(h).props.value, 'Assistant narrative', 'Use the new one loads the new section');
    assert.equal(load('drafts').draftStore.get(MODEL, 'docs|model|overview', ''), null, 'and ends the draft');
  });

  test('documentation: a section that did not move still saves on the way out, and that save ends the draft', async () => {
    calls.length = 0; liveSection.value = 'Old narrative';
    const q = open('Old narrative'); await settle();
    box(q.h).props.onChange({ target: { value: 'Typed, nobody else touched it' } }); await settle();
    q.rt.unmount(); await settle(); await settle();
    assert.deepEqual(calls.find((c) => c[0] === 'setDocSection')?.slice(1, 4), ['model', 'overview', 'Typed, nobody else touched it'], 'the autosave on switching away still works when nothing moved');
    assert.equal(held(q.drafts), null, 'the engine took it: nothing is held');
  });

  // The page's copy is a moment behind: the assistant rewrote the section inside the reload window, so the page still
  // shows the old text, but the section itself has moved. Switching away re-reads before it writes.
  test('documentation: switching away re-reads the section, and a section the assistant rewrote inside the reload window is not written over', async () => {
    calls.length = 0; liveSection.value = 'Assistant narrative';
    const q = open('Old narrative'); await settle();
    box(q.h).props.onChange({ target: { value: 'Typed words' } }); await settle();
    q.rt.unmount(); await settle();
    assert.ok(calls.some((c) => c[0] === 'getDocSection'), 'the section is read again before the write');
    assert.ok(!calls.some((c) => c[0] === 'setDocSection'), 'nothing is written over the assistant\'s newer section');
    assert.deepEqual(held(q.drafts), { value: 'Typed words', base: 'Old narrative' }, 'the typed words are kept for the person');
    liveSection.value = undefined;
  });
}

// ---- 7 Studio's Ctrl+S commits the field in hand --------------------------------------------------------------------------------
{
  /** The bridge, loaded with a fake page: one focused input whose blur handler writes (as the diagram rename does). */
  function bridgeWorld() {
    const posted = [], listeners = [];
    const fakeWindow = Object.assign(globalThis, {
      acquireVsCodeApi: () => ({ postMessage: (m) => posted.push(m), getState: () => undefined, setState: () => undefined }),
      addEventListener: (type, fn) => { if (type === 'message') listeners.push(fn); },
    });
    void fakeWindow;
    const doc = { body: {}, activeElement: null };
    globalThis.document = doc;
    const load = makeLoader({});
    const bridge = load('bridge.ts');
    const deliver = (data) => listeners.forEach((fn) => fn({ data }));
    return { bridge, posted, doc, deliver };
  }

  test('Studio: asking for a commit blurs the focused field and answers only after the write it started has landed', async () => {
    const w = bridgeWorld();
    let blurred = false;
    const input = { matches: (sel) => /input/.test(sel), blur() { blurred = true; void w.bridge.rpc('setCalcGroupPrecedence', 'calcgroup:Time', 3); } };
    w.doc.activeElement = input;
    w.deliver({ type: 'commitFocused', id: 41 });
    await wait(20);
    assert.ok(blurred, 'the field lost focus, so its own blur handler ran');
    const rpcMsg = w.posted.find((m) => m.type === 'rpc');
    assert.ok(rpcMsg, 'the blur handler\'s write went to the host');
    assert.ok(!w.posted.some((m) => m.type === 'focusedCommitted'), 'no answer while the write is still in flight');
    w.deliver({ type: 'rpcResult', id: rpcMsg.id, result: 'ok' });
    await wait(20);
    assert.deepEqual(w.posted.filter((m) => m.type === 'focusedCommitted').map(({ type, id }) => ({ type, id })), [{ type: 'focusedCommitted', id: 41 }], 'answered once the write landed');
  });

  test('Studio: with nothing being typed in, the commit answers at once', async () => {
    const w = bridgeWorld();
    w.doc.activeElement = w.doc.body;
    w.deliver({ type: 'commitFocused', id: 7 });
    await wait(20);
    assert.deepEqual(w.posted.filter((m) => m.type === 'focusedCommitted').map(({ type, id }) => ({ type, id })), [{ type: 'focusedCommitted', id: 7 }]);
    assert.ok(!w.posted.some((m) => m.type === 'rpc'));
  });

  test('Studio: the host asks Studio to commit before every Save (Ctrl+S and the Save button), and waits for the answer', () => {
    const ext = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
    const save = ext.slice(ext.indexOf('async function saveCommand('));
    assert.match(save.slice(0, 900), /propGrid\?\.commitFocused\(\)[\s\S]{0,400}await commitStudioFocused\(\)/, 'saveCommand commits the grid field and then the Studio field before it saves');
    assert.match(ext, /msg\?\.type === 'focusedCommitted'\) \{ studioCommitWaiters\.get\(msg\.id\)\?\.\(\)/, 'the relay hands the answer to the waiting Save');
    assert.match(ext, /async function commitStudioFocused[\s\S]{0,600}2500\);/, 'a Studio that never answers cannot hold a Save hostage');
  });
}

// ---- the sweep: every webview write is classified -------------------------------------------------------------------------------
// The class is "a save that writes a whole thing (or merges by id) from a copy taken earlier". This test reads every rpc write
// in webview/src and fails on one it has not been told about, so the next whole-object save cannot arrive unclassified.
//   guarded   an editor with a typed draft: the editbase guard (follow / ask, Save re-reads) or a version check at Save
//   delta     a whole list built from the page's copy: Save re-reads the live list and applies only the person's difference
//   field     one named field set to the value the control shows (a toggle, a pick): there is no older whole copy to write back
//   reviewed  the write waits behind a preview or dry run the person approves, or a fence/hash the engine checks
//   action    a command with no stored copy to be stale against (create, delete, run, open, import)
const CLASS = {
  guarded: ['editInsight', 'setPrimer', 'setAiInstructions', 'setTableSourceMapping', 'saveTest', 'setDax', 'setTablePermission', 'setDocSection', 'setSpec',
    'saveSqlSource', 'setIncrementalRefreshPolicy', 'loadBpaRules', 'loadReadinessRules', 'setPartitionM', 'updateNamedExpression', 'editWorkflowDocument', 'saveWorkflow', 'addInterviewQuestion',
    'writeStartWords'],   // writeStartWords (join 7): the person's edit of words on a start plan line carries the words it started from (StartWord.Expected), and the engine refuses it, inside the write, when they are not the line's words any more
  delta: ['setWorkflowBinding', 'setReportScope'],
  field: ['setColumnMetadata', 'setRolePermission', 'setCalcGroupPrecedence', 'setCalcItemFormatString', 'setRelationship', 'setRelationshipCardinality',
    'setPerspectiveMember', 'setRoleMember', 'setTableObjectPermission', 'setColumnObjectPermission', 'setCompatibilityLevel', 'setDecisionPointEnabled',
    'setWorkflowEnabled', 'setWorkflowActivation', 'setWorkflowEnforcement', 'setAgentPolicyCell', 'setAgentPolicyEnabled', 'setAgentPolicyPreset', 'setPublishDestination',
    'setModelSqlEndpoint', 'setDefaultAccountProfile', 'setConsent', 'setPlanItem', 'setPlanRepeat', 'setCalcGroupPrecedence', 'editStartPlan'],   // editStartPlan (join 6): one edit op on one named line, table or question; the engine applies it to the live plan
  reviewed: ['updateDataAgent', 'createDataAgent', 'applyDiff', 'applyPlan', 'replaceInObject', 'applySchemaUpdate', 'saveLayout'],
  action: ['createCalculationGroup', 'createCalculationItem', 'createFieldParameter', 'createNamedExpression', 'createPerspective', 'createRelationship', 'createRole',
    'createModel', 'createHistoryCheckpoint', 'addPlanItem', 'addPlanRepeat', 'addPlanTask', 'defineCalendarFromTemplate', 'loadSpec', 'saveSpec', 'saveEvidence', 'applySafeFixes', 'editDraft'],
};
test('every webview write is classified, so a new whole-object save cannot arrive unreviewed', () => {
  const known = new Map();
  for (const [kind, names] of Object.entries(CLASS)) for (const n of names) known.set(n, kind);
  const dir = resolve(root, 'webview', 'src');
  const verbs = /^(set|update|edit|save|load[A-Z]|apply|replace|put|write|configure)/;
  const seen = new Map();
  for (const file of readdirSync(dir).filter((f) => /\.(tsx|ts)$/.test(f))) {
    const t = readFileSync(join(dir, file), 'utf8');
    for (const m of t.matchAll(/\brpc(?:<[^()]*?>)?\(\s*'([A-Za-z]+)'/g)) if (verbs.test(m[1])) { if (!seen.has(m[1])) seen.set(m[1], new Set()); seen.get(m[1]).add(file); }
  }
  const missing = [...seen].filter(([name]) => !known.has(name)).map(([name, files]) => `${name} (${[...files].join(', ')})`);
  assert.deepEqual(missing, [], `these webview writes are not classified in the stale-copy sweep (guarded / delta / field / reviewed / action): ${missing.join('; ')}`);
});

test('the surfaces the second round put behind the guard ask it first', () => {
  const rules = src('rulesauthor.tsx');
  assert.match(rules, /if \(form\.editingId && !\(await guard\.beforeSave\(\)\)\) return;[\s\S]{0,1500}rpc\(loadOp, JSON\.stringify\(\[rule\]\), false, 'human', expected\)/, 'the rule editor asks the guard before it merges the rule');
  const mcode = src('mcode.tsx');
  assert.match(mcode, /await policyGuardRef\.current\.beforeSave\(\)\)\) return;[\s\S]{0,200}rpc<\{ warning\?: string \| null \}>\('setIncrementalRefreshPolicy'/, 'the policy form asks the guard before it writes the policy');
  assert.match(src('spec.tsx'), /rpc<SpecSnapshot>\('getSpec'\);\s+if \(baseContent != null && contentOf\(live\.spec\) !== baseContent\)[\s\S]{0,300}return;[\s\S]{0,400}rpc<SpecSnapshot>\('setSpec', JSON\.stringify\(draft\)/, 'the Spec Save compares the spec content it was typed over (round 3: not a version, which a restarted engine counts again from 0) before it writes the draft');
});
