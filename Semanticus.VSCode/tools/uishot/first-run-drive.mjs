// Browser-driven proof of the first-run choice (1.4): "How do you want to work?", the one press that connects, the
// "say this next" card, By hand, and the Plan tour's Connect your assistant step. The driver plays the HOST's half:
// it answers getFirstRun / firstRunConnect / firstRunClose and keeps the once-flag in localStorage, which survives a
// reload the way context.globalState survives a window reload.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium [THEMES=dark,light] node tools/uishot/first-run-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'first-run');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark,light').split(',');
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('Build the webview first.'); process.exit(1); }
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
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
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// connected: what the host says about the folder. failConnect: the first press answers with an error.
const seed = (connected, failConnect) => `
${FIXTURE_SRC}
const __plan = makePlan({});
window.__host = [];
const FLAGS = 'uishot-first-run';
const read = () => { try { return JSON.parse(localStorage.getItem(FLAGS) || '{}'); } catch { return {}; } };
const reply = (m) => setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: m })), 0);
let __failed = false;
let realApi;
Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
  get: () => function () {
    const api = realApi(); const post = api.postMessage;
    api.postMessage = function (msg) {
      const t = msg && msg.type;
      if (t === 'getFirstRun' || t === 'firstRunConnect' || t === 'firstRunClose' || t === 'getGuideFlags' || t === 'setGuideFlag') {
        window.__host.push(msg);
        const f = read();
        if (t === 'getFirstRun') reply({ type: 'firstRun', id: msg.id, show: !f.done && !${connected}, connected: ${connected} || !!f.connected });
        else if (t === 'firstRunConnect') {
          if (${failConnect} && !__failed) { __failed = true; setTimeout(() => reply({ type: 'firstRunConnected', id: msg.id, ok: false, error: 'The connection file in this folder is not valid JSON, so nothing was written. Fix it and press Connect again.' }), 350); }
          else setTimeout(() => { localStorage.setItem(FLAGS, JSON.stringify({ done: true, connected: true, mode: 'assistant' })); reply({ type: 'firstRunConnected', id: msg.id, ok: true }); }, 350);
        }
        else if (t === 'firstRunClose') localStorage.setItem(FLAGS, JSON.stringify({ ...f, done: true, mode: msg.mode }));
        else if (t === 'getGuideFlags') reply({ type: 'guideFlags', id: msg.id, planTour: false, whatChanged: false });
        return;
      }
      return post.call(this, msg);
    };
    return api;
  },
  set: (v) => { realApi = v; } });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v; v.setResponse('getModelPlan', () => __plan);
  v.setResponse('getPlanHistory', () => ({ sessionId: 's', rows: [], more: false, kept: true }));
} });
`;
const click = (page, text) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button')].find((x) => x.textContent.trim().startsWith(t) && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, text);
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };
const card = (page) => page.evaluate(() => { const c = document.querySelector('.first-run'); if (!c) return null; const r = c.getBoundingClientRect();
  return { text: c.innerText, inView: r.left >= 0 && r.top >= 0 && r.right <= innerWidth && r.bottom <= innerHeight, focusIn: c.contains(document.activeElement) }; });
async function open(ctx, theme, { connected = false, failConnect = false } = {}) {
  const page = await ctx.newPage();
  page.on('pageerror', (e) => { failed += 1; console.error('pageerror: ' + e.message); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(connected, failConnect));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1200);
  return page;
}
const shot = (page, theme, name) => page.screenshot({ path: join(OUT, `${theme}-${name}.png`) });

for (const theme of THEMES) {
  console.log(`== ${theme}`);
  // a fresh machine, not connected: the choice, a failed press, a good press, the say card, a reload
  {
    const ctx = await browser.createBrowserContext();
    const page = await open(ctx, theme, { failConnect: true });
    let c = await card(page);
    check(c && c.text.includes('How do you want to work?') && c.text.includes('With your assistant') && c.text.includes('By hand'), 'a fresh machine sees the choice');
    check(c && c.inView && c.focusIn, 'the card is on screen and has the keyboard');
    await shot(page, theme, '1-choose');
    await click(page, 'Connect Claude Code'); await wait(120);
    await shot(page, theme, '2-connecting');
    await wait(500); c = await card(page);
    check(c && c.text.includes('not valid JSON'), 'a failed press says what happened and stays open');
    await shot(page, theme, '3-error');
    await click(page, 'Connect Claude Code'); await wait(700);
    c = await card(page);
    check(c && c.text.includes('Open Claude Code in this folder and say:') && /restart/i.test(c.text), 'a good press shows what to say and the restart line');
    await shot(page, theme, '4-say');
    check(await click(page, 'Done'), 'Done closes it'); await wait(200);
    check((await card(page)) === null, 'and it is gone');
    const sent = await page.evaluate(() => window.__host.filter((m) => m.type === 'firstRunConnect').length);
    check(sent === 2, 'one host call per press (a failed press and a good one)');
    await page.reload({ waitUntil: 'networkidle0' }); await page.waitForSelector('nav button, [data-tab]'); await applyTheme(page, theme); await wait(1000);
    check((await card(page)) === null, 'after a connect and a reload it never comes back');
    await ctx.close();
  }
  // By hand: the other choice
  {
    const ctx = await browser.createBrowserContext();
    const page = await open(ctx, theme);
    await click(page, 'By hand'); await wait(150);
    const c = await card(page);
    check(c && c.text.includes('Start working') && !c.text.includes('Connect Claude Code'), 'choosing By hand swaps the press for Start working');
    await shot(page, theme, '5-by-hand');
    await click(page, 'Start working'); await wait(200);
    check((await card(page)) === null, 'it closes');
    const mode = await page.evaluate(() => window.__host.find((m) => m.type === 'firstRunClose')?.mode);
    check(mode === 'hand', 'and tells the host the mode is By hand');
    await page.reload({ waitUntil: 'networkidle0' }); await page.waitForSelector('nav button, [data-tab]'); await applyTheme(page, theme); await wait(1000);
    check((await card(page)) === null, 'never again on this machine');
    await ctx.close();
  }
  // Show me around: closes the card with nothing chosen and starts the Plan tour
  {
    const ctx = await browser.createBrowserContext();
    const page = await open(ctx, theme);
    await shot(page, theme, '7-choose-with-around');
    check(await click(page, 'Show me around'), 'Show me around is on the card'); await wait(1200);
    check((await card(page)) === null, 'it closes the card');
    const sent = await page.evaluate(() => window.__host.find((m) => m.type === 'firstRunClose')?.mode);
    check(sent === 'tour', 'and tells the host nothing was chosen (tour)');
    const count = await page.evaluate(() => document.querySelector('.plan-tour-count')?.textContent ?? null);
    check(count === '1 of 7', `and the Plan tour starts (${count})`);
    await shot(page, theme, '8-around-tour');
    await ctx.close();
  }
  // already connected: nothing shows
  {
    const ctx = await browser.createBrowserContext();
    const page = await open(ctx, theme, { connected: true });
    check((await card(page)) === null, 'a connected folder is never asked');
    await ctx.close();
  }
  // the tour: not connected gets 7 steps with Connect your assistant; connected gets 6
  for (const connected of [false, true]) {
    const ctx = await browser.createBrowserContext();
    const page = await open(ctx, theme, { connected });
    if (!connected) { await click(page, 'By hand'); await click(page, 'Start working'); await wait(300); }
    await page.evaluate(() => {});
    await wait(1200);
    const count = await page.evaluate(() => document.querySelector('.plan-tour-count')?.textContent ?? null);
    check(count === (connected ? '1 of 6' : '1 of 7'), `${connected ? 'connected' : 'not connected'} tour starts at ${count}`);
    if (!connected) {
      for (let i = 0; i < 4; i += 1) { await click(page, 'Next'); await wait(150); }
      await wait(700);
      const t = await page.evaluate(() => { const c = document.querySelector('.plan-tour'); const ring = document.querySelector('.plan-tour-ring'); const a = document.querySelector('[data-tour="assistant-chip"]');
        const rr = ring?.getBoundingClientRect(), ar = a?.getBoundingClientRect();
        return { title: c?.querySelector('.plan-tour-title')?.textContent, count: c?.querySelector('.plan-tour-count')?.textContent,
          ringOnChip: !!(rr && ar && Math.abs(rr.left - ar.left + 6) < 3 && Math.abs(rr.top - ar.top + 6) < 3) }; });
      check(t.title === 'Connect your assistant' && t.count === '5 of 7', `step 5 is Connect your assistant (${t.title}, ${t.count})`);
      check(t.ringOnChip, 'its ring is on the header Your assistant button');
      await shot(page, theme, '6-tour-connect');
    }
    await ctx.close();
  }
}
await browser.close(); server.close();
console.log(failed ? `${failed} FAILED` : 'first-run drive: all checks passed');
process.exit(failed ? 1 : 0);
