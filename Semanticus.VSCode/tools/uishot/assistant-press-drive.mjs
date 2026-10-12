// The assistant's presses and its sign, in the real Studio on the mocked engine (1.4, Kane 12 Oct: option A for both).
//   Part 2: a ring on the real button for Run the plan, Save and Publish when the button is on ANOTHER page. The Studio opens the
//           button's page, waits for it to draw, then rings the button. No mouse pointer. Nothing is clicked and nothing is written.
//   Part 1: the assistant's own tile beside "<Name> is working" in the driver bar, in each state.
// Run: node tools/uishot/assistant-press-drive.mjs   (build the webview first)
// OUTDIR=<dir> for the PNGs, LABEL=before|after names them, THEMES=dark,light. Exits 1 when a press shows no ring.
// The same script proves before and after: build the old cursor, run it with LABEL=before and it fails on the press from another page.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(here, 'shots/assistant-press');
const LABEL = process.env.LABEL || 'after';
const THEMES = (process.env.THEMES || 'dark,light').split(',');
const STATES = process.env.STATES || '/tmp/semanticus-joint-review/r1/plan/repro/states.json';
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run the webview build first.'); process.exit(1); }
const plan = JSON.parse(readFileSync(STATES, 'utf8')).plan0;
const rows = new Map(plan.items.map((item) => [item.id, item]));
for (const item of plan.items) item.waitsFor = (item.waitsFor || []).map((wait) => ({ ...wait, title: rows.get(wait.id)?.title || wait.id }));
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((done) => server.listen(0, '127.0.0.1', done));
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const failures = [];
const seen = [];
const expect = (ok, what) => { seen.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) failures.push(what); };
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 120000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb'] });
const RAN = { performed: true, applied: 3, itemIds: ['starter/table:Sales'], revision: 2, entryId: 'ed-1', entryIds: ['ed-1'], waves: 1, canSave: true,
  sentence: 'Applied 3 changes. One undo takes the whole run back.' };

async function open(query, theme) {
  const page = await browser.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push(e.message));
  await page.setViewport({ width: 1280, height: 800, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((fixture, ran) => {
    let harness;
    Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => harness, set: (value) => {
      harness = value;
      value.setResponse('getModelPlan', fixture);
      value.setResponse('applyPlanItems', () => ({ ...ran, plan: fixture }));
      value.setResponse('getStudioNotes', { sessionId: 'uishot', notes: [] });
    } });
  }, plan, RAN);
  // A press lasts about a second and a half, and a loaded machine can take longer than that to answer a poll, so a latch records every
  // control that ever got the ring. The check reads the latch, never the instant.
  await page.evaluateOnNewDocument(() => {
    window.__ringed = new Set();
    new MutationObserver((records) => { for (const r of records) if (r.target.hasAttribute?.('data-claude-press')) window.__ringed.add(r.target.getAttribute('data-testid')); })
      .observe(document, { subtree: true, attributes: true, attributeFilter: ['data-claude-press'] });
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 40000 });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 15000 });
  await wait(1500);
  await applyTheme(page, theme);
  return page;
}
const send = (page, data) => page.evaluate((m) => window.dispatchEvent(new MessageEvent('message', { data: m })), data);
const note = (id, place, press, say) => ({ id, say, how: '', place, press, at: new Date().toISOString(), by: 'assistant', sessionId: 'uishot', auto: true });
const goTo = async (page, tab) => { await send(page, { type: 'navigate', tab }); await wait(900); };
const ringed = (page, testid) => page.waitForFunction((id) => window.__ringed.has(id), { timeout: 8000, polling: 50 }, testid).then(() => true, () => false);
const stillRinged = (page, testid) => page.$(`[data-testid="${testid}"][data-claude-press]`).then((e) => !!e);
const ids = (page, re) => page.evaluate((r) => [...document.querySelectorAll('[data-testid]')].map((e) => e.getAttribute('data-testid')).filter((t) => new RegExp(r).test(t)), re.source);
const pointerOnScreen = (page) => page.evaluate(() => !!document.querySelector('[data-testid="studio-cursor"], .sc-pointer, .sc-layer'));

const inView = (page, testid) => page.evaluate((id) => {
  const b = document.querySelector(`[data-testid="${id}"]`)?.getBoundingClientRect();
  return !!b && b.top >= 0 && b.bottom <= innerHeight && b.left >= 0 && b.right <= innerWidth;
}, testid);
/** One press: send the note, wait for the ring, take the picture while it is on. A loaded machine can be slower than the ring, so it tries again. */
async function press(page, theme, id, place, pressId, say, testid, file, what) {
  for (let attempt = 1; attempt <= 3; attempt++) {
    await page.evaluate(() => window.__ringed.clear());
    await send(page, { type: 'studioShow', payload: note(`${id}-${attempt}`, place, pressId, say) });
    const ok = await ringed(page, testid);
    if (!ok) { expect(false, `${theme}: ${what} ${JSON.stringify(await ids(page, new RegExp(testid.split('-')[0])))}`); return false; }
    const box = await page.$eval(`[data-testid="${testid}"]`, (e) => { const r = e.getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; });
    const vw = page.viewport().width, vh = page.viewport().height;
    const x = Math.max(0, Math.min(vw - 360, box.x + box.w / 2 - 180)), y = Math.max(0, Math.min(vh - 110, box.y + box.h / 2 - 55));
    await page.screenshot({ path: file.replace('.png', '-zoom.png'), clip: { x, y, width: Math.min(360, vw), height: Math.min(110, vh) } });
    await page.screenshot({ path: file });
    if (await stillRinged(page, testid)) { expect(true, `${theme}: ${what}`); return true; }
    await wait(2500);   // the ring let go before the picture: let the note finish, then press again
  }
  expect(false, `${theme}: ${what} (the ring never stayed for a picture)`);
  return false;
}

try {
  for (const theme of THEMES) {
    const tag = `${LABEL}-${theme}`;
    // 1. Run the plan, from the Data page.
    let page = await open('tab=data&conn=1&tier=pro&dirty=1', theme);
    await send(page, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    expect(!(await page.$('[data-testid="plan-run"]')), `${theme}: Run the plan is not on the page the person is looking at`);
    await press(page, theme, 'p1', 'plan', 'run-plan', 'Your assistant pressed Run the plan.', 'plan-run', join(OUT, `1-run-the-plan-${tag}.png`),
      'Run the plan is ringed after the Studio opens the Plan page');
    expect(!(await pointerOnScreen(page)), `${theme}: no mouse pointer is drawn`);
    // 2. Save. It is in the run receipt, so press Run the plan for real first (the mocked engine answers at once). Then make the window
    //    short so Save is below the fold: the old check wanted the button fully in the window and gave up.
    await wait(2000);   // the first ring lets go
    await page.evaluate(() => document.querySelector('[data-testid="plan-run"]')?.click());
    await page.waitForSelector('[data-testid="plan-run-save"]', { timeout: 8000 }).catch(() => null);
    expect(!!(await page.$('[data-testid="plan-run-save"]')), `${theme}: the Save button exists after a run (set-up)`);
    await page.setViewport({ width: 1280, height: 330, deviceScaleFactor: 1 });
    await page.evaluate(() => { const b = document.querySelector('[data-testid="plan-run-save"]'); for (let e = b?.parentElement; e; e = e.parentElement) if (e.scrollHeight > e.clientHeight + 4) e.scrollTop = 0; window.scrollTo(0, 0); });
    await wait(300);
    const below = !(await inView(page, 'plan-run-save'));
    expect(below, `${theme}: Save is below the fold of a short window (set-up)`);
    await press(page, theme, 'p2', 'plan', 'save', 'Your assistant pressed Save.', 'plan-run-save', join(OUT, `2-save-${tag}.png`),
      'Save is ringed and brought into view when it was below the fold');
    expect(await inView(page, 'plan-run-save'), `${theme}: Save is now in the window`);
    await page.close();
    // 3. Publish, from the Plan page.
    page = await open('tab=plan&conn=1&tier=pro&live=1', theme);
    await send(page, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    expect(!(await page.$('[data-testid="publish-go"]')), `${theme}: Publish is not on the Plan page`);
    await press(page, theme, 'p3', 'publish/changes', 'publish', 'Your assistant pressed Publish.', 'publish-go', join(OUT, `3-publish-${tag}.png`),
      'Publish is ringed after the Studio opens the Publish page');
    // 4. The person takes the wheel: the press is noted, not shown.
    await wait(2500);
    await goTo(page, 'plan');
    await page.evaluate(() => { window.__ringed.clear(); document.querySelector('.studio-root')?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })); });
    await send(page, { type: 'studioShow', payload: note('p4', 'publish/changes', 'publish', 'Your assistant pressed Publish.') });
    await wait(2000);
    expect(!(await page.evaluate(() => window.__ringed.size)), `${theme}: with the wheel the person's, no ring is ever shown`);
    expect(!!(await page.$('[data-testid="note-say"]')), `${theme}: and the press is still noted in the strip`);
    expect(page.errors.length === 0, `${theme}: no page errors ${page.errors.join(' | ')}`);
    await page.close();
  }
  // (the sign scenes follow in the same browser)

// ---- Part 1: the sign in the driver bar. Skipped with SIGN=0 (the build before this lane has no sign to look at). ----
  if (process.env.SIGN !== '0') try {
  const WHO = {
    claude: { key: 'claude', name: 'Claude Code', initials: 'CC', version: '2.1.0' },
    codex: { key: 'codex', name: 'Codex', initials: 'CX', version: '0.5.1' },
    copilot: { key: 'copilot', name: 'GitHub Copilot', initials: 'GC', version: '1.2' },
    cursor: { key: 'cursor', name: 'Cursor', initials: 'CU', version: '1.0' },
    other: { key: 'other', name: 'Zed Agent', initials: 'ZA', version: '1' },
    unknown: { key: 'unknown', name: 'Your assistant', initials: null, version: null },
  };
  const bar = (page) => page.$('[data-studio-driver-bar]');
  const state = (page) => page.evaluate(() => {
    const sign = document.querySelector('[data-testid="assistant-sign"]');
    const tile = sign?.querySelector('.as-tile');
    return { sign: sign?.getAttribute('data-state') ?? null, who: sign?.getAttribute('data-assistant') ?? null, tile: tile?.textContent ?? null,
      animation: tile ? getComputedStyle(tile).animationName : null, words: document.querySelector('[data-studio-driver-bar] [role="status"]')?.textContent ?? null };
  });
  const call = (page, who) => send(page, { type: 'assistantCall', payload: { tool: 'get_model_summary', at: new Date().toISOString(), assistant: WHO[who] } });
  for (const theme of THEMES) {
    const tag = `${LABEL}-${theme}`;
    const page = await open('tab=plan&conn=1&tier=pro', theme);
    await send(page, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    // Connected, nothing called yet: no tile, the bar waits.
    let st = await state(page);
    expect(st.sign === null && /Waiting for your assistant/.test(st.words || ''), `${theme}: no assistant has called, no sign (${st.words})`);
    await call(page, 'claude'); await wait(300);
    st = await state(page);
    expect(st.sign === 'working' && st.who === 'claude' && st.tile === 'CC' && st.words === 'Claude Code is working. Click anything to take the wheel.', `${theme}: Claude Code working: ${JSON.stringify(st)}`);
    expect(st.animation === 'as-tile-pulse', `${theme}: the tile pulses (${st.animation})`);
    await (await bar(page)).screenshot({ path: join(OUT, `sign-1-working-${tag}.png`) });
    await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: 'reduce' }]); await wait(150);
    st = await state(page);
    expect(st.sign === 'working' && st.animation === 'none', `${theme}: reduced motion: still tile, no pulse (${st.animation})`);
    await (await bar(page)).screenshot({ path: join(OUT, `sign-2-working-reduced-motion-${tag}.png`) });
    await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: 'no-preference' }]);
    // The same assistant goes quiet: the tile stops after one quiet gap and the words say connected.
    await page.waitForFunction(() => document.querySelector('[data-testid="assistant-sign"]')?.getAttribute('data-state') === 'idle', { timeout: 20000 }).catch(() => null);
    st = await state(page);
    expect(st.sign === 'idle' && st.animation === 'none' && st.words === 'Claude Code is connected.', `${theme}: idle after the quiet gap: ${JSON.stringify(st)}`);
    await (await bar(page)).screenshot({ path: join(OUT, `sign-3-idle-${tag}.png`) });
    // The person takes the wheel, then I drive: the tile and the name stay.
    await page.evaluate(() => document.querySelector('.studio-root')?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
    await wait(200);
    st = await state(page);
    expect(st.sign !== null && /Your next message to Claude Code gives it back/.test(st.words), `${theme}: person has the wheel: ${st.words}`);
    await (await bar(page)).screenshot({ path: join(OUT, `sign-4-person-${tag}.png`) });
    await page.click('[data-testid="driver-mode-iDrive"]'); await wait(200);
    st = await state(page);
    expect(/^I drive\. Claude Code works/.test(st.words || ''), `${theme}: I drive: ${st.words}`);
    await (await bar(page)).screenshot({ path: join(OUT, `sign-5-i-drive-${tag}.png`) });
    await page.click('[data-testid="driver-mode-claudeDrives"]');
    // Every mapped assistant, working. Each is its own tile with its own initials; the unknown one gets three dots.
    for (const who of ['codex', 'copilot', 'cursor', 'other', 'unknown']) {
      await call(page, who); await wait(250);
      st = await state(page);
      expect(st.who === who && st.sign === 'working' && st.words.startsWith(WHO[who].name + ' is working'), `${theme}: ${who}: ${JSON.stringify(st)}`);
      await (await bar(page)).screenshot({ path: join(OUT, `sign-6-${who}-${tag}.png`) });
    }
    // Narrow window: the bar wraps and nothing overflows sideways.
    await page.setViewport({ width: 400, height: 800, deviceScaleFactor: 1 }); await wait(250);
    const overflow = await page.evaluate(() => { const b = document.querySelector('[data-studio-driver-bar]'); return b.scrollWidth - b.clientWidth; });
    expect(overflow <= 0, `${theme}: the bar does not overflow sideways at 400 px (${overflow})`);
    await (await bar(page)).screenshot({ path: join(OUT, `sign-7-narrow-${tag}.png`) });
    expect(page.errors.length === 0, `${theme}: no page errors ${page.errors.join(' | ')}`);
    await page.close();
  }
} catch (e) { expect(false, `the sign scenes stopped: ${e.message}`); }
} finally { await browser.close(); server.close(); }

writeFileSync(join(OUT, `results-${LABEL}.txt`), seen.join('\n') + '\n');
console.log(seen.join('\n'));
console.log(failures.length ? `assistant-press (${LABEL}): ${failures.length} not as built; shots in ${OUT}` : `assistant-press (${LABEL}): every press shows a ring; shots in ${OUT}`);
process.exit(failures.length ? 1 : 0);
