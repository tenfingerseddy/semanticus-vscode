// The Calendars page (Edit, Advanced modelling), driven for real on the uishot mock engine: every press the plan promises is made and its
// result checked, at 1440x900 and 1090x800, dark and light, with the first-view measure, the alignment measure, console errors and sideways
// scroll. The mock engine's plan is typed (tools/uishot/harness.html, "the calendar mock"); the real plan is the engine's.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/calendars-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'calendars');
mkdirSync(OUT, { recursive: true });
const ALIGN = readFileSync(join(here, 'calendars-align.js'), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const MEASURE = readFileSync(join(here, 'measure.js'), 'utf8');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `calendars-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 240000,
  args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const fails = [];
const log = { errors: [], sideways: 0, alignFaults: 0, steps: 0, measures: {} };
const expect = (ok, msg) => { if (!ok) fails.push(msg); return ok; };

async function open(cal, w, h) {
  const p = await browser.newPage();
  p.on('pageerror', (e) => log.errors.push(`${cal}-${w}: ${e.message}`));
  p.on('console', (m) => { if (m.type() === 'error') log.errors.push(`${cal}-${w}: ${m.text()}`); });
  await p.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await p.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=advmodels&cal=${cal}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(1500);
  await p.evaluate(() => [...document.querySelectorAll('button')].find((e) => e.textContent.trim() === 'Calendars')?.click());
  await wait(900);
  return p;
}
const has = (p, sel) => p.evaluate((s) => { const e = document.querySelector(s); return !!e && e.getClientRects().length > 0; }, sel);
const click = async (p, sel) => {
  const el = await p.$(sel);
  if (!el) { fails.push(`missing ${sel}`); return false; }
  await el.evaluate((e) => e.click()); await wait(500); return true;
};
const pressText = (p, text) => p.evaluate((x) => { const b = [...document.querySelectorAll('[role="menuitem"], button')].find((e) => e.textContent.trim().startsWith(x)); if (b) b.click(); return !!b; }, text);

async function shot(p, w, name) {
  log.steps += 1;
  await p.mouse.move(2, 2); await wait(150);
  const a = await p.evaluate(`(${ALIGN})()`);
  const side = await p.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
  if (a?.faults) { log.alignFaults += a.faults; fails.push(`alignment ${name} ${w}: ${JSON.stringify(a)}`); }
  if (side) { log.sideways += 1; fails.push(`sideways scroll ${name} ${w}`); }
  for (const th of ['dark', 'light']) { await applyTheme(p, th); await wait(200); await p.screenshot({ path: join(OUT, `${w}-${name}-${th}.png`) }); }
  await applyTheme(p, 'dark');
}

for (const [w, h] of [[1440, 900], [1090, 800]]) {
  // 1. the plan on a classic model: the first view, measured
  let p = await open('classic', w, h);
  expect(await has(p, t('cal-main')), `${w}: the plan card is the first view`);
  expect(await has(p, t('cal-preview')), `${w}: the page says Preview`);
  expect(await has(p, t('cal-apply')), `${w}: Set up calendars is on the page`);
  const m = await p.evaluate(`(${MEASURE.replace(/^\/\/.*$/mg, '').trim()})`).catch(() => null);
  const first = await p.evaluate(() => {
    const root = document.querySelector('.cal-page');
    const words = (root?.innerText || '').split(/\s+/).filter((x) => /[\p{L}\p{N}]/u.test(x)).length;
    const controls = root ? [...root.querySelectorAll('button, input, select, textarea, a[href]')].filter((c) => c.getClientRects().length && !c.disabled).length : 0;
    const sc = document.scrollingElement; const main = document.querySelector('main') || sc;
    return { words, controls, scrollRange: Math.max(0, Math.round(main.scrollHeight - main.clientHeight)) };
  });
  log.measures[`classic-${w}`] = first; void m;
  expect(await has(p, t('cal-ladder-year')) && await has(p, t('cal-ladder-fiscal')), `${w}: calendar year and fiscal (from the clue) are on the plan`);
  expect(!(await has(p, t('cal-ladder-retail'))), `${w}: retail is not on the plan until chosen`);
  expect((await p.$eval(t('cal-clue'), (e) => e.textContent)).includes('July'), `${w}: the fiscal clue names July`);
  await shot(p, w, '01-plan');
  // 2. choose a retail calendar: one press
  await click(p, t('cal-kind-retail'));
  expect(await has(p, t('cal-ladder-retail')), `${w}: one press adds the retail ladder`);
  await shot(p, w, '02-retail-added');
  await click(p, t('cal-kind-retail'));
  // 3. change one level: press it, then pick a column
  await click(p, t('cal-rung-Calendar year-Quarter'));
  expect(await has(p, '[role="menu"]'), `${w}: pressing a level opens its menu`);
  await shot(p, w, '03-change-a-level');
  await p.keyboard.press('Escape'); await wait(300);
  // 4. fiscal start month
  await click(p, t('cal-fiscal-month'));
  expect(await pressText(p, 'April'), `${w}: the month menu lists April`); await wait(500);
  expect((await p.$eval(t('cal-clue'), (e) => e.textContent)).includes('April'), `${w}: the fiscal start follows the pick`);
  await click(p, t('cal-fiscal-month')); await pressText(p, 'July'); await wait(500);
  // 5. every change
  await click(p, t('cal-more'));
  expect(await has(p, t('cal-changes')), `${w}: See every change opens the list`);
  expect((await p.$eval(t('cal-changes'), (e) => e.textContent)).includes('TOTALYTD ( [Total Sales], \'Calendar year\' )'), `${w}: the list shows the DAX after`);
  await shot(p, w, '04-every-change');
  await click(p, t('cal-more'));
  await checkScreen(p, w === 1440 ? 'calendars-plan' : 'calendars-plan-1090', { root: '.cal-page' });
  // 6. set up: one press, then done, with the number check said plainly
  await click(p, t('cal-apply')); await wait(800);
  expect(await has(p, t('cal-done')), `${w}: after Set up the done line shows`);
  const done = await p.$eval(t('cal-done'), (e) => e.textContent);
  expect(/calendars set up/.test(done) && /measures moved/.test(done), `${w}: the done line says what moved: ${done}`);
  expect(/not compared/.test(done), `${w}: without a live model the number check says it did not compare`);
  expect(await has(p, t('cal-addm')), `${w}: the time measures are offered`);
  await shot(p, w, '05-done');
  await checkScreen(p, w === 1440 ? 'calendars-done' : 'calendars-done-1090', { root: '.cal-page' });
  // 7. add time measures, then undo them, then undo the setup
  await click(p, t('cal-addm-go'));
  expect(await has(p, t('cal-added')), `${w}: the added line shows`);
  await shot(p, w, '06-measures-added');
  await click(p, t('cal-added') + ' .cal-link');
  expect(await has(p, t('cal-addm-go')), `${w}: undo of the measures brings the list back`);
  await click(p, t('cal-undo'));
  expect(await has(p, t('cal-apply')), `${w}: undo of the setup is the plan again`);
  await shot(p, w, '07-undone');
  // 8. add a calendar after setup
  await click(p, t('cal-apply')); await wait(600);
  await click(p, t('cal-add-retail')); await wait(600);
  expect(await has(p, t('cal-ladder-retail')), `${w}: a calendar added after setup shows as a ladder`);
  await shot(p, w, '08-retail-after');
  await p.close();
  // 9. a model with no Date table
  p = await open('none', w, h);
  expect(await has(p, t('cal-apply')), `${w}: no Date table still opens on a plan`);
  expect((await p.$eval('.cal-hero', (e) => e.textContent)).includes('Sales[Order Date]'), `${w}: the plan names the source column`);
  await shot(p, w, '09-no-date-table');
  await click(p, t('cal-apply')); await wait(700);
  expect((await p.$eval(t('cal-done'), (e) => e.textContent)).length > 0 && (await p.$eval('.cal-hero', (e) => e.textContent)).includes('Date table and calendars are ready'), `${w}: the new Date table is said to be ready`);
  await shot(p, w, '10-no-date-table-done');
  await p.close();
  // 10. a live model, and a model with no date column
  p = await open('live', w, h);
  expect(await has(p, t('cal-live')) && !(await has(p, t('cal-apply'))), `${w}: a live model gets a card and no Set up press`);
  await shot(p, w, '11-live');
  await p.close();
  p = await open('unavailable', w, h);
  expect(await has(p, t('cal-stop')), `${w}: no date column gets a card that says why`);
  await shot(p, w, '12-no-date-column');
  await p.close();
}
expect(log.errors.length === 0, 'console errors: ' + log.errors.slice(0, 5).join('; '));
for (const f of screenFailures()) fails.push(f);
writeFileSync(join(OUT, 'drive.json'), JSON.stringify({ ...log, fails }, null, 1));
console.log(`${fails.length ? 'FAIL' : 'PASS'} calendars: ${log.steps} shots, ${log.errors.length} console errors, ${log.sideways} sideways, ${log.alignFaults} alignment faults${fails.length ? '\n  - ' + fails.join('\n  - ') : ''}`);
console.log(JSON.stringify(log.measures));
await browser.close(); server.close();
process.exit(fails.length ? 1 : 0);
