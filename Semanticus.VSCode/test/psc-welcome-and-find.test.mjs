// Plan statement check 2, items 5 and 6 (lane m11/psc-plan). Each was red before the change.
//   5  An empty Semanticus panel (no model open) shows a welcome with three starts: From a SQL source, New blank model, Open a model.
//      The status bar's "no model" click lands there.
//   6  "Fix N names that Copilot reads as the same word" opens Find and replace with the clashing names filled in, not empty.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

const read = (p) => readFileSync(new URL('../' + p, import.meta.url), 'utf8');
const pkg = JSON.parse(read('package.json'));
const extension = read('src/extension.ts');

// ---- 5. the welcome ---------------------------------------------------------------------------------------------------

test('5 the empty Model view has a welcome with the three starts, in order, shown only while no model is open', () => {
  const all = (pkg.contributes.viewsWelcome ?? []).filter((w) => w.view === 'semanticusModel');
  // Plus one that shows only while the last model waits for a sign-in (K15): it sits above the three starts.
  const reconnect = all.filter((w) => /semanticus\.reconnect/.test(w.when));
  assert.equal(reconnect.length, 1, 'one Sign in to reconnect welcome');
  assert.match(reconnect[0].contents, /\[Sign in to reconnect\]\(command:semanticus\.signInToReconnect\)/);
  assert.ok(extension.includes("registerPersonCommand('semanticus.signInToReconnect'"), 'and its command is registered');
  const welcomes = all.filter((w) => !/semanticus\.reconnect/.test(w.when));
  assert.equal(welcomes.length, 1, 'one welcome for the Model view');
  const w = welcomes[0];
  assert.equal(w.when, '!semanticus.hasModel');
  const labels = [...w.contents.matchAll(/\[([^\]]+)\]\(command:([A-Za-z.]+)\)/g)].map((m) => [m[1], m[2]]);
  assert.deepEqual(labels, [
    ['From a SQL source', 'semanticus.draftFromSqlSource'],
    ['New blank model', 'semanticus.newBlankModel'],
    ['Open a model', 'semanticus.openModel'],
  ]);
});

test('5 every command the welcome runs is declared in the package and registered by the extension', () => {
  const declared = new Set(pkg.contributes.commands.map((c) => c.command));
  for (const id of ['semanticus.draftFromSqlSource', 'semanticus.newBlankModel', 'semanticus.openModel']) {
    assert.ok(declared.has(id), `${id} is declared in package.json`);
    assert.ok(extension.includes(`registerPersonCommand('${id}'`), `${id} is registered`);
  }
});

test('5 the context key follows the session: set from every SessionInfo, and cleared when a reopen finds no model', () => {
  assert.match(extension, /function setHasModelContext\(has: boolean\)[\s\S]{0,200}'setContext', 'semanticus\.hasModel', has/);
  assert.match(extension, /function setStatusFromInfo\(info\?: SessionInfo\): void \{[\s\S]{0,1200}setHasModelContext\(!!info\?\.sessionId\)/);
  assert.ok((extension.match(/setHasModelContext\(false\)/g) ?? []).length >= 2, 'both "no model" paths of the cold reopen clear it');
});

test('5 the status bar\'s "no model" click lands on the Model view, which is where the welcome is', () => {
  assert.match(extension, /status\.command = 'semanticusModel\.focus'/);
  assert.ok(pkg.contributes.views.semanticus.some((v) => v.id === 'semanticusModel'));
});

test('5 New blank model opens the Connections hub on its create form, on every door that names a hub section', () => {
  assert.match(extension, /registerPersonCommand\('semanticus\.newBlankModel', \(\) => void openConnectionsManager\('create'\)\)/);
  assert.match(extension, /openConnectionsManager\(section: [^)]*'create'/);
  assert.match(read('webview/src/connectionshub.tsx'), /export type HubView = [^\n]*'create'/);
  assert.match(read('webview/src/connectionshub.tsx'), /if \(section === 'create'\) openCreate\(\)/);
  assert.match(read('webview/src/connectionshub.tsx'), /if \(nextView === 'create'\) openCreate\(\)/);
  assert.match(read('webview/src/App.tsx'), /section === 'createsql' \|\| section === 'create'\) openConnectionsOnView\(section\)/);
  assert.match(read('webview/src/main.tsx'), /'createsql', 'create'\] as const/);
});

test('5 the welcome follows the product copy style: plain words, no em dash, never "one click"', () => {
  const w = pkg.contributes.viewsWelcome.find((x) => x.view === 'semanticusModel').contents;
  assert.ok(!w.includes('—'), 'no em dash');
  assert.ok(!/one[- ]click|single[- ]click/i.test(w), 'never "one click"');
  assert.ok(!/\b(engine|session|revision)\b/i.test(w), 'no engine vocabulary');
});

// ---- 6. Find and replace opens with the names ------------------------------------------------------------------------

const load = makeLoader({}, req('react'));
const { jumpTarget, namesOfTarget, findBoxFor } = load('search-names.ts');

test('6 a collisions jump becomes a target that carries every name, and only a search jump with names does', () => {
  const names = ['Group1Key', 'Group1 Key', 'PRAC_CD'];
  const target = jumpTarget({ tool: 'search', label: 'Find and replace', find: names });
  assert.equal(typeof target, 'string');
  assert.deepEqual(namesOfTarget(target), names);
  assert.equal(jumpTarget({ tool: 'search', label: 'Find and replace' }), undefined, 'no names, the page opens as it always did');
  assert.equal(jumpTarget({ tool: 'search', find: [] }), undefined);
  assert.equal(jumpTarget({ tool: 'bpa', find: names }), undefined, 'names mean nothing to another page');
  assert.equal(jumpTarget(null), undefined);
});

test('6 a target that is not ours is left alone (an object ref, a typed word, damaged JSON)', () => {
  assert.equal(namesOfTarget('table:Sales'), null);
  assert.equal(namesOfTarget('Sales'), null);
  assert.equal(namesOfTarget('find:not json'), null);
  assert.equal(namesOfTarget('find:{"a":1}'), null);
  assert.equal(namesOfTarget('find:[]'), null);
  assert.equal(namesOfTarget(undefined), null);
});

test('6 one name is plain text in the Find box; several are one pattern that matches each name whole, safe to read as a pattern', () => {
  assert.deepEqual(findBoxFor(['Amount']), { text: 'Amount', regex: false });
  const many = findBoxFor(['Profit', 'Profit %', 'Net (after tax)', 'a.b', 'x|y']);
  assert.equal(many.regex, true);
  const re = new RegExp(many.text);
  for (const n of ['Profit', 'Profit %', 'Net (after tax)', 'a.b', 'x|y']) assert.ok(re.test(n), n);
  assert.ok(!re.test('aXb'), 'a dot in a name is a dot, not "any character"');
  assert.ok(!re.test('x'), 'a bar in a name is a bar, not "or"');
  assert.ok(!re.test('Margin'));
});

test('6 the Plan\'s row link passes the names, Studio turns them into a search seed, and the page reads them', () => {
  // Round 1 (plan-web): the Choose button is gone, the row link is the one way to the page and it passes the names.
  const row = read('webview/src/plan-row.tsx');
  assert.equal((row.match(/onGo\(item\.jump!\.tool, jumpTarget\(item\.jump\)\)/g) ?? []).length, 1, 'the row link');
  const app = read('webview/src/App.tsx');
  assert.match(app, /tool === 'search' && namesOfTarget\(target\)\) return \{ kind: 'search', value: namesOfTarget\(target\)! \}/);
  assert.match(app, /Array\.isArray\(seed\.value\)\s*\? \{ query: findBoxFor\(seed\.value\)\.text, regex: findBoxFor\(seed\.value\)\.regex/);
  const search = read('webview/src/search.tsx');
  assert.match(search, /navQuery\?: \{ query: string; scope\?: string; regex\?: boolean; nonce: number \}/);
  assert.match(search, /if \(navQuery\.regex != null && navQuery\.query\) \{ setRegex\(navQuery\.regex\)/);
  assert.match(read('webview/src/plan-model.ts'), /interface PlanJump \{ tool: string; label: string; find\?: string\[\] \| null \}/);
});
