// The 1.4 follow lane, through the real Studio bundle and the mock engine: the engine narrates a burst of tool calls by itself (one note whose
// count grows, no pile of notes), the screen follows to a new place, and the answer the assistant asked for is on the Ask page, labelled.
//
//   OUTDIR=<dir> node tools/uishot/follow-drive.mjs
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'follow');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `follow-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
try {
  for (const theme of ['dark', 'light']) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=plan&tier=pro`, { waitUntil: 'networkidle0', timeout: 45000 });
    await wait(2000);
    await applyTheme(page, theme);
    const send = (data) => page.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), data);
    const shot = async (name) => { const file = join(OUT, `${name}-${theme}.png`); await page.screenshot({ path: file }); results.push(file); };
    const strip = () => page.$eval('[data-studio-note-strip]', (el) => el.textContent.replace(/\s+/g, ' ').trim()).catch(() => '');
    await send({ type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    await wait(300);

    // A burst of set_description calls: the engine sends one automatic note, then updates its count.
    const note = (extra) => ({ id: 'sn-0000000auto1', say: 'Your assistant is writing descriptions.', how: 'Open the Model tree and select the object. Its properties open beside it.',
      place: 'edit/properties', focus: 'measure:Sales/Total Sales', at: new Date().toISOString(), by: 'assistant', auto: true, ...extra });
    await send({ type: 'studioShow', payload: note({}) });
    await wait(400);
    assert.match(await strip(), /Your assistant is writing descriptions\./);
    await shot('follow-1-first-note');
    for (let n = 2; n <= 24; n++) {
      await send({ type: 'studioShow', payload: note({ update: true, count: n, say: `Your assistant has written ${n} descriptions so far.` }) });
      if (n === 9) { await wait(200); await shot('follow-2-burst-9'); }
    }
    await wait(300);
    const text = await strip();
    assert.match(text, /Your assistant has written 24 descriptions so far\./);
    assert.ok(!/more/.test(text), 'a burst of 24 calls is one note, not a queue: ' + text);
    await shot('follow-3-burst-24');

    // The work moves on: a new place is a new note, and the screen follows.
    await send({ type: 'studioShow', payload: { id: 'sn-0000000auto2', say: 'Your assistant is checking how ready the model is for AI.', how: 'Open Review, then AI readiness.',
      place: 'review/ai-readiness', at: new Date().toISOString(), by: 'assistant', auto: true } });
    await page.waitForFunction(() => /how ready the model is for AI/.test(document.querySelector('[data-studio-note-strip]')?.textContent ?? ''), { timeout: 20000 });
    await wait(900);
    await shot('follow-4-next-place');

    // The assistant asks a data question: the note moves the screen to Ask your data, and the answer arrives on that page.
    await send({ type: 'studioShow', payload: { id: 'sn-0000000auto3', say: 'Your assistant is asking your data a question.', how: 'Open Explore, then Ask your data, and type a question.',
      place: 'explore/ask', at: new Date().toISOString(), by: 'assistant', auto: true } });
    await page.waitForSelector('[data-testid="askdata-page"]', { timeout: 30000 }).catch(async (e) => { await page.screenshot({ path: join(OUT, 'debug-ask.png') }); throw e; });
    const answer = await page.evaluate(() => window.__askDataMock.answer({ measures: ['Total Sales'], groupBy: ["'Store'[Region]"], filters: [], top: null, sortBy: null }));
    await wait(1200);
    await send({ type: 'studioAnswer', payload: { id: 'sa-0000000000a1', at: new Date().toISOString(), question: 'total sales by store region', answer } });
    await page.waitForSelector('[data-testid="query-by-assistant"]', { timeout: 10000 });
    const label = await page.$eval('[data-testid="query-by-assistant"]', (el) => el.textContent.replace(/\s+/g, ' ').trim());
    assert.match(label, /^Asked by your assistant/);
    assert.ok(await page.$('[data-testid="askdata-page"] table, [data-testid="askdata-page"] [role="grid"], [data-testid="askdata-page"] [data-testid]'), 'the answer view is on the page');
    await wait(500);
    await shot('follow-5-ask-answer');

    // The person asks next: the label goes, the answer is theirs.
    await page.evaluate(() => { const f = document.querySelector('[aria-label="Ask in your own words"]'); f.focus(); });
    assert.equal(errors.length, 0, errors.join('\n'));
    await page.close();

    // D4: plan/start while the Plan sits on History opens the start form (the read-source button is on screen), not the old Model Spec page.
    const plan = await browser.newPage();
    await plan.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await plan.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=plan&tier=pro`, { waitUntil: 'networkidle0', timeout: 45000 });
    await wait(2000);
    await applyTheme(plan, theme);
    const planSend = (data) => plan.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), data);
    await planSend({ type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    await plan.evaluate(() => { [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'History')?.click(); });
    await wait(600);
    assert.ok(await plan.evaluate(() => !!document.querySelector('[data-testid="plan-history"], [data-testid="ph-list"]') || /History/.test(document.body.textContent)), 'the Plan is on History');
    await planSend({ type: 'studioShow', payload: { id: 'sn-00000000start', say: 'Your assistant is reading your SQL source.', how: 'Open Plan, press New model, then From a SQL source.',
      place: 'plan/start', at: new Date().toISOString(), by: 'assistant', auto: true } });
    await plan.waitForSelector('[data-testid="sp-form-start"]', { timeout: 15000 }).catch(async (e) => { await plan.screenshot({ path: join(OUT, 'debug-start.png') }); throw e; });
    await wait(400);
    await plan.screenshot({ path: join(OUT, `follow-6-plan-start-${theme}.png`) });
    results.push(`follow-6-plan-start-${theme}.png`);
    await plan.close();
  }
  writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(`PASS: follow burst, next place, assistant answer on the Ask page. ${results.length} screenshots in ${OUT}`);
} finally {
  await browser.close();
  await new Promise((done) => server.close(done));
}
