// The Query page (1.6, Kane 15 Oct: Ask your data and Queries became one page, Explore > Query, option B).
//
//   1. THE RULES. Which visuals can draw a result, run for real (query-model.ts), including the honesty cases: a split is never added
//      into a total card, and a trend with a hole in it is not offered.
//   2. THE DATA. A real result drawn by each visual kind, read back out of the markup (react-dom/server).
//   3. THE LAYOUT. One 12-column grid, rows of a 7 and a 5 card, every card and part open by default with a fold, tokens only.
//   4. THE PLACE. Old routes, places, help, shortcuts and the Learn tour all land on Query; nothing names a retired page.
//   5. PARITY. Every feature of the two old pages has a home on this one: the list below is the inventory
//      (m16 query-design parity-inventory.md, 119 features) reduced to the checks a source read can make, each tied to the code.
// There is no DOM here; the real clicks are driven by tools/uishot/query-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.replace(/\.mjs$/, '.js'),
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const M = load(join(webview, 'src', 'query-model.ts'));
const V = load(join(webview, 'src', 'visuals', 'visuals.tsx'));
const text = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

// ── 1. The rules ────────────────────────────────────────────────────────────────────────────────────
const cat = (name, distinct) => ({ name, isDate: false, distinct });
const date = (name, distinct, ordered = true) => ({ name, isDate: true, ordered, distinct });
const fits = (shape) => M.queryVisualFits(shape);
assert.deepEqual(fits({ dims: [], measures: 1, rows: 1 }).fits, ['total', 'table'], 'one number: a total card');
assert.equal(fits({ dims: [cat('Region', 3)], measures: 1, rows: 3 }).fits.includes('total'), false, 'a split is NEVER added into a total card: a ratio added up is a wrong number shown as right');
assert.deepEqual(fits({ dims: [cat('Region', 3)], measures: 1, rows: 3 }).fits, ['bars', 'table']);
assert.equal(fits({ dims: [cat('Region', 80)], measures: 1, rows: 80 }).pick, 'table', 'past 50 members, bars are not offered');
assert.equal(fits({ dims: [date('Month', 12)], measures: 1, rows: 12 }).pick, 'line');
assert.equal(fits({ dims: [date('Month', 12, false)], measures: 1, rows: 12 }).fits.includes('line'), false, 'a date nothing orders is a category, never a line');
const pair = fits({ dims: [cat('Segment', 5)], measures: 2, rows: 5, sameUnit: true });
assert.equal(pair.pick, 'dumbbell');
assert.deepEqual(pair.fits, ['dumbbell', 'variance', 'bullet', 'slope', 'table']);
assert.equal(fits({ dims: [cat('Segment', 5)], measures: 2, rows: 5, sameUnit: true, negatives: true }).fits.includes('bullet'), false, 'a bullet draws from zero, so a negative rules it out');
assert.equal(fits({ dims: [cat('Segment', 2)], measures: 2, rows: 2, sameUnit: true }).fits.includes('dumbbell'), false, 'a dumbbell needs three members');
assert.deepEqual(fits({ dims: [cat('Segment', 5)], measures: 2, rows: 5, sameUnit: false }).fits, ['table'], 'two numbers in different units are never drawn against each other');
const trends = fits({ dims: [cat('Region', 4), date('Month', 8)], measures: 1, rows: 32 });
assert.deepEqual(trends.fits, ['multiples', 'table']);
assert.equal(fits({ dims: [cat('Region', 8), date('Month', 8)], measures: 1, rows: 64 }).fits.includes('sparktable'), true);
assert.deepEqual(fits({ dims: [cat('Region', 4), date('Month', 4)], measures: 1, rows: 16 }).fits, ['groups', 'table'], 'under six periods there is no trend to draw, so grouped bars lead (18 Oct, worst 4)');
assert.deepEqual(fits({ dims: [], measures: 0, rows: 3 }).fits, ['table']);
assert.deepEqual(fits({ dims: [cat('Region', 3)], measures: 1, rows: 0 }).fits, ['table']);
assert.equal(M.QUERY_KINDS.every((k) => k in V.KIND_NAME), true, 'every offered kind is in the shared visual set');

// ── 2. The data ─────────────────────────────────────────────────────────────────────────────────────
const config = (rows, cols, values) => ({ rows, cols, values, filters: [] });
const f = (name, kind = 'column') => ({ kind, ref: kind === 'measure' ? `[${name}]` : `'T'[${name}]`, name, table: 'T' });
const money = '\\$#,0';
const region = {
  columns: [{ name: 'Region' }, { name: 'Net Sales', format: money }, { name: 'Last Year', format: money }],
  rows: [['Coast', 4120000, 4000000], ['Central', 3010000, 3010000], ['West', 2980500, 3000000], ['North', 1204000, 1190000]],
  rowCount: 4, truncated: false, elapsedMs: 5,
};
const cfg2 = config([f('Region')], [], [f('Net Sales', 'measure'), f('Last Year', 'measure')]);
const bars = M.visualData('bars', region, config([f('Region')], [], [f('Net Sales', 'measure')]));
assert.equal(bars.rows.length, 4);
assert.equal(bars.vLabel, 'Net Sales');
assert.equal(bars.format(4120000), '$4,120,000', 'the visuals show a number in the measure\'s own format');
const html = (kind, d, w = 700) => renderToStaticMarkup(V.drawVisual(kind, d, w));
assert.match(text(html('bars', bars)), /Coast.*\$4,120,000/, 'bars carry the model format');
const dumb = M.visualData('dumbbell', region, cfg2);
assert.equal(dumb.v2Label, 'Last Year');
assert.match(text(html('dumbbell', dumb)), /West.*-\$19,500/, 'a dumbbell says the signed difference');
assert.equal(M.visualData('multiples', region, cfg2), null, 'a result with no dates cannot be small multiples');
const one = { columns: [{ name: 'Net Sales', format: money }, { name: 'Last Year', format: money }], rows: [[2646905, 2441800]], rowCount: 1, truncated: false, elapsedMs: 3 };
const total = M.visualData('total', one, config([], [], [f('Net Sales', 'measure'), f('Last Year', 'measure')]));
assert.equal(total.total.value, 2646905);
assert.equal(total.total.prior, 2441800);
assert.match(text(html('total', total, 900)), /\$2,646,905.*\+\$205,105/, 'the total card says the number and how it moved');
assert.equal(M.visualData('total', region, config([f('Region')], [], [f('Net Sales', 'measure')])), null, 'a split result has no total card');
// An amount beside a quantity (Total return amount PY, Total return quantity PY) shows both numbers and no Difference or %: the gap between
// them states nothing ("+25920.1%", verifier v3). The same two amounts keep their gap.
const mixed = { columns: [{ name: 'Region' }, { name: 'Total return amount', format: money }, { name: 'Total return quantity', format: '#,0' }], rows: [['Coast', 732466.8, 2815]], rowCount: 1, truncated: false, elapsedMs: 2 };
const mixedData = M.visualData('table', mixed, config([f('Region')], [], [f('Total return amount', 'measure'), f('Total return quantity', 'measure')]));
assert.equal(mixedData.noGap, true);
const mixedHtml = text(html('table', mixedData));
assert.match(mixedHtml, /Total return quantity/);
assert.doesNotMatch(mixedHtml, /Difference|%/, 'an amount and a quantity have no gap to show');
assert.match(text(html('table', dumb)), /Difference/, 'two amounts keep their gap');

// A trend with a hole in it is not drawn: a missing month would read as zero.
const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul'];
const flat = (skip) => ({
  columns: [{ name: 'Date' }, { name: 'Region' }, { name: 'Units' }],
  rows: ['East', 'West'].flatMap((r) => months.map((m, i) => [`2025-${String(i + 1).padStart(2, '0')}-01`, r, 10 + i])).filter((_, i) => i !== skip),
  rowCount: 14, truncated: false, elapsedMs: 4,
});
const trendCfg = config([f('Date'), f('Region')], [], [f('Units', 'measure')]);
const whole = M.visualData('multiples', flat(-1), trendCfg);
assert.ok(whole, 'a full set of months draws');
assert.deepEqual(whole.rows.map((r) => r.label), ['East', 'West']);
assert.equal(whole.rows[0].series.length, 7);
assert.equal(M.visualData('multiples', flat(3), trendCfg), null, 'a member missing a month is not drawn as if it were zero');
assert.match(text(html('multiples', whole, 700)), /East.*West/);

// The headline for a result nothing wrote a sentence for.
assert.equal(M.headlineOf(null, null, false), 'Nothing has run yet. Ask a question, choose fields, or write a query, then run it.');
assert.match(M.headlineOf(region, config([f('Region')], [], [f('Net Sales', 'measure')]), false), /^Net Sales by Region: 4 rows\.$/);
assert.equal(M.headlineOf(one, config([], [], [f('Net Sales', 'measure')]), false), 'Net Sales is $2,646,905.');
assert.match(M.headlineOf({ ...region, rows: [], rowCount: 0 }, cfg2, false), /No rows came back\. Nothing was added up, so no total is shown in its place\./, 'sales in 2008 never shows the all-years total in its place (K21)');
assert.equal(M.headlineOf(region, cfg2, true), 'Building, checking and running the query.');

// ── 3. The layout (the notebook, Kane 16 Oct: option C) ────────────────────────────────────────────────────
const page = src('query.tsx');
const nbSrc = src('query-notebook.tsx');
const css = src('query.css');
const order = ['<Head q={q} nb={nb} />', '<TimingLine nb={nb} />', '<Book q={q} nb={nb}'];
order.reduce((at, needle) => { const i = page.indexOf(needle); assert.ok(i > at, `${needle} follows the part before it`); return i; }, -1);
assert.match(page, /<section className="sem-card qn-book" data-testid="notebook"/, 'the notebook is ONE card; each question is a row in it');
assert.match(nbSrc, /export const TABS: Tab\[\] = \['answer', 'dax', 'table', 'speed', 'steps', 'compare', 'save'\]/, 'every tool opens on demand, in one place, as a tab of the open row');
assert.doesNotMatch(page, /sem-span-\d|qv-qcard|fold-(read|checks|visual|result|explain|compare|steps|history|save)/, 'no stack of cards and no card folds: nothing is on screen until it is asked for');
for (const id of ['answer', 'dax-card', 'result', 'speed-card', 'time-card', 'compare-card', 'steps-card', 'save-card', 'explain-card']) assert.match(page, new RegExp(`data-testid="${id}"`), `${id} is a view of the open row`);
assert.match(page, /\{nb\.tab === 'explain' && <ExplainBody q=\{q\} \/>\}/, 'Explain opens when a number is pressed, as a view of the same row');
// The box asks in plain words only (Kane 16 Oct): nothing it says invites DAX. DAX is the row's own view.
const ask = src('queriesask.tsx');
assert.match(ask, /placeholder="Ask about your data, like net sales by store region for 2025"/, 'the box prompt asks about the data, in words');
assert.doesNotMatch(ask + page + nbSrc, /paste\s+DAX|or DAX|write DAX in|type DAX/i, 'no text on the page asks people to paste DAX');
assert.match(page, /data-testid=\{`view-\$\{t\}`\}/, 'each view is one press');
assert.match(page, /data-testid="mode-build"[\s\S]*data-testid="mode-dax"/, 'the DAX view holds the fields and the query as written, and runs it');
assert.match(nbSrc, /history-open[\s\S]*history-new/, 'the clock opens the full history, and New query opens a blank row');
assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b|rgba?\(|font-size:\s*\d/, 'the page types no colour and no font size');
assert.doesNotMatch(page + nbSrc + src('query-notebook-model.ts') + src('query-parts.tsx') + src('query-model.ts') + css, /—/, 'no em dash anywhere on the page');
assert.doesNotMatch(page + nbSrc, /one click/i, 'no "one click" in the copy');
assert.doesNotMatch(page + nbSrc + css, /\bMap\b/, 'never "Map"');

// ── 4. The place ────────────────────────────────────────────────────────────────────────────────────
const route = src('route.ts');
assert.match(route, /askdata: \{ tool: 'query' \}/); assert.match(route, /daxlab: \{ tool: 'query' \}/);
assert.match(src('App.tsx'), /\{ id: 'query', label: 'Query' \}/);
assert.doesNotMatch(src('App.tsx'), /label: 'Ask your data'|label: 'Queries'/, 'the strip names neither old page');
assert.match(src('shortcuts.tsx'), /\{ key: 'l', tab: 'query', label: 'Query' \}/, 'Ctrl+Alt+L opens the page');
const places = JSON.parse(readFileSync(join(webview, 'src', 'studio', 'places.json'), 'utf8')).places;
for (const id of ['explore/ask', 'edit/queries']) assert.deepEqual([places.find((p) => p.id === id).tool, places.find((p) => p.id === id).label], ['query', 'Query'], `${id} still resolves, and lands on Query`);
const help = src('help.tsx');
assert.doesNotMatch(help, /"askdata": \{|"daxlab": \{|Edit > Queries|"title": "Ask your data"/, 'Help names neither old page');
assert.match(help, /"query": \{\n    "title": "Query"/);
for (const f2 of ['ask.json', 'index.json', 'edit.json']) {
  const learn = readFileSync(join(root, 'assets', 'learn', f2), 'utf8');
  assert.doesNotMatch(learn, /Ask your data|Open Queries|Queries runs|open Queries/, `the Learn chapter ${f2} names neither old page`);
}

// ── 5. Parity: the 119 features, by group, each with its home ───────────────────────────────────────────
const all = [page, nbSrc, src('daxlab.tsx'), src('queriesask.tsx'), src('askdata.tsx'), src('querywells.tsx'), src('fieldspanel.tsx'), src('query-parts.tsx')].join('\n');
const PARITY = [
  // Ask (A1 to A50)
  ['A1 Q1', 'the words box is Pro, with the Free line', /AskFreeLine/],
  ['A2 Q3', 'Enter asks in words', /Enter to ask Jev/],
  ['A3', 'words carried in from the Plan fill the box once', /navWords\.nonce/],
  ['A4 Q67', 'words, parts and DAX kept per model', /lab\.ask\.parts\./],
  ['A5 A6 Q4', 'Read as chips', /<AskChips ask=\{q\.askBox\}/],
  ['A7 to A14', 'chip menus: swap, find, sort, remove, filters, top N, add', /export function ChipMenu/],
  ['A15 A17', 'the finder says when nothing matches', /askdata-finder-empty/],
  ['A18', 'not run since you came back, with Run', /This question has not run since you came back\./],
  ['A19 A20 Q12', 'Run, Stop, the stopped line', /The question was stopped\./],
  ['Q13 Q14', 'Ctrl+Enter runs on the page', /e\.key === 'Enter' && conn\?\.connected && !q\.idle/],
  ['A21 Q32 Q33', 'an assistant answer and its query, with Back to my query', /Asked by your assistant[\s\S]*Back to my (query|fields)/],
  ['A22 A23 A24', 'statement, assumption, the other reading', /export function AssumptionLine/],
  ['A25', 'the Checks part, in Steps', /data-testid="checks"/],
  ['A26', 'unsaved edits are not in the results', /<QueryStalenessChip \/>/],
  ['A27 A28 Q7', 'refusals, unsure lines, the note', /role="alert"/],
  ['A29', 'Allow and the consent card', /<AllowButton onAllowed=\{\(\) => ask\.askWords\(\)\} \/>/],
  ['A30 A31 Q44', 'the error with its plain notice, and Sign in again', /SIGN_IN_AGAIN/],
  ['A32 Q42 Q43', 'needs a model with data, the connect prompt', /<ConnectBar hint="Connect a model that holds data to run queries\." \/>/],
  ['A33 to A36 Q37 to Q40', 'the visual, why, back to the pick, and every other fit beside the answer (UAT F35)', /data-testid="answer-more"[\s\S]*data-testid="pick-back"[\s\S]*data-testid=\{`fit-\$\{k\}`\}/],
  ['A37 Q45', 'the result grid with its filter and sort', /<ResultGrid /],
  ['Q46', 'model formats on results', /withMeasureFormats/],
  ['A38 Q47 Q50', 'Explain this number, pressed or right-clicked', /onCellClick=\{q\.chartConfig/],
  ['A39', 'the DAX is shown beside the answer', /data-testid="dax-card"/],
  ['A41 A42 Q9 Q10 Q11', 'Save as a check: SQL, trusted answer, a number from the answer, Check saved, Open Validation', /data-testid="save-sql"[\s\S]*data-testid="save-trusted"[\s\S]*data-testid="open-validation"/],
  ['A43 to A46', 'Asked before: who, when, outcome, one row for repeats, press to run again', /data-testid="history-list"[\s\S]*OUTCOME_WORD\[r\.outcome\][\s\S]*Assistant, /],
  ['A47 Q68', 'a model switch clears the page', /setAll\(\[\]\); setReady\(false\); setNote\(null\); setSelected\(new Set\(\)\); void reload\(\);[^\n]*\[key\]/],
  ['A48', 'the Learn tour examples', /<ExampleAnswers example=\{q\.learn\.tour\.example\} \/>/],
  ['A49 A50', 'screen reader lines; calculation groups left out of the pickers', /aria-live="polite"/],
  // Queries (Q1 to Q69)
  ['Q2 Q8', 'Words, Build, DAX; Put back my query', /Put back my query/],
  ['Q5 Q6', 'a query changed by hand; Back to my fields', /Build from the parts again/],
  ['Q15 Q16', 'Build runs on every change; starting fields', /wellsTouched\.current/],
  ['Q17 to Q19', 'Fields: search, tables, insert, in use', /expanded/],
  ['Q20 to Q24', 'the four wells', /<QueryWells /],
  ['Q26 Q27 Q52 Q53', 'Compare a rewrite and its last verdict', /Last comparison:/],
  ['Q28 to Q30', 'the DAX editor', /<DaxEditor ref=\{q\.editorRef\}/],
  ['Q34 to Q36', 'Result and Speed side by side, Reset', /data-testid="query-reset"/],
  ['Q41', 'the rows and ms line', /rowAnnouncement\(res\.rowCount, res\.truncated\)/],
  ['Q51', 'Calculation steps', /data-testid="log-measures"/],
  ['Q55 to Q60', 'Measure speed and every option, the shared endpoint confirm, headline numbers', /data-testid="speed-start"[\s\S]*data-testid="how-quick"[\s\S]*data-testid="how-profile"[\s\S]*data-testid="clear-first"/],
  ['Q61', 'where the time went: split, totals, heaviest scans', /Heaviest scans, in order/],
  ['Q62 Q63', 'cold and warm tables, every run', /data-testid="coldwarm"[\s\S]*data-testid="every-run"/],
  ['Q64', 'server steps, logical and physical, with the cold record', /data-testid="plan-logical"[\s\S]*data-testid="plan-physical"/],
  ['Q65 Q66', 'speed follows the query on screen; the busy mark', /useDaxLabBusy/],
  // The five losses the verifier found (fix round, 15 Oct): each is back, with its own home in the code.
  ['A31', 'Sign in again on a question that failed to sign in', /askSignIn[\s\S]*data-testid="signin-again"/],
  ['A6', 'the chips stay after a refused question so the refused part can be fixed', /chipsKept/],
  ['Q51', 'each logged step shows its own rows', /data-testid="step-row"[\s\S]*<ResultGrid columns=\{e\.columns/],
  ['Q53', 'Compare names the question\'s filters', /filterWordsShown: \(q\.qParts\?\.filters \?\? \[\]\)\.map\(filterWords\)/],
  ['Q64', 'a plan step keeps its detail as a hover title', /title=\{n\.detail \|\| undefined\}/],
  ['Q30', 'opening a measure moves the bar to DAX', /setMode\('query'\); setEntryRaw\('dax'\); setQuery\(seedText\)/],
  ['Jev', 'the number names go to the chart pick', /shape\.measures, shape\.measureNames,/],
];
for (const [ids, what, re] of PARITY) assert.match(all, re, `${ids}: ${what}`);
assert.ok(PARITY.length >= 40, 'the parity list covers the groups of the inventory');
// Three features are "not needed" on purpose, and the reason is in the notes: the DAX is beside the answer, every tool is a card, and the
// Optimize page is reached from the Plan only.
assert.doesNotMatch(all, />Open in Queries</, 'A40: Open in Queries is gone, because the DAX is beside the answer');

console.log('query view: the rules, the data, the layout, the place and the parity list passed');
