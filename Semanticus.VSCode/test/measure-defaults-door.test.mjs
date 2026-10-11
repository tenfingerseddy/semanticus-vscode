// The human door for propose_measure_defaults, executed rather than read.
//
// WHAT WENT WRONG. F-176: all seven typed-decision operations shipped on the RPC target and on MCP with
// no caller anywhere in VS Code. Six of them got a webview surface; this one could not have an honest
// webview surface, because a measure is not made in the Studio. It is made by the Model tree's New
// Measure gesture, which is zero dialog on purpose: it creates the measure with a generated name and
// the expression "0" and opens the DAX editor. A proposal about THAT measure is a proposal about a
// placeholder. The measure first has a real name and a real formula when the editor is SAVED, so the
// offer lives at the end of the save.
//
// HOW THIS TESTS IT. The compiled out/extension.js is the real code. Two things are executed here, not
// regexed:
//
//   1. noteMeasureSaved itself, with the engine faked. Every branch a person can meet is driven: a
//      proposal that prefills the prompt, a not-decided answer, a free session, a measure the engine
//      has nothing to say about, and Apply writing through the ordinary operations.
//   2. DaxFileSystem.writeFile's own body, with fifteen host seams stubbed, to prove the save is never
//      delayed by the offer. The strongest case here hands writeFile an offer that NEVER resolves and
//      asserts the save still completes: if anybody ever puts an `await` in front of that call, this
//      test hangs and then fails rather than going quietly green.
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

/**
 * The text of one top-level declaration: a `const` up to the semicolon that ends it, or a function (or a
 * class method) up to the brace that closes its body. Brackets inside quotes and comments do not count,
 * which is why this is a scanner. Copied in shape from ask-create-command.test.mjs, which slices the
 * same file for the same reason.
 */
function sliceDeclaration(source, opener) {
  const start = source.indexOf(opener);
  assert.ok(start >= 0, `out/extension.js no longer contains "${opener}". This test slices real code; find where it went.`);
  const open = { '{': 1, '(': 1, '[': 1 };
  const close = { '}': 1, ')': 1, ']': 1 };
  let depth = 0;
  let body = false;
  for (let i = start; i < source.length; i++) {
    const c = source[i];
    if (c === '/' && source[i + 1] === '/') { i = source.indexOf('\n', i); if (i < 0) break; continue; }
    if (c === '/' && source[i + 1] === '*') { i = source.indexOf('*/', i) + 1; continue; }
    if (c === '\'' || c === '"' || c === '`') {
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
// 1. noteMeasureSaved, executed against a fake engine.
// ===================================================================================================
const doorBlock = sliceDeclaration(compiled, 'const MEASURE_DEFAULTS_WAIT_MS')
  + '\n' + sliceDeclaration(compiled, 'const FORMAT_FAMILY_CATEGORY')
  + '\n' + sliceDeclaration(compiled, 'async function noteMeasureSaved');

// eslint-disable-next-line no-new-func
const makeDoor = new Function('vscode', 'refParts', 'tree',
  doorBlock + '\nreturn noteMeasureSaved;');


const MEASURE = 'measure:Sales/Margin Percent';

// The real refParts and the real kind set, both from the same build, so a ref that parses differently
// there parses differently here. A hand-rolled split would let this test pass while the product mis-read
// a ref. tsc rewrites the import to `daxHeader_1.MODEL_SCOPED_KINDS`, so the module object is what the
// slice needs handing.
const daxHeaderModule = req(join(root, 'out', 'daxHeader.js'));
// eslint-disable-next-line no-new-func
const realRefParts = new Function('daxHeader_1',
  sliceDeclaration(compiled, 'function refParts') + '\nreturn refParts;')(daxHeaderModule);

function fakeEngine({ tier = 'pro', proposal = null, templates = [{ formatString: '0%' }], fail = null,
  sessions = [{ sessionId: 's1', revision: 7 }] } = {}) {
  const calls = [];
  let reads = 0;
  let revision = sessions[0].revision;
  return {
    calls,
    conn: {
      sendRequest(method, ...args) {
        calls.push({ method, args });
        if (fail === method) return Promise.reject(new Error('the engine said no'));
        if (method === 'getEntitlement') return Promise.resolve({ tier });
        if (method === 'proposeMeasureDefaults') return Promise.resolve(proposal);
        if (method === 'listFormatTemplates') return Promise.resolve(templates);
        // Each sessionInfo read answers the next state in the list, so a test can move the model
        // between the moment the offer is made and the moment it is answered.
        if (method === 'sessionInfo') return Promise.resolve(sessions[Math.min(reads++, sessions.length - 1)]);
        // A real write bumps the model's revision and hands back the new one. The fixture does the same,
        // and refuses a write whose fence names a revision that is no longer current, exactly as
        // GuardExpectedRevision does inside the engine's dispatch.
        if (method === 'setDisplayFolder' || method === 'setMeasureFormat') {
          const fencedAt = method === 'setDisplayFolder' ? args[4] : args[4];
          if (fencedAt != null && fencedAt !== revision) {
            return Promise.reject(new Error('The model changed before this edit landed. Nothing was written.'));
          }
          revision += 1;
          return Promise.resolve({ revision, changed: true });
        }
        return Promise.resolve({});
      },
    },
  };
}

function fakeVscode({ answer = undefined } = {}) {
  const shown = [];
  const ran = [];
  return {
    shown, ran,
    vscode: {
      window: {
        showInformationMessage: (text, ...buttons) => { shown.push({ text, buttons }); return Promise.resolve(answer); },
        showWarningMessage: (text) => { shown.push({ warning: text }); return Promise.resolve(undefined); },
        setStatusBarMessage: () => undefined,
      },
      commands: { executeCommand: (id, ...args) => { ran.push({ id, arg: args[0], args }); return Promise.resolve(); } },
    },
  };
}

const refreshed = { n: 0 };
const fakeTree = { refresh: () => { refreshed.n++; } };

// ---- a confident proposal prefills the prompt, and Apply writes through the ordinary operations ----
{
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs', proposedFormatFamily: 'percent' },
    templates: [{ category: 'Percentages', formatString: '0%' }, { formatString: '0.0%' }],
  });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);

  assert.equal(ui.shown.filter((x) => x.text).length, 1, 'exactly one quiet prompt');
  assert.equal(ui.shown[0].text, 'Put "Margin Percent" in KPIs and format it as 0%?',
    'the prompt carries the proposed folder and the format string the engine catalog holds');
  assert.deepEqual(ui.shown[0].buttons, ['Apply', 'Change', 'Not for this one']);

  // The format STRING is read from the engine's catalog for the family the point chose, never typed here.
  const templateCall = engine.calls.find((c) => c.method === 'listFormatTemplates');
  assert.ok(templateCall, 'the format string must come from the engine catalog');
  assert.equal(templateCall.args[0], 'Percentages', 'the family maps to the catalog category');

  const folderWrite = engine.calls.find((c) => c.method === 'setDisplayFolder');
  const formatWrite = engine.calls.find((c) => c.method === 'setMeasureFormat');
  assert.ok(folderWrite && formatWrite, 'Apply writes through both existing operations');
  assert.deepEqual(folderWrite.args, [[MEASURE], 'KPIs', 'human', 's1', 7],
    'an ordinary, undoable folder write, fenced on the model the offer was made about');
  // Codex, pull request 387 comment 4066759408: the folder write BUMPS the revision, so a second write
  // fenced on the offer's revision is refused and the person gets half the proposal and a warning that
  // is not true. The second write is fenced on what the first one returned.
  assert.deepEqual(formatWrite.args, [MEASURE, '0%', 'human', 's1', 8],
    'the format write is fenced on the revision the folder write returned, not on the stale one');
}

// ---- "Not for this one" writes nothing ------------------------------------------------------------
{
  const engine = fakeEngine({ proposal: { asked: true, proposedDisplayFolder: 'KPIs' } });
  const ui = fakeVscode({ answer: 'Not for this one' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(ui.shown.filter((x) => x.text).length, 1);
  assert.equal(engine.calls.filter((c) => c.method === 'setDisplayFolder' || c.method === 'setMeasureFormat').length, 0,
    'declining must write nothing at all');
}

// ---- "Change" writes nothing and opens the pickers that already exist -----------------------------
{
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs', proposedFormatFamily: 'whole_number' },
    templates: [{ formatString: '#,##0' }],
  });
  const ui = fakeVscode({ answer: 'Change' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(engine.calls.filter((c) => c.method.startsWith('set')).length, 0, 'Change writes nothing');
  assert.deepEqual(ui.ran.map((r) => r.id), ['semanticus.moveToFolder', 'semanticus.setFormat'],
    'Change opens the pickers the right-click menu already opens');
  assert.equal(ui.ran[0].arg.ref, MEASURE, 'the picker is handed the measure that was just saved');
  // Codex, pull request 387 comment 4066759416: a picker can stand open while the other door opens a
  // different model, and the write it makes on close lands on whatever is current. Each picker carries
  // the model this offer was about.
  assert.equal(ui.ran[0].args[2], 's1', 'moveToFolder was not told which model the offer was about');
  assert.equal(ui.ran[1].args[1], 's1', 'setFormat was not told which model the offer was about');
}

// ---- a not-decided answer leaves today's defaults alone -------------------------------------------
{
  // asked:true, nothing sure enough. This is the engine saying "I looked and I do not know", and the
  // page it reaches must look exactly like the page that shipped before any of this existed.
  const engine = fakeEngine({ proposal: { asked: true, reason: 'nothing was sure enough to suggest' } });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(ui.shown.length, 0, 'a not-decided answer shows no prompt');
  assert.equal(engine.calls.filter((c) => c.method.startsWith('set')).length, 0);
}

// ---- no permission (or no service, or a switched-off point) is asked:false, and is silent ---------
{
  const engine = fakeEngine({
    proposal: { asked: false, reason: "you have not given permission to send this model's details for advice" },
  });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(ui.shown.length, 0, 'an unasked point shows no prompt and no explanation nobody requested');
}

// ---- a free session NEVER calls the operation -----------------------------------------------------
{
  const engine = fakeEngine({ tier: 'free', proposal: { asked: true, proposedDisplayFolder: 'KPIs' } });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(engine.calls.filter((c) => c.method === 'proposeMeasureDefaults').length, 0,
    'a free session must not reach a Pro operation at all, not even to be refused');
  assert.equal(ui.shown.length, 0);
}

// ---- an unreadable licence is not a licence to ask ------------------------------------------------
{
  const engine = fakeEngine({ fail: 'getEntitlement', proposal: { asked: true, proposedDisplayFolder: 'KPIs' } });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(engine.calls.filter((c) => c.method === 'proposeMeasureDefaults').length, 0);
}

// ---- anything that is not a measure is not this point's business ----------------------------------
{
  const engine = fakeEngine({ proposal: { asked: true, proposedDisplayFolder: 'KPIs' } });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, 'column:Sales/Amount');
  assert.equal(engine.calls.length, 0, 'a column save must not even read the licence');
}

// ---- a currency family offers the folder and NOT a currency nobody chose --------------------------
{
  // Every currency template names a currency and the point is never asked which. Filling one in would
  // be guessing at somebody's money, so the format half is left to the picker on purpose.
  const engine = fakeEngine({ proposal: { asked: true, proposedDisplayFolder: 'KPIs', proposedFormatFamily: 'currency' } });
  const ui = fakeVscode({ answer: 'Not for this one' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(engine.calls.filter((c) => c.method === 'listFormatTemplates').length, 0);
  assert.equal(ui.shown[0].text, 'Put "Margin Percent" in KPIs?', 'the folder half still stands on its own');
}

// ---- Codex 2: Apply with BOTH a folder and a format lands both -----------------------------------
{
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs', proposedFormatFamily: 'percent' },
    templates: [{ formatString: '0%' }],
  });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);

  assert.ok(engine.calls.some((c) => c.method === 'setDisplayFolder'), 'the folder did not land');
  assert.ok(engine.calls.some((c) => c.method === 'setMeasureFormat'), 'the format did not land');
  assert.equal(ui.shown.filter((x) => x.warning).length, 0,
    'both writes landed, so there is nothing to warn about');
}

// ---- Codex 2: when the SECOND write fails, the person is told which one did not -------------------
{
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs', proposedFormatFamily: 'percent' },
    templates: [{ formatString: '0%' }],
    fail: 'setMeasureFormat',
  });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);

  const warned = ui.shown.find((x) => x.warning);
  assert.ok(warned, 'a half-applied proposal has to be reported');
  assert.match(warned.warning, /folder/i, 'the line has to say the folder was moved');
  assert.match(warned.warning, /format/i, 'and that the format was not');
}

// ---- Astra 3: an offer answered after the model changed writes NOTHING --------------------------
{
  // Astra's walk, 2026-09-22: save measure:Sales/Total in A, leave the offer open, open B which has the
  // same ref, press Apply. The ref resolves in whatever session is current, so B took A's folder and
  // format. The offer now carries the model it was made about.
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs', proposedFormatFamily: 'percent' },
    sessions: [{ sessionId: 's1', revision: 7 }, { sessionId: 's2', revision: 1 }],
  });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);

  assert.equal(engine.calls.filter((c) => c.method === 'setDisplayFolder' || c.method === 'setMeasureFormat').length, 0,
    'a different model is open, so the suggestion must not be written to it');
  const warned = ui.shown.find((x) => x.warning);
  assert.ok(warned, 'the person is told, in a plain line, that nothing was written');
  assert.match(warned.warning, /different model|changed/i);
}

// ---- Astra 3: the same for an edit that moved the model under the offer ---------------------------
{
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs' },
    sessions: [{ sessionId: 's1', revision: 7 }, { sessionId: 's1', revision: 9 }],
  });
  const ui = fakeVscode({ answer: 'Apply' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.equal(engine.calls.filter((c) => c.method === 'setDisplayFolder').length, 0,
    'the model moved after the offer was made, so the suggestion is stale');
  assert.ok(ui.shown.some((x) => x.warning), 'and the person is told');
}

// ---- Astra 3: Change is fenced too, because it carries the same stale ref ------------------------
{
  const engine = fakeEngine({
    proposal: { asked: true, proposedDisplayFolder: 'KPIs' },
    sessions: [{ sessionId: 's1', revision: 7 }, { sessionId: 's2', revision: 1 }],
  });
  const ui = fakeVscode({ answer: 'Change' });
  await makeDoor(ui.vscode, realRefParts, fakeTree)(engine.conn, MEASURE);
  assert.deepEqual(ui.ran, [], 'a picker opened on a stale ref would edit the wrong model too');
  assert.ok(ui.shown.some((x) => x.warning));
}

// ===================================================================================================
// 1b. The pickers themselves, executed, to prove the fence reaches the write and not just the command.
// ===================================================================================================
{
  // setFormatCmd is the smaller of the two and it writes on three branches. The template branch is
  // driven here with the fence set, and the write it makes has to carry it: a command that accepted the
  // argument and dropped it would pass the assertion above and still write to the wrong model.
  const formatBlock = sliceDeclaration(compiled, 'async function setFormatCmd');
  const sent = [];
  const stub = {
    conn: { sendRequest: (method, ...args) => { sent.push({ method, args }); return Promise.resolve({}); } },
    tree: fakeTree,
    vscode: {
      window: {
        showQuickPick: () => Promise.resolve({ label: 'Percent', tpl: { name: 'Percent', kind: 'static', formatString: '0%' } }),
        showInputBox: () => Promise.resolve(undefined),
        showErrorMessage: () => undefined,
      },
      QuickPickItemKind: { Separator: -1 },
    },
  };
  // eslint-disable-next-line no-new-func
  const setFormat = new Function(...Object.keys(stub), formatBlock + '\nreturn setFormatCmd;')(...Object.values(stub));
  await setFormat({ ref: MEASURE, name: 'Margin Percent', kind: 'measure', hasChildren: false }, 's1');

  const write = sent.find((c) => c.method === 'setMeasureFormat');
  assert.ok(write, 'the picker did not write at all');
  assert.equal(write.args[3], 's1',
    'setFormatCmd took the model fence and dropped it: the write can still land on a model opened while the picker waited');
}

// ===================================================================================================
// 2. The save is never delayed by the offer, proven by executing writeFile's real body.
// ===================================================================================================
// writeFile opens the scope of the Save (modelFence.js personAction) and runs writeFileOnOpenModel, which is the body that does the work.
const writeFileBody = sliceDeclaration(compiled, 'async writeFileOnOpenModel(uri, content)');

function runWriteFile({ note }) {
  const order = [];
  const stub = {
    Buffer,
    // tsc namespaces every daxHeader import, so the pure decision helpers arrive as one module object.
    daxHeader_1: {
      splitDaxHeader: (raw) => ({ header: 'Margin Percent', body: raw }),
      decideDaxSave: () => ({ kind: 'header', name: 'Margin Percent' }),
      decideRenameRecovery: () => ({ kind: 'ok', baseRef: MEASURE }),
      reKeyRef: (r) => r,
      shouldApplyDaxBuffer: () => true,
    },
    refFromUri: () => MEASURE,
    isDaxHeaderDoc: () => true,
    headerUriModelKey: () => 'model-1',
    getPendingRename: () => undefined,
    clearPendingRename: async () => undefined,
    setPendingRename: async () => undefined,
    reopenRenamedDax: () => undefined,
    daxHeaderDocs: new Set(),
    refParts: realRefParts,
    movedFormulaError: () => false,
    noteMeasureSaved: (...args) => { order.push('offer'); return note(...args); },
    vscode: { window: { setStatusBarMessage: () => undefined } },
  };
  // eslint-disable-next-line no-new-func
  const make = new Function(...Object.keys(stub), `return ({ ${writeFileBody} }).writeFileOnOpenModel;`);
  const writeFile = make(...Object.values(stub));

  const self = {
    conn: () => ({
      sendRequest: (method) => { order.push(method); return Promise.resolve({ revision: 2 }); },
    }),
    liveModelIdentity: async () => ({ token: 'model-1', sessionId: 's1', revision: 1 }),
    lastShown: new Map(),
    startedFrom: new Map(),
    rememberWritten: () => { order.push('remembered'); },
    warnIfModelDrifted: async () => { order.push('driftChecked'); },
    afterApplied: () => undefined,   // the file save after an apply (dax-editor-ctrl-s-file.test.mjs holds that)
  };
  const uri = { toString: () => 'semanticus-dax:/Sales/Margin Percent.dax?hdr=1' };
  return { order, done: writeFile.call(self, uri, Buffer.from('SUM ( Sales[Margin] )', 'utf8')) };
}

// ---- the offer is made only after the write and the bookkeeping have finished ---------------------
{
  const { order, done } = runWriteFile({ note: async () => undefined });
  await done;
  const setDax = order.indexOf('setDax');
  const remembered = order.indexOf('remembered');
  const offer = order.indexOf('offer');
  assert.ok(setDax >= 0 && remembered >= 0 && offer >= 0, `every step should have run, saw ${order.join(' ')}`);
  assert.ok(setDax < offer, 'the DAX is written before anything is offered');
  assert.ok(remembered < offer, 'the editor bookkeeping is finished before anything is offered');
}

// ---- an offer that never returns cannot hold the save open ----------------------------------------
{
  // The load-bearing one. If somebody puts `await` in front of noteMeasureSaved, this promise never
  // settles and the race below fails with a named message instead of the suite hanging silently.
  const { done } = runWriteFile({ note: () => new Promise(() => { /* never settles */ }) });
  const raced = await Promise.race([
    done.then(() => 'saved'),
    new Promise((r) => setTimeout(() => r('blocked'), 1500)),
  ]);
  assert.equal(raced, 'saved',
    'the save waited on the proposal. noteMeasureSaved must be called with `void`, never awaited: a suggestion may never hold a save open.');
}

console.log('measure defaults door: noteMeasureSaved executed across 14 states, three of them a model that moved under the offer and two a two-part Apply; writeFile proven not to wait on it');
