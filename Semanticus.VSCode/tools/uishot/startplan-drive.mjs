// Browser-driven proof of the start plan (lane sf-studio, L12): option A of the spec-first start, on the Plan page, on both
// sides of the Free and Pro line.
//
// It serves THIS worktree's built bundle, drives the real React page with real clicks, MEASURES what landed into
// results.json next to its PNGs, and RECORDS every call the page made, with its arguments. A scene that looks right while
// saying the wrong thing fails on its `expect`. The engine is played by fixtures/startplan-fixture.mjs, a MOCK of the
// contract's verbs (the real doors are lane sf-doors'), inlined into the page.
//
// Scenes: the start form; the grant (Pro, before Jev may look); Jev deciding; the plan ready with its Needs you band; a
// line opened; an answer that moves only its part; Approve only (then Build); an approval made stale by the assistant's
// words; Incomplete; the built plan (Built, the grades, Discard only). And two journeys, Pro and Free, from the
// form to the build, which COUNT what the person pressed, what the assistant wrote and what the grades did.
// The wiring step's scenes (2 Oct): Not now then Allow; a line held when its table is left out; the sample being read and
// not read; a provisional grade; the marks at 900 px; two links between one pair of tables drawn apart; the open row's
// source name. The real engine's run (2 Oct): Allow opens the start plan's permission card, which gives the model's own
// permission (setConsent) and THEN the grant, because the mock now refuses a grant alone as the engine does; the Free plan
// holds no Default line; a line that asks nothing carries no "asks" mark; the Changed filter adds batches up; the fiscal year.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/startplan-drive.mjs
//   ONLY=ready-pro,journey-free node tools/uishot/startplan-drive.mjs      just those scenes
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'startplan');
mkdirSync(OUT, { recursive: true });
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.'); process.exit(1); }

const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'startplan-fixture.mjs'), 'utf8').replace(/^export /gm, '');

// The engine stub, installed before studio.js makes its first call (it rides in on the harness setter, as every driver's
// seed does). `opts` picks the starting state of the plan.
const seed = (opts) => `
${FIXTURE_SRC}
window.__spCalls = [];
window.__sp = makeStartPlanState(${JSON.stringify(opts)});
window.__spNone = ${JSON.stringify(!!opts.none)};
window.__spWords = (n) => { startPlanAssistantWords(window.__sp, n); window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }); return true; };
window.__spExternalWords = (text) => { startPlanExternalWords(window.__sp, text); window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }); return true; };
// The assistant's words land but the page is not told yet: a Save made in that moment carries the words it started from, and the engine refuses it (join 8, K9; UAT-J8-A-002).
window.__spExternalWordsQuiet = (text) => { startPlanExternalWords(window.__sp, text); return true; };
// A snapshot of the editor and the rows, taken between steps; it rides in the call record so a scene can read how the page stood at that moment (the draft scenes).
window.__spSnap = (label) => { const q = (x) => document.querySelector(x); window.__spCalls.push({ call: '@snap', params: [], label,
  renameEditor: !!q('[data-testid="sp-rename-editor"]'), renameInput: (q('[data-testid="sp-rename-input"]') || {}).value ?? null, renameMoved: !!q('[data-testid="sp-rename-moved"]'),
  editor: !!q('[data-testid="sp-words-editor"]'), input: (q('[data-testid="sp-words-input"]') || {}).value ?? null,
  keptAfterBuild: [...document.querySelectorAll('[data-testid="sp-kept-after-build"] [data-testid="sp-kept-row"]')].map((e) => e.textContent.trim()), keptGone: [...document.querySelectorAll('[data-testid="sp-kept-gone"] [data-testid="sp-kept-row"]')].map((e) => e.textContent.trim()), keptGoneCopy: document.querySelectorAll('[data-testid="sp-kept-gone"] [data-testid="sp-kept-copy"]').length, keptGoneDiscard: document.querySelectorAll('[data-testid="sp-kept-gone"] [data-testid="sp-kept-discard"]').length, flagged: [...document.querySelectorAll('.sp-row[data-draft="yes"]')].map((r) => r.dataset.testid), flagText: [...document.querySelectorAll('[data-testid="sp-draft-flag"]')].map((e) => e.textContent.trim()),
  openRows: [...document.querySelectorAll('.sp-row[aria-expanded="true"]')].map((r) => r.dataset.testid),
  stale: (q('[data-testid="sp-stale"]') || {}).textContent ?? null, saveDisabled: q('[data-testid="sp-words-save"]')?.disabled ?? null, away: (q('[data-testid="sp-words-away"]') || {}).textContent ?? null,
  said: (q('[data-testid="sp-words-said"]') || {}).textContent ?? null, moved: !!q('[data-testid="sp-words-moved"]'), onPlan: !!q('[data-testid="sp-page"]'), notConnected: /not connected to its engine/.test(document.body.innerText) }); return true; };
// A second line the person can edit the words of (the fixture has one): Codes, written by the assistant.
window.__spMakeCodesEditable = () => { const l = window.__sp.lines.find((x) => x.item.id === 'w-codes'); l.item.kind = 'set_description'; l.item.after = 'What the codes mean.'; l.word = 'What the codes mean.'; l.decidedBy = 'assistant'; window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }); return true; };
window.__spState = (patch) => { Object.assign(window.__sp, patch); window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }); return true; };
const __note = (name, params) => window.__spCalls.push({ call: name, params: JSON.parse(JSON.stringify(params || [])), digestNow: startPlanView(window.__sp).digest, approvedNow: !!window.__sp.approval });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('sessionInfo', () => ({ sessionId: 'startplan-copy', modelName: 'Contoso Consulting', source: startPlanView(window.__sp).destination?.modelPath, liveBound: false, tables: 0, measures: 0, hasUnsavedEdits: false }));
    v.setResponse('getStartPlan', () => (window.__spNone ? { exists: false } : startPlanView(window.__sp)));
    v.setResponse('startModelFromSql', (p) => { __note('startModelFromSql', p); window.__spNone = false; return startPlanView(window.__sp); });
    v.setResponse('editStartPlan', (p) => { __note('editStartPlan', p); return startPlanEdit(window.__sp, p[0]); });
    v.setResponse('grantStartDisclosure', (p) => { __note('grantStartDisclosure', p); return startPlanGrant(window.__sp, p[0]); });
    // The model's own permission, which the grant needs first (LocalEngine.GrantStartDisclosureAsync).
    v.setResponse('getConsent', () => startPlanConsent(window.__sp));
    v.setResponse('setConsent', (p) => { __note('setConsent', p); return startPlanSetConsent(window.__sp); });
    v.setResponse('getDecisionPoints', []);
    v.setResponse('approveStartPlan', (p) => { __note('approveStartPlan', p); return startPlanApprove(window.__sp, p[0]); });
    // The engine answers a build with the BUILT plan, and every read after it too (view.built), never with "no plan".
    v.setResponse('buildStartPlan', (p) => { __note('buildStartPlan', p); return startPlanBuild(window.__sp, p[0]); });
    v.setResponse('discardStartPlan', (p) => { __note('discardStartPlan', p); window.__spNone = true; return { exists: false }; });
    v.setResponse('writeStartWords', (p) => { __note('writeStartWords', p); return startPlanWriteWords(window.__sp, p[0], p[1]); });
  },
});
(() => {})()`;

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// What a scene reads off the page: every field is a measurement of the DOM, not of the fixture.
const MEASURE = `(() => {
  const q = (s) => document.querySelector(s);
  const text = (e) => (e ? e.textContent.replace(/\\s+/g, ' ').trim() : null);
  const all = (s) => [...document.querySelectorAll(s)];
  return {
    onPlan: !!q('[data-testid="sp-page"]'), onForm: !!q('[data-testid="sp-form"]'),
    pro: q('[data-testid="sp-page"]')?.dataset.pro ?? null, approval: q('[data-testid="sp-page"]')?.dataset.approval ?? null,
    decide: q('[data-testid="sp-page"]')?.dataset.decide ?? null, builtState: q('[data-testid="sp-page"]')?.dataset.built ?? null,
    footState: q('[data-testid="sp-foot"] .sp-foot-t')?.dataset.foot ?? null, gradeNote: text(q('[data-testid="sp-grade-provisional"]')),
    gradeProvisional: q('[data-testid="sp-grade"]')?.dataset.provisional ?? null, discard: !!q('[data-testid="sp-discard"]'),
    heldRows: all('.sp-row[data-held="yes"]').map((r) => ({ id: r.dataset.testid.replace('sp-row-', ''), state: r.querySelector('.sp-state')?.textContent ?? null, sub: text(r.querySelector('.sp-row-t > span')) })),
    detailSource: text(q('[data-testid="sp-detail-source"]')), detail: text(q('.sp-detail')),
    // A mark is SEEN when it has a box: every Needs you line and every row must carry one, at any width.
    needMarks: all('[data-testid^="sp-need-"][data-kind]').map((e) => ({ kind: e.dataset.kind, asks: e.querySelectorAll('button').length > 0,
      marked: [...e.querySelectorAll('.sp-who')].some((w) => w.getClientRects().length > 0 && w.getBoundingClientRect().width > 0) })),
    card: text(q('[data-testid="consent-card-start"]')), bp: text(q('[data-testid="sp-bp"]')), freeNoteText: text(q('[data-testid="sp-free-note"]')),
    rowMarks: all('.sp-row').map((r) => [...r.querySelectorAll('.sp-who')].some((w) => w.getClientRects().length > 0 && w.getBoundingClientRect().width > 0)),
    drawingPaths: all('[data-testid="sp-link-drawing"] path[data-active]').map((p) => p.getAttribute('d')),
    head: text(q('.sp-head')), freeNote: text(q('[data-testid="sp-free-note"]')), decideNote: text(q('[data-testid="sp-decide-note"]')),
    decideTone: q('[data-testid="sp-decide-note"]')?.dataset.tone ?? null, approvalNote: text(q('[data-testid="sp-approval"]')),
    error: text(q('[data-testid="sp-error"]')), said: text(q('[data-testid="sp-said"]')), planSaid: text(q('[data-testid="plan-said"]')), built: text(q('[data-testid="plan-built"]')),
    strip: text(q('[data-testid="sp-strip"]')), grade: text(q('[data-testid="sp-grade"]')), facts: text(q('[data-testid="sp-facts"]')),
    needs: all('[data-testid^="sp-need-"][data-kind]').map((e) => ({ id: e.dataset.testid.replace('sp-need-', ''), kind: e.dataset.kind, text: text(e), options: [...e.querySelectorAll('button')].map((b) => b.textContent.trim()) })),
    needsBand: text(q('[data-testid="sp-needs"] .sp-needs-h')),
    filters: all('[data-testid^="sp-filter-"]').map((b) => b.textContent.trim()),
    sections: all('[data-section]').map((e) => ({ id: e.dataset.section, title: text(e.querySelector('.sp-sec-h b')), rows: e.querySelectorAll('.sp-row').length })),
    rows: all('.sp-row').map((r) => ({ id: r.dataset.testid.replace('sp-row-', ''), who: r.dataset.who, kept: r.dataset.kept, state: r.dataset.state ?? text(r.querySelector('.sp-state')), title: text(r.querySelector('.sp-row-t b')) })),
    changedRows: all('.sp-row.sp-changed').map((r) => r.dataset.testid.replace('sp-row-', '')),
    nameFlags: all('[data-testid="sp-name-flag"]').length, keepNameButtons: all('[data-testid="sp-keepname"]').length, colKeepNameButtons: all('[data-testid="sp-col-keepname"]').length, colFlags: all('[data-testid="sp-col-flag"]').length,
    wordsInput: (q('[data-testid="sp-words-input"]') || {}).value ?? null, wordsEditorOpen: !!q('[data-testid="sp-words-editor"]'),
    wordsMoved: text(q('[data-testid="sp-words-moved"]')), wordsTheirs: text(q('[data-testid="sp-words-theirs"]')), wordsSaveDisabled: q('[data-testid="sp-words-save"]')?.disabled ?? null,
    wordsChoice: ['keepmine', 'usenew'].map((k) => text(q('[data-testid="sp-words-' + k + '"]'))), wordsSaid: text(q('[data-testid="sp-words-said"]')),
    wordsMovedInView: (() => { const e = q('[data-testid="sp-words-moved"]'); if (!e) return null; const r = e.getBoundingClientRect(); return r.top >= 0 && r.bottom <= window.innerHeight; })(),
    descAfter: text(q('[data-testid="sp-row-w-desc-staff"] .sp-row-t > span')),
    sampleBoxes: all('[data-testid="sp-sample"]').map((l) => ({ table: l.dataset.table, allowed: l.dataset.allowed, checked: l.querySelector('input')?.checked, words: text(l) })), keepNameWhy: text(q('[data-testid="sp-keepname-why"]')),
    drawing: !!q('[data-testid="sp-link-drawing"]'), drawingNodes: all('[data-testid="sp-link-drawing"] [data-node]').map((g) => g.dataset.node),
    drawingEdges: all('[data-testid="sp-link-drawing"] path[data-active]').map((p) => p.dataset.active),
    buttons: all('[data-testid^="sp-approve"],[data-testid^="sp-build"]').map((b) => ({ id: b.dataset.testid, text: b.textContent.trim(), disabled: b.disabled })),
    foot: text(q('[data-testid="sp-foot"]')), legend: text(q('.sp-legend')), formText: text(q('[data-testid="sp-form"]')),
    allText: document.body.innerText,
    docWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
    calls: window.__spCalls || [],
  };
})()`;

const results = [];
const click = (page, id) => page.evaluate((s) => { const el = document.querySelector(`[data-testid="${s}"]`); if (!el) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true; }, id);
const clickText = (page, scope, label) => page.evaluate((sc, t) => {
  const el = [...document.querySelectorAll(`${sc} button`)].find((b) => b.textContent.replace(/\s+/g, ' ').trim().startsWith(t));
  if (!el) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true;
}, scope, label);
const typeInto = async (page, testId, value) => { const sel = `[data-testid="${testId}"]`; await page.focus(sel); await page.keyboard.type(value, { delay: 4 }); };
const has = (cond, message) => (cond ? [] : [message]);
const THEMES_SHOT = ['dark', 'light'];

async function open(width, opts, query, height = 1100) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width, height, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(opts));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(1500);
  return { page, errors };
}

// One scene: open the page, take the steps, measure, check the screen, and shoot each view in both themes.
// `screen: false` skips the screen check for a scene whose page is NOT this lane's (the wizard it hands back to).
async function scene(name, { opts = {}, free = false, width = 1280, steps = [], views = [{}], expect = null, query = null, wait: settle = 600, height, screen = true } = {}) {
  if (only && !only.includes(name)) return;
  const q = query ?? `conn=1&live=1&tab=plan${free ? '' : '&tier=pro'}`;
  const { page, errors } = await open(width, { free, ...opts }, q, height);
  const notes = [];
  for (const s of steps) {
    let ok;
    if (s.click) ok = await click(page, s.click);
    else if (s.text) ok = await clickText(page, s.scope || 'body', s.text);
    else if (s.type) { ok = !!(await page.$(`[data-testid="${s.type}"]`)); if (ok) await typeInto(page, s.type, s.value); }
    else if (s.eval) ok = await page.evaluate(s.eval);
    else ok = false;
    if (!ok) notes.push(`step not found: ${JSON.stringify(s)}`);
    await wait(s.settle ?? settle);
  }
  if (screen) await checkScreen(page, `startplan-drive:${name}`);
  await page.setViewport({ width, height: height ?? 1100, deviceScaleFactor: 1 });
  await wait(300);
  const measured = await page.evaluate(MEASURE);
  const shots = {};
  for (const v of views) {
    if (v.scrollTo) {
      const found = await page.evaluate((id) => { const el = document.querySelector(`[data-testid="${id}"]`); if (!el) return false; el.scrollIntoView({ block: 'start' }); return true; }, v.scrollTo);
      if (!found) notes.push(`scroll target not found: ${v.scrollTo}`);
    } else await page.evaluate(() => { document.querySelectorAll('main, .studio-root').forEach((e) => { e.scrollTop = 0; }); window.scrollTo(0, 0); });
    await wait(250);
    for (const theme of THEMES_SHOT) {
      await applyTheme(page, theme); await wait(250);
      const file = join(OUT, `${name}${v.suffix ? '-' + v.suffix : ''}-${width}-${theme}.png`);
      await page.screenshot({ path: file, fullPage: false });
      shots[`${v.suffix || 'top'}-${theme}`] = file;
    }
  }
  if (expect) for (const f of expect(measured)) notes.push(f);
  if (measured.docWidth > measured.viewportWidth + 1) notes.push(`the page scrolls sideways: ${measured.docWidth} > ${measured.viewportWidth}`);
  const dashes = (measured.allText.match(/[–—]/g) || []).length;
  if (dashes) notes.push(`${dashes} em or en dash(es) on screen`);
  if (/one[- ]click/i.test(measured.allText)) notes.push('"one click" on screen');
  results.push({ scene: name, free, width, measured: { ...measured, allText: undefined }, shots, notes, errors });
  console.log(`${notes.length || errors.length ? '!' : 'ok'} ${name}@${width}${notes.length ? ': ' + notes.join(' | ') : ''}${errors.length ? ' ERR ' + errors.slice(0, 2).join(' | ') : ''}`);
  await page.close();
  return measured;
}

const callNames = (m) => m.calls.map((c) => c.call);
const proPlan = { opts: {} };
const SECTION_TITLES = ['Tables and columns', 'Links and dates', 'Measures', 'Words for Copilot', 'Best practice'];

for (const width of [1280, 900]) {
  // ---- the start form: a saved endpoint first, a name, an empty folder ------------------------------------
  await scene('form-pro', {
    width, opts: { none: true }, query: 'conn=1&live=1&tier=pro&spec=empty&coldNav=spec:start:sql',
    expect: (m) => [
      ...has(m.onForm, 'the start form is on the page for an empty model'),
      ...has(/Start a model from a SQL source/.test(m.formText), 'form title'),
      ...has(/Contoso/.test(m.formText), 'a saved endpoint is listed'),
      ...has(/empty folder/i.test(m.formText), 'it says the folder must be empty'),
      ...has(/never sees rows|No rows are copied/i.test(m.formText), 'it says no rows are copied'),
      ...has(/Jev decides/.test(m.formText), 'Pro is told what Jev does'),
    ],
  });
  await scene('form-free', {
    width, free: true, opts: { none: true, free: true }, query: 'conn=1&live=1&spec=empty&coldNav=spec:start:sql',
    expect: (m) => [...has(m.onForm, 'the form'), ...has(/your own assistant writes the words/.test(m.formText), 'Free is told who writes the words'), ...has(!/Jev decides/.test(m.formText), 'Free is not told Jev decides')],
  });

  // ---- a model that already has tables keeps the wizard's two-step path (the SQL step), never the start plan -------------
  if (width === 1280) await scene('wizard-kept-pro', {
    width, opts: { none: true }, query: 'conn=1&live=1&tier=pro&coldNav=spec:start:sql', views: [{ suffix: 'wizard' }], screen: false,
    expect: (m) => [...has(!m.onForm && !m.onPlan, 'the start plan is not drawn over a model that has tables'), ...has(/Use a SQL endpoint from Connections/.test(m.allText), 'the wizard\'s SQL step is')],
  });

  // ---- the grant: the first thing on Pro, naming the source and what Jev never sees (S2) --------------------
  await scene('grant-pro', {
    width, opts: { granted: false, decide: 'waiting_grant' },
    expect: (m) => [
      ...has(m.needs.some((n) => n.kind === 'grant' && /Jev needs your permission to see the tables of ContosoOps/.test(n.text) && /never sees rows/.test(n.text)), 'the grant names the source and says no rows'),
      ...has(m.needs.find((n) => n.kind === 'grant')?.options.join('|').startsWith('Allow'), 'the first option grants'),
      ...has(m.decide === 'waiting_grant' && /waiting for you/.test(m.decideNote), `waiting note "${m.decideNote}"`),
      ...has(m.buttons.every((b) => b.disabled), `no button is live while Jev waits: ${JSON.stringify(m.buttons)}`),
      ...has(!m.needs.some((n) => n.kind === 'jev'), 'no Jev question before the grant'),
    ],
  });

  // ---- Jev deciding: progress, and nothing can be approved yet ----------------------------------------------
  await scene('deciding-pro', {
    width, opts: { decide: 'deciding', progress: [{ family: 'measures', asked: 41, answered: 38, unusable: 3, remaining: 34 }, { family: 'columnFormat', asked: 20, answered: 20, unusable: 0, remaining: 50 }] },
    expect: (m) => [...has(/Jev is deciding\. Round 2, 61 of 145 looked at/.test(m.decideNote), `progress "${m.decideNote}"`), ...has(m.buttons.every((b) => b.disabled), 'nothing is live while Jev decides')],
  });

  // ---- the plan, ready: Needs you above five sections, the drawing at the top of Links and dates -----------
  // Also: every Needs you line and every row keeps its mark at this width (they dropped out at 900), and the two links
  // from Invoices to Date (IssueDate on, DueDate kept off) are two lines, not one on top of the other.
  await scene('ready-pro', {
    width, ...proPlan, views: [{}, { suffix: 'links', scrollTo: 'sp-section-links' }, { suffix: 'words', scrollTo: 'sp-section-words' }, { suffix: 'foot', scrollTo: 'sp-foot' }],
    expect: (m) => [
      ...has(JSON.stringify(m.sections.map((s) => s.title)) === JSON.stringify(SECTION_TITLES), `sections ${JSON.stringify(m.sections.map((s) => s.title))}`),
      ...has(m.needs.length === 5 && m.needs.filter((n) => n.kind === 'jev').length === 3, `needs ${JSON.stringify(m.needs.map((n) => n.id))}`),
      ...has(/Jev left 3 changes for you to decide/.test(m.needsBand), `band "${m.needsBand}"`),
      ...has(m.drawing && m.drawingNodes.length === 6 && m.drawingEdges.length === 8, `drawing nodes ${m.drawingNodes} edges ${m.drawingEdges}`),
      ...has(m.drawingEdges.filter((e) => e === 'off').length === 2, 'two links drawn as kept off'),
      ...has(/35/.test(m.grade) && /92/.test(m.grade), `grade "${m.grade}"`),
      ...has(m.approval === 'none', 'nothing is approved'),
      ...has(m.buttons.some((b) => /Approve and build/.test(b.text) && !b.disabled), 'Approve and build is live'),
      ...has(!/Free plan/.test(m.allText), 'Pro does not read the Free note'),
      ...has(m.rows.some((r) => r.id === 't-sum-budget' && /Left out until you answer/.test(r.state)), 'an unsure Jev line is left out until answered'),
      ...has(m.needMarks.length > 0 && m.needMarks.every((n) => n.marked === n.asks), `a Needs you line that asks shows who asks, and one that asks nothing shows no mark: ${JSON.stringify(m.needMarks)}`),
      ...has(/2\s*best practice problems stay open/.test(m.bp ?? '') && /Best practice B/.test(m.bp ?? ''), `best practice after the build: "${m.bp}"`),
      ...has(m.rowMarks.length > 0 && m.rowMarks.every(Boolean), `every row shows who decided it (${m.rowMarks.filter((x) => !x).length} without)`),
      ...has(new Set(m.drawingPaths).size === m.drawingPaths.length, `no two links drawn on one path: ${JSON.stringify(m.drawingPaths)}`),
      ...has(/A/.test(m.grade) && m.gradeProvisional === 'no' && !m.gradeNote, `a settled grade is the letter, not provisional: "${m.grade}"`),
      ...has(m.footState === 'waiting', `foot ${m.footState}`),
    ],
  });
  await scene('ready-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan', views: [{}, { suffix: 'foot', scrollTo: 'sp-foot' }],
    expect: (m) => [
      ...has(m.pro === 'no' && /^Free plan\./.test(m.freeNote), `free note "${m.freeNote}"`),
      ...has(!m.needs.some((n) => n.kind === 'jev' || n.kind === 'grant'), 'Free is asked no Jev question'),
      ...has(m.decideNote === null, 'Free shows no Jev progress'),
      ...has(!m.filters.some((f) => /^(Default|Jev)/.test(f)), `no Jev filter and no empty Default filter: ${m.filters}`),
      ...has(!m.rows.some((r) => /Default, no Jev/.test(r.state)) && !/marked Default|plain defaults/.test(m.allText), 'the real Free plan has no Default line, and nothing promises one'),
      ...has(!/Jev leans|Jev asks|Jev needs your permission|Jev decided/.test(m.allText), 'Free never says Jev decided or asks'),
      ...has(m.buttons.some((b) => /Approve and build/.test(b.text) && !b.disabled), 'Free builds too'),
      ...has(/78/.test(m.grade), `grade "${m.grade}"`),
    ],
  });

  // ---- links guessed from names (uat7-links): a source that declares no key. Each guess is a line the person can drop or keep; the engine builds only the kept ones.
  // Four states in one list: on with the rows read, on from names alone, off because another table answers to the same column, and left out by the person.
  // Open the first, press Drop, and the page must send the engine's own edit (drop_line on that link's own id), then show the line as left out.
  await scene('guessed-links-free', {
    width, free: true, opts: { free: true, guessedLinks: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-relationship:g-staff' }],
    views: [{ suffix: 'open', scrollTo: 'sp-row-relationship:g-staff' }],
    expect: (m) => [
      ...has(/Linked Timesheets\[StaffKey\] to Staff\[StaffKey\]: same name, and StaffKey is unique in Staff \(from the sample\)/.test(m.allText), 'the sentence names both ends, how the names matched and what makes the other end a key'),
      ...has(/\(rows not read\)/.test(m.allText), 'a guess made from names alone says the rows were not read'),
      ...has(/A guess from the names: the source declares no key for these two tables/.test(m.allText), 'the open line says it is a guess'),
      ...has(/Drop/.test(m.detail ?? ''), 'Drop is offered on a kept guess'),
    ],
  });
  await scene('guessed-links-drop-free', {
    width, free: true, opts: { free: true, guessedLinks: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-relationship:g-staff' }, { click: 'sp-drop' }],
    views: [{ suffix: 'dropped', scrollTo: 'sp-row-relationship:g-staff' }],
    expect: (m) => [
      ...has(m.calls.some((c) => c.call === 'editStartPlan' && c.params[0]?.op === 'drop_line' && c.params[0]?.target === 'relationship:g-staff' && c.params[1] === 'human'),
        `Drop sent drop_line for that link: ${JSON.stringify(m.calls.map((c) => c.params))}`),
      ...has(m.rows.some((r) => /Linked Timesheets\[StaffKey\]/.test(r.title ?? r.text ?? '') && /Left out/i.test(r.state ?? '')) || /Left out/i.test(m.allText), 'the dropped guess reads as left out'),
    ],
  });

  // ---- a name the naming rules flag (join 8, K1): the row says so, the open row and the column list offer Keep this name, and pressing it sends the engine's own keep_name from the person's door.
  await scene('keep-name-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan', steps: [{ click: 'sp-row-t-in-Staff' }],
    views: [{ suffix: 'open', scrollTo: 'sp-row-t-in-Staff' }],
    expect: (m) => [
      ...has(m.nameFlags === 2, `the two flagged names (the Staff table and Total tax) say so on their rows: ${m.nameFlags}`),
      ...has(m.keepNameButtons === 1, `one open row, one Keep this name: ${m.keepNameButtons}`),
      ...has(/would still read as a code/.test(m.keepNameWhy ?? '') && /question closes/.test(m.keepNameWhy ?? ''), `the open row says why and what the keep does: "${m.keepNameWhy}"`),
    ],
  });
  await scene('keep-name-pressed-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan', steps: [{ click: 'sp-row-t-in-Staff' }, { click: 'sp-keepname' }],
    views: [{ suffix: 'kept', scrollTo: 'sp-row-t-in-Staff' }],
    expect: (m) => [
      ...has(m.calls.some((c) => c.call === 'writeStartWords' && c.params[0]?.[0]?.ref === 'table:Staff' && c.params[0]?.[0]?.kind === 'keep_name' && c.params[1] === 'human'),
        `Keep this name sent keep_name for the table from the person's door: ${JSON.stringify(m.calls.map((c) => c.params))}`),
      ...has(m.nameFlags === 1, `the kept name is not flagged any more, the other still is: ${m.nameFlags}`),
      ...has(m.keepNameButtons === 0, 'no Keep this name left on the kept row'),
    ],
  });
  await scene('keep-name-columns-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ eval: `(() => { const t = document.querySelector('[data-group="Staff"] [data-testid="sp-columns-toggle"]'); if (!t) return false; t.scrollIntoView({ block: 'center' }); t.click(); return true; })()` }],
    views: [{ suffix: 'columns', scrollTo: 'sp-group-tables-Staff' }],
    expect: (m) => [
      ...has(m.colFlags === 1 && m.colKeepNameButtons === 1, `one flagged column, one Keep this name: flags ${m.colFlags}, buttons ${m.colKeepNameButtons}`),
    ],
  });

  // ---- the words editor meets newer words (UAT-J8-A-002, U1). Nothing typed is lost: the editor stays open on the draft, the newer words stand beside it, and the choice is the one every other editor offers.
  // 1. The assistant's newer words arrive live while the person has typed: the banner shows at once, in view, and Save waits.
  await scene('words-conflict-live-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "window.__spExternalWords('The assistant wrote newer words.')" }],
    views: [{ suffix: 'banner', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => [
      ...has(m.wordsEditorOpen, 'the editor is still open'),
      ...has(/And contractors\./.test(m.wordsInput ?? '') && /People who work at the firm\./.test(m.wordsInput ?? ''), `the draft is still in the box: "${m.wordsInput}"`),
      ...has(/changed while you were editing them/.test(m.wordsMoved ?? '') && /Your words are still here, and nothing was saved yet/.test(m.wordsMoved ?? ''), `the editor says the words moved: "${m.wordsMoved}"`),
      ...has(/The assistant wrote newer words\./.test(m.wordsTheirs ?? ''), `the newer words are beside the draft: "${m.wordsTheirs}"`),
      ...has(JSON.stringify(m.wordsChoice) === JSON.stringify(['Keep mine', 'Use the new one']), `the choice is Keep mine / Use the new one: ${JSON.stringify(m.wordsChoice)}`),
      ...has(m.wordsSaveDisabled === true, 'Save waits for the choice'),
      ...has(m.wordsMovedInView === true, 'the banner is on the screen, not at the top of the page'),
      ...has(!m.calls.some((c) => c.call === 'writeStartWords'), 'nothing was sent'),
    ],
  });
  // 2. The words land but the page is not told yet; the person presses Save. The engine refuses (K9: Save names the words the person started from). The editor stays open with the draft and shows the newer words, and no warning is left at the top of the page.
  await scene('words-conflict-save-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "window.__spExternalWordsQuiet('The assistant wrote newer words.')" }, { click: 'sp-words-save' }],
    views: [{ suffix: 'saved', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const w = m.calls.find((c) => c.call === 'writeStartWords');
      return [
        ...has(!!w, 'Save sent the words'),
        ...has(w?.params[0]?.[0]?.expected === 'People who work at the firm.', `Save named the words the person started from, not the newer ones: "${w?.params[0]?.[0]?.expected}"`),
        ...has(/And contractors\./.test(w?.params[0]?.[0]?.text ?? '') && /People who work at the firm\./.test(w?.params[0]?.[0]?.text ?? ''), `and sent what was typed with the words it started from: "${w?.params[0]?.[0]?.text}"`),
        ...has(m.wordsEditorOpen, 'the editor did not close'),
        ...has(/And contractors\./.test(m.wordsInput ?? '') && /People who work at the firm\./.test(m.wordsInput ?? ''), `the draft is kept: "${m.wordsInput}"`),
        ...has(/The assistant wrote newer words\./.test(m.wordsTheirs ?? ''), `the newer words are shown beside it: "${m.wordsTheirs}"`),
        ...has(JSON.stringify(m.wordsChoice) === JSON.stringify(['Keep mine', 'Use the new one']), `Keep mine / Use the new one: ${JSON.stringify(m.wordsChoice)}`),
        ...has(m.wordsMovedInView === true, 'in view'),
        ...has(!m.error, `no warning left at the top of the page: "${m.error}"`),
        ...has(/What changesThe assistant wrote newer words\./.test(m.detail ?? ''), `the line holds the newer words, not the draft: "${m.detail}"`),
      ];
    },
  });
  // 3. Keep mine: the draft stays, the next Save names the newer words as the ones it replaces and writes the draft over them.
  await scene('words-conflict-keepmine-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "window.__spExternalWordsQuiet('The assistant wrote newer words.')" }, { click: 'sp-words-save' }, { click: 'sp-words-keepmine' }, { click: 'sp-words-save' }],
    views: [{ suffix: 'kept', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const ws = m.calls.filter((c) => c.call === 'writeStartWords');
      return [
        ...has(ws.length === 2, `two saves were sent, the refused one and the chosen one: ${ws.length}`),
        ...has(ws[1]?.params[0]?.[0]?.expected === 'The assistant wrote newer words.', `the second names the newer words as the ones it replaces: "${ws[1]?.params[0]?.[0]?.expected}"`),
        ...has(/And contractors\./.test(ws[1]?.params[0]?.[0]?.text ?? ''), `and carries the draft: "${ws[1]?.params[0]?.[0]?.text}"`),
        ...has(!m.wordsEditorOpen, 'the editor closes once the engine took the words'),
        ...has(/What changes.*And contractors\./.test(m.detail ?? ''), `the line holds the person's words: "${m.detail}"`),
      ];
    },
  });
  // 3b. The engine REFUSES the words for a reason that is not a conflict (a name that clashes, a rule on the words): the editor stays open with what was typed, and the reason is said beside it.
  await scene('words-refused-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' [refuse]' }, { click: 'sp-words-save' }],
    views: [{ suffix: 'refused', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => [
      ...has(m.calls.some((c) => c.call === 'writeStartWords'), 'Save sent the words'),
      ...has(m.wordsEditorOpen, 'the editor stays open'),
      ...has(/\[refuse\]/.test(m.wordsInput ?? '') && /People who work at the firm\./.test(m.wordsInput ?? ''), `with what was typed: "${m.wordsInput}"`),
      ...has(/These words cannot be saved/.test(m.wordsSaid ?? ''), `and the reason beside it: "${m.wordsSaid}"`),
      ...has(m.wordsMoved === null, 'it is not told as a conflict'),
      ...has(!m.error, `nothing is left at the top of the page: "${m.error}"`),
    ],
  });
  // 4. Use the new one: the box takes the newer words, and Save has nothing to save.
  await scene('words-conflict-usenew-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "window.__spExternalWords('The assistant wrote newer words.')" }, { click: 'sp-words-usenew' }],
    views: [{ suffix: 'usenew', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => [
      ...has(m.wordsEditorOpen && m.wordsMoved === null, 'the editor is open and the banner is gone'),
      ...has(m.wordsInput === 'The assistant wrote newer words.', `the box holds the newer words: "${m.wordsInput}"`),
      ...has(m.wordsSaveDisabled === true, 'Save has nothing to save'),
      ...has(!m.calls.some((c) => c.call === 'writeStartWords'), 'nothing was sent'),
    ],
  });

  // ---- typed words outlive the editor (join 8 fix round 2, D1): a row switch, a restarted engine and a down engine never take them.
  // The caret starts at the front of the box, so the typed words come first: the draft is the typed words and the words it began from.
  const typedOk = (v) => /And contractors\./.test(v ?? '') && /People who work at the firm\./.test(v ?? '');
  const SNAP = (label) => ({ eval: `window.__spSnap(${JSON.stringify(label)})` });
  const snaps = (m) => Object.fromEntries(m.calls.filter((c) => c.call === '@snap').map((c) => [c.label, c]));
  const DOWN = `(() => { const h = window.__uishotHarness; window.__orig = { session: h.response('sessionInfo') }; for (const k of ['getStartPlan', 'sessionInfo', 'writeStartWords', 'editStartPlan']) h.setResponse(k, () => ({ __uishotError: 'Engine not connected.' })); h.dispatch({ type: 'engineState', connected: false }); return true; })()`;
  const BACK = `(() => { const h = window.__uishotHarness; const o = window.__orig; h.setResponse('sessionInfo', () => o.session);
    h.setResponse('getStartPlan', () => (window.__spNone ? { exists: false } : startPlanView(window.__sp)));
    h.setResponse('writeStartWords', (p) => { __note('writeStartWords', p); return startPlanWriteWords(window.__sp, p[0], p[1]); });
    h.setResponse('editStartPlan', (p) => { __note('editStartPlan', p); return startPlanEdit(window.__sp, p[0]); });
    h.dispatch({ type: 'engineState', connected: true }); h.dispatch({ type: 'reconnected' }); return true; })()`;
  // (a) open another row: the first row keeps its draft, says so, and reopens on it.
  await scene('draft-row-switch-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ eval: 'window.__spMakeCodesEditable()' }, { click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { click: 'sp-row-w-codes' }, SNAP('other row open'), { click: 'sp-row-w-desc-staff' }, SNAP('first row back')],
    views: [{ suffix: 'back', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const a = snaps(m)['other row open'], b = snaps(m)['first row back'];
      return [
        ...has(a && a.openRows.length === 1 && a.openRows[0] === 'sp-row-w-codes', `one row is open, the other: ${JSON.stringify(a?.openRows)}`),
        ...has(a && !a.editor, 'the first editor is gone with its row'),
        ...has(a && a.flagged.includes('sp-row-w-desc-staff') && a.flagText.every((t) => /Draft not saved/.test(t)), `the first row says its draft is not saved: ${JSON.stringify(a?.flagged)} ${JSON.stringify(a?.flagText)}`),
        ...has(b && b.editor && typedOk(b.input ?? ''), `reopened, the editor holds the draft: "${b?.input}"`),
        ...has(b && b.saveDisabled === false, 'and Save is open'),
        ...has(!m.calls.some((c) => c.call === 'writeStartWords'), 'nothing was sent'),
      ];
    },
  });
  // The person's own Discard ends a draft, and nothing else does.
  await scene('draft-discard-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' }, SNAP('typed'), { click: 'sp-words-cancel' }, SNAP('discarded')],
    views: [{ suffix: 'discarded', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const a = snaps(m).typed, b = snaps(m).discarded;
      return [
        ...has(a && a.editor, 'the editor was open on the draft'),
        ...has(b && !b.editor && b.flagged.length === 0, `Discard closes the editor and leaves no draft: ${JSON.stringify(b?.flagged)}`),
        ...has(!m.calls.some((c) => c.call === 'writeStartWords'), 'nothing was sent'),
      ];
    },
  });
  // (b) the engine restarts: the model reopens under a new session id, every tab remounts, and the draft is found by the model's file.
  await scene('draft-restart-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "(() => { const h = window.__uishotHarness; const s = h.response('sessionInfo'); h.setResponse('sessionInfo', () => ({ ...s, sessionId: 'uishot-restarted' })); h.dispatch({ type: 'reconnected' }); return true; })()", settle: 2500 },
      SNAP('after restart'), { click: 'sp-row-w-desc-staff' }, SNAP('reopened')],
    views: [{ suffix: 'reopened', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const a = snaps(m)['after restart'], b = snaps(m).reopened;
      return [
        ...has(a && a.onPlan, 'the plan is back on the page after the restart'),
        ...has(a && a.flagged.includes('sp-row-w-desc-staff'), `the row says its draft is not saved: ${JSON.stringify(a?.flagged)}`),
        ...has(b && b.editor && typedOk(b.input ?? ''), `the row opens on the draft: "${b?.input}"`),
        ...has(!m.calls.some((c) => c.call === 'writeStartWords'), 'nothing was sent'),
      ];
    },
  });
  // A new name typed into the Rename editor outlives it too (join 11): a restarted engine, then the row, opens on the typed name; Save is still the person's.
  await scene('draft-rename-restart-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-t-in-Staff' }, { click: 'sp-rename' }, { type: 'sp-rename-input', value: ' Members' },
      { eval: "(() => { const h = window.__uishotHarness; const s = h.response('sessionInfo'); h.setResponse('sessionInfo', () => ({ ...s, sessionId: 'uishot-restarted' })); h.dispatch({ type: 'reconnected' }); return true; })()", settle: 2500 },
      SNAP('after restart'), { click: 'sp-row-t-in-Staff' }, SNAP('reopened')],
    views: [{ suffix: 'reopened', scrollTo: 'sp-row-t-in-Staff' }],
    expect: (m) => {
      const a = snaps(m)['after restart'], b = snaps(m).reopened;
      return [
        ...has(a && a.onPlan, 'the plan is back on the page after the restart'),
        ...has(a && a.flagged.includes('sp-row-t-in-Staff'), `the row says its draft is not saved: ${JSON.stringify(a?.flagged)}`),
        ...has(b && b.renameEditor && /Members$/.test(b.renameInput ?? ''), `the row opens on the typed name: "${b?.renameInput}"`),
        ...has(b && !b.renameMoved, 'nothing moved: no choice to make'),
        ...has(!m.calls.some((c) => c.call === 'editStartPlan'), 'nothing was sent'),
      ];
    },
  });
  // A build must not leave typed words behind unasked (join 11, A3): the rows that hold them are named and the person chooses. Build without them builds, and the words end with the plan (A4).
  await scene('draft-build-ask-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' }, { click: 'sp-approve-head', settle: 900 }, { click: 'sp-build-head', settle: 600 },
      { eval: "window.__spCalls.push({ call: '@ask', params: [], shown: !!document.querySelector('[data-testid=\"sp-unsaved-ask\"]'), text: (document.querySelector('[data-testid=\"sp-unsaved-ask\"]') || {}).textContent ?? null })" },
      { click: 'sp-unsaved-save-first', settle: 600 }, SNAP('after save first'),
      { click: 'sp-build-head', settle: 600 }, { click: 'sp-unsaved-build-without', settle: 1500 }, SNAP('after build')],
    views: [{ suffix: 'built' }],
    expect: (m) => {
      const ask = m.calls.find((c) => c.call === '@ask'), a = snaps(m)['after save first'], b = snaps(m)['after build'];
      const builds = m.calls.filter((c) => c.call === 'buildStartPlan');
      return [
        ...has(ask && ask.shown && /Staff|Bring in|description/i.test(ask.text ?? ''), `the build asks first and names the row: "${ask?.text}"`),
        ...has(a && a.openRows.length === 1 && a.editor && typedOk(a.input ?? ''), `Save them first goes back to the row, open on its words: ${JSON.stringify(a?.openRows)} "${a?.input}"`),
        ...has(builds.length === 1, `exactly one build was sent, after Build without them: ${builds.length}`),
        ...has(!m.calls.some((c) => c.call === 'writeStartWords'), 'the unsaved words were not written'),
        // F14 (drafts-sol): Build without them does NOT delete the held words. The built page lists them (Copy, Discard) and the row still says its draft is not saved.
        ...has(b && b.keptAfterBuild.length === 1 && /contractors/.test(b.keptAfterBuild[0]), `the built plan lists the kept words: ${JSON.stringify(b?.keptAfterBuild)}`),
      ];
    },
  });
  // D3 (drafts-sol round 2): the assistant discards the plan while words typed on one of its rows are unsaved. They are NOT deleted: the Plan lists them with Copy and Discard, and only that Discard ends them.
  await scene('draft-gone-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "window.__spNone = true; window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }); true", settle: 1800 }, SNAP('after discard')],
    views: [{ suffix: 'listed' }],
    expect: (m) => {
      const a = snaps(m)['after discard'];
      return [
        ...has(a && !a.onPlan && a.keptGone.length === 1 && /contractors/.test(a.keptGone[0]), `the Plan lists the words kept for the plan the assistant discarded: ${JSON.stringify(a?.keptGone)}`),
        ...has(a && a.keptGoneCopy === 1 && a.keptGoneDiscard === 1, `with Copy and Discard: ${a?.keptGoneCopy}/${a?.keptGoneDiscard}`),
        ...has(!m.calls.some((c) => c.call === 'writeStartWords' || c.call === 'discardStartPlan'), 'nothing was written and nothing was discarded by the page'),
      ];
    },
  });
  await scene('draft-gone-discard-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: "window.__spNone = true; window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }); true", settle: 1800 },
      { click: 'sp-kept-discard', settle: 600 }, SNAP('after kept discard')],
    views: [{ suffix: 'ended' }],
    expect: (m) => { const b = snaps(m)['after kept discard']; return [...has(b && b.keptGone.length === 0, `the list's own Discard ends them: ${JSON.stringify(b?.keptGone)}`)]; },
  });
  await scene('draft-build-ask-shown-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' }, { click: 'sp-approve-head', settle: 900 }, { click: 'sp-build-head', settle: 600 }],
    views: [{ suffix: 'ask', scrollTo: 'sp-unsaved-ask' }],
    expect: (m) => [...has(m.calls.filter((c) => c.call === 'buildStartPlan').length === 0, 'nothing was built while the question is open')],
  });
  // (c) the engine is down while the editor is open: the plan stays on screen, read-only, with the draft; Save waits; when it is back Save runs the same expected-words check.
  await scene('draft-engine-down-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: DOWN, settle: 1200 }, SNAP('down'),
      // The engine is back (the same session): the page reads the plan again, the notice goes, and Save runs.
      { eval: BACK, settle: 1500 }, SNAP('back'), { click: 'sp-words-save', settle: 1200 }, SNAP('saved')],
    views: [{ suffix: 'down', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const a = snaps(m).down;
      return [
        ...has(a && a.onPlan && a.editor && !a.notConnected, `the plan stays on screen with its editor, not the not-connected page: ${JSON.stringify({ onPlan: a?.onPlan, editor: a?.editor, notConnected: a?.notConnected })}`),
        ...has(a && typedOk(a.input ?? ''), `the draft is in the box: "${a?.input}"`),
        ...has(a && (a.stale ?? '').startsWith('Semanticus is not answering; your typed words are kept.') && /Restart engine/.test(a.stale), `the page says so in plain words: "${a?.stale}"`),
        ...has(a && a.saveDisabled === true && /Save waits until Semanticus answers/.test(a.away ?? ''), `Save waits and says why: ${a?.saveDisabled} "${a?.away}"`),
        ...has(m.calls.filter((c) => c.call === 'writeStartWords').length === 1, `exactly one Save went out, after the engine was back: ${m.calls.filter((c) => c.call === 'writeStartWords').length}`),
        ...has(snaps(m).back && snaps(m).back.editor && snaps(m).back.stale === null && snaps(m).back.saveDisabled === false && typedOk(snaps(m).back.input ?? ''), `the engine is back: the notice is gone, the draft is in the box, Save is open: ${JSON.stringify(snaps(m).back)}`),
        ...has((() => { const w = m.calls.find((c) => c.call === 'writeStartWords'); return !!w && w.params[0]?.[0]?.expected === 'People who work at the firm.' && /And contractors\./.test(w.params[0]?.[0]?.text ?? ''); })(), 'then Save sends the draft with the words it started from (the same expected-words check)'),
        ...has(snaps(m).saved && !snaps(m).saved.editor && snaps(m).saved.flagged.length === 0, 'a Save the engine took closes the editor and leaves no draft'),
      ];
    },
  });
  // (c2) Save pressed in the moment the engine goes (the page has not been told): the refusal says it in plain words, and the editor, the draft and the plan stay.
  await scene('draft-save-engine-gone-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    steps: [{ click: 'sp-row-w-desc-staff' }, { click: 'sp-words' }, { type: 'sp-words-input', value: ' And contractors.' },
      { eval: `(() => { const h = window.__uishotHarness; for (const k of ['getStartPlan', 'sessionInfo', 'writeStartWords']) h.setResponse(k, () => ({ __uishotError: 'Engine not connected.' })); return true; })()` },
      { click: 'sp-words-save', settle: 1500 }, SNAP('saved while gone')],
    views: [{ suffix: 'gone', scrollTo: 'sp-row-w-desc-staff' }],
    expect: (m) => {
      const a = snaps(m)['saved while gone'];
      return [
        ...has(a && a.onPlan && a.editor, `the plan and the editor stay: ${JSON.stringify({ onPlan: a?.onPlan, editor: a?.editor })}`),
        ...has(a && /And contractors\./.test(a.input ?? ''), `the draft is in the box: "${a?.input}"`),
        ...has(a && /typed words are kept\./.test((a.said ?? '') + (a.away ?? '')) && !/Engine not connected/.test((a.said ?? '') + (a.away ?? '') + (a.stale ?? '')), `the editor says it in plain words, not the raw refusal: said "${a?.said}", away "${a?.away}"`),
        ...has(a && /Semanticus is not answering; your typed words are kept\./.test(a.stale ?? ''), `and so does the page: "${a?.stale}"`),
      ];
    },
  });

  // ---- the per-table sample choice after the grant (UAT-J8-B, U2): on each table, the grant dialog's words; one press sends the assistant's own set_table_sample, from the person's door.
  await scene('sample-control-pro', {
    width, ...proPlan,
    steps: [{ eval: `(() => { const b = document.querySelector('[data-testid="sp-sample"][data-table="Staff"] input'); if (!b) return false; b.scrollIntoView({ block: 'center' }); b.click(); return true; })()` }],
    views: [{ suffix: 'refused', scrollTo: 'sp-group-tables-Staff' }],
    expect: (m) => [
      ...has(m.sampleBoxes.length >= 2, `each included table carries the choice: ${m.sampleBoxes.length}`),
      ...has(m.sampleBoxes.every((b) => /Send counts and patterns from its sample/.test(b.words)), 'in the dialog\'s words'),
      ...has(m.calls.some((c) => c.call === 'editStartPlan' && c.params[0]?.op === 'set_table_sample' && c.params[0]?.target === 'Staff' && c.params[0]?.value === 'off' && c.params[1] === 'human'),
        `one press sent set_table_sample off for Staff from the person's door: ${JSON.stringify(m.calls.map((c) => c.params))}`),
      ...has(m.sampleBoxes.find((b) => b.table === 'Staff')?.checked === false && m.sampleBoxes.filter((b) => b.table !== 'Staff').every((b) => b.checked), 'only Staff reads refused'),
    ],
  });
  await scene('sample-control-before-grant-pro', {
    width, opts: { granted: false, decide: 'waiting_grant' },
    expect: (m) => [...has(m.sampleBoxes.length === 0, `before the grant the dialog has the choice, the page has none: ${m.sampleBoxes.length}`)],
  });
  await scene('sample-control-free', {
    width, free: true, opts: { free: true }, query: 'conn=1&live=1&tab=plan',
    expect: (m) => [...has(m.sampleBoxes.length === 0, `Free has no Jev to refuse a sample to: ${m.sampleBoxes.length}`)],
  });

  // ---- the Needs you band says what the build does with each kind of question (UAT-J7-S2-004, U7): a pick the rule made is built; a line Jev left out is not.
  await scene('needs-pick-lead-pro', {
    width, opts: { pickNeed: true }, steps: [{ text: 'No total', scope: '[data-testid="sp-need-need-budgets"]' }, { text: 'Hide from Copilot', scope: '[data-testid="sp-need-need-rates"]' }, { text: 'Copilot can read it', scope: '[data-testid="sp-need-need-left"]' }],
    views: [{ suffix: 'band', scrollTo: 'sp-needs' }],
    expect: (m) => [
      ...has(m.needs.some((n) => n.kind === 'unsure_pick'), `the pick question is in the band: ${m.needs.map((n) => n.kind)}`),
      ...has(/builds the rule's pick for it\. Answer to change it\./.test(m.needsBand ?? ''), `the band says the plan builds the pick: "${m.needsBand}"`),
      ...has(!/stays out/.test(m.needsBand ?? ''), `and not that it stays out: "${m.needsBand}"`),
    ],
  });

  // ---- a row opened: why, the source, who decides it, Keep and Drop and a pin -------------------------------
  await scene('row-open-pro', {
    width, ...proPlan, steps: [{ click: 'sp-row-t-sum-rate' }], views: [{ suffix: 'open', scrollTo: 'sp-row-t-sum-rate' }],
    expect: (m) => [...has(/Adding \$270/.test(m.allText), 'the reason is shown'), ...has(/Jev, the decision service/.test(m.allText), 'it says who decides this'), ...has(/Keep this value/.test(m.allText), 'the pin is offered'),
      ...has(m.detailSource === 'fact_timesheet, column Rate', `the source name is the source's: "${m.detailSource}"`),
      ...has(!/\b(model|column|table|measure|relationship):\S/.test(m.detail ?? ''), `no internal ref in the open row: "${m.detail}"`)],
  });

  // A time measure the view makes from its base (join 6): Drop is offered, Pin is not.
  await scene('row-open-time-pro', {
    width, ...proPlan, steps: [{ click: 'sp-row-m-hours-ytd' }], views: [{ suffix: 'open', scrollTo: 'sp-row-m-hours-ytd' }],
    expect: (m) => [...has(/Drop/.test(m.allText), 'Drop is offered on a time measure'), ...has(!m.buttons.some((b) => /Keep this value|Pin/.test(b.text)), 'no Pin on a time measure')],
  });

  // ---- an answer moves only its part --------------------------------------------------------------------------
  await scene('answer-pro', {
    width, ...proPlan, steps: [{ text: 'No total', scope: '[data-testid="sp-need-need-budgets"]' }, { click: 'sp-filter-changed' }],
    views: [{ suffix: 'changed' }],
    expect: (m) => [
      ...has(callNames(m).join() === 'editStartPlan', `calls ${callNames(m)}`),
      ...has(JSON.stringify(m.calls[0]?.params) === JSON.stringify([{ op: 'answer', target: 'need-budgets', value: 'No total' }, 'human']), `edit ${JSON.stringify(m.calls[0]?.params)}`),
      ...has(JSON.stringify([...m.changedRows].sort()) === JSON.stringify(['m-bud-amount', 'm-bud-hours', 't-sum-budget']), `changed rows ${m.changedRows}`),
      ...has(m.needs.length === 4 && !m.needs.some((n) => n.id === 'need-budgets'), `the answered question left the band: ${m.needs.map((n) => n.id)}`),
      ...has(/Changed3/.test(m.filters.join('')), `filters ${m.filters}`),
      ...has(m.rows.length === 3, `the Changed filter shows only the lines that moved: ${m.rows.length}`),
    ],
  });

  // ---- approval, K3: the person approves; then either may build; an edit or words void it ---------------------
  await scene('approved-pro', {
    width, ...proPlan, steps: [{ click: 'sp-approve-head' }],
    expect: (m) => [
      ...has(JSON.stringify(callNames(m)) === '["approveStartPlan"]', `calls ${callNames(m)}`),
      ...has(m.calls[0]?.params.length === 1 && m.calls[0].params[0] === m.calls[0].digestNow, 'approve was sent the digest the page showed'),
      ...has(m.approval === 'current' && /You approved this plan/.test(m.approvalNote) && /You or your assistant can build it/.test(m.approvalNote), `approval note "${m.approvalNote}"`),
      ...has(m.footState === 'approved' && /You approved this plan\./.test(m.foot) && /Build/.test(m.foot), `the foot says the person approved and what is next: "${m.foot}"`),
      ...has(m.buttons.length > 0 && m.buttons.every((b) => /^Build$/.test(b.text)), `the button is now Build: ${JSON.stringify(m.buttons)}`),
      ...has(!/Approve and build/.test(m.allText), 'Approve and build is gone once approved'),
    ],
  });
  await scene('stale-pro', {
    width, ...proPlan, steps: [{ click: 'sp-approve-head' }, { eval: 'window.__spWords(3)', settle: 900 }],
    expect: (m) => [
      ...has(m.approval === 'stale' && /no longer matches/.test(m.approvalNote), `stale note "${m.approvalNote}"`),
      ...has(m.buttons.some((b) => /Approve and build/.test(b.text) && !b.disabled) && !m.buttons.some((b) => /^Build$/.test(b.text)), `buttons ${JSON.stringify(m.buttons)}`),
      ...has(m.footState === 'stale' && /changed after you approved/.test(m.foot), `the foot says the approval is void: "${m.foot}"`),
      ...has(/147 of 147|Words147|147/.test(m.strip), `the new words are counted: "${m.strip}"`),
    ],
  });
  // ---- the wiring step's states ---------------------------------------------------------------------------
  await scene('notnow-pro', {
    width, opts: { granted: false, decide: 'waiting_grant' }, steps: [{ text: 'Not now', scope: '[data-testid="sp-need-grant"]' }],
    expect: (m) => [
      ...has(JSON.stringify(m.calls.map((c) => [c.call, c.params])) === JSON.stringify([['editStartPlan', [{ op: 'answer', target: 'grant', value: 'Not now' }, 'human']]]), `Not now is the answer op: ${JSON.stringify(m.calls)}`),
      ...has(m.needs.some((n) => n.kind === 'grant_declined' && JSON.stringify(n.options) === '["Allow"]' && /carry on without Jev/.test(n.text)), `the declined line offers Allow: ${JSON.stringify(m.needs)}`),
      ...has(!/Jev is waiting for you/.test(m.allText), 'nothing says Jev is waiting'),
      ...has(m.buttons.some((b) => /Approve and build/.test(b.text) && !b.disabled), 'the plan can be approved without Jev'),
    ],
  });
  await scene('notnow-allow-pro', {
    width, opts: { granted: false, decide: 'waiting_grant' }, steps: [{ text: 'Not now', scope: '[data-testid="sp-need-grant"]' }, { text: 'Allow', scope: '[data-testid="sp-need-grant"]' }, { click: 'consent-ok' }],
    expect: (m) => [
      ...has(JSON.stringify(m.calls.map((c) => c.call)) === '["editStartPlan","setConsent","grantStartDisclosure"]', `calls ${m.calls.map((c) => c.call)}`),
      ...has(m.calls[2]?.params[0] === m.calls[2]?.digestNow && m.calls[2]?.params[1] === 'human', 'Allow is the grant, by the person, for the digest shown'),
      ...has(!m.needs.some((n) => n.kind === 'grant_declined' || n.kind === 'grant'), 'after Allow the band has no grant line'),
    ],
  });
  // Allow on a model with no permission yet: the start plan's card names the source and its tables and also asks for the
  // model, and nothing is granted until its OK; then the model's permission, then the grant, for the digest the page showed.
  await scene('grant-card-pro', {
    width, opts: { granted: false, decide: 'waiting_grant' }, steps: [{ text: 'Allow', scope: '[data-testid="sp-need-grant"]' }],
    expect: (m) => [
      ...has(/Let Pro see this source\?/.test(m.card ?? '') && /ContosoOps/.test(m.card ?? '') && /Timesheets/.test(m.card ?? ''), `the start plan card: "${m.card}"`),
      ...has(m.calls.length === 0, `nothing granted before OK: ${m.calls.map((c) => c.call)}`),
    ],
  });
  await scene('grant-ok-pro', {
    width, opts: { granted: false, decide: 'waiting_grant' }, steps: [{ text: 'Allow', scope: '[data-testid="sp-need-grant"]' }, { click: 'consent-ok', settle: 900 }],
    expect: (m) => [
      ...has(JSON.stringify(m.calls.map((c) => c.call)) === '["setConsent","grantStartDisclosure"]', `the model's permission, then the grant: ${m.calls.map((c) => c.call)}`),
      ...has(m.calls[1]?.params[0] === m.calls[1]?.digestNow, 'the grant carries the digest the page showed'),
      ...has(!m.card && !m.error && m.decide !== 'waiting_grant' && !m.needs.some((n) => n.kind === 'grant'), `Jev may look now: decide ${m.decide}, error "${m.error}"`),
    ],
  });
  // Two batches of the assistant's words: the Changed filter keeps both, not the last batch alone.
  await scene('changed-adds-pro', {
    width, ...proPlan,
    steps: [{ eval: 'window.__sp.lines.find((l) => l.item.id === "w-desc").item.title = "Descriptions for 40 fields"; window.__spWords(1)', settle: 900 },
      { eval: 'window.__sp.lines.find((l) => l.item.id === "w-names").item.title = "Business names for 7 tables and 41 fields"; window.__spWords(1)', settle: 900 },
      { click: 'sp-filter-changed' }],
    expect: (m) => [...has(JSON.stringify([...m.rows.map((r) => r.id)].sort()) === JSON.stringify(['w-desc', 'w-names', 'w-syn']), `Changed holds both batches: ${m.rows.map((r) => r.id)}`)],
  });
  await scene('held-pro', {
    width, ...proPlan, steps: [{ click: 'sp-row-t-in-Codes' }, { click: 'sp-drop' }],
    views: [{ suffix: 'words', scrollTo: 'sp-row-w-codes' }],
    expect: (m) => [
      ...has(m.heldRows.length === 1 && m.heldRows[0].id === 'w-codes' && /Held, not built/.test(m.heldRows[0].state) && /Held, because the table Codes was left out by you/.test(m.heldRows[0].sub), `held rows ${JSON.stringify(m.heldRows)}`),
      ...has(/1 held, not built/.test(m.facts), `the facts count it apart: "${m.facts}"`),
    ],
  });
  await scene('sample-reading-pro', {
    width, opts: { sample: 'reading' },
    expect: (m) => [...has(m.needs.some((n) => n.kind === 'sample_reading' && n.options.length === 0 && /Reading a sample of the rows/.test(n.text)), `sample line ${JSON.stringify(m.needs.find((n) => n.id === 'sample'))}`)],
  });
  await scene('sample-refused-pro', {
    width, opts: { sample: 'refused' },
    expect: (m) => [...has(m.needs.some((n) => n.kind === 'sample_refused' && n.options.length === 0 && /was not read/.test(n.text)), `sample line ${JSON.stringify(m.needs.find((n) => n.id === 'sample'))}`)],
  });
  await scene('provisional-pro', {
    width, opts: { decide: 'deciding', progress: [{ family: 'measures', asked: 41, answered: 38, unusable: 3, remaining: 34 }] },
    expect: (m) => [...has(m.gradeProvisional === 'yes' && /may still change while Jev works/.test(m.gradeNote ?? ''), `provisional grade "${m.grade}" note "${m.gradeNote}"`), ...has(m.footState === 'deciding', `foot ${m.footState}`)],
  });

  await scene('incomplete-pro', {
    width, opts: { decide: 'incomplete', reason: 'Jev stopped after 6 rounds, with 12 measures not looked at.' },
    expect: (m) => [
      ...has(m.decideTone === 'warn' && /not finished/.test(m.decideNote) && /6 rounds/.test(m.decideNote), `note "${m.decideNote}"`),
      ...has(!/\bdone\b/i.test(m.decideNote), 'Incomplete never says done'),
      ...has(m.buttons.some((b) => /Approve and build/.test(b.text) && !b.disabled), 'it may still be approved: the rest becomes Plan rows'),
    ],
  });
}

// ---- the journeys: the form to the build, counted --------------------------------------------------------------
async function journey(name, { free }) {
  if (only && !only.includes(name)) return;
  const q = `conn=1&live=1&spec=empty&coldNav=spec:start:sql${free ? '' : '&tier=pro'}`;
  const { page, errors } = await open(1280, { free, none: true, ...(free ? {} : { granted: false, decide: 'waiting_grant' }) }, q, 1300);
  const notes = [], log = [];
  let clicks = 0;
  const press = async (label, fn) => { const ok = await fn(); if (!ok) notes.push(`could not ${label}`); else { clicks++; log.push(label); } await wait(700); };
  // Typing the name and the folder are fields filled in, not presses: they are counted apart.
  await typeInto(page, 'sp-form-name', 'Contoso Consulting');
  await typeInto(page, 'sp-form-folder', '/home/kane/models/Contoso Consulting');
  await press('read the source', () => click(page, 'sp-form-start'));
  const m0 = await page.evaluate(MEASURE);
  if (!m0.onPlan) notes.push('the start plan did not open after the source was read');
  const gradesBefore = m0.grade;
  if (!free) {
    await press('allow Jev to look', () => clickText(page, '[data-testid="sp-need-grant"]', 'Allow'));
    await press('give this model permission and let Jev see the tables', () => click(page, 'consent-ok'));
    for (const [id, opt] of [['need-budgets', 'No total'], ['need-rates', 'Hide from Copilot'], ['need-left', 'Copilot can read it']]) await press(`answer ${id}`, () => clickText(page, `[data-testid="sp-need-${id}"]`, opt));
  }
  await press('answer fiscal_year', () => clickText(page, '[data-testid="sp-need-fiscal_year"]', 'Year ends 6/30'));
  const m1 = await page.evaluate(MEASURE);
  // the assistant writes words on the other door (not a press: it is counted apart)
  await page.evaluate('window.__spWords(2)'); await wait(800);
  const assistantWrites = 1;
  await press('approve and build', () => click(page, 'sp-approve-build-foot'));
  const m2 = await page.evaluate(MEASURE);
  await checkScreen(page, `startplan-drive:${name}`);
  await page.screenshot({ path: join(OUT, `${name}-end-1280-dark.png`) });
  const calls = m2.calls;
  const names = calls.map((c) => c.call);
  const buildAt = names.indexOf('buildStartPlan'), approveAt = names.indexOf('approveStartPlan');
  notes.push(...has(approveAt >= 0 && buildAt > approveAt, `approve came before build: ${names}`));
  notes.push(...has(calls[approveAt]?.params[0] === calls[approveAt]?.digestNow && calls[buildAt]?.params[0] === calls[approveAt]?.params[0], 'build was sent the digest the person approved, which is the digest they looked at'));
  notes.push(...has(calls[buildAt]?.params[1] === 'human' && calls[buildAt]?.approvedNow === true, 'build ran on an approval, as the person'));
  notes.push(...has(names.filter((n) => n === 'buildStartPlan').length === 1, 'exactly one build call'));
  notes.push(...has(m2.onPlan && m2.builtState === 'yes' && /one undo takes it back/i.test(m2.said ?? ''), `after the build the plan says it was built and how to take it back: "${m2.said}"`));
  if (free) notes.push(...has(!names.includes('grantStartDisclosure') && m1.needs.every((n) => n.kind !== 'jev' && n.kind !== 'grant'), 'Free made no Jev grant and met no Jev question'));
  else notes.push(...has(names.indexOf('grantStartDisclosure') >= 0 && names.indexOf('grantStartDisclosure') < names.indexOf('editStartPlan'), 'the grant came before any answer'));
  const r = {
    scene: name, free, presses: clicks, pressLog: log, fieldsTyped: 2, assistantWrites, calls: names, gradeBefore: m1.grade, gradeAtStart: gradesBefore,
    approvalSentDigestEqualsShown: calls[approveAt]?.params[0] === calls[approveAt]?.digestNow, planAfterBuild: m2.said, notes, errors,
  };
  results.push(r);
  console.log(`${notes.length || errors.length ? '!' : 'ok'} ${name}: ${clicks} presses, ${assistantWrites} assistant write, calls ${names.join(',')}${notes.length ? ' | ' + notes.join(' | ') : ''}`);
  await page.close();
}
await journey('journey-pro', { free: false });
await journey('journey-free', { free: true });

// ---- an open model with unsaved changes is never replaced quietly: the form asks, and only the second press discards ----
await scene('form-unsaved-pro', {
  opts: { none: true }, query: 'conn=1&live=1&tier=pro&spec=empty&dirty=1&coldNav=spec:start:sql',
  steps: [{ type: 'sp-form-name', value: 'Contoso Consulting' }, { type: 'sp-form-folder', value: '/home/kane/models/Contoso Consulting' }, { click: 'sp-form-start', settle: 900 }],
  views: [{ suffix: 'asked' }],
  expect: (m) => [
    ...has(m.onForm && /changes that are not saved/.test(m.formText), 'the form says the open model has unsaved changes'),
    ...has(callNames(m).length === 0, `nothing was started on the first press: ${callNames(m)}`),
    ...has(/Discard those changes and read the source/.test(m.formText), 'the button now says what it will do'),
  ],
});
await scene('form-unsaved-discard-pro', {
  opts: { none: true }, query: 'conn=1&live=1&tier=pro&spec=empty&dirty=1&coldNav=spec:start:sql',
  steps: [{ type: 'sp-form-name', value: 'Contoso Consulting' }, { type: 'sp-form-folder', value: '/home/kane/models/Contoso Consulting' }, { click: 'sp-form-start', settle: 900 }, { click: 'sp-form-start', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callNames(m)) === '["startModelFromSql"]', `calls ${callNames(m)}`),
    ...has(m.calls[0]?.params[0]?.discardUnsaved === true && m.calls[0]?.params[1] === 'human', `request ${JSON.stringify(m.calls[0]?.params)}`),
    ...has(m.calls[0]?.params[0]?.folder === '/home/kane/models/Contoso Consulting' && m.calls[0]?.params[0]?.name === 'Contoso Consulting', 'the folder and the name went with it'),
    ...has(m.onPlan, 'the plan is on the page'),
  ],
});

// ---- a plan that moves under the person's hand: nothing is approved on a plan they did not look at ------------------
await scene('approve-race-pro', {
  ...proPlan,
  // The assistant writes words on the other door, and the broadcast has not reached the page yet (the honest cost of two doors).
  steps: [{ eval: 'window.__sp.words.total += 2; window.__sp.lines.find((l) => l.item.id === "w-syn").item.title = "40 synonyms"; true' }, { click: 'sp-approve-build-head', settle: 900 }],
  views: [{ suffix: 'refused' }],
  expect: (m) => [
    ...has(JSON.stringify(callNames(m)) === '["approveStartPlan"]', `calls ${callNames(m)}: the build never ran`),
    ...has(!!m.error && /changed since you read it/.test(m.error), `error "${m.error}"`),
    ...has(m.approval === 'none', 'nothing is approved'),
    ...has(m.onPlan, 'the plan is still there, read again'),
    ...has(/146 of 146|146/.test(m.strip), `the page now shows the new words: "${m.strip}"`),
  ],
});

// ---- the build is undone (join 8, K15): the same plan is a plan again, and the page no longer says "Built in one step" above a live Build button.
await scene('built-then-undone-pro', {
  ...proPlan, steps: [{ click: 'sp-approve-build-head', settle: 900 }, { eval: 'window.__spState({ built: false, approval: null })', settle: 900 }], views: [{}],
  expect: (m) => [
    ...has(m.builtState !== 'yes', `the plan is not built any more: ${m.builtState}`),
    ...has(!/Built in one step|One undo takes it back/.test(m.said ?? '') && !/Built in one step/.test(m.allText), `the page does not still say it was built: "${m.said}"`),
    ...has(m.buttons.some((b) => /Approve/.test(b.text) && !b.disabled), `the plan can be approved again: ${JSON.stringify(m.buttons)}`),
    // D7 (launch video, 12 Oct): the card says the model has no grade now, so the person sees the grade fall, and keeps the forecast named as one.
    ...has(/No tables now/.test(m.grade) && /after the build/.test(m.grade), `the grade cell after an undo: "${m.grade}"`),
  ],
});

// ---- built: the plan says it was built, shows the grades, offers Discard only --------------------------------------
for (const width of [1280, 900]) await scene('built-pro', {
  width, ...proPlan, steps: [{ click: 'sp-approve-build-head', settle: 900 }], views: [{}, { suffix: 'foot', scrollTo: 'sp-foot' }],
  expect: (m) => [
    ...has(m.onPlan && m.builtState === 'yes', 'the built plan stays on the page, marked built'),
    ...has(/One undo takes it back/.test(m.said ?? ''), `it says it was built and how to take it back: "${m.said}"`),
    ...has(m.buttons.length === 0 && m.discard, `Discard is the one thing offered: ${JSON.stringify(m.buttons)}`),
    ...has(m.footState === 'built' && /Built into Contoso Consulting\./.test(m.foot) && /Undo/.test(m.foot), `foot "${m.foot}"`),
    ...has(/A/.test(m.grade), `the grades are shown: "${m.grade}"`),
    ...has(!/Model not built yet/.test(m.allText), 'never "not built yet"'),
    ...has(JSON.stringify(callNames(m)) === '["approveStartPlan","buildStartPlan"]', `calls ${callNames(m)}`),
  ],
});

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
const bad = results.filter((r) => r.errors.length > 0 || r.notes.length > 0);
console.log(`\n${results.length} scenes, ${results.length - bad.length} clean.`);
for (const r of bad) console.log(`  ! ${r.scene}${r.width ? '@' + r.width : ''}: ${[...r.notes, ...r.errors].join(' | ')}`);
const screenBad = screenFailures();
for (const f of screenBad) console.log(`  ! screen: ${f}`);
console.log(`results: ${join(OUT, 'results.json')}`);
if (bad.length > 0 || screenBad.length > 0) process.exitCode = 1;
