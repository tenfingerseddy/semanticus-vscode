// Verifier, m11/sql-start at 78757da5 (28 Sep 2026): Connections' "From a SQL source" must land on the new model
// wizard's SQL step in the REAL product, where creating a model opens a NEW engine session.
//
// What the real host does after createModel (src/extension.ts, the modelSwapped branch of the Studio RPC relay): the
// engine answers with a new session, the host replies to the call and then posts 'reconnected' to every panel, and
// Studio reads sessionInfo again. The harness's own createModel keeps sessionId 'uishot', so sql-start-drive.mjs never
// sees the swap and passes. This driver makes the harness do what the host does and then checks where the page is.
//
// Measured on the real Studio (UAT lane j, the linux-x64 package of 78757da5): From a SQL source, name, Create and
// choose the source created the model and showed the Plan ("Nothing to do on the plan"), not the SQL step, both from
// Connections inside Studio and from the standalone Connections panel. This driver shows the same thing headlessly:
// it FAILS (exit 1) at 78757da5 and is left failing on purpose for the builder.
//
//   OUTDIR=<dir> node sql-start-swap-drive.mjs
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'sql-start-swap');
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

for (const theme of ['dark', 'light']) {
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?spec=empty`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(900);
  await applyTheme(page, theme);
  // The real engine: createModel opens a NEW session; the host then posts 'reconnected' to every panel.
  await page.evaluate(() => {
    const h = window.__uishotHarness;
    window.__createModelCalls = 0;
    h.setResponse('createModel', (p) => {
      window.__createModelCalls++;
      h.setResponse('sessionInfo', { sessionId: 'uishot-new', modelName: p[0], measures: 0, tables: 0, hasUnsavedChanges: false,
        liveConnected: false, currentAccount: 'megan@contoso.com', currentTenant: 'contoso.com' });
      setTimeout(() => window.postMessage({ type: 'reconnected', by: 'person' }, '*'), 0);
      return { sessionId: 'uishot-new', revision: 0, modelName: p[0], tables: 0, measures: 0, liveConnected: false };
    });
  });
  await page.click('[data-testid="connections-editing"]');
  await page.waitForSelector('[data-testid="hub-add-sql"]', { timeout: 15000 });
  await page.click('[data-testid="hub-add-sql"]');
  // The spec-first start (M11): From a SQL source opens the start plan's own form (source, name, empty folder) and makes NO blank model first, so
  // there is no name step and no session swap to survive. What this still guards: the click lands on the start form, never on the Plan, and
  // nothing creates a model behind the person's back. (The Plan behind the hub still reads as empty, so that is not asserted.)
  await page.waitForSelector('[data-testid="hub-start-sql"]', { timeout: 10000 });
  await wait(500);
  const formOpen = await page.evaluate(() => !!document.querySelector('[data-testid="hub-start-sql"]'));
  const createCalls = await page.evaluate(() => window.__createModelCalls);
  const onStep = await page.evaluate(() => !!document.querySelector('[data-testid="spec-sql-step"]'));
  const onPlan = await page.evaluate(() => /Nothing to do on the plan/.test(document.body.innerText));
  await page.screenshot({ path: join(OUT, `swap-${theme}.png`) });
  console.log(JSON.stringify({ theme, startFormOpen: formOpen, createModelCalls: createCalls, onSqlStep: onStep, onEmptyPlan: onPlan }));
  if (!formOpen) failures.push(`${theme}: From a SQL source did not open the start plan's form`);
  if (createCalls !== 0) failures.push(`${theme}: From a SQL source made a blank model first (${createCalls} createModel call(s))`);
  if (onStep) failures.push(`${theme}: From a SQL source opened the old SQL spec step, which needs a blank model`);
  await page.close();
}

await browser.close(); server.close();
console.log(failures.length ? `FAIL\n  ${failures.join('\n  ')}` : 'sql-start swap: From a SQL source opens the start form and makes no blank model');
process.exit(failures.length ? 1 : 0);
