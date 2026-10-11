// Acceptance driver for the m11/uat-ui lane: the "changed while you were editing" banner, in the REAL Studio and the REAL
// Properties grid, driven through the mock engine.
//
// UAT 2 Oct, class "an editor that saves a stale copy" (UAT-F-S06a-1-002 and UAT-F-S06b-2-007). Every scene states what it
// must show, and the run fails when one does not.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'uat-ui');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `uat-ui-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';
const failures = [];
const must = (ok, why) => { if (!ok) failures.push(why); };

// ---- scene 1: the Notes insight editor, the assistant rewrites the lesson while it is open ------------------------------
{
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 1100, height: 900, deviceScaleFactor: 2 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=knowledge`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('#knowledge-insights', { timeout: 20000 });
  await wait(1500);
  const clickIn = (label) => page.evaluate((text) => {
    const a = [...document.querySelectorAll('#knowledge-insights article')].find((x) => /Hiding technical FK|Assistant reworded/.test(x.textContent + (x.querySelector('textarea')?.value || '')));
    const b = [...(a?.querySelectorAll('button') ?? [])].find((x) => (x.textContent || '').trim() === text);
    if (b) b.click();
    return !!b;
  }, label);
  must(await clickIn('Edit'), 'the lesson has no Edit button');
  await wait(300);
  await page.click('#knowledge-insights textarea[aria-label="Insight text"]');
  await page.keyboard.type(' I added this sentence myself.');
  // the assistant rewords the lesson over MCP: the engine now answers with the new text, and the page hears an activity event
  await page.evaluate(() => {
    const h = window.__uishotHarness;
    h.setResponse('listInsights', {
      insights: [{ id: 'ki-77b0e9a4', text: 'Assistant reworded: hide technical key columns model-wide, it lifted Naming and Clarity by about 8 points.', kind: 'insight', keys: ['FK-HIDDEN', 'naming'], status: 'approved', score: 4, uses: 1, retrievals: 3, scope: 'project', provenance: { origin: 'agent', when: '2026-10-02T07:00:00Z' } }],
      skippedCorruptLines: 0,
    });
    h.dispatch({ type: 'activity', payload: { kind: 'edit_insight', tool: 'edit_insight', at: new Date().toISOString() } });
  });
  await wait(1200);
  const seen = await page.evaluate(() => {
    const a = document.querySelector('#knowledge-insights article textarea')?.closest('article');
    return { banner: (a?.querySelector('[role="alert"]')?.textContent || '').trim(), typed: a?.querySelector('textarea')?.value || '', buttons: [...(a?.querySelectorAll('[role="alert"] button') ?? [])].map((b) => b.textContent.trim()) };
  });
  must(/changed while you were editing/.test(seen.banner), `no banner after the assistant reworded the lesson: ${JSON.stringify(seen)}`);
  must(/I added this sentence myself/.test(seen.typed), 'the typed words were replaced');
  must(seen.buttons.join('|') === 'Keep mine|Use the new one', `banner buttons: ${seen.buttons}`);
  await (await page.$('#knowledge-insights article textarea')).evaluate((t) => t.closest('article').scrollIntoView({ block: 'center' }));
  await wait(200);
  await page.screenshot({ path: join(OUT, `insight-banner-${LABEL}.png`) });
  await page.evaluate(() => { const b = [...document.querySelectorAll('[role="alert"] button')].find((x) => x.textContent.trim() === 'Use the new one'); b?.click(); });
  await wait(500);
  const after = await page.evaluate(() => document.querySelector('#knowledge-insights article textarea')?.value || '');
  must(/Assistant reworded/.test(after), `Use the new one did not load the assistant's text: ${after}`);
  await page.screenshot({ path: join(OUT, `insight-use-new-${LABEL}.png`) });
  if (errors.length) failures.push(errors.join(' | '));
  await page.close();
}

// ---- scene 2: the Properties grid format-expression editor ---------------------------------------------------------------
{
  const page = await browser.newPage();
  await page.setViewport({ width: 420, height: 760, deviceScaleFactor: 2 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/propgrid.html?s=fxmoved`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(600);
  const seen = await page.evaluate(() => ({
    note: (document.querySelector('.fx-msg')?.textContent || '').trim(),
    typed: document.querySelector('.fx-ta')?.value || '',
    useNew: !!document.querySelector('[data-fx-usenew]'),
  }));
  must(/changed while you were editing/.test(seen.note), `no note in the grid: ${JSON.stringify(seen)}`);
  must(/my own format/.test(seen.typed), 'the typed expression was replaced');
  must(seen.useNew, 'no Use the new one button');
  await page.screenshot({ path: join(OUT, `propgrid-fxmoved-${LABEL}.png`) });
  await page.close();
}

await browser.close();
server.close();
writeFileSync(join(OUT, `results-${LABEL}.json`), JSON.stringify({ failures }, null, 2));
if (failures.length) { console.error('scenes did not show what they promised:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('all scenes showed what they promised');
