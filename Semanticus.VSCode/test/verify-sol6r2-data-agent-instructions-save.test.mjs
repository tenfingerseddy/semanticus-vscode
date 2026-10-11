// verify (m11/sol6-ui round 2, the class test's allowances): the data agent's Teach panel is the same class Sol's C4 named. Its
// "Save instructions" sends updateDataAgent(workspace, agent, typed, ...) with no read at Save and no value it replaces, and the engine
// keeps every part it fetches at commit time except the instructions, which it replaces outright. The panel seeds its box once, when the
// agent is picked; the assistant can write the same draft instructions through update_data_agent on the MCP door at any time after. The
// review step between Save and Apply shows only "aiInstructions N chars", not the text it replaces, so the class test's allowance
// ("the second writes only what that preview showed the person") does not hold: the assistant's newer instructions are written over
// without a word. This test fails until the Apply names what it replaces (or reads first and asks), the way the model editors now do.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req, makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

const ts = req('typescript');
const draftModules = (React) => { const real = makeLoader({ './bridge': { loadState: (k, d) => d, saveState: () => undefined } }, React); return { './drafts': real('drafts.ts'), './editbase': real('editbase.tsx') }; };
const walk = (node, visit) => {
  if (node == null || typeof node !== 'object') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node); walk(node.props?.children, visit);
};
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };

/** Load the real dataagent.tsx with its imports stubbed, and reach its (unexported) TeachPanel. */
function loadTeachPanel(rpc, React) {
  const src = readFileSync(join(webview, 'src', 'dataagent.tsx'), 'utf8') + '\nexport { TeachPanel };\n';
  const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const stubs = {
    './bridge': { rpc, copyText: () => undefined },
    './hooks': { usePersistedState: (k, v) => [v, () => undefined] },
    './workflows': { Button: () => null },
    './pro': { useTier: () => 'pro', isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null },
    './connection': { useConnection: () => ({}) },
    './accountpicker': { AccountPicker: () => null },
    './dataagent-schema.mjs': { isEditableElementTree: () => false, parseElementTree: () => [], updateElementSelection: (x) => x },
    './copy': { uiLabel: (s) => s },
    // join 11: the Teach editors hold their drafts in the shared store (no model is set here, so nothing is held)
    ...draftModules(React),
  };
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? React : req(n)), module, module.exports);
  return module.exports.TeachPanel;
}

test('data agent Teach: Save instructions does not write over the assistant\'s newer draft instructions', async () => {
  // Fabric's copy of the agent's draft instructions, and a fake of the engine's update: it replaces the instructions part wholesale,
  // as LocalEngine.UpdateDataAgentAsync does, unless the call names a value it replaces that no longer matches.
  const fabric = { instr: 'Use Sales Amount for revenue.' };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args.slice(0, 7)]);
    if (method !== 'updateDataAgent') return undefined;
    const [, , instr, , , , commit] = args;
    const expected = args[11];   // any value the save names as its start, appended after the engine's current parameters
    if (!commit) return { status: 'dry-run', summary: `aiInstructions ${instr.length} chars` };
    if (expected != null && expected !== fabric.instr) throw new Error('changed since you opened it; nothing was written');
    fabric.instr = instr;
    return { status: 'ok' };
  };
  const rt = makeHookRuntime();
  const TeachPanel = loadTeachPanel(rpc, rt.React);
  const detail = { info: { id: 'agent-1' }, draft: { aiInstructions: fabric.instr, dataSources: [] } };
  const h = rt.renderHook(() => TeachPanel({ workspaceId: 'ws', agentId: 'agent-1', detail, tier: 'pro', authMode: 'azcli', tenantId: null, onChanged: () => calls.push(['reload']) }));
  await settle();
  const box = find(h.result, (el) => el.type === 'textarea');
  box.props.onChange({ target: { value: 'Use Sales Amount for revenue. Fiscal year starts in July.' } });   // the person types
  await settle();
  fabric.instr = 'Use Net Sales for revenue; Sales Amount includes returns.';   // the assistant rewrote the draft (update_data_agent, commit true)
  const flow = find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save instructions…');
  assert.ok(flow, 'the panel has its Save instructions flow');
  await flow.props.run(false);   // Save instructions... : the review step (the engine's dry run names only a character count)
  try { await flow.props.run(true); } catch { /* a refusal is the right answer */ }   // Apply (writes to Fabric)
  assert.equal(fabric.instr, 'Use Net Sales for revenue; Sales Amount includes returns.',
    `the assistant's newer draft instructions were written over without a word (calls: ${JSON.stringify(calls)})`);
});
