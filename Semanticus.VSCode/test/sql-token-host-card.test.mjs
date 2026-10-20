// A check names a server the person has not saved (a teammate's suite file, a cloned repo). The engine sends no sign-in token and runs
// nothing, so the card must say so in plain words and offer ONE press that opens the source form with the host filled in (no dead end).
// The card also shows where the check's SQL goes, as one quiet line. The line is a pure rule and runs here; the wiring is pinned by source.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

async function loadTypeScriptModule(file) {
  const output = ts.transpileModule(readFileSync(file, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output, 'utf8').toString('base64')}`);
}

const model = await loadTypeScriptModule(resolve(root, 'webview/src/tests-model.ts'));
const page = readFileSync(resolve(root, 'webview/src/tests.tsx'), 'utf8');
const authoring = readFileSync(resolve(root, 'webview/src/tests-authoring.tsx'), 'utf8');
const addSource = readFileSync(resolve(root, 'webview/src/tests-addsource.tsx'), 'utf8');

// 1. The quiet line says the server and the database, as written, and says nothing when no server is known.
assert.equal(model.runsAgainstLine('contoso-sql.database.windows.net', 'Warehouse'), 'Runs against contoso-sql.database.windows.net, database Warehouse.');
assert.equal(model.runsAgainstLine('  contoso-sql.database.windows.net ', ''), 'Runs against contoso-sql.database.windows.net.');
assert.equal(model.runsAgainstLine(undefined, 'Warehouse'), null);
assert.equal(model.runsAgainstLine('', ''), null);

// 2. The card: an outcome with an unsaved server shows the engine's words and a "Choose a source" press that carries the host.
assert.match(page, /outcome\.unsavedServer/);
assert.match(page, /data-testid="check-unsaved-server"/);
assert.match(page, /action\('Choose a source', \(\) => props\.onEdit\(def, undefined, outcome\.unsavedServer\)/);
// The press reaches the drawer as the host, and the drawer opens the add-a-source form with it.
assert.match(page, /openAuthor\(def\.kind === 'measureValue' \? 'measureValue' : 'measureReconcile', def, focus, false, host\)/);
assert.match(page, /addSourceHost=\{authorHost\}/);
assert.match(authoring, /<SqlSourceAddForm initialServer=\{addSourceHost\}/);
assert.match(addSource, /useState\(initialServer \?\? ''\)/);

// 3. The card shows where the check goes, for a check that reads a source (never for a trusted answer).
assert.match(page, /data-testid="check-runs-against"/);
assert.match(page, /isTrustedAnswer \? null : runsAgainstLine\(outcome\?\.server \?\? reconcile\?\.server, outcome\?\.database \?\? reconcile\?\.database\)/);

// 4. The form still takes a typed server and saves it (the Northstar Source flow): the host prefill only seeds the box.
assert.match(addSource, /saveSqlSource', savedId, name\.trim\(\), server\.trim\(\)/);
console.log('sql-token-host-card: ok');
