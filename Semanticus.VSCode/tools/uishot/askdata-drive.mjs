// Acceptance driver for the Ask your data page (26 September 2026). The REAL Studio through the mock engine
// (harness.html's __askDataMock stands in for AskDataAsync), driven by clicks and keys the way a person drives
// it, and shot in Dark Modern, Light Modern and Dark High Contrast.
//
// WHAT IT PROVES, per scene, from the harness's own call trace:
//   - every chip edit (swap, remove, add, top N, sort) sends askData with parts and NO words, so Jev is never asked;
//   - a refusal shows its line verbatim and keeps the chips;
//   - Free makes zero askData and zero listDataQuestions calls and shows the preview;
//   - an ask the assistant made is in the list with the "Your assistant" marker, and Run again reruns it.
// WHAT IT DOES NOT: prove the engine. The real journey on Northstar Working is the UAT lane's.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/askdata-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'askdata');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `askdata-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = ['dark', 'light', 'hc'];
const results = [];
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };

async function open(q) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=askdata&${q}`, { waitUntil: 'networkidle0', timeout: 45000 });
  await wait(2800);
  return { page, errors };
}
const calls = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).map((m) => m.method).filter(Boolean));
const asks = (page) => page.evaluate(() => (window.__askDataMock?.calls || []).slice());
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const clickText = (page, label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, scope);
const chip = (page, kind, i = 0) => page.evaluate((k, n) => { const b = document.querySelectorAll(`[data-chip="${k}"]`)[n]; if (b) b.click(); return !!b; }, kind, i);
const menuPick = async (page, label) => { await wait(150); return clickText(page, label, '[data-testid="askdata-menu"]'); };
const typeIn = async (page, sel, value) => { await page.focus(sel); await page.keyboard.type(value); };
async function settle(page) { await wait(700); }

async function shoot(page, slug, desc) {
  for (const t of THEMES) {
    await applyTheme(page, t);
    await wait(250);
    const file = join(OUT, `${slug}-${t}.png`);
    await page.screenshot({ path: file });
    results.push({ slug, theme: t, file, desc });
  }
  await applyTheme(page, null);
}

// Build "Total Sales by Region, Year = 2025" from the empty page, by clicks and typing.
async function build(page) {
  await chip(page, 'add'); await menuPick(page, 'Total Sales'); await settle(page);
  await chip(page, 'add'); await menuPick(page, 'A column to split by'); await wait(200);
  await page.evaluate(() => { const i = document.querySelector('[data-testid="askdata-menu"] input'); if (i) { i.value = ''; } });
  await menuPick(page, 'Region'); await settle(page);
  await chip(page, 'add'); await menuPick(page, 'A filter'); await wait(200);
  await menuPick(page, 'Year'); await wait(200);
  await typeIn(page, '[data-testid="askdata-menu"] input[aria-label="Values to keep"]', '2025');
  await page.keyboard.press('Enter'); await settle(page);
}

try {
  // 1. Empty.
  {
    const { page, errors } = await open('tier=pro');
    expect(await page.$('[data-testid="askdata-empty"]'), 'empty: the choose-a-number prompt');
    await shoot(page, '01-empty', 'Empty: nothing asked yet, and the list holds the assistant\'s earlier ask');
    expect((await text(page, '[data-testid="askdata-history"]')).includes('Your assistant'), 'history: the assistant marker');
    expect(errors.length === 0, 'empty: page errors ' + errors.join('; '));
    await page.close();
  }
  // 2. The journey: build, then every chip edit, each one a parts ask with no words.
  {
    const { page, errors } = await open('tier=pro');
    await build(page);
    const a = await text(page, '[data-testid="askdata-answer"]');
    expect(/rows/.test(a), 'answered: the rows line');
    await shoot(page, '02-answered', 'Answered: Total Sales by Region, Year = 2025, from chips');
    await chip(page, 'group'); await wait(200);
    await shoot(page, '03-chip-menu', 'A chip open: swap, sort or remove the split');
    await page.keyboard.press('Escape'); await wait(200);
    expect(!(await page.$('[data-testid="askdata-menu"]')), 'escape closes the chip menu');
    const focused = await page.evaluate(() => document.activeElement?.getAttribute('data-chip'));
    expect(focused === 'group', 'escape returns focus to the chip, got ' + focused);
    await chip(page, 'group'); await menuPick(page, 'Category'); await settle(page);           // swap
    await chip(page, 'add'); await menuPick(page, 'Top 10'); await settle(page);             // top N
    await chip(page, 'top'); await menuPick(page, '3'); await settle(page);                  // change N
    await chip(page, 'add'); await menuPick(page, 'A number'); await menuPick(page, 'Margin'); await settle(page); // add measure
    await chip(page, 'measure', 1); await menuPick(page, 'Remove'); await settle(page);      // remove
    await chip(page, 'top'); await menuPick(page, 'Remove the top'); await settle(page);
    await chip(page, 'measure', 0); await menuPick(page, 'Sort by'); await settle(page);     // sort
    const sent = await asks(page);
    expect(sent.length >= 9, `journey: ${sent.length} asks`);
    expect(sent.every((x) => !x.question), 'every chip ask carries no words, so Jev is never asked');
    await clickText(page, 'Chart'); await wait(400);
    await shoot(page, '04-chart', 'The same answer as a chart, on the shared chart');
    await clickText(page, 'Table'); await clickText(page, 'Show DAX'); await wait(300);
    await shoot(page, '05-dax', 'The DAX view, the only place DAX words appear');
    await clickText(page, 'Save as a check'); await wait(300);
    await shoot(page, '06-save-chooser', 'Save as a check: pick the number and the row');
    await clickText(page, 'Continue'); await wait(800);
    expect((await page.evaluate(() => document.body.textContent || '')).includes('Trusted answer'), 'the check drawer opened');
    await shoot(page, '07-check-drawer', 'The existing New check drawer, filled from the answer');
    // Leave for Data and come back: the question stays, with a Run button, because Studio unmounts a tab.
    await page.evaluate(() => document.querySelector('[data-testid="askdata-page"]') && null);
    await page.keyboard.press('Escape'); await wait(200);
    await page.evaluate(() => { const b = document.querySelector('[data-tab="data"]'); b?.click(); }); await wait(800);
    await page.evaluate(() => { const b = document.querySelector('[data-tab="askdata"]'); b?.click(); }); await wait(1200);
    expect(await page.$('[data-testid="askdata-idle"]'), 'coming back keeps the question and offers Run');
    expect(await page.$('[data-chip="measure"]'), 'coming back keeps the chips');
    await shoot(page, '07b-came-back', 'Back on the page after Data: the question kept, with Run');
    await clickText(page, 'Run', '[data-testid="askdata-idle"]'); await settle(page);
    await clickText(page, 'Open in Queries'); await wait(1500);
    const lab = await page.evaluate(() => ({ tab: document.querySelector('[data-tab="daxlab"][aria-current="page"]') != null, dax: (document.body.textContent || '').includes('SUMMARIZECOLUMNS') }));
    expect(lab.dax, 'Open in Queries lands on Queries with the engine\'s DAX in the editor');
    await shoot(page, '07c-open-in-queries', 'Open in Queries: the query the engine built, in the editor');
    expect(errors.length === 0, 'journey: page errors ' + errors.join('; '));
    await page.close();
  }
  // 3. Refused: a filter word with no match.
  {
    const { page } = await open('tier=pro');
    await build(page);
    await chip(page, 'filter'); await wait(200);
    await page.evaluate(() => { const i = document.querySelector('[data-testid="askdata-menu"] input[aria-label="Values to keep"]'); if (i) { const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set; set.call(i, ''); i.dispatchEvent(new Event('input', { bubbles: true })); } });
    await typeIn(page, '[data-testid="askdata-menu"] input[aria-label="Values to keep"]', 'Atlantis');
    await page.keyboard.press('Enter'); await settle(page);
    const r = await text(page, '[data-testid="askdata-refused"]');
    expect(r.includes("No value in Year matches 'Atlantis'."), 'refused line verbatim: ' + r);
    expect(await page.$('[data-chip="measure"]'), 'refused keeps the chips');
    await shoot(page, '08-refused', 'Refused: the engine\'s line, word for word, and the chips kept');
    await page.close();
  }
  // 4. Words, read by Jev (query.summarize, on for Pro): the reading is the chips, and the answer says Jev read it.
  {
    const { page } = await open('tier=pro');
    await typeIn(page, 'input[aria-label="Ask in your own words"]', 'total sales by region');
    await page.keyboard.press('Enter'); await settle(page);
    const chips = await page.evaluate(() => [...document.querySelectorAll('[data-chip]')].map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()));
    expect(chips.some((c) => c.startsWith('Total Sales')) && chips.some((c) => c.startsWith('Region')), 'words: the reading is the chips: ' + chips.join(' | '));
    expect((await text(page, '[data-testid="askdata-checks"]')).includes('Jev read this.'), 'words: the answer says Jev read it');
    expect(await page.$('[data-outcome="answered"]'), 'words: answered');
    expect(!(await text(page, 'body')).includes('not switched on'), 'words: no not-yet line when Jev read it');
    await shoot(page, '09-words-jev-read', 'A question in words: Jev read it into the chips, and Semanticus built and ran the query');
    await page.close();
  }
  // 4b. Words with the switch off for this model, and in a build where it is not on: the engine's line, chips kept.
  // 4b. Two good readings (Kane, 28 Sep): the top one runs, the other is named, and one click switches with no words sent.
  {
    const { page, errors } = await open('tier=pro');
    await typeIn(page, 'input[aria-label="Ask in your own words"]', 'how are we doing by region');
    await page.keyboard.press('Enter'); await wait(900);
    const others = await text(page, '[data-testid="askdata-others"]');
    expect(others.includes('Showing Total Sales. You may have meant Margin.'), 'two readings: the other is named: ' + others);
    expect(others.includes('Show Margin instead'), 'two readings: one button to switch');
    await shoot(page, '09d-two-readings', 'Jev torn between two numbers: the top one runs, and the other is named with a switch');
    const before = (await asks(page)).length;
    await clickText(page, 'Show Margin instead', '[data-testid="askdata-others"]'); await settle(page);
    const sent = (await asks(page)).slice(before);
    expect(sent.length === 1 && !sent[0].question && (sent[0].measures || []).join() === 'Margin', 'the switch asks the other parts, with no words: ' + JSON.stringify(sent));
    const chipsNow = await page.evaluate(() => [...document.querySelectorAll('[data-chip="measure"]')].map((b) => b.textContent.trim()));
    expect(chipsNow.some((c) => c.startsWith('Margin')), 'after the switch the chips show Margin: ' + chipsNow.join(' | '));
    expect(!(await page.$('[data-testid="askdata-others"]')), 'after the switch, a reading you chose names no other');
    await shoot(page, '09e-switched', 'After Show Margin instead: the other reading, built from your choices, no Jev call');
    expect(errors.length === 0, 'two readings: page errors ' + errors.join('; '));
    await page.close();
  }
  // 4c. Never a number the question did not ask for, unsaid (Kane's ruling, 28 Sep, round 2): a comparison shown side by
  // side on one table with the other named; "last week" read on the model's days and said; words it cannot apply need you.
  {
    const { page, errors } = await open('tier=pro');
    const words = 'input[aria-label="Ask in your own words"]';
    const retype = async (v) => { await page.focus(words); await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control'); await page.keyboard.press('Backspace'); await page.keyboard.type(v); };
    await retype('total sales coast vs north');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="answered"]'), 'compare: answered');
    expect((await text(page, 'body')).includes("Showing 'coast' and 'north' side by side, split by Store Region."), 'compare: the side-by-side note');
    const others = await text(page, '[data-testid="askdata-others"]');
    expect(others.includes('You may have meant Customer Region.') && others.includes('Show Customer Region instead'), 'compare: the other table is named with a switch: ' + others);
    await shoot(page, '09f-compare-twin', 'Coast vs North where Region is in two tables: side by side on one, the other named');
    await retype('total sales last week');
    await page.keyboard.press('Enter'); await wait(900);
    expect((await text(page, 'body')).includes("'last week' is read as Monday 29 September to Sunday 5 October 2025."), 'last week: said');
    const dayChips = await page.evaluate(() => [...document.querySelectorAll('[data-chip]')].map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()));
    expect(dayChips.some((c) => c.includes('from 2025-09-29')) && dayChips.some((c) => c.includes('before 2025-10-06')), 'last week: the day range is two chips: ' + dayChips.join(' | '));
    await shoot(page, '09g-last-week', 'Last week read on the model\'s days, the range shown as chips and said');
    await retype('total sales last financial year');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="needs-you"]'), 'financial year: needs you');
    expect((await text(page, '[data-testid="askdata-unsure"]')).includes("cannot apply 'last financial year' yet"), 'financial year: the words are named');
    expect(!(await page.$('[data-testid="askdata-others"]')), 'financial year: no other reading offered');
    await shoot(page, '09h-cannot-apply', 'Last financial year: nothing runs, and the words it could not apply are named');
    // Round 5: the check by Jev stopped the reading. The statement is the first line, the gap is said, nothing runs, and the
    // reading is one click away ("Show it anyway"), asked as parts with no words.
    await retype('total sales since last year');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="needs-you"]'), 'stopped: needs you');
    expect((await text(page, '[data-testid="askdata-statement"]')).includes('This shows Total Sales as one total, where Year is 2024.'), 'stopped: the statement is the first line');
    const stopLine = await text(page, '[data-testid="askdata-unsure"]');
    expect(stopLine.includes('The question narrows the numbers in a way this reading does not show. Nothing was run.') && (stopLine.match(/This shows Total Sales/g) || []).length === 0, 'stopped: the gap is said once, the statement not repeated: ' + stopLine);
    const offer = await text(page, '[data-testid="askdata-others"]');
    expect(offer.includes('Show it anyway') && !offer.includes('instead'), 'stopped: the reading is offered as Show it anyway: ' + offer);
    expect(!(await text(page, 'body')).includes('Jev read this'), 'stopped: "Jev read this" is not shown over a reading that did not run');
    await shoot(page, '09i-stopped-by-the-check', 'The check stopped the reading: the statement first, what is missing, and Show it anyway one click away');
    const beforeOffer = (await asks(page)).length;
    await clickText(page, 'Show it anyway', '[data-testid="askdata-others"]'); await settle(page);
    const took = (await asks(page)).slice(beforeOffer);
    expect(took.length === 1 && !took[0].question && (took[0].filters || []).some((f) => f.column === "'Date'[Year]" && (f.values || []).join() === '2024'), 'stopped: the offer asks its parts, with no words: ' + JSON.stringify(took));
    expect(await page.$('[data-outcome="answered"]'), 'stopped: the offered reading runs');
    expect((await text(page, '[data-testid="askdata-statement"]')).startsWith('This shows Total Sales'), 'answered: the statement is the first line of an answer too');
    await shoot(page, '09j-took-the-offer', 'After Show it anyway: it runs as your choice, no Jev call, and says what it shows');
    // Round 6: a ratio says what it divides, and its number reads as the model's own percent (0.7%, not 0.0068119056469534).
    await retype('return rate');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="answered"]'), 'ratio: answered');
    expect((await text(page, '[data-testid="askdata-statement"]')).includes('Return Rate % (Returns Amount divided by Net Sales Amount)'), 'ratio: the statement names what it divides');
    const ratioGrid = await text(page, '[data-testid="askdata-answer"]');
    expect(ratioGrid.includes('0.7%') && !ratioGrid.includes('0.0068119056469534'), 'ratio: the grid shows the percent format: ' + ratioGrid);
    await shoot(page, '09l-ratio-statement', 'A ratio measure: the statement says what it divides, and the grid shows 0.7% for 0.0068');
    // G5: a share of orders asked of a ratio of value: stopped, with the reading one click away.
    await retype('what percentage of orders had a return');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="needs-you"]'), 'ratio share: needs you');
    const shareLine = await text(page, '[data-testid="askdata-unsure"]');
    expect(shareLine.includes('its words do not name what Return Rate % divides') && shareLine.includes('Nothing was run.'), 'ratio share: the stop line says what is wrong: ' + shareLine);
    expect((await text(page, '[data-testid="askdata-others"]')).includes('Show it anyway'), 'ratio share: the reading is one click away');
    await shoot(page, '09m-ratio-share-stopped', 'A share of orders asked of a value ratio: stopped, what it divides is named, Show it anyway');
    // G4: a fiscal period asked of a model with no fiscal year.
    await retype('total sales in fiscal 2025');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="needs-you"]'), 'fiscal: needs you');
    const fiscalLine = await text(page, '[data-testid="askdata-unsure"]');
    expect(fiscalLine.includes('this model has no fiscal year') && fiscalLine.includes('Nothing was run.'), 'fiscal: the stop line says the model has no fiscal year: ' + fiscalLine);
    expect((await text(page, '[data-testid="askdata-statement"]')).includes('This shows Total Sales as one total, where Year is 2025.'), 'fiscal: the statement is the first line');
    expect((await text(page, '[data-testid="askdata-others"]')).includes('Show it anyway'), 'fiscal: the calendar reading is one click away');
    await shoot(page, '09n-fiscal-stopped', 'A fiscal year asked of a model with no fiscal year: stopped, the calendar reading offered as Show it anyway');
    expect(errors.length === 0, 'ruling scenes: page errors ' + errors.join('; '));
    await page.close();
  }
  // 4c2. A percent answer keeps its format when it is opened in Queries (Sol 6.1, 3.1): Return Rate % 0.0068 is 0.7% there too, never a
  // rounded 0.01 under the percent measure's own name.
  {
    const { page, errors } = await open('tier=pro');
    const words = 'input[aria-label="Ask in your own words"]';
    await page.focus(words); await page.keyboard.type('return rate');
    await page.keyboard.press('Enter'); await wait(900);
    expect(await page.$('[data-outcome="answered"]'), 'queries percent: answered');
    await clickText(page, 'Open in Queries'); await wait(1500);
    const shown = await text(page, '[data-testid="queries-page"]');
    expect(shown.includes('0.7%') && !/(^|[^\d.])0\.01([^\d]|$)/.test(shown), 'queries percent: Queries shows 0.7% for 0.0068: ' + shown.slice(0, 400));
    await shoot(page, '09o-queries-percent', 'Open in Queries on a percent answer: the grid shows 0.7%, as the answer did');
    expect(errors.length === 0, 'queries percent: page errors ' + errors.join('; '));
    await page.close();
  }
  // 4d. A date range from the Studio (golden rule 2): the assistant can send one, so the person can build one too.
  {
    const { page, errors } = await open('tier=pro');
    await build(page);
    await chip(page, 'add'); await menuPick(page, 'A filter'); await wait(200);
    const hint = await text(page, '[data-testid="askdata-menu"]');
    expect(hint.includes('A date keeps a range') && !hint.includes('Dates and yes or no columns cannot be filtered yet'), 'date: the menu copy is true: ' + hint);
    expect(await menuPick(page, 'OrderDate (Sales)'), 'date: the column of days is offered under Filter on');
    await wait(200);
    const ops = await page.evaluate(() => [...document.querySelectorAll('[data-testid="askdata-menu"] select[aria-label="How it filters"] option')].map((o) => o.value));
    expect(ops.join() === 'after,from,before,up_to', 'date: only the four range words: ' + ops.join());
    expect(await page.$('[data-testid="askdata-menu"] input[type="date"]'), 'date: a day picker, not a list of values: ' + await text(page, '[data-testid="askdata-menu"]'));
    await page.evaluate(() => {
      const i = document.querySelector('[data-testid="askdata-menu"] input[type="date"]');
      if (!i) return;
      const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(i, '2025-07-01'); i.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await wait(200);
    await shoot(page, '09k-date-filter', 'Filter on a date: from, after, before or up to one day, the range the assistant can send');
    const beforeDate = (await asks(page)).length;
    await menuPick(page, 'Apply'); await settle(page);
    const sentDate = (await asks(page)).slice(beforeDate);
    expect(sentDate.length === 1 && (sentDate[0].filters || []).some((f) => f.column === "'Sales'[OrderDate]" && f.op === 'from' && (f.values || []).join() === '2025-07-01'), 'date: the range is sent as parts: ' + JSON.stringify(sentDate));
    const dateChips = await page.evaluate(() => [...document.querySelectorAll('[data-chip]')].map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()));
    expect(dateChips.some((c) => c.includes('from 2025-07-01')), 'date: the range is a chip: ' + dateChips.join(' | '));
    await shoot(page, '09l-date-range-chip', 'The date range as a chip, built by the person');
    // A date column, then Back, then a text column: the range word is not kept for text (Sol, 29 Sep).
    await chip(page, 'add'); await menuPick(page, 'A filter'); await wait(200);
    await menuPick(page, 'OrderDate (Sales)'); await wait(200);
    await menuPick(page, 'Back'); await wait(200);
    await menuPick(page, 'Region (Customer)'); await wait(300);
    const textOp = await page.evaluate(() => document.querySelector('[data-testid="askdata-menu"] select[aria-label="How it filters"]')?.value);
    expect(textOp === 'is', 'date then text: the text filter opens on is, got ' + textOp);
    await page.keyboard.press('Escape'); await wait(200);
    expect(errors.length === 0, 'date: page errors ' + errors.join('; '));
    await page.close();
  }
  for (const [slug, mode, line] of [['09b-words-switched-off', 'wordsoff', 'switched off for this model'], ['09c-words-not-yet', 'notyet', 'not switched on in this version yet']]) {
    const { page } = await open('tier=pro&askdata=' + mode);
    await typeIn(page, 'input[aria-label="Ask in your own words"]', 'total sales by region');
    await page.keyboard.press('Enter'); await settle(page);
    expect((await text(page, '[data-testid="askdata-unsure"]')).includes(line), `${slug}: the engine's line`);
    await shoot(page, slug, `A question in words with Jev reading off: "${line}"`);
    await page.close();
  }
  // 5. Built, no data. 6. Error. 7. Unsaved. 8. Capped.
  for (const [slug, q, check, desc] of [
    ['10-built-no-data', 'tier=pro&askdata=nodata', '[data-outcome="built"]', 'Built with no data connection: chips, DAX and the connect bar, no grid'],
    ['11-error', 'tier=pro&askdata=error', '[data-testid="askdata-error"]', 'Error: the plain line and Sign in again'],
    ['12-unsaved', 'tier=pro&askdata=unsaved', '[data-testid="askdata-history"]', 'Unsaved model: the history note'],
    ['13-capped', 'tier=pro&askdata=capped', '[data-outcome="answered"]', 'Stopped at 500 rows'],
  ]) {
    const { page } = await open(q);
    if (slug !== '12-unsaved') await build(page);
    expect(await page.$(check), `${slug}: ${check}`);
    if (slug === '10-built-no-data') expect(!(await page.$('[data-testid="askdata-answer"] .sem-grid-scroll, [data-testid="askdata-chart"]')), 'built: no grid');
    // A failed run says ONE plain thing and keeps the server's own words behind Show details, with nothing on
    // screen claiming the query checked out (verify-askdata-r5, r5-062, where the raw text was printed twice).
    if (slug === '11-error') {
      const shown = await text(page, '[data-testid="askdata-answer"]');
      expect(!/RootActivityId/.test(shown), 'error: the server raw text is not on screen until it is asked for');
      expect(!/checks out/.test(shown), 'error: nothing says the query checked out');
      // The SHUT state is shot before Show details is pressed. It used to be shot after, so 11-error and
      // 11b-error-details were the same picture and no picture of the shut state existed (verify-askdata-r6 L7).
      await shoot(page, slug, desc);
      expect(await clickText(page, 'Show details'), 'error: Show details is there'); await wait(300);
      const raw = await page.$eval('[data-testid="askdata-error-raw"] pre', (el) => ({ wrap: getComputedStyle(el).whiteSpace, over: el.scrollWidth - el.clientWidth }));
      expect(raw.wrap === 'pre-wrap' && raw.over <= 1, `error: what the server said wraps and scrolls sideways by nothing (${raw.wrap}, ${raw.over}px)`);
      expect(/RootActivityId/.test(await text(page, '[data-testid="askdata-error-raw"]')), 'error: opened, it shows what the server said');
      expect(((await text(page, '[data-testid="askdata-answer"]')).match(/RootActivityId/g) || []).length === 1,
        'error: the server raw text is shown once, not twice');
      await shoot(page, '11b-error-details', 'Error with Show details open: one plain notice, the server\'s own words once');
    }
    if (slug !== '11-error') await shoot(page, slug, desc);
    await page.close();
  }
  // 9. Running, with Stop.
  {
    const { page } = await open('tier=pro&hold=askData');
    await chip(page, 'add'); await menuPick(page, 'Total Sales'); await wait(500);
    expect(await page.$('[data-testid="askdata-running"]'), 'running: the in-flight line and Stop');
    await shoot(page, '14-running', 'Running: one in-flight line and Stop');
    await clickText(page, 'Stop'); await wait(300);
    const trace = await calls(page);
    expect(trace.includes('cancelDax'), 'Stop sends the query cancel');
    await shoot(page, '15-stopped', 'Stopped');
    await page.close();
  }
  // 10. Run again from the list: the assistant's ask, rerun the same.
  {
    const { page } = await open('tier=pro');
    await page.evaluate(() => { const b = document.querySelector('[data-testid="askdata-history-row"] button'); b?.click(); });
    await settle(page);
    const sent = await asks(page);
    expect(sent.length === 1 && sent[0].top === 3 && sent[0].groupBy[0] === "'Product'[Category]", 'run again resends the kept parts');
    await shoot(page, '16-run-again', 'Run again on the assistant\'s question: its table, from the kept parts');
    await page.close();
  }
  // 10b. The assistant asks while the page is open: the engine pushes an ask_data activity, and the list refreshes.
  {
    const { page } = await open('tier=pro');
    const before = await page.$$eval('[data-testid="askdata-history-row"]', (r) => r.length);
    await page.evaluate(() => {
      const a = window.__askDataMock.agentAsk({ measures: ['Units'], groupBy: ["'Customer'[Region]"] });
      window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
        seq: 9001, kind: 'ask_data', origin: 'agent', label: 'Asked a question about the data', target: a.reading, ok: true, at: new Date().toISOString() } } }));
    });
    await wait(900);
    const after = await page.$$eval('[data-testid="askdata-history-row"]', (r) => r.length);
    expect(after === before + 1, `pushed assistant ask appears without Refresh (${before} -> ${after})`);
    await shoot(page, '16b-pushed-assistant-ask', 'The assistant asked while the page was open: the list updated by itself');
    await page.close();
  }
  // 11. Free: the preview, and not one call.
  {
    const { page } = await open('');
    const trace = await calls(page);
    expect(!trace.includes('askData') && !trace.includes('listDataQuestions'), 'free: zero askData and listDataQuestions calls, got ' + trace.filter((m) => /Data/.test(m)).join(','));
    expect(await page.$('[data-pro-preview="askdata"]'), 'free: the preview');
    await shoot(page, '17-free', 'Free: the preview, and no call behind it');
    await page.close();
  }
} finally {
  await browser.close(); server.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures }, null, 2));
console.log(`${results.length} shots in ${OUT}`);
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('askdata-drive: every scene held');
