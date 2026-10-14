// copy21: the Pending changes rows and the Review list in plain words, as a headless shot. A pending row that is applied says "applied" once
// (its chip), and a rule name has no "[Category]" bracket. The engine is mocked: getPlan answers a plan of applied rows with the engine's own
// "applied" note and bracketed rule names, as the shipped rules write them.
//
//   OUTDIR=<dir> SIDE=before|after WIDTH=1090 node tools/uishot/copy21-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'copy21');
const SIDE = process.env.SIDE || 'after';
const WIDTH = Number(process.env.WIDTH) || 1090;
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = await new Promise((r) => { const s = createServer((req, res) => {
  const f = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!f.startsWith(webRoot) || !existsSync(f) || statSync(f).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(f).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(f));
}); s.listen(0, '127.0.0.1', () => r(s)); });
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--disable-gpu', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const item = (id, title, objectName, status, note, o = {}) => ({
  id, objectRef: 'column:Activity Event/' + id, objectName, kind: 'set_property', source: 'bpa', category: 'Formatting', severity: 'Warning', risk: 'safe',
  ruleId: 'SUMMARIZE', target: 'summarizeBy', title, rationale: '', before: 'Default', after: 'None', status, note, ...o,
});
const PLAN = { planId: 'plan-1', scope: 'whole model', revision: 9, items: [
  item('a1', '[Formatting] Do not summarize numeric columns', "'Activity Event'[Duration Ms]", 'applied', 'applied'),
  item('a2', '[Formatting] Do not summarize numeric columns', "'Activity Event'[Row Count]", 'applied', 'applied'),
  item('a3', 'Set geographic data category', 'Country Name', 'applied', 'applied', { source: 'deterministic', before: '(none)', after: 'Country' }),
  item('a4', '[Performance] Remove unused columns', "'Activity Event'[Spare]", 'applied', 'applied (checked again first that nothing uses it)', { before: undefined, after: undefined }),
  item('p1', '[Formatting] Hide foreign-key columns', "Sales[CustomerKey]", 'proposed', undefined),
], summary: { total: 5, deterministic: 1, bpa: 4, ai: 0, jev: 0, renames: 0, needsContent: 0, approved: 0, rejected: 0, applied: 4, unverified: 0 } };
const seed = `
const __PLAN = ${JSON.stringify(PLAN)};
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => { __h = v; v.setResponse('getPlan', () => __PLAN); v.setResponse('getChangePlan', () => __PLAN); } });
`;
let failed = 0;
try {
  for (const [tab, name, readyText] of [['optimize', 'pending', 'Do not summarize'], ['bpa', 'review', 'Avoid bi-directional']]) {
    const page = await browser.newPage();
    page.on('pageerror', (e) => { failed += 1; console.error('pageerror: ' + e.message); });
    await page.setViewport({ width: WIDTH, height: 900, deviceScaleFactor: 1 });
    if (tab === 'optimize') await page.evaluateOnNewDocument(seed);
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=${tab}`, { waitUntil: 'networkidle0', timeout: 45000 });
    await page.waitForFunction((t) => (document.body.textContent || '').includes(t), { timeout: 20000 }, readyText).catch(() => { failed += 1; console.error('  FAIL ' + name + ' never showed ' + readyText); });
    await applyTheme(page, 'dark'); await wait(500);
    await page.screenshot({ path: join(OUT, `${SIDE}-${name}-dark-${WIDTH}.png`) });
    const text = await page.evaluate(() => document.body.innerText);
    const brackets = (text.match(/\[(Formatting|Performance|Maintenance|Naming|Metadata)\]/g) ?? []).length;
    const applied = name === 'pending' ? (text.match(/\bapplied\b/gi) ?? []).length : null;
    console.log(`${SIDE} ${name} ${WIDTH}: bracketed rule words ${brackets}` + (applied === null ? '' : `, the word "applied" ${applied} times on the page`));
    await page.close();
  }
} finally { await browser.close(); server.close(); }
process.exit(failed ? 1 : 0);
