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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'h16-connect');
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


// The host's half for hero16 N2/N9: the person already left the first-run card (the flag is written), the driver mode in the
// page is I drive, and the header "Connect your assistant" is pressed. The host answers as connectClaudeCodeCmd now does:
// driveMode (Claude drives, wired), then firstRunSay. An assistant call arrives later, on the driver's cue.
const seed = () => `
${FIXTURE_SRC}
const __plan = makePlan({});
window.__host = [];
const reply = (m) => setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: m })), 0);
window.__reply = reply;
let realApi;
Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
  get: () => function () {
    const api = realApi(); const post = api.postMessage;
    api.postMessage = function (msg) {
      const t = msg && msg.type;
      if (t === 'getFirstRun') { window.__host.push(msg); reply({ type: 'firstRun', id: msg.id, show: false, connected: false }); return; }
      if (t === 'getGuideFlags') { reply({ type: 'guideFlags', id: msg.id, planTour: true, whatChanged: true }); return; }
      if (t === 'runCommand' && msg.command === 'semanticus.connectClaudeCode') {
        window.__host.push(msg);
        setTimeout(() => { reply({ type: 'driveMode', mode: 'claudeDrives', assistantConnected: true }); reply({ type: 'firstRunSay' }); }, 300);
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
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };
const click = (page, text) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button, [role="menuitem"]')].find((x) => x.textContent.trim().startsWith(t) && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, text);
const state = (page) => page.evaluate(() => ({
  card: document.querySelector('.first-run')?.innerText ?? null,
  bar: document.querySelector('[data-studio-driver-bar]')?.innerText ?? null,
  frame: !!document.querySelector('[data-testid="studio-drive-edge"], .sc-edge'),
}));
for (const theme of THEMES) {
  console.log(`== ${theme}`);
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  page.on('pageerror', (e) => { failed += 1; console.error('pageerror: ' + e.message); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed());
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1000);
  const shot = (name) => page.screenshot({ path: join(OUT, `${theme}-${name}.png`) });
  let s = await state(page);
  check(s.card === null, 'no card: the person left the first-run card earlier');
  check(s.bar === null && !s.frame, 'no driver bar and no line before anything is connected');
  await shot('1-before');
  // The harness already shows an activity feed, which hides the header's Connect row (it leaves once an assistant has been seen).
  // The header row and the palette run the same host command, so the driver sends that command the way runHostCommand does.
  await page.evaluate(() => window.acquireVsCodeApi().postMessage({ type: 'runCommand', command: 'semanticus.connectClaudeCode' }));
  await wait(1000);
  s = await state(page);
  check(s.card && s.card.includes('Open Claude Code in this folder and say:'), `the say card shows (${(s.card ?? '').slice(0, 40)})`);
  await shot('3-say');
  check(await click(page, 'Done'), 'Done'); await wait(400);
  s = await state(page);
  check(s.bar && s.bar.includes('Waiting for your assistant'), `the bar waits plainly (${(s.bar ?? '').slice(0, 60)})`);
  check(!s.frame, 'no line before an assistant has started');
  await shot('4-waiting');
  await page.evaluate(() => window.__reply({ type: 'assistantCall', payload: { at: new Date().toISOString(), tool: 'model_summary' } }));
  await wait(1500);
  s = await state(page);
  check(s.bar && s.bar.includes('is working'), `the assistant starts: the bar says it is working (${(s.bar ?? '').slice(0, 40)})`);
  check(s.frame, 'and the thin line shows round the Studio while it works');
  await shot('5-driving');
  await ctx.close();
}
await browser.close(); server.close();
console.log(failed ? `${failed} FAILED` : 'all ok'); process.exit(failed ? 1 : 0);
