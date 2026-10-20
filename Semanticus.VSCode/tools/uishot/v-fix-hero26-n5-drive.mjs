// Verifier v-fix-hero26 (N5): Validation after a simulated full restart. The engine already holds saved checks and a recorded run,
// but the page mounts before the model is back: listTests answers the way LocalEngine does with no open model (a SUCCESS with
// "No open model.", no definitions), listTestRuns has no runs and sessionInfo has no id. After ARRIVE ms the model is back:
// the real fixtures answer and the host posts 'reconnected' (extension.ts, on a model open). The DOM is sampled every 100 ms.
//   OUTDIR=<dir> node tools/uishot/v-fix-hero26-n5-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || '/tmp/vfh26-n5';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh26n5-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = [];
const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);
let CONTROL = '';
const HELD = ['listTests', 'listTestRuns', 'sessionInfo', 'connectionStatus'];

async function restart(arriveMs, mode) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  // Every read the page makes at mount is parked, so the "before the model" answers are in place before the first one lands.
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=tests&hold=${HELD.join(',')}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => !!window.__uishotHarness, { timeout: 20000 });
  await page.evaluate((held, mode) => {
    const h = window.__uishotHarness;
    window.__real = {};
    held.forEach((m) => { const raw = h.response(m, []); window.__real[m] = raw; });
    // LocalEngine.ListTestDefinitionsCoreAsync with no open model: a success, not an error.
    h.setResponse('listTests', mode === 'error' ? { __uishotError: 'No open model. Use open_model or connect first.' } : { note: 'No open model.' });
    h.setResponse('listTestRuns', { runs: [] });
    const si = window.__real.sessionInfo;
    const siNow = typeof si === 'function' ? si([]) : si;
    if (mode === 'swap') h.setResponse('sessionInfo', Object.assign({}, siNow, { sessionId: 'sOLDSESSION0000000000000000000000' }));
    else if (mode === 'same') { h.setResponse('sessionInfo', si); h.setResponse('connectionStatus', window.__real.connectionStatus); h.setResponse('listTests', { definitions: [] }); }
    else h.setResponse('sessionInfo', { open: false });
    if (mode !== 'same') h.setResponse('connectionStatus', { connected: false });
    held.forEach((m) => h.release(m));
  }, HELD, mode);
  const samples = [];
  const t0 = Date.now();
  const sample = () => page.evaluate(() => {
    const txt = (document.body?.innerText || '').replace(/\s+/g, ' ');
    return { noChecks: /No checks yet/.test(txt), noSaved: /No saved checks yet/.test(txt), loading: /Loading your saved checks/.test(txt),
      notRun: /not run yet/.test(txt), verdict: (document.querySelector('[data-testid="v-verdict"]')?.textContent || '').replace(/\s+/g, ' ').trim() };
  }).catch(() => null);
  let arrived = false, shot = false;
  while (Date.now() - t0 < arriveMs + 5000) {
    if (!arrived && Date.now() - t0 >= arriveMs) {
      arrived = true;
      await page.evaluate(() => {
        const h = window.__uishotHarness;
        Object.keys(window.__real).forEach((m) => h.setResponse(m, window.__real[m]));
        window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } }));
      });
      note(`${mode} ${arriveMs} ms: the model is back, 'reconnected' posted`);
    }
    if (!arrived && !shot && Date.now() - t0 >= arriveMs - 600) { shot = true; await page.screenshot({ path: join(OUT, `n5-${mode}-${arriveMs}-before-model.png`) }); }
    const s = await sample();
    if (s) samples.push({ at: Date.now() - t0, arrived, ...s });
    await wait(100);
  }
  const name = `n5-${mode}-${arriveMs}`;
  await page.screenshot({ path: join(OUT, `${name}-end.png`) });
  const flash = samples.filter((s) => s.noChecks || s.noSaved);
  const firstFlash = flash[0];
  note(`${name}: samples ${samples.length}; "No checks yet" seen in ${flash.length} samples${firstFlash ? ` from ${firstFlash.at} ms (before arrival: ${flash.filter((s) => !s.arrived).length}, after: ${flash.filter((s) => s.arrived).length})` : ''}`);
  const end = samples[samples.length - 1];
  note(`${name}: end verdict "${end?.verdict}"`);
  check(flash.length === 0, `${name}: never "No checks yet" / "No saved checks yet" while the engine holds checks`);
  check(end && !end.noChecks && /pass|check/.test(end.verdict), `${name}: after the model is back the saved checks show ("${end?.verdict}")`);
  check(end && end.verdict === CONTROL, `${name}: the checks show the same last run as a page opened on the model ("${end?.verdict}" vs "${CONTROL}")`);
  if (mode === 'same') {
    await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await wait(1500);
    const f = await sample();
    note(`${name}: after the window gets focus again: "${f?.verdict}"`);
    check(f && f.verdict === CONTROL, `${name}: a focus (tab switch back) reads the saved checks again ("${f?.verdict}")`);
  }
  check(errors.length === 0, `${name}: no page errors (${errors.join(' | ').slice(0, 160)})`);
  writeFileSync(join(OUT, `${name}-samples.json`), JSON.stringify(samples, null, 1));
  await page.close();
}

// The control: the same page with the model already open at mount.
async function control() {
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=tests`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  const v = await page.evaluate(() => (document.querySelector('[data-testid="v-verdict"]')?.textContent || '').replace(/\s+/g, ' ').trim());
  CONTROL = v; note(`control (model open at mount): verdict "${v}"`);
  await page.screenshot({ path: join(OUT, 'n5-control.png') });
  await page.close();
}

try {
  await control();
  for (const mode of ['empty', 'error', 'swap', 'same']) for (const ms of (mode === 'same' ? [1500] : [1500, 10000])) await restart(ms, mode);
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
