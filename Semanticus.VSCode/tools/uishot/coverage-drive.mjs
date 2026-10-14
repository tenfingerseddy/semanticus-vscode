// The coverage lane (13 Oct 2026), headless, engine mocked: the Plan with the coverage rows and the Review page's Fix N, before and after.
// The rows are what each engine sends for Kane's model (a float column, hidden unused columns, a two-way link, a Copilot limit).
//
//   ROOT=<worktree with a built media/studio> OUT=<dir> LABEL=before|after node tools/uishot/coverage-drive.mjs
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
const OUT = process.env.OUT || join(ROOT, 'shots', 'coverage');
const LABEL = process.env.LABEL || 'after';
const AFTER = LABEL === 'after';
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(ROOT, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing in ' + ROOT); process.exit(1); }


const row = (id, title, doer, stateWord, extra = {}) => ({
  id, section: extra.section ?? 'Structure', title, sub: extra.sub ?? '', who: doer === 'you' ? 'you' : doer === 'assistant' ? 'asst' : 'auto', status: 'proposed', state: 'gap',
  confidence: 0.8, why: extra.why ?? ['Semanticus found this on the model.'], source: 'found', doer, stateWord, goal: extra.goal ?? 'quality', goals: [extra.goal ?? 'quality'],
  rules: extra.rules ?? [], objects: extra.objects ?? [], waitsFor: [], jump: extra.jump ?? { tool: 'bpa', label: 'Model quality' }, doneText: 'The model check passes.',
  stateNote: extra.stateNote ?? null, order: extra.order ?? null, changeSet: null, optional: extra.optional ?? false,
});
const plan = () => {
  const items = [
    row('bpafix/model', 'Fix 4 Model quality findings', 'semanticus', 'ready', { sub: 'hide foreign keys, hide fact table columns', rules: ['HIDE_FOREIGN_KEYS'] }),
    ...(AFTER ? [
      row('proposal/coverage-bpa', 'Fix 6 best-practice issues', 'assistant', 'ready', { sub: 'Avoid floating point data types, Remove unnecessary columns', rules: ['AVOID_FLOATING_POINT_DATA_TYPES', 'UNNECESSARY_COLUMNS'] }),
      row('proposal/coverage-ai', 'Fix 2 AI readiness issues', 'assistant', 'ready', { section: 'Trust', goal: 'ai', sub: 'A formula refers to an object the model does not have', jump: { tool: 'readiness', label: 'AI readiness' } }),
      row('compat/raise', 'Raise compatibility level to 1701', 'assistant', 'ready', { sub: 'this model uses level 1604', jump: { tool: 'advmodels', label: 'Advanced modelling' } }),
      row('cleanup/model', 'Remove 1 unused column', 'assistant', 'ready', { optional: true, jump: { tool: 'lineage', label: 'Lineage' } }),
      row('proposal/limits', 'Work around 1 Copilot limit', 'assistant', 'ready', { section: 'Trust', goal: 'ai', jump: { tool: 'readiness', label: 'AI readiness' } }),
      row('jev.ai-schema.not-finished', 'Jev has not finished choosing what Copilot sees', 'semanticus', 'waiting', { section: 'Trust', goal: 'ai', stateNote: 'Jev tries again after the next change to the model, or in the next session. There is nothing for you or your assistant to do.', jump: { tool: 'readiness', label: 'AI readiness' } }),
    ] : [
      row('cleanup/model', 'Remove 1 unused column', 'you', 'needs_you', { optional: true, jump: { tool: 'lineage', label: 'Lineage' } }),
      row('proposal/limits', 'Work around 1 Copilot limit', 'you', 'needs_you', { section: 'Trust', goal: 'ai', jump: { tool: 'readiness', label: 'AI readiness' } }),
      row('jev.ai-schema.not-finished', 'Jev has not finished choosing what Copilot sees', 'you', 'needs_you', { section: 'Trust', goal: 'ai', jump: { tool: 'readiness', label: 'AI readiness' } }),
    ]),
  ];
  return { modelName: 'Contoso', revision: 3, items, history: [], tables: [], readyDigest: 'rd1', assistantQueue: 'on',
    strip: { gradeNow: 'B', gradeAfter: null, bestPractice: 'C', tracks: [] } };
};

const viol = (ruleId, ruleName, objectRef, objectName, severity, canAutoFix, fixWithheld) =>
  ({ ruleId, ruleName, category: 'Performance', severity, objectRef, objectName, message: ruleName, canAutoFix, fixWithheld: AFTER ? fixWithheld : undefined });
const bpaCard = () => {
  const violations = [
    viol('HIDE_FOREIGN_KEYS', 'Hide foreign keys', 'column:Sales/CustomerKey', 'Sales[CustomerKey]', 2, true, false),
    viol('HIDE_FOREIGN_KEYS', 'Hide foreign keys', 'column:Sales/ProductKey', 'Sales[ProductKey]', 2, true, false),
    viol('HIDE_FACT_TABLE_COLUMNS', 'Hide fact table columns', 'column:Sales/Amount', 'Sales[Amount]', 2, true, false),
    viol('MARK_PRIMARY_KEYS', 'Mark primary keys', 'column:Customer/CustomerKey', 'Customer[CustomerKey]', 2, true, false),
    viol('AVOID_FLOATING_POINT_DATA_TYPES', 'Avoid floating point data types', 'column:Sales/Amount', 'Sales[Amount]', 2, true, true),
    viol('UNNECESSARY_COLUMNS', 'Remove unnecessary columns', 'column:Sales/Batch Id', 'Sales[Batch Id]', 2, false, false),
  ];
  return { ruleCount: 74, violationCount: violations.length, autoFixable: violations.filter((v) => v.canAutoFix && !v.fixWithheld).length, waivedCount: 0, violations, ruleErrors: [],
    bestPractice: 'C', bestPracticeScore: 74, bestPracticeWhy: 'Best practice C', categories: [] };
};
const afterPress = () => ({ scorecard: bpaCard(), applied: 0, unfixed: [{ ruleId: 'AVOID_FLOATING_POINT_DATA_TYPES', ruleName: 'Avoid floating point data types', objectRef: 'column:Sales/Amount', objectName: 'Sales[Amount]',
  reason: "This fix changes the column's data type, which can change the values it shows. Fix all leaves it as it is." }] });

const seed = (cfg) => `
const __cfg = ${JSON.stringify(cfg)};
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  v.setResponse('getModelPlan', () => __cfg.plan);
  v.setResponse('bpaScan', () => __cfg.card);
  v.setResponse('bpaFixAll', () => __cfg.press);
  v.setResponse('readingCard', () => ({ show: false }));
  v.setResponse('getNextStep', () => ({ id: 's1', on: false }));
} });
let __acquire; Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, get: () => __acquire, set: (original) => { __acquire = () => { const api = original(); const post = api.postMessage;
  api.postMessage = (m) => { post(m); if (m.type === 'studioReady') setTimeout(() => {
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true } }));
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'assistantCall', payload: { at: new Date().toISOString(), tool: 'get_model_plan', assistant: { key: 'unknown', name: 'Your assistant', initials: null } } } }));
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

for (const [theme, width] of [['dark', 1440], ['dark', 400]]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width, height: 960, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed({ plan: plan(), card: bpaCard(), press: afterPress() }));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan&compat=1604&dirty=1`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await new Promise((r) => setTimeout(r, 1800));
  await page.screenshot({ path: join(OUT, `plan-${LABEL}-${theme}-${width}.png`), fullPage: true });
  const facts = await page.evaluate(() => ({
    groups: [...document.querySelectorAll('[data-testid^="plan-group-count-"]')].map((e) => e.getAttribute('data-testid').replace('plan-group-count-', '') + '=' + e.textContent.trim()),
    compatRowDrawnByPage: !!document.querySelector('[data-testid="plan-row-compat"]'),
    overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
  }));
  console.log(`plan-${LABEL}-${theme}-${width}: ${JSON.stringify(facts)} errors=${JSON.stringify(errors)}`);
  await page.close();
}
// The Review page (Model quality): the Fix button's count, and what the page says after a press that fixed nothing.
for (const [theme, width] of [['dark', 1440]]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed({ plan: plan(), card: bpaCard(), press: afterPress() }));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=bpa`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="bpa-head"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await new Promise((r) => setTimeout(r, 900));
  await page.screenshot({ path: join(OUT, `review-${LABEL}-${theme}-${width}-a.png`) });
  const label = await page.evaluate(() => [...document.querySelectorAll('[data-testid="bpa-head"] button')].map((b) => b.textContent.trim()).find((t) => /^Fix \d+$/.test(t)) ?? null);
  await page.evaluate(() => [...document.querySelectorAll('[data-testid="bpa-head"] button')].find((b) => /^Fix \d+$/.test(b.textContent.trim()))?.click());
  await new Promise((r) => setTimeout(r, 900));
  await page.screenshot({ path: join(OUT, `review-${LABEL}-${theme}-${width}-b.png`) });
  const said = await page.evaluate(() => [...document.querySelectorAll('.sem-text-meta')].map((e) => e.textContent.trim()).find((t) => /Fix all/.test(t)) ?? null);
  console.log(`review-${LABEL}-${theme}-${width}: button=${JSON.stringify(label)} said=${JSON.stringify(said)} errors=${JSON.stringify(errors)}`);
  await page.close();
}
await browser.close(); server.close();
