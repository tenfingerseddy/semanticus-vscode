// The fixes for the third Plan statement check (m11/psc3), the page side. Each block names the fault it holds shut.
//   3. The start plan's After-the-build card said "0 findings stay open" beside "Best practice D": it counted the plan's own Best practice lines, which are not the
//      scan. It now says the count the preview's own best practice scan gives (the engine sends it as grades.afterBuildBestPracticeOpen), or only the letter.
//   5. The Model quality header squeezed its words at a window of 1440 with the side bars open: it wraps below the letter now.
//   6. After a model opens, Connections still showed the start form with the old sign-in error: the form goes when a model opens.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { makeStartPlanState, startPlanView, startPlanRealShape } from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({
  './startplan.css': {},
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {}, copyText: async () => true,
    onDidChange: () => () => {}, onPlanChange: () => () => {}, navigateInStudio: () => {},
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
});
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&')
  .replace(/&#x2F;/g, '/').replace(/&gt;/g, '>').replace(/&lt;/g, '<').replace(/\s+/g, ' ').trim();
const src = (...f) => readFileSync(join(root, ...f), 'utf8');
const M = load('startplan-model');
const P = load('startplan');
const H = load('connectionshub-model');
const asView = (state) => JSON.parse(JSON.stringify(startPlanView(state)));
const noop = () => {};
const handlers = { onEdit: noop, onNeed: noop, onApprove: noop, onBuild: noop, onApproveAndBuild: noop, onDiscard: noop, onRetry: noop };
const render = (view, pro = true) => renderToStaticMarkup(React.createElement(P.StartPlanBody, { view, pro, busy: null, error: null, sentence: null, changed: new Set(), ...handlers }));
const failed = [];
const check = (name, fn) => {
  try { fn(); console.log(`  [PASS] ${name}`); } catch (e) { failed.push(name); console.log(`  [FAIL] ${name}: ${e.message.split('\n')[0]}`); }
};

// ---- 3. the count beside the letter is the scan's, not the plan's own lines ---------------------------------------------
check('3. the card says the count the preview scan gives, and the letter beside it is the scan\'s', () => {
  // A plan whose own Best practice lines are all kept (0 of them would "stay open" by the old count) beside a scan that leaves 11 open and a D.
  const v = asView(makeStartPlanState({}));
  for (const sec of v.sections.filter((s) => s.id === 'bestpractice')) for (const g of sec.groups) for (const l of g.lines) l.kept = true;
  v.grades.afterBuildBestPractice = 'D';
  v.grades.afterBuildBestPracticeOpen = 11;
  const t = readable(render(v));
  assert.match(t, /11 errors and warnings stay open/, 'the count is the one the preview scan gave');
  assert.match(t, /Best practice D/, 'and the letter is the scan\'s');
  assert.doesNotMatch(t, /\b0 errors and warnings stay open/, 'never "0 errors and warnings stay open" beside a D');
  // One open finding reads as one.
  v.grades.afterBuildBestPracticeOpen = 1;
  assert.match(readable(render(v)), /1 error or warning stays open/);
  // The engine sent no count: the plan lists its own Best practice lines, but they are not the scan, so no count is claimed. The letter is said.
  delete v.grades.afterBuildBestPracticeOpen;
  const none = readable(render(v));
  assert.doesNotMatch(none, /\d+ findings? stays? open/i, 'no count of open findings is claimed when the scan gave none');
  assert.match(none, /Best practice D/);
  // The real engine's shape (no Best practice lines at all, no count sent) says the letter only, as before.
  const real = JSON.parse(JSON.stringify(startPlanRealShape({})));
  assert.doesNotMatch(readable(render(real, false)), /\d+ findings? stays? open/i);
  assert.equal(M.gradeOf(v, { pro: true }).bestPracticeOpen, null, 'the grade carries no count it was not given');
});

// ---- 5. the Model quality header wraps ---------------------------------------------------------------------------------
check('5. the Model quality header wraps below the letter instead of squeezing its words', () => {
  const bpa = src('webview', 'src', 'bpa.tsx');
  const head = /<div className="([^"]*)" data-testid="bpa-head">/.exec(bpa);
  assert.ok(head, 'the header row is named');
  assert.match(head[1], /\bflex-wrap\b/, 'the header row wraps');
  assert.doesNotMatch(head[1], /\bgap-6\b/, 'and has no single gap that would leave the wrapped row touching the one above');
  // Minimal pass (8 Oct): the title and the long explainer are gone, so the words block is the one short "N checks" line.
  const words = /<div className="([^"]*)">\s*\{\/\*[^*]*\*\/\}\s*(?:\{card\.bestPractice && <h2 className="sem-card-title">Best practice<\/h2>\}\s*)?<div className="sem-text-meta">\{card\.ruleCount\} checks<\/div>/.exec(bpa);
  assert.ok(words, 'the words block is found');
  assert.match(words[1], /min-w-\[\d+px\]/, 'the words block keeps a floor: the buttons drop to their own row first');
  assert.doesNotMatch(words[1], /\bmin-w-0\b/, 'min-w-0 let it shrink to one word a line');
});

// ---- 6. a model that opens takes the start form away ------------------------------------------------------------------
check('6. the start form goes when a model opens, and stays when nothing changed', () => {
  assert.equal(H.startFormStaysOpen(true, null, 'sess-1'), false, 'a model opened where none was: the form goes');
  assert.equal(H.startFormStaysOpen(true, 'sess-1', 'sess-2'), false, 'another model opened over the first: the form goes');
  assert.equal(H.startFormStaysOpen(true, null, null), true, 'no model, no change: the form stays');
  assert.equal(H.startFormStaysOpen(true, 'sess-1', 'sess-1'), true, 'the same model: the form stays (it was opened over it, and asks before it replaces it)');
  assert.equal(H.startFormStaysOpen(true, 'sess-1', null), true, 'a model closing is not a model opening');
  assert.equal(H.startFormStaysOpen(false, null, 'sess-1'), false, 'a form that is shut stays shut');
  const hub = src('webview', 'src', 'connectionshub.tsx');
  assert.match(hub, /setStartSqlOpen\(\(on\) => startFormStaysOpen\(on, was, openSessionId\)\)/, 'the page applies it whenever the open model changes');
});

if (failed.length) { console.error(`\n${failed.length} failed: ${failed.join('; ')}`); process.exit(1); }
console.log('psc3 page fixes passed');
