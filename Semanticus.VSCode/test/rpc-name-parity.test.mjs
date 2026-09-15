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
  // (emptied 2026-09-15: listSqlSourceUsage landed on EngineRpcTarget with the Pro engine half)
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
