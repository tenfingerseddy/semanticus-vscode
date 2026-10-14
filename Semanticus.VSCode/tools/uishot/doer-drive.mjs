// Kane's Plan from the 1.4.101 test build (13 Oct 2026), headless, engine mocked: who does each row, before and after the doer lane.
// The rows are what each engine sends for that plan (the after rows are the ones PlanDoerTests proves the engine now sends).
//
//   ROOT=<worktree with a built media/studio> OUT=<dir> LABEL=before|after node tools/uishot/doer-drive.mjs
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
const OUT = process.env.OUT || join(ROOT, 'shots', 'doer');
const LABEL = process.env.LABEL || 'after';
const AFTER = LABEL === 'after';
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(ROOT, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing in ' + ROOT); process.exit(1); }

const row = (id, title, doer, stateWord, extra = {}) => ({
  id, section: 'Trust', title, sub: extra.sub ?? '', who: doer === 'you' ? 'you' : doer === 'assistant' ? 'asst' : 'auto', status: 'proposed', state: 'gap',
  confidence: 0.8, why: ['Semanticus found this on the model.'], source: 'found', doer, stateWord, goal: 'quality', goals: ['quality'], rules: [],
  objects: [], waitsFor: extra.waitsFor ?? [], jump: extra.jump ?? { tool: 'tests', label: 'Validation' }, doneText: 'The model check passes.',
  stateNote: extra.stateNote ?? null, order: extra.order ?? null, changeSet: extra.changeSet ?? null,
});
const NOT_CHECKED = 'could not be checked this time';
const UNTICKED = 'Every change on this task is unticked, so Run the plan skips it. Your assistant asks you which to keep. You can also tick them yourself.';
const checks = ['# Customers', '# Sales MoM %', '# New Customers', '# Sales MTD', '# Orders', '# Sales', '# Sales YoY %'];
const discountSet = { id: 'cs-d', state: 'proposed', digest: 'dg-d', lines: [{ id: 'l1', planItemId: 'ci-1', line: '~ Discount: format 0.0%', op: 'set_format', target: 'measure:Discount Scenario/Discount', kept: false }] };
const plan = () => {
  const by = (before, after) => (AFTER ? after : before);
  const items = [
    row('pt-cal', 'Define a calendar', 'assistant', 'ready', { stateNote: 'when your assistant next checks the Plan', order: 1, jump: { tool: 'advmodels', label: 'Advanced modelling' } }),
    row('jev.measures.table:Discount Scenario', 'Add folders and formats to 1 measure in Discount Scenario', by('semanticus', 'assistant'), by('needs_you', 'ready'),
      { changeSet: discountSet, stateNote: by(null, UNTICKED), jump: { tool: 'readiness', label: 'AI readiness' } }),
    row('jev.measures.table:Sales', 'Add folders and formats to 1 measure in Sales', 'semanticus', 'waiting',
      { waitsFor: [{ id: 'jev.measures.table:Discount Scenario', title: 'Add folders and formats to 1 measure in Discount Scenario' }], jump: { tool: 'readiness', label: 'AI readiness' } }),
    row('recheck/reports', 'Look at 1 report again', by('you', 'assistant'), 'waiting', { waitsFor: [{ id: 'jev.measures.table:Sales', title: 'Add folders and formats to 1 measure in Sales' }], jump: { tool: 'lineage', label: 'Lineage' } }),
    row('ship/unsaved', 'Save the changes you have made', by('you', 'assistant'), by('needs_you', 'ready'), { jump: { tool: 'history', label: 'History' } }),
    row('check/more', '29 more measures need a check', by('you', 'assistant'), by('needs_you', 'ready'), { sub: NOT_CHECKED }),
    ...checks.map((m) => row('check/measure:Key Measures/' + m, 'Add a check for ' + m, by('you', 'assistant'), by('needs_you', 'ready'), { sub: NOT_CHECKED })),
  ];
  return { modelName: 'Contoso', revision: 3, items, history: [], tables: [], readyDigest: 'rd1', assistantQueue: 'on',
    strip: { gradeNow: 'B', gradeAfter: null, bestPractice: 'C', tracks: [] } };
};

const seed = (cfg) => `
const __cfg = ${JSON.stringify(cfg)};
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  v.setResponse('getModelPlan', () => __cfg.plan);
  v.setResponse('readingCard', () => ({ show: false }));
  v.setResponse('getNextStep', () => ({ id: 's1', on: false }));
} });
let __acquire; Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, get: () => __acquire, set: (original) => { __acquire = () => { const api = original(); const post = api.postMessage;
  api.postMessage = (m) => { post(m); if (m.type === 'studioReady') setTimeout(() => {
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true } }));
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'assistantCall', payload: { at: new Date().toISOString(), tool: 'get_model_plan', assistant: __cfg.assistant } } }));
  }, 30); }; return api; }; } });`;

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(ROOT, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(ROOT) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
// What each engine reports for Kane's Claude Desktop client ("local-agent-mode-semanticus"): the raw id before, "Your assistant" after.
const assistant = AFTER ? { key: 'unknown', name: 'Your assistant', initials: null } : { key: 'other', name: 'local-agent-mode-semanticus', initials: 'LA' };
for (const [theme, width] of [['dark', 1440], ['dark', 400]]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width, height: 960, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed({ plan: plan(), assistant }));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan&compat=1606&dirty=1`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await new Promise((r) => setTimeout(r, 1800));
  await page.screenshot({ path: join(OUT, `${LABEL}-${theme}-${width}.png`), fullPage: true });
  const facts = await page.evaluate(() => ({
    groups: [...document.querySelectorAll('[data-testid^="plan-group-count-"]')].map((e) => e.getAttribute('data-testid').replace('plan-group-count-', '') + '=' + e.textContent.trim()),
    disabled: [...document.querySelectorAll('[data-testid^="plan-act-"]')].filter((b) => b.disabled).map((b) => b.closest('[data-row]')?.getAttribute('data-row') + ':' + b.textContent),
    drive: document.querySelector('[data-testid="driver-mode-claudeDrives"]')?.textContent ?? null,
    overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
  }));
  console.log(`${LABEL}-${theme}-${width}: ${JSON.stringify(facts)} errors=${JSON.stringify(errors)}`);
  await page.close();
}
await browser.close(); server.close();
