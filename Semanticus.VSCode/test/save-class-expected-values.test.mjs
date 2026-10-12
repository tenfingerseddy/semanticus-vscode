// Sol join 6, round 2: the CLASS test for "a save writes over a value another writer put there since". Rounds 1 and 2 each closed the saves a reviewer named and left
// a sibling beside them (the calculation item's Save format sat one line below the fenced expression Save). This test finds the whole class from the sources, so
// the next setter someone adds cannot be an unfenced sibling without this test saying so.
//
// WHAT IT DOES. It scans every webview source and the extension host for the engine calls they make (rpc(...) and sendRequest(...), plus any other place a
// setter's name appears as a string), keeps the ones that are SETTERS (the engine method is named set*, update*, edit*, save*, rename*, replace*, apply* or load*;
// creates and deletes are not setters: they have no value to overwrite or nothing left to protect), and demands of each one of two things:
//   1. FENCED: the engine setter has an expected* (or expect*) parameter, and EVERY call site passes something at that position that is not the literal null.
//      The engine compares it inside its own write (LocalEngine.GuardExpectedValue / GuardExpectedFields); the null path stays unfenced for the MCP door.
//   2. ALLOWED, with a reason written here, per setter (or per call site, by a pattern of the call's text). A reason must say why nothing the person typed
//      can be written over. An entry that no longer matches a call is stale and fails the test, so the list cannot rot.
// It does not prove a fence is RIGHT (the engine tests in Semanticus.Tests/Sol6SaveFenceTests.cs and the component tests do that); it proves none is missing.
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..');

const SETTER = /^(set|update|edit|save|rename|replace|apply|load)[A-Z]/;
const FENCE_PARAM = /^expect/;

// ---- 1. the engine's RPC methods and their parameters ----------------------------------------------------------
function splitTop(text, sep = ',') {
  const parts = []; let depth = 0, quote = null, cur = '';
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quote) {
      cur += c;
      if (c === '\\') { cur += text[++i] ?? ''; continue; }
      if (c === quote) quote = null;
      continue;
    }
    if (c === '"' || c === "'" || c === '`') { quote = c; cur += c; continue; }
    if (c === '(' || c === '[' || c === '{' || c === '<') depth++;
    if (c === ')' || c === ']' || c === '}' || (c === '>' && text[i - 1] !== '=')) depth--;
    if (c === sep && depth === 0) { parts.push(cur); cur = ''; continue; }
    cur += c;
  }
  if (cur.trim() !== '') parts.push(cur);
  return parts.map((p) => p.trim());
}
const engine = new Map();   // rpc name -> parameter names
for (const file of ['EngineRpcTarget.cs', 'HumanGovernanceRpcTarget.cs']) {
  const cs = readFileSync(join(repo, 'Semanticus.Engine', file), 'utf8');
  for (const m of cs.matchAll(/public\s+Task(?:<[^\n(]*?>)?\s+([a-z]\w*)\(([^)]*)\)\s*=>/g)) {
    const params = splitTop(m[2]).map((p) => p.replace(/=.*$/s, '').trim().split(/\s+/).pop());
    engine.set(m[1], params);
  }
}
assert.ok(engine.size > 200, `the engine RPC list was read (${engine.size} methods)`);

// ---- 2. every call the webview and the extension make ---------------------------------------------------------
function files(dir, accept) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...files(full, accept));
    else if (accept(name)) out.push(full);
  }
  return out;
}
const sources = [
  ...files(join(root, 'webview', 'src'), (n) => /\.(ts|tsx)$/.test(n) && !/\.d\.ts$/.test(n)),
  ...files(join(root, 'src'), (n) => /\.ts$/.test(n) && !/\.d\.ts$/.test(n)),
];
/** The text of the call whose opening parenthesis is at `open`, and its top-level arguments. */
function callAt(text, open) {
  let depth = 0, quote = null, tpl = 0;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (quote) { if (c === '\\') { i++; continue; } if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'" || c === '`') { quote = c; continue; }
    if (c === '(') depth++;
    if (c === ')') { depth--; if (depth === 0) return { text: text.slice(open, i + 1), args: splitTop(text.slice(open + 1, i)) }; }
  }
  throw new Error('unbalanced call');
}
const sites = [];   // { name, file, line, call, args }
for (const file of sources) {
  const text = readFileSync(file, 'utf8');
  const lineOf = (index) => text.slice(0, index).split('\n').length;
  const direct = new Set();
  for (const m of text.matchAll(/\b(?:rpc|sendRequest)\s*(?:<[^()]*?>)?\s*\(\s*(['"`])([A-Za-z0-9_]+)\1/g)) {
    const open = text.indexOf('(', m.index + m[0].indexOf(m[2]) - 40 < m.index ? m.index : m.index);
    const callOpen = text.indexOf('(', m.index + (m[0].startsWith('rpc') || m[0].startsWith('sendRequest') ? 0 : 0));
    const parenAt = text.indexOf('(', m.index + m[0].search(/\(/));
    const call = callAt(text, parenAt);
    sites.push({ name: m[2], file: relative(root, file), line: lineOf(m.index), call: call.text, args: call.args.slice(1) });
    direct.add(m.index + m[0].indexOf(m[2]));
    void open; void callOpen;
  }
  // A loader whose name is held in a variable (the rules editor picks loadBpaRules or loadReadinessRules once, then calls rpc(loadOp, ...)).
  for (const m of text.matchAll(/\brpc\s*\(\s*loadOp\s*,/g)) {
    const call = callAt(text, text.indexOf('(', m.index));
    for (const name of ['loadBpaRules', 'loadReadinessRules']) sites.push({ name, file: relative(root, file), line: lineOf(m.index), call: call.text, args: call.args.slice(1) });
  }
  // Any other place a setter's name is written as a string (a helper that forwards it): it is a call this scan cannot read, so it must be allowed by name.
  for (const m of text.matchAll(/(['"`])([a-z][A-Za-z0-9]+)\1/g)) {
    const name = m[2];
    if (!engine.has(name) || !SETTER.test(name) || direct.has(m.index + 1)) continue;
    const lineStart = text.lastIndexOf('\n', m.index) + 1;
    const lineText = text.slice(lineStart, text.indexOf('\n', m.index));
    if (/^\s*(\/\/|\*|\/\*)/.test(lineText)) continue;
    if (/\b(rpc|sendRequest)\s*(<[^()]*>)?\s*\(\s*['"`]/.test(lineText) && direct.has(lineStart)) continue;
    if (sites.some((s) => s.file === relative(root, file) && s.name === name && Math.abs(s.line - lineOf(m.index)) <= 3)) continue;
    sites.push({ name, file: relative(root, file), line: lineOf(m.index), call: lineText.trim(), args: null, indirect: true });
  }
}

// ---- 3. the setters, and why each is fenced or allowed ----------------------------------------------------------
// Each reason says why nothing the person typed can be written over by this call. Shared wording is one constant, not one copy per entry.
const CHOICE = 'a one-step choice or switch the person makes from a list or a control (pick, tick, press); no typed text is held, so no draft can be written over';
const GOVERNANCE = 'a person-only control on the assistant\'s own governance (plan-heart contract, section 12): the assistant cannot write it, so no other writer can have changed it under the person';
const REVIEWED_BATCH = 'one press that runs a batch the person reviewed in this view; no typed draft is held, and the batch is judged against the model as it runs';
const TWO_STEP = 'two steps: the first call only previews (nothing is written), and the second writes only what that preview showed the person';
const GRID = 'the Properties grid write, built in src/propSet.ts (gridSetCalls) so it can be tested without VS Code: it passes the value the words were typed over, or each object\'s own (expectedAll), and the model; test/drafts-sol-host.test.mjs holds every route';
const ALLOW = [
  // ---- choices and switches (CHOICE) ----
  ...['setColumnMetadata', 'setColumnObjectPermission', 'setTableObjectPermission', 'setRolePermission', 'setRoleMember', 'setPerspectiveMember', 'setRelationship',
    'setRelationshipCardinality', 'setCompatibilityLevel', 'setDefaultAccountProfile', 'setPublishDestination', 'setModelSqlEndpoint', 'setReportScope',
    'setWorkflowActivation', 'setWorkflowBinding', 'setWorkflowEnabled', 'setWorkflowEnforcement'].map((setter) => ({ setter, reason: CHOICE })),
  // ---- person-only governance ----
  ...['setAgentPolicyCell', 'setAgentPolicyEnabled', 'setAgentPolicyPreset', 'setAssistantQueue', 'setPlanItemDoer', 'setPlanRepeat', 'setPlanTaskNote'].map((setter) => ({ setter, reason: GOVERNANCE })),
  // ---- reviewed batches and previews ----
  ...['applyPlan', 'applyPlanItems', 'applySafeFixes', 'applySchemaUpdate'].map((setter) => ({ setter, reason: REVIEWED_BATCH })),
  { setter: 'applyFix', reason: 'runs one rule\'s fix on the object a scan found it on; the fix is worked out from the live object when it runs, and no text is typed' },
  { setter: 'applyDiff', reason: TWO_STEP + ' (the second carries the preview\'s confirm token)' },
  { setter: 'replaceInObject', reason: TWO_STEP + ' (the apply sends the same request the preview answered)' },
  { setter: 'applyLicense', reason: 'a licence the person pasted or chose to apply; it replaces the old licence on purpose and is verified before it is stored, with no draft of the old one held' },
  { setter: 'applyDaxScript', reason: 'each block carries the @base hash of the object it was scripted from and the engine compares it inside its write; the session id pins the model (Sol join 6, C1)' },
  { setter: 'applyTmdlScript', reason: 'each block carries the @base hash of the object it was scripted from and the engine compares it inside its write; the session id pins the model (Sol join 6, C1)' },
  // ---- documents the composer, the start plan and files own ----
  { setter: 'editDraft', reason: 'the composer draft has its own Keep yours / Apply banner and revision (composer-draft.tsx); these calls are its steps, and open_in_author reads' },
  { setter: 'editStartPlan', reason: 'a one-line operation (pick, rename, answer) on a start plan that is read and approved before anything is built; nothing is written to the model by an edit' },
  { setter: 'loadSpec', reason: 'replaces the draft with a file the person chose in a dialog; the whole replacement is the request, and no text of the old draft is being edited' },
  { setter: 'saveSpec', reason: 'writes the draft to a path chosen in a save dialog; overwriting that file is the dialog\'s question and the engine holds a path it has not been allowed to write' },
  { setter: 'saveEvidence', reason: 'writes a new evidence file into the project from a finished run; it replaces no text the person edited' },
  { setter: 'setStudioDriver', reason: 'reports who has the wheel (I drive or Claude drives, and whether the person just touched the screen) to the engine; it is a live status, not a value anyone typed or saved, and the next report replaces it' },
  { setter: 'saveLayout', reason: 'diagram box positions, saved best-effort after a drag; no text, and the next layout read wins' },
  { setter: 'saveWorkflow', site: /'human', true/, reason: 'createOnly is true: the engine refuses to replace a workflow that already exists, so nothing can be written over' },
  { setter: 'saveTest', site: /enabled: !def\.enabled/, reason: 'the enable switch: it writes the definition read a moment before with only the switch changed, and no typed text is held' },
  // ---- one call site of an otherwise fenced setter ----
  { setter: 'updateDataAgent', site: /config\.datasourceJson/, reason: 'applies the generated source the person is looking at (its elements are listed above the button) into a draft folder, after a review step; it is built from the open model when generated and no stored copy is edited' },
  { setter: 'renameObject', site: /'renameObject', n\.ref, newName, 'human'\)/, reason: 'a rename from the tree names the object by its ref; a ref that moved no longer resolves, so the rename misses instead of landing on another object' },
  { setter: 'renameDisplayFolder', reason: 'renames a folder by the old path the person picked, and the engine moves the members that are in it now; no text of a draft is replaced' },
  { setter: 'setDisplayFolder', site: /'setDisplayFolder', (movable\.map|\[newRef\])/, reason: 'a one-step move command (the person picks or types the folder in an input box); no editor draft is held, and a move names the objects, not a value' },
  { setter: 'setMeasureFormat', site: /'setMeasureFormat', n\.ref, /, reason: 'a quick pick of a format from a list the person chose from in one step; no editor draft is held' },
  { setter: 'setMeasureFormatExpression', site: /pick\.tpl\.formatString/, reason: 'a quick pick of a dynamic-format template from a list, chosen in one step; no editor draft is held' },
  { setter: 'setObjectProperties', site: /'IsHidden', String\(hidden\)/, reason: 'the Hide / Show command on the selection: a switch pressed in one step, nothing typed' },
  // ---- the Properties grid (drafts-sol): its calls moved out of extension.ts into src/propSet.ts, where this scan reads a method name held in data and cannot read the arguments ----
  ...['setMeasureFormatExpression', 'setColumnDataType', 'setObjectProperty', 'setObjectProperties'].map((setter) => ({ setter, site: new RegExp("method: '" + setter + "'"), reason: GRID })),
  { setter: 'setPlanItem', site: /'setPlanItem', \w+, null,/, reason: 'a tick or a drop passes no new value (the value argument is null), so there is no typed text to write over' },
  { setter: 'loadBpaRules', site: /JSON\.stringify\(remaining\), true\)/, reason: 'removing one rule: the set is read from the engine just before and written back without that rule, so no typed text is replaced' },
  { setter: 'loadReadinessRules', site: /JSON\.stringify\(remaining\), true\)/, reason: 'removing one rule: the set is read from the engine just before and written back without that rule, so no typed text is replaced' },
  { setter: 'loadBpaRules', site: /const loadOp/, reason: 'the name is chosen once in the rules panel (BPA or readiness) and the calls through it are judged on their own call sites' },
  { setter: 'loadReadinessRules', site: /const loadOp/, reason: 'the name is chosen once in the rules panel (BPA or readiness) and the calls through it are judged on their own call sites' },
];

const setters = [...new Set(sites.filter((s) => engine.has(s.name) && SETTER.test(s.name)).map((s) => s.name))].sort();
if (process.env.SHOW_SETTERS) console.log(setters.map((n) => `${n} [${engine.get(n).join(', ')}] x${sites.filter((s) => s.name === n).length}`).join('\n'));

function allowedBy(site) {
  return ALLOW.find((a) => a.setter === site.name && (!a.site || a.site.test(site.call)));
}
const fenced = new Set(), allowedWhole = new Set(), problems = [];
for (const name of setters) {
  const own = sites.filter((s) => s.name === name);
  const params = engine.get(name);
  let fenceIndex = -1;
  params.forEach((p, i) => { if (FENCE_PARAM.test(p)) fenceIndex = i; });
  const unmatched = own.filter((s) => !allowedBy(s));
  if (unmatched.length === 0) { allowedWhole.add(name); continue; }
  if (fenceIndex < 0) { problems.push(`${name}: no expected* parameter on the engine setter, and ${unmatched.length} call(s) are not allowed: ${unmatched.map((s) => `${s.file}:${s.line}`).join(', ')}`); continue; }
  let ok = true;
  for (const s of unmatched) {
    if (s.indirect) { problems.push(`${name}: ${s.file}:${s.line} forwards the name through a helper this scan cannot read; allow it by name with a reason`); ok = false; continue; }
    const arg = s.args[fenceIndex];
    if (arg === undefined || /^(null|undefined)$/.test(arg.trim())) {
      problems.push(`${name}: ${s.file}:${s.line} passes no starting value at argument ${fenceIndex + 1} (${params[fenceIndex]}). Call: ${s.call.replace(/\s+/g, ' ').slice(0, 140)}`);
      ok = false;
    }
  }
  if (ok) fenced.add(name);
}
// A stale allowance names a call that is gone, and an allowance with a thin reason is not a reason.
for (const a of ALLOW) {
  if (!sites.some((s) => s.name === a.setter && (!a.site || a.site.test(s.call)))) problems.push(`stale allowance: ${a.setter}${a.site ? ' ' + a.site : ''} matches no call`);
  if (typeof a.reason !== 'string' || a.reason.trim().length < 30) problems.push(`allowance for ${a.setter} has no real reason`);
  if (!engine.has(a.setter)) problems.push(`allowance for ${a.setter}: the engine has no such method`);
}

assert.deepEqual(problems, [], 'every setter the webview or extension calls is fenced or allowed with a reason:\n  ' + problems.join('\n  '));
const partial = [...fenced].filter((n) => sites.some((s) => s.name === n && allowedBy(s)));
console.log(`save class: ${setters.length} setters called from the webview and extension; ${fenced.size} fenced (${partial.length} of them with some call sites allowed: ${partial.join(', ')}); ${allowedWhole.size} allowed with a reason; ${ALLOW.length} allowances in all.`);
