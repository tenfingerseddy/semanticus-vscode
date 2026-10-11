// K20 (Kane, 8 Oct): Model quality shows the same big grade and per-category bars as AI readiness. Shot at 1440 wide, dark and
// light, over the harness's mock scan, next to AI readiness so the two can be compared by eye.
//
//   OUTDIR=<dir> SIDE=<before|after> node tools/uishot/bpa-bars-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'bpa-bars');
const SIDE = process.env.SIDE || 'after';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `bpabars-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
try {
  for (const [tab, name] of [['bpa', 'bpa'], ['readiness', 'ai']]) {
    for (const theme of ['dark', 'light']) {
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));
      await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=${tab}`, { waitUntil: 'networkidle0', timeout: 45000 });
      await wait(2000);
      await applyTheme(page, theme); await wait(300);
      const file = join(OUT, `${SIDE}-${name}-${theme}.png`);
      await page.screenshot({ path: file });
      console.log('  shot', file);
      if (tab === 'bpa' && SIDE === 'after') {
        const info = await page.evaluate(() => ({
          grade: document.querySelector('[data-testid="bpa-grade"]')?.textContent,
          bars: [...document.querySelectorAll('[data-testid="bpa-categories"] .sem-rag-bar')].length,
          overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
        }));
        console.log('  ', JSON.stringify(info));
        if (!info.bars) failures.push('no category bars');
        if (info.overflow) failures.push('page scrolls sideways');
      }
      if (errors.length) failures.push(...errors);
      await page.close();
    }
  }
} finally { await browser.close(); server.close(); }
if (failures.length) { console.error('FAIL', failures); process.exit(1); }
