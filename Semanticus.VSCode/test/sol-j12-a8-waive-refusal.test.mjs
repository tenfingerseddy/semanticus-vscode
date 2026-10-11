// A8: use the actual BPA callback and Accept control. A model-fence
// refusal must leave the audited reason available rather than clear it.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const flat = (el) => el == null || typeof el === 'boolean' ? '' : Array.isArray(el) ? el.map(flat).join('')
  : typeof el === 'object' ? flat(el.props?.children) : String(el);
const REASON = 'Historical codes stay unchanged because the source contract requires them.';

test('A8: a refused Accept write preserves the reason through a redraw', async () => {
  const disk = makeDisk();
  const calls = [];
  const finding = { ruleId: 'R1', ruleName: 'Use readable names', category: 'Naming', severity: 2,
    objectRef: 'column:Sales/Code', objectName: 'Code', message: 'A finding.', canAutoFix: false };
  let refuse;
  const pg = makePage({
    './bridge': { loadState: disk.loadState, saveState: disk.saveState, onDidChange: () => () => undefined,
      revealInTree() {},
      rpc: (method, ...args) => {
        calls.push([method, ...args]);
        if (method === 'bpaScan') return Promise.resolve({ ruleCount: 1, violationCount: 1,
          autoFixable: 0, waivedCount: 0, violations: [finding], ruleErrors: [] });
        if (method === 'waiveFinding') return new Promise((resolve, reject) => { refuse = reject; });
        throw new Error(`Unexpected call: ${method}`);
      } },
    './hooks': { useFixState: () => ({ state: {}, keyOf: (r, o) => r + o, fix() {} }) },
    './ui': { Caret: () => null },
    './rulesauthor': { CustomRulesPanel: () => null },
    './sendtask': { GiveToAssistant: () => null, findingBuilder: () => ({}) },
  }, MODEL);
  pg.React.createContext = (value) => ({ current: value, Provider: ({ children }) => children });
  pg.React.useContext = (context) => context.current;
  const F = pg.load('findings.tsx');
  const B = pg.load('bpa.tsx');
  const page = pg.mount(() => B.BpaView());
  await settleAll(); page.rerender();
  const grouped = find(page.result, (el) => el.type?.name === 'GroupedFindings');
  const action = grouped.props.renderActions(grouped.props.rows[0]);
  const element = find(action, (el) => el.type?.name === 'WaiveControl');
  const scope = 'waive|bpa|' + JSON.stringify(['R1', 'column:Sales/Code']);
  F.WaiveHold.current = { scope, label: 'Code (Use readable names)' };
  const mount = () => pg.mount(() => element.type(element.props));
  let box = mount();
  find(box.result, (el) => el.type?.name === 'FBtn' && flat(el) === 'Accept').props.onClick();
  box.rerender();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: REASON } });
  await settleAll(); box.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, scope)?.value, REASON);
  find(box.result, (el) => el.type?.name === 'FBtn' && flat(el) === '✓').props.onClick();
  assert.deepEqual(calls.at(-1), ['waiveFinding', 'bpa', 'R1', 'column:Sales/Code', REASON]);
  refuse(new Error('This page was showing another model than the one open now, so nothing was changed. Anything you typed is kept.'));
  await settleAll(); page.rerender();
  assert.match(flat(page.result), /This page was showing another model/);
  box.unmount();
  box = mount();
  await settleAll(); box.rerender();
  assert.equal(find(box.result, (el) => el.type === 'input')?.props.value, REASON,
    'the refusal did not save the reason; its editor must reopen on those words');
});
