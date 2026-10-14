// Browser-driven proof of Connect another assistant (1.5, lane connect-any): the header's assistant menu gets the new item, it opens the
// picker, the picker lists Claude Desktop, Codex, Cursor and Claude Code (terminal) with what is found here, and one press per assistant
// shows what happened and what to do next. The engine is mocked: listAssistants and connectAssistant answer from a small fake machine.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium [THEMES=dark,light] node tools/uishot/connect-any-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'connect-any');
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



const seed = () => `
${FIXTURE_SRC}
const __plan = makePlan({});
window.__calls = [];
const __machine = [
  { id: 'claude-desktop', name: 'Claude Desktop', found: true, state: 'notConnected', configPath: '/home/kane/.config/Claude/claude_desktop_config.json', nextStep: 'Quit Claude Desktop fully and open it again.' },
  { id: 'codex', name: 'Codex', found: true, state: 'connected', configPath: '/home/kane/.codex/config.toml', nextStep: 'Start a new Codex session.' },
  { id: 'cursor', name: 'Cursor', found: false, state: 'notFound', configPath: '/home/kane/.cursor/mcp.json', nextStep: 'Restart Cursor.' },
  { id: 'claude-code', name: 'Claude Code (terminal)', found: true, state: 'outdated', configPath: 'claude mcp add --scope user', nextStep: 'Start a new Claude Code session in your terminal.' },
];
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v; v.setResponse('getModelPlan', () => __plan);
  v.setResponse('getPlanHistory', () => ({ sessionId: 's', rows: [], more: false, kept: true }));
  v.setResponse('listAssistants', () => { window.__calls.push(['listAssistants']); return __machine.map((m) => ({ ...m })); });
  v.setResponse('connectAssistant', (params) => {
    const id = params[0];
    window.__calls.push(['connectAssistant', id]);
    const m = __machine.find((x) => x.id === id);
    if (id === 'cursor') return { assistant: id, name: m.name, ok: false, changed: false, message: 'The config file of Cursor is not valid JSON, so Semanticus left it alone. Fix the file, then press Connect again.' };
    m.state = 'connected';
    return { assistant: id, name: m.name, ok: true, changed: true, message: 'Connected ' + m.name + '.', nextStep: m.nextStep, configPath: m.configPath,
      backupPath: id === 'claude-desktop' ? m.configPath + '.semanticus-bak' : null,
      licenceNote: id === 'claude-desktop' ? 'Your Pro licence is saved in /home/kane/.semanticus/license so Claude Desktop runs as Pro too.' : null };
  });
} });
`;
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };
const click = (page, text) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button, [role="menuitem"]')].find((x) => x.textContent.trim().startsWith(t) && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, text);
const panel = (page) => page.evaluate(() => document.querySelector('.connect-any')?.innerText ?? null);
for (const theme of THEMES) {
  console.log(`== ${theme}`);
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  page.on('pageerror', (e) => { failed += 1; console.error('pageerror: ' + e.message); });
  await page.setViewport({ width: Number(process.env.WIDTH) || 1280, height: 760, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed());
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-tour="assistant-chip"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(800);
  const shot = (name) => page.screenshot({ path: join(OUT, `${theme}-${name}.png`) });
  await page.click('[data-tour="assistant-chip"]'); await wait(300);
  const menu = await page.evaluate(() => document.querySelector('[role="menu"]')?.innerText ?? '');
  check(menu.includes('Connect another assistant'), `the assistant menu has the new item (${menu.replace(/\n/g, ' | ').slice(0, 90)})`);
  await shot('1-menu');
  check(await click(page, 'Connect another assistant'), 'press Connect another assistant'); await wait(500);
  let t = await panel(page);
  check(t && ['Claude Desktop', 'Codex', 'Cursor', 'Claude Code (terminal)'].every((n) => t.includes(n)), 'the picker lists the four assistants');
  check(t.includes('Not found here') && t.includes('On this computer') && t.includes('Connected') && t.includes('Needs an update'), 'it says what is found, connected and out of date');
  await shot('2-picker');
  check(await click(page, 'Connect Claude Desktop') || await page.evaluate(() => { const b = document.querySelector('[aria-label="Connect Claude Desktop"]'); if (b) b.click(); return !!b; }), 'press Connect on Claude Desktop');
  await wait(700);
  t = await panel(page);
  check(!t.includes('Connected Claude Desktop.') && t.includes('Quit Claude Desktop fully and open it again.'), 'it says the one thing to do next, and Connected only once');
  check(t.includes('A copy of the old file is next to it.') && t.includes('runs as Pro too'), 'it names the copy and the saved licence');
  await shot('3-connected');
  await page.evaluate(() => { const b = document.querySelector('[aria-label="Connect Cursor"]'); if (b) b.click(); });
  await wait(700);
  t = await panel(page);
  check(t.includes('not valid JSON'), 'a refusal shows the engine sentence');
  await shot('4-refused');
  const calls = await page.evaluate(() => window.__calls);
  check(calls.some((c) => c[0] === 'connectAssistant' && c[1] === 'claude-desktop'), 'the page called connectAssistant with the assistant id');
  const fit = await page.evaluate(() => { const r = document.querySelector('.connect-any')?.getBoundingClientRect(); return r ? { l: r.left, r: r.right, w: innerWidth } : null; });
  check(fit && fit.l >= 0 && fit.r <= fit.w, 'the picker sits inside the window');
  await page.keyboard.press('Escape'); await wait(200);
  check((await panel(page)) === null, 'Escape leaves the picker');
  await ctx.close();
}
await browser.close(); server.close();
console.log(failed ? `${failed} FAILED` : 'all ok'); process.exit(failed ? 1 : 0);
