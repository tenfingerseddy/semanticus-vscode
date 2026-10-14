// Proof for hero19 D7 (the red-check line on Publish) and D10 (the Pending changes copy). Serves THIS worktree's built bundle, sends the
// page the engine's activity event, reads the words off the page and shoots each state (dark).
//
//   OUTDIR=<dir> node tools/uishot/publish-gate-drive.mjs      (build the webview first: npm run build:webview)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || '/tmp/semanticus-joint-review/m15/publish-gate/shots';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); };

async function open(q, seed, w = 1440, h = 900) {
  const page = await browser.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  if (seed) await page.evaluateOnNewDocument((s) => { window.__UISHOT__ = s; }, seed);
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${q}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(3500);
  return page;
}
const text = (page, sel) => page.evaluate((s) => { const e = document.querySelector(s); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; }, sel);
const shot = (page, slug) => page.screenshot({ path: join(OUT, `${slug}.png`) });

// ---- D7: the red-check line on Publish ----
{
  const page = await open('live=1&conn=1&tab=deploy');
  const send = (payload) => page.evaluate((p) => window.postMessage({ type: 'activity', payload: p }, '*'), payload);
  expect(await text(page, '[data-testid="publish-asking"]') === null, 'before: no red-check line');
  await shot(page, 'd7-1-before');
  const words = 'Your assistant is asking you about 1 red check: Avoid IFERROR (Return Rate)';
  await send({ seq: 1, kind: 'publish_asking', origin: 'agent', ok: true, label: words });
  await wait(400);
  expect(await text(page, '[data-testid="publish-asking"]') === words, `the line: ${JSON.stringify(await text(page, '[data-testid="publish-asking"]'))}`);
  await shot(page, 'd7-2-asking');
  await send({ seq: 2, kind: 'run_dax', origin: 'agent', ok: true, label: 'x' });
  await wait(200);
  expect(await text(page, '[data-testid="publish-asking"]') === words, 'another event leaves the line');
  await send({ seq: 3, kind: 'publish_asking', origin: 'agent', ok: false });
  await wait(300);
  expect(await text(page, '[data-testid="publish-asking"]') === null, 'cleared by the engine');
  await shot(page, 'd7-3-cleared');
  expect(page.errors.length === 0, 'page errors: ' + page.errors.join(' | '));
  await page.close();
}

// ---- D10: the Pending changes page, every row applied ----
{
  const item = (o) => ({ id: o.id, objectRef: o.ref, objectName: o.name, kind: o.kind, source: 'jev', category: o.category, severity: 'Warning', risk: 'safe', ruleId: '', target: o.target,
    title: o.title, rationale: '', before: o.before, after: o.after, status: 'applied', generated: false });
  const fmt = '\\$#,0.00;(\\$#,0.00);\\$#,0.00';
  const plan = { planId: 'p1', scope: 'whole model', revision: 9,
    items: [
      item({ id: 'a1', ref: 'measure:Sales/Total Discount Amount', name: 'Total Discount Amount', kind: 'set_format_string', category: 'Measures', target: 'formatString', title: 'Format as ' + fmt, before: '(none)', after: fmt }),
      item({ id: 'a2', ref: 'measure:Sales/Total Discount Amount', name: 'Total Discount Amount', kind: 'set_description', category: 'Measures', target: 'description', title: 'Describe it', before: '(none)', after: 'The total of Discount Amount in Sales.' }),
      item({ id: 'a3', ref: 'measure:Sales/Average Unit Price', name: 'Average Unit Price', kind: 'set_format_string', category: 'Measures', target: 'formatString', title: 'Format as ' + fmt, before: '(none)', after: fmt }),
    ],
    summary: { total: 3, deterministic: 1, bpa: 0, ai: 0, jev: 2, renames: 0, needsContent: 0, approved: 0, rejected: 0, applied: 3, unverified: 0 } };
  const page = await open('conn=1&live=1&tier=pro&tab=optimize', { plan });
  await page.waitForFunction(() => (document.body.textContent || '').includes('Total Discount Amount'), { timeout: 15000 });
  const body = await page.evaluate(() => (document.body.innerText || ''));
  expect(!/AI-authored|deterministic/i.test(body), 'no engine words on the page');
  expect(!/0 approved/.test(body), 'no "0 approved"');
  expect(!/Nothing is written until you apply/.test(body), 'no "Nothing is written" when all are applied');
  expect(await text(page, '[data-testid="opt-summary"]') === '3 applied', `summary: ${JSON.stringify(await text(page, '[data-testid="opt-summary"]'))}`);
  expect(/^All applied\./.test(await text(page, '[data-testid="opt-note"]') || ''), 'the note says all applied');
  expect(await text(page, '[data-testid="opt-group-state"]') === '3 applied', `group: ${JSON.stringify(await text(page, '[data-testid="opt-group-state"]'))}`);
  expect(!body.includes('\\$'), 'no backslashes in the rows');
  expect(body.includes('$#,0.00;($#,0.00);$#,0.00'), 'the format reads as people read it');
  await shot(page, 'd10-pending-applied');
  expect(page.errors.length === 0, 'page errors: ' + page.errors.join(' | '));
  await page.close();
}

await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failures }, null, 1));
if (failures.length) { console.error(failures.join('\n')); process.exit(1); }
console.log('publish-gate: clean');
