// Reads the drive edge's weight off the pixels, side by side, the way VS Code shows it: a webview is an iframe of whole CSS pixels inside a
// workbench that is zoomed (Ctrl+ plus, or a 125% or 150% display), so the frame is a fractional number of DEVICE pixels. Emulating a
// device scale factor does not show this (it snaps to CSS pixels), so this launches a browser at a real scale factor and puts the Studio
// in an iframe of whole CSS pixels, then compares the frame with the line against the same frame without it.
//
//   MODE=old   draws the 1px accent border at half strength that 1.4 shipped, on the same Studio (to measure what Kane saw)
//   MODE=new   the real bundle's line (default)
//   OUTDIR=<dir> TAG=<tag> node tools/uishot/edge-probe.mjs      then:  python3 edge-weight.py <line colour hex> <dir>/probe-*-<tag>.png
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser } from './browser.mjs';
import { applyTheme } from './themes.mjs';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'colours');
const MODE = process.env.MODE || 'new';
const THEME = process.env.THEME || 'dark';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/__host') {
    const w = url.searchParams.get('w'), h = url.searchParams.get('h');
    res.writeHead(200, { 'content-type': 'text/html' });
    res.end(`<!doctype html><body style="margin:0;background:#1F1F1F"><iframe id="f" src="/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan" style="border:0;display:block;width:${w}px;height:${h}px"></iframe>`);
    return;
  }
  const p = normalize(join(webRoot, decodeURIComponent(url.pathname)));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const SEED = `Date.now = () => 100000; let a; Object.defineProperty(window,'acquireVsCodeApi',{get:()=>a,set:(o)=>{a=()=>{const api=o();const post=api.postMessage;api.postMessage=(m)=>{post(m);if(m.type==='studioReady')window.dispatchEvent(new MessageEvent('message',{data:{type:'driveMode',mode:'iDrive',assistantConnected:true}}));};return api;};}});`;
const out = [];
// [css width, css height, device scale factor]: 100%, the usual VS Code zoom steps (120%, 144%), and Windows display scales (125%, 150%, 175%).
const CASES = (process.env.CASES ? JSON.parse(process.env.CASES) : [[1280, 800, 1], [1283, 801, 1.2], [1277, 803, 1.1], [1919, 1079, 1.25], [1281, 799, 1.5], [1281, 799, 1.75], [1500, 900, 1.44]]);
for (const [w, h, dpr] of CASES) {
  const browser = await puppeteer.launch({ executablePath: findBrowser({ quiet: true }), headless: true, defaultViewport: null,
    args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', `--force-device-scale-factor=${dpr}`, `--window-size=${Math.ceil(w) + 20},${Math.ceil(h) + 20}`] });
  const page = await browser.newPage();
  await page.setViewport({ width: w + 20, height: h + 20, deviceScaleFactor: dpr });
  await page.evaluateOnNewDocument(SEED);
  await page.goto(`http://127.0.0.1:${server.address().port}/__host?w=${w}&h=${h}`, { waitUntil: 'networkidle0' });
  const frame = page.frames().find((f) => f.url().includes('harness.html'));
  await frame.waitForSelector('[data-testid="plan-page"]', { timeout: 30000 });
  await applyTheme(frame, THEME);
  await frame.waitForSelector('[data-testid="driver-mode-claudeDrives"]');
  await frame.click('[data-testid="driver-mode-claudeDrives"]');
  await frame.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'assistantCall', payload: { at: new Date(100000).toISOString(), tool: 't', assistant: { key: 'claude', name: 'Claude Code', initials: 'CC' } } } })));
  await frame.waitForSelector('[data-testid="studio-drive-edge"]');
  await new Promise((r) => setTimeout(r, 600));
  if (MODE === 'old') {
    // What 1.4 drew: one 1px accent border on a fixed frame, at the 50 percent top of its breath.
    await frame.addStyleTag({ content: '.sc-edge { display: none !important; } .sc-old { position: fixed; inset: 0; pointer-events: none; z-index: 89; box-sizing: border-box; border: 1px solid var(--sem-accent); opacity: .5; }' });
    await frame.evaluate(() => { const d = document.createElement('div'); d.className = 'sc-old'; document.body.appendChild(d); });
  } else {
    await frame.evaluate(() => document.querySelector('.sc-edge').getAnimations().forEach((a) => { a.pause(); a.currentTime = 2100; }));
  }
  const info = await frame.evaluate(() => ({ dpr: devicePixelRatio, iw: innerWidth, ih: innerHeight }));
  const clip = { x: 0, y: 0, width: w, height: h };
  const file = `probe-${w}x${h}-x${dpr}-${process.env.TAG || MODE}.png`;
  await page.screenshot({ path: join(OUT, file), clip });
  await frame.addStyleTag({ content: '.sc-edge, .sc-old { display: none !important; }' });
  await page.screenshot({ path: join(OUT, file.replace('probe-', 'probe-off-')), clip });
  out.push({ file, ...info, device: [w * dpr, h * dpr] });
  await browser.close();
}
writeFileSync(join(OUT, `probe-${process.env.TAG || MODE}.json`), JSON.stringify(out, null, 1));
server.close();
console.log(JSON.stringify(out));
