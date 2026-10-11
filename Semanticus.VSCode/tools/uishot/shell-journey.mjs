// Focused browser proof for the integrated six-area shell (seven until the Overview retired in slice 11, lane R1). It drives the built React bundle through the same
// host-message bridge used by VS Code and asserts state transitions that source-pattern tests cannot prove.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';

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
// Land on an area and wait for its segment strip. Every journey that wants a segment has to say which area it
// belongs to first: the Plan is the landing area (the Overview was, until slice 11) and has ONE tool, so its row
// names the area and has no segments on it at all.
const goArea = async (page, label) => {
  await clickText(page, label, 'nav[aria-label="Studio areas"]');
  await page.waitForSelector('.studio-arearow .area-tabs', { timeout: 15000 });
};
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
  // The Plan is the start page since slice 11, and the picked object filters it: "Showing Total Sales", with the
  // object's actions on that line (contract 11.1).
  const cold = await openPage('live=1&initialSelection=measure%3ASales%2FTotal%20Sales');
  await cold.page.waitForSelector('[data-testid="plan-page"]');
  await waitText(cold.page, 'Edit formula');
  assert.ok((await cold.page.$eval('[data-testid="plan-list"] .pw-grp', (node) => node.textContent)).includes('Total Sales'));
  assert.deepEqual(cold.errors, []);
  await cold.page.close();

  // tier=pro, from 2026-09-15. This journey walks the DAX Lab and Tests hand-offs, and Tests is one whole
  // Pro feature now: on the free plan the Tests page renders its preview and the New check drawer it drives
  // below does not exist at all. The subject here is the seed hand-off, not the entitlement, so the journey
  // opens on a plan that can reach it. The free side of that boundary is driven by cp-pro-drive.mjs.
  const { page, errors } = await openPage('tier=pro');
  await page.waitForSelector('[data-testid="plan-page"]');
  assert.equal(await activeArea(page), 'Plan');

  // An exact object action reaches the native host relay with the chosen ref, not whichever tree row is selected later.
  await dispatch(page, { type: 'treeSelection', sessionId: 'uishot', ref: 'measure:Sales/Total Sales' });
  await waitText(page, 'Edit formula');
  await clickText(page, 'Edit formula', '[data-testid="plan-object-actions"]');
  const editMessage = await page.evaluate(() => window.__uishotHarness.messages.filter((item) => item.type === 'runCommand').at(-1));
  assert.deepEqual(editMessage, { type: 'runCommand', command: 'semanticus.editDax', ref: 'measure:Sales/Total Sales', sessionId: 'uishot' });

  // A deliberate measure seed escapes the output label and DAX identifier separately, then is consumed once.
  const awkwardRef = 'measure:Sales/Net "Sales]';
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab', target: awkwardRef });
  await page.waitForFunction(() => window.__uishotHarness.state()['input:lab.bq.uishot'] === 'EVALUATE ROW("Net ""Sales]", [Net "Sales]]])');
  assert.equal(await activeArea(page), 'Edit');
  const editor = await page.$('.cm-content');
  assert.ok(editor, 'DAX query editor mounted');
  await editor.click();
  await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
  await page.keyboard.type('EVALUATE ROW("mine", 42)');
  await page.waitForFunction(() => window.__uishotHarness.state()['input:lab.bq.uishot'] === 'EVALUATE ROW("mine", 42)');
  await clickText(page, 'Plan', 'nav[aria-label="Studio areas"]');
  await clickText(page, 'Edit', 'nav[aria-label="Studio areas"]');
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
  assert.ok(/New check/i.test(await text(page)), 'the drawer names itself New check (sentence case since One Visual Language)');

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
    const choice = [...document.querySelectorAll('button')].find((item) => (item.textContent || '').trim().startsWith('Check against source SQL'));
    choice?.click(); return !!choice;
  }), true, 'check-against-source-SQL choice exists');
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
  await clickText(page, 'Open Validation');
  await waitText(page, 'Total Sales ties to source SQL');
  await page.evaluate(() => window.addEventListener('message', (event) => {
    const request = window.__uishotHarness.messages.find((item) => item.type === 'rpc' && item.id === event.data?.id);
    if (event.data?.type === 'rpcResult' && request?.method === 'runTests') window.__lastCheckResult = event.data.result;
  }));
  await clickText(page, 'Run checks');
  await waitText(page, 'Net Revenue ties to finance extract');
  await page.evaluate(() => document.querySelector('button[aria-label="More for Total Sales ties to source SQL"]')?.click());
  // SCOPED to that row's own menu. Unscoped, this used to be the only Edit on screen; from 22 September 2026
  // there is an area button called Edit in the header, it comes first in the document, and an unscoped click
  // navigated away instead of opening the check.
  await clickText(page, 'Edit', '[role="menu"][aria-label="Actions for Total Sales ties to source SQL"]');
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
  await clickText(page, 'Run checks');
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
  await page.waitForSelector('[data-testid="plan-page"]');
  assert.equal(await page.$('[data-testid="plan-object-actions"]'), null);
  await dispatch(page, { type: 'treeSelection', sessionId: 'uishot', ref: 'measure:Sales/Total Sales' });
  await dispatch(page, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab', target: 'measure:Sales/Total Sales' });
  await delay(100);
  assert.equal(await activeArea(page), 'Plan');
  assert.equal(await page.$('[data-testid="plan-object-actions"]'), null);
  await dispatch(page, { type: 'treeSelection', sessionId: 'session-b', ref: 'measure:Sales/Total Sales' });
  await waitText(page, 'Try calculation');

  // The object hand-off still opens the complete tool. What it no longer leaves behind is a Back button: Kane retired
  // that on 2026-09-14 after hitting it himself ("The back button doesnt do anything"). Its replacement is narrower on
  // purpose and lives further down this file — one Done, on Settings pages only.
  await clickText(page, 'Try calculation', '[data-testid="plan-object-actions"]');
  await waitText(page, 'Queries');
  assert.equal(await page.$('.studio-arearow .studio-back'), null, 'a page reached by a hand-off carries no Back button');
  assert.doesNotMatch(await page.$eval('.studio-arearow', (node) => node.innerText), /Back/, 'and no Back text in the area row');

  // The legacy Data Agent route lands on the Publish page (the Data Agent screen is hidden for now). On that page the
  // header Publish button is hidden, because the page carries its own.
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'dataagent' });
  await page.waitForFunction(() => document.body.innerText.includes('Earlier versions') && document.body.innerText.includes('Changes'));
  assert.equal(await activeArea(page), 'Publish');
  assert.equal(await page.$('.studio-publish'), null, 'the header Publish is hidden on the Publish page');
  assert.equal(await page.$('[data-testid="publish-confirm"]'), null, 'no review card');

  // The header fold is MEASURED, so at this 1440-wide viewport the utilities belong on the header itself and More
  // must be folded away. (Find a tool left the header on 8 Oct 2026: it is Help > All tools, so it is never "shown" here.) The old fixed 1500px container query hid them here, which was the defect.
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
  assert.deepEqual(await readHeader(), { fold: '0', findATool: false, connections: true, more: false, gear: false, assistant: true, areas: 6 });

  // Narrow the window and the utilities fold into More, while the six areas stay put.
  await page.setViewport({ width: 1000, height: 800, deviceScaleFactor: 1 });
  await page.waitForFunction(() => document.querySelector('.studio-chrome')?.dataset.fold !== '0', { timeout: 10000 });
  const narrow = await readHeader();
  assert.equal(narrow.more, true, 'utilities fold into More on a narrow header');
  assert.equal(narrow.findATool, false, 'Find a tool leaves the header when it no longer fits');
  assert.equal(narrow.areas, 6, 'the six areas fold LAST and are still on a 1000-wide header');

  // Find a tool chooses the complete existing tool. Changes and Workflows retain primary-area highlighting.
  await clickText(page, 'More');
  await clickText(page, 'Find a tool');
  await page.click('[data-tool="docs"]');
  await page.waitForSelector('iframe[title="Documentation preview"]');
  assert.equal(await activeArea(page), 'Review');
  // The old Edit History id lands on the Plan, on its History view (slice 11, lane R2).
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'history' });
  await page.waitForFunction(() => document.querySelector('[data-testid="plan-page"]')?.dataset.view === 'hist', { timeout: 15000 });
  assert.equal(await activeArea(page), 'Plan');
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'workflows' });
  assert.equal(await activeArea(page), 'Workflows');

  // A current-session model change refreshes the start page instead of leaving a stale plan. The old Overview id
  // lands on the Plan (slice 11), and the plan it reads after the change is the one it shows.
  await dispatch(page, { type: 'navigate', sessionId: 'session-b', tab: 'modelhome' });
  await page.waitForFunction(() => document.querySelector('[data-testid="plan-page"]')?.dataset.view === 'work', { timeout: 15000 });
  const planReads = () => page.evaluate(() => window.__uishotHarness.messages.filter((m) => m.type === 'rpc' && m.method === 'getModelPlan').length);
  await delay(400);
  const readsBefore = await planReads();
  await dispatch(page, { type: 'didChange', payload: { sessionId: 'session-b', revision: 2, origin: 'human', label: 'edit', deltas: [] } });
  await page.waitForFunction((n) => window.__uishotHarness.messages.filter((m) => m.type === 'rpc' && m.method === 'getModelPlan').length > n,
    { timeout: 15000 }, readsBefore);
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
    await shell.waitForSelector('[data-testid="plan-page"]');
    const rowHeights = () => shell.evaluate(() => {
      const h = (s) => { const el = document.querySelector(s); return el ? Math.round(el.getBoundingClientRect().height) : null; };
      return { header: h('header.studio-chrome'), arearow: h('.studio-arearow'), contextbar: h('.studio-contextbar'),
        breadcrumb: document.querySelector('.studio-breadcrumb') === null ? 'gone' : 'still here',
        guide: document.querySelector('aside[aria-label="Page guidance"]') === null ? 'gone' : 'still here' };
    });
    assert.deepEqual(await rowHeights(), { header: 40, arearow: null, contextbar: 34, breadcrumb: 'gone', guide: 'gone' },
      'one 40px row above the Plan (it has one tool, so no second row) and a 34px bar under it, with no breadcrumb row and no open guide');

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
    // The Plan is the landing AREA and has one tool, so its row names the area and has no segments to click.
    // Diagram lives under Explore, so this walks there the way a person would: the area, then the tab.
    await goArea(shell, 'Explore');
    await clickText(shell, 'Diagram', '.studio-arearow');
    await shell.waitForFunction(() => !!document.querySelector('.react-flow'), { timeout: 10000 });
    assert.equal(await backShown(), false, 'and none once you have navigated');
    assert.equal(await backText(), false, 'and still no Back text');
    assert.equal(await doneShown(), false, 'and still no Done');

    // The page guide has one door now: Help > Help for this page (the area row's i left on 8 Oct 2026).
    assert.equal(await shell.$('.studio-page-notes'), null, 'no page notes button on the row');
    await clickText(shell, 'Help', 'header.studio-chrome');
    await shell.waitForSelector('[role="menu"][aria-label="Help"]');
    await clickText(shell, 'Help for this page', '[role="menu"][aria-label="Help"]');
    await shell.waitForFunction(() => document.body.innerText.includes('See how your tables connect. Arrange the map and create or edit the links between tables.'), { timeout: 10000 });
    const guide = await shell.evaluate(() => document.body.innerText);
    assert.ok(guide.includes('Getting started'), 'the guide still reads Getting started');
    assert.ok(guide.includes('Open a model, then expand a table to see its columns.'), 'with the numbered steps');
    assert.ok(!guide.includes('When a page cannot continue'), 'and without the shared state block that repeated on every page');
    await shell.keyboard.press('Escape');
    await shell.waitForFunction(() => !document.body.innerText.includes('Getting started'), { timeout: 5000 });

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
      if (!done) return null;
      const d = done.getBoundingClientRect();
      const row = document.querySelector('.studio-arearow').getBoundingClientRect();
      return { height: Math.round(d.height),
        // Adjacent to the ⓘ, not stranded in mid-row: the gap between them is the row's own 8px, and the pair sits
        // at the right-hand end. Both halves are needed — the first build put Done exactly halfway, which satisfied
        // "before the notes" while looking nothing like the design.
        nearTheRightEdge: row.right - d.right < 40 };
    });

    // The Lineage landmark is a control that is ON the page, not a sub-tab name: "Published reports" was a
    // sub-tab until Impact became one page, and a landmark that can be retired makes this journey fail for a
    // reason that has nothing to do with the shell.
    // 1. WARM. Lineage, then Settings, then Done lands back on Lineage, not on the Plan, which is what a fallback
    //    would give and what would make the button look almost right while being wrong.
    const warm = await openPage('live=1');
    await warm.page.waitForSelector('[data-testid="plan-page"]');
    await goArea(warm.page, 'Explore');
    await clickText(warm.page, 'Lineage', '.studio-arearow');
    await waitText(warm.page, 'Impact');
    await openAssistantMenu(warm.page);
    await clickText(warm.page, 'Permissions', '[role="menu"][aria-label="Your assistant"]');
    await warm.page.waitForFunction(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'), { timeout: 10000 });
    assert.match(await settingsRow(warm.page), /^Settings › Assistant permissions/, 'the Settings row names the page you are on');
    assert.doesNotMatch(await settingsRow(warm.page), /← Back/, 'and offers no Back');
    // The shared dense control step is 24px (styles.css --sem-control-h-sm), and Done sits at the right-hand end of
    // the row, ahead of the ⓘ. Measured, because "far right, before the i" is geometry.
    assert.deepEqual(await doneAt(warm.page), { height: 24, nearTheRightEdge: true },
      'Done is on the shared 24px step, at the right-hand end of the row');
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
    await cold.page.waitForSelector('[data-testid="plan-page"]', { timeout: 10000 });
    assert.equal(await cold.page.$('.studio-arearow [data-testid="settings-done"]'), null,
      'Done leaves Settings even though the page it returns to was recorded before sessionInfo answered');
    assert.deepEqual(cold.errors, []);
    await cold.page.close();

    // 4. A real model swap forgets the page you came from, so Done falls back to the Plan rather than
    //    returning you into the model you no longer have open.
    const swap = await openPage('live=1');
    await swap.page.waitForSelector('[data-testid="plan-page"]');
    await goArea(swap.page, 'Explore');
    await clickText(swap.page, 'Lineage', '.studio-arearow');
    await waitText(swap.page, 'Impact');
    await openAssistantMenu(swap.page);
    await clickText(swap.page, 'Permissions', '[role="menu"][aria-label="Your assistant"]');
    await swap.page.waitForFunction(() => !!document.querySelector('.studio-arearow [data-testid="settings-done"]'), { timeout: 10000 });
    await swap.page.evaluate(() => window.__uishotHarness.setResponse('sessionInfo', {
      sessionId: 'session-c', modelName: 'Third Model', revision: 1, tables: 11, measures: 12, source: 'C:/Models/Third.Model' }));
    await dispatch(swap.page, { type: 'reconnected' });
    await swap.page.waitForSelector('[data-testid="plan-page"]', { timeout: 10000 });
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
    await p.waitForSelector('[data-testid="plan-page"]');
    const readRow = () => p.evaluate(() => {
      const bar = document.querySelector('.studio-arearow');
      const segments = [...bar.querySelectorAll('.area-tabs button')];
      return {
        strip: segments.length > 0,
        segments: segments.map((b) => b.textContent.replace(/\s+/g, ' ').trim()),
        // The open segment says so with aria-current="page"; since Instrument (23 September 2026) its look is
        // drawn from that attribute in styles.css, not from an inline background.
        active: segments.find((b) => b.getAttribute('aria-current') === 'page')?.textContent.replace(/\s+/g, ' ').trim() ?? null,
        objectMarkup: bar.querySelectorAll('.studio-arearow-object, [data-testid="arearow-clear-object"]').length,
        tool: bar.querySelector('.studio-arearow-tool')?.textContent.trim() ?? null,
        height: Math.round(bar.getBoundingClientRect().height),
        text: bar.innerText.replace(/\s+/g, ' ').trim(),
      };
    });
    // Kane's own route in was the chevron on an Overview table row, which WAS "Preview data". The Overview retired in
    // slice 11 (lane R1) and its table actions moved to the Plan's object line (contract 11.1): pick a table in the
    // model tree and "Preview data" is the first action under "Showing <table>". The journey is about the
    // HAND-OFF, so the table is read back off the line rather than trusted.
    const previewTable = async () => {
      await dispatch(p, { type: 'treeSelection', sessionId: 'uishot', ref: 'table:Promotion' });
      await p.waitForSelector('[data-testid="plan-object-actions"]', { timeout: 15000 });
      const picked = await p.evaluate(() => {
        const line = document.querySelector('[data-testid="plan-list"] .pw-grp b');
        const go = [...document.querySelectorAll('[data-testid="plan-object-actions"] button')].find((b) => b.textContent.trim() === 'Preview data');
        if (go) go.click();
        return go && line ? (line.innerText || '').trim() : null;
      });
      assert.ok(picked, 'the object line under the picked table offers Preview data');
      // Wait for the Data page itself: its preview title.
      await p.waitForFunction(() => /Preview · |Data preview/.test(document.querySelector('main')?.innerText || ''), { timeout: 15000 });
      return picked;
    };
    // Matched as a whole word, and escaped: the assertions below say this table's name is NOT on the
    // context row, and a bare substring would be answered by any longer word that happens to contain it.
    const wholeWord = (name) => new RegExp(`\\b${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`);

    const previewed = await previewTable();
    const stranded = await readRow();
    assert.equal(stranded.strip, true, `Data reached with a table in hand keeps the Explore strip (row read "${stranded.text}")`);
    assert.equal(stranded.active, 'Data', 'and Data is the segment marked as open');
    assert.ok(stranded.segments.includes('Diagram'), 'so the rest of Explore is one click away on the row itself');
    assert.equal(stranded.tool, null, 'the tool name is not repeated: the open segment already says it');
    assert.equal(stranded.objectMarkup, 0, 'and the table is not on the row at all, as a crumb or as a chip');
    assert.doesNotMatch(stranded.text, wholeWord(previewed), `nothing on the row names the table (row read "${stranded.text}")`);
    // The page is what names it, which is why the row does not have to.
    assert.ok((await text(p)).includes(`Preview · ${previewed}`),
      `the PAGE says which table you are looking at (expected "Preview · ${previewed}")`);
    assert.equal(stranded.height, 40, 'the row is still one compact 40px line');

    // Exit one: the strip itself. It is the thing that was missing.
    await clickText(p, 'Diagram', '.studio-arearow');
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.getAttribute('aria-current') === 'page' && b.getAttribute('data-tab') === 'diagram'), { timeout: 15000 });
    assert.equal((await readRow()).active, 'Diagram', 'a segment on the strip takes you off the page');

    // Exit two: the area you are ALREADY in goes to its home page, never back to the page you are looking at.
    // Walk into the trap again first, the way Kane walked into it.
    await clickText(p, 'Plan', 'nav[aria-label="Studio areas"]');
    await p.waitForSelector('[data-testid="plan-page"]', { timeout: 10000 });
    await previewTable();
    assert.equal((await readRow()).active, 'Data', 'the hand-off is live again');
    await clickText(p, 'Explore', 'nav[aria-label="Studio areas"]');
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.getAttribute('aria-current') === 'page' && b.getAttribute('data-tab') === 'data'), { timeout: 10000 });
    assert.equal((await readRow()).active, 'Data', 'Explore, pressed while you are already in Explore, lands on its home');

    // The same exit on a second multi-tool area, so the rule is not read off one area's home.
    // Matched on data-tab, not on the words: a gated segment can wear a Pro pill inside the button, and a
    // text-equality match never fires against "WorkflowsPro". readRow below still reads the label, so the
    // pill is allowed for in the assertion instead of being matched against.
    await clickText(p, 'Review', 'nav[aria-label="Studio areas"]');
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.getAttribute('aria-current') === 'page' && b.getAttribute('data-tab') === 'bpa'), { timeout: 15000 });
    await clickText(p, 'AI readiness', '.studio-arearow');
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.getAttribute('aria-current') === 'page' && b.getAttribute('data-tab') === 'readiness'), { timeout: 15000 });
    await clickText(p, 'Review', 'nav[aria-label="Studio areas"]');
    await p.waitForFunction(() => [...document.querySelectorAll('.studio-arearow .area-tabs button')]
      .some((b) => b.getAttribute('aria-current') === 'page' && b.getAttribute('data-tab') === 'bpa'), { timeout: 15000 });
    assert.match((await readRow()).active, /^Model quality \(BPA\)(Pro)?$/,
      'Review, pressed while you are in Review, lands on its home, not on the page you were reading');

    // The same trap on an area with ONE tool. There is no strip to show, so the row names the tool. Workflows
    // is that area now: Calculations was, and its one tool is a segment of Edit from 22 September 2026.
    await dispatch(p, { type: 'navigate', sessionId: 'uishot', tab: 'workflows' });
    await p.waitForFunction(() => document.querySelector('.studio-arearow')?.innerText.includes('Workflows'), { timeout: 15000 });
    // The pill is part of the button's text on a free plan, so this matches rather than compares. Workflows
    // is the one paid area and it is the one area that has no segment strip, so the AREA button wears it.
    assert.match(await activeArea(p), /^Workflows(Pro)?$/);
    // An area with one tool has no second row at all (8 Oct 2026): the page title says it.
    assert.equal(await p.$('.studio-arearow'), null, 'Workflows has no second row');

    // And a measure hand-off into a MULTI-tool area still keeps the object off the row: the lab's own wells
    // carry that, which is the fact the Calculations case used to prove here.
    await dispatch(p, { type: 'navigate', sessionId: 'uishot', tab: 'daxlab', target: 'measure:Sales/Total Sales' });
    await p.waitForSelector('.cm-content', { timeout: 15000 });
    assert.equal(await activeArea(p), 'Edit');
    const queries = await readRow();
    assert.equal(queries.strip, true, 'Queries is a segment of Edit, so the strip stays up');
    assert.equal(queries.active, 'Queries', 'and Queries is the segment marked as open');
    assert.equal(queries.objectMarkup, 0, 'the measure is not on the row as markup');
    assert.doesNotMatch(queries.text, /Total Sales/, `nor as text (row read "${queries.text}")`);
    assert.equal(queries.height, 40, 'still one 40px line');
    await p.close();
  }

  // --- Studio zoom and the keyboard: Astra's third spot review, 2026-09-14 -------------------------------
  // Four defects that only a real browser at a real zoom can see. Every one of these assertions was watched to
  // FAIL against 41c0b397 before the fix existed, with the measured numbers recorded in the lane report.
  {
    const spot = await openPage('live=1&conn=1&tier=pro&dirty=1&act=1');
    const p = spot.page;
    await p.waitForSelector('[data-testid="plan-page"]');
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
    // The header's content must be the SAME on every run before its fold is read. ?act=1 fires its activity events on
    // a 500ms timer after load, and when that timer beats the page subscribing, the events are lost and the Your
    // assistant chip never draws its feed mark. The mark is about 10px, and 1250px sits between the two folds: with it,
    // level 0 misses by 10.5px (fold 1); without it, level 0 fits with 0.1px to spare (fold 0). Measured on 9f0e6ec0,
    // 10 runs of this path: the geometry was identical run to run, and only the mark's presence changed the fold.
    // So the fold was right each time and the fixture was racing. The mark is now a badge on the chip's corner that
    // takes no layout width (styles.css .studio-feed-mark), so activity no longer changes the header; this event
    // stays as a guard.
    await dispatch(p, { type: 'activity', payload: { seq: 99, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true } });
    await p.waitForSelector('.studio-actions .studio-feed-mark[data-live]', { timeout: 10000 });
    await setZoom(80);
    const roomy = await headerIsUsable('1000px at 80%');
    // 1000px at 80% is a 1250px effective header, and on this build it is the knife edge: level 0 fits it with 0.125px
    // to spare (999px is fold 1). Asserting a bare '0' there made any sub-pixel change to the header (a font, a
    // Chromium update) fail the journey without anything being wrong (verifier, m11 header-stable round 2). So the
    // check reads the room level 0 actually has at this width, with the same rule the fold uses (every area button's
    // text ends left of the actions, 0.5px of rounding allowed, nothing sticks out of the header), and holds the fold
    // to it EXACTLY: level 0 whenever level 0 fits, which is Kane's F1 rule (the areas and the model name show
    // whenever they fit), and only the cheapest fold, the model-identity detail, when it misses. That is no weaker:
    // an early fold, a stale fold or a deeper fold all still fail, at any width the threshold lands on.
    const levelZero = await p.evaluate(() => {
      const h = document.querySelector('header.studio-chrome');
      const was = { fold: h.dataset.fold, brand: h.dataset.brandFolded };
      h.dataset.fold = '0'; delete h.dataset.brandFolded;
      const edge = h.querySelector('.studio-actions').getBoundingClientRect().left;
      let slack = Infinity;
      for (const b of h.querySelector('.studio-groups').children) {
        if (!(b instanceof HTMLElement) || b.getClientRects().length === 0) continue;
        slack = Math.min(slack, edge - (b.getBoundingClientRect().right - (parseFloat(getComputedStyle(b).paddingRight) || 0)));
      }
      const overflow = h.scrollWidth - h.clientWidth;
      h.dataset.fold = was.fold; if (was.brand) h.dataset.brandFolded = was.brand;
      return { slack, overflow };
    });
    const zeroFits = levelZero.slack >= -0.5 && levelZero.overflow <= 0;
    assert.equal(roomy.fold, zeroFits ? '0' : '1',
      `at 80% a 1250px header takes the shallowest fold that fits: level 0 has ${levelZero.slack.toFixed(3)}px of room and ${levelZero.overflow}px of overflow`);
    // And the header has not grown. The fold above follows whatever the header holds, so this is the guard that the
    // room for the six areas and the model name at 1250px is still there: it may move by rounding, never by a pixel.
    // If a later change wants that pixel, the room has to come from the area labels, not from loosening this line.
    assert.ok(levelZero.slack >= -1, `at 80% a 1250px header has room for level 0 to within a pixel (it has ${levelZero.slack.toFixed(3)}px)`);
    // The live feed mark costs the header nothing at this width: with the mark taken out and the fold measured afresh,
    // the header folds exactly as it does with the mark live (the mark is a badge inside the chip, styles.css).
    const refold = async (hide) => {
      await p.evaluate((off) => {
        const h = document.querySelector('header.studio-chrome');
        const mark = h.querySelector('.studio-feed-mark');
        if (mark) mark.style.display = off ? 'none' : '';
        h.firstElementChild.style.setProperty('--fold-probe', String(Math.random()));   // a mutation the fold re-measures on
      }, hide);
      await p.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 50)))));
      return p.evaluate(() => { const h = document.querySelector('header.studio-chrome'); return h.dataset.fold + (h.dataset.brandFolded ? 'b' : ''); });
    };
    const withoutMark = await refold(true);
    const withMark = await refold(false);
    assert.equal(withMark, withoutMark, 'at 80% and 1000px the live feed mark changes no fold level');
    assert.equal(withMark, roomy.fold, 'and a fresh measure agrees with the fold the header showed');

    // The areas nav never paints outside its own box, whatever the fold level decides.
    // Clipped, with a clip margin of the header gap: it paints up to the actions' edge and never over them.
    assert.equal(await p.evaluate(() => getComputedStyle(document.querySelector('.studio-groups')).overflow), 'clip',
      'the areas nav clips its own buttons rather than painting them over the actions');
    assert.equal(await p.evaluate(() => {
      const g = document.querySelector('.studio-groups'); const h = document.querySelector('header.studio-chrome');
      // Compared as numbers: under Studio zoom (this block runs at 80%) each may be reported scaled or rounded.
      return Math.abs(parseFloat(getComputedStyle(g).overflowClipMargin) - parseFloat(getComputedStyle(h).columnGap)) < 0.5;
    }), true, 'and its clip margin is exactly the gap before the actions');

    // 2. The shared tool menu is placed in ONE coordinate space. Under CSS zoom a measured screen rect written
    // straight into a fixed left/top is multiplied by the zoom a second time: Astra measured the Lineage Tree
    // Show menu ending at x=1065.7 in a 1000px viewport, 46.7px below its button instead of beside it.
    await setZoom(100);
    await goArea(p, 'Explore');
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

    // 3. (The page notes popover left on 8 Oct 2026, with its Tab trap and focusout rules.) Live activity below keeps its own.
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
    await goArea(p, 'Explore');
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

  // --- F1 (journey m11): the areas fold only when they genuinely do not fit, and come back when they do -------
  // The fold used to be an ESTIMATE of each level's saving. It left out the actions' tighter gap and the nav's
  // dropped margin, so after any resize it could land on 4 at a width where level 3 visibly fitted: measured
  // on the harness, a 884px header read fold 4 while level 3 fitted, and 1036px read 3 while 2 fitted. The
  // lane hit the same at 792px after opening a measure or pressing Ctrl+B twice. And content that grows without
  // a resize (the Pro chip once the entitlement answers, the assistant count 250 ms after the header) was never
  // re-measured at all.
  // `fit` is the shallowest level at which nothing sticks out of the header and every area button's text (its
  // content box) is inside the nav, left of the actions. GEOMETRY, not scrollWidth: the recheck on 9319becf
  // (journey m11-2) found the nav's scrollWidth 6px over its clientWidth at level 3 while Workflows ended 2px
  // before the actions. scrollWidth counts trailing space and sub-pixels nobody sees, and judging by it folded the
  // six areas into More from the first frame at 1440 wide with the sidebar open (a 792px webview).
  const fitProbe = () => {
    const h = document.querySelector('header.studio-chrome');
    const g = h.querySelector('.studio-groups');
    const a = h.querySelector('.studio-actions');
    const fits = () => {
      if (h.scrollWidth > h.clientWidth) return false;
      // The actions' left edge, not the nav's right edge: the nav paints into the gap before the actions, and the
      // m11-3 lane (a real 792px Studio) folded because Workflows' content edge was 1.9px past the nav's box while
      // it ended 6px clear of More, drawn whole.
      const edge = a.getBoundingClientRect().left;
      return [...g.children].filter((b) => b.getClientRects().length).every((b) => {
        const r = b.getBoundingClientRect();
        return r.right - (parseFloat(getComputedStyle(b).paddingRight) || 0) <= edge + 0.5;
      });
    };
    const current = h.dataset.fold;
    let fit = 4;
    for (const level of ['0', '1', '2', '3']) { h.dataset.fold = level; if (fits()) { fit = Number(level); break; } }
    h.dataset.fold = current;
    const areas = [...g.children].filter((b) => b.getClientRects().length).length;
    return { fold: Number(current), fit, width: h.clientWidth, areas };
  };
  // The layout of the recheck: no workflow run live, so no run chip on the header (the harness has one by default).
  // Swept at the widths VS Code gives the Studio: 792 (1440 wide, sidebar open), 1040, and every 4px between.
  {
    const lane = await browser.newPage();
    const laneErrors = [];
    lane.on('pageerror', (error) => laneErrors.push(error.message));
    await lane.evaluateOnNewDocument(() => {
      let h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => h, set: (v) => {
        h = v; v.setResponse('getWorkflowRun', null); v.setResponse('getOrientation', { activeWork: { workflows: [] } });
      } });
    });
    await lane.setViewport({ width: 792, height: 800, deviceScaleFactor: 1 });
    await lane.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tier=pro`, { waitUntil: 'networkidle0', timeout: 30000 });
    await lane.waitForSelector('[data-testid="plan-page"]');
    await delay(800);
    const first = await lane.evaluate(fitProbe);
    assert.equal(first.fold, first.fit, `first frame at 792 (1440 wide, sidebar open): fold ${first.fold}, fits at ${first.fit}`);
    assert.equal(first.areas, 6, 'and the six areas are on the header from the first frame');
    const wrong = [];
    const widths = [792, 1040]; for (let w = 1200; w >= 720; w -= 2) widths.push(w); widths.push(792, 1040);
    for (const width of widths) {
      await lane.setViewport({ width, height: 800, deviceScaleFactor: 1 });
      await delay(120);
      const read = await lane.evaluate(fitProbe);
      if (read.fold !== read.fit) wrong.push(`${read.width}px fold ${read.fold}, fits at ${read.fit}`);
      if ((width === 792 || width === 1040) && read.areas !== 6) wrong.push(`${width}px shows ${read.areas} areas`);
    }
    assert.deepEqual(wrong, [], 'with no run chip, every VS Code width folds to the shallowest level whose buttons visibly fit');
    assert.deepEqual(laneErrors, []);
    await lane.close();
  }
  {
    const f1 = await openPage('tier=pro&hold=getEntitlement');
    const p = f1.page;
    await p.waitForSelector('header.studio-chrome');
    const readFit = () => p.evaluate(fitProbe);
    // Below 720 the areas fold by rule, not by fit, so the sweep stops there.
    const sweep = async (widths) => {
      const wrong = [];
      for (const width of widths) {
        await p.setViewport({ width, height: 800, deviceScaleFactor: 1 });
        await delay(150);
        const read = await readFit();
        if (read.fold !== read.fit) wrong.push(`${read.width}px fold ${read.fold}, fits at ${read.fit}`);
      }
      return wrong;
    };
    const down = []; for (let w = 1200; w >= 720; w -= 8) down.push(w);
    const noChip = {};
    for (const width of down) { await p.setViewport({ width, height: 800, deviceScaleFactor: 1 }); await delay(100); noChip[width] = (await readFit()).fit; }
    // The entitlement answers late, as it can in VS Code, and the Pro chip appears. Nothing resizes when it does.
    await p.evaluate(() => window.__uishotHarness.release('getEntitlement'));
    await p.waitForSelector('[data-testid="pro-chip"]', { timeout: 10000 });
    assert.deepEqual(await sweep(down), [], 'shrinking the window: every width folds to the shallowest level that fits');
    assert.deepEqual(await sweep([...down].reverse()), [], 'widening it again: the areas come back as soon as they fit (Ctrl+B twice)');
    // A width where the late count changes what fits, found rather than hard-coded, so a fixture change moves it.
    const withChip = {};
    for (const width of down) { await p.setViewport({ width, height: 800, deviceScaleFactor: 1 }); await delay(100); withChip[width] = (await readFit()).fit; }
    const tipping = down.find((width) => noChip[width] !== withChip[width]);
    assert.ok(tipping, 'some width changes its fold when the Pro chip arrives');
    await p.close();
    const late = await openPage('tier=pro&hold=getEntitlement');
    await late.page.setViewport({ width: tipping, height: 800, deviceScaleFactor: 1 });
    await late.page.waitForSelector('header.studio-chrome');
    await delay(600);
    await late.page.evaluate(() => window.__uishotHarness.release('getEntitlement'));
    await late.page.waitForSelector('[data-testid="pro-chip"]', { timeout: 10000 });
    await delay(400);
    const settled = await late.page.evaluate(fitProbe);
    assert.equal(settled.fold, settled.fit, `${tipping}px: the header re-folds when its content grows without a resize`);
    assert.deepEqual([...f1.errors, ...late.errors], []);
    await late.page.close();
  }

  // --- the fold segment's menu must not live inside the strip that scrolls --------------------------------
  // The Edit strip has NO menu now (Kane, 8 Oct 2026): Advanced modelling is a plain tab, and its page row lists Power
  // Query with the six builders. So this checks the strip is plain at both widths and never scrolls.
  {
    const strip = await openPage('live=1&conn=1&tier=pro');
    const p = strip.page;
    await p.waitForSelector('[data-testid="plan-page"]');
    await dispatch(p, { type: 'navigate', sessionId: 'uishot', tab: 'search' });
    await p.waitForSelector('.area-tabs');
    for (const width of [1366, 1000]) {
      await p.setViewport({ width, height: 768, deviceScaleFactor: 1 });
      await delay(400);
      const read = await p.evaluate(() => {
        const bar = document.querySelector('.area-tabs');
        return { segments: [...bar.querySelectorAll('button')].map((b) => (b.textContent || '').trim()),
          menus: document.querySelectorAll('.area-tabs [role="menu"], .area-tabs [aria-haspopup]').length,
          scrollLeft: Math.round(bar.scrollLeft), canScrollY: bar.scrollHeight > bar.clientHeight + 1 };
      });
      assert.deepEqual(read.segments, ['Find and replace', 'Queries', 'AI instructions', 'Advanced modelling'], `${width}px: a plain strip of four`);
      assert.equal(read.menus, 0, `${width}px: no menu in the strip`);
      assert.equal(read.canScrollY, false, `${width}px: and no vertical scrollbar`);
    }
    await dispatch(p, { type: 'navigate', sessionId: 'uishot', tab: 'advmodels' });
    await p.waitForFunction(() => [...document.querySelectorAll('[aria-label="Advanced modelling"] button')].some((b) => (b.textContent || '').trim() === 'Power Query'), { timeout: 10000 });
    await clickText(p, 'Power Query', '[aria-label="Advanced modelling"]');
    await p.waitForFunction(() => [...document.querySelectorAll('[aria-label="Advanced modelling"] button[aria-pressed="true"]')].some((b) => (b.textContent || '').trim() === 'Power Query'), { timeout: 15000 });
    assert.equal(await p.evaluate(() => document.querySelector('.area-tabs [aria-current="page"]')?.textContent.trim()), 'Advanced modelling',
      'Power Query lights Advanced modelling on the strip');
    await p.keyboard.press('Escape');
    assert.deepEqual(strip.errors, []);
    await p.close();
  }


  // --- one scene per area, at 1440, showing that area's second row -----------------------------------
  // Six shots plus the Advanced modelling fold open, written to SHELL_SCENES_DIR when it is set. They are
  // the evidence for the strip itself, which is the one thing a source-pattern test can describe but never
  // show: whether six areas and their rows actually FIT, and whether a row reads as a sentence or as a
  // pile of words. The block ASSERTS what each row contains before it shoots it, so a green run is not just
  // eight pictures nobody looked at, and it runs whether or not the shots are being kept.
  {
    const scenesDir = process.env.SHELL_SCENES_DIR;
    if (scenesDir) mkdirSync(scenesDir, { recursive: true });
    const scenes = await openPage('live=1&conn=1&tier=pro');
    const p = scenes.page;
    await p.setViewport({ width: 1440, height: 980, deviceScaleFactor: 2 });
    await p.waitForSelector('[data-testid="plan-page"]');
    // One Visual Language: every area scene is also measured at 1440x900 against its screen baseline, shot or not.
    const shoot = async (name) => {
      await checkScreen(p, `shell:${name}`);
      if (!scenesDir) return;
      await p.screenshot({ path: join(scenesDir, `${name}-chrome.png`), clip: { x: 0, y: 0, width: 1440, height: 104 } });
      await p.screenshot({ path: join(scenesDir, `${name}-page.png`) });
    };
    // The second row, as a person reads it: the area name, then either the segments or the tool's own name.
    const readSecondRow = () => p.evaluate(() => {
      const bar = document.querySelector('.studio-arearow');
      if (!bar) return { area: null, segments: [], tool: null };
      return {
        area: bar.querySelector('.studio-arearow-area')?.textContent.replace(/\s*›\s*$/, '').trim() ?? null,
        segments: [...bar.querySelectorAll('.area-tabs button')].map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()),
        tool: bar.querySelector('.studio-arearow-tool')?.textContent.trim() ?? null,
      };
    });
    const AREA_ROWS = [
      // [button, the area name on row two, the segments it must show, the tool name when it has no segments]
      // A one-tool area whose tool has the area's own name says it ONCE: null here, not 'Plan' again. Plan is
      // ONE tool from 23 September, so its area has no segment row at all: the pending changes are a section of
      // that page, the Model Spec is a panel on it, and since slice 11 History is a view on it too.
      ['Plan', null, [], null],
      // Ask your data is the Explore segment right after Data since 26 September (ask-data-page.test.mjs pins it).
      ['Explore', 'Explore', ['Data', 'Ask your data', 'Diagram', 'Lineage', 'Size'], null],
      ['Edit', 'Edit', ['Find and replace', 'Queries', 'AI instructions', 'Advanced modelling'], null],
      ['Review', 'Review', ['Model quality (BPA)', 'AI readiness', 'Validation', 'Docs'], null],
      // One tool since Edit History became the Plan's History (slice 11), so the row names it instead of a strip.
      ['Publish', null, [], null],
      ['Workflows', null, [], null],
    ];
    const headerAreas = await p.evaluate(() => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')]
      .map((b) => (b.textContent || '').trim()));
    assert.deepEqual(headerAreas, AREA_ROWS.map(([label]) => label), 'the header carries the six areas in strip order');
    for (const [index, [label, areaName, segments, tool]] of AREA_ROWS.entries()) {
      await clickText(p, label, 'nav[aria-label="Studio areas"]');
      // An area with one tool has no second row (8 Oct 2026), and a row that exists names no area: the lit header
      // button says it. So wait on the header button being lit, then read the row if there is one.
      await p.waitForFunction((want) => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')]
        .some((b) => b.getAttribute('aria-current') === 'page' && (b.textContent || '').trim().startsWith(want)), { timeout: 15000 }, label);
      await delay(600);
      const row = await readSecondRow();
      assert.equal(row.area, areaName, `${label}: row two names no area (the lit header button does)`);
      // A segment can carry a pill or a badge, so the comparison is on the label each segment STARTS with.
      const seen = row.segments.map((text, i) => (segments[i] && text.startsWith(segments[i]) ? segments[i] : text));
      assert.deepEqual(seen, segments, `${label}: row two shows exactly its own segments`);
      assert.equal(row.tool, tool, `${label}: the row does not name the tool a second time`);
      await shoot(`${String(index + 1).padStart(2, '0')}-${label.toLowerCase()}`);
    }
    // And the Advanced modelling page, whose own row lists Power Query and the six builders, in one order.
    await clickText(p, 'Edit', 'nav[aria-label="Studio areas"]');
    await p.waitForSelector('.area-tabs');
    await p.evaluate(() => [...document.querySelectorAll('.area-tabs button')]
      .find((b) => (b.textContent || '').trim().startsWith('Advanced modelling'))?.click());
    await p.waitForFunction(() => [...document.querySelectorAll('[aria-label="Advanced modelling"] button')].some((b) => (b.textContent || '').trim() === 'Power Query'), { timeout: 10000 });
    await delay(400);
    const rowItems = await p.evaluate(() => [...document.querySelector('[aria-label="Advanced modelling"]').querySelectorAll('button')].map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()));
    assert.deepEqual(rowItems, ['Power Query', 'Perspectives', 'Field parameters', 'Calculation groups', 'Calendars', 'RLS / OLS', 'DaxLib'],
      'the page row lists Power Query and the six builders (the order of the builders is lane 3 s to set: E3)');
    await shoot('08-edit-advanced-modelling');
    await clickText(p, 'Calendars', '[aria-label="Advanced modelling"]');
    // `.some`, not `.find` then compare: Validation is mounted for every open model and merely HIDDEN with a
    // css class, and its own Checks/Reports strip uses the same .sem-seg-item, so the first pressed segment in
    // the document belongs to a page nobody is looking at.
    await p.waitForFunction(() => [...document.querySelectorAll('.sem-seg-item')]
      .some((b) => b.getAttribute('aria-pressed') === 'true' && (b.textContent || '').trim() === 'Calendars'),
    { timeout: 15000 });
    await shoot('09-edit-advanced-modelling-calendars');
    // Validation's two tabs, because Reports left the strip and became one of them.
    await dispatch(p, { type: 'navigate', sessionId: 'uishot', tab: 'evidence' });
    await p.waitForFunction(() => document.querySelector('[data-page-tab="reports"]')?.getAttribute('aria-pressed') === 'true',
      { timeout: 15000 });
    assert.equal(await activeArea(p), 'Review', 'a link to Reports opens Review');
    assert.equal((await readSecondRow()).segments.find((t) => t.startsWith('Validation')), 'Validation',
      'and the strip marks Validation, because Reports is a tab on it');
    await shoot('10-review-validation-reports');
    assert.deepEqual(scenes.errors, []);
    await p.close();

    // The one Pro tab, on a plan that does not reach it. Workflows is an area with ONE tool, so it has no
    // segment strip to wear the pill and nothing on the free plan said it was paid at all; the AREA button
    // carries it now, and this is the shot that shows it. Every other area must stay unmarked: a mixed area
    // wearing a pill would be lying about the free half of itself.
    const free = await openPage('live=1&conn=1');
    await free.page.setViewport({ width: 1440, height: 980, deviceScaleFactor: 2 });
    await free.page.waitForSelector('[data-testid="plan-page"]');
    await free.page.waitForFunction(() => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')]
      .some((b) => (b.textContent || '').replace(/\s+/g, '').endsWith('Pro')), { timeout: 15000 });
    const marked = await free.page.evaluate(() => [...document.querySelectorAll('nav[aria-label="Studio areas"] button')]
      .filter((b) => (b.textContent || '').replace(/\s+/g, '').endsWith('Pro'))
      .map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()));
    assert.deepEqual(marked, ['WorkflowsPro'], 'Workflows is the only area marked Pro on a free plan');
    if (scenesDir) {
      await free.page.screenshot({ path: join(scenesDir, '11-free-workflows-pro-chrome.png'), clip: { x: 0, y: 0, width: 1440, height: 104 } });
      await free.page.screenshot({ path: join(scenesDir, '11-free-workflows-pro-page.png') });
    }
    assert.deepEqual(free.errors, []);
    await free.page.close();
  }

  // Loading, failure, an empty plan, and no-model startup are four distinct states of the start page (the Plan since
  // slice 11; the Overview's graph read was the one these held before).
  for (const [mode, expected] of [['loading', 'Reading the plan…'], ['error', 'The plan could not be read'], ['empty', 'Nothing to do on the plan'], ['nomodel', 'Open a model to begin']]) {
    const opened = await openPage(`home=${mode}`);
    await waitText(opened.page, expected);
    assert.deepEqual(opened.errors, []);
    await opened.page.close();
  }

  assert.deepEqual(screenFailures(), [], 'a page got worse than its screen-check baseline (tools/uishot/visual-baseline.json)');
  console.log('integrated shell journey passed: native relay, one-use seeds, draft preservation, session fences, aliases, publish, tool chooser, Settings Done (warm, Escape, cold hand-off, model swap), the strip on a page reached with an object in hand, start page states and the assistant keyboard journey');
} finally {
  await browser.close();
  await new Promise((done) => server.close(done));
}
