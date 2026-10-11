// Acceptance driver for the Queries redesign (27 September 2026: option C, since 8 October labelled wells, with diagnostics D1, and the
// Pro question box, contract H1 to H9). The REAL Studio through the mock engine, driven by clicks and keys, and shot
// in Dark Modern, Light Modern and Dark High Contrast at 2000x1200, 1440x900 and 900x900.
//
// WHAT IT PROVES, per scene, from the harness's own call trace and the DOM:
//   - one result, shown once, that fills the page (the result region is at least 40% of the page height);
//   - two views only, Result and Speed; Server steps fold inside Speed; Calculation steps and Compare open as sheets;
//   - Speed opens with its answer; a number's right-click offers Explain this number and Calculation steps;
//   - the chart rules label their pick with a reason, and one click changes it and one click puts it back;
//   - Pro: a question fills the DAX (the lab's own writing), the rows arrive with NO runDax call (H2), the run key in
//     the field asks and does not run (H7), a hand edit steps the chips aside, Put back my query restores (H5), and
//     the only check offered while the chips own the query is a number from the answer (H6);
//   - Free: one quiet Pro line, no field, and ZERO askData and listDataQuestions calls.
// WHAT IT DOES NOT: prove the engine, Jev, or a live model. That is the UAT lane's.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/queries-drive.mjs
//   QUICK=1 shoots 1440x900 only.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'queries');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `queries-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = ['dark', 'light', 'hc'];
const SIZES = process.env.QUICK ? [[1440, 900]] : [[2000, 1200], [1440, 900], [900, 900]];
const results = [];
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };

async function open(q, size = [1440, 900]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=daxlab&conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const methods = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).map((m) => m.method).filter(Boolean));
const count = async (page, m) => (await methods(page)).filter((x) => x === m).length;
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const clickText = (page, label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => !x.disabled && (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, scope);
const has = (page, sel) => page.evaluate((s) => !!document.querySelector(s), sel);

// The space rule: the result region takes at least 40% of the page, and the page does not scroll sideways.
async function space(page, slug) {
  const m = await page.evaluate(() => {
    const r = document.querySelector('[data-primary="result"]')?.getBoundingClientRect();
    return { resultH: r ? r.height : 0, vh: window.innerHeight, hScroll: document.documentElement.scrollWidth > window.innerWidth + 1, resultW: r ? r.width : 0, vw: window.innerWidth };
  });
  expect(m.resultH >= m.vh * 0.4, `${slug}: result is ${Math.round(m.resultH)} px of ${m.vh} (under 40%)`);
  expect(!m.hScroll, `${slug}: the page scrolls sideways`);
  return m;
}

async function shoot(page, slug, desc, { sizes = SIZES, check = true } = {}) {
  for (const [w, h] of sizes) {
    await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
    await wait(400);
    const m = check ? await space(page, `${slug} ${w}x${h}`) : null;
    for (const t of THEMES) {
      await applyTheme(page, t);
      await wait(500);
      const file = join(OUT, `${slug}-${w}x${h}-${t}.png`);
      await page.screenshot({ path: file });
      results.push({ slug, size: `${w}x${h}`, theme: t, file, desc, space: m });
    }
    await applyTheme(page, null);
  }
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await wait(200);
}

try {
  // 1. Pro, the sentence built from the model, run: the result fills the page, the rules pick a chart and say why.
  {
    const { page, errors } = await open('tier=pro');
    expect(await has(page, '[data-testid="queries-ask"]'), 'pro: the question field is there');
    expect(await has(page, '[data-testid="queries-wells"]'), 'pro: the wells row is there');
    const tabs = await page.evaluate(() => [...document.querySelectorAll('[aria-label="Result views"] button')].map((b) => b.textContent.replace(/\s+/g, ' ').trim()));
    expect(tabs.length === 2 && tabs[0].startsWith('Result') && tabs[1].startsWith('Speed'), 'two views only: ' + tabs.join(' | '));
    await clickText(page, 'Run the query'); await wait(1200);
    expect(await has(page, '[data-testid="queries-chart-reason"]'), 'the chart rules say why');
    expect(!(await page.evaluate(() => document.body.textContent.includes('Result of:'))), 'the query is not shown a second time');
    await shoot(page, '01-result', 'Pro, Build view: the sentence, run; the result fills the page and the rules label their pick');
    // One click changes the chart, one click goes back to the rules' pick.
    await clickText(page, 'Table', '[aria-label="Chart kind"]'); await wait(400);
    expect((await text(page, '[data-testid="queries-chart-reason"]')).includes('Your pick'), 'a changed chart is labelled as yours');
    await shoot(page, '02-your-pick', 'The person picked Table; the rules pick is named, with Back to the rules pick', { sizes: [[1440, 900]] });
    await clickText(page, "Back to the rules' pick"); await wait(300);
    expect((await text(page, '[data-testid="queries-chart-reason"]')).includes('chart rules'), 'back to the rules pick');
    await clickText(page, 'More', '[aria-label="Chart kind"]'); await wait(200);
    await shoot(page, '03-more-kinds', 'More: the charts that do not fit, each with its reason', { sizes: [[1440, 900]], check: false });
    await page.keyboard.press('Escape'); await wait(150);
    expect(errors.length === 0, 'result: page errors ' + errors.join('; '));
    await page.close();
  }
  // 2. Speed: Measure speed, the answer first, every run and server steps as folds.
  {
    const { page, errors } = await open('tier=pro');
    await clickText(page, 'Run the query'); await wait(900);
    await clickText(page, 'Speed', '[aria-label="Result views"]'); await wait(300);
    await shoot(page, '04-speed-empty', 'Speed before measuring: one line that says what Measure speed does', { sizes: [[1440, 900]] });
    await clickText(page, 'Measure speed'); await wait(1800);
    const calls = await methods(page);
    expect(calls.includes('benchmarkColdWarm') && calls.includes('profileDax'), 'Measure speed runs first and repeat runs, then the profile');
    expect(await has(page, '[data-testid="queries-speed-answer"]'), 'Speed opens with its answer');
    await shoot(page, '05-speed', 'Speed after Measure speed: the answer, where the time went, the runs table, and the folds');
    await page.evaluate(() => document.querySelector('[data-testid="queries-server-steps"] summary')?.click()); await wait(200);
    await clickText(page, 'Record the steps'); await wait(1200);
    await shoot(page, '06-server-steps', 'Server steps, the deepest fold inside Speed', { sizes: [[1440, 900]] });
    expect(errors.length === 0, 'speed: page errors ' + errors.join('; '));
    await page.close();
  }
  // 3. A number's right-click: Explain this number and Calculation steps. Then the Calculation steps sheet.
  {
    const { page, errors } = await open('tier=pro&state=' + encodeURIComponent(JSON.stringify({})));
    await clickText(page, 'Run the query'); await wait(1000);
    await clickText(page, 'Matrix', '[aria-label="Chart kind"]'); await wait(400);
    await shoot(page, '07a-matrix', 'The Matrix kind: the rows share the result card, as the picked mockup draws them');
    const td =await page.evaluate(() => { const c = [...document.querySelectorAll('[data-testid="queries-result"] td')].find((x) => /\d/.test(x.textContent) && x.className.includes('tnum')); if (!c) return null; const r = c.getBoundingClientRect(); return { x: r.x + 10, y: r.y + 6 }; });
    if (expect(!!td, 'a matrix number to right-click')) {
      await page.mouse.click(td.x, td.y, { button: 'right' }); await wait(300);
      expect(await page.evaluate(() => [...document.querySelectorAll('[role="menu"] button')].map((b) => b.textContent).join('|').includes('Calculation steps')), 'the number menu offers Calculation steps next to Explain');
      await shoot(page, '07-number-menu', 'Right-click a number: Explain this number, and Calculation steps beside it', { sizes: [[1440, 900]], check: false });
      await clickText(page, 'Calculation steps', '[role="menu"]'); await wait(1400);
      expect(await has(page, '[data-testid="queries-steps"]'), 'the Calculation steps sheet opened');
      await shoot(page, '08-steps-sheet', 'Calculation steps as a side sheet; the result stays in view', { check: false });
      await page.keyboard.press('Escape'); await wait(200);
      expect(!(await has(page, '[data-testid="queries-sheet"]')), 'Escape closes the sheet');
    }
    // The more menu reaches Calculation steps and Compare too.
    await clickText(page, 'More tools for this result'); await wait(200);
    await shoot(page, '09-more-menu', 'The more menu: Calculation steps, Compare a rewrite, Show the DAX', { sizes: [[1440, 900]], check: false });
    await page.keyboard.press('Escape'); await wait(150);
    expect(errors.length === 0, 'steps: page errors ' + errors.join('; '));
    await page.close();
  }
  // 4. Compare a rewrite from the measure chip's swap icon, as a side sheet.
  {
    const { page, errors } = await open('tier=pro');
    await clickText(page, 'Run the query'); await wait(900);
    const ok = await page.evaluate(() => { const b = document.querySelector('[data-well-chip="values"] button[aria-label^="Compare a rewrite"]'); b?.click(); return !!b; });
    expect(ok, 'the measure chip has the compare icon');
    await wait(500);
    expect(await has(page, '[data-testid="queries-compare"]'), 'Compare opened as a sheet');
    await shoot(page, '10-compare', 'Compare a rewrite, opened from the measure chip, as a side sheet', { check: false });
    await page.keyboard.press('Escape'); await wait(200);
    // and from the Save measure check menu
    await clickText(page, 'Save measure check'); await wait(200);
    expect(await clickText(page, 'Compare a rewrite first'), 'Save measure check offers Compare a rewrite');
    await wait(300);
    expect(await has(page, '[data-testid="queries-compare"]'), 'Compare opened from the check menu');
    await page.keyboard.press('Escape'); await wait(200);
    expect(errors.length === 0, 'compare: page errors ' + errors.join('; '));
    await page.close();
  }
  // 5. The DAX view: the editor on top, the one result under it, and Compare a rewrite from a DAX-view button.
  {
    const { page, errors } = await open('tier=pro&lab=northstar');
    await clickText(page, 'DAX', '[role="group"]'); await wait(400);
    await clickText(page, 'Run the query'); await wait(1000);
    expect(await clickText(page, 'Compare a rewrite'), 'the DAX view has a Compare a rewrite button');
    await wait(300);
    expect(await has(page, '[data-testid="queries-compare"]'), 'Compare opened from the DAX view');
    await page.keyboard.press('Escape'); await wait(200);
    await clickText(page, 'Table', '[aria-label="Chart kind"]'); await wait(400);
    const heads = await page.evaluate(() => [...document.querySelectorAll('[data-testid="queries-result"] .xe-gcell')].slice(0, 3).map((c) => c.textContent.trim()));
    expect(heads[0] === 'Store region' && heads[1] === 'Month' && heads[2] === 'Net sales', 'friendly headers: ' + heads.join(' | '));
    expect(await page.evaluate(() => document.querySelector('[data-testid="queries-result"]')?.textContent.includes('Jan 2025')), 'formatted dates: Jan 2025');
    await clickText(page, "Back to the rules' pick"); await wait(300);
    await shoot(page, '11-dax-view', 'DAX view: the editor, then the one result, friendly headers and formatted dates');
    expect(errors.length === 0, 'dax: page errors ' + errors.join('; '));
    await page.close();
  }
  // 5b. A percent measure in a hand-typed query reads as its own format only when the format is proven to be the one the numbers
  // came from (Sol 6.1, 3.1, then delta B4). The engine sends no formats for typed DAX, so Queries looks each plain measure
  // column's format up by name. That is the model the numbers came from when the query connection IS the editing model (the
  // engine's relationship says sameInstance) or a linked copy whose own formats Queries reads from it (the default setup): both
  // read 0.0068 as 0.7%. With a separate test model nothing proves the format, so it shows the plain number 0.0068, never a
  // borrowed percent and never a rounded 0.01.
  for (const kind of ['same', 'linked', 'two']) {
    const sameInstance = kind === 'same';
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.evaluateOnNewDocument((same, two) => {
      const side = (name, live) => ({ available: true, modelName: name, source: 'localhost:51000', connectionId: 'cn3', kind: 'localDesktop', endpoint: 'localhost:51000', database: 'Contoso', live });
      window.__UISHOT__ = {
        measures: [{ ref: 'measure:Sales/Return Rate %', name: 'Return Rate %', table: 'Sales', displayFolder: '', formatString: '0.0%;-0.0%;0.0%', isHidden: false, hasDescription: false, description: '', expression: 'DIVIDE ( [Returns Amount], [Net Sales Amount] )' }],
        pivot: { columns: [{ name: '[Return Rate %]', type: 'Double' }], rows: [[0.0068119056469534]], rowCount: 1, truncated: false, elapsedMs: 9 },
        ...(same || two ? { connectionContext: {
          editing: side('Contoso Retail', true), querying: side('Contoso Retail', true), publishing: { available: false, live: false }, reference: { available: false, live: false },
          relationship: same ? 'sameInstance' : 'twoModels', twoModelsInPlay: !same, publishDestinationSeparateFromQuerying: false, summary: same ? 'Editing and querying Contoso Retail in the same live model.' : 'Two models are in play.',
        } } : {}),
      };
    }, sameInstance, kind === 'two');
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=daxlab&conn=1&live=1`, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2500);
    await clickText(page, 'DAX', '[role="group"]'); await wait(300);
    await page.evaluate(() => { const v = document.querySelector('.cm-content'); if (v) v.focus(); });
    await page.keyboard.down('Control'); await page.keyboard.press('a'); await page.keyboard.up('Control');
    await page.keyboard.type('EVALUATE ROW("Return Rate %", [Return Rate %])'); await wait(300);
    await page.keyboard.down('Control'); await page.keyboard.press('Enter'); await page.keyboard.up('Control'); await wait(1500);
    const shown = await page.evaluate(() => (document.querySelector('[data-testid="queries-page"]')?.textContent || '').replace(/\s+/g, ' '));
    if (kind !== 'two') expect(shown.includes('0.7%') && !/(^|[^\d.])0\.01([^\d]|$)/.test(shown), 'typed percent (' + kind + '): reads 0.7% for 0.0068: ' + shown.slice(0, 400));
    else expect(!shown.includes('0.7%') && /Return rate %\s*0\.006812/i.test(shown), 'typed percent: another model\'s number shows plain 0.006812, never a borrowed percent or 0.01: ' + shown.slice(0, 400));
    await shoot(page, { same: '11b-typed-percent', linked: '11c-typed-percent-linked-copy', two: '11d-typed-percent-other-model' }[kind], { same: 'A hand-typed query on a percent measure in the editing model itself: the result reads 0.7%, as the measure is formatted', linked: 'The default setup, tests on the editing model\'s linked copy: its own measure format is read from it, and the result reads 0.7%', two: 'The same query on a separate test model: the number shows plain, 0.006812, as no format is proven to belong to it' }[kind], { sizes: [[1440, 900]] });
    expect(errors.length === 0, 'typed percent: page errors ' + errors.join('; '));
    await page.close();
  }
  // 6. Pro, a question: H1 (the DAX is the lab's writing), H2 (rows with no runDax), the chips, then H7, H5, H6.
  {
    const { page, errors } = await open('tier=pro');
    // A hand-written query first, so H5 has something to put back.
    await clickText(page, 'DAX', '[role="group"]'); await wait(300);
    await page.evaluate(() => { const v = document.querySelector('.cm-content'); if (v) v.focus(); });
    await page.keyboard.down('Control'); await page.keyboard.press('a'); await page.keyboard.up('Control');
    await page.keyboard.type('EVALUATE ROW("mine", 1)'); await wait(300);
    await clickText(page, 'Build', '[role="group"]'); await wait(300);
    const runsBefore = await count(page, 'runDax');
    await page.focus('[data-testid="queries-ask"] input');
    await page.keyboard.type('total sales by region');
    // H7: the run key in the field asks and does not run the old query.
    await page.keyboard.down('Control'); await page.keyboard.press('Enter'); await page.keyboard.up('Control');
    await wait(1400);
    expect(await count(page, 'askData') === 1, 'H7: the run key in the field asked once');
    expect(await count(page, 'runDax') === runsBefore, 'H2/H7: no runDax for an ask (rows come from the answer)');
    expect(await has(page, '[data-testid="queries-ask-chips"][data-owns="yes"]'), 'the question\'s chips own the query');
    expect((await text(page, '[data-testid="queries-ask-chips"]')).includes('Jev read it as'), 'Jev read it as');
    expect(await has(page, '[data-testid="queries-putback"]'), 'H5: Put back my query is offered');
    await shoot(page, '12-asked', 'Pro: asked in words; Jev read it as chips, the result holds the answer rows, and Put back my query');
    // H6: the only check is a number from this answer.
    await clickText(page, 'Save measure check'); await wait(200);
    const choices = await page.evaluate(() => [...document.querySelectorAll('button')].map((b) => b.textContent).filter((t) => /Check against source SQL|Check a trusted answer|Save a number from this answer/.test(t)));
    expect(choices.length === 1 && /Save a number from this answer/.test(choices[0]), 'H6: only a number from this answer, got ' + choices.join(' | '));
    await clickText(page, 'Save a number from this answer'); await wait(300);
    await shoot(page, '13-save-number', 'H6: Save a number from this answer, the shared chooser', { sizes: [[1440, 900]], check: false });
    await clickText(page, 'Cancel'); await wait(200);
    // A chip edit asks with parts only: no Jev.
    const before = await page.evaluate(() => window.__askDataMock.calls.length);
    await page.evaluate(() => document.querySelector('[data-testid="queries-ask-chips"] [data-chip="group"]')?.click()); await wait(250);
    await clickText(page, 'Category', '[data-testid="askdata-menu"]'); await wait(1200);
    const sent = await page.evaluate(() => window.__askDataMock.calls.slice());
    expect(sent.length === before + 1 && !sent[sent.length - 1].question, 'a chip edit sends parts only, so Jev is not asked');
    expect(sent.every((c) => c.maxRows === 10000), 'Queries asks for up to 10,000 rows');
    // A hand edit steps the chips aside.
    await clickText(page, 'DAX', '[role="group"]'); await wait(300);
    await page.evaluate(() => document.querySelector('.cm-content')?.focus());
    await page.keyboard.press('End'); await page.keyboard.type(' '); await page.keyboard.type('-- mine'); await wait(300);
    await clickText(page, 'Build', '[role="group"]'); await wait(300);
    expect(await has(page, '[data-testid="queries-ask-aside"]'), 'a hand edit steps the chips aside');
    await shoot(page, '14-chips-aside', 'A hand edit: the chips step aside, with Build from the parts again', { sizes: [[1440, 900]] });
    // Over the hand edit Put back is not offered (it would throw the edit away); rebuilding from the parts brings it back.
    expect(!(await has(page, '[data-testid="queries-putback"]')), 'H5: Put back is not offered over a newer hand edit');
    await clickText(page, 'Build from the parts again'); await wait(1200);
    expect(await has(page, '[data-testid="queries-putback"]'), 'H5: Put back is offered again once the parts rebuilt the query');
    await clickText(page, 'Put back my query'); await wait(400);
    const q = await page.evaluate(() => document.querySelector('.cm-content')?.textContent || '');
    // The rebuild wrote over the hand edit, so the hand edit is now the person's own query to put back (H5 keeps the
    // newest text the person wrote before the box writes over it).
    expect(q.includes('-- mine'), 'H5: Put back my query restored the hand-written query, got ' + q.slice(0, 60));
    expect(errors.length === 0, 'asked: page errors ' + errors.join('; '));
    await page.close();
  }
  // 6b. Two good readings (Kane, 28 Sep): the top one runs, the other is named with a switch that asks its parts, no words.
  {
    const { page, errors } = await open('tier=pro');
    await page.focus('[data-testid="queries-ask"] input');
    await page.keyboard.type('how are we doing by region'); await page.keyboard.press('Enter'); await wait(1400);
    const others = await text(page, '[data-testid="askdata-others"]');
    expect(others.includes('Showing Total Sales. You may have meant Margin.') && others.includes('Show Margin instead'), 'queries: the other reading is named with a switch: ' + others);
    await shoot(page, '12b-two-readings', 'Queries box: Jev torn between two numbers; the top one runs and the other is named', { sizes: [[1440, 900]] });
    const before = await page.evaluate(() => window.__askDataMock.calls.length);
    await clickText(page, 'Show Margin instead', '[data-testid="askdata-others"]'); await wait(1400);
    const sent = await page.evaluate(() => window.__askDataMock.calls.slice());
    const last = sent[sent.length - 1];
    expect(sent.length === before + 1 && !last.question && (last.measures || []).join() === 'Margin', 'queries: the switch asks the other parts only: ' + JSON.stringify(last));
    expect(!(await has(page, '[data-testid="askdata-others"]')), 'queries: after the switch no other is named');
    await shoot(page, '12c-switched', 'Queries box after Show Margin instead: built from your choices', { sizes: [[1440, 900]] });
    expect(errors.length === 0, 'two readings: page errors ' + errors.join('; '));
    await page.close();
  }
  // 6c. A reading the check stopped (the M11 join, 30 Sep): the box says the statement first, the stop line without it, and
  // the same "Show it anyway" as Ask your data, which asks the reading's parts only.
  {
    const { page, errors } = await open('tier=pro');
    await page.focus('[data-testid="queries-ask"] input');
    await page.keyboard.type('total sales since last year'); await page.keyboard.press('Enter'); await wait(1400);
    const said = await text(page, '[data-testid="queries-ask-statement"]');
    expect(said.includes('This shows Total Sales as one total, where Year is 2024.'), 'queries: the stopped reading is said first: ' + said);
    const lines = await text(page, '[data-testid="queries-ask-lines"]');
    expect((lines.match(/This shows Total Sales/g) || []).length === 1, 'queries: the statement is said once: ' + lines);
    expect(lines.includes('Show it anyway') && !lines.includes('instead'), 'queries: the stopped reading is one click away: ' + lines);
    await shoot(page, '12d-stopped', 'Queries box: the check stopped the reading; the statement, the gap, and Show it anyway', { sizes: [[1440, 900]] });
    const before = await page.evaluate(() => window.__askDataMock.calls.length);
    await clickText(page, 'Show it anyway', '[data-testid="askdata-others"]'); await wait(1400);
    const sent = await page.evaluate(() => window.__askDataMock.calls.slice());
    const last = sent[sent.length - 1];
    expect(sent.length === before + 1 && !last.question && (last.measures || []).join() === 'Total Sales', 'queries: Show it anyway asks the parts only: ' + JSON.stringify(last));
    expect(errors.length === 0, 'stopped: page errors ' + errors.join('; '));
    await page.close();
  }
  // 7. Pro, states of the box: reading (held), refused, words switched off.
  {
    const { page, errors } = await open('tier=pro');
    await page.evaluate(() => window.__uishotHarness.hold('askData'));
    await page.focus('[data-testid="queries-ask"] input'); await page.keyboard.type('total sales by region'); await page.keyboard.press('Enter'); await wait(400);
    expect(await has(page, '[data-testid="queries-ask-reading"]'), 'reading: the reading line');
    expect(await page.evaluate(() => [...document.querySelectorAll('button')].some((b) => b.textContent.trim() === 'Stop')), 'reading: one Stop (H3)');
    await shoot(page, '15-reading', 'Reading: Jev reads, Semanticus builds and checks; one Stop for the tab', { sizes: [[1440, 900]], check: false });
    await clickText(page, 'Stop'); await wait(300);
    expect(await has(page, '[data-testid="queries-ask-stopped"]'), 'stopped: the stopped line');
    await page.evaluate(() => window.__uishotHarness.release('askData')); await wait(300);
    expect(errors.length === 0, 'states: page errors ' + errors.join('; '));
    await page.close();
  }
  // 8. Free: one quiet Pro line, no field, zero askData and listDataQuestions calls; everything else still works.
  {
    const { page, errors } = await open('tier=free');
    expect(!(await has(page, '[data-testid="queries-ask"]')), 'free: no question field');
    expect(await has(page, '[data-testid="queries-ask-pro-pill"]'), 'free: the quiet Pro line');
    await clickText(page, 'Run the query'); await wait(1000);
    expect(await count(page, 'runDax') >= 1, 'free: Run still runs');
    await shoot(page, '16-free', 'Free: the quiet Pro line, no field; the sentence, the result and the rules pick all work');
    const m = await methods(page);
    expect(!m.includes('askData') && !m.includes('listDataQuestions'), 'free: zero askData and listDataQuestions calls');
    expect(errors.length === 0, 'free: page errors ' + errors.join('; '));
    await page.close();
  }
  // 9. Keyboard only: Tab from the view switch reaches the field, the chips and the actions; Ctrl+Enter outside the field runs.
  {
    const { page, errors } = await open('tier=pro');
    await page.evaluate(() => document.querySelector('[role="group"][aria-label="DAX Lab mode"] button')?.focus());
    const order = [];
    for (let i = 0; i < 14; i++) { await page.keyboard.press('Tab'); order.push(await page.evaluate(() => { const a = document.activeElement; return (a?.getAttribute('aria-label') || a?.textContent || a?.tagName || '').replace(/\s+/g, ' ').trim().slice(0, 40); })); }
    expect(order.some((o) => o.startsWith('Ask about your data')), 'keyboard: Tab reaches the question field: ' + order.join(' > '));
    expect(order.some((o) => o.startsWith('Run the query')), 'keyboard: Tab reaches Run');
    const before = await count(page, 'runDax');
    await page.evaluate(() => document.querySelector('[aria-label="Result views"] button')?.focus());
    await page.keyboard.down('Control'); await page.keyboard.press('Enter'); await page.keyboard.up('Control'); await wait(900);
    expect(await count(page, 'runDax') === before + 1, 'keyboard: Ctrl+Enter outside the field runs');
    writeFileSync(join(OUT, 'keyboard-order.txt'), order.join('\n') + '\n');
    expect(errors.length === 0, 'keyboard: page errors ' + errors.join('; '));
    await page.close();
  }
} catch (e) {
  failures.push('driver crashed: ' + (e?.stack || e));
} finally {
  await browser.close();
  server.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures }, null, 2));
console.log(`queries-drive: ${results.length} screenshots in ${OUT}`);
if (failures.length) { console.log('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('queries-drive: all checks passed');
