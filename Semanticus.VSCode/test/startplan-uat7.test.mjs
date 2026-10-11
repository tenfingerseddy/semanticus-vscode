// Join 7: Sol's UAT of the start journeys (UAT-J7-S1, UAT-J7-S2). The person's line controls, the column list, the review's plain words for what the
// build sets, and the per-table permission card. Executed against the real page code (startplan-model.ts, startplan.tsx, consent.tsx) and read as the
// words a person sees. What only a browser sees (one open row, the card over the page) is proven by tools/uishot/startplan-real-drive.mjs against the real engine.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({
  './startplan.css': {},
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {}, copyText: async () => true,
    onDidChange: () => () => {}, onPlanChange: () => () => {}, navigateInStudio: () => {}, claimConsentAsk: async () => true,
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
});
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');
const M = load('startplan-model');
const P = load('startplan');
const C = load('consent');
const asView = (state) => JSON.parse(JSON.stringify(startPlanView(state)));
const failed = [];
const check = (name, fn) => { try { fn(); console.log(`  [PASS] ${name}`); } catch (e) { failed.push(name); console.log(`  [FAIL] ${name}: ${e.message.split('\n')[0]}`); } };
const checkAsync = async (name, fn) => { try { await fn(); console.log(`  [PASS] ${name}`); } catch (e) { failed.push(name); console.log(`  [FAIL] ${name}: ${e.message.split('\n')[0]}`); } };

const line = (over) => ({ item: { id: 'x1', kind: 'set_description', objectRef: 'table:Sales', objectName: 'Sales', after: 'Every sale.', ...over.item }, decidedBy: 'rule', kept: true, ...over, });

check('lines: a table, a link, a measure, a time measure and the date table each offer only what the engine can do', () => {
  const table = M.lineControls(line({ item: { id: 't1', kind: 'include_table', objectRef: 'table:Sales', objectName: 'Sales', after: null } }));
  assert.equal(table.switchable, true); assert.equal(table.pin, false);
  assert.deepEqual(table.rename, { target: 'table:Sales', current: 'Sales' });
  const date = M.lineControls(line({ item: { id: 'd1', kind: 'mark_date_table', objectRef: 'table:Date', objectName: 'Date', after: null } }));
  assert.equal(date.switchable, false, 'the date table has no switch');
  assert.equal(date.rename, null);
  const base = M.lineControls(line({ item: { id: 'm1', kind: 'create_measure', objectRef: 'measure:Sales/Total', objectName: 'Total', after: null } }));
  assert.deepEqual(base.rename, { target: 'measure:Sales/Total', current: 'Total' }); assert.equal(base.pin, false);
  const time = M.lineControls(line({ derived: true, item: { id: 'm2', kind: 'create_measure', objectRef: 'measure:Sales/Total YTD', objectName: 'Total YTD', after: 'TOTALYTD(...)' } }));
  assert.equal(time.switchable, true); assert.equal(time.pin, false, 'a time measure formula cannot be pinned'); assert.equal(time.rename, null, 'a time measure takes its name from its base');
  const link = M.lineControls(line({ item: { id: 'l1', kind: 'create_relationship', objectRef: 'relationship:x', after: null } }));
  assert.equal(link.switchable, true); assert.equal(link.pin, false);
});

check('lines: the value a rule or Jev chose can be kept; words the assistant or the person wrote can be edited; nothing else', () => {
  assert.equal(M.lineControls(line({ decidedBy: 'jev', item: { id: 'f1', kind: 'set_measure_format', after: '0.0%' } })).pin, true);
  assert.equal(M.lineControls(line({ decidedBy: 'rule', item: { id: 'f2', kind: 'set_summarize_by', after: 'None' } })).pin, true);
  assert.equal(M.lineControls(line({ decidedBy: 'person' })).pin, false, 'a line already the person\'s has nothing to keep');
  const written = M.lineControls(line({ decidedBy: 'assistant', word: 'Every sale.' }));
  assert.deepEqual(written.words, { ref: 'table:Sales', kind: 'description', current: 'Every sale.' });
  assert.equal(M.lineControls(line({ decidedBy: 'person', word: 'Mine.', item: { id: 'w2', kind: 'set_synonyms', objectRef: 'table:Sales', after: 'a, b' } })).words.kind, 'synonyms');
  assert.equal(M.lineControls(line({ decidedBy: 'assistant', word: 'New name', item: { id: 'w3', kind: 'rename', objectRef: 'column:Sales/Qty', after: 'New name' } })).words.kind, 'display_name');
  assert.equal(M.lineControls(line({ decidedBy: 'jev' })).words, null, 'words from Jev are changed with Keep this value, not an edit box');
  assert.equal(M.lineControls(line({ decidedBy: 'assistant', item: { id: 'w4', kind: 'bpa_fix' }, word: 'x' })).words, null, 'only the four word kinds');
});

check('calls: each control sends one engine call, as the person', () => {
  const l = line({ decidedBy: 'assistant', word: 'Every sale.' });
  assert.deepEqual(M.editLine(l, false), { op: 'drop_line', target: 'x1' });
  assert.deepEqual(M.editLine(l, true), { op: 'keep_line', target: 'x1' });
  assert.deepEqual(M.renameEdit('table:Sales', '  Orders '), { op: 'rename', target: 'table:Sales', value: 'Orders' });
  const [words, who] = M.wordsCall(M.lineControls(l).words, ' One row per order line. ');
  assert.deepEqual(words, [{ ref: 'table:Sales', kind: 'description', text: 'One row per order line.', expected: 'Every sale.' }], 'the edit names the words the person started from'); assert.equal(who, 'human');
  const col = { ref: 'column:Sales/Qty', name: 'Qty', included: true, hidden: false, key: false };
  assert.deepEqual(M.columnRenameEdit(col, 'Quantity'), { op: 'rename', target: 'column:Sales/Qty', value: 'Quantity' });
  assert.deepEqual(M.columnIncludeEdit(col, false), { op: 'include_column', target: 'column:Sales/Qty', value: 'false' });
  assert.deepEqual(M.tableIncludeEdit('Payroll', false), { op: 'include_table', target: 'Payroll', value: 'false' });
  assert.deepEqual(M.tableSampleEdit('Payroll', false), { op: 'set_table_sample', target: 'Payroll', value: 'off' });
  assert.deepEqual(M.tableSampleEdit('Payroll', true), { op: 'set_table_sample', target: 'Payroll', value: 'on' });
});

check('lines: the open detail names the controls it offers, and the plain words for what the build sets', () => {
  const view = { sections: [{ id: 'measures', groups: [] }] };
  const measure = line({ item: { id: 'm1', kind: 'create_measure', objectRef: 'measure:Sales/Total', objectName: 'Total', after: null }, sets: [{ ref: 'measure:Sales/Total', property: 'DisplayFolder', value: 'Base Measures', words: 'Filed in the folder Base Measures' }] });
  const html = renderToStaticMarkup(React.createElement(P.LineDetail, { line: measure, view, tier: { pro: true }, busy: false, onEdit() {}, onWords() {} }));
  const text = readable(html);
  assert.match(text, /The build also sets/); assert.match(text, /Filed in the folder Base Measures/);
  assert.match(html, /data-testid="sp-drop"/); assert.match(html, /data-testid="sp-rename"/);
  assert.doesNotMatch(html, /data-testid="sp-pin"/); assert.doesNotMatch(html, /data-testid="sp-words"/);
  const date = renderToStaticMarkup(React.createElement(P.LineDetail, { line: line({ item: { id: 'd1', kind: 'mark_date_table', objectRef: 'table:Date', objectName: 'Date', after: null } }), view, tier: { pro: true }, busy: false, onEdit() {} }));
  assert.doesNotMatch(date, /data-testid="sp-drop"/, 'no Drop on a line the engine cannot drop');
  assert.match(readable(date), /The plan chooses the date table/);
  const words = renderToStaticMarkup(React.createElement(P.LineDetail, { line: line({ decidedBy: 'assistant', word: 'Every sale.' }), view, tier: { pro: true }, busy: false, onEdit() {}, onWords() {} }));
  assert.match(words, /data-testid="sp-words"/); assert.match(readable(words), /Edit the words/);
  const noWriter = renderToStaticMarkup(React.createElement(P.LineDetail, { line: line({ decidedBy: 'assistant', word: 'Every sale.' }), view, tier: { pro: true }, busy: false, onEdit() {} }));
  assert.doesNotMatch(noWriter, /data-testid="sp-words"/, 'with no way to write them, no edit box is drawn');
});

check('columns: names and types under each table, folded until opened', () => {
  const cols = [
    { ref: 'column:Sales/Id', name: 'Id', dataType: 'Int64', included: true, hidden: true, key: true },
    { ref: 'column:Sales/Qty', name: 'Quantity', sourceName: 'Qty', dataType: 'Int64', included: true, hidden: false, key: false },
    { ref: 'column:Sales/Note', name: 'Note', dataType: 'String', included: false, hidden: false, key: false },
  ];
  const folded = renderToStaticMarkup(React.createElement(P.ColumnList, { columns: cols, open: false, busy: false, onToggle() {}, onEdit() {} }));
  assert.match(readable(folded), /3 columns/); assert.doesNotMatch(readable(folded), /Quantity/, 'folded: no names yet');
  const open = renderToStaticMarkup(React.createElement(P.ColumnList, { columns: cols, open: true, busy: false, onToggle() {}, onEdit() {} }));
  const text = readable(open);
  assert.match(text, /Quantity/); assert.match(text, /was Qty/); assert.match(text, /Int64 · hidden · key/); assert.match(text, /String · left out/);
  assert.match(open, /data-included="no"/); assert.match(text, /Bring back/); assert.match(text, /Leave out/);
  assert.equal((open.match(/data-testid="sp-col-rename-open"/g) || []).length, 3);
});

check('the page: each table keeps its column list closed, and the page holds one open row by line id', () => {
  const page = src('startplan.tsx');
  assert.match(page, /const \[colsOpen, setColsOpen\]/); assert.match(page, /open=\{!!colsOpen\[g\.table\]\}/, 'closed unless the person opened that table\'s list');
  assert.match(page, /openRow === lid/, 'one open row, by id');
  const v = asView(makeStartPlanState({}));
  const ids = M.linesOf(v).map((l) => l.item.id);
  assert.equal(new Set(ids).size, ids.length, 'every line has its own id');
});

check('needs: the whole text after the first sentence is the body, so the scope and the sample are read', () => {
  const need = { id: 'grant', kind: 'grant', text: 'Jev needs your permission to see the tables of Sales on wh.example.com before it decides anything. It would see the names, columns and settings of Sales, Date. It would also see counts and patterns from a sample of the rows in Sales, never the rows themselves.' };
  assert.equal(M.needTitle(need), 'Jev needs your permission to see the tables of Sales on wh.example.com before it decides anything.');
  assert.match(M.needBody(need), /names, columns and settings of Sales, Date\./); assert.match(M.needBody(need), /never the rows themselves\.$/);
});

const tablesView = (over = {}) => ({
  exists: true, digest: 'd1', sampleFacts: true, sample: { state: 'read' }, source: { database: 'Ops', server: 'wh.example.com' },
  sections: [{ id: 'tables', groups: [
    { table: 'Sales', lines: [{ item: { id: 'a', kind: 'include_table', objectName: 'Sales' }, kept: true, decidedBy: 'rule' }] },
    { table: 'Payroll', sampleOff: true, lines: [{ item: { id: 'b', kind: 'include_table', objectName: 'Payroll' }, kept: true, decidedBy: 'rule' }] },
    { table: 'Store', lines: [{ item: { id: 'c', kind: 'include_table', objectName: 'Store' }, kept: false, decidedBy: 'person' }] },
  ] }], needsYou: [], ...over,
});

check('the card: every table with its two choices, the included ones apart from the left out ones', () => {
  const card = M.grantCardOf(tablesView());
  assert.deepEqual(card.tables, ['Sales', 'Payroll']);
  assert.deepEqual(card.choices.map((c) => [c.name, c.included, c.sampleOff]), [['Sales', true, false], ['Payroll', true, true], ['Store', false, false]]);
  assert.equal(card.sample, true); assert.equal(card.sampleState, 'read'); assert.match(card.source, /Ops on wh\.example\.com/);
});

await checkAsync('the card: Allow always shows it for a model that can keep permission, even when its own permission is current', async () => {
  const opened = [];
  const view = tablesView();
  const r = await M.allowJev(view, { id: 'grant', kind: 'grant', text: 'x' }, {
    getConsent: async () => ({ current: true, appliesTo: 'model#1' }), grant: async () => { throw new Error('granted with no card'); },
    openCard: (plan, scope) => opened.push([plan, scope]),
  });
  assert.equal(r, 'card'); assert.equal(opened.length, 1); assert.equal(opened[0][1], 'model#1');
  // a model with nowhere to keep permission is sent the grant, and the engine says why it cannot
  const sent = [];
  const r2 = await M.allowJev(view, { id: 'grant', kind: 'grant', text: 'x' }, { getConsent: async () => ({ current: false, appliesTo: null }), grant: async (d) => { sent.push(d); return { ok: true }; }, openCard: () => { throw new Error('no card'); } });
  assert.deepEqual(sent, ['d1']); assert.deepEqual(r2, { ok: true });
});

await checkAsync('the card: each choice is one edit, and the grant is bound to the digest of the plan after the last choice', async () => {
  let current = tablesView();
  const edits = [], grants = [];
  const deps = {
    getConsent: async () => ({ current: true, appliesTo: 'm#1' }),
    grant: async (d) => { grants.push(d); return { granted: d }; },
    edit: async (e) => { edits.push(e); current = tablesView({ digest: 'd' + (edits.length + 1) }); return current; },
    openCard: () => {},
  };
  let card = M.grantCardFor(current, deps);
  await card.grant(); assert.deepEqual(grants, ['d1'], 'the first grant is for the plan as it was shown');
  card = await card.edit(M.tableSampleEdit('Sales', false));
  card = await card.edit(M.tableIncludeEdit('Payroll', false));
  assert.deepEqual(edits, [{ op: 'set_table_sample', target: 'Sales', value: 'off' }, { op: 'include_table', target: 'Payroll', value: 'false' }]);
  await card.grant(); assert.equal(grants.at(-1), 'd3', 'the grant carries the digest after both choices, never the one the card opened with');
});

check('the card: plain words, no dash, and the choices are named in the card', () => {
  const text = JSON.stringify(C.START_CARD);
  assert.doesNotMatch(text, /[—–]/);
  for (const k of ['tableOut', 'tablePutBack', 'tableSample', 'sampleSome', 'sampleNone', 'sampleReading']) assert.ok(C.START_CARD[k], k + ' is in the card copy');
  assert.match(C.START_CARD.sampleSome(['Sales', 'Store']), /Sales and Store/); assert.match(C.START_CARD.sampleSome(['Sales']), /never sees the rows/);
  const consent = src('consent.tsx');
  assert.match(consent, /consent-table-sample/); assert.match(consent, /consent-table-toggle/);
  assert.match(consent, /op: 'set_table_sample'/, 'the same op the assistant uses');
  assert.match(consent, /op: 'include_table'/);
});

check('the facts line counts the tables the plan builds, not the ones left out (UAT-J7-S2-012)', () => {
  const view = { ...tablesView(), words: { total: 0, open: 0 }, decide: { state: 'stable' }, grades: {}, destination: { name: 'M' }, built: true };
  const html = renderToStaticMarkup(React.createElement(P.StartPlanBody, { view, pro: true, busy: null, error: null, sentence: null, changed: new Set(),
    onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {} }));
  const facts = readable(html.slice(html.indexOf('data-testid="sp-facts"')).slice(0, 600));
  assert.match(facts, /\b2 tables/, `Sales and Payroll are built, Store was left out: ${facts}`);
});

check('the page says built once, and never that the raw tables are unscored on a built model', () => {
  assert.match(src('startplan.tsx'), /built \? M\.COPY\.gradeBuiltScored/);
  assert.doesNotMatch(M.COPY.gradeBuiltScored, /not scored/i);
});

if (failed.length) { console.error(`\n${failed.length} failed: ${failed.join('; ')}`); process.exit(1); }
console.log('startplan-uat7: all checks passed');
