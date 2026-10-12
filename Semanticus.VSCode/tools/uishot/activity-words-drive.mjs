import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync } from 'node:fs';
import { dirname, resolve, join, normalize, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = '/tmp/semanticus-joint-review/m14/activity-words';
const before = process.argv.includes('--before');
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
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900 });
  await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?g=mini`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('header.studio-chrome');
  await page.evaluate(() => {
    const send = (type, payload) => window.__uishotHarness.dispatch({ type, payload });
    for (let i = 1; i <= 50; i++) {
      send('assistantCall', { sessionId: 'uishot', call: `syn-${i}`, tool: 'set_synonyms' });
      send('didChange', { sessionId: 'uishot', revision: 100 + i, origin: 'agent', by: 'assistant', label: 'set synonyms',
        deltas: [{ ref: 'culture:en-US', kind: 'update', props: ['Content'] }],
        words: { verb: 'added synonyms to', noun: 'fields', count: 1, targets: [`measure:Sales/Field ${i}`],
          sentence: `Your assistant added synonyms to Field ${i}.` } });
    }
  });
  await page.waitForSelector('[data-testid="said-line"]');
  if (!before) assert.equal(await page.$eval('[data-testid="said-line"] [role="status"]', el => el.textContent), 'Your assistant added synonyms to 50 fields.');
  await page.screenshot({ path: join(out, before ? 'said-before.png' : 'said-after.png') });
  await new Promise(r => setTimeout(r, 8200));
  await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Your assistant').click());
  await page.waitForSelector('.studio-chip-menu');
  await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith('Live activity')).click());
  await page.waitForFunction(() => document.querySelector('.studio-header-pop')?.textContent.includes('Live activity ·'));
  if (!before) {
    const synonyms = await page.$$eval('.studio-header-pop .text-xs.font-medium', els => els.map(el => el.textContent).filter(text => text.includes('added synonyms to')));
    assert.deepEqual(synonyms, ['Your assistant added synonyms to 50 fields.']);
  }
  await page.screenshot({ path: join(out, before ? 'panel-before.png' : 'panel-after.png') });
  if (!before) {
    await page.evaluate(() => {
      [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Your assistant').click();
      const send = (type, payload) => window.__uishotHarness.dispatch({ type, payload });
      send('didChange', { sessionId: 'uishot', revision: 151, origin: 'human', by: 'you', deltas: [{ ref: 'table:Sales', kind: 'update' }] });
      send('assistantCall', { sessionId: 'uishot', call: 'after-person', tool: 'set_synonyms' });
      send('didChange', { sessionId: 'uishot', revision: 152, origin: 'agent', by: 'assistant',
        deltas: [{ ref: 'culture:en-US', kind: 'update' }], words: { verb: 'added synonyms to', noun: 'fields', count: 1,
          targets: ['measure:Sales/Other'], sentence: 'Your assistant added synonyms to Other.' } });
    });
    await page.waitForSelector('[data-testid="said-line"]');
    assert.equal(await page.$eval('[data-testid="said-line"] [role="status"]', el => el.textContent), 'Your assistant added synonyms to Other.');
    await new Promise(r => setTimeout(r, 8200));
    await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Your assistant').click());
    await page.waitForSelector('.studio-chip-menu');
    await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith('Live activity')).click());
    await page.waitForFunction(() => document.querySelector('.studio-header-pop')?.textContent.includes('Live activity ·'));
    const synonyms = await page.$$eval('.studio-header-pop .text-xs.font-medium', els => els.map(el => el.textContent).filter(text => text.includes('added synonyms to')));
    assert.deepEqual(synonyms, ['Your assistant added synonyms to Other.', 'Your assistant added synonyms to 50 fields.']);
  }
  assert.deepEqual(errors, []);
  console.log(`PASS ${before ? 'before' : 'after'} activity screenshots and browser checks`);
} finally { await browser.close(); server.close(); }
