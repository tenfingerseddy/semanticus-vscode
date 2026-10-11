// drafts-sol round 3, J2: the Model tree's own commands, run through the compiled personAction (the same call the one registration wrapper makes), do not write into model B
// when B opens while the person is in the prompt (the check's T1: test/verify-drafts-sol-2.test.mjs and tools/uishot/verify-dsol2-tree-drive.mjs).
//
// Each command is the REAL source of extension.ts (transpiled), with only its surroundings stubbed. The connection writes through the extension's own compiled
// FencedMessageWriter, and the stub engine applies the two rules the real one has for a request from that writer: a request that names another model than the open one is
// refused (PersonFence, round 2), and so is one that names none while a model is open (round 3). A write that gets through is recorded against the model that was open.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const ts = require('typescript');
const { FencedMessageWriter, personAction, MODEL_REFUSAL } = require(join(root, 'out', 'modelFence.js'));
const { folderParts, normFolder, folderRef } = require(join(root, 'out', 'folders.js'));
const extSrc = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

const A = 'file:/m/A.bim';
const B = 'file:/m/B.bim';

/** The source of one top-level `async function name(...)` of extension.ts, transpiled to plain JavaScript. */
function functionSource(name) {
  const start = extSrc.indexOf(`\nasync function ${name}(`);
  assert.ok(start >= 0, `${name} is a function of extension.ts`);
  const next = extSrc.slice(start + 1).search(/\n(async function |function |class |const |let |export |\/\/\/ |\/\/ ----)/);
  const body = extSrc.slice(start + 1, start + 1 + next);
  return ts.transpileModule(body, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;
}

const READS = new Set(['sessionInfo', 'listRoles', 'getObject', 'getObjectProperties', 'listFormatTemplates', 'listTree', 'getEntitlement']);
const TEMPLATES = [{ name: 'Dynamic currency', kind: 'dynamic', formatString: 'SELECTEDVALUE(Currency[Format])', category: 'Currency', common: true, exampleOutput: '$1', note: '' }];

/** Two models with the same objects, A open first. `switchToB()` is what the assistant does over MCP while the person is still in the prompt. */
function world() {
  const engine = { open: { identity: A, session: 's-a' } };
  const switchToB = () => { engine.open = { identity: B, session: 's-b' }; };
  const applied = [];   // { model, method }: a write that got through, and the model that was open when it did
  const refusals = [];
  const sent = [];
  const writer = new FencedMessageWriter({ write: (m) => { sent.push(m); return Promise.resolve(); }, onError: () => undefined, onClose: () => undefined, end() {}, dispose() {} });
  let id = 0;
  const apply = (msg) => {
    const { method, params } = msg;
    if (READS.has(method)) {
      if (method === 'listRoles') return [{ name: 'Readers', tableFilters: [] }];
      if (method === 'getObject') return { properties: { dataType: 'Int64' } };
      if (method === 'getObjectProperties') return [{ name: 'DataCategory', value: '' }, { name: 'CrossFilteringBehavior', value: 'OneDirection' }];
      if (method === 'listFormatTemplates') return TEMPLATES;
      if (method === 'sessionInfo') return { modelIdentity: engine.open.identity, sessionId: engine.open.session };
      return [];
    }
    // PersonFence, round 2: the request names another model than the open one.
    if (msg.modelIdentity && msg.modelIdentity !== engine.open.identity) { refusals.push(method); throw new Error(MODEL_REFUSAL); }
    // PersonFence, round 3: the extension's own writer names a model on every request; one that names none cannot say which model it meant.
    if (msg.namesModel && !msg.modelIdentity) { refusals.push(method); throw new Error(MODEL_REFUSAL); }
    applied.push({ model: engine.open.identity, method, params });
    if (method.startsWith('create')) return 'table:' + (params[0] ?? 'New');
    if (method === 'renameObject') return { newRef: 'measure:Sales/' + params[1] };
    if (method === 'renameDisplayFolder') return { members: 1, to: params[2] };
    return undefined;
  };
  const conn = {
    sendRequest(method, ...params) {
      const msg = { jsonrpc: '2.0', id: ++id, method, params };
      void writer.write(msg);   // the fenced writer stamps the model on the request exactly as it goes out
      try { return Promise.resolve(apply(msg)); } catch (e) { return Promise.reject(e); }
    },
  };
  return { engine, switchToB, applied, refusals, sent, conn };
}

const measure = { ref: 'measure:Sales/Amount', name: 'Amount', kind: 'measure', hasChildren: false };
const column = { ref: 'column:Sales/Region', name: 'Region', kind: 'column', hasChildren: false };
const table = { ref: 'table:Sales', name: 'Sales', kind: 'table', hasChildren: true };
const role = { ref: 'role:Readers', name: 'Readers', kind: 'role', hasChildren: false };
const rel = { ref: 'relationship:Sales[Date]->Date[Date]', name: 'Sales[Date] -> Date[Date]', kind: 'relationship', hasChildren: false };
const folder = { ref: folderRef('Sales', 'KPIs'), name: 'KPIs', kind: 'dfolder', hasChildren: true };

/** One command: its source(s), its arguments, what the person answers (the LAST prompt is the one B opens during), and the engine method that is the write. */
const CASES = [
  { cmd: 'renameCmd', args: [measure], answers: { input: ['Revenue'] }, write: 'renameObject' },
  { cmd: 'authorDelete', args: [measure, [measure]], answers: { warn: ['Delete'] }, write: 'deleteObject' },
  { cmd: 'roleAddRlsCmd', args: [role], answers: { input: ['table:Sales', '[Region] = "West"'] }, write: 'setTablePermission' },
  { cmd: 'summarizeByCmd', args: [column], answers: { pick: [(items) => items.find((i) => i === 'Sum')] }, write: 'setColumnMetadata' },
  { cmd: 'dataCategoryCmd', args: [column], answers: { pick: [(items) => items.find((i) => i.value === 'City')] }, write: 'setColumnMetadata' },
  { cmd: 'relCrossFilterCmd', args: [rel], answers: { pick: [(items) => items.find((i) => i.value === 'BothDirections')] }, write: 'setRelationship' },
  { cmd: 'relToggleActiveCmd', args: [rel], answers: {}, write: 'setRelationship', noPrompt: true },
  { cmd: 'roleModelPermCmd', args: [role], answers: { pick: ['Read'] }, write: 'setRolePermission' },
  { cmd: 'roleAddMemberCmd', args: [role], answers: { input: ['user@contoso.com'] }, write: 'setRoleMember' },
  { cmd: 'timeIntelCmd', args: [measure], answers: { input: ['column:Date/Date'] }, write: 'generateTimeIntelligence' },
  { cmd: 'markDateTableCmd', args: [table], answers: { input: ['Date'] }, write: 'markDateTable' },
  { cmd: 'renameFolderCmd', args: [folder], answers: { input: ['KPIs2'] }, write: 'renameDisplayFolder' },
  { cmd: 'setHiddenCmd', args: [true, measure, [measure]], answers: {}, write: 'setObjectProperty', noPrompt: true },
  { cmd: 'setFormatCmd', args: [measure], answers: { pick: [(items) => items.find((i) => i.tpl?.kind === 'dynamic')] }, write: 'setMeasureFormatExpression' },
  { cmd: 'authorNewRole', args: [], answers: { input: ['Analysts'] }, write: 'createRole' },
  { cmd: 'authorNewCalcGroup', args: [], answers: { input: ['Currency'] }, write: 'createCalculationGroup', also: ['createAndRefresh'] },
];

/** Run one case. `moment`: 'prompt' opens B during the last prompt; 'lag' opens B before the command starts but the host has not been told (a command with no prompt); 'never' is the control. */
async function runCase(c, moment, personDoor = true) {
  const w = world();
  const errors = [];
  const queues = { input: [...(c.answers.input ?? [])], pick: [...(c.answers.pick ?? [])], warn: [...(c.answers.warn ?? [])] };
  const answer = (kind, items) => {
    const q = queues[kind];
    const next = q.shift();
    if (q.length === 0 && moment === 'prompt') w.switchToB();   // the last prompt: the person types and the assistant opens B in the meantime
    return typeof next === 'function' ? next(items) : next;
  };
  const window = {
    showInputBox: async () => answer('input'),
    showQuickPick: async (items) => answer('pick', items),
    showWarningMessage: async () => answer('warn'),
    showInformationMessage: () => undefined, setStatusBarMessage: () => undefined,
    showErrorMessage: (m) => { errors.push(m); },
  };
  const scope = {
    conn: w.conn, vscode: { window, QuickPickItemKind: { Separator: -1 } }, tree: { refresh: () => undefined }, treeView: { selection: [] }, warnNoEngine: () => undefined,
    COPYABLE_KINDS: { measure: 'measure' }, folderParts, normFolder, openDax: async () => undefined,
    refParts: (ref) => { const rest = ref.slice(ref.indexOf(':') + 1); return { name: rest.includes('/') ? rest.slice(rest.indexOf('/') + 1) : rest }; },
    selectionRefs: (n, ns) => (ns?.length ? ns : n ? [n] : []),
  };
  const source = [c.cmd, ...(c.also ?? [])].map(functionSource).join('\n');
  const fn = new Function(...Object.keys(scope), `${source}\nreturn ${c.cmd};`)(...Object.values(scope));
  if (moment === 'lag') w.switchToB();   // B is open already; the host has not caught up (it still shows A)
  const host = A;                          // what the host showed at the click
  if (personDoor) await personAction(c.args, host, () => fn(...c.args));
  else await fn(...c.args);               // the old way: the handler called directly, no scope (the check's red)
  return { w, errors };
}

for (const c of CASES) {
  const moment = c.noPrompt ? 'lag' : 'prompt';
  test(`J2 ${c.cmd}: ${c.noPrompt ? 'with B already open and the host a moment behind' : 'a prompt left open while B opens'}, the write is not made into B (and the person is told)`, async () => {
    const { w, errors } = await runCase(c, moment);
    assert.deepEqual(w.applied.filter((a) => a.model === B), [], `B was written into by ${c.cmd}: ${JSON.stringify(w.applied)}`);
    assert.ok(w.sent.some((m) => m.method === c.write), `the command did try to write (${c.write}), so this test is not vacuous: ${JSON.stringify(w.sent.map((m) => m.method))}`);
    assert.ok(w.sent.filter((m) => m.method === c.write).every((m) => m.modelIdentity === A && m.namesModel === true), 'the write names the model the action started on');
    assert.ok(w.refusals.includes(c.write), `the engine refused ${c.write}`);
    assert.ok(errors.length >= 1 || c.noPrompt, 'the person sees the refusal');
  });
  test(`J2 control ${c.cmd}: with no other model opening the write lands on A as the person asked`, async () => {
    const { w, errors } = await runCase(c, 'never');
    assert.ok(w.applied.some((a) => a.model === A && a.method === c.write), `${c.write} was applied to A: ${JSON.stringify(w.applied)} (errors ${JSON.stringify(errors)})`);
    assert.deepEqual(w.applied.filter((a) => a.model === B), []);
  });
}

test('J2: the same commands called directly, with no personAction around them, name no model and are refused outright (the registration wrapper is what makes them work)', async () => {
  for (const c of CASES.slice(0, 3)) {
    const { w } = await runCase(c, 'never', false);
    assert.deepEqual(w.applied, [], `${c.cmd}: a write with no scope names no model, and the engine refuses it while a model is open`);
  }
});
