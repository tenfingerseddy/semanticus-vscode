// Focused browser proof for the integrated five-area shell. It drives the built React bundle through the same
// host-message bridge used by VS Code and asserts state transitions that source-pattern tests cannot prove.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((request, response) => {
  const urlPath = decodeURIComponent((request.url || '/').split('?')[0]);
  const path = normalize(join(webRoot, urlPath));
  if (!path.startsWith(webRoot) || !existsSync(path) || statSync(path).isDirectory()) { response.writeHead(404); response.end('not found'); return; }
  response.writeHead(200, { 'content-type': types[extname(path)] || 'application/octet-stream' }); response.end(readFileSync(path));
});
await new Promise((done) => server.listen(0, '127.0.0.1', done));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox'] });

const delay = (ms) => new Promise((done) => setTimeout(done, ms));
const text = (page) => page.evaluate(() => document.body.innerText);
const dispatch = (page, data) => page.evaluate((message) => window.__uishotHarness.dispatch(message), data);
const clickText = async (page, label, within = 'body') => {
  const clicked = await page.evaluate(({ label, within }) => {
    const root = document.querySelector(within);
    const button = root && [...root.querySelectorAll('button')].find((item) => (item.textContent || '').trim() === label && item.getClientRects().length > 0);
    if (button) button.click();
    return !!button;
  }, { label, within });
  assert.equal(clicked, true, `button ${label} exists`);
};
const waitText = (page, expected) => page.waitForFunction((value) => document.body.innerText.includes(value), { timeout: 15000 }, expected);
const activeArea = (page) => page.evaluate(() => document.querySelector('nav[aria-label="Studio areas"] button[aria-current="page"]')?.textContent?.trim());
const openPage = async (query = 'live=1') => {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    const value = message.text();
    const expectedDocsNotice = value === "Blocked script execution in 'about:srcdoc' because the document's frame is sandboxed and the 'allow-scripts' permission is not set.";
    if (message.type() === 'error' && !expectedDocsNotice) errors.push(value);
  });
  await page.setViewport({ width: 1440, height: 980, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  return { page, errors };
};

try {
  // Opening Studio with an existing native selection can deliver that selection before the model information.
  const cold = await openPage('live=1&initialSelection=measure%3ASales%2FTotal%20Sales');
  await cold.page.waitForSelector('.overview-row');
  await waitText(cold.page, 'Edit formula');
  assert.ok((await cold.page.$eval('.object-actions', (node) => node.textContent)).includes('Total Sales'));
  assert.deepEqual(cold.errors, []);
  await cold.page.close();

  // tier=pro, from 2026-09-15. This journey walks the DAX Lab and Tests hand-offs, and Tests is one whole
  // Pro feature now: on the free plan the Tests page renders its preview and the New check drawer it drives
  // below does not exist at all. The subject here is the seed hand-off, not the entitlement, so the journey
  // opens on a plan that can reach it. The free side of that boundary is driven by cp-pro-drive.mjs.
  const { page, errors } = await openPage('tier=pro');
  await page.waitForSelector('.overview-row');
  assert.equal(await activeArea(page), 'Model');

  // An exact object action reaches the native host relay with the chosen ref, not whichever tree row is selected later.
  await dispatch(page, { type: 'treeSelection', sessionId: 'uishot', ref: 'measure:Sales/Total Sales' });
  await waitText(page, 'Edit formula');
  await clickText(page, 'Edit formula', '.object-actions');
  const editMessage = await page.evaluate(() => window.__uishotHarness.messages.filter((item) => item.type === 'runCommand').at(-1));
  assert.deepEqual(editMessage, { type: 'runCommand', command: 'semanticus.editDax', ref: 'measure:Sales/Total Sales', sessionId: 'uishot' });

  // A deliberate measure seed escapes the output label and DAX identifier separately, then is consumed once.
  const awkwardRef = 'measure:Sales/Net "Sales]';
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab', target: awkwardRef });
  await page.waitForFunction(() => window.__uishotHarness.state()['input:lab.bq.uishot'] === 'EVALUATE ROW("Net ""Sales]", [Net "Sales]]])');
  assert.equal(await activeArea(page), 'Calculations');
  const editor = await page.$('.cm-content');
  assert.ok(editor, 'DAX query editor mounted');
  await editor.click();
  await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
  await page.keyboard.type('EVALUATE ROW("mine", 42)');
  await page.waitForFunction(() => window.__uishotHarness.state()['input:lab.bq.uishot'] === 'EVALUATE ROW("mine", 42)');
  await clickText(page, 'Model', 'nav[aria-label="Studio areas"]');
  await clickText(page, 'Calculations', 'nav[aria-label="Studio areas"]');
  assert.equal(await page.evaluate(() => window.__uishotHarness.state()['input:lab.bq.uishot']), 'EVALUATE ROW("mine", 42)');

  // A second Add test hand-off retargets the open draft without erasing fields the person already typed.
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'tests', target: 'measure:Sales/Total Sales' });
  await page.waitForSelector('aside select');
  await page.waitForFunction(() => document.querySelector('aside select')?.value === 'measure:Sales/Total Sales');
  await page.evaluate(() => {
    const input = document.querySelectorAll('aside input')[0];
    const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    set.call(input, '42'); input.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'tests', target: 'measure:Sales/Total Cost' });
  await page.waitForFunction(() => document.querySelector('aside select')?.value === 'measure:Sales/Total Cost');
  assert.equal(await page.evaluate(() => document.querySelectorAll('aside input')[0]?.value), '42');
  assert.ok((await text(page)).includes('NEW CHECK'));

  // A visual calculation becomes a measure-backed saved check with contexts from three different tables.
  // The edited DAX text stays a lab draft; the saved definition pins only the accepted SQL endpoint.
  await page.click('aside > div:first-child button');
  await page.evaluate(() => {
    const state = window.__uishotHarness.state();
    state['input:lab.mode.uishot'] = 'visual';
    state['input:lab.testMeasure.uishot'] = 'measure:Sales/Total Sales';
    state['input:lab.config.uishot'] = {
      rows: [
        { kind: 'column', ref: "'Product'[Category]", name: 'Category', table: 'Product' },
        { kind: 'column', ref: "'Customer'[Region]", name: 'Region', table: 'Customer' },
      ],
      cols: [{ kind: 'column', ref: "'Date'[Year]", name: 'Year', table: 'Date' }],
      values: [{ kind: 'measure', ref: '[Total Sales]', name: 'Total Sales', table: 'Sales' }],
      filters: [],
    };
  });
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab' });
  await waitText(page, 'Save measure check');
  await clickText(page, 'Save measure check ▾');
  assert.equal(await page.evaluate(() => {
    const choice = [...document.querySelectorAll('button')].find((item) => (item.textContent || '').trim().startsWith('Compare with source SQL'));
    choice?.click(); return !!choice;
  }), true, 'source SQL check choice exists');
  await page.waitForSelector('aside select');
  await page.waitForFunction(() => document.querySelector('aside select')?.value === 'measure:Sales/Total Sales');
  await waitText(page, 'Product · Category');
  await waitText(page, 'Customer · Region');
  await waitText(page, 'Date · Year');
  await waitText(page, 'Saved once in Connections, then used by any check or table mapping.');
  if (process.env.CALC_CHECK_REVIEW_DIR) await page.screenshot({ path: join(process.env.CALC_CHECK_REVIEW_DIR, 'dax-to-source-check.png') });
  await page.click('aside textarea');
  await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
  await page.keyboard.type('SELECT Category, Region, CalendarYear, SUM(SalesAmount) AS Value FROM fact.Sales GROUP BY Category, Region, CalendarYear');
  await page.click('aside input[type="radio"]');
  // A compare-with-source check must NAME the SQL source it asks. The drawer refuses to save without one,
  // which is the whole point of sources being saved once instead of typed per check.
  await page.waitForSelector('aside select[aria-label="SQL source"]');
  await page.evaluate(() => {
    const picker = document.querySelector('aside select[aria-label="SQL source"]');
    const real = [...picker.options].find((o) => o.value && o.value !== '__add__');
    const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
    set.call(picker, real.value); picker.dispatchEvent(new Event('change', { bubbles: true }));
  });
  // Tolerance is tuning, so it lives behind Advanced. Open it before typing into it.
  await page.evaluate(() => {
    const box = [...document.querySelectorAll('aside details')].find((d) => (d.querySelector('summary')?.textContent || '').startsWith('Advanced'));
    if (box) box.open = true;
  });
  // The relative box is LABELLED and TYPED in percent (tests-authoring.tsx ToleranceInputs), so 1 here is the 0.01
  // fraction the saved contract is asserted on below. Wait for each box: the drawer re-renders as the radio choice
  // lands, and a click racing that render is what made this step flaky.
  for (const [selector, value] of [['aside input[aria-label="Absolute tolerance"]', '0.5'], ['aside input[aria-label="Relative tolerance in percent"]', '1']]) {
    await page.waitForSelector(selector, { visible: true });
    await page.click(selector);
    await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
    await page.keyboard.type(value);
  }
  assert.equal(await page.evaluate(() => {
    const save = [...document.querySelectorAll('aside button')].find((item) => (item.textContent || '').trim().startsWith('Save'));
    save?.click(); return !!save;
  }), true, 'save check button exists');
  await waitText(page, 'Measure check saved');
  const savedCheck = await page.evaluate(() => window.__uishotHarness.messages.filter((item) => item.type === 'rpc' && item.method === 'saveTest').at(-1)?.params?.[0]);
  const savedParams = JSON.parse(savedCheck.paramsJson);
  assert.equal(savedCheck.targetRef, 'measure:Sales/Total Sales');
  assert.equal(Object.hasOwn(savedCheck, 'query'), false, 'the edited DAX query is not part of the saved contract');
  assert.deepEqual(savedParams.groupBy, ['column:Product/Category', 'column:Customer/Region', 'column:Date/Year']);
  // The saved contract names a SOURCE, not an endpoint. Nothing in the drawer writes server/database any
  // more; they ride through only when an older check already carried them.
  assert.equal(Object.hasOwn(savedParams, 'sqlSourceId'), true, 'the saved check names its SQL source');
  assert.equal(savedParams.toleranceAbsolute, 0.5);
  assert.equal(savedParams.toleranceRelative, 0.01);

  // Run the known mismatch, edit and re-save its source, and keep the old failure visible under a stale banner.
  await clickText(page, 'Open Tests');
  await waitText(page, 'Total Sales ties to source SQL');
  await page.evaluate(() => window.addEventListener('message', (event) => {
    const request = window.__uishotHarness.messages.find((item) => item.type === 'rpc' && item.id === event.data?.id);
    if (event.data?.type === 'rpcResult' && request?.method === 'runTests') window.__lastCheckResult = event.data.result;
  }));
  await clickText(page, 'Run all enabled checks');
  await waitText(page, 'Net Revenue ties to finance extract');
  await page.evaluate(() => document.querySelector('button[aria-label="More for Total Sales ties to source SQL"]')?.click());
  await clickText(page, 'Edit');
  await waitText(page, 'Product · Category');
  assert.equal(await page.$eval('aside textarea', (input) => input.value), 'SELECT Category, Region, CalendarYear, SUM(SalesAmount) AS Value FROM fact.Sales GROUP BY Category, Region, CalendarYear');
  await page.click('aside textarea');
  await page.keyboard.press('End');
  await page.keyboard.type(' ORDER BY Category, Region, CalendarYear');
  assert.equal(await page.evaluate(() => {
    const save = [...document.querySelectorAll('aside button')].find((item) => (item.textContent || '').trim().startsWith('Save'));
    save?.click(); return !!save;
  }), true, 'save edited check button exists');
  await waitText(page, 'results below still show the older check');
  assert.ok((await text(page)).includes('Net Revenue ties to finance extract'),
    'the older evidence stays on screen under the stale banner');
  if (process.env.CALC_CHECK_REVIEW_DIR) {
    await page.evaluate(() => document.querySelector('main')?.scrollTo({ top: 0, behavior: 'auto' }));
    await page.screenshot({ path: join(process.env.CALC_CHECK_REVIEW_DIR, 'tests-stale-banner.png') });
    await page.evaluate(() => [...document.querySelectorAll('span')].find((node) => (node.textContent || '').trim() === 'Net Revenue ties to finance extract')?.scrollIntoView({ block: 'center' }));
    await page.screenshot({ path: join(process.env.CALC_CHECK_REVIEW_DIR, 'tests-stale-mismatch.png') });
  }

  // An edit during an in-flight run cannot make the returned old result appear current.
  await page.evaluate(() => window.__uishotHarness.setResponse('runTests', '__uishotNever'));
  await clickText(page, 'Run all enabled checks');
  await dispatch(page, { type: 'didChange', payload: { sessionId: 'uishot', revision: 8, origin: 'agent', label: 'Measure changed during test', deltas: [] } });
  await waitText(page, 'The model changed. These results may be out of date');
  await page.evaluate(() => {
    const request = window.__uishotHarness.messages.filter((item) => item.type === 'rpc' && item.method === 'runTests').at(-1);
    window.__uishotHarness.dispatch({ type: 'rpcResult', id: request.id, result: window.__lastCheckResult });
  });
  await delay(100);
  assert.ok((await text(page)).includes('The model changed. These results may be out of date'), 'late result keeps the stale evidence notice');

  // A model swap clears route/object seeds. Delayed old-session selection and navigation cannot affect model B.
  const sessionB = { sessionId: 'session-b', modelName: 'Second Model', revision: 1, tables: 11, measures: 12, source: 'C:/Models/Second.Model', liveBound: true, liveDatabase: 'Second Model' };
  await page.evaluate((session) => window.__uishotHarness.setResponse('sessionInfo', session), sessionB);
  await dispatch(page, { type: 'reconnected' });
  await waitText(page, 'Second Model');
  await page.waitForSelector('.overview-row');
  assert.equal(await page.$('.object-actions'), null);
  await dispatch(page, { type: 'treeSelection', sessionId: 'uishot', ref: 'measure:Sales/Total Sales' });
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab', target: 'measure:Sales/Total Sales' });
  await delay(100);
  assert.equal(await activeArea(page), 'Model');
  assert.equal(await page.$('.object-actions'), null);
  await dispatch(page, { type: 'treeSelection', sessionId: 'session-b', ref: 'measure:Sales/Total Sales' });
  await waitText(page, 'Try calculation');

  // The object hand-off still opens the complete tool. What it no longer leaves behind is a Back button: Kane retired
  // that on 2026-09-14 after hitting it himself ("The back button doesnt do anything"). Its replacement is narrower on
  // purpose and lives further down this file — one Done, on Settings pages only.
  await clickText(page, 'Try calculation', '.object-actions');
  await waitText(page, 'Calculations');
  assert.equal(await page.$('.studio-arearow .studio-back'), null, 'a page reached by a hand-off carries no Back button');
  assert.doesNotMatch(await page.$eval('.studio-arearow', (node) => node.innerText), /Back/, 'and no Back text in the area row');

  // The legacy Data Agent route keeps its Advanced subview, and Publish opens review from that mounted subview.
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'dataagent' });
  await page.waitForFunction(() => document.body.innerText.includes('Data Agent') && document.body.innerText.includes('Delivery tools'));
  assert.equal(await activeArea(page), 'Changes');
  await page.click('.studio-publish');
  await page.waitForSelector('[data-testid="publish-confirm"]');

  // The header fold is MEASURED, so at this 1440-wide viewport the utilities belong on the header itself and More
  // must be folded away. The old fixed 1500px container query hid them here, which was the defect.
  const readHeader = () => page.evaluate(() => {
    const header = document.querySelector('.studio-chrome');
    const shown = (label) => [...header.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === label && b.getClientRects().length > 0);
    return {
      fold: header.dataset.fold, findATool: shown('Find a tool'), connections: shown('Connections'), more: shown('More'),
      // The gear left the header on 2026-09-14. Assistant permissions live in the Your assistant menu now, so what
      // this reads is that no gear came back, and that the chip which replaced it is on the header at this width.
      gear: header.querySelector('.studio-settings') !== null,
      assistant: shown('Your assistant'),
      areas: [...header.querySelectorAll('nav[aria-label="Studio areas"] button')].filter((b) => b.getClientRects().length > 0).length,
    };
  });
  assert.deepEqual(await readHeader(), { fold: '0', findATool: true, connections: true, more: false, gear: false, assistant: true, areas: 5 });

  // Narrow the window and the utilities fold into More, while the five areas stay put.
  await page.setViewport({ width: 1000, height: 800, deviceScaleFactor: 1 });
  await page.waitForFunction(() => document.querySelector('.studio-chrome')?.dataset.fold !== '0', { timeout: 10000 });
  const narrow = await readHeader();
  assert.equal(narrow.more, true, 'utilities fold into More on a narrow header');
  assert.equal(narrow.findATool, false, 'Find a tool leaves the header when it no longer fits');
  assert.equal(narrow.areas, 5, 'the five areas fold LAST and are still on a 1000-wide header');

  // Find a tool chooses the complete existing tool. Changes and Workflows retain primary-area highlighting.
  await clickText(page, 'More');
  await clickText(page, 'Find a tool');
  await page.click('[data-tool="docs"]');
  await page.waitForSelector('iframe[title="Documentation preview"]');
  assert.equal(await activeArea(page), 'Model');
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'history' });
  assert.equal(await activeArea(page), 'Changes');
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'workflows' });
  assert.equal(await activeArea(page), 'Workflows');

  // A current-session model change refreshes Model home instead of leaving a same-position stale graph.
  const changedGraph = { tables: [{ ref: 'table:Only', name: 'Only', isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 2, measures: 1, keyColumns: [], hasDescription: true }], relationships: [] };
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'modelhome' });
  await page.evaluate((graph) => window.__uishotHarness.setResponse('getModelGraph', graph), changedGraph);
  await dispatch(page, { type: 'didChange', payload: { sessionId: 'session-b', revision: 2, origin: 'human', label: 'edit', deltas: [] } });
  await page.waitForFunction(() => document.querySelectorAll('.overview-row').length === 1 && document.body.innerText.includes('Only'));
  assert.deepEqual(errors, []);
  await page.close();

  // --- the Your assistant chip, driven from the keyboard alone, focus probed at every step ------------------
  // Astra's finding 4: opening Live activity by keyboard dropped focus to BODY, because the pick focused the menu
  // that was about to unmount. Escape then went nowhere and the feed stayed open behind an expanded chip. Source
  // patterns cannot see that, so this is a real keyboard journey and every step asserts document.activeElement.
  {
    const kb = await openPage('live=1&act=1');
    const page = kb.page;
    await delay(600);
    const focus = () => page.evaluate(() => {
      const a = document.activeElement;
      if (!a || a === document.body) return 'BODY';
      const label = (a.textContent || a.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim();
      return `${a.getAttribute('role') || a.tagName.toLowerCase()}:${label.slice(0, 28)}`;
    });
    const surface = () => page.evaluate(() => {
      const chip = [...document.querySelectorAll('header [aria-haspopup="menu"]')].find((b) => (b.textContent || '').includes('Your assistant'));
      const panes = [...document.querySelectorAll('header .absolute')].filter((e) => e.getClientRects().length)
        .map((e) => (e.textContent || '').replace(/\s+/g, ' ').trim());
      return { expanded: chip?.getAttribute('aria-expanded'),
        menu: [...document.querySelectorAll('[role="menu"]')].some((m) => m.getAttribute('aria-label') === 'Your assistant' && m.getClientRects().length > 0),
        feed: panes.some((t) => t.startsWith('Live activity · your assistant')) };
    });
    const focusChip = () => page.evaluate(() => {
      const b = [...document.querySelectorAll('header [aria-haspopup="menu"]')].find((x) => (x.textContent || '').includes('Your assistant'));
      if (b) b.focus(); return !!b;
    });

    assert.equal(await focusChip(), true, 'the Your assistant chip is on the header and focusable');
    await page.keyboard.press('ArrowDown'); await delay(250);
    assert.deepEqual(await surface(), { expanded: 'true', menu: true, feed: false }, 'ArrowDown opens the chip menu');
    // Escape from the menu returns focus to the chip.
    await page.keyboard.press('Escape'); await delay(250);
    assert.match(await focus(), /Your assistant/, 'Escape from the menu lands back on the chip');
    assert.deepEqual(await surface(), { expanded: 'false', menu: false, feed: false }, 'Escape closes the menu');

    // Space opens too, and Enter on Live activity must move focus INTO the feed, not drop it on the document.
    await page.keyboard.press(' '); await delay(250);
    assert.equal((await surface()).menu, true, 'Space opens the chip menu');
    await page.keyboard.press('ArrowDown'); await delay(200);
    assert.match(await focus(), /Live activity/, 'ArrowDown moves the ring onto Live activity');
    await page.keyboard.press('Enter'); await delay(350);
    assert.deepEqual(await surface(), { expanded: 'true', menu: false, feed: true }, 'Enter opens the live feed one level in');
    assert.notEqual(await focus(), 'BODY', 'opening the feed by keyboard must not drop focus on the document');

    // Escape from INSIDE the feed dismisses the whole popover and returns the ring to the chip.
    await page.keyboard.press('Escape'); await delay(350);
    assert.deepEqual(await surface(), { expanded: 'false', menu: false, feed: false }, 'Escape from the feed closes the popover');
    assert.match(await focus(), /Your assistant/, 'Escape from the feed returns focus to the chip');

    // Back walks out of the feed to the menu with the ring still on Live activity.
    await page.keyboard.press('Enter'); await delay(250);
    await page.keyboard.press('ArrowDown'); await delay(200);
    await page.keyboard.press('Enter'); await delay(350);
    assert.equal((await surface()).feed, true, 'the feed is open again');
    await clickText(page, 'Back', 'header');
    await delay(300);
    assert.deepEqual(await surface(), { expanded: 'true', menu: true, feed: false }, 'Back returns to the chip menu');
    assert.match(await focus(), /Live activity/, 'Back puts the ring back on the row you came from');

    // Tab dismisses the menu to its chip rather than stranding focus on an unmounting row.
    await page.keyboard.press('Tab'); await delay(300);
    assert.deepEqual(await surface(), { expanded: 'false', menu: false, feed: false }, 'Tab dismisses the menu');
    assert.match(await focus(), /Your assistant/, 'Tab leaves focus on the chip, never on the document');

    assert.deepEqual(kb.errors, []);
    await page.close();
  }

  // --- the compact top (contract v1.5) --------------------------------------------------------------------
  // Three rows above every page, the same everywhere, and a text size that survives a canvas. Source patterns
  // cannot prove any of this: it is geometry, a real dialog, and a real wheel event.
  {
    const compact = await openPage('live=1&conn=1');
    const shell = compact.page;
    await shell.waitForSelector('.overview-row');
    const rowHeights = () => shell.evaluate(() => {
      const h = (s) => { const el = document.querySelector(s); return el ? Math.round(el.getBoundingClientRect().height) : null; };
      return { header: h('header.studio-chrome'), arearow: h('.studio-arearow'), contextbar: h('.studio-contextbar'),
        breadcrumb: document.querySelector('.studio-breadcrumb') === null ? 'gone' : 'still here',
        guide: document.querySelector('aside[aria-label="Page guidance"]') === null ? 'gone' : 'still here' };
    });
    assert.deepEqual(await rowHeights(), { header: 40, arearow: 40, contextbar: 34, breadcrumb: 'gone', guide: 'gone' },
      'two 40px rows above the page and a 34px bar under it, with no breadcrumb row and no open guide');

    // Back is gone from every area page. This block used to assert the opposite — that it appeared the moment you
    // left the landing page — and that is exactly the behaviour Kane rejected: a control whose only promise is
    // "somewhere", which on a cold open it could not even keep. The row now goes straight from the area name to the
    // segment strip, and Done (the Settings block below) is the only way-out control in Studio.
    const backShown = () => shell.evaluate(() => !!document.querySelector('.studio-arearow .studio-back'));
    const backText = () => shell.evaluate(() => /Back/.test(document.querySelector('.studio-arearow')?.innerText ?? ''));
    const doneShown = () => shell.evaluate(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'));
    assert.equal(await backShown(), false, 'no Back on the landing page');
    assert.equal(await backText(), false, 'and no Back text on the landing page');
    assert.equal(await doneShown(), false, 'and no Done: Done is a Settings control, not an every-page control');
    await clickText(shell, 'Diagram', '.studio-arearow');
    await shell.waitForFunction(() => !!document.querySelector('.react-flow'), { timeout: 10000 });
    assert.equal(await backShown(), false, 'and none once you have navigated');
    assert.equal(await backText(), false, 'and still no Back text');
    assert.equal(await doneShown(), false, 'and still no Done');

    // The ⓘ opens the page notes as a real dialog, and Escape closes it and gives focus back.
    await shell.click('.studio-page-notes');
    await shell.waitForSelector('[role="dialog"][aria-label="Page notes"]');
    const notes = await shell.$eval('[role="dialog"][aria-label="Page notes"]', (n) => n.innerText);
    // innerText is what a person actually READS, so this also catches a CSS transform rewording the heading.
    // The whole guide moves verbatim (Kane, 2026-09-14): lead sentence, heading, steps, closing line.
    assert.ok(notes.startsWith('See how your tables connect. Arrange the map and create or edit the links between tables.'),
      'the page sentence is the guide lead, word for word');
    assert.ok(notes.includes('Getting started'), 'the heading still reads Getting started, not GETTING STARTED');
    assert.ok(notes.includes('Open a model, then expand a table to see its columns.'),
      'the numbered steps are the guide steps, word for word');
    assert.ok(notes.includes('Open Help above for more detail, related tasks and explanations of common terms.'),
      'and the closing line is the guide closing line');
    await shell.keyboard.press('Escape');
    await shell.waitForFunction(() => document.querySelector('[role="dialog"][aria-label="Page notes"]') === null);
    assert.equal(await shell.evaluate(() => document.activeElement?.className || ''), 'studio-page-notes',
      'Escape returns focus to the ⓘ it came from');

    // Ctrl+wheel scales Studio, steps by 10, and clamps. The wheel is dispatched on a real element in the page.
    const zoom = () => shell.evaluate(() => getComputedStyle(document.querySelector('.studio-root')).zoom);
    const wheelOn = (selector, deltaY) => shell.evaluate((sel, dy) => {
      const el = document.querySelector(sel);
      el.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, bubbles: true, cancelable: true }));
    }, selector, deltaY);
    await wheelOn('main', -1);
    await shell.waitForFunction(() => getComputedStyle(document.querySelector('.studio-root')).zoom !== '1');
    assert.equal(await zoom(), '1.1', 'one notch up is 110%');
    assert.ok((await shell.evaluate(() => document.querySelector('.studio-zoom-toast')?.textContent)) === 'Studio at 110%',
      'and it says so, once, in plain words');
    for (let i = 0; i < 6; i++) await wheelOn('main', -1);
    assert.equal(await zoom(), '1.3', 'it clamps at 130%');
    for (let i = 0; i < 12; i++) await wheelOn('main', 1);
    assert.equal(await zoom(), '0.8', 'and at 80%');
    await shell.keyboard.down('Control'); await shell.keyboard.press('0'); await shell.keyboard.up('Control');
    await shell.waitForFunction(() => getComputedStyle(document.querySelector('.studio-root')).zoom === '1');

    // Inside a canvas the event is left alone, so React Flow keeps zooming itself as it always has.
    const canvas = await shell.$('.react-flow');
    assert.ok(canvas, 'the Diagram canvas is mounted for this check');
    await wheelOn('.react-flow', -1);
    await delay(150);
    assert.equal(await zoom(), '1', 'Ctrl+wheel over the canvas must not resize Studio');

    assert.deepEqual(compact.errors, []);
    await shell.close();
  }

  // --- Settings has exactly one way out, and it always works (Kane, 2026-09-14) ---------------------------------
  // Back was removed because it could be OFFERED and then do nothing. Done is the narrow replacement: one button,
  // Settings pages only, returning to the one page that sent you there. Every probe here is behavioural, because the
  // trap that broke Back was an ordering trap and no source pattern can see ordering.
  {
    const openAssistantMenu = async (page) => {
      const opened = await page.evaluate(() => {
        const chip = [...document.querySelectorAll('header button[aria-haspopup="menu"]')]
          .find((b) => (b.getAttribute('title') || '').startsWith('Your assistant'));
        if (chip) chip.click();
        return !!chip;
      });
      assert.equal(opened, true, 'the assistant chip is on the header');
      await page.waitForSelector('[role="menu"][aria-label="Your assistant"]');
    };
    const settingsRow = (page) => page.evaluate(() => document.querySelector('.studio-arearow')?.innerText.replace(/\s+/g, ' ').trim());
    const doneAt = (page) => page.evaluate(() => {
      const done = document.querySelector('.studio-arearow [data-testid="settings-done"]');
      const notes = document.querySelector('.studio-arearow .studio-page-notes');
      if (!done || !notes) return null;
      const d = done.getBoundingClientRect(); const n = notes.getBoundingClientRect();
      const row = document.querySelector('.studio-arearow').getBoundingClientRect();
      return { height: Math.round(d.height), beforeTheNotes: d.right <= n.left + 1,
        // Adjacent to the ⓘ, not stranded in mid-row: the gap between them is the row's own 8px, and the pair sits
        // at the right-hand end. Both halves are needed — the first build put Done exactly halfway, which satisfied
        // "before the notes" while looking nothing like the design.
        nextToTheNotes: Math.round(n.left - d.right) <= 10, nearTheRightEdge: row.right - n.right < 40 };
    });

    // The Lineage landmark is a control that is ON the page, not a sub-tab name: "Published reports" was a
    // sub-tab until Impact became one page, and a landmark that can be retired makes this journey fail for a
    // reason that has nothing to do with the shell.
    // 1. WARM. Lineage, then Settings, then Done lands back on Lineage — not on Overview, which is what a fallback
    //    would give and what would make the button look almost right while being wrong.
    const warm = await openPage('live=1');
    await warm.page.waitForSelector('.overview-row');
    await clickText(warm.page, 'Lineage', '.studio-arearow');
    await waitText(warm.page, 'Impact');
    await openAssistantMenu(warm.page);
    await clickText(warm.page, 'Permissions', '[role="menu"][aria-label="Your assistant"]');
    await warm.page.waitForFunction(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'), { timeout: 10000 });
    assert.match(await settingsRow(warm.page), /^Settings › Assistant permissions/, 'the Settings row names the page you are on');
    assert.doesNotMatch(await settingsRow(warm.page), /← Back/, 'and offers no Back');
    // The shared dense control step is 24px (styles.css --sem-control-h-sm), and Done sits at the right-hand end of
    // the row, ahead of the ⓘ. Measured, because "far right, before the i" is geometry.
    assert.deepEqual(await doneAt(warm.page), { height: 24, beforeTheNotes: true, nextToTheNotes: true, nearTheRightEdge: true },
      'Done is on the shared 24px step, at the right-hand end of the row, immediately ahead of the page notes');
    await clickText(warm.page, 'Done', '.studio-arearow');
    await waitText(warm.page, 'Impact');
    assert.equal(await warm.page.$('.studio-arearow [data-testid="settings-done"]'), null, 'and Done is gone once you are out of Settings');
    assert.deepEqual(warm.errors, []);

    // 2. ESCAPE does the same thing, with focus on the page rather than in a field.
    await openAssistantMenu(warm.page);
    await clickText(warm.page, 'Permissions', '[role="menu"][aria-label="Your assistant"]');
    await warm.page.waitForFunction(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'), { timeout: 10000 });
    await warm.page.evaluate(() => document.querySelector('main')?.click());
    await warm.page.keyboard.press('Escape');
    await waitText(warm.page, 'Impact');
    assert.equal(await warm.page.$('.studio-arearow [data-testid="settings-done"]'), null, 'Escape on a Settings page is Done');
    assert.deepEqual(warm.errors, []);
    await warm.page.close();

    // 3. COLD, and this is the one that matters. The host queues a navigation while Studio is still mounting and
    //    flushes it on studioReady, which can beat the webview's own sessionInfo call home (harness.html's
    //    ?sessionDelay / ?coldNav reproduce exactly that ordering). The page recorded as "where you came from" is
    //    then stamped with an EMPTY session id. goRoute drops any route whose session is not the live one, so
    //    without the restamp in goDone this Done would be a click that does nothing — the precise defect that made
    //    the old Back inert. Proven here by driving it, not by reading the code.
    const cold = await openPage('live=1&sessionDelay=1200&coldNav=permissions');
    await cold.page.waitForFunction(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'), { timeout: 10000 });
    assert.match(await settingsRow(cold.page), /^Settings › Assistant permissions/, 'the cold hand-off lands on Settings before the session is known');
    await cold.page.waitForFunction(() => document.body.innerText.includes('Contoso (mock)'), { timeout: 10000 });
    assert.match(await settingsRow(cold.page), /^Settings › Assistant permissions/, 'and stays there once the session arrives');
    await clickText(cold.page, 'Done', '.studio-arearow');
    await cold.page.waitForSelector('.overview-row', { timeout: 10000 });
    assert.equal(await cold.page.$('.studio-arearow [data-testid="settings-done"]'), null,
      'Done leaves Settings even though the page it returns to was recorded before sessionInfo answered');
    assert.deepEqual(cold.errors, []);
    await cold.page.close();

    // 4. A real model swap forgets the page you came from, so Done falls back to Model > Overview rather than
    //    returning you into the model you no longer have open.
    const swap = await openPage('live=1');
    await swap.page.waitForSelector('.overview-row');
    await clickText(swap.page, 'Lineage', '.studio-arearow');
    await waitText(swap.page, 'Impact');
    await openAssistantMenu(swap.page);
    await clickText(swap.page, 'Permissions', '[role="menu"][aria-label="Your assistant"]');
    await swap.page.waitForFunction(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'), { timeout: 10000 });
    await swap.page.evaluate(() => window.__uishotHarness.setResponse('sessionInfo', {
      sessionId: 'session-c', modelName: 'Third Model', revision: 1, tables: 11, measures: 12, source: 'C:/Models/Third.Model' }));
    await dispatch(swap.page, { type: 'reconnected' });
    await swap.page.waitForSelector('.overview-row', { timeout: 10000 });
    await waitText(swap.page, 'Third Model');
    assert.deepEqual(swap.errors, []);
    await swap.page.close();
  }

  // --- P1: an object in hand may never take the navigation away (Kane, on the Yoga, 2026-09-15) ----------
  // "went to data preview from overview and no way back, it hides all navigation buttons under model." Overview >
  // Preview data hands Data the table as the route object. The strip rule hid itself whenever a route carried an
  // object, Back had already been retired, and the Model button restored the remembered page, which was that same
  // page. Two exits are proven here: the strip itself, and the area button for the area you are already in. The
  // object is NOT on the row in any form, which this block also holds: the page names what it was opened with.
  // Every assertion here was watched to FAIL against 6b9b9b36 first.
  {
    const stuck = await openPage('live=1');
    const p = stuck.page;
    await p.waitForSelector('.overview-row');
    const readRow = () => p.evaluate(() => {
      const bar = document.querySelector('.studio-arearow');
      const segments = [...bar.querySelectorAll('.area-tabs button')];
      return {
        strip: segments.length > 0,
        segments: segments.map((b) => b.textContent.replace(/\s+/g, ' ').trim()),
        // Tab paints the open segment with an inline accent-soft background, and carries no other marker.
        active: segments.find((b) => b.style.background === 'var(--sem-accent-soft)')?.textContent.replace(/\s+/g, ' ').trim() ?? null,
        objectMarkup: bar.querySelectorAll('.studio-arearow-object, [data-testid="arearow-clear-object"]').length,
        tool: bar.querySelector('.studio-arearow-tool')?.textContent.trim() ?? null,
        height: Math.round(bar.getBoundingClientRect().height),
        text: bar.innerText.replace(/\s+/g, ' ').trim(),
      };
    });
    // Kane's own route in: the chevron on an Overview table row IS "Preview data".
    const previewTable = async (name) => {
      const clicked = await p.evaluate((label) => {
        const row = [...document.querySelectorAll('.overview-row')].find((item) => (item.innerText || '').includes(label));
        const go = row && row.querySelector('.overview-go');
        if (go) go.click();
        return !!go;
      }, name);
      assert.equal(clicked, true, `the ${name} row offers Preview data`);
      // The sidebar header is CSS-uppercased, so innerText reads "TABLES (11)": match it without regard to case.
      await p.waitForFunction(() => /tables \(\d+\)/i.test(document.body.innerText), { timeout: 15000 });
    };

    await previewTable('Promotion');
    const stranded = await readRow();
    assert.equal(stranded.strip, true, `Data reached with a table in hand keeps the Model strip (row read "${stranded.text}")`);
    assert.equal(stranded.active, 'Data', 'and Data is the segment marked as open');
    assert.ok(stranded.segments.includes('Overview'), 'so Overview is one click away on the row itself');
    assert.equal(stranded.tool, null, 'the tool name is not repeated: the open segment already says it');
    assert.equal(stranded.objectMarkup, 0, 'and the table is not on the row at all, as a crumb or as a chip');
    assert.doesNotMatch(stranded.text, /Promotion/, `nothing on the row names the table (row read "${stranded.text}")`);
    // The page is what names it, which is why the row does not have to.
    assert.ok((await text(p)).includes('Preview · Promotion'), 'the PAGE says which table you are looking at');
    assert.equal(stranded.height, 40, 'the row is still one compact 40px line');

    // Exit one: the strip itself. It is the thing that was missing.
    await clickText(p, 'Overview', '.studio-arearow');
    await p.waitForSelector('.overview-row', { timeout: 10000 });
    assert.equal((await readRow()).active, 'Overview', 'a segment on the strip takes you off the page');

    // Exit two: the area you are ALREADY in goes to its home page, never back to the page you are looking at.
    // Walk into the trap again first, the way Kane walked into it.
    await previewTable('Promotion');
    assert.equal((await readRow()).active, 'Data', 'the hand-off is live again');
    await clickText(p, 'Model', 'nav[aria-label="Studio areas"]');
    await p.waitForSelector('.overview-row', { timeout: 10000 });
    assert.equal((await readRow()).active, 'Overview', 'Model, pressed while you are already in Model, lands on Overview');

    // The same exit on a multi-tool area that is not Model, so the rule is not read off one area's home.
    await clickText(p, 'Checks', 'nav[aria-label="Studio areas"]');
    await p.waitForFunction(() => document.querySelectorAll('.studio-arearow .area-tabs button').length > 0, { timeout: 10000 });
    await clickText(p, 'Model quality', '.studio-arearow');
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.style.background === 'var(--sem-accent-soft)' && b.getAttribute('data-tab') === 'bpa'), { timeout: 10000 });
    await clickText(p, 'Checks', 'nav[aria-label="Studio areas"]');
    // Matched on data-tab, not on the words. From 2026-09-15 a locked segment wears a Pro pill inside the
    // button, so this page (a FREE one) shows "TestsPro" and a text-equality match never fires. The stable id
    // is what a check like this should always have used; readRow below still reads the label, so the pill is
    // allowed for in the assertion instead of being matched against.
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.style.background === 'var(--sem-accent-soft)' && b.getAttribute('data-tab') === 'tests'), { timeout: 10000 });
    assert.match((await readRow()).active, /^Tests(Pro)?$/, 'Checks, pressed while you are in Checks, lands on Tests, not on the page you were reading');

    // The same trap on an area with ONE tool. There is no strip to show, so the row names the tool, and the
    // measure it was opened with is still not on the row: the lab's own wells carry that.
    await dispatch(p, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab', target: 'measure:Sales/Total Sales' });
    await p.waitForSelector('.cm-content', { timeout: 15000 });
    assert.equal(await activeArea(p), 'Calculations');
    const lab = await readRow();
    assert.equal(lab.strip, false, 'Calculations has one tool, so it has no strip');
    assert.equal(lab.tool, 'DAX Lab', 'so the row names the tool itself');
    assert.equal(lab.objectMarkup, 0, 'and still carries no object markup');
    assert.doesNotMatch(lab.text, /Total Sales/, `the measure is not on the row (row read "${lab.text}")`);
    assert.equal(lab.height, 40, 'still one 40px line');
    await p.close();
  }

  // --- Studio zoom and the keyboard: Astra's third spot review, 2026-09-14 -------------------------------
  // Four defects that only a real browser at a real zoom can see. Every one of these assertions was watched to
  // FAIL against 41c0b397 before the fix existed, with the measured numbers recorded in the lane report.
  {
    const spot = await openPage('live=1&conn=1&tier=pro&dirty=1&act=1');
    const p = spot.page;
    await p.waitForSelector('.overview-row');
    const rootZoom = () => p.evaluate(() => getComputedStyle(document.querySelector('.studio-root')).zoom);
    // The wheel is dispatched on a real element, the same way the compact block above does it, so the window
    // listener sees exactly what a Ctrl+wheel gives it.
    const wheelOn = (selector, deltaY, times = 1) => p.evaluate((sel, dy, n) => {
      const el = document.querySelector(sel);
      for (let i = 0; i < n; i++) el.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, bubbles: true, cancelable: true }));
    }, selector, deltaY, times);
    // The fold is measured by a ResizeObserver, one pass per animation frame, so data-fold lands a frame or two
    // AFTER the zoom itself. Waiting only for the root's zoom style reads a header that has not refolded yet
    // (measured: 1000px stepped 130 -> 100 -> 80 read fold 2, and 0 a frame later). So settle on the header's
    // own width agreeing with the new zoom before anything reads data-fold.
    const settleHeader = async (percent) => {
      await p.waitForFunction((factor) => {
        const h = document.querySelector('header.studio-chrome');
        return h && Math.abs(h.clientWidth - Math.round(window.innerWidth / factor)) <= 2;
      }, { timeout: 10000 }, percent / 100);
      await delay(250);
    };
    const setZoom = async (percent) => {
      await p.keyboard.down('Control'); await p.keyboard.press('0'); await p.keyboard.up('Control');
      await p.waitForFunction(() => getComputedStyle(document.querySelector('.studio-root')).zoom === '1');
      if (percent === 100) { await settleHeader(100); return; }
      await wheelOn('header.studio-chrome', percent > 100 ? -1 : 1, Math.abs(percent - 100) / 10);
      await p.waitForFunction((want) => getComputedStyle(document.querySelector('.studio-root')).zoom === want,
        { timeout: 10000 }, String(percent / 100));
      await settleHeader(percent);
    };

    // 1. Every navigation target a person can SEE on the header has its own usable hit area. Astra measured
    // Workflows at x=482.7-581.8 with the actions starting at x=479.6 at 1000px/130%: its centre opened More.
    // This walks the header rather than naming Workflows, so the next crowded control is caught too.
    const headerTargets = () => p.evaluate(() => {
      const header = document.querySelector('header.studio-chrome');
      const seen = [...header.querySelectorAll('button, a[href]')].filter((e) => e.getClientRects().length > 0);
      return {
        fold: header.dataset.fold, clientWidth: header.clientWidth, innerWidth: window.innerWidth,
        targets: seen.map((e) => {
          const b = e.getBoundingClientRect();
          const hit = document.elementFromPoint(b.x + b.width / 2, b.y + b.height / 2);
          return {
            label: (e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 24) || e.className,
            w: Math.round(b.width), h: Math.round(b.height),
            own: !!hit && (e === hit || e.contains(hit)),
          };
        }),
      };
    });
    const headerIsUsable = async (where) => {
      const read = await headerTargets();
      const stolen = read.targets.filter((t) => !t.own).map((t) => t.label);
      assert.deepEqual(stolen, [], `${where}: every visible header control owns its own centre (fold ${read.fold}, header ${read.clientWidth}px)`);
      // 12 screen px, not 16. The smallest thing on the header is the approval-count badge pinned to the Your
      // assistant chip: 16x16 at 100%, so 13x13 once Studio is at 80%. That is by design (the whole chip is the
      // primary target and the menu behind it spells the count out in words), so the floor is set where it
      // catches a control being SQUEEZED rather than one being deliberately small.
      const tiny = read.targets.filter((t) => t.w < 12 || t.h < 12).map((t) => `${t.label} ${t.w}x${t.h}`);
      assert.deepEqual(tiny, [], `${where}: no header control is squeezed below a usable hit area`);
      return read;
    };

    for (const width of [1440, 1280, 1000, 769]) {
      await p.setViewport({ width, height: 800, deviceScaleFactor: 1 });
      await delay(400);
      await headerIsUsable(`${width}px at 100%`);
    }
    await p.setViewport({ width: 1000, height: 768, deviceScaleFactor: 1 });
    await setZoom(130);
    const crowded = await headerIsUsable('1000px at 130%');
    assert.equal(crowded.fold, '4', 'level 3 cannot cover the overflow at a 769px effective header, so the areas fold too');
    // The label above is read from aria-label first, so the More button reads "More Studio tools" here.
    assert.ok(crowded.targets.some((t) => t.label.startsWith('More')), 'the areas are still reachable, through More');
    await setZoom(80);
    const roomy = await headerIsUsable('1000px at 80%');
    assert.equal(roomy.fold, '0', 'at 80% the same window has room for everything again');

    // The areas nav never paints outside its own box, whatever the fold level decides.
    assert.equal(await p.evaluate(() => getComputedStyle(document.querySelector('.studio-groups')).overflow), 'hidden',
      'the areas nav clips its own buttons rather than painting them over the actions');

    // 2. The shared tool menu is placed in ONE coordinate space. Under CSS zoom a measured screen rect written
    // straight into a fixed left/top is multiplied by the zoom a second time: Astra measured the Lineage Tree
    // Show menu ending at x=1065.7 in a 1000px viewport, 46.7px below its button instead of beside it.
    await setZoom(100);
    await clickText(p, 'Lineage', '.studio-arearow');
    await p.waitForSelector('.sem-toolrow');
    const openShowMenu = async () => {
      await p.evaluate(() => [...document.querySelectorAll('.sem-toolrow button')].find((b) => (b.innerText || '').includes('Show'))?.click());
      await p.waitForSelector('.sem-rowmenu-pop');
      await delay(150);
      return p.evaluate(() => {
        const pop = document.querySelector('.sem-rowmenu-pop').getBoundingClientRect();
        const btn = [...document.querySelectorAll('.sem-toolrow button')].find((b) => (b.innerText || '').includes('Show')).getBoundingClientRect();
        const factor = parseFloat(getComputedStyle(document.querySelector('.studio-root')).zoom) || 1;
        return { factor, innerWidth: window.innerWidth, left: +pop.left.toFixed(1), right: +pop.right.toFixed(1),
          gap: +(pop.top - btn.bottom).toFixed(1), overBtnLeft: +(pop.left - btn.left).toFixed(1) };
      });
    };
    for (const width of [1000, 1366]) {
      await p.setViewport({ width, height: 768, deviceScaleFactor: 1 });
      for (const percent of [80, 100, 130]) {
        await setZoom(percent);
        const menu = await openShowMenu();
        const where = `${width}px at ${percent}%`;
        assert.ok(menu.right <= menu.innerWidth - 4, `${where}: the whole menu stays inside the viewport (right ${menu.right} of ${menu.innerWidth})`);
        assert.ok(menu.left >= 4, `${where}: and inside its left edge (left ${menu.left})`);
        // 4 CSS px under the button, whatever the zoom multiplies that into on screen.
        assert.ok(menu.gap > 1 && menu.gap < 4 * menu.factor + 4,
          `${where}: the menu sits directly under its trigger (gap ${menu.gap} at factor ${menu.factor})`);
        await p.keyboard.press('Escape');
        await p.waitForFunction(() => document.querySelector('.sem-rowmenu-pop') === null);
      }
    }
    await setZoom(100);
    await p.setViewport({ width: 1366, height: 768, deviceScaleFactor: 1 });

    // 3. Page notes and Live activity close when focus leaves them, and Escape works for their whole open life.
    // Astra: Space, Tab, Escape left the notes over the canvas with focus in the tool behind them.
    const notesState = () => p.evaluate(() => {
      const a = document.activeElement;
      const pop = document.querySelector('.studio-page-notes-pop');
      return { open: !!pop, insidePop: !!pop && pop.contains(a),
        focus: (a && a !== document.body ? (a.getAttribute('role') || a.className || a.tagName.toLowerCase()) : 'BODY') };
    });
    for (const tool of ['Overview', 'Diagram', 'Lineage']) {
      await clickText(p, tool, '.studio-arearow');
      await delay(400);
      await p.evaluate(() => document.querySelector('.studio-page-notes').focus());
      await p.keyboard.press('Space'); await delay(300);
      assert.equal((await notesState()).open, true, `${tool}: Space opens the page notes`);
      // Tab STAYS in the dialog. Nothing behind the notes may take the ring while they are over it.
      await p.keyboard.press('Tab'); await delay(250);
      const tabbed = await notesState();
      assert.equal(tabbed.open, true, `${tool}: Tab does not close the notes`);
      assert.equal(tabbed.insidePop, true, `${tool}: Tab keeps the ring inside the notes dialog`);
      await p.keyboard.press('Escape'); await delay(300);
      assert.equal((await notesState()).open, false, `${tool}: Escape still closes the notes after a Tab`);
      assert.equal(await p.evaluate(() => document.activeElement?.className || ''), 'studio-page-notes',
        `${tool}: and hands the ring back to the ⓘ`);
      // Shift+Tab out of the dialog is the same contract from the other direction.
      await p.keyboard.press('Space'); await delay(300);
      await p.keyboard.down('Shift'); await p.keyboard.press('Tab'); await p.keyboard.up('Shift'); await delay(250);
      const back = await notesState();
      assert.equal(back.insidePop, true, `${tool}: Shift+Tab keeps the ring inside the notes dialog too`);
      await p.keyboard.press('Escape'); await delay(300);
      assert.equal((await notesState()).open, false, `${tool}: Escape closes after a Shift+Tab as well`);
      // Focus taken by something else entirely dismisses the notes rather than stranding them.
      await p.keyboard.press('Space'); await delay(300);
      assert.equal((await notesState()).open, true, `${tool}: reopened for the focusout check`);
      await p.evaluate(() => document.querySelector('.studio-page-notes-wrap').parentElement.querySelector('button')?.focus());
      await p.evaluate(() => [...document.querySelectorAll('.sem-toolrow button, main button')].find((b) => b.getClientRects().length)?.focus());
      await delay(250);
      assert.equal((await notesState()).open, false, `${tool}: the notes close when focus lands outside them`);
    }

    // The feed keeps native Tab through its entries, and closes the moment focus leaves the surface.
    const feedState = () => p.evaluate(() => {
      const chip = [...document.querySelectorAll('header [aria-haspopup="menu"]')].find((b) => (b.textContent || '').includes('Your assistant'));
      const panes = [...document.querySelectorAll('header .absolute')].filter((e) => e.getClientRects().length);
      const feed = panes.find((t) => (t.textContent || '').replace(/\s+/g, ' ').trim().startsWith('Live activity · your assistant'));
      return { expanded: chip?.getAttribute('aria-expanded'), open: !!feed, inside: !!feed && feed.contains(document.activeElement) };
    });
    const openFeed = async () => {
      await p.evaluate(() => [...document.querySelectorAll('header [aria-haspopup="menu"]')].find((b) => (b.textContent || '').includes('Your assistant'))?.focus());
      await p.keyboard.press('Space'); await delay(250);
      await p.keyboard.press('ArrowDown'); await delay(200);
      await p.keyboard.press('Enter'); await delay(350);
    };
    await openFeed();
    assert.deepEqual(await feedState(), { expanded: 'true', open: true, inside: true }, 'Enter opens the feed with the ring inside it');
    // Native Tab still walks the feed's own entries: the header lane's decision, kept.
    await p.keyboard.press('Tab'); await delay(250);
    assert.equal((await feedState()).inside, true, 'Tab walks the feed entries rather than trapping on one control');
    // Tab until the ring genuinely leaves the feed; the feed must be gone by then, not stranded open.
    for (let i = 0; i < 12 && (await feedState()).inside; i++) { await p.keyboard.press('Tab'); await delay(120); }
    const left = await feedState();
    assert.equal(left.open, false, 'the feed closes when the ring leaves it, so Escape is never dead');
    assert.equal(left.expanded, 'false', 'and the chip says so');

    // 4. A canvas that zooms itself owns its Ctrl+wheel. Astra: at 1000x768, x=70,y=450 on the Lineage Graph
    // canvas took Studio to 110%; x=700,y=400 on the same canvas did not, so it depended on where the pointer was.
    await clickText(p, 'Lineage', '.studio-arearow');
    await delay(400);
    await p.evaluate(() => [...document.querySelectorAll('.sem-toolrow button')].find((b) => (b.innerText || '').trim() === 'Graph')?.click());
    await p.setViewport({ width: 1000, height: 768, deviceScaleFactor: 1 });
    await p.waitForFunction(() => [...document.querySelectorAll('canvas')].some((c) => c.getClientRects().length > 0), { timeout: 15000 });
    assert.equal(await p.evaluate(() => !!document.querySelector('.lineage-graph-canvas[data-own-zoom]')), true,
      'the ECharts graph container declares that it owns its zoom gesture');
    for (const [x, y] of [[70, 450], [700, 400]]) {
      await p.keyboard.down('Control'); await p.keyboard.press('0'); await p.keyboard.up('Control');
      await p.waitForFunction(() => getComputedStyle(document.querySelector('.studio-root')).zoom === '1');
      await p.mouse.move(x, y);
      await p.keyboard.down('Control');
      for (let i = 0; i < 3; i++) { await p.mouse.wheel({ deltaY: -100 }); await delay(120); }
      await p.keyboard.up('Control');
      await delay(400);
      assert.equal(await rootZoom(), '1', `Ctrl+wheel at ${x},${y} on the graph canvas must leave Studio at 100%`);
    }

    assert.deepEqual(spot.errors, []);
    await p.close();
  }

  // --- the Create segment's menu must not live inside the strip that scrolls ------------------------------
  // Kane hit this on the Yoga on his first click: Create opened, the strip scrolled itself to reveal the focused
  // item, and the row was left showing a lone scrolled "Docs" with a scrollbar on two sides. .area-tabs is
  // overflow-x: auto (styles.css) so the strip can shrink instead of wrapping; an absolutely positioned menu
  // INSIDE it is therefore both clippable and able to move the row under itself.
  {
    const strip = await openPage('live=1&conn=1&tier=pro');
    const p = strip.page;
    await p.waitForSelector('.area-tabs');
    const openCreate = () => p.evaluate(() => [...document.querySelectorAll('.area-tabs button')]
      .find((b) => (b.textContent || '').trim().startsWith('Create'))?.click());
    const readStrip = () => p.evaluate(() => {
      const bar = document.querySelector('.area-tabs');
      const menu = document.querySelector('[role="menu"][aria-label="Create"]');
      const btn = [...bar.querySelectorAll('button')].find((b) => (b.textContent || '').trim().startsWith('Create'));
      const bb = btn.getBoundingClientRect();
      const mb = menu ? menu.getBoundingClientRect() : null;
      const factor = parseFloat(getComputedStyle(document.querySelector('.studio-root')).zoom) || 1;
      return {
        menuOpen: !!menu, insideStrip: !!menu && bar.contains(menu),
        scrollLeft: Math.round(bar.scrollLeft), scrollTop: Math.round(bar.scrollTop),
        canScrollY: bar.scrollHeight > bar.clientHeight + 1,
        segments: [...bar.querySelectorAll('button')].map((b) => (b.textContent || '').trim().split(' ')[0]),
        gap: mb ? +(mb.top - bb.bottom).toFixed(1) : null,
        leftOffset: mb ? +(mb.left - bb.left).toFixed(1) : null,
        right: mb ? +mb.right.toFixed(1) : null, innerWidth: window.innerWidth, factor,
      };
    });
    for (const width of [1366, 1000]) {
      await p.setViewport({ width, height: 768, deviceScaleFactor: 1 });
      await delay(400);
      const before = await readStrip();
      assert.equal(before.menuOpen, false, `${width}px: the Create menu starts closed`);
      await openCreate();
      await p.waitForSelector('[role="menu"][aria-label="Create"]', { timeout: 5000 });
      await delay(250);
      const after = await readStrip();
      assert.equal(after.insideStrip, false, `${width}px: the Create menu is not a descendant of the scrolling strip`);
      assert.equal(after.scrollLeft, before.scrollLeft, `${width}px: opening Create does not scroll the strip sideways`);
      assert.equal(after.scrollTop, 0, `${width}px: and the strip never scrolls vertically`);
      assert.equal(after.canScrollY, false, `${width}px: nothing inside the strip gives it a vertical scrollbar`);
      assert.deepEqual(after.segments, before.segments, `${width}px: every segment is still on the row`);
      assert.ok(after.gap > 1 && after.gap < 4 * after.factor + 4, `${width}px: the menu sits under Create (gap ${after.gap})`);
      assert.ok(Math.abs(after.leftOffset) < 2, `${width}px: and starts at Create's own left edge (offset ${after.leftOffset})`);
      assert.ok(after.right <= after.innerWidth - 4, `${width}px: the whole menu is inside the viewport`);
      // The keyboard contract is unchanged by the move: arrows walk it, Escape cancels back to the segment.
      await p.keyboard.press('ArrowDown'); await delay(200);
      assert.equal(await p.evaluate(() => document.activeElement?.getAttribute('role')), 'menuitem',
        `${width}px: ArrowDown still moves the ring onto a menu item`);
      assert.equal((await readStrip()).scrollLeft, before.scrollLeft,
        `${width}px: and focusing an item still does not move the strip`);
      await p.keyboard.press('Escape'); await delay(250);
      assert.equal((await readStrip()).menuOpen, false, `${width}px: Escape closes the Create menu`);
      assert.equal(await p.evaluate(() => (document.activeElement?.textContent || '').trim().startsWith('Create')), true,
        `${width}px: and returns the ring to the segment`);
    }
    // No open menu anywhere in the two rows that clip may live inside a container that scrolls or hides it.
    await openCreate();
    await p.waitForSelector('[role="menu"][aria-label="Create"]');
    assert.equal(await p.evaluate(() => document.querySelectorAll('.area-tabs [role="menu"], .sem-toolrow [role="menu"], .area-tabs [role="dialog"], .sem-toolrow [role="dialog"]').length), 0,
      'no menu or dialog is rendered inside the strip or the tool row');
    await p.keyboard.press('Escape');
    assert.deepEqual(strip.errors, []);
    await p.close();
  }

  // Loading, failure, a real zero-table model, and no-model startup are four distinct states.
  for (const [mode, expected] of [['loading', 'Loading model structure…'], ['error', 'Couldn’t load this model'], ['empty', 'This model is open and has no tables.'], ['nomodel', 'Open a model to begin']]) {
    const opened = await openPage(`home=${mode}`);
    await waitText(opened.page, expected);
    assert.deepEqual(opened.errors, []);
    await opened.page.close();
  }

  console.log('integrated shell journey passed: native relay, one-use seeds, draft preservation, session fences, aliases, publish, tool chooser, Settings Done (warm, Escape, cold hand-off, model swap), the strip on a page reached with an object in hand, Model states and the assistant keyboard journey');
} finally {
  await browser.close();
  await new Promise((done) => server.close(done));
}
