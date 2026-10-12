import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync, mkdirSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = process.env.OUTDIR || '/tmp/semanticus-joint-review/m14/said';
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer((req, res) => {
  const file = normalize(join(root, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!file.startsWith(root) || !existsSync(file) || statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[extname(file)] || 'application/octet-stream' });
  res.end(readFileSync(file));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'] });
const page = await browser.newPage();
const failures = [];
page.on('pageerror', (e) => failures.push(e.message));
await page.setViewport({ width: 1440, height: 900 });
const send = (type, payload) => page.evaluate((type, payload) => window.__uishotHarness.dispatch({ type, payload }), type, payload);
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const call = (id) => send('assistantCall', { sessionId: 'uishot', call: id, tool: 'set_properties', at: new Date().toISOString() });
const change = (revision, refs, extra = {}) => send('didChange', { sessionId: 'uishot', revision,
  by: 'assistant', origin: 'agent', undo: { canUndo: true },
  deltas: refs.map((ref) => ({ ref, kind: 'update', props: ['Description'] })), ...extra });
const text = () => page.$eval('[data-testid="said-line"] [role="status"]', (el) => el.textContent);
try {
  await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?g=mini`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('header.studio-chrome');
  await page.evaluate(() => {
    [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Explore').click();
  });
  await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Diagram').click());
  await page.waitForSelector('.react-flow__node');
  await wait(600);
  await page.screenshot({ path: join(out, 'before-sample.png') });
  await call('descriptions');
  const refs = ['table:Sales', 'table:Product', ...Array.from({ length: 22 }, (_, i) => `measure:Sales/Measure ${i + 1}`)];
  await change(105, refs.slice(0, 12));
  await change(106, [...refs.slice(12), 'table:Sales']);
  await page.waitForSelector('[data-testid="said-line"]');
  assert.equal(await text(), 'Your assistant changed 24 descriptions.');
  assert.equal(await page.$$eval('[data-testid="said-line"]', (els) => els.length), 1);
  assert.equal(await page.$$eval('[data-object-ref="table:Sales"].sem-flash', (els) => els.length), 1);
  // Hold the animation's visible frame for the PNG. The real three-second timer still removes the class.
  await page.evaluate(() => document.getAnimations().filter((a) => a.animationName === 'sem-assistant-flash')
    .forEach((a) => { a.currentTime = 500; a.pause(); }));
  const flash = await page.$eval('[data-object-ref="table:Sales"]', (el) => ({
    animation: getComputedStyle(el).animationName, shadow: getComputedStyle(el).boxShadow,
  }));
  assert.equal(flash.animation, 'sem-assistant-flash');
  assert.notEqual(flash.shadow, 'none');
  console.log('flash paint:', JSON.stringify(flash));
  await page.screenshot({ path: join(out, 'after.png') });
  await wait(8200);
  assert.equal(await page.$('[data-testid="said-line"]'), null);
  assert.equal(await page.$('.sem-flash'), null);
  await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Your assistant').click());
  await page.evaluate(() => [...document.querySelectorAll('[role="menuitem"]')].find((b) => b.textContent.includes('Live activity')).click());
  await page.waitForFunction(() => document.body.textContent.includes('Your assistant changed 24 descriptions.'));
  await page.screenshot({ path: join(out, 'feed.png') });
  await page.keyboard.press('Escape');
  await change(107, ['table:Sales'], { by: 'you', origin: 'human' });
  assert.equal(await page.$('[data-testid="said-line"]'), null);
  await call('undo-test');
  await change(108, ['measure:Sales/Net Sales']);
  await page.waitForSelector('[data-testid="said-undo"]');
  await page.evaluate(() => window.__uishotHarness.setResponse('getPlanHistory', { sessionId: 'uishot', rows: [
    { id: 'edit-108', kind: 'edit', sessionId: 'uishot', revision: 108, undoable: true },
  ] }));
  await page.click('[data-testid="said-undo"]');
  await page.waitForFunction(() => window.__uishotHarness.messages.some((m) => m.method === 'undo'));
  const undo = await page.evaluate(() => window.__uishotHarness.messages.find((m) => m.method === 'undo'));
  assert.deepEqual(undo.params, ['human', 'edit-108']);
  await page.waitForFunction(() => !document.querySelector('[data-testid="said-line"]'));
  await call('later-writer');
  await change(109, ['measure:Sales/Net Sales']);
  await page.waitForSelector('[data-testid="said-undo"]');
  await page.evaluate(() => window.__uishotHarness.setResponse('getPlanHistory', { sessionId: 'uishot', rows: [
    { id: 'edit-109', kind: 'edit', sessionId: 'uishot', revision: 109, undoable: true },
  ] }));
  await change(110, ['table:Sales'], { by: 'you', origin: 'human' });
  await page.click('[data-testid="said-undo"]');
  await page.waitForSelector('[data-testid="plan-history"]');
  assert.equal(await page.evaluate(() => window.__uishotHarness.messages.filter((m) => m.method === 'undo').length), 1);
  assert.deepEqual(failures, []);
  // A separate capture holds expiry timers. The behavioral page above uses the real clock throughout.
  const shot = await browser.newPage();
  await shot.setViewport({ width: 1440, height: 900 });
  await shot.evaluateOnNewDocument(() => {
    const schedule = window.setTimeout.bind(window);
    window.setTimeout = (fn, ms, ...args) => schedule(fn, [3000, 4000, 8000].includes(ms) ? 600000 : ms, ...args);
  });
  await shot.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?g=mini`, { waitUntil: 'networkidle2' });
  await shot.waitForSelector('header.studio-chrome');
  await shot.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Explore').click());
  await shot.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Diagram').click());
  await shot.waitForSelector('.react-flow__node');
  await shot.screenshot({ path: join(out, 'before-sample.png') });
  await shot.evaluate((refs) => {
    const dispatch = window.__uishotHarness.dispatch;
    dispatch({ type: 'assistantCall', payload: { sessionId: 'uishot', call: 'capture' } });
    dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 105, by: 'assistant', origin: 'agent',
      deltas: refs.map((ref) => ({ ref, kind: 'update', props: ['Description'] })) } });
  }, refs);
  await shot.waitForSelector('[data-testid="said-line"]');
  await shot.evaluate(() => document.getAnimations().filter((a) => a.animationName === 'sem-assistant-flash')
    .forEach((a) => { a.currentTime = 500; a.pause(); }));
  await shot.screenshot({ path: join(out, 'after.png') });
  assert.equal(await shot.$eval('[data-testid="said-line"] [role="status"]', (el) => el.textContent), 'Your assistant changed 24 descriptions.');
  await shot.close();
  console.log('PASS grouped copy, on-screen flash, expiry, feed, human silence, exact Undo, later writer opens History');
} finally { await browser.close(); server.close(); }
