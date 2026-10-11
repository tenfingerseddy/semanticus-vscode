// verify (m11/sol6-ui round 2, item 3): in the Properties grid, "Keep mine" does not keep the person's words. After the stored value moves under
// typed words, the grid shows Keep mine / Use the new one. Keep mine hides the note and leaves the words in the box, but nothing writes them
// when the person leaves the box (click elsewhere, Tab), and a Save Model with the focus elsewhere neither writes them nor says so. The box
// keeps showing the words as if they were the value until the selection changes; then they are gone. The model holds the other writer's text.
// (The builder's scene A passes only because it dispatches a synthetic 'change' event after Keep mine; a real blur sends none.)
// This driver fails until Keep mine writes the words (naming the new value) or leaves them visibly unwritten and a Save says so.
// Runs the REAL media/propgrid/propgrid.js in the headless harness page. Screenshots go to OUTDIR (default shots/verify-sol6r2-keep-mine).
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-sol6r2-keep-mine');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vsol6r2-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const must = (ok, why) => { if (!ok) failures.push(why); };

async function grid(scenario) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 420, height: 760, deviceScaleFactor: 2 });
  // Record everything the grid posts to the host: the harness's mock host swallows it.
  await page.evaluateOnNewDocument(() => {
    window.__posted = [];
    let installed;
    Object.defineProperty(window, 'acquireVsCodeApi', {
      configurable: true,
      get() { return installed; },
      set(factory) { installed = () => { const api = factory(); const real = api.postMessage; api.postMessage = (m) => { window.__posted.push(m); return real(m); }; return api; }; },
    });
  });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/propgrid.html?s=${scenario}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(400);
  return { page, errors };
}
const send = (page, msg) => page.evaluate((m) => window.dispatchEvent(new MessageEvent('message', { data: m })), msg);
const posted = (page, type) => page.evaluate((t) => window.__posted.filter((m) => m.type === t), type);
const P = (name, displayName, category, kind, value) => ({ name, displayName, category, kind, value, description: displayName, options: [], readOnly: false, varies: false });
/** The grid's load for the measure, with the description the assistant has now. */
const measureLoad = (description) => ({ type: 'load', key: '["measure:Sales/Total Sales"]', name: 'Total Sales', multi: false, props: [
  P('Name', 'Name', 'Basic', 'string', 'Total Sales'), P('Description', 'Description', 'Basic', 'string', description),
  P('DisplayFolder', 'Display Folder', 'Basic', 'string', 'Sales\\KPIs'), P('FormatString', 'Format String', 'Formatting', 'string', '#,##0.00')] });

const descOf = (page) => page.evaluate(() => document.querySelector('textarea[data-prop="Description"]')?.value);
{
  const { page, errors } = await grid('measure');
  await page.click('textarea[data-prop="Description"]');
  await page.keyboard.down('Control'); await page.keyboard.press('End'); await page.keyboard.up('Control');
  await page.keyboard.type(' MY TYPED WORDS');
  const typed = await descOf(page);
  await send(page, measureLoad('THE ASSISTANT REWROTE THIS'));   // the assistant changes the description; the model change reloads the grid
  await wait(200);
  must(await page.$('[data-keep]'), 'no Keep mine button after the stored value moved');
  await page.click('[data-keep]');   // Keep mine
  await wait(150);
  await page.click('textarea[data-prop="Description"]');   // back into the box, then leave it the ordinary way
  await page.click('input[data-prop="DisplayFolder"]');
  await wait(150);
  const shown = await descOf(page);
  await page.screenshot({ path: join(OUT, 'keep-mine-then-leave.png') });
  const sets = await posted(page, 'set');
  must(!(shown === typed && sets.length === 0), `Keep mine then leaving the box: the box shows the words but nothing was written (sets: ${JSON.stringify(sets)})`);
  // Save Model with the focus elsewhere: the words must be written or the Save must say they were not.
  await send(page, { type: 'applyFocused', id: 51 });
  await wait(150);
  const applied = (await posted(page, 'applied')).find((m) => m.id === 51);
  const setsAfterSave = await posted(page, 'set');
  must(setsAfterSave.some((m) => m.name === 'Description' && m.value === typed) || !!applied?.notice,
    `Save Model after Keep mine: words neither written nor reported (applied: ${JSON.stringify(applied)}, sets: ${JSON.stringify(setsAfterSave)})`);
  if (errors.length) failures.push(errors.join(' | '));
  await page.close();
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failures }, null, 2));
if (failures.length) { console.error('verify: Keep mine did not keep the words:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('verify: Keep mine kept the words');
