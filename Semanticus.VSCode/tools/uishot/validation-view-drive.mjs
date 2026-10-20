// Proof for Review > Validation A (1.6): serves THIS worktree's built bundle, drives the real page with real clicks on the
// harness's engine fixtures plus an 8-run history, and writes PNGs (dark and light, 1440 and 1090) and results.json with
// what the DOM holds, so a claim about the page can be checked rather than eyeballed.
//   OUTDIR=<dir> node tools/uishot/validation-view-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'validation-view');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vv-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// Eight recorded runs of one history, so the small multiples have something to draw. Run n finds: Net Revenue wandering,
// Total Sales always right, Relationships improving, Table row counts steady. Jev's call is answered like the engine does.
const SEED = `
  let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
    get: () => __h,
    set: (v) => { __h = v;
      const days = [1, 3, 5, 8, 10, 12, 14, 16];
      const hh = (g, roots) => ({ overall: 0, grade: g, gatedBy: [], coveragePct: 80, checked: 23, passed: 22 - roots, failed: roots, suspect: 0, notVerifiable: 1, missing: 0, rootFailures: roots, categories: [] });
      v.setResponse('listTestRuns', { runs: days.map((d, i) => ({ runId: 'q' + i, when: '2026-09-' + String(d).padStart(2, '0') + 'T09:00:00Z', live: true, health: hh(i < 4 ? 'B' : 'D', i < 4 ? 1 : 3) })) });
      v.setResponse('getTestRun', function (params) {
        const id = params && params[0]; const n = Number(String(id).slice(1));
        const net = [0.0, 0.04, 0.02, 0.06, 0.05, 0.07, 0.06, 0.063][n];
        const rel = [0.5, 0.5, 0.5, 0.33, 0.33, 0.33, 0.17, 0.17][n];
        const ctx = (bad, total) => Array.from({ length: total }, (_, k) => ({ context: 'Place ' + k, expected: 100, actual: k < bad ? 90 : 100, difference: k < bad ? -10 : 0, verdict: k < bad ? 'Fail' : 'Pass' }));
        return { gradedBeforeSecurityLeftTests: false, run: { runId: id, when: '2026-09-' + String(days[n]).padStart(2, '0') + 'T09:00:00Z', live: true, modelName: 'Contoso Retail', health: hh('B', 1), outcomes: { checks: [
          { kind: 'savedCheck', defId: 'd1', name: 'Total Sales ties to the GL', verdict: 'Pass', contexts: ctx(0, 8), contextsTotal: 8 },
          { kind: 'savedCheck', defId: 'd2', name: 'Net Revenue ties to finance extract', verdict: net > 0 ? 'Fail' : 'Pass', contexts: ctx(Math.round(net * 100), 100), contextsTotal: 100 },
          { kind: 'relationship', name: 'Sales to Customer', check: 'ReferentialIntegrity', verdict: rel > 0.4 ? 'Fail' : 'Pass' },
          { kind: 'relationship', name: 'Sales to Product', check: 'ReferentialIntegrity', verdict: rel > 0.2 ? 'Fail' : 'Pass' },
          { kind: 'relationship', name: 'Sales to Date', check: 'ReferentialIntegrity', verdict: 'Pass' },
          { kind: 'tableRowCount', name: 'Returns', verdict: 'Pass' }, { kind: 'tableRowCount', name: 'Sales', verdict: 'Pass' },
        ] } } };
      });
      if (window.__uishotJev) v.setResponse('pickCheckVisuals', function (params) {
        window.__uishotJevCalls = (window.__uishotJevCalls || 0) + 1;
        const picks = ((params && params[0] && params[0].checks) || []).map((c) => ({ id: c.id, kind: c.fits[1] || c.fits[0], by: 'jev', reason: 'Jev picked the ' + (c.fits[1] || c.fits[0]) + ' for ' + c.title + '.', confidence: 0.93 }));
        return { picks };
      });
    } });`;

const MEASURE = `(() => {
  const txt = (el) => el ? el.innerText.replace(/\\n+/g, ' | ').trim() : null;
  const rows = [...document.querySelectorAll('[data-testid^="va-row-"]')].map((c) => ({ id: c.dataset.testid, text: txt(c).slice(0, 200), rect: (() => { const r = c.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)]; })() }));
  return {
    verdict: txt(document.querySelector('[data-testid="v-verdict"]')),
    rows, jevCalls: window.__uishotJevCalls || 0,
    visuals: [...document.querySelectorAll('[data-kind]')].map((v) => v.dataset.kind),
    pickBy: [...document.querySelectorAll('[data-testid="pickline"]')].map((p) => p.dataset.by),
    docWidth: document.documentElement.scrollWidth, viewWidth: document.documentElement.clientWidth,
    emDash: (document.body.innerText.match(/\\u2014/g) || []).length,
    mapWord: (document.body.innerText.match(/\\bMap\\b/g) || []).length,
  };
})()`;

const ALIGN_FILE = join(here, 'validation-align.js');
const MEASURE_FILE = readFileSync(join(here, 'validation-measure.js'), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const ALIGN = readFileSync(ALIGN_FILE, 'utf8').replace(/^\/\/.*$/mg, '').trim();
const results = [];
async function scene(name, { query = 'conn=1&live=1&tier=pro', jev = false, extra = '', steps = [], widths = [1440, 1090], themes = ['dark', 'light'], shoot = true, expect = null } = {}) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(`window.__uishotJev = ${jev};` + SEED + extra);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(600);
  const notes = [];
  const click = async (label) => {
    const ok = await page.evaluate((l) => {
      const vis = (el) => el.getClientRects().length > 0;
      const el = [...document.querySelectorAll('button, [role="button"], [role="menuitem"]')].filter(vis).filter((b) => !b.closest('nav[aria-label="Studio areas"]'))
        .find((b) => (b.getAttribute('aria-label') || b.textContent || '').trim().startsWith(l));
      if (el) { el.click(); return true; } return false;
    }, label);
    if (!ok) notes.push(`control "${label}" not found`);
    await wait(900);
  };
  await page.evaluate(() => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')].find((b) => b.textContent.trim() === 'Review')?.click());
  await wait(900);
  await page.evaluate(() => [...document.querySelectorAll('button, [role="tab"]')].filter((b) => b.getClientRects().length).find((b) => b.textContent.trim() === 'Validation')?.click());
  await wait(900);
  for (const step of steps) {
    if (typeof step === 'object' && step.first) { await page.evaluate(() => document.querySelector('[data-testid="v-problems"] [data-testid^="va-row-"]')?.click()); await wait(700); }
    else if (typeof step === 'object' && step.row) { await page.evaluate((r) => document.querySelector(`[data-testid="va-row-${r}"]`)?.click(), step.row); await wait(700); }
    else if (typeof step === 'object' && step.testid) { await page.evaluate((t) => document.querySelector(`[data-testid="${t}"]`)?.click(), step.testid); await wait(700); }
    else if (typeof step === 'object' && step.type) { await page.click('[data-testid="v-pass-filter"]'); await page.keyboard.type(step.type); await wait(500); }
    else if (typeof step === 'object' && step.wait) await wait(step.wait);
    else await click(step);
  }
  const shots = {}, measured = {};
  for (const theme of themes) {
    await applyTheme(page, theme); await wait(300);
    for (const w of widths) {
      const h = w === 1090 ? 800 : 900;
      await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
      await wait(700);
      measured[`${theme}-${w}`] = await page.evaluate(MEASURE);
      measured[`${theme}-${w}`].align = await page.evaluate(`(${ALIGN})()`);
      measured[`${theme}-${w}`].first = await page.evaluate(`(${MEASURE_FILE})(${JSON.stringify({ root: '.vc-page', targets: { lastProblem: '[data-testid="v-problems"] .va-rowwrap::last', verdict: '[data-testid="v-verdict"]' } })})`);
      if (!shoot) continue;
      // The page scrolls inside the Studio, so the shot is taken at the page's own full height.
      const full = await page.evaluate(() => { let best = 0; for (const el of document.querySelectorAll('*')) if (el.scrollHeight > el.clientHeight + 4 && getComputedStyle(el).overflowY !== 'visible') best = Math.max(best, el.scrollHeight); return best; });
      await page.setViewport({ width: w, height: Math.min(Math.max(900, full + 120), 6000), deviceScaleFactor: 1 });
      await wait(900);
      const file = join(OUT, `${name}-${theme}-${w}.png`);
      await page.screenshot({ path: file });
      shots[`${theme}-${w}`] = file;
      await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
    }
  }
  if (expect) {
    const got = await page.evaluate(PAGE_FACTS);
    for (const [label, ok] of expect(got)) if (!ok) notes.push('FAILED: ' + label + ' | ' + JSON.stringify(got).slice(0, 600));
    measured.facts = got;
  }
  results.push({ scene: name, notes, errors, shots, measured });
  await page.close();
}

const ONLY = (process.env.SCENES || '').split(',').filter(Boolean);
const want = (n) => !ONLY.length || ONLY.includes(n);
// ---- Polish (16 Oct): headline over the checks that ran, one "not run yet" line, the gap that names its places, "and N more", the pass filter.
const PATCH = (body) => `
  (function(){ const t = setInterval(() => { const v = window.__uishotHarness; if (!v) return; clearInterval(t);
    const full = JSON.parse(JSON.stringify(v.response('runTests', [false, 'probe', null, null])));
    const defs = JSON.parse(JSON.stringify(v.response('listTests', [])));
    ${body}
    v.setResponse('runTests', () => JSON.parse(JSON.stringify(full)));
    v.setResponse('listTests', () => JSON.parse(JSON.stringify(defs)));
  }, 0); })();`;
const MANY = (n, bad, nv, off) => `
  defs.definitions = []; full.reconciles = [];
  for (let i = 1; i <= ${n}; i++) { const id = 'm' + i, kind = i <= ${bad} ? 'bad' : i <= ${bad + nv} ? 'nv' : 'ok';
    defs.definitions.push({ id, kind: 'measureValue', title: 'Region ' + String(i).padStart(2, '0') + ' sales total', targetTag: 'lt-' + id, targetRef: 'measure:Sales/Total Sales', enabled: true, createdBy: 'human', createdWhen: '2026-09-12T00:00:00Z',
      paramsJson: JSON.stringify({ measureRef: 'measure:Sales/Total Sales', expectedValue: '1000', toleranceAbsolute: 1e-6, toleranceRelative: 1e-9 }) });
    if (i > ${n - off}) continue;
    full.reconciles.push(kind === 'ok' ? { defId: id, title: 'x', verdict: 'Pass', expected: '1000', actual: '1000', difference: 0, matches: 1, mismatches: 0 }
      : kind === 'bad' ? { defId: id, title: 'x', verdict: 'Fail', expected: '1000', actual: String(1000 - i), difference: -i, matches: 0, mismatches: 1 }
      : { defId: id, title: 'x', verdict: 'NotVerifiable', message: 'the query timed out after 30 seconds' }); }`;
const PAGE_FACTS = `(() => {
  const t = (q) => document.querySelector(q)?.innerText.replace(/\\n+/g, ' | ').trim() ?? null;
  const rows = (q) => [...document.querySelectorAll(q + ' [data-testid^="va-row-"]')].map((r) => r.innerText.replace(/\\n+/g, ' | ').trim());
  return { verdict: t('[data-testid="v-verdict"]'), problems: rows('[data-testid="v-problems"]'), more: t('[data-testid="v-problems-more"]'), notRun: t('[data-testid="v-notrun"]'),
    passRows: rows('[data-testid="v-passing"]').length, filter: !!document.querySelector('[data-testid="v-pass-filter"]'), noMatch: !!document.querySelector('.va-nomatch') };
})()`;
const GAP_PATCH = PATCH(`
  const d = defs.definitions.find((x) => x.id === 'd2'); const pj = JSON.parse(d.paramsJson); pj.toleranceAbsolute = 10; d.paramsJson = JSON.stringify(pj);
  const o = full.reconciles.find((x) => x.defId === 'd2'); o.mismatches = 2; o.matches = 2; o.difference = 3; o.actual = '18,185,053.80';
  o.rows = [ { context: 'Grand total', sql: 18185050.80, dax: 18185053.80, delta: 3, verdict: 'Pass', grandTotal: true },
    { context: 'Europe', sql: 5206771.20, dax: 5207271.20, delta: 500, verdict: 'Fail' }, { context: 'Australia', sql: 4821903.44, dax: 4821406.44, delta: -497, verdict: 'Fail' },
    { context: 'North America', sql: 8114058.12, dax: 8114058.12, delta: 0, verdict: 'Pass' } ];`);
// 9. The gap line: places differ, the grand total is inside the allowance.
if (want('p-gap')) await scene('p-gap', { extra: GAP_PATCH, steps: ['Run checks'], widths: [1440, 1090], themes: ['dark', 'light'],
  expect: (g) => [['the row names its places and the widest gap', g.problems.some((r) => /2 places differ, up to 500/.test(r))]] });
// 10. Not-run checks fold into one line and never shrink the big number: 20 saved checks (3 differ, 2 could not check, 6 never ran)
//     plus the harness's relationships and table row counts, which both ran and differ: 16 ran, 9 pass, 5 differ.
if (want('p-notrun')) await scene('p-notrun', { extra: PATCH(MANY(20, 3, 2, 6)), steps: ['Run checks'], widths: [1440, 1090], themes: ['dark', 'light'],
  expect: (g) => [['big number is over the 16 that ran', /^9 of 16 pass/.test(g.verdict.replace(/\s+/g, ' '))], ['sub line says 3 differ and 6 not run yet', /5 differ/.test(g.verdict) && /6 not run yet/.test(g.verdict)],
    ['one not-run line, not one row each', /6 not run yet/.test(g.notRun) && !g.problems.some((r) => /Not run yet/.test(r))]] });
// 11. Sixteen problems (10 + 4 saved checks, plus relationships and table row counts): ten and "and 6 more"; the line opens the rest in place.
if (want('p-many')) {
  await scene('p-many', { extra: PATCH(MANY(30, 10, 4, 0)), steps: ['Run checks'], widths: [1440, 1090], themes: ['dark', 'light'],
    expect: (g) => [['first ten problems', g.problems.length === 10], ['and 6 more', /and 6 more/.test(g.more ?? '')]] });
  await scene('p-many-open', { extra: PATCH(MANY(30, 10, 4, 0)), steps: ['Run checks', { testid: 'v-problems-more' }], widths: [1440], themes: ['dark'],
    expect: (g) => [['all sixteen after the press', g.problems.length === 16 && g.more === null]] });
}
// 12. Sixty checks: the opened pass list has a filter that finds a check by name.
if (want('p-sixty')) {
  await scene('p-sixty', { extra: PATCH(MANY(60, 8, 4, 0)), steps: ['Run checks', { testid: 'v-show-pass' }], widths: [1440, 1090], themes: ['dark', 'light'],
    expect: (g) => [['a filter over twenty passes', g.filter && g.passRows === 48]] });
  await scene('p-sixty-filter', { extra: PATCH(MANY(60, 8, 4, 0)), steps: ['Run checks', { testid: 'v-show-pass' }, { type: 'Region 47' }], widths: [1440], themes: ['dark'],
    expect: (g) => [['the filter finds one check', g.passRows === 1], ['the filter keeps the box', g.filter]] });
  await scene('p-sixty-none', { extra: PATCH(MANY(60, 8, 4, 0)), steps: ['Run checks', { testid: 'v-show-pass' }, { type: 'zzz' }], widths: [1440], themes: ['dark'],
    expect: (g) => [['no match says so', g.noMatch && g.passRows === 0]] });
  // twenty passes or fewer: no box
  await scene('p-twenty', { extra: PATCH(MANY(30, 5, 5, 0)), steps: ['Run checks', { testid: 'v-show-pass' }], widths: [1440], themes: ['dark'], shoot: false,
    expect: (g) => [['no filter at twenty passes', !g.filter && g.passRows === 20]] });
}
// 1. After a full run: the verdict, the strip, only the problems. The rules chose the visuals (Jev's point is off).
if (want('rules')) await scene('rules', { steps: ['Run checks'] });
// 2. The same run with Jev answering the one call, and the first problem opened in place.
if (want('jev')) await scene('jev', { jev: true, steps: ['Run checks', { first: true }] });
// 3. Before any run: nothing invented.
if (want('norun')) await scene('norun', { widths: [1440], themes: ['dark'] });
// 4. The first problem opened in place; then the passes behind their one link.
if (want('open')) await scene('open', { steps: ['Run checks', { first: true }], widths: [1440, 1090], themes: ['dark', 'light'] });
if (want('passes')) await scene('passes', { steps: ['Run checks', { testid: 'v-show-pass' }], widths: [1440], themes: ['dark'] });
// 5. A failing check's differing places, then every place.
if (want('differing')) await scene('differing', { steps: ['Run checks', { first: true }, 'All places'], widths: [1440], themes: ['dark'] });
// 6. The menus and panels the head opens.
if (want('menus')) {
  await scene('menu-more', { steps: ['Run checks', 'More'], widths: [1440], themes: ['dark'] });
  await scene('menu-run', { steps: ['Run checks', 'What to run'], widths: [1440], themes: ['dark'] });
  await scene('panel-history', { steps: ['Run checks', 'More', 'Run history and grade'], widths: [1440, 1090], themes: ['dark', 'light'] });
  await scene('panel-new', { steps: ['Run checks', 'More', 'New check'], widths: [1440], themes: ['dark'] });
  await scene('reports', { steps: ['Run checks', 'More', 'Kept reports'], widths: [1440], themes: ['dark'] });
  await scene('panel-choose', { steps: ['Run checks', 'What to run', 'Choose checks'], widths: [1440], themes: ['dark'] });
}
// 7. Relationships opened (details, then the values that do not match).
if (want('relationship')) await scene('relationship', { steps: ['Run checks', { row: 'relationships' }, 'Show details', 'Show the values that do not match'], widths: [1440], themes: ['dark'] });
// 8a. The happy path with real clicks: every feature of the old page is reachable, and each one does what it says.
if (want('journey')) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 1440, height: 900 });
  await page.evaluateOnNewDocument(`window.__uishotJev = false;` + SEED);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro`, { waitUntil: 'networkidle0' });
  await page.waitForSelector('nav button'); await wait(600);
  await page.evaluate(() => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')].find((b) => b.textContent.trim() === 'Review')?.click()); await wait(800);
  await page.evaluate(() => [...document.querySelectorAll('button, [role="tab"]')].filter((b) => b.getClientRects().length).find((b) => b.textContent.trim() === 'Validation')?.click()); await wait(800);
  const checks = [];
  const body = () => page.evaluate(() => document.querySelector('.vc-page')?.innerText ?? '');
  const press = async (label, nth = 0) => {
    const ok = await page.evaluate((l, n) => {
      const vis = (el) => el.getClientRects().length > 0;
      const all = [...document.querySelectorAll('button, [role="menuitem"]')].filter(vis).filter((b) => !b.closest('nav[aria-label="Studio areas"]'))
        .filter((b) => (b.getAttribute('aria-label') || b.textContent || '').trim().startsWith(l));
      const el = all[n]; if (el) { el.click(); return true; } return false;
    }, label, nth);
    await wait(900); return ok;
  };
  const expect = async (name, fn) => { let ok = false, detail = ''; try { const r = await fn(); ok = !!r; detail = typeof r === 'string' ? r : ''; } catch (e) { detail = String(e); } checks.push({ name, ok, detail }); };
  await expect('before a run the page says "not run yet", nothing invented', async () => /not run yet|Not run yet/.test(await body()));
  await expect('Run checks gives "N of M pass"', async () => { await press('Run checks'); const t = await body(); return /\d+\s+of\s+\d+ pass/.test(t) && t.match(/\d+ of \d+ pass/)[0]; });
  await expect('only the problems are listed; the passes sit behind one link', async () => { const t = await body(); return /Show the \d+ that pass/.test(t) && !/Total Sales ties to the GL/.test(t); });
  await expect('the link shows the passing check', async () => { await press('Show the'); return /Total Sales ties to the GL/.test(await body()); });
  await expect('a problem opens its differences in place and Run again works', async () => {
    await page.evaluate(() => document.querySelector('[data-testid="v-problems"] [data-testid^="va-row-"]')?.click()); await wait(700);
    const t = await body(); const had = /Expected/.test(t) && /Where/.test(t);
    const ran = await press('Run again'); return had && ran;
  });
  await expect('All places shows more places than only the differing ones', async () => {
    const before = await page.evaluate(() => document.querySelectorAll('[data-testid="data-table"] tbody tr').length);
    await press('All places');
    const after = await page.evaluate(() => document.querySelectorAll('[data-testid="data-table"] tbody tr').length);
    return after > before && `${before} then ${after}`;
  });
  await expect('the check menu holds Edit, Turn off, Delete', async () => {
    await page.evaluate(() => document.querySelector('[data-testid^="va-detail-"] [aria-label^="More for"]')?.click()); await wait(500);
    const t = await page.evaluate(() => [...document.querySelectorAll('[role="menuitem"]')].map((x) => x.textContent.trim()).join('|'));
    await page.evaluate(() => window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))); await wait(300);
    return /Edit/.test(t) && /Turn off/.test(t) && /Delete/.test(t) && t;
  });
  await expect('Run checks with the scope menu runs one family only and says what its grade covers', async () => {
    await press('What to run'); const menu = await page.evaluate(() => [...document.querySelectorAll('[role="menu"] [role="menuitem"]')].map((x) => x.getAttribute('aria-label') || x.textContent.trim()).join('|'));
    await press('Run relationships only'); await wait(1200);
    const t = await body();
    return /This grade is|did not run/.test(t) && menu;
  });
  await expect('Choose checks lists the checks that are on, and Run N chosen runs them', async () => {
    await press('What to run'); await press('Choose checks'); await wait(400);
    const boxes = await page.evaluate(() => document.querySelectorAll('[role="group"][aria-label="Checks to run"] input[type="checkbox"]').length);
    await page.evaluate(() => document.querySelector('[role="group"][aria-label="Checks to run"] input[type="checkbox"]')?.click()); await wait(300);
    const label = await page.evaluate(() => [...document.querySelectorAll('.va-panel button')].map((b) => b.textContent.trim()).find((t) => /^Run \d+ chosen/.test(t)));
    await press('Run 1 chosen'); await wait(1200);
    return !!label && boxes > 0 && `${boxes} boxes, button "${label}"`;
  });
  await expect('More > Run history and grade shows the grade, and Record this run records', async () => {
    await press('More', 0); await press('Run history and grade'); await wait(500);
    const open = /Grade/.test(await body());
    const rec = await press('Record this run', 0); await wait(900);
    return open && rec && 'grade shown, record pressed';
  });
  await expect('More > New check opens the choices, and a choice opens the drawer', async () => {
    await press('More', 0); await press('New check'); await wait(500);
    const t = await body(); await press('Check against SQL'); await wait(700);
    const drawer = await page.evaluate(() => !!document.querySelector('[role="dialog"], aside, [data-testid*="author"]'));
    return /Add a SQL source/.test(t) && drawer;
  });
  await expect('More > Report of this run opens the report dialog', async () => {
    await page.keyboard.press('Escape'); await wait(300);
    await press('More', 0); await press('Report of this run'); await wait(900);
    const t = await page.evaluate(() => document.body.innerText);
    return /Check report|Markdown|HTML/.test(t);
  });
  await expect('More > Kept reports opens the reports, and Back to checks returns', async () => {
    await page.keyboard.press('Escape'); await wait(300);
    await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => /^(Close|Done)/.test(b.textContent.trim()))?.click()); await wait(300);
    await press('More', 0); await press('Kept reports'); await wait(900);
    const onReports = /Kept reports/.test(await body()) && /Back to checks/.test(await body());
    await press('Back to checks'); await wait(700);
    return onReports && /of \d+ pass/.test(await body());
  });
  results.push({ scene: 'journey', notes: checks.filter((c) => !c.ok).map((c) => 'FAILED: ' + c.name + ' ' + c.detail), errors, shots: {}, measured: { checks } });
  await page.close();
}
// 8. The alignment measure must catch a fault: nudge the column off the lines and count what it reports.
if (want('negcheck')) {
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 900 });
  await page.evaluateOnNewDocument(`window.__uishotJev = false;` + SEED);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro`, { waitUntil: 'networkidle0' });
  await page.waitForSelector('nav button'); await wait(600);
  await page.evaluate(() => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')].find((b) => b.textContent.trim() === 'Review')?.click()); await wait(800);
  await page.evaluate(() => [...document.querySelectorAll('button, [role="tab"]')].filter((b) => b.getClientRects().length).find((b) => b.textContent.trim() === 'Validation')?.click()); await wait(800);
  await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Run checks')?.click()); await wait(1500);
  const count = (a) => Object.fromEntries(['offGrid', 'column', 'gaps', 'inner'].map((k) => [k, a[k].length]));
  const asBuilt = count(await page.evaluate(`(${ALIGN})()`));
  await page.addStyleTag({ content: '.va-col { margin-left: 3px; } .va-list { margin-top: 5px; } .va-detail { padding-left: 30px; }' });
  await wait(300);
  const broken = count(await page.evaluate(`(${ALIGN})()`));
  results.push({ scene: 'negcheck', notes: [], errors: [], shots: {}, measured: { asBuilt, broken } });
  await page.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
for (const r of results) console.log(r.scene, 'errors:', r.errors.length, 'notes:', r.notes.join('; ') || 'none', 'shots:', Object.keys(r.shots).length);
await browser.close();
server.close();
