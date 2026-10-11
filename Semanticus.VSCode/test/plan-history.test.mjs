// History inside the Plan (Plan-heart lane U2): the view logic, executed, and the rows, rendered.
//
// Two kinds of check, and the difference matters when reading a pass:
//   1. The WORDS and the DECISIONS are real logic, executed. plan-history-model.ts is a pure module, run here
//      against the same engine stand-in the browser driver uses (tools/uishot/fixtures/plan-history-fixture.mjs).
//   2. The ROWS, the DETAIL and the RESTORE QUESTION are rendered for real with react-dom/server through a loader
//      that stubs only the VS Code bridge. Clicks are proved in a browser by tools/uishot/cp-plan-history-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { makeHistoryEngine, HISTORY_FIXED_NOW, SESSION_NOW } from '../tools/uishot/fixtures/plan-history-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls; drive the page for that'); },
    onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: () => () => {},
    copyText: async () => true, revealInTree: () => {},
  },
  './sparkline': { ValueSparkline: () => null },
  './plan-history.css': {},
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (STUBS[name] ? STUBS[name] : name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

const M = load(join(webview, 'src', 'plan-history-model.ts'));
const now = HISTORY_FIXED_NOW;

// ===================================================================================================
// 1. The who filter: exactly five choices, and what each asks the engine for.
// ===================================================================================================
assert.deepEqual(M.WHO_FILTERS.map((f) => f.label), ['Everyone', 'You', 'Your assistant', 'Semanticus', 'Workflows'],
  'the who filter is Everyone, You, Your assistant, Semanticus, Workflows and nothing else');
assert.deepEqual(M.WHO_FILTERS.map((f) => M.whoParam(f.id)), [null, 'you', 'assistant', 'semanticus', 'workflow'],
  'each choice asks getPlanHistory for the matching `by`, and Everyone asks for no filter');
{
  const engine = makeHistoryEngine();
  for (const f of M.WHO_FILTERS.slice(1)) {
    const rows = engine.getPlanHistory(M.whoParam(f.id), null, null, 50).rows;
    assert.ok(rows.length > 0 && rows.every((r) => r.by === f.id), `filtering by ${f.label} keeps only that doer's rows`);
  }
}
assert.deepEqual(['you', 'assistant', 'semanticus', 'workflow'].map((w) => M.WHO_WORD[w]), ['You', 'Your assistant', 'Semanticus', 'Workflow']);
assert.equal(M.doerClass('assistant'), 'sem-doer sem-doer-agent', 'who is a word plus a mark in the doer hue');
assert.equal(M.doerClass('semanticus'), 'sem-doer sem-doer-engine');
assert.equal(M.doerClass('you'), 'sem-doer sem-doer-you');

// ===================================================================================================
// 2. The undo count sentence, the "+N later" tag, and the undone bar.
// ===================================================================================================
assert.equal(M.undoSentence(0), 'Takes back this change.');
assert.equal(M.undoSentence(1), 'Takes back this and 1 later change.');
assert.equal(M.undoSentence(5), 'Takes back this and 5 later changes.');
assert.equal(M.laterTag(0), '', 'the newest edit carries no tag');
assert.equal(M.laterTag(8), '+8 later');
assert.equal(M.undoneBarText(3), '3 changes undone. They come back with Redo until you make a new change.');
assert.equal(M.undoneBarText(1), '1 change undone. They come back with Redo until you make a new change.');

// ===================================================================================================
// 3. Thirteen verdicts become three words.
// ===================================================================================================
const words = Object.fromEntries(['proven', 'validated', 'safe', 'deployed', 'attributed', 'needs-review', 'impact', 'interval',
  'data-suspected', 'overridden', 'batch', 'info', 'inconclusive'].map((v) => [v, M.verifiedWord(v)?.word ?? null]));
assert.deepEqual([...new Set(Object.values(words).filter(Boolean))].sort(), ['Needs review', 'Overridden', 'Verified']);
assert.equal(words.proven, 'Verified');
assert.equal(words['needs-review'], 'Needs review');
assert.equal(words.overridden, 'Overridden');
assert.equal(words.batch, null, 'a neutral record carries no word: it was not checked, which is not the same as failing');

// ===================================================================================================
// 4. Seam placement, against the engine stand-in.
// ===================================================================================================
{
  const engine = makeHistoryEngine();
  const view = engine.getPlanHistory(null, null, null, 50);
  const lines = M.timelineLines(view.rows, new Set());
  const seams = lines.map((l, i) => (l.type === 'seam' ? { i, which: l.which } : null)).filter(Boolean);
  assert.deepEqual(seams.map((s) => s.which), ['session'], 'one session seam and no restore seam before any restore');
  const after = lines[seams[0].i + 1];
  assert.equal(after.row.title, 'Add AI instructions', 'the session seam sits right above the first row from an earlier session');
  assert.ok(lines.slice(0, seams[0].i).every((l) => !l.row.beforeThisSession), 'nothing above the seam is from an earlier session');
  assert.ok(lines.slice(seams[0].i + 1).every((l) => l.row.beforeThisSession), 'everything below it is');

  // After a restore: the restore seam under the restored row, and the session seam still above the older session.
  engine.restoreHistoryCheckpoint('a1b2c3d4e5f60718293a4b5c6d7e8f9012345678', true);
  const restored = engine.getPlanHistory(null, null, null, 50);
  const merged = restored.rows;   // the engine folds the rescue version in (contract 8.8)
  const rlines = M.timelineLines(merged, new Set());
  const order = rlines.map((l) => (l.type === 'seam' ? '|' + l.which : l.row.kind));
  assert.deepEqual(order.slice(0, 3), ['restored', 'saved', '|restore'],
    'after a restore: the Restored row, the version saved for you first, then the restore seam');
  assert.equal(order.filter((x) => x === '|session').length, 1, 'the session seam stays for the sessions before the one the restore replaced');
  assert.ok(order.indexOf('|restore') < order.indexOf('|session'));
  const rescue = merged.find((r) => r.id.startsWith('checkpoint:'));
  assert.equal(M.rowTitle(rescue), 'Saved version: Before the restore', 'the rescue version is a flag row with Restore');
  assert.equal(M.metaLine(rescue), 'Saved version · saved for you before the restore');
  assert.equal(M.rowAction(rescue).kind, 'restore');
  const oldEdit = rlines.find((l) => l.type === 'row' && l.row.kind === 'edit' && l.row.beforeRestore);
  assert.deepEqual(M.rowAction(oldEdit.row), { kind: 'none', why: 'restore' }, 'nothing above a restore can be undone');
}

// ===================================================================================================
// 5. What a row offers, and nesting by cause.
// ===================================================================================================
{
  const engine = makeHistoryEngine();
  const rows = engine.getPlanHistory(null, null, null, 50).rows;
  const top = rows[0];
  assert.equal(top.kind, 'item');
  assert.deepEqual(M.rowAction(top), { kind: 'undo', later: 0 }, 'Undo to here on the top row replaces Undo last');
  const describe = rows.find((r) => r.title === 'Describe 4 Customer columns');
  assert.equal(describe.children.length, 4, 'edits are nested under their item');
  assert.equal(M.metaLine(describe), 'Plan item · you approved · 4 changes');
  assert.equal(M.rowTitle(describe.children[0], describe), 'Customer[Tier] · description');
  const run = rows.find((r) => r.kind === 'run');
  assert.equal(M.rowTitle(run.children[1], run), '4 measures · format', 'a batch of one kind of change is named by its count');
  assert.equal(M.metaLine(run), 'Workflow run · 7 steps · 3 changes');
  const quantity = rows.find((r) => r.id === 'ed-0000000a');
  assert.equal(M.rowTitle(quantity), 'Set summarize by', 'an operation id is put in words');
  assert.equal(M.metaLine(quantity), 'Your own edit · Sales[Quantity]', 'the objects each edit touched stay on the row');
  const rename = rows.find((r) => r.id === 'ed-00000005');
  assert.equal(M.rowTitle(rename), 'Renamed Total Sales to Sales Amount', 'a sentence the engine wrote is kept as written');
  const saved = rows.find((r) => r.kind === 'saved' && !r.beforeThisSession);
  assert.equal(M.rowAction(saved).kind, 'restore', 'a saved version offers Restore');
  const old = rows.find((r) => r.beforeThisSession && r.kind === 'saved');
  assert.equal(M.rowAction(old).kind, 'restore', 'a saved version keeps Restore below the seam too');
  const oldItem = rows.find((r) => r.beforeThisSession && r.kind === 'item');
  assert.deepEqual(M.rowAction(oldItem), { kind: 'none', why: 'session' });

  // The before and after table.
  const table = M.changeRows(rename);
  assert.deepEqual(table[0], { ref: 'measure:Sales/Total Sales', object: 'Total Sales', change: 'Name', before: 'Total Sales', after: 'Sales Amount' });
  assert.equal(table[1].change, 'Formula');
  assert.deepEqual(M.changeRows(describe.children[0])[0].before, '(empty)');
  const hidden = M.changeRows(rows[0])[0];
  assert.deepEqual([hidden.object, hidden.change, hidden.before, hidden.after], ['Promotion[PromotionKey]', 'Hidden', 'No', 'Yes']);

  // Undo to here on the fourth row: the stub walks there in one call, and the four rows strike through together.
  engine.undo('human', describe.id);
  const after = engine.getPlanHistory(null, null, null, 50).rows;
  assert.deepEqual(after.slice(0, 4).map((r) => r.undone), [true, true, true, true]);
  assert.equal(after[4].undone, false);
  assert.equal(M.undoneCount(after), 9, 'nine edits under those four rows are undone');
  assert.equal(M.redoAllTarget(after), 'ed-00000001', 'Redo all walks to the newest undone edit');
  assert.deepEqual(M.rowAction(after[0]), { kind: 'redo' });
  assert.equal(engine.state.calls.filter((c) => c.call === 'undo').length, 1, 'one call, not one per step');
}

// ===================================================================================================
// 6. The engine's rows: a verified record with no edit is a row of its own, the red check reads word for word.
// ===================================================================================================
{
  const engine = makeHistoryEngine({ redCheck: true });
  const merged = engine.getPlanHistory(null, null, null, 50).rows;
  const red = merged.find((r) => r.kind === 'record');
  assert.ok(red, 'the publish that went ahead on a red check is one row');
  assert.equal(M.rowTitle(red), 'Published with a red check');
  assert.equal(M.metaLine(red), '"The board pack goes out at noon and these two are cosmetic."', 'the reason, word for word');
  assert.equal(M.verifiedWord(red.verified).word, 'Overridden');
  assert.deepEqual(M.redCheckLines(red.record), [
    'The check found 2 blocking problems: Sales[Discount] has no format; Sales[Net] is hidden and unused. The publish went ahead.',
    'Where it went: powerbi://api.powerbi.com/v1.0/myorg/Contoso Sales',
  ], 'the detail says what the check found and where the publish went');
  assert.equal(merged.filter((r) => r.kind === 'record').length, 1, 'records that sit on an edit are not repeated as rows');
  assert.equal(merged.filter((r) => r.id.startsWith('checkpoint:')).length, 0, 'versions the ledger already flagged are not repeated');
  const onlyYou = engine.getPlanHistory('assistant', null, null, 50).rows;
  assert.ok(onlyYou.every((r) => r.by === 'assistant'), 'the who filter holds for the record rows too');
  const record = M.recordFor(merged.find((r) => r.title === 'Describe 4 Customer columns'), engine.listVerifiedEdits());
  assert.equal(record.seq, 40, 'the verified record is found by session and revision');
  const promo = merged.find((r) => r.revision === 58);
  assert.equal(M.footLine(promo, M.recordFor(promo, engine.listVerifiedEdits()), engine.listVerifiedEdits()),
    'Session change 58 · verified record 41 · record intact');
  const found = M.rowForRevision(merged, SESSION_NOW, 52);
  assert.equal(found.row.id, 'ed-00000007', 'Show this edit finds the edit by its revision in this session');
  assert.equal(found.parent.title, 'Describe 4 Customer columns');
}

{
  // A broken record: a Verified word whose record sits at or after the break is not shown as Verified.
  const engine = makeHistoryEngine({ broken: true });
  const merged = engine.getPlanHistory(null, null, null, 50).rows;
  const describe = merged.find((r) => r.title === 'Describe 4 Customer columns');
  assert.equal(M.verifiedWord(describe.verified).word, 'Not trusted', 'record 40 is at the break, so its Verified word is not trusted');
  const promo = merged.find((r) => r.revision === 58);
  assert.equal(M.verifiedWord(promo.verified).word, 'Not trusted', 'and so is every record after it');
  const old = merged.find((r) => r.title === 'Add AI instructions');
  assert.equal(M.verifiedWord(old.verified).word, 'Verified', 'a record before the break keeps its word');
}

// ===================================================================================================
// 7. Time is a clock, and the restore question is asked once.
// ===================================================================================================
assert.equal(M.clock(new Date(2026, 8, 24, 10, 46).toISOString(), now), '10:46');
assert.equal(M.clock(new Date(2026, 8, 23, 16, 10).toISOString(), now), 'Yesterday');
assert.equal(M.clock(new Date(2026, 8, 21, 9, 0).toISOString(), now), 'Sep 21');
assert.deepEqual(M.restoreQuestion('Before AI prep', new Date(2026, 8, 24, 10, 30).toISOString(), now), {
  question: 'Restore Before AI prep, saved at 10:30?',
  text: 'The model goes back to how it was then. Semanticus first saves where you are now as a new version, so you can come back.',
});
assert.equal(M.askRestore(null, 'a'), 'a');
assert.equal(M.askRestore('a', 'b'), 'b', 'asking about another version replaces the question, never adds a second');
assert.equal(M.askRestore('a', 'a'), null, 'asking again closes it');

// What a restore's answer does to the page (Codex F-283). restored comes first: the engine answers restored AND an
// error when the files came back but the checkpoint commit after them failed, and that restore happened, so the
// question closes and the error is a warning, as the Edit History view does. Only an answer that restored nothing
// keeps the question open, with the error as a failure.
const partial = { restored: true, error: 'The model files were restored and reopened, but the restored state was not committed: git commit failed.' };
assert.deepEqual(M.restoreOutcome(partial), { settled: true, msg: partial.error, tone: 'warn' }, 'half a restore is still a restore');
assert.deepEqual(M.restoreOutcome({ restored: true, note: 'Restored.' }), { settled: true, msg: null, tone: null },
  'a clean restore closes the question; the Restored row on top says what happened');
assert.deepEqual(M.restoreOutcome({ restored: false, error: 'git could not restore the target' }),
  { settled: false, msg: 'git could not restore the target', tone: 'bad' }, 'nothing restored: the question stays, with why');
assert.deepEqual(M.restoreOutcome({ restored: false }), { settled: false, msg: 'Nothing was restored.', tone: 'bad' },
  'a refusal with no error text still says nothing was restored (m11/retire save-honesty)');
{
  const src = readFileSync(join(webview, 'src', 'plan-history.tsx'), 'utf8');
  const body = src.slice(src.indexOf('const restore = async'), src.indexOf('const save = async'));
  assert.match(body, /M\.restoreOutcome\(/, 'the page decides a restore through restoreOutcome');
  assert.ok(!/if \(r\.error\) setMsg/.test(body), 'and never error-first again');
}

// ===================================================================================================
// 8. The rows, rendered.
// ===================================================================================================
const H = load(join(webview, 'src', 'plan-history.tsx'));
{
  const engine = makeHistoryEngine({ undone: 3 });
  const view = engine.getPlanHistory(null, null, null, 50);
  const lines = M.timelineLines(view.rows, new Set(['workflow_run:run-7f3a']));
  const props = { lines, now, selected: null, ask: null, busy: false, restoreFor: () => null,
    onSelect() {}, onToggle() {}, onAction() {}, onConfirmRestore() {}, onCancelRestore() {} };
  const html = renderToStaticMarkup(React.createElement(H.HistoryTimeline, props));
  const rowClasses = [...html.matchAll(/<div class="([^"]*)" data-hid="([^"]+)"/g)].map((m) => ({ cls: m[1], id: m[2] }));
  const struck = rowClasses.filter((r) => /\bph-undone\b/.test(r.cls));
  assert.deepEqual(struck.map((r) => r.id), ['plan_item:safefix/promotion-keys', 'ed-00000002', 'ed-00000003'],
    'the rows struck through are exactly the undone ones');
  assert.equal(struck.length, 3, 'three undone edits: the top row and two of the run\'s edits are struck through');
  assert.match(readable(html), /Redo to here/);
  assert.match(readable(html), /Undo to here \+1 later/, 'a live row says how many later changes go with it');
  assert.match(readable(html), /Before this session Undo is not available for these\./);
  assert.match(readable(html), /Saved version: Before AI prep/);
  assert.equal((html.match(/data-testid="ph-restore-confirm"/g) || []).length, 0, 'no question until Restore is pressed');

  // With a restore asked: exactly one confirmation, under that version, with the mockup's words.
  const asked = renderToStaticMarkup(React.createElement(H.HistoryTimeline, { ...props, ask: 'ed-000000c1',
    restoreFor: (row) => M.restoreQuestion('Before AI prep', row.when, now) }));
  assert.equal((asked.match(/data-testid="ph-restore-confirm"/g) || []).length, 1, 'Restore shows exactly one confirmation');
  assert.match(readable(asked), /Restore Before AI prep, saved at 10:30\? The model goes back to how it was then\./);
  assert.match(readable(asked), /Restore this version Cancel/);
  const css = readFileSync(join(webview, 'src', 'plan-history.css'), 'utf8');
  assert.match(css, /\.ph-undone[^{]*\{[^}]*line-through/, 'an undone row is struck through');
}
{
  // The detail panel: who, before and after, Verified as a word with its Evidence, the undo sentence, one quiet line.
  const engine = makeHistoryEngine();
  const rows = engine.getPlanHistory(null, null, null, 50).rows;
  const chain = engine.listVerifiedEdits();
  const describe = rows.find((r) => r.title === 'Describe 4 Customer columns');
  const html = renderToStaticMarkup(React.createElement(H.HistoryDetail, {
    row: describe.children[0], parent: describe, chain, now, busy: false, sessionId: SESSION_NOW,
    onClose() {}, onAction() {}, onObject() {}, onJump() {},
  }));
  const text = readable(html);
  assert.match(text, /Your assistant 10:41/);
  assert.match(text, /Customer\[Tier\] · description/);
  assert.match(text, /Part of: Describe 4 Customer columns · Plan item · you approved · 4 changes/);
  assert.match(text, /Object Change Before After Customer\[Tier\] Description \(empty\) Loyalty tier: Bronze, Silver or Gold\./);
  assert.match(text, /Verified\. The check passed: all 4 columns now have a description\. Evidence/);
  assert.match(text, /Undo to here Takes back this and 5 later changes\./);
  assert.match(text, /Session change 53 · verified record 40 · record intact/);

  const old = rows.find((r) => r.beforeThisSession && r.kind === 'item');
  const oldHtml = readable(renderToStaticMarkup(React.createElement(H.HistoryDetail, {
    row: old, chain, now, busy: false, sessionId: SESSION_NOW, onClose() {}, onAction() {}, onObject() {}, onJump() {} })));
  assert.match(oldHtml, /Before this session\. Undo is not available\. Restore a saved version instead\./);
  assert.doesNotMatch(oldHtml, /Undo to here/);

  // "Open in <page>": a row caused by a plan item links to the page that does that item's work. The Plan
  // answers which page from the cause's item ids; with no answer (or no Plan to ask) there is no link.
  const asked = [];
  const pageOf = (ids) => { asked.push(ids); return ids.includes('describe/customer') ? { tool: 'readiness', label: 'AI readiness' } : null; };
  const went = [];
  const linked = renderToStaticMarkup(React.createElement(H.HistoryDetail, {
    row: describe.children[0], parent: describe, chain, now, busy: false, sessionId: SESSION_NOW,
    onClose() {}, onAction() {}, onObject() {}, onJump() {}, pageOf, onGo: (t) => went.push(t) }));
  assert.match(readable(linked), /Open in AI readiness/, 'an edit inside a plan item links to that item\'s page');
  assert.deepEqual(asked[0], ['describe/customer'], 'the Plan is asked with the cause\'s item ids');
  assert.match(linked, /data-testid="ph-detail-page"/);
  assert.doesNotMatch(readable(renderToStaticMarkup(React.createElement(H.HistoryDetail, {
    row: old, chain, now, busy: false, sessionId: SESSION_NOW, onClose() {}, onAction() {}, onObject() {}, onJump() {}, pageOf }))),
    /Open in/, 'a row whose item has no page gets no link');
  assert.doesNotMatch(text, /Open in/, 'without the Plan to ask, no link');
}

// ===================================================================================================
// 9. Copy: no em dashes, no retired words, and the page reads getPlanHistory, not the old buffer.
// ===================================================================================================
for (const f of ['plan-history.tsx', 'plan-history-model.ts', 'plan-history.css']) {
  const bytes = readFileSync(join(webview, 'src', f));
  assert.equal(bytes.includes(Buffer.from([0xe2, 0x80, 0x94])), false, `${f} carries no em dash`);
}
const page = readFileSync(join(webview, 'src', 'plan-history.tsx'), 'utf8');
assert.match(page, /rpc<[^>]*>\('getPlanHistory'/, 'the page reads getPlanHistory');
assert.match(page, /rpc<[^>]*>\('(undo|redo)', 'human', /, 'undo and redo to a point send toEntry in one call');
assert.doesNotMatch(page, /for \(let k = 0; k < times/, 'the N-call undo loop is gone');
assert.doesNotMatch(page, /history-counts/, 'the counts line is cut: the who filter does it');

// F-353: a Primer write is a History row whose delta names the Primer and the section it changed.
assert.equal(M.objectName('primer'), 'Primer', 'a Primer change names the Primer, not the raw ref');
assert.equal(M.propertyWord('Business context'), 'Business context');

// A restore the person starts reopens the model as a new session, and Studio remounts every page on it (lane
// m11/create-model-ui, verifier D3). Without a landing the page came back on Work and a half restore's warning ("the
// files were restored, but the restored state was not committed") vanished with the old page. The landing carries
// History and that warning across the remount, once, and only onto the session the restore reopened (verifier r2 O4:
// kept by time alone, a restore whose remount never came put its line on the next model opened within the minute).
{
  const t0 = 1_000_000;
  const half = 'The model files were restored and reopened, but the restored state was not committed: disk full';
  assert.equal(M.peekRestoreLanding('s2', t0), false, 'nothing is carried before a restore');
  M.noteRestoreLanding({ settled: true, msg: half, tone: 'warn' }, 's2', t0);
  assert.equal(M.peekRestoreLanding('s3', t0 + 500), false, 'a different model opened within the minute does not open on History');
  assert.equal(M.takeRestoreLanding('s3', t0 + 500), null, 'nor does it say what another model\'s restore said');
  assert.equal(M.peekRestoreLanding('s2', t0 + 500), true, 'the remounted Plan of the restored model opens on History');
  assert.deepEqual(M.takeRestoreLanding('s2', t0 + 600), { msg: half, tone: 'warn' }, 'and History says what the restore said');
  assert.equal(M.takeRestoreLanding('s2', t0 + 700), null, 'once');
  assert.equal(M.peekRestoreLanding('s2', t0 + 700), false, 'and the next mount opens on Work again');
  M.noteRestoreLanding({ settled: true, msg: null, tone: null }, 's4', t0);
  assert.equal(M.peekRestoreLanding('s4', t0 + 120_000), false, 'a landing no remount picked up goes stale');
  assert.equal(M.takeRestoreLanding('s4', t0 + 120_000), null);
  M.noteRestoreLanding({ settled: true, msg: half, tone: 'warn' }, undefined, t0);
  assert.equal(M.peekRestoreLanding(undefined, t0 + 1), false, 'a restore that named no reopened session lands nowhere');
  assert.equal(M.peekRestoreLanding('', t0 + 1), false, 'and no session is not a session');

  const page = readFileSync(join(webview, 'src', 'plan-history.tsx'), 'utf8');
  const restoreFn = page.slice(page.indexOf('const restore = async'), page.indexOf('const save = async'));
  assert.match(restoreFn, /if \(r\.restored\) M\.noteRestoreLanding\(o, r\.sessionId\)/, 'a restore that restored leaves a landing for the session it reopened');
  assert.match(page, /M\.takeRestoreLanding\(sessionId\)/, 'History picks the landing up when it mounts on that session');
  const planPage = readFileSync(join(webview, 'src', 'plan.tsx'), 'utf8');
  assert.match(planPage, /useState<View>\(\(\) => \(peekRestoreLanding\(sessionId\) \? 'hist' : 'work'\)\)/, 'the Plan mounts on History after a restore of this model');
  assert.match(planPage, /<PlanHistory [^>]*sessionId=\{sessionId\}/, 'the Plan tells History which session it is on');
}

console.log('plan-history.test.mjs: all checks passed');
