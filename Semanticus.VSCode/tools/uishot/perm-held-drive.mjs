// Screenshot of the Permissions page under the Locked preset with three held local requests, in the words the engine
// writes for them (the object and the new text). Lane m11/uat-undo: the person is told WHAT an assistant wants to change.
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/perm-held-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(here, 'shots');
mkdirSync(OUT, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((request, response) => {
  const path = normalize(join(webRoot, decodeURIComponent((request.url || '/').split('?')[0])));
  if (!path.startsWith(webRoot) || !existsSync(path) || statSync(path).isDirectory()) { response.writeHead(404); response.end('nf'); return; }
  response.writeHead(200, { 'content-type': types[extname(path).toLowerCase()] || 'application/octet-stream' });
  response.end(readFileSync(path));
});
await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox'] });
let failed = false;
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.setViewport({ width: 1366, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?live=1&tier=pro&perm=locked`, { waitUntil: 'networkidle0', timeout: 30000 });
  const clicked = (fn, arg) => page.evaluate(fn, arg);
  await clicked(() => { const chip = [...document.querySelectorAll('header button[aria-haspopup="menu"]')].find((b) => (b.getAttribute('title') || '').startsWith('Your assistant')); chip?.click(); });
  await page.waitForSelector('[role="menu"][aria-label="Your assistant"]');
  await clicked(() => { [...document.querySelectorAll('[role="menu"][aria-label="Your assistant"] button')].find((b) => (b.textContent || '').trim() === 'Permissions')?.click(); });
  await page.waitForFunction(() => document.body.innerText.includes('Your assistant wants to set the description'), { timeout: 15000 });
  const body = await page.evaluate(() => document.body.innerText);
  for (const want of ['wants to set the description of [Total Sales]', 'wants to delete [Old Margin]', 'wants to restore the model files'])
    if (!body.includes(want)) { failed = true; console.error('missing on the page: ' + want); }
  if (errors.length) { failed = true; console.error(errors.join('\n')); }
  // The held requests sit below the matrix: bring the first one to the top of the view before the shot.
  await page.evaluate(() => {
    const node = [...document.querySelectorAll('div, li, p, span')].find((n) => n.children.length === 0 && (n.textContent || '').startsWith('Your assistant wants to set the description'));
    let at = node; while (at && at.parentElement && at.scrollHeight <= at.clientHeight) at = at.parentElement;
    node?.scrollIntoView({ block: 'start' });
  });
  await new Promise((ok) => setTimeout(ok, 300));
  await page.screenshot({ path: join(OUT, 'permissions-locked-held.png') });
  console.log('wrote ' + join(OUT, 'permissions-locked-held.png'));
} finally {
  await browser.close();
  server.close();
}
process.exit(failed ? 1 : 0);
