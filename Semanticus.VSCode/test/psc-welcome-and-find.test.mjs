// Plan statement check 2, items 5 and 6 (lane m11/psc-plan). Each was red before the change.
//   5  An empty Semanticus panel (no model open) shows a welcome with three starts: From a SQL source, New blank model, Open a model.
//      The status bar's "no model" click lands there.
//   6  Find and replace left the Studio (1.5.1): the Plan row link names no search page and hands over no names.
import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const read = (p) => readFileSync(new URL('../' + p, import.meta.url), 'utf8');
const pkg = JSON.parse(read('package.json'));
const extension = read('src/extension.ts');

// ---- 5. the welcome ---------------------------------------------------------------------------------------------------

test('5 the empty Model view has a welcome with the Studio and the three starts, in order, shown only while no model is open', () => {
  const all = (pkg.contributes.viewsWelcome ?? []).filter((w) => w.view === 'semanticusModel');
  // Plus one that shows only while the last model waits for a sign-in (K15): it sits above the three starts.
  const reconnect = all.filter((w) => /semanticus\.reconnect/.test(w.when));
  assert.equal(reconnect.length, 1, 'one Sign in to reconnect welcome');
  // And one for a window with no folder, which offers the last model in one press (round 2 UAT N25).
  const offer = all.filter((w) => /semanticus\.recentOffer/.test(w.when));
  assert.equal(offer.length, 1, 'one Open your last model welcome');
  assert.match(offer[0].contents, /\[Open your last model\]\(command:semanticus\.signInToReconnect\)/);
  assert.match(reconnect[0].contents, /\[Sign in to reconnect\]\(command:semanticus\.signInToReconnect\)/);
  assert.ok(extension.includes("registerPersonCommand('semanticus.signInToReconnect'"), 'and its command is registered');
  const welcomes = all.filter((w) => !/semanticus\.reconnect|semanticus\.recentOffer/.test(w.when));
  assert.equal(welcomes.length, 1, 'one welcome for the Model view');
  const w = welcomes[0];
  assert.equal(w.when, '!semanticus.hasModel');
  const labels = [...w.contents.matchAll(/\[([^\]]+)\]\(command:([A-Za-z.]+)\)/g)].map((m) => [m[1], m[2]]);
  assert.deepEqual(labels, [
    ['Open Studio', 'semanticus.openStudio'],   // fix-uat-first-start: the empty Model view leads to the Studio
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

// ---- 6. Find and replace left the Studio (1.5.1) ----------------------------------------------------------------------

test('6 the Plan row link is the one way to the page it names, with no names handed over, and nothing opens Find and replace', () => {
  const row = read('webview/src/plan-row.tsx');
  assert.equal((row.match(/onGo\(item\.jump!\.tool\)/g) ?? []).length, 1, 'the row link');
  assert.ok(!existsSync(join(root, 'webview/src/search.tsx')), 'the page is gone');
  assert.ok(!existsSync(join(root, 'webview/src/search-names.ts')), 'the names hand-off is gone');
  assert.ok(!/SearchView|namesOfTarget|findBoxFor|jumpTarget/.test(read('webview/src/App.tsx') + read('webview/src/plan.tsx') + row));
  // The names still travel to the assistant, in get_model_plan.
  assert.match(read('webview/src/plan-model.ts'), /interface PlanJump \{ tool: string; label: string; find\?: string\[\] \| null \}/);
});
