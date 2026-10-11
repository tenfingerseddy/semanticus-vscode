// "Create a measure" has to open the real New Measure path, and this file is the extension half of that.
//
// WHAT WENT WRONG. Kane typed "create a measure" into the ask box on 21 September and the box sent him to
// DAX Lab, which runs queries and cannot make a measure. The Studio has no measure form and never did.
// The product does have a create-a-measure path: the Model tree's own New Measure command. So the box now
// asks the host to run that command. Two things had to be true for that to work and neither was:
//
//   1. runWebviewHostCommand refused every create command. newMeasure was allowed only WITH a table ref
//      (the tree's own right-click hands one over), and the other four were not in the allowlist at all,
//      so a press from the Overview did nothing and said nothing.
//   2. The handler has to land somewhere sensible with nothing selected. It already does, and that is
//      asserted here by RUNNING it rather than by reading it: the Ctrl+Alt+N keybinding passes no node
//      either, so the picker has been there since that gesture shipped.
//
// HOW THIS TESTS IT. The compiled out/extension.js is the real code, so the two functions are sliced out
// of it and EXECUTED with the host seams stubbed. That is weaker than an Extension Development Host and
// much stronger than a regex over the source: a picker that stopped being shown, an allowlist that lost an
// id, or a handler that put the measure on the wrong table all fail here.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const req = createRequire(join(root, 'package.json'));
const compiledPath = join(root, 'out', 'extension.js');
assert.ok(existsSync(compiledPath), 'out/extension.js is missing. Run npm run compile first.');
const compiled = readFileSync(compiledPath, 'utf8');
const extensionSrc = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
const askSrc = readFileSync(join(root, 'webview', 'src', 'askbox.tsx'), 'utf8');
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

/**
 * The text of one top-level declaration: a `const` up to the semicolon that ends it, or a `function` up
 * to the brace that closes its body.
 *
 * Brackets inside quoted strings and comments do not count, which is the whole reason this is a scanner
 * and not a regular expression. Template literals ARE counted through, because the only braces they
 * carry here are their own `${` and `}`, which balance.
 */
function sliceDeclaration(source, opener) {
  const start = source.indexOf(opener);
  assert.ok(start >= 0, `out/extension.js no longer contains "${opener}". This test slices real code; find where it went.`);
  const open = { '{': 1, '(': 1, '[': 1 };
  const close = { '}': 1, ')': 1, ']': 1 };
  let depth = 0;
  let body = false;   // a function body has been entered, so its closing brace ends the slice
  for (let i = start; i < source.length; i++) {
    const c = source[i];
    if (c === '/' && source[i + 1] === '/') { i = source.indexOf('\n', i); if (i < 0) break; continue; }
    if (c === '/' && source[i + 1] === '*') { i = source.indexOf('*/', i) + 1; continue; }
    if (c === '\'' || c === '"') {
      for (i++; i < source.length; i++) {
        if (source[i] === '\\') { i++; continue; }
        if (source[i] === c) break;
      }
      continue;
    }
    if (open[c]) { depth++; if (c === '{') body = true; continue; }
    if (close[c]) { depth--; if (body && depth === 0) return source.slice(start, i + 1); continue; }
    if (c === ';' && depth === 0) return source.slice(start, i + 1);
  }
  throw new Error(`could not find the end of "${opener}"`);
}

// ===================================================================================================
// 1. The relay. Which commands a webview press is allowed to run, executed against the real allowlists.
// ===================================================================================================
const relayBlock = sliceDeclaration(compiled, 'const WEBVIEW_HOST_COMMANDS')
  + '\n' + sliceDeclaration(compiled, 'const WEBVIEW_CREATE_COMMANDS')
  + '\n' + sliceDeclaration(compiled, 'function runWebviewHostCommand');

const SESSION = 'session-1';
let executed = [];
let editedDax = [];
const relayVscode = { commands: { executeCommand: (...args) => { executed.push(args); return Promise.resolve(); } } };
// eslint-disable-next-line no-new-func
const makeRelay = new Function('vscode', 'currentSessionId', 'refParts', 'DAX_KINDS', 'editDaxCmd',
  relayBlock + '\nreturn runWebviewHostCommand;');
const relay = makeRelay(relayVscode, SESSION,
  (ref) => { const i = ref.indexOf(':'); const rest = ref.slice(i + 1); const s = rest.indexOf('/');
    return s < 0 ? { kind: ref.slice(0, i), name: rest }
      : { kind: ref.slice(0, i), table: rest.slice(0, s), name: rest.slice(s + 1) }; },
  new Set(['measure', 'calcColumn', 'calcitem']),
  (node) => { editedDax.push(node); });

const runRelay = (command, ref, session) => { executed = []; relay(command, ref, session); return executed; };

// The five, each with NO ref and no session: this is exactly what the ask box sends, because the
// Overview has no table selected and never claims to have one.
const TREE_CREATE = ['semanticus.newMeasure', 'semanticus.newCalcColumn', 'semanticus.newCalcTable',
  'semanticus.newCalcItem', 'semanticus.newFunction'];
for (const command of TREE_CREATE) {
  assert.deepEqual(runRelay(command), [[command]],
    `${command} must run from the webview with no ref: the ask box has no table to name, and the handler asks for one`);
}

// The tree's own right-click path is untouched: a ref plus the CURRENT session still runs the command on
// that node, and a ref from a stale session still runs nothing at all.
assert.deepEqual(runRelay('semanticus.newMeasure', 'table:Sales', SESSION),
  [['semanticus.newMeasure', { ref: 'table:Sales', name: 'Sales', kind: 'table', hasChildren: false }]],
  'a webview that names a table still gets the measure on that table');
assert.deepEqual(runRelay('semanticus.newMeasure', 'table:Sales', 'a-stale-session'), [],
  'and a ref from a session that has gone is still refused, not quietly downgraded to the picker');
assert.deepEqual(runRelay('semanticus.newMeasure', 'measure:Sales/Margin', SESSION), [],
  'a measure is not a container for a measure');

// Nothing else got in. The relay is the one place webview content can reach a workbench command.
assert.deepEqual(runRelay('semanticus.deleteObject'), [], 'the allowlist is still an allowlist');
assert.deepEqual(runRelay('semanticus.editDax'), [], 'editDax still needs the ref it edits');
assert.deepEqual(runRelay('semanticus.newHierarchy'), [],
  'New Hierarchy asks for levels in two input boxes, so it is not a one-press answer to a typed question');
assert.equal(editedDax.length, 0);

// ===================================================================================================
// 2. The handler, run for real: no node, no selection, and it still lands on a table.
// ===================================================================================================
const measureBlock = sliceDeclaration(compiled, 'async function authorNewMeasure');
const daxHeader = req('./out/daxHeader.js');

function runAuthorNewMeasure({ selection = [], tables = [] }) {
  const shown = [];
  const messages = [];
  const created = [];
  const vscodeStub = {
    window: {
      showQuickPick: async (items, options) => { shown.push({ items, options }); return items[0]; },
      showInformationMessage: (text) => { messages.push(text); },
      showWarningMessage: (text) => { messages.push(text); },
      showErrorMessage: (text) => { messages.push(text); },
    },
  };
  const conn = { sendRequest: async (method) => (method === 'listTree' ? tables : []) };
  // eslint-disable-next-line no-new-func
  const make = new Function('vscode', 'treeView', 'conn', 'warnNoEngine', 'refParts', 'folders_1',
    'daxHeader_1', 'existingNames', 'createThenEdit',
    measureBlock + '\nreturn authorNewMeasure;');
  const fn = make(vscodeStub, { selection }, conn, () => { messages.push('no engine'); },
    (ref) => { const i = ref.indexOf(':'); const rest = ref.slice(i + 1); const s = rest.indexOf('/');
      return s < 0 ? { kind: ref.slice(0, i), name: rest }
        : { kind: ref.slice(0, i), table: rest.slice(0, s), name: rest.slice(s + 1) }; },
    { folderParts: (ref) => ({ table: ref.split(':')[1].split('/')[0], path: ref.split('/').slice(1).join('/') }) },
    daxHeader,
    async () => new Set(),
    async (method, params, label, suffix) => { created.push({ method, params, label, suffix }); });
  return fn(undefined).then(() => ({ shown, messages, created }));
}

const tree = [
  { ref: 'table:Sales', name: 'Sales', kind: 'table', hasChildren: true },
  { ref: 'table:Product', name: 'Product', kind: 'table', hasChildren: true },
  { ref: 'table:Time intelligence', name: 'Time intelligence', kind: 'calcgroup', hasChildren: true },
  { ref: 'measure:Sales/Margin', name: 'Margin', kind: 'measure', hasChildren: false },
];

// (a) THE ASK BOX'S CASE. Nothing selected anywhere, so the person is asked which table, and the measure
//     is made on the one they chose. This is what the answer line promises: "pick the table, then write
//     the formula."
const cold = await runAuthorNewMeasure({ selection: [], tables: tree });
assert.equal(cold.shown.length, 1, 'with nothing selected the handler asks which table, it does not fail quietly');
assert.deepEqual(cold.shown[0].items.map((i) => i.label), ['Sales', 'Product', 'Time intelligence'],
  'and it offers the tables and calculation groups, which are the only things that can hold a measure');
assert.match(cold.shown[0].options.title, /New Measure/i);
assert.match(cold.shown[0].options.placeHolder, /which table/i, 'the prompt says what is being asked for');
assert.equal(cold.created.length, 1, 'and the measure is made');
assert.equal(cold.created[0].method, 'createMeasure');
assert.equal(cold.created[0].params[0], 'table:Sales', 'on the table that was picked');
assert.equal(cold.messages.length, 0, 'and nothing was reported as a problem');

// (b) A selection the person can see is used instead of asking. A selected MEASURE means its table, so
//     the gesture lands without a question the person has already answered by clicking.
const selected = await runAuthorNewMeasure({
  selection: [{ ref: 'measure:Product/Margin', name: 'Margin', kind: 'measure' }], tables: tree,
});
assert.equal(selected.shown.length, 0, 'a table the person already picked is not asked about again');
assert.equal(selected.created[0].params[0], 'table:Product');

// (c) No model, or a model with no tables. The person is told, and nothing is made.
const empty = await runAuthorNewMeasure({ selection: [], tables: [] });
assert.equal(empty.created.length, 0);
assert.equal(empty.shown.length, 0);
assert.match(empty.messages[0], /open a model/i, 'the honest answer is that there is no table to hold it');

// ===================================================================================================
// 3. The five ids are real, on both sides. A typo here is a button that does nothing.
// ===================================================================================================
const askCommands = [...askSrc.matchAll(/command: '(semanticus\.[A-Za-z]+)'/g)].map((m) => m[1]);
assert.deepEqual([...askCommands].sort(), [...TREE_CREATE].sort(),
  'the ask box offers exactly the five the relay allows');

const contributed = new Set(pkg.contributes.commands.map((c) => c.command));
for (const command of TREE_CREATE) {
  assert.ok(contributed.has(command), `${command} must be a contributed command`);
  assert.ok(extensionSrc.includes(`registerPersonCommand('${command}'`),
    `${command} must be registered in extension.ts`);
}

// The allowlist stays one readable literal, the way the navigation test holds the other one.
const createList = (extensionSrc.match(/const WEBVIEW_CREATE_COMMANDS = new Set\(\[([^\]]*)\]\)/) || [])[1];
assert.ok(createList, 'the create allowlist must be one readable literal in the source too');
assert.deepEqual([...createList.matchAll(/'([^']+)'/g)].map((m) => m[1]).sort(), [...TREE_CREATE].sort());

console.log('Ask create commands: the five tree create commands run from the webview with no ref, the '
  + 'stale-session and unknown-command refusals still hold, New Measure really shows the table picker '
  + 'with nothing selected, and the ids match package.json and the ask box.');
