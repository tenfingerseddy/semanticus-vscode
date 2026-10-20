// Verifier v-fix-hero26 (N3): the Validation page fed the run the real engine made (VFixHero26PipeTests wrote it: two saved year
// checks, eight measures with no saved check, run over the agent pipe). The page's verdict, its Run history panel and the
// assistant's note must say the same checks as the tool headline, the report and the kept report (WIRE carries those).
//   WIRE=<wire-n3.json> OUTDIR=<dir> node tools/uishot/v-fix-hero26-n3-drive.mjs
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
const OUT = process.env.OUTDIR || '/tmp/vfh26-n3';
const WIRE = JSON.parse(readFileSync(process.env.WIRE, 'utf8'));
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh26n3-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = [];
const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);
const camel = (o) => JSON.parse(JSON.stringify(o));
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=tests&hold=listTests,listTestRuns,listTableMappings`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => !!window.__uishotHarness, { timeout: 20000 });
  await page.evaluate((tests, run) => {
    const h = window.__uishotHarness;
    h.setResponse('listTests', tests);
    h.setResponse('listTestRuns', { runs: [] });
    h.setResponse('runTests', run);
    h.setResponse('listTableMappings', { rows: [] });   // the engine's answer for this model: no table mappings
    h.release('listTests'); h.release('listTestRuns'); h.release('listTableMappings');
  }, camel(WIRE.tests), camel(WIRE.run));
  await wait(2500);
  await page.click('[data-testid="v-run"]'); await wait(2500);
  const verdict = await page.evaluate(() => (document.querySelector('[data-testid="v-verdict"]')?.textContent || '').replace(/\s+/g, ' ').trim());
  note(`page verdict after Run checks: "${verdict}"`);
  await page.screenshot({ path: join(OUT, 'n3-page.png') });
  // The assistant's run reaches the page as the run_tests activity, with its label (the assistant's note).
  await page.evaluate((run, label) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: { seq: 77, kind: 'run_tests', origin: 'agent', label, ok: true, result: run } } })), camel(WIRE.run), WIRE.note);
  await wait(2000);
  const body = await page.evaluate(() => (document.body.innerText || '').replace(/\s+/g, ' '));
  const noteShown = (body.match(/Your assistant[^.]*\.[^.]*\./) || [''])[0];
  note(`after the assistant's run_tests: verdict "${await page.evaluate(() => (document.querySelector('[data-testid="v-verdict"]')?.textContent || '').replace(/\s+/g, ' ').trim())}"; note text "${noteShown}"`);
  await page.screenshot({ path: join(OUT, 'n3-after-assistant.png') });
  const any = /(\d+) of (\d+) check/.exec(body);
  note(`counts in page text: ${[...body.matchAll(/\d+ of \d+[^.,:]{0,24}/g)].map((m) => m[0]).join(' | ')}`);
  // Run history panel
  const opened = await page.evaluate(() => { const b = document.querySelector('[data-testid="v-when"]'); if (b) { b.click(); return true; } return false; });
  await wait(1500);
  const panel = await page.evaluate(() => { const p = [...document.querySelectorAll('section, div')].find((x) => /Run history and grade/.test(x.querySelector('h2,h3,header,.sem-panel-title')?.textContent || '')); return (p?.textContent || document.body.innerText || '').replace(/\s+/g, ' '); });
  note(`Run history opened: ${opened}; its counts: ${[...panel.matchAll(/\d+ of \d+[^.]{0,60}/g)].map((m) => m[0]).slice(0, 4).join(' | ')}`);
  await page.screenshot({ path: join(OUT, 'n3-history.png') });
  const want = /^(\d+) of (\d+)/.exec(WIRE.headline);
  const pageNums = /(\d+)\s*of\s*(\d+)\s*pass/.exec(verdict);
  check(pageNums && want && pageNums[1] === want[1] && pageNums[2] === want[2], `page verdict "${verdict}" says the same checks as the tool headline "${WIRE.headline}"`);
  check(new RegExp(`${want[1]} of ${want[2]}`).test(WIRE.note), `assistant's note "${WIRE.note}"`);
  check(new RegExp(`${want[1]} of ${want[2]}`).test(WIRE.reportCheckLine), `report "${WIRE.reportCheckLine}"`);
  check(WIRE.kept === `${want[1]} of ${want[2]}`, `kept report "${WIRE.kept}"`);
  check(new RegExp(`${want[1]} of ${want[2]}`).test(panel), `Run history panel says ${want[1]} of ${want[2]}`);
  check(!/1 differ\b/.test(body + panel), 'no "1 differ" anywhere');
  check(errors.length === 0, `no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
