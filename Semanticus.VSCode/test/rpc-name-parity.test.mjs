// Every rpc() name the webview says must be a method the engine answers to.
//
// Kane's Yoga build, 2026-09-15: the Tests page came up with "No method by the name 'saveSqlSource' is
// found". That one was a stale engine binary, but nothing in our gates could have caught a REAL name
// mismatch either, because the uishot harness fakes every rpc call and answers whatever it is asked. A
// typed name, a renamed engine method, a method that only ever existed in a lane report: all three ship
// green today and fail in front of Kane.
//
// So this reads the two JSON-RPC targets out of the C# source and checks every literal name against them.
// StreamJsonRpc maps a request to a method by name with the Async suffix dropped, so both spellings count.
// It cannot check argument counts or types; it checks the thing that actually broke.
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const repo = resolve(root, '..');

// ---- the engine's side of the wire ----------------------------------------------------------------
const TARGETS = ['Semanticus.Engine/EngineRpcTarget.cs', 'Semanticus.Engine/HumanGovernanceRpcTarget.cs'];
const CLASS = /\b(?:sealed\s+|partial\s+|static\s+)*class\s+(\w+)/g;
// public [async] <return type> name(   — the return type may carry generics, arrays, nullables and dots.
const METHOD = /^[ \t]*(?:public|internal)\s+(?:async\s+)?(?:override\s+)?[\w<>[\],.\s?]+?\s+(\w+)\s*\(/gm;

const engineMethods = new Set();
const classNames = new Set();
for (const file of TARGETS) {
  const source = readFileSync(resolve(repo, file), 'utf8');
  for (const match of source.matchAll(CLASS)) classNames.add(match[1]);
  for (const match of source.matchAll(METHOD)) engineMethods.add(match[1]);
}
for (const name of classNames) engineMethods.delete(name);   // a constructor is not a method
assert.ok(engineMethods.size > 200,
  `the RPC targets should expose hundreds of methods; parsed ${engineMethods.size}, so the parse is wrong, not the callers`);
assert.ok(engineMethods.has('listSqlSources'), 'sanity: a known engine method must be found by the parse');
assert.ok(engineMethods.has('saveSqlSource'), 'sanity: the method Kane’s build could not find must be found here');

// StreamJsonRpc drops a trailing Async when it maps a name, so a method declared either way answers both.
const answersTo = (name) => engineMethods.has(name) || engineMethods.has(`${name}Async`)
  || (name.endsWith('Async') && engineMethods.has(name.slice(0, -'Async'.length)));

// ---- the webview's side of the wire ---------------------------------------------------------------
function sources(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) { out.push(...sources(path)); continue; }
    if (/\.tsx?$/.test(entry)) out.push(path);
  }
  return out;
}

// rpc('name', ...) and rpc<Shape>('name', ...), across a line break after the parenthesis.
const CALL = /\brpc\s*(?:<[^()]*?>)?\s*\(\s*(['"])([^'"]+)\1/g;
// A call whose first argument is NOT a literal: reported, never silently passed over.
const DYNAMIC = /\brpc\s*(?:<[^()]*?>)?\s*\(\s*(?!['"])[A-Za-z_$]/g;

const found = new Map();      // name -> [where]
const dynamic = [];
for (const file of sources(resolve(root, 'webview/src'))) {
  const text = readFileSync(file, 'utf8');
  const where = relative(root, file);
  for (const match of text.matchAll(CALL)) {
    const line = text.slice(0, match.index).split('\n').length;
    const at = found.get(match[2]) ?? [];
    at.push(`${where}:${line}`);
    found.set(match[2], at);
  }
  for (const match of text.matchAll(DYNAMIC)) {
    const line = text.slice(0, match.index).split('\n').length;
    dynamic.push(`${where}:${line}`);
  }
}

assert.ok(found.size > 50, `expected the webview to call many engine methods; extracted ${found.size}`);
assert.ok(found.has('listTests'), 'sanity: a known call site must be extracted');

// ---- names a sibling lane is building RIGHT NOW ---------------------------------------------------
// One narrow, self-deleting exception, and it exists for exactly one reason: this repo is being changed by
// two lanes at once. cp/pro-page moved Connections onto the new free list_sql_source_usage projection, and
// cp/pro-engine is adding the engine method in parallel. Until both land on main, the page half alone would
// turn this whole gate red and hide every real mismatch behind one known one.
//
// It cannot rot. The assertion below REQUIRES the name to still be absent from the engine, so the day the
// engine lane lands the method this file fails and whoever is holding it deletes these lines. A pending
// name is also NOT proof the call works: on this branch, that call really would fail against a live engine.
const PENDING_ENGINE_METHODS = new Map([
  // (emptied 2026-10-02: the start plan's seven verbs answer on EngineRpcTarget and HumanGovernanceRpcTarget since the
  //  M11 wiring step, so the start plan page calls the real names and this test checks them like any other.)
  // (emptied 2026-09-15: listSqlSourceUsage landed on EngineRpcTarget with the Pro engine half)
  // (emptied 2026-09-21: the Jev M2 exception did its job. getModelPlan, skipModelPlanItem,
  //  unskipModelPlanItem and modelPlanAction all answer on EngineRpcTarget now, so the Plan page calls
  //  the real names and this test checks them like any other.)
  // (emptied 2026-09-24: the slice C exception did its job. listWorkspaceModels answers on EngineRpcTarget since
  //  C1 applied lane C3's engine patch, so the workspace picker calls the real name like any other.)
]);
for (const [name, why] of PENDING_ENGINE_METHODS) {
  assert.ok(!answersTo(name),
    `${name} now EXISTS on the engine (${why}). Delete it from PENDING_ENGINE_METHODS: the exception has done its job.`);
}

const missing = [...found.entries()]
  .filter(([name]) => !answersTo(name) && !PENDING_ENGINE_METHODS.has(name))
  .map(([name, at]) => `  ${name}  (called from ${at.join(', ')})`);

assert.equal(missing.length, 0,
  `these rpc names have no method on EngineRpcTarget or HumanGovernanceRpcTarget, so the call fails at\n`
  + `runtime with "No method by the name '<name>' is found":\n${missing.join('\n')}\n`
  + 'Fix the caller, not this test.');

// A dynamic name cannot be checked here. Say so out loud, so nobody reads a green run as complete cover.
console.log(`rpc name parity: ${found.size} distinct names checked against ${engineMethods.size} engine methods`
  + `${dynamic.length ? `; ${dynamic.length} call(s) pass a variable name and were NOT checked: ${dynamic.join(', ')}` : '; no dynamic call sites'}`
  + `${PENDING_ENGINE_METHODS.size ? `; ${PENDING_ENGINE_METHODS.size} name(s) are WAITING on a sibling lane and are NOT proven: ${[...PENDING_ENGINE_METHODS.keys()].join(', ')}` : ''}`);

// ---- both doors, for the seven typed-decision operations (F-176) ----------------------------------
//
// The gate above proves a name the webview says is a name the engine answers to. It says nothing about a
// name the webview NEVER says, and that is the hole F-176 fell through: all seven decision operations
// shipped on the RPC target and on MCP, and a search of Semanticus.VSCode found no caller, no handler and
// no view for any of them, so a person using VS Code could not give permission at all. An operation with
// one door is half an operation (CLAUDE.md, golden rule 2).
//
// So these seven are checked from the OTHER direction: each must answer on the RPC target, be registered
// as an MCP tool, and be called by name from the webview source. The MCP name is derived from the RPC one
// rather than written down twice, so a rename cannot leave this list agreeing with itself and with
// nothing else.
const DECISION_OPS = [
  'getDecisionPoints', 'setDecisionPointEnabled', 'getConsent', 'setConsent',
  'withdrawConsent', 'getDecisionBudget', 'proposeMeasureDefaults',
];
const snake = (name) => name.replace(/[A-Z]/g, (c) => '_' + c.toLowerCase());

const mcpSource = readFileSync(resolve(repo, 'Semanticus.Engine/McpTools.Decisions.cs'), 'utf8');
const mcpTools = new Set([...mcpSource.matchAll(/McpServerTool\(Name\s*=\s*"([^"]+)"/g)].map((m) => m[1]));
assert.ok(mcpTools.size >= DECISION_OPS.length,
  `McpTools.Decisions.cs should register at least ${DECISION_OPS.length} tools; parsed ${mcpTools.size}, so the parse is wrong`);

// The extension host is a door too, and for one of these it is the ONLY honest one.
//
// propose_measure_defaults fires about a measure, and a measure is not made in the Studio: the Model
// tree's New Measure gesture makes it and the DAX editor saves it, both in the extension host. So the
// caller search covers webview/src AND src, and this file no longer carries an exception for it. The
// behaviour of that caller is executed in test/measure-defaults-door.test.mjs; what is checked here is
// only that a caller exists at all, which is the thing F-176 found missing.
const hostSources = sources(resolve(root, 'src'));
const hostCalls = new Map();   // name -> [where]
for (const file of hostSources) {
  const text = readFileSync(file, 'utf8');
  const where = relative(root, file);
  // sendRequest('name', ...) is the host's spelling of the same wire call rpc('name', ...) makes.
  for (const match of text.matchAll(/\bsendRequest\s*(?:<[^()]*?>)?\s*\(\s*(['"])([^'"]+)\1/g)) {
    const line = text.slice(0, match.index).split('\n').length;
    const at = hostCalls.get(match[2]) ?? [];
    at.push(`${where}:${line}`);
    hostCalls.set(match[2], at);
  }
}
assert.ok(hostCalls.size > 20, `expected the extension host to call many engine methods; extracted ${hostCalls.size}`);
assert.ok(hostCalls.has('getEntitlement'), 'sanity: a known host call must be extracted');

const oneDoored = [];
for (const op of DECISION_OPS) {
  if (!answersTo(op)) oneDoored.push(`  ${op}: no method on the RPC target`);
  if (!mcpTools.has(snake(op))) oneDoored.push(`  ${op}: no MCP tool named ${snake(op)}`);
  if (!found.has(op) && !hostCalls.has(op)) {
    oneDoored.push(`  ${op}: nothing in webview/src or src calls it, so no person can reach it`);
  }
}
assert.equal(oneDoored.length, 0,
  'these typed-decision operations do not ship through both doors, which is what F-176 found:\n'
  + oneDoored.join('\n')
  + '\nEvery capability ships twice, on one session. Add the missing half, do not relax this test.');

const viaWebview = DECISION_OPS.filter((op) => found.has(op));
const viaHost = DECISION_OPS.filter((op) => !found.has(op) && hostCalls.has(op));
console.log(`decision doors: ${DECISION_OPS.length} operations on the RPC target and on MCP; `
  + `${viaWebview.length} called from webview/src, ${viaHost.length} from the extension host`
  + `${viaHost.length ? ` (${viaHost.map((op) => `${op} at ${hostCalls.get(op).join(', ')}`).join('; ')})` : ''}`);
