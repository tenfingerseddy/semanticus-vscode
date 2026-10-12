// Browser-driven proof of History inside the Plan (Plan-heart lane U2, mockup plan-heart.html section 4).
//
// It builds plan-history.tsx on its own with this worktree's Vite, mounts it in the real screenshot harness (the
// same mock engine seam every driver uses), drives it with real clicks, and MEASURES what landed. The Plan page
// that hosts it belongs to lane U1, so this driver does not wait on that page: it proves the History itself. Once
// U1 mounts it, the Studio scenes reach it through the Plan's History view.
//
// The engine stand-in is tools/uishot/fixtures/plan-history-fixture.mjs, shared with test/plan-history.test.mjs,
// and every call the page makes is recorded, so "Undo to here sent ONE undo call with toEntry" is a measurement in
// results.json and not a sentence in a report.
//
// Scenes follow the mockup's Play steps: open an edit, undo to here, restore (the question, then the result),
// filter by who, reset. Then the rest of the surface: the export menu, a red-check publish, a broken record, a
// model outside git, and an empty model. Every scene is shot in Dark Modern, Light Modern and Dark High Contrast.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/cp-plan-history-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
const webview = join(webRoot, 'webview');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'plan-history');
mkdirSync(OUT, { recursive: true });

// ---- build the History on its own -------------------------------------------------------------------------
const BUILD = join(process.env.TMPDIR || tmpdir(), 'semanticus-ph-build-' + process.pid);
mkdirSync(BUILD, { recursive: true });
const ENTRY = join(BUILD, 'entry.tsx');
const src = (f) => JSON.stringify(join(webview, 'src', f));
writeFileSync(ENTRY, `
import ${src('tokens.css')};
import ${src('styles.css')};
import { createRoot } from 'react-dom/client';
import { PlanHistory } from ${src('plan-history')};
const q = new URLSearchParams(location.search);
createRoot(document.getElementById('root')).render(
  <main className="sem-page"><h1 className="sem-page-title">History</h1>
    <PlanHistory objectRef={q.get('object')} onGo={(tab) => { (window as any).__phGo = tab; }} /></main>);
`);
const wreq = createRequire(join(webview, 'package.json'));
const vite = await import(pathToFileURL(wreq.resolve('vite')).href);
const react = (await import(pathToFileURL(wreq.resolve('@vitejs/plugin-react')).href)).default;
const tailwind = (await import(pathToFileURL(wreq.resolve('@tailwindcss/vite')).href)).default;
await vite.build({
  configFile: false, root: webview, base: './', logLevel: 'warn',
  plugins: [react(), tailwind()],
  resolve: { alias: [
    { find: /^react$/, replacement: join(webview, 'node_modules', 'react') },
    { find: /^react\//, replacement: join(webview, 'node_modules', 'react') + '/' },
    { find: /^react-dom\//, replacement: join(webview, 'node_modules', 'react-dom') + '/' },
  ] },
  build: {
    outDir: join(BUILD, 'out'), emptyOutDir: true, cssCodeSplit: false, target: 'es2020', minify: false,
    rollupOptions: { input: ENTRY, output: { format: 'es', entryFileNames: 'ph.js', chunkFileNames: 'ph-[name].js', assetFileNames: 'ph.[ext]' } },
  },
});

// The harness page, pointed at this bundle instead of the whole Studio.
const HARNESS = readFileSync(join(here, 'harness.html'), 'utf8')
  .replace('<link rel="stylesheet" href="../../media/studio/studio.css" />', '<link rel="stylesheet" href="/__ph/ph.css" />')
  .replace('<script type="module" src="../../media/studio/studio.js"></script>', '<script type="module" src="/__ph/ph.js"></script>');
if (!HARNESS.includes('/__ph/ph.js') || !HARNESS.includes('/__ph/ph.css')) throw new Error('harness.html changed: the bundle could not be swapped in');

const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-history-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = decodeURIComponent((req.url || '/').split('?')[0].split('#')[0]);
  if (url === '/ph.html') { res.writeHead(200, { 'content-type': 'text/html' }); res.end(HARNESS); return; }
  const base = url.startsWith('/__ph/') ? join(BUILD, 'out') : webRoot;
  const p = normalize(join(base, url.replace(/^\/__ph\//, '/')));
  if (!p.startsWith(base) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// The stub rides in on the setter for __uishotHarness, before the bundle's first call. Every call is recorded.
const seed = (options) => `
${FIXTURE_SRC}
Date.now = () => HISTORY_FIXED_NOW;
window.__ph = makeHistoryEngine(${JSON.stringify(options)});
window.__phCalls = [];
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  const rec = (name, fn) => v.setResponse(name, (params) => { window.__phCalls.push({ call: name, params }); return fn(...params); });
  rec('getPlanHistory', window.__ph.getPlanHistory);
  rec('listVerifiedEdits', window.__ph.listVerifiedEdits);
  rec('listHistoryCheckpoints', window.__ph.listHistoryCheckpoints);
  rec('undo', window.__ph.undo);
  rec('redo', window.__ph.redo);
  rec('createHistoryCheckpoint', window.__ph.createHistoryCheckpoint);
  rec('restoreHistoryCheckpoint', window.__ph.restoreHistoryCheckpoint);
  rec('exportVerifiedEdits', window.__ph.exportVerifiedEdits);
} });`;

// One read of the page, in the page's own DOM.
const MEASURE = `(() => {
  const text = (el) => el ? el.innerText.replace(/\\n+/g, ' | ').trim() : null;
  const rows = [...document.querySelectorAll('[data-hid]')].map((r) => ({
    id: r.dataset.hid, kind: r.dataset.kind, undone: r.classList.contains('ph-undone'), selected: r.classList.contains('ph-sel'),
    kid: r.classList.contains('ph-kid'), text: text(r),
    struck: getComputedStyle(r.querySelector('.ph-title, .ph-kidtitle') || r).textDecorationLine,
  }));
  return {
    rows,
    seams: [...document.querySelectorAll('.ph-seam')].map(text),
    who: [...document.querySelectorAll('[data-testid^="ph-who-"]')].map((b) => ({ label: b.textContent, pressed: b.getAttribute('aria-pressed') })),
    detail: text(document.querySelector('[data-testid="ph-detail"]')),
    confirms: document.querySelectorAll('[data-testid="ph-restore-confirm"]').length,
    confirm: text(document.querySelector('[data-testid="ph-restore-confirm"]')),
    bar: text(document.querySelector('[data-testid="ph-undone-bar"]')),
    menu: text(document.querySelector('[data-testid="ph-menu"]')),
    msg: text(document.querySelector('[data-testid="ph-msg"]')),
    msgTone: document.querySelector('[data-testid="ph-msg"]')?.getAttribute('data-tone') ?? null,
    broken: text(document.querySelector('[data-testid="ph-broken"]')),
    nogit: text(document.querySelector('[data-testid="ph-nogit"]')),
    empty: text(document.querySelector('[data-testid="ph-empty"]')),
    intact: text(document.querySelector('[data-testid="ph-intact"]')),
    save: !!document.querySelector('[data-testid="ph-save"]'),
    overflowX: document.documentElement.scrollWidth > document.documentElement.clientWidth,
    calls: window.__phCalls.filter((c) => !['getPlanHistory', 'listVerifiedEdits', 'listHistoryCheckpoints'].includes(c.call)),
  };
})()`;

const results = {};
const failures = [];
const expect = (cond, what) => { if (!cond) failures.push(what); };

async function open(options = {}, query = '') {
  const page = await browser.newPage();
  page.on('pageerror', (e) => failures.push('page error: ' + e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(options));
  await page.goto(`http://127.0.0.1:${port}/ph.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-history"]', { timeout: 20000 });
  await wait(400);
  return page;
}
const click = async (page, sel) => { await page.click(sel); await wait(450); };
const clickText = async (page, text) => {
  const ok = await page.evaluate((t) => {
    const b = [...document.querySelectorAll('button')].find((x) => x.textContent.trim() === t);
    if (b) b.click(); return !!b;
  }, text);
  if (!ok) failures.push('no button reads ' + text);
  await wait(450);
};

// A floating menu is a layer, not page layout: it wears Studio's shared .compact-menu, which every Studio menu uses
// and which the page rules (card padding, the 12 columns) were never written for. So the menu scene is SHOT open
// and MEASURED closed; `beforeCheck` closes it.
async function shoot(page, name, { beforeCheck = null } = {}) {
  // A hover is not a state the page is in: the pointer leaves before the page is read and shot.
  await page.mouse.move(1, 1);
  await wait(150);
  const m = await page.evaluate(MEASURE);
  results[name] = m;
  expect(!m.overflowX, `${name}: the page scrolls sideways`);
  for (const t of ['dark', 'light', 'hc']) {
    await applyTheme(page, t);
    await wait(150);
    await page.screenshot({ path: join(OUT, `${t}-${name}.png`), fullPage: true });
  }
  await applyTheme(page, null);
  if (beforeCheck) await beforeCheck(page);
  await checkScreen(page, 'plan-history-' + name);
  return m;
}

// 0. An edit of an earlier session the file never held (join 8, K4): it says "Not saved" in its row and in its open detail, and it is not struck through.
{
  const page = await open();
  await click(page, '[data-hid="ed-000000d0"]');
  const m = await shoot(page, 'notsaved');
  expect(/Not saved · Your own edit/.test(m.rows.map((r) => r.text).join(' ')), 'notsaved: the row says it was not saved');
  expect(/This change was never saved, so the model on disk does not have it\./.test(m.detail ?? ''), `notsaved: the detail says what is true: "${m.detail}"`);
  expect(!m.rows.some((r) => r.id === 'ed-000000d0' && r.undone), 'notsaved: it is not struck through as if it were undone');
  await page.close();
}

// 1. Open an edit: an edit under its plan item, with who, before and after, Verified and the undo sentence.
{
  const page = await open();
  await click(page, '[data-hid="plan_item:describe/customer"] .ph-caret');
  await click(page, '[data-hid="ed-00000006"]');
  const m = await shoot(page, 'detail');
  expect(m.who.map((w) => w.label).join('|') === 'Everyone|You|Your assistant|Semanticus|Workflows', 'detail: the who filter is the five choices');
  expect(/Customer\[Tier\] · description/.test(m.detail ?? ''), 'detail: the edit title');
  expect(/Object \| Change \| Before \| After/.test(m.detail ?? '') || /Object\s+Change\s+Before\s+After/.test(m.detail ?? ''), 'detail: the before and after table');
  expect(/Verified\./.test(m.detail ?? ''), 'detail: Verified as a word');
  expect(/Takes back this and 5 later changes\./.test(m.detail ?? ''), 'detail: the undo count sentence');
  expect(m.seams.length === 1 && /From an earlier visit/.test(m.seams[0]), 'detail: the Before this session seam');
  await page.close();
}

// 2. Undo to here on the fourth row: ONE undo call with toEntry, and the four rows strike through together.
{
  const page = await open();
  const before = await page.evaluate(MEASURE);
  const fourth = before.rows.filter((r) => !r.kid)[3];
  expect(fourth.id === 'plan_item:describe/customer', 'undo: the fourth row is Describe 4 Customer columns');
  await click(page, `[data-testid="ph-undo-${fourth.id}"]`);
  await click(page, '[data-hid="workflow_run:run-7f3a"] .ph-caret');
  await click(page, '[data-hid="ed-00000003"]');
  const m = await shoot(page, 'undo');
  const undos = m.calls.filter((c) => c.call === 'undo');
  expect(undos.length === 1 && undos[0].params[0] === 'human' && undos[0].params[1] === fourth.id, `undo: one undo call with toEntry ${fourth.id}, got ${JSON.stringify(undos)}`);
  const tops = m.rows.filter((r) => !r.kid);
  expect(tops.slice(0, 4).every((r) => r.undone && r.struck.includes('line-through')), 'undo: the four rows strike through together');
  expect(!tops[4].undone, 'undo: the fifth row is untouched');
  expect(/9 changes undone\. They come back with Redo until you make a new change\./.test(m.bar ?? ''), 'undo: the undone bar with Redo all');
  results.undoCalls = undos;
  // Redo all brings everything back in one redo call.
  await click(page, '[data-testid="ph-redo-all"]');
  const r = await page.evaluate(MEASURE);
  const redos = r.calls.filter((c) => c.call === 'redo');
  expect(redos.length === 1 && redos[0].params[1] === 'ed-00000001', 'undo: Redo all is one redo call to the newest undone edit');
  expect(r.rows.every((x) => !x.undone) && !r.bar, 'undo: after Redo all nothing is struck through');
  await page.close();
}

// 3. Restore: exactly one question, then the restored timeline with both seams.
{
  const page = await open();
  await click(page, '[data-testid="ph-restore-ed-000000c1"]');
  const m = await shoot(page, 'restore');
  expect(m.confirms === 1, 'restore: exactly one confirmation');
  expect(/Restore Before AI prep, saved at 10:30\?/.test(m.confirm ?? ''), 'restore: the question names the version and its time');
  expect(m.calls.filter((c) => c.call === 'restoreHistoryCheckpoint').map((c) => c.params[1]).join() === 'false', 'restore: the dry run is asked first, nothing restored yet');
  // Asking about a second version replaces the question.
  await click(page, '[data-testid="ph-restore-ed-000000e0"]');
  const again = await page.evaluate(MEASURE);
  expect(again.confirms === 1 && /Friday handoff/.test(again.confirm ?? ''), 'restore: a second Restore replaces the question, never adds one');
  await click(page, '[data-testid="ph-restore-ed-000000c1"]');
  await clickText(page, 'Restore this version');
  const done = await shoot(page, 'restored');
  expect(done.calls.filter((c) => c.call === 'restoreHistoryCheckpoint' && c.params[1] === true).length === 1, 'restored: one restore');
  expect(done.rows[0].kind === 'restored', 'restored: the Restored flag row is on top');
  expect(done.seams.length === 2 && /Before the restore/.test(done.seams[0]) && /From an earlier visit/.test(done.seams[1]), 'restored: both seams, restore first');
  expect(!done.rows.some((r) => r.kind === 'edit' || r.kind === 'item') || !/Undo to here/.test(done.rows.map((r) => r.text).join(' ')), 'restored: nothing older offers Undo to here');
  await page.close();
}

// 3b. A restore that half succeeds (Codex F-283): the files came back but the checkpoint commit failed. The
// engine answers restored and an error together. The question closes, since the restore happened and pressing
// Restore again would save and restore a second time, and the error shows as a warning.
{
  const page = await open({ commitFails: true });
  await click(page, '[data-testid="ph-restore-ed-000000c1"]');
  await clickText(page, 'Restore this version');
  const m = await shoot(page, 'restore-partial');
  expect(m.calls.filter((c) => c.call === 'restoreHistoryCheckpoint' && c.params[1] === true).length === 1, 'partial: one restore');
  expect(m.confirms === 0, `partial: the question closes once the files are restored, saw ${m.confirms}`);
  expect(!/Restore this version/.test(m.rows.map((r) => r.text).join(' ')), 'partial: Restore this version is not offered again');
  expect(/were restored and reopened, but the restored state was not committed/.test(m.msg ?? ''), 'partial: the error is said');
  expect(m.msgTone === 'warn', `partial: as a warning, got ${m.msgTone}`);
  await page.close();
}

// 4. Filter by who: Your assistant, with an edit open.
{
  const page = await open();
  await click(page, '[data-testid="ph-who-assistant"]');
  await click(page, '[data-hid="plan_item:describe/customer"] .ph-caret');
  await click(page, '[data-hid="ed-00000006"]');
  const m = await shoot(page, 'who');
  const asked = await page.evaluate(() => window.__phCalls.filter((c) => c.call === 'getPlanHistory').map((c) => c.params[0]));
  expect(asked.includes('assistant'), 'who: the filter asks the engine for by = assistant');
  expect(m.rows.filter((r) => !r.kid).every((r) => /Your assistant/.test(r.text)), 'who: only your assistant\'s rows');
  await page.close();
}

// 5. Reset: the page as it first opens.
{
  const page = await open();
  await shoot(page, 'reset');
  await page.close();
}

// The rest of the surface.
{
  const page = await open();
  await click(page, '[data-testid="ph-more"]');
  const m = await shoot(page, 'menu', { beforeCheck: (p) => click(p, '[data-testid="ph-more"]') });
  await click(page, '[data-testid="ph-more"]');
  expect(/Export as Markdown \| Export as JSON \| Published versions are in Publish/.test(m.menu ?? ''), 'menu: export in a menu, and where published versions went');
  await clickText(page, 'Export as JSON');
  const e = await page.evaluate(MEASURE);
  expect(e.calls.some((c) => c.call === 'exportVerifiedEdits' && c.params[0] === 'json'), 'menu: Export as JSON asks the engine for JSON');
  await page.close();
}
{
  const page = await open({ redCheck: true });
  await click(page, '[data-hid="record:42"]');
  const m = await shoot(page, 'redcheck');
  const row = m.rows.find((r) => r.id === 'record:42');
  expect(row && /Published with a red check/.test(row.text) && /The board pack goes out at noon/.test(row.text), 'redcheck: one row, the reason word for word');
  await page.close();
}
{
  const page = await open({ broken: true });
  const m = await shoot(page, 'broken');
  expect(/broken at record 40/.test(m.broken ?? ''), 'broken: a loud warning when the record is broken');
  expect(!m.intact, 'broken: no "intact" line beside it');
  await page.close();
}
{
  const page = await open({ noGit: true });
  const m = await shoot(page, 'nogit');
  expect(!m.nogit, 'nogit: no sentence about git (undo still works and nobody asked for versions)');
  expect(!m.save, 'nogit: no Save a version button');
  await page.close();
}
{
  const page = await open({ empty: true });
  const m = await shoot(page, 'empty');
  expect(/No changes yet\./.test(m.empty ?? ''), 'empty: one line');
  await page.close();
}
{
  const page = await open({}, 'object=' + encodeURIComponent('measure:Sales/Total Sales'));
  const m = await shoot(page, 'object');
  const asked = await page.evaluate(() => window.__phCalls.filter((c) => c.call === 'getPlanHistory').map((c) => c.params[1]));
  expect(asked.includes('measure:Sales/Total Sales'), 'object: History for one object asks the engine with objectRef');
  expect(m.rows.filter((r) => !r.kid).length === 1, 'object: only the rename touched Total Sales');
  await page.close();
}

// Journey m11 F15. In VS Code the Studio is often about 790px wide. There, the fixed columns left the title no room:
// with an entry open the list showed only time and who, and Undo to here and "+1 later" ran over the divider into
// the details. Without the pane, titles were cut short while half the row stood empty. Measured at 792px, in the
// page's own boxes: every title keeps real room, and nothing in the list crosses the list's right edge.
const narrowRead = (page) => page.evaluate(() => {
  const list = document.querySelector('.ph-list').getBoundingClientRect();
  const rows = [...document.querySelectorAll('.ph-row:not(.ph-kid)')].map((r) => {
    const t = r.querySelector('.ph-title');
    const tb = t ? t.getBoundingClientRect() : null;
    return { id: r.dataset.hid, row: Math.round(r.getBoundingClientRect().width), title: tb ? Math.round(tb.width) : null,
      cut: t ? t.scrollWidth > t.clientWidth + 1 : false, need: t ? t.scrollWidth : 0 };
  });
  const spill = [...document.querySelectorAll('.ph-list .ph-row *')].filter((e) => e.getClientRects().length
    && e.getBoundingClientRect().right > list.right + 0.5).map((e) => `${e.className || e.tagName} ${(e.textContent || '').trim().slice(0, 20)}`);
  return { listWidth: Math.round(list.width), rows, spill, overflowX: document.documentElement.scrollWidth > document.documentElement.clientWidth };
});
const narrowCheck = (name, m) => {
  expect(!m.overflowX, `${name}: the page scrolls sideways`);
  expect(m.spill.length === 0, `${name}: things cross the list's right edge: ${m.spill.slice(0, 4).join(' | ')}`);
  for (const r of m.rows) {
    if (r.title == null) continue;
    // A title that fits shows whole; one that is cut still gets at least 40% of its row, never a sliver.
    expect(!r.cut || r.title >= 0.4 * r.row, `${name}: ${r.id} title cut to ${r.title}px of a ${r.row}px row (needs ${r.need})`);
  }
};
{
  const page = await open();
  await page.setViewport({ width: 792, height: 900, deviceScaleFactor: 1 });
  await wait(400);
  const plain = await narrowRead(page);
  results['narrow-list'] = plain;
  narrowCheck('narrow-list', plain);
  await page.screenshot({ path: join(OUT, 'dark-narrow-list.png') });
  await click(page, '[data-hid="workflow_run:run-7f3a"]');
  const split = await narrowRead(page);
  results['narrow-detail'] = split;
  narrowCheck('narrow-detail', split);
  expect(!!(await page.$('[data-testid="ph-detail"]')), 'narrow-detail: the details pane is open');
  await applyTheme(page, 'dark'); await wait(150);
  await page.screenshot({ path: join(OUT, 'dark-narrow-detail.png') });
  await applyTheme(page, null);
  await page.close();
}

const screens = screenFailures();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures, screens }, null, 1));
await browser.close();
server.close();
console.log(`plan-history driver: ${Object.keys(results).length} scenes, ${failures.length} failures, ${screens.length} screen failures. Shots in ${OUT}`);
for (const f of [...failures, ...screens]) console.log('  FAIL ' + f);
process.exit(failures.length || screens.length ? 1 : 0);
