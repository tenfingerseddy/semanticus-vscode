// VERIFY (m11/uat-ui, 2 Oct): two editors of the stale-copy class that the lane left uncovered still write an older copy
// over the assistant's newer change, without a word to the person.
//
// The class (UAT-F-S06a-1-002, UAT-F-S06b-2-007): an editor saves a whole thing from a copy it took earlier. The lane's rule
// (webview/src/editbase.tsx): a Save whose starting point is older than the live thing writes nothing until the person
// chose. These two surfaces save a whole object and do not follow that rule:
//   1 the incremental refresh policy form (mcode.tsx): the person changes one field, the assistant changes another field of
//     the same policy (set_incremental_refresh_policy over MCP), and Save policy writes every field from the form, so the
//     assistant's field silently goes back to the old value;
//   2 the custom rule editor (rulesauthor.tsx): the person edits one field of a rule, the assistant rewrites the same rule
//     (load_rules, merge by id), and Save changes writes the whole rule from the form, so the assistant's condition is lost.
// Each test passes when Save either writes nothing until the person chose, or writes the assistant's value for the field
// the person did not touch. These tests are left failing on purpose: they prove the defect; the fix is the builder's.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, settle } from './helpers/webview-load.mjs';

/** The shared helper's hook runtime with one change: useMemo and useCallback keep their value while their dependencies are
 *  equal, as React does. The policy form derives arrays with useMemo and lists them as effect dependencies, so a runtime that
 *  recomputes them on every render re-runs those effects forever. */
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
  return { React, renderHook(fn) { current = { fn, result: null, rerender: () => render() }; render(); return current; } };
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

// ---- 1 the incremental refresh policy form -------------------------------------------------------------------------
test('policy form: Save policy does not write the old refresh window over the one the assistant just set', async () => {
  const calls = [];
  const changed = [];
  const policy = (incrementalPeriods, incrementalGranularity) => ({
    table: 'Sales', enabled: true, policyType: 'Basic', mode: 'Import',
    rollingWindowGranularity: 'Year', rollingWindowPeriods: 5, incrementalGranularity, incrementalPeriods,
    incrementalPeriodsOffset: 0, sourceExpression: null, pollingExpression: null, wiredDateField: 'OrderDate',
  });
  let engine = policy(10, 'Day');
  const doc = () => ({
    header: { compatibilityLevel: 1600 },
    tables: [{ ref: 'table:Sales', name: 'Sales', isCalculated: false, partitions: [{ name: 'Sales', mode: 'Import', sourceType: 'M', source: 'let S = Sql.Database("h","d"), T = Table.SelectRows(S, each [OrderDate] >= RangeStart and [OrderDate] < RangeEnd) in T' }] }],
    columns: [{ table: 'Sales', name: 'OrderDate', dataType: 'DateTime', sourceColumn: 'OrderDate', isCalculated: false }],
    expressions: [
      { name: 'RangeStart', kind: 'M', expression: '#datetime(2020,1,1,0,0,0) meta [IsParameterQuery=true]' },
      { name: 'RangeEnd', kind: 'M', expression: '#datetime(2021,1,1,0,0,0) meta [IsParameterQuery=true]' },
    ],
  });
  const stubs = {
    './bridge': {
      rpc: async (method, ...args) => {
        calls.push([method, ...args]);
        if (method === 'getDocModel') return doc();
        if (method === 'getIncrementalRefreshPolicy') return engine;
        return {};
      },
      onDidChange: (fn) => { changed.push(fn); return () => undefined; },
      loadState: (_k, d) => (_k.includes('table') ? 'Sales' : d), saveState: () => undefined,
    },
    './meditor': { MEditor: () => null },
    './mlang': { formatM: async (s) => ({ ok: true, text: s }) },
    './manalysis': { mDiagnostics: async () => [] },
    './connection': { useConnection: () => ({ conn: null }) },
    './mtransform': { gen: {}, mQuoteIdent: (s) => s },
    './pqtransforms': { SamplePreview: () => null, AppliedStepsPanel: () => null, sourceMName: (c) => c.sourceColumn ?? c.name },
  };
  const { rt, load } = fresh(stubs);
  const { MCodeView } = load('mcode.tsx');
  const h = rt.renderHook(() => MCodeView({}));
  for (let i = 0; i < 6; i++) await settle();
  await wait(50);

  const configure = find(h.result, (el) => el.type === 'button' && /^Configure/.test(text(el)));
  assert.ok(configure, 'the Incremental refresh card has a Configure button');
  configure.props.onClick(); await settle();
  const nums = () => findAll(h.result, (el) => el.type?.name === 'Num');
  assert.equal(nums()[0]?.props.value, 5, 'the form opens on the stored policy: store 5 years');
  assert.equal(nums()[1]?.props.value, 10, 'and refresh 10 days');

  // The person sets "store" to 7 years.
  nums()[0].props.onChange(7); await settle();
  // The assistant changes the refresh window of the same policy to 3 months (set_incremental_refresh_policy over MCP).
  engine = policy(3, 'Month');
  changed.forEach((fn) => fn({ revision: 2, origin: 'agent' }));
  await wait(400); for (let i = 0; i < 6; i++) await settle();

  const save = find(h.result, (el) => el.type === 'button' && text(el) === 'Save policy');
  assert.ok(save, 'Save policy is on the page');
  await save.props.onClick(); for (let i = 0; i < 6; i++) await settle();
  const write = calls.find((c) => c[0] === 'setIncrementalRefreshPolicy');
  if (write) {
    // (method, table, dateColumn, storePeriods, storeGranularity, refreshPeriods, refreshGranularity, ...)
    assert.deepEqual([write[5], write[6]], [3, 'Month'],
      `Save policy wrote refresh ${write[5]} ${write[6]} over the assistant's newer 3 Month window without asking (lost work)`);
  }
});

// ---- 2 the custom rule editor ----------------------------------------------------------------------------------------
test('rule editor: Save changes does not write the old condition over a rule the assistant just rewrote', async () => {
  const calls = [];
  const changed = [];
  const rule = (expression) => ({ id: 'ORG-DESC', name: 'Measures need descriptions', category: 'Descriptions', severity: 'Medium', scope: 'Measure', expression, message: 'needs a description' });
  let live = [rule('string.IsNullOrEmpty(Description)')];
  const stubs = {
    './bridge': {
      rpc: async (method, ...args) => {
        calls.push([method, ...args]);
        if (method === 'getCustomRules') return { bpa: [], readiness: live.map((r) => ({ ...r })), readinessCategories: ['Descriptions'], scopes: ['Measure'] };
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
  for (let i = 0; i < 4; i++) await settle();
  find(h.result, (el) => el.type === 'button' && el.props.title === 'Expand').props.onClick(); await settle();
  find(h.result, (el) => el.type?.name === 'MiniBtn' && text(el) === 'Edit').props.onClick(); await settle();

  // The person rewords the message.
  const message = find(h.result, (el) => el.type === 'input' && el.props.placeholder === '%object% needs a description');
  assert.equal(message.props.value, 'needs a description', 'the form opens on the rule');
  message.props.onChange({ target: { value: 'has no description yet' } }); await settle();
  // The assistant rewrites the same rule's condition (load_rules, merge by id).
  live = [rule('string.IsNullOrWhiteSpace(Description) and not IsHidden')];
  changed.forEach((fn) => fn({ revision: 2, origin: 'agent' }));
  await wait(650); for (let i = 0; i < 6; i++) await settle();   // the panel's refresh (400 ms) and the rule check (500 ms)

  const save = find(h.result, (el) => el.type?.name === 'Btn' && el.props.primary);
  assert.ok(save && !save.props.disabled, 'Save changes is enabled');
  save.props.onClick(); for (let i = 0; i < 6; i++) await settle();
  const write = calls.find((c) => c[0] === 'loadReadinessRules');
  if (write) {
    const sent = JSON.parse(write[1]).find((r) => r.ID === 'ORG-DESC');
    assert.equal(sent?.Expression, 'string.IsNullOrWhiteSpace(Description) and not IsHidden',
      `Save changes wrote the old condition "${sent?.Expression}" over the assistant's newer rule without asking (lost work)`);
  }
});
