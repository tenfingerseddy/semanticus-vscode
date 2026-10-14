// Browser-driven screens of the Plan header's three small figures (1.5, Kane's pick A "Three small figures"), headless, engine mocked. It plays a Contoso
// plan through the real bundle: at rest, a tooltip on hover and on keyboard focus, a run (the figures move as rows turn Done), the end, and the fit at 1440, 1100 and 400.
//
//   ROOT=<worktree with a built media/studio> OUT=<dir> [SCALE=0.4] node tools/uishot/story-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(process.env.ROOT || join(here, '..', '..'));
const OUT = process.env.OUT || join(ROOT, 'shots', 'calm');
const SCALE = Number(process.env.SCALE || 1);
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(ROOT, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing in ' + ROOT); process.exit(1); }

// ---- the plans ----
const why = ['Semanticus found this on the model.'];
const row = (id, title, doer, stateWord, extra = {}) => ({
  id, section: 'Meaning', title, sub: extra.sub ?? '', who: doer === 'you' ? 'you' : doer === 'assistant' ? 'asst' : 'auto', status: 'proposed', state: 'gap', confidence: 0.8, why,
  source: 'found', doer, stateWord, goal: extra.goal ?? 'quality', goals: [extra.goal ?? 'quality'], rules: [], objects: extra.objects ?? [], waitsFor: extra.waitsFor ?? [],
  jump: extra.jump ?? { tool: 'readiness', label: 'AI readiness' }, doneText: 'The model check passes.', stateNote: extra.stateNote ?? null, order: extra.order ?? null, ...(extra.rest ?? {}),
});

const STEP_MS = Math.round(1100 * SCALE);
const RUN_IDS = ['starter/Customer', 'rel/sales-date', 'starter/Product', 'starter/Discount', 'fmt/sales'];
// What the engine would answer after each row turns Done (the mock stands in for the engine; the page only reads these numbers).
const BP = [[ 'D', 64 ], [ 'D', 66 ], [ 'C', 71 ], [ 'C', 72 ], [ 'C', 72 ], [ 'C', 75 ]];
const plan12 = (doneRows) => {
  const rows = [
    row('words/descriptions', 'Rewrite 3 descriptions', 'assistant', 'ready', { goal: 'ai', order: 1, jump: { tool: 'instructions', label: 'Model notes' } }),
    row('words/measures', 'Describe 2 measures', 'assistant', 'ready', { goal: 'ai', order: 2 }),
    row('safefix/names', 'Make 9 names easier to read', 'assistant', 'ready', { goal: 'ai', order: 3 }),
    row('starter/Customer', 'Add 3 measures for the numbers in Customer', 'semanticus', 'ready', { goal: 'ai', jump: { tool: 'advmodels', label: 'Advanced modelling' } }),
    row('starter/Product', 'Add 3 measures for the numbers in Product', 'semanticus', 'ready', { goal: 'ai' }),
    row('starter/Discount', 'Add 1 measure for the numbers in Discount Scenario', 'semanticus', 'ready', { goal: 'ai' }),
    row('rel/sales-date', 'Join Sales to Date on Order Date', 'semanticus', 'ready', { jump: { tool: 'relationships', label: 'Relationships' } }),
    row('summ/spend', "Set 3 columns to Don't summarize in Customer Spend Band", 'you', 'needs_you', {}),
    row('summ/metrics', "Set 1 column to Don't summarize in Metrics", 'you', 'needs_you', {}),
    row('fmt/sales', 'Add folders and formats to 2 measures in Sales', 'semanticus', 'ready', {}),
    row('pt-1', 'Pick a sort order for Fiscal Month', 'you', 'needs_you', { source: 'words', jump: null, rest: { id: 'pt-1', doneWhen: 'you_mark' } }),
  ].map((r) => (RUN_IDS.slice(0, doneRows).includes(r.id) ? { ...r, stateWord: 'done', status: 'done', state: 'done' } : r));
  const [letter, score] = BP[doneRows];
  const all = 19, done = 8 + doneRows;     // 11 open rows and 8 done in History at the start
  return { modelName: 'Contoso', revision: 1 + doneRows, items: rows, history: [], tables: [], readyDigest: 'rd1', assistantQueue: 'on',
    strip: { gradeNow: 'A', gradeAfter: null, bestPractice: letter, bestPracticeScore: score, tasksDone: done, tasksAll: all, tracks: [] } };
};

const seed = (cfg) => `
const __cfg = ${JSON.stringify(cfg)};
window.__calls = [];
let __done = 0;               // rows the mock engine has finished
const __plan = () => __cfg.plans[Math.min(__done, __cfg.plans.length - 1)];
const __fire = (payload) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'planRunProgress', payload } }));
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getModelPlan', () => { window.__calls.push({ call: 'getModelPlan', done: __done }); return __plan(); });
    v.setResponse('readingCard', () => { const g = __done >= __cfg.runIds.length ? __cfg.grade[1] : __cfg.grade[0]; return { show: true, grade: g[0], score: g[1], gradeAfter: null, scoreAfter: null, heldBy: [], safeFixCount: 3, writingCount: 5, writingJobs: 2, openFindings: 14, revision: 1, next: '', top: [] }; });
    v.setResponse('getNextStep', () => ({ id: 's1', on: true, stage: 2, stageName: 'Get it AI ready', step: { kind: 'run_plan', ask: 'Ask your assistant to run the plan', why: '3 tasks are ready. They run in Plan order.', source: 'From your Plan', prompt: 'Work through my Semanticus plan.' } }));
    v.setResponse('applyPlanItems', () => {
      const ids = __cfg.runIds;
      ids.forEach((id, k) => {
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'work', wave: 1, ms: k * __cfg.step }), k * __cfg.step);
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'apply', wave: 1, changes: 4, ms: k * __cfg.step + 300 }), k * __cfg.step + __cfg.step * 0.4);
        setTimeout(() => { __done = k + 1; __fire({ runId: 'r1', itemIds: [id], phase: 'done', wave: 1, changes: 4, ms: k * __cfg.step + 600 }); }, k * __cfg.step + __cfg.step * 0.9);
      });
      return new Promise((resolve) => setTimeout(() => { __done = ids.length; resolve({ performed: true, applied: ids.length * 4, itemIds: ids, revision: 6, entryId: 'ed-1', canSave: true, plan: __plan() }); }, ids.length * __cfg.step + 200));
    });
    v.setResponse('modelPlanAction', (params) => { const p = __plan(); return { itemId: params[0], action: params[1], performed: false, plan: Object.assign({}, p, { items: p.items.map((i) => (i.id === params[0] ? Object.assign({}, i, { changeSet: { state: 'proposed', digest: 'dg-' + i.id, lines: [{ id: 'l1', line: 'x', kept: true }] } }) : i)) }) }; });
    v.setResponse('undo', () => { __done = 0; return { canUndo: false, canRedo: true }; });
    for (const name of ['addPlanTask', 'setAssistantQueue', 'setCompatibilityLevel', 'setPlanItemDoer', 'claimPlanTask', 'skipModelPlanItem', 'movePlanTaskUp', 'completePlanTask']) v.setResponse(name, () => __plan());
  },
});
let __acquire; Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, get: () => __acquire, set: (original) => { __acquire = () => { const api = original(); const post = api.postMessage;
  api.postMessage = (m) => { post(m); if (m.type === 'studioReady') setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true } })), 30); }; return api; }; } });`;

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(ROOT, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(ROOT) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 600000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const text = `(sel) => { const e = document.querySelector(sel); return e ? e.textContent.replace(/\\s+/g, ' ').trim() : null; }`;
const notes = [];
const say = (m) => { console.log(m); notes.push(m); };

async function open(theme, width, cfg) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width, height: width < 700 ? 900 : 960, deviceScaleFactor: 1 });
  await page.emulateTimezone('UTC');
  await page.evaluateOnNewDocument(seed(cfg));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan&compat=1606&dirty=0`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1800);
  return { page, errors };
}

const CFG = { plans: [0, 1, 2, 3, 4, 5].map(plan12), grade: [['A', 91], ['A', 94]], runIds: RUN_IDS, step: STEP_MS };
const shot = async (page, name) => { await page.screenshot({ path: join(OUT, `${name}.png`), fullPage: false }); };
const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
const figs = (page) => page.evaluate(() => ({
  ai: document.querySelector('[data-testid="fig-ai"]')?.getAttribute('aria-label') ?? null,
  bp: document.querySelector('[data-testid="fig-bp"]')?.getAttribute('aria-label') ?? null,
  plan: document.querySelector('[data-testid="fig-plan"]')?.getAttribute('aria-label') ?? null,
  planValue: document.querySelector('[data-testid="fig-plan-percent"]')?.getAttribute('data-value') ?? null,
  bpScore: document.querySelector('[data-testid="fig-bp-score"]')?.getAttribute('data-value') ?? null,
  aiScore: document.querySelector('[data-testid="fig-ai-score"]')?.getAttribute('data-value') ?? null,
}));
const offenders = (page) => page.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > window.innerWidth + 1; })
  .slice(0, 6).map((e) => `${e.tagName.toLowerCase()}.${String(e.className).split(' ').slice(0, 2).join('.')} right=${Math.round(e.getBoundingClientRect().right)}`));
const slotRoom = (page) => page.evaluate(() => { const h = document.querySelector('.pc-slot-host'); const r = h?.getBoundingClientRect(); return r ? Math.round(r.width) : null; });

const sizes = (process.env.WIDTHS || '1440,1100,900,400').split(',').map(Number);
for (const [theme, width] of sizes.flatMap((w) => (process.env.THEMES || 'dark,light').split(',').map((t) => [t, w]))) {
  const tag = `${theme}-${width}`;
  const { page, errors } = await open(theme, width, CFG);
  await shot(page, `${tag}-1-rest`);
  const rest = await figs(page);
  const room = await slotRoom(page);
  const bad1 = await overflow(page);
  const bad1who = bad1 ? await offenders(page) : [];
  // the tooltip: hover on the first figure (keyboard focus is checked below)
  await page.hover('[data-testid="fig-ai"]'); await wait(400);
  await shot(page, `${tag}-2-tip-hover`);
  const tipShown = await page.evaluate(() => { const t = document.querySelector('[data-testid="fig-ai-tip"]'); return t ? getComputedStyle(t).opacity + '/' + getComputedStyle(t).visibility : null; });
  await page.mouse.move(2, 2); await wait(300);
  // keyboard focus shows the same tip; Escape hides it
  await page.focus('[data-testid="fig-bp"]'); await wait(400);
  const focusTip = await page.evaluate(() => { const t = document.querySelector('[data-testid="fig-bp-tip"]'); return t ? getComputedStyle(t).opacity + '/' + getComputedStyle(t).visibility : null; });
  const focusText = await page.evaluate(`(${text})('[data-testid="fig-bp-tip"]')`);
  await shot(page, `${tag}-3-tip-focus`);
  await page.keyboard.press('Escape'); await wait(300);
  const escTip = await page.evaluate(() => { const t = document.querySelector('[data-testid="fig-bp-tip"]'); return t ? getComputedStyle(t).visibility : null; });
  await page.evaluate(() => document.activeElement?.blur());
  // the run
  await page.evaluate(() => document.querySelector('[data-testid="plan-run"]')?.click());
  await wait(STEP_MS * 2.5);
  await shot(page, `${tag}-4-running`);
  const mid = await figs(page);
  const slotText = await page.evaluate(`(${text})('[data-testid="plan-slot"]')`);
  const midRoom = await slotRoom(page);
  await wait(STEP_MS * 3.6 + 2600);
  await shot(page, `${tag}-5-done`);
  const end = await figs(page);
  const doneText = await page.evaluate(`(${text})('[data-testid="plan-slot"]')`);
  const calls = await page.evaluate(() => window.__calls.length);
  say(`${tag}: rest ${JSON.stringify(rest)} slotRoom=${room}px overflow=${bad1} ${bad1 ? JSON.stringify(bad1who) : ''}`);
  say(`${tag}: tip hover=${tipShown} focus=${focusTip} escape=${escTip} text="${focusText}"`);
  say(`${tag}: running ${JSON.stringify(mid)} slot "${slotText}" room=${midRoom}px`);
  say(`${tag}: done ${JSON.stringify(end)} slot "${doneText}" overflow=${await overflow(page)} plan reads=${calls} errors ${JSON.stringify(errors)}`);
  await page.close();
}
await browser.close(); server.close();
writeFileSync(join(OUT, 'notes.txt'), notes.join('\n') + '\n');
