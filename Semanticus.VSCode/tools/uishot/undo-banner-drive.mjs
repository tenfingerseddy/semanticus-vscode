// The "Undone." banner must not move anything under the pointer (m17 P1, v2-m17 report lines 83-84: it pushed the page down 37 px,
// so the next press hit nothing). Real Studio bundle, engine mocked. For every theme and width: note where a control below the
// strip sits, raise an Undo, wait for the banner, and require the control and the page to be exactly where they were.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync, mkdirSync } from 'node:fs';
import { dirname, resolve, join, normalize, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = process.env.UNDO_BANNER_OUT || join(tmpdir(), 'uishot-undo-banner');
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer((req, res) => {
  const file = normalize(join(root, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!file.startsWith(root) || !existsSync(file) || statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[extname(file)] || 'application/octet-stream' });
  const content = readFileSync(file);
  res.end(file.endsWith('harness.html') ? content.toString().replace('var EDITS = [', 'var EDITS = cfg.voice ? [] : [') : content);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'] });
const errors = [];
// Where the page's content starts, and where the first control under the strip sits.
const probe = (page) => page.evaluate(() => {
  const strip = document.querySelector('[data-testid="note-strip"]');
  const stripBottom = strip ? strip.getBoundingClientRect().bottom : 0;
  const controls = [...document.querySelectorAll('main button, [role="tabpanel"] button, .studio-body button, button')]
    .filter((b) => { const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.top >= stripBottom && r.top < innerHeight - 40 && !b.closest('header.studio-chrome') && !b.closest('[data-testid="note-strip"]'); });
  const c = controls[0];
  const r = c ? c.getBoundingClientRect() : null;
  return { control: r ? { x: Math.round(r.left), y: Math.round(r.top), label: (c.textContent || c.getAttribute('aria-label') || '').trim().slice(0, 30) } : null, scrollH: document.documentElement.scrollHeight, bodyTop: Math.round(document.body.getBoundingClientRect().top) };
});
try {
  for (const width of [1440, 1090]) {
    for (const theme of ['dark', 'light']) {
      const page = await browser.newPage();
      page.on('pageerror', (e) => errors.push(e.message));
      await page.setViewport({ width, height: 900 });
      await page.evaluateOnNewDocument(() => { window.__UISHOT__ = { voice: true }; });
      await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?g=mini`, { waitUntil: 'networkidle2' });
      await page.waitForSelector('header.studio-chrome');
      await applyTheme(page, theme);
      await new Promise((r) => setTimeout(r, 400));
      const before = await probe(page);
      assert.ok(before.control, 'no control below the strip to watch');
      await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 104, origin: 'human', replay: 'undo', deltas: [{ ref: 'measure:Sales/Test', kind: 'remove' }] } }));
      await page.waitForSelector('[data-testid="personality-message"]');
      const during = await probe(page);
      const banner = await page.$eval('[data-testid="personality-message"]', (el) => ({ text: el.textContent.trim(), h: Math.round(el.getBoundingClientRect().height) }));
      await page.screenshot({ path: join(out, `undo-banner-${width}-${theme}.png`) });
      const sideways = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      console.log(width, theme, JSON.stringify({ before, during, banner, sideways }));
      assert.deepEqual(during.control, before.control, `${width} ${theme}: the banner moved the control under the pointer (${banner.h} px banner)`);
      assert.equal(sideways, 0, `${width} ${theme}: the page scrolls sideways`);
      await page.close();
    }
  }
  assert.deepEqual(errors, []);
  console.log('PASS undo banner does not move the page');
} finally { await browser.close(); server.close(); }
