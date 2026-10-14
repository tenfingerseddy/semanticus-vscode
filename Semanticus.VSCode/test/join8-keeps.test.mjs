// Join part 8, the page's half of the items found at the join. Executed against the real page code and read as the words a person sees; what only a browser sees
// (a click, a live update arriving while an editor is open) is proven by tools/uishot/startplan-drive.mjs (scenes keep-name-*, words-conflict-*) and the History driver.
//
//   K1  the person's keep of a name the naming rules flag has a control on the start plan line (and on a flagged column), drawn only where the engine flagged a name.
//   K4  an edit of an earlier session the file never held says "Not saved", in its meta line and in its open detail; it is not struck as if it were undone.
//   K9  the words editor sends the words the person STARTED from, captured when it opened, not the newer words a live update brought in.
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
  './plan-history.css': {},
  './personality/motion/history': { HistoryMotion: () => null },
  './sparkline': { ValueSparkline: () => null },
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {}, copyText: async () => true,
    onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: () => () => {}, navigateInStudio: () => {}, claimConsentAsk: async () => true,
    revealInTree: () => {},
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
});
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');
const M = load('startplan-model');
const P = load('startplan');
const HM = load('plan-history-model');
const HP = load('plan-history');

// ---------------------------------------------------------------- K1: Keep this name
const flaggedTable = {
  item: { id: 'sh-1', kind: 'include_table', objectRef: 'table:dim_client', objectName: 'dim_client', title: 'Bring in dim_client (6 of 6 columns)' },
  decidedBy: 'rule', kept: true, nameFlag: "'dim_client' would still read as a code or an abbreviation: an underscore, words run together, a name that ends in Key, ID or Cd, or capital letters that are not a known acronym.",
};
const cleanTable = { ...flaggedTable, item: { ...flaggedTable.item, id: 'sh-2', objectRef: 'table:Client', objectName: 'Client' }, nameFlag: null };
const leftOut = { ...flaggedTable, kept: false };

assert.deepEqual(M.lineControls(flaggedTable).keepName, { ref: 'table:dim_client', why: flaggedTable.nameFlag }, 'a flagged name offers Keep this name, for the table the line is about');
assert.equal(M.lineControls(cleanTable).keepName, null, 'a name nobody flagged offers nothing');
assert.equal(M.lineControls(leftOut).keepName, null, 'a table left out has no name to keep');
assert.deepEqual(M.keepNameCall('table:dim_client'), [[{ ref: 'table:dim_client', kind: 'keep_name', text: '' }], 'human'],
  'the keep is the engine\'s own keep_name word, sent from the person\'s door (so the line is the person\'s)');
assert.equal(M.COPY.keepName, 'Keep this name');

const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({ free: true }))));
const grp = view.sections.find((s) => s.id === 'tables').groups.find((g) => g.table === 'Staff');
const staffLine = grp.lines.find((l) => l.item.kind === 'include_table');
staffLine.nameFlag = 'flag reason for Staff';
const body = (extra = {}) => renderToStaticMarkup(React.createElement(P.StartPlanBody, {
  view, pro: false, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onKeepName() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {}, ...extra,
}));
{
  const html = body();
  assert.match(html, /data-testid="sp-name-flag"/, 'the row says its name is flagged');
  assert.match(readable(html), /Use a name people recognise/);
  const detail = renderToStaticMarkup(React.createElement(P.LineDetail, { line: staffLine, view, tier: { pro: false }, busy: false, onEdit() {}, onKeepName() {} }));
  assert.match(detail, /data-testid="sp-keepname"/, 'the open row has the Keep this name button');
  assert.match(readable(detail), /Keep this name/);
  assert.match(readable(detail), /flag reason for Staff/, 'and says why the name is flagged');
  const clean = renderToStaticMarkup(React.createElement(P.LineDetail, { line: { ...staffLine, nameFlag: null }, view, tier: { pro: false }, busy: false, onEdit() {}, onKeepName() {} }));
  assert.ok(!/sp-keepname/.test(clean), 'no control where nothing is flagged');
  const noHandler = renderToStaticMarkup(React.createElement(P.LineDetail, { line: staffLine, view, tier: { pro: false }, busy: false, onEdit() {} }));
  assert.ok(!/data-testid="sp-keepname"/.test(noHandler), 'and none where the page cannot send the keep');
}
{
  const cols = [
    { ref: 'column:dim_client/CLNT_NM', name: 'CLNT_NM', dataType: 'String', included: true, hidden: false, key: false, nameFlag: 'column reason' },
    { ref: 'column:dim_client/Name', name: 'Name', dataType: 'String', included: true, hidden: false, key: false },
    { ref: 'column:dim_client/OLD_CD', name: 'OLD_CD', dataType: 'String', included: false, hidden: false, key: false, nameFlag: 'left out, so not offered' },
  ];
  const html = renderToStaticMarkup(React.createElement(P.ColumnList, { columns: cols, open: true, busy: false, onToggle() {}, onEdit() {}, onKeepName() {} }));
  assert.equal((html.match(/data-testid="sp-col-keepname"/g) ?? []).length, 1, 'one Keep this name: the flagged column that is brought in');
  assert.equal((html.match(/data-testid="sp-col-flag"/g) ?? []).length, 1);
}
{
  const s = src('startplan.tsx');
  assert.match(s, /M\.keepNameCall\(ref\)/, 'the page sends the keep through the model\'s call');
  assert.match(s, /'writeStartWords', \.\.\.M\.keepNameCall/, 'it is the same RPC the words editor uses');
}

// ---------------------------------------------------------------- K9: the editor sends the words it started from
{
  const s = src('startplan.tsx');
  const detail = s.slice(s.indexOf('export function LineDetail'), s.indexOf('export function ColumnList'));
  assert.match(detail, /setWordsAtOpen\(h \? \{ \.\.\.can\.words, current: h\.base \} : can\.words\)/, 'the words are captured when the editor opens (or, with a held draft, the words it was typed over)');
  // The editor opens on the captured words (`start`) and a save names the words it holds as its base: the captured ones, or the newer ones the person chose to write over.
  assert.match(detail, /const startWords: M\.Words \| null = wordsAtOpen \?\?/, 'the editor starts from the captured words');
  assert.match(detail, /<WordsEditor start=\{startWords\}/, 'the editor is opened on them');
  assert.match(detail, /onWords\(base, t\)/, 'a save names the editor\'s own base');
  assert.ok(!/onWords\(can\.words!/.test(detail), 'and never the words the line carries by then');
  const editor = s.slice(s.indexOf('export function WordsEditor'), s.indexOf('export function LineDetail'));
  assert.match(editor, /useState<M\.Words>\(start\)/, 'the base begins as what the person started from');
  assert.match(editor, /onSave\(base, sent\)/, 'and the save sends that base, never the live words');
}

// ---------------------------------------------------------------- K4: Not saved
const row = {
  id: 'ed-1', kind: 'edit', when: '2026-10-03T10:00:00Z', by: 'you', title: 'Set description', sessionId: 's-old', revision: 3,
  deltas: [{ kind: 'update', ref: 'measure:Sales/M1', props: ['Description'], before: '', after: 'new' }],
  undoable: false, redoable: false, undone: false, beforeThisSession: true, notSaved: true,
};
assert.match(HM.metaLine(row), /^Not saved · /, 'the meta line says it was not saved');
assert.equal(HM.metaLine({ ...row, notSaved: false }).startsWith('Not saved'), false, 'a saved edit says nothing of the kind');
assert.match(HM.NOT_SAVED_SENTENCE, /never saved/);
assert.ok(!/[–—]/.test(HM.NOT_SAVED_SENTENCE), 'no dash in the words');
{
  const lines = HM.timelineLines([row], new Set());
  const props = { lines, now: Date.parse('2026-10-04T10:00:00Z'), selected: null, ask: null, busy: false, restoreFor: () => null,
    onSelect() {}, onToggle() {}, onAction() {}, onConfirmRestore() {}, onCancelRestore() {} };
  const html = renderToStaticMarkup(React.createElement(HP.HistoryTimeline, props));
  assert.match(html, /data-notsaved="true"/);
  assert.ok(!/ph-undone/.test(html), 'it is not struck through: it was not taken back, it was never saved');
  assert.match(readable(html), /Not saved · Your own edit/);
  const detail = renderToStaticMarkup(React.createElement(HP.HistoryDetail, { row, chain: null, now: Date.parse('2026-10-04T10:00:00Z'), busy: false, sessionId: 's-new', onClose() {}, onAction() {}, onObject() {}, onJump() {} }));
  assert.match(detail, /data-testid="ph-notsaved-sentence"/);
  assert.match(readable(detail), /This change was never saved, so the model on disk does not have it\./);
  assert.ok(!/Restore a saved version instead/.test(readable(detail)), 'the before-this-session sentence does not stand in for it');
}

console.log('join8-keeps: ok');
