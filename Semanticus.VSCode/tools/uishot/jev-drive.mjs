// Browser-driven screens of Jev on the calm Plan (1.5, pick A of the Jev plan), headless, engine mocked: a spark and "Jev" after the title of
// every row Jev chose lines for, the worth ("+4 AI ready") before the state word in Jev's light jade, Jev's reason when a row opens, and
// "Jev chose N." on the Done line. Dark and light, 1440 and 400, at rest and with a Jev row open.
//
//   ROOT=<worktree with a built media/studio> OUT=<dir> [SCALE=0.4] node tools/uishot/jev-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
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
const OUT = process.env.OUT || join(ROOT, 'shots', 'jev');
const SCALE = Number(process.env.SCALE || 1);
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(ROOT, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing in ' + ROOT); process.exit(1); }

// ---- the plan: a Contoso model with five Jev rows (the real wording of the engine's family titles and reasons) ----
const row = (id, title, doer, stateWord, extra = {}) => ({
  id, section: 'Meaning', title, sub: extra.sub ?? '', who: doer === 'you' ? 'you' : doer === 'assistant' ? 'asst' : 'auto', status: 'proposed', state: 'gap', confidence: 0.8,
  why: extra.why ?? ['Semanticus found this on the model.'], source: extra.source ?? 'found', doer, stateWord, goal: 'ai', goals: ['ai'], rules: [], objects: [], waitsFor: extra.waitsFor ?? [],
  value: extra.value ?? '', jump: { tool: 'optimize', label: 'Pending changes' }, doneText: 'The model check passes.', ...(extra.rest ?? {}),
});
const jline = (id, text, word, kept = true) => ({ id, planItemId: 'ci-' + id, line: '~ ' + text, op: 'set_property', target: 'column:Customer/' + id, property: 'isHidden', before: 'false', after: 'true', kept,
  reason: 'Jev chose it from a fixed list.', check: { exists: true, text: 'Checked by Semanticus.' }, provenance: { kind: 'decision', point: 'ai.schema', confidence: word === 'unsure' ? 0.4 : 0.9, confidenceWord: word } });
const jev = (id, title, value, why, lines, extra = {}) => row(id, title, 'semanticus', extra.stateWord ?? 'ready', { source: 'jev', value, why, sub: extra.sub,
  rest: { changeSet: { state: 'proposed', digest: 'dg-' + id, stale: false, lines } } });
const items = [
  row('words/descriptions', 'Rewrite 3 descriptions', 'assistant', 'ready'),
  row('words/measures', 'Describe 2 measures', 'assistant', 'ready'),
  jev('jev.ai.table:Customer', 'Choose which 12 fields Copilot can see in Customer', '+4 AI ready',
    ['5 keys and ids in Customer are visible to Copilot, and it cannot use them to answer. Jev left them out and kept the 7 it can use.', 'Jev chose each setting from a fixed list using the names, types and settings on this computer. Semanticus wrote every change and checked it before showing it.'],
    [jline('CustomerKey', 'Leave out of what Copilot sees: CustomerKey', 'sure'), jline('GeographyKey', 'Leave out of what Copilot sees: GeographyKey', 'sure'),
     jline('Customer Code', 'Leave out of what Copilot sees: Customer Code', 'likely'), jline('Segment', 'Let Copilot see it: Customer Segment', 'sure')], { sub: 'CustomerKey, GeographyKey, Customer Code, Customer Segment and 8 more' }),
  jev('jev.measures.table:Sales', 'Add folders and formats to 6 measures in Sales', '+3 AI ready', ['6 measures in Sales have no folder or no number format yet. They show as plain numbers in one long list.'],
    [jline('Revenue', 'Revenue: set the folder to Money', 'sure'), jline('Margin', 'Margin: set the format to a percent', 'likely')]),
  jev('jev.starter.table:Product', 'Add measures for the numbers in Product', '+2 AI ready', ['Product has number columns with no measure, so a question about them has nothing to total.'], [jline('Price', 'Add a measure for Price', 'sure')], { stateWord: 'waiting' }),
  jev('jev.relationships.table:Sales', 'Join Sales to Date on Order Date', '+2 AI ready', ['Sales has an Order Date and no link to the Date table, so a question by month cannot be answered.'], [jline('OrderDate', 'Join Sales to Date on Order Date', 'sure')]),
  row('starter/Discount', 'Add 1 measure for the numbers in Discount Scenario', 'semanticus', 'waiting'),
  row('pt-1', 'Raise compatibility level to 1701', 'you', 'needs_you', { source: 'found' }),
];
const plan = (done) => ({ modelName: 'Contoso Retail', revision: done ? 2 : 1, items: done ? items.filter((i) => i.doer !== 'semanticus' || i.source !== 'jev') : items, history: [], tables: [], readyDigest: done ? null : 'rd1', assistantQueue: 'on',
  strip: { gradeNow: 'A', gradeAfter: null, bestPractice: 'D', tracks: [] } });
const seed = (cfg) => `
const __cfg = ${JSON.stringify(cfg)};
window.__calls = [];
let __stage = 0;
const __plans = __cfg.plans;
const __cur = () => __plans[Math.min(__stage, __plans.length - 1)];
const __note = (name, params) => window.__calls.push({ call: name, params });
const __fire = (payload) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'planRunProgress', payload } }));
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getModelPlan', () => { __note('getModelPlan'); return __cur(); });
    v.setResponse('readingCard', () => ({ show: true, grade: __cfg.grade[Math.min(__stage, 1)][0], score: __cfg.grade[Math.min(__stage, 1)][1], gradeAfter: null, scoreAfter: null, heldBy: [], safeFixCount: 3, writingCount: 5, writingJobs: 2, openFindings: 14, revision: 1, next: '3 writing tasks go to your assistant.',
      top: __stage > 0 ? [] : [{ key: 'd1', count: 3, headline: '3 descriptions need a rewrite', why: 'A weak description helps no one.', who: 'assistant', holdsGrade: false, state: 'open', ruleIds: [] }, { key: 'n1', count: 9, headline: '9 names are hard to read', why: 'People and Copilot both guess wrong.', who: 'assistant', holdsGrade: false, state: 'open', ruleIds: [] }, { key: 'm1', count: 2, headline: '2 measures have no description', why: 'Copilot has to guess what they mean.', who: 'assistant', holdsGrade: false, state: 'open', ruleIds: [] }] }));
    v.setResponse('getNextStep', () => ({ id: 's1', on: true, stage: 2, stageName: 'Get it AI ready', step: { kind: 'run_plan', ask: 'Ask your assistant to run the plan', why: '3 tasks are ready. They run in Plan order.', source: 'From your Plan', prompt: 'Work through my Semanticus plan.' } }));
    v.setResponse('applyPlanItems', (params) => {
      __note('applyPlanItems', params);
      const ids = (params && params[0] && params[0].length) ? params[0].map((x) => x.id) : __cfg.runIds;
      ids.forEach((id, k) => {
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'work', wave: 1, ms: k * __cfg.step }), k * __cfg.step);
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'apply', wave: 1, changes: 4, ms: k * __cfg.step + 300 }), k * __cfg.step + __cfg.step * 0.4);
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'done', wave: 1, changes: 4, ms: k * __cfg.step + 600 }), k * __cfg.step + __cfg.step * 0.9);
      });
      return new Promise((resolve) => setTimeout(() => { __stage = 1; resolve({ performed: true, applied: ids.length * 4, itemIds: ids, revision: 2, entryId: 'ed-1', canSave: true, plan: __cur() }); }, ids.length * __cfg.step + 200));
    });
    v.setResponse('modelPlanAction', (params) => { __note('modelPlanAction', params); const p = __cur(); return { itemId: params[0], action: params[1], performed: false, plan: __cfg.proposeFor ? Object.assign({}, p, { items: p.items.map((i) => (i.id === params[0] ? Object.assign({}, i, { changeSet: { state: 'proposed', digest: 'dg-' + i.id, lines: [{ id: 'l1', line: 'x', kept: true }] } }) : i)) }) : p }; });
    v.setResponse('undo', (params) => { __note('undo', params); __stage = 0; return { canUndo: false, canRedo: true }; });
    v.setResponse('addPlanTask', (params) => { __note('addPlanTask', params); return { itemId: 'pt-9', plan: __cur() }; });
    v.setResponse('setAssistantQueue', (params) => { __note('setAssistantQueue', params); return Object.assign({}, __cur(), { assistantQueue: params[0] }); });
    v.setResponse('setCompatibilityLevel', (params) => { __note('setCompatibilityLevel', params); return {}; });
    for (const name of ['setPlanItemDoer', 'claimPlanTask', 'skipModelPlanItem', 'movePlanTaskUp', 'completePlanTask']) v.setResponse(name, (params) => { __note(name, params); return __cur(); });
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
const CFG = { plans: [plan(false), plan(true)], grade: [['A', 91], ['A', 94]], runIds: ['jev.ai.table:Customer', 'jev.measures.table:Sales', 'jev.relationships.table:Sales'], step: Math.round(900 * SCALE), proposeFor: false };
const shot = async (page, name) => { await page.screenshot({ path: join(OUT, `${name}.png`), fullPage: false }); };
const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
const offenders = (page) => page.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > window.innerWidth + 1; })
  .slice(0, 6).map((e) => `${e.tagName.toLowerCase()}.${String(e.className).split(' ').slice(0, 2).join('.')} right=${Math.round(e.getBoundingClientRect().right)}`));

for (const [theme, width] of [['dark', 1440], ['light', 1440], ['dark', 400], ['light', 400]]) {
  const tag = `${theme}-${width}`;
  const { page, errors } = await open(theme, width, CFG);
  await shot(page, `${tag}-1-rest`);
  const marks = await page.evaluate(() => [...document.querySelectorAll('[data-testid="plan-row-jev"]')].length);
  const worths = await page.evaluate(() => [...document.querySelectorAll('[data-testid="plan-row-worth"]')].map((e) => e.textContent));
  const colour = await page.evaluate(() => { const e = document.querySelector('.pw-jev'); return e ? getComputedStyle(e).color : null; });
  const bad1 = await overflow(page);
  // open a Jev row: the reason, then the lines
  await page.evaluate(() => document.querySelector('[data-testid="plan-row-open-jev.ai.table:Customer"]')?.click());
  await wait(500);
  await shot(page, `${tag}-2-open`);
  const reason = await page.evaluate(`(${text})('[data-testid="plan-jev-reason-jev.ai.table:Customer"]')`);
  const bad2 = await overflow(page);
  await page.evaluate(() => document.querySelector('[data-testid="plan-row-open-jev.ai.table:Customer"]')?.click());
  await wait(300);
  // the run: "Jev chose N." on the Done line
  await page.evaluate(() => document.querySelector('[data-testid="plan-run"]')?.click());
  await wait(CFG.step * 4 + 2400);
  await shot(page, `${tag}-3-done`);
  const done = await page.evaluate(`(${text})('[data-testid="plan-slot"]')`);
  if (bad1 || bad2) say(`${tag}: overflow offenders ${JSON.stringify(await offenders(page))}`);
  say(`${tag}: marks=${marks} worths=${JSON.stringify(worths)} jevColour=${colour} reason="${reason}" done="${done}" overflow rest=${bad1} open=${bad2} errors ${JSON.stringify(errors)}`);
  await page.close();
}
await browser.close(); server.close();
console.log('wrote ' + OUT);
