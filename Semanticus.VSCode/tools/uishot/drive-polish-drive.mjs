// hero15 drive polish (1.4): the moments the hero run failed, drawn in the real Studio on the mocked engine, Dark Modern.
//   A. a long walk-through while the person has the wheel: the waiting notes fold into one that counts them (H16)
//   B. the same walk while Claude drives: after the first read the strip jumps to the newest, with the count (H16)
//   C. an engine note that presses Run the plan: the Claude cursor draws on the button (H1)
//   D. the work is over: the read note leaves the strip, What I did keeps it (H4)
//   E. the drive switch shows which mode is on in all three states, I drive included (Kane, 11 Oct)
// Run: node drive-polish-drive.mjs   (build the webview first). OUTDIR=<dir> sets where the PNGs go; LABEL names them (before/after).
// It reports what it saw and exits 1 when a moment is not as the fix says, so the same script proves before and after.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(here, 'shots', 'drive-polish');
const LABEL = process.env.LABEL || 'after';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = await new Promise((r) => { const s = createServer((req, res) => {
  const f = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!f.startsWith(webRoot) || !existsSync(f) || statSync(f).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(f).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(f));
}); s.listen(0, '127.0.0.1', () => r(s)); });
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox'] });
const seen = []; const failures = [];
const expect = (ok, words) => { seen.push(`${ok ? 'ok  ' : 'FAIL'} ${words}`); if (!ok) failures.push(words); };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

let n = 0;
const MEASURES = ['Average list price', 'Average standard cost', 'Average discount percent', 'Net sales', 'Gross margin', 'Units sold', 'Return rate'];
const step = (i) => ({ id: `sn-${String(++n).padStart(12, '0')}`, say: `${i + 1} of 49: ${MEASURES[i % MEASURES.length]}. It shows money with 2 decimals, so I keep it.`,
  place: 'plan', how: 'Open Plan. The top rows are the work worth doing next.', at: new Date(Date.UTC(2026, 9, 11, 2, 58, n)).toISOString(), by: 'assistant', sessionId: 'uishot' });
const row = (id, title, doer) => ({ id, section: 'Do now', title, sub: '', who: doer === 'assistant' ? 'asst' : 'sem', status: 'ready', stateWord: 'ready', doer, waitsFor: [], goal: 'quality', rules: [] });
const PLAN = { planId: 'p1', scope: 'whole model', revision: 1, assistantQueue: 'on', items: [
  row('safefix/Visibility', 'Hide the key columns', 'semanticus'), row('bpafix/model', 'Fix 3 best-practice issues', 'semanticus'),
  row('words/model', 'Write 3 descriptions', 'assistant'),
], summary: {}, strip: { tracks: [
  { doer: 'semanticus', done: 0, onIt: 0, ready: 2, needsYou: 0, waiting: 0 }, { doer: 'you', done: 0, onIt: 0, ready: 0, needsYou: 0, waiting: 0 },
  { doer: 'assistant', done: 0, onIt: 0, ready: 1, needsYou: 0, waiting: 0 }] } };

const drive = (page, mode, wheel) => page.evaluate((m, w) => {
  window.postMessage({ type: 'driveMode', mode: m, assistantConnected: true }, '*');
  if (w) window.postMessage({ type: 'personInput', at: Date.now() }, '*');
}, mode, wheel);
const post = (page, type, payload) => page.evaluate((t, p) => window.postMessage({ type: t, payload: p }, '*'), type, payload);
const text = (page, sel) => page.evaluate((s) => [...document.querySelectorAll(s)].map((e) => (e.textContent || '').trim()), sel);

async function open() {
  const page = await browser.newPage();
  page.on('pageerror', (e) => failures.push(`pageerror ${e.message}`));
  await page.setViewport({ width: 1280, height: 760, deviceScaleFactor: 2 });
  // The Plan reads its rows as it mounts, so the answers are in place before the page loads.
  await page.evaluateOnNewDocument((plan) => {
    let h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => h, set: (v) => {
      h = v; v.setResponse('getModelPlan', plan); v.setResponse('getStudioNotes', { sessionId: 'uishot', notes: [], suggestion: null });
    } });
  }, PLAN);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await applyTheme(page, 'dark');
  return page;
}
const shot = async (page, name) => { await wait(200); await page.screenshot({ path: join(OUT, `${name}-${LABEL}.png`) }); };

try {
  // A. The person has the wheel and Claude walks on: six notes wait.
  let page = await open();
  await drive(page, 'claudeDrives', true);
  for (let i = 0; i < 6; i++) await post(page, 'studioShow', step(i));
  await page.waitForSelector('[data-testid="note-say"]', { timeout: 5000 });
  const sayA = (await text(page, '[data-testid="note-say"]'))[0];
  const moreA = (await text(page, '[data-testid="note-more"]'))[0] ?? '';
  const foldA = (await text(page, '[data-testid="note-folded"]'))[0] ?? '';
  expect(sayA.startsWith('6 of 49'), `A: the strip shows the newest step ("${sayA.slice(0, 30)}")`);
  expect(foldA === '5 more steps in What I did' && !moreA, `A: one note counts the rest ("${foldA}"${moreA ? `, and "${moreA}"` : ''})`);
  await shot(page, 'A-fold-person-has-wheel');
  await page.close();

  // B. Claude drives, notes come faster than they can be read.
  page = await open();
  await drive(page, 'claudeDrives', false);
  for (let i = 0; i < 6; i++) { await post(page, 'studioShow', step(i)); await wait(300); }
  const moreB = (await text(page, '[data-testid="note-more"]'))[0] ?? '';
  expect(!/^[4-9]|^\d\d/.test(moreB), `B: at most three wait behind the strip ("${moreB}")`);
  await wait(4200);   // the first note's read time
  const sayB = (await text(page, '[data-testid="note-say"]'))[0] ?? '';
  const foldB = (await text(page, '[data-testid="note-folded"]'))[0] ?? '';
  expect(sayB.startsWith('6 of 49') && foldB === '4 more steps in What I did', `B: after one read the strip is at the newest ("${sayB.slice(0, 12)}", "${foldB}")`);
  await shot(page, 'B-fold-claude-drives');

  // D. The work is over: no note and no call for 8 s after the last read.
  await wait(9000);
  const sayD = await page.$('[data-testid="note-say"]');
  const listD = await page.$('[data-testid="note-what-i-did"], [data-testid="what-i-did"]');
  expect(!sayD && !!listD, `D: the strip is clear and What I did is there (note ${sayD ? 'still on screen' : 'gone'})`);
  await shot(page, 'D-work-over');
  await page.close();

  // C. The engine's own note for apply_plan_items carries the run-plan press: Run the plan itself shows pressed and lit, and
  //    the note names it (Kane, 11 Oct: seeing the button clicked matters more than the cursor's glide).
  page = await open();
  await drive(page, 'claudeDrives', false);
  await page.waitForSelector('[data-testid="plan-run"]', { timeout: 10000 });
  await post(page, 'studioShow', { ...step(0), say: 'Your assistant pressed Run the plan.', auto: true, press: 'run-plan' });
  await page.waitForSelector('[data-testid="plan-run"][data-claude-press]', { timeout: 2000 }).catch(() => null);
  await wait(420);   // past the push-in, while the button is lit
  const pressed = await page.$('[data-testid="plan-run"][data-claude-press]');
  const sayC = (await text(page, '[data-testid="note-say"]'))[0] ?? '';
  expect(!!pressed && sayC === 'Your assistant pressed Run the plan.', `C: Run the plan shows pressed and the note names it ("${sayC}")`);
  const box = await page.$eval('[data-testid="plan-run"]', (b) => { const r = b.getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; });
  await page.screenshot({ path: join(OUT, `C-button-pressed-zoom-${LABEL}.png`), clip: { x: box.x - 140, y: box.y - 40, width: box.w + 280, height: box.h + 80 } });
  await page.screenshot({ path: join(OUT, `C-button-pressed-${LABEL}.png`) });
  await wait(1700);
  expect(!(await page.$('[data-claude-press]')), 'C: the button lets go after a moment');
  await page.close();

  // E. The drive switch in its three states. The one that is on must not look like the one that is off.
  page = await open();
  const bar = async (name) => {
    await wait(250);
    const look = await page.$$eval('[data-testid^="driver-mode-"]', (bs) => bs.map((b) => ({ on: b.getAttribute('aria-pressed') === 'true',
      color: getComputedStyle(b).color, ring: getComputedStyle(b).boxShadow })));
    const on = look.find((b) => b.on); const off = look.find((b) => !b.on);
    expect(!!on && !!off && on.color !== off.color && on.ring !== 'none', `E ${name}: the mode that is on stands out (${on?.color} ring ${on?.ring !== 'none'} vs ${off?.color})`);
    const r = await page.$eval('[data-testid="studio-driver-bar"]', (b) => { const x = b.getBoundingClientRect(); return { x: x.left, y: x.top, w: x.width, h: x.height }; });
    await page.screenshot({ path: join(OUT, `E-switch-${name}-${LABEL}.png`), clip: { x: r.x, y: r.y, width: r.w, height: r.h } });
  };
  await drive(page, 'claudeDrives', false); await bar('claude-drives');
  await drive(page, 'claudeDrives', true); await bar('person-has-wheel');
  await page.click('[data-testid="driver-mode-iDrive"]'); await bar('i-drive');
  await page.close();
} finally { await browser.close(); server.close(); }
console.log(seen.join('\n'));
console.log(failures.length ? `drive-polish (${LABEL}): ${failures.length} not as fixed; shots in ${OUT}` : `drive-polish (${LABEL}): all four moments as fixed; shots in ${OUT}`);
process.exit(failures.length ? 1 : 0);
