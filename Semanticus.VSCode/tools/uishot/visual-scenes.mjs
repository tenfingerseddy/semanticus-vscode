// The 22 Studio pages the screen check scores, and the server and browser to render them. Shared by
// visual-check.mjs (the scoreboard) and visual-audit-drive.mjs (the annotated shots). The scene list is the one
// the 23 September 2026 visual audit measured, moved here from that audit so the scoreboard and the baseline
// talk about the same 22 pages.
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
export const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
export const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };

export async function start() {
  if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) throw new Error('media/studio/studio.js is missing. Run `cd webview && npm run build` in Semanticus.VSCode first.');
  const server = createServer((req, res) => {
    const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
    if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
    res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
    res.end(readFileSync(p));
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  const browser = await puppeteer.launch({
    executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
    args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
  });
  return { server, browser, origin: `http://127.0.0.1:${port}`, base: `http://127.0.0.1:${port}/tools/uishot/harness.html` };
}

// The Plan page needs the Plan driver's engine seed. It is a template inside cp-plan-drive.mjs, which runs its
// whole journey on import, so the seed is lifted out of the source text and evaluated on its own.
export function planSeed(mode = 'plain', free = false) {
  const src = readFileSync(join(here, 'cp-plan-drive.mjs'), 'utf8');
  const a = src.indexOf('const seed = (');
  const end = '})()`;';
  const b = src.indexOf(end, a);
  if (a === -1 || b === -1) throw new Error('could not find the Plan seed in cp-plan-drive.mjs; the screen check needs `const seed = (` ... `})()`;` there');
  const fixture = readFileSync(join(here, 'fixtures', 'plan-heart-fixture.mjs'), 'utf8').replace(/^export /gm, '');
  // The History engine rides in too, so the plan-history scene draws the Plan's real History (lane R2) and not a
  // History with nothing behind it.
  const history = readFileSync(join(here, 'fixtures', 'plan-history-fixture.mjs'), 'utf8').replace(/^export /gm, '');
  const seed = new Function('FIXTURE_SRC', 'HISTORY_SRC', src.slice(a, b + end.length) + '\nreturn seed;')(fixture, history);
  return seed(mode, free);
}

const PRO = 'tier=pro&conn=1&live=1';
// A click that finds nothing leaves the scene on the page underneath, which is then measured and scored as if it
// were the state the scene names. Both of these say so instead: the ratified design is that a scene which cannot
// be put into its own state is reported as not measured, never as a pass.
const click = async (page, sel) => {
  const hit = await page.evaluate((s) => { const el = document.querySelector(s); if (!el) return false; el.click(); return true; }, sel);
  if (!hit) throw new Error(`nothing on the page matches ${sel}, so the scene was never put in its own state`);
};
const clickText = (page, sel, text) => page.evaluate((s, t) => { const el = [...document.querySelectorAll(s)].find((e) => e.textContent.trim().startsWith(t) && e.getClientRects().length); if (!el) return false; el.click(); return true; }, sel, text);
const clickTextOrThrow = async (page, sel, text) => {
  if (!await clickText(page, sel, text)) throw new Error(`no ${sel} reading "${text}" is on the page, so the scene was never put in its own state`);
};

// root: the page's own box when it is not <main>. prepare(page) finds it after render and returns a selector.
export const SCENES = [
  // The start page: no tab asked for, so this is what a person lands on. It replaced the Overview's scene when the
  // Overview retired in slice 11 (Plan-heart lane R1, contract 15), one for one, so the scoreboard stays 22 of 22.
  { id: 'plan-start', area: 'Plan', label: 'Plan, the start page', q: PRO, plan: true },
  { id: 'plan-list', area: 'Plan', label: 'Plan, the list', q: PRO + '&tab=plan', plan: true },
  { id: 'plan-item', area: 'Plan', label: 'Plan, an item open', q: PRO + '&tab=plan', plan: true,
    run: async (p) => { await click(p, '[data-testid="plan-row-open-jev.describe.table:Product"]'); await wait(900); },
    prepare: async (p) => { await p.evaluate(() => { const r = document.querySelector('[data-testid="plan-row-jev.describe.table:Product"]'); const m = document.querySelector('main'); if (r && m) m.scrollTop += r.getBoundingClientRect().top - m.getBoundingClientRect().top - 150; }); await wait(200); return null; } },
  { id: 'explore-data', area: 'Explore', label: 'Explore, Data', q: PRO + '&tab=data' },
  { id: 'explore-diagram', area: 'Explore', label: 'Explore, Diagram', q: PRO + '&tab=diagram' },
  { id: 'explore-lineage', area: 'Explore', label: 'Explore, Lineage', q: PRO + '&tab=lineage' },
  { id: 'explore-size', area: 'Explore', label: 'Explore, Size by table', q: PRO + '&tab=stats' },
  { id: 'edit-queries', area: 'Edit', label: 'Edit, Queries', q: PRO + '&tab=daxlab' },
  { id: 'edit-instructions', area: 'Edit', label: 'Edit, AI instructions', q: PRO + '&tab=instructions' },
  { id: 'edit-advanced', area: 'Edit', label: 'Edit, Advanced modelling', q: PRO + '&tab=advmodels' },
  { id: 'edit-pq', area: 'Edit', label: 'Edit, Power Query', q: PRO + '&tab=mcode' },
  { id: 'review-bpa', area: 'Review', label: 'Review, Model quality', q: PRO + '&tab=bpa' },
  { id: 'review-readiness', area: 'Review', label: 'Review, AI readiness', q: PRO + '&tab=readiness' },
  { id: 'review-tests', area: 'Review', label: 'Review, Validation', q: PRO + '&tab=tests' },
  { id: 'review-docs', area: 'Review', label: 'Review, Docs', q: PRO + '&tab=docs' },
  { id: 'publish', area: 'Publish', label: 'Publish, Published', q: PRO + '&tab=deploy' },
  // The old Edit History id, which lands on the Plan's History view (lane R2). It replaced publish-history one for one.
  { id: 'plan-history', area: 'Plan', label: 'Plan, History', q: PRO + '&tab=history', plan: true },
  { id: 'workflows', area: 'Workflows', label: 'Workflows', q: PRO + '&tab=workflows' },
  { id: 'connections', area: 'Connections', label: 'Connections', q: 'view=connections&tier=pro&conn=1', prepare: connectionsRoot },
  { id: 'pro', area: 'Pro', label: 'The Pro page', q: 'conn=1&live=1', root: '[data-testid="pro-page"]',
    run: async (p) => { await click(p, '[data-testid="pro-chip"]'); await p.waitForSelector('[data-testid="pro-page"]', { timeout: 8000 }); await wait(500); } },
  // The start page's help, which since slice 9 is the Plan's (its Pro note and tip used to stack 16 px apart).
  { id: 'help', area: 'Help', label: 'Help for this page', q: PRO, prepare: helpRoot,
    run: async (p) => { await click(p, 'button[aria-label="Help"]'); await wait(300); await clickTextOrThrow(p, '[role="menu"] button, [role="menuitem"]', 'Help for this page'); await wait(700); } },
];

// The standalone Connections hub has no <main>: its root is the hub's frame. Slice C (lane C2) made it one page, so
// the widest box under the centre is now the page column inside the frame's margins, not the frame; the frame is
// named directly, and the old walk stays as the fallback.
export async function connectionsRoot(p) {
  return p.evaluate(() => {
    // The walk is the FALLBACK, which is what the line above has always said and what the code did not do: it ran on
    // the named frame too, and at a 900 px window the frame is 828 px, so the walk climbed off the top of the
    // document and the scene was measured against <body>. The hub's own scroll region then stopped counting as the
    // page's, and every control below the fold of a 30-model list read as an action hidden in a box.
    let e = document.querySelector('.hub-frame');
    if (!e) { e = document.elementFromPoint(innerWidth / 2, innerHeight / 2); while (e && e.getBoundingClientRect().width < 1000) e = e.parentElement; }
    if (!e) return null;
    e.setAttribute('data-screen-root', 'connections');
    return '[data-screen-root="connections"]';
  });
}

// Help for this page is a dialog over the page; its root is the dialog box.
export async function helpRoot(p) {
  return p.evaluate(() => {
    const d = document.querySelector('.fixed.inset-0.z-50 > div');
    if (!d) return null;
    d.setAttribute('data-screen-root', 'help');
    return '[data-screen-root="help"]';
  });
}

// Open one scene on a fresh page at 1440 x 900, ready to measure.
export async function openScene(browser, base, s) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  if (s.plan) await page.evaluateOnNewDocument(planSeed());
  if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
  await page.goto(s.page ? base.replace('harness.html', s.page) : `${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 45000 });
  await wait(2600);
  if (s.run) await s.run(page);
  await wait(400);
  return { page, errors };
}

// ---- the space scenes ------------------------------------------------------------------------------------
// The space check (screencheck.mjs checkSpace) scores these at 2000 x 1200, 1440 x 900 and 900 x 900. They are
// the 22 pages plus the sub-pages the 27 September space audit measured, with FULL-LENGTH fixtures: the stock
// 483-character instructions and nine saved models made AI instructions and Connections look fine, so a short
// fixture hides the very faults this family exists to catch.
export const LONG_INSTRUCTIONS = ['Northstar Retail sells outdoor gear in 14 countries. Answer as a careful finance analyst.',
  'The fiscal year starts on 1 July. When someone says "this year" use the fiscal year unless they say calendar year.',
  '"Sales" means net sales after returns and discounts. Use [Total Sales] for it, never [Gross Sales].',
  'Prefer Net Revenue for financial questions. Qualify results by region when the question does not name one.',
  'Dates come from the Date table only. Budget figures are monthly and must not be added up across years.',
  'Margin is [Gross Margin %]. Never average a percentage across stores; recompute it from the totals.',
  'A "customer" is an account in the Customer table with at least one order. Prospects are not customers.',
  'Store counts use [Open Stores], which excludes stores closed before the period starts.',
  'Stock figures are snapshots at month end. Never add stock up across months; take the last value.',
  'Currency: report in AUD unless the question names another. Use the Rates table at the month-end rate.',
  'Regions are APAC, EMEA and Americas. "The US" means the Americas region, country United States only.',
  'Say when a number is a forecast rather than an actual. Forecast rows have Scenario = "Forecast".',
  'If a question needs data the model does not have (for example web traffic), say so plainly. Do not guess.',
  'Round money to whole dollars and percentages to one decimal place.',
  'Returns are a negative quantity on the original order line. Never count a return as a second sale.',
  'Discounts sit in [Discount Amount] and are already taken out of net sales. Do not subtract them twice.',
  'A store belongs to one region for its whole life. Region changes are handled as a close and a re-open.',
  'The Employee table is people, not headcount: one row per person, including leavers. Filter on IsActive.',
  'Online and store sales share the Sales table. Channel tells them apart; "retail" means the store channel.',
  'When a question asks for a trend, give twelve months and say which months are incomplete.'].join('\n\n');
function longInstructions(text) {
  window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { aiInstructions: () => ({ present: true, instructions: text, length: text.length, limit: 2000, culture: 'en-US', note: null }) });
}
// The full-length fixtures the audit asked for. A short one hides the very fault its scene exists to catch: three
// packages made DaxLib's two short boxes look deliberate, two perspectives made a quarter-width tick grid look
// full, and a one-sentence narrative made the Docs writing boxes look big enough. Each is seeded per scene, so
// every other driver keeps the harness's own short defaults.
function manyPackages(n) {
  const rows = [];
  for (let i = 0; i < n; i++) rows.push({ id: 'Contoso.Package' + i, version: '1.' + i + '.0', description: 'Helper functions for area ' + i + ' of the finance model.', authors: ['Contoso BI'], downloads: 1000 - i });
  window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { daxLibSearch: rows });
}
function manyPerspectives(n) {
  const names = ['Finance', 'Sales Ops', 'Marketing', 'Supply chain', 'Executive', 'Store managers', 'Data science', 'Audit'];
  const rows = [];
  for (let i = 0; i < n; i++) rows.push({ ref: 'perspective:' + names[i % names.length], name: names[i % names.length], description: '', members: i % 2 ? ['table:Sales', 'measure:Sales/Total Sales'] : ['table:Customer', 'column:Customer/CustomerName'] });
  window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { perspectives: rows });
}
function longNarrative(text) {
  window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { narrative: text });
}
export const LONG_NARRATIVE = ['Grain is **one row per order line**. Revenue is recognised at ship date, so a line ordered in June and shipped in July belongs to July.',
  'Returns come back as a negative quantity on the original line, which keeps the customer and the product with the return.',
  'Use `[Net Revenue]` for anything a finance person will read. `[Gross Sales]` is before returns and discounts and is kept only for the warehouse reconciliation.',
  'Discounts are already taken out of net revenue. Subtracting `[Discount Amount]` again is the most common mistake made with this table.',
  'The channel column splits online from store. "Retail" in a question almost always means the store channel, not both.',
  'Budget rows are monthly and are not additive across years, because each year is re-based. Compare a year to its own budget only.'].join('\n\n');

// Thirty saved models, so a Connections window that does not grow shows its list scrolling.
function manyConnections(n) {
  const ago = (m) => new Date(Date.now() - m * 60000).toISOString();
  const rows = [];
  for (let i = 0; i < n; i++) rows.push({ id: 'cs' + i, kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Workspace' + i, database: 'Model ' + i, modelName: 'Sales model ' + (i + 1), authMode: 'interactive', label: ['prod', 'uat', 'dev', null][i % 4], tenantId: 'contoso.com', lastAccount: 'megan@contoso.com', useCount: n - i, lastUsedUtc: ago(15 * (i + 1)) });
  window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { connections: rows });
}
// A scene that could not be put into the state it names is NOT MEASURED, and the ratified design says it is never
// reported as a pass. Ignoring what clickText returned meant a renamed or hidden switch simply re-measured the tab
// underneath and scored it clean, which is how a scene can quietly go on proving nothing after a rename.
const seg = (label) => async (p) => {
  if (!await clickText(p, 'button', label)) throw new Error(`the "${label}" switch this scene needs is not on the page, so the scene was never put in its own state`);
  await wait(900);
};
// The narrative column opens on the model, whose narrative is the harness's one sentence; the full-length one is
// seeded on the tables. Picking the seeded table is what puts the long text in a writing box, which is the whole
// point of the scene: without it the boxes hold 110 characters and the fix they exist to guard cannot be undone
// visibly.
const pickSeededNarrative = async (p) => {
  const picked = await p.evaluate(() => {
    const sel = [...document.querySelectorAll('aside.sem-card')].pop()?.querySelector('select');
    const option = sel && [...sel.options].find((o) => /Sales/.test(o.textContent));
    if (!option) return null;
    sel.value = option.value;
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return option.textContent;
  });
  if (!picked) throw new Error('the Docs narrative picker has no Sales table, so the scene was never put in its own state');
  await wait(1200);
};
const LONG = [longInstructions, LONG_INSTRUCTIONS];
export const SPACE_SCENES = [
  ...SCENES.map((s) => s.id === 'edit-instructions' ? { ...s, init: LONG }
    : s.id === 'connections' ? { ...s, init: [manyConnections, 30] }
    : s.id === 'review-docs' ? { ...s, init: [longNarrative, LONG_NARRATIVE], run: pickSeededNarrative }
    : s.id === 'edit-advanced' ? { ...s, init: [manyPerspectives, 8] } : s),
  { id: 'edit-instructions-editing', area: 'Edit', label: 'Edit, AI instructions, editing', q: PRO + '&tab=instructions', init: LONG,
    run: async (p) => {
      // The exact word: the card's other buttons may start with it. A miss throws like every other scene's click,
      // or the read view would be measured and scored as the editing state.
      const hit = await p.evaluate(() => { const b = [...document.querySelectorAll('[data-testid=overview-instructions] button')].find((x) => x.textContent.trim() === 'Edit'); if (!b) return false; b.click(); return true; });
      if (!hit) throw new Error('no "Edit" button is on the AI instructions card, so the scene was never put in its own state');
      await wait(900);
    } },
  { id: 'plan-pending', area: 'Plan', label: 'Plan, Pending changes', q: PRO + '&tab=optimize', plan: true },
  ...['Field parameters', 'Calculation groups', 'Calendars', 'RLS / OLS', 'DaxLib'].map((l) => ({ id: 'adv-' + l.toLowerCase().replace(/[^a-z]+/g, ''), area: 'Edit', label: 'Advanced modelling, ' + l, q: PRO + '&tab=advmodels', run: seg(l) })),
  // DaxLib twice, because its two faults live at opposite ends of the fixture. The scene above keeps the harness's
  // three packages, the state the audit measured and the one that showed two short boxes with 601 px of blank page
  // under them. This one is the full-length feed the audit asked for: with three packages the check cannot catch a
  // revert, and only a long feed can push "Installed in this model" off the bottom.
  { id: 'adv-daxlib-long', area: 'Edit', label: 'Advanced modelling, DaxLib, a full feed', q: PRO + '&tab=advmodels', run: seg('DaxLib'), init: [manyPackages, 30] },
  { id: 'settings', area: 'Settings', label: 'Settings, assistant permissions', q: PRO + '&tab=permissions' },
  { id: 'propgrid-long', area: 'Properties', label: 'Properties grid, long description', page: 'propgrid.html?s=measure&desc=long', root: 'body' },
  // The ratified design asked for about 45 scenes, not the 22 pages and their obvious sub-pages. These are the
  // states a person actually lands in once they start working: a view switched, a result run, a panel opened. Each
  // one lays out differently from the page it starts on, which is the whole reason a space fault hides at one size
  // and shows at another, and none of them was measured before.
  { id: 'instructions-notes', area: 'Edit', label: 'AI instructions, the Notes tab', q: PRO + '&tab=instructions', run: seg('Notes') },
  { id: 'edit-queries-result', area: 'Edit', label: 'Edit, Queries, a query run', q: PRO + '&tab=daxlab', run: seg('Run') },
  // The Queries redesign (m11/queries-redesign) named the second result view "Speed" and made the editor part of the page.
  { id: 'edit-queries-performance', area: 'Edit', label: 'Edit, Queries, the speed view', q: PRO + '&tab=daxlab',
    run: async (p) => { await seg('Run')(p); await seg('Speed')(p); } },
  { id: 'edit-pq-profile', area: 'Edit', label: 'Edit, Power Query, column profiles', q: PRO + '&tab=mcode', run: seg('Profile columns') },
  // There is no "Query" switch any more: the editor is on the page as it opens, so this scene is the page in that state
  // (kept under its id so the baseline keeps its 45 scenes).
  { id: 'edit-queries-query', area: 'Edit', label: 'Edit, Queries, the query editor as the page opens', q: PRO + '&tab=daxlab' },
  { id: 'explore-diagram-relationships', area: 'Explore', label: 'Explore, Diagram, the relationship list', q: PRO + '&tab=diagram', run: seg('Relationships') },
  { id: 'explore-diagram-vertical', area: 'Explore', label: 'Explore, Diagram, the vertical layout', q: PRO + '&tab=diagram', run: seg('Vertical') },
  { id: 'explore-lineage-graph', area: 'Explore', label: 'Explore, Lineage, the graph', q: PRO + '&tab=lineage', run: seg('Graph') },
  { id: 'explore-lineage-impact', area: 'Explore', label: 'Explore, Lineage, impact', q: PRO + '&tab=lineage', run: seg('Impact') },
  { id: 'review-bpa-warnings', area: 'Review', label: 'Review, Model quality, warnings only', q: PRO + '&tab=bpa', run: seg('Warnings') },
  { id: 'review-tests-reports', area: 'Review', label: 'Review, Validation, the Reports tab', q: PRO + '&tab=tests', run: seg('Reports') },
  { id: 'publish-earlier-versions', area: 'Publish', label: 'Publish, Earlier versions', q: PRO + '&tab=deploy', run: seg('Earlier versions') },
];
