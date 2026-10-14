// The screen follows the tool to its home (drive follow, Kane 13 Oct: "it will be checking BPA but stay on AI readiness").
// Real Studio on the mocked engine, Claude drives. The scene posts what the host posts, at the pace of a real assistant:
//   t = 0 s    ai_readiness_scan   (the call, then the engine's note for review/ai-readiness)
//   t = 1 s    bpa_scan            (the call, then the note for review/model-quality)
// and shoots the Studio at t = 0.6 s and at t = 2.6 s. The Review row shows which view is on screen; the strip shows the words.
// Run: node tools/uishot/drive-follow-drive.mjs   (build the webview first). OUTDIR=<dir> THEMES=dark. Exits 1 on a failed check.
// Writes follow-<theme>-1-readiness.png and follow-<theme>-2-bpa.png (2.6 s after the readiness call).
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
const OUT = process.env.OUTDIR || join(here, 'shots/drive-follow');
const THEMES = (process.env.THEMES || 'dark').split(',');
const EXPECT_FOLLOW = process.env.EXPECT_FOLLOW !== '0';   // EXPECT_FOLLOW=0 records the "before" run without failing it
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run the webview build first.'); process.exit(1); }
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
const expect = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok && EXPECT_FOLLOW) failures.push(what); };
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 120000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb'] });
const send = (page, data) => page.evaluate((m) => window.dispatchEvent(new MessageEvent('message', { data: m })), data);
// The view on screen: the heading of the visible page, and the Review row's selected segment.
const onScreen = (page) => page.evaluate(() => {
  const h1 = [...document.querySelectorAll('h1')].filter((h) => h.offsetParent !== null).map((h) => h.textContent.trim());
  const sel = [...document.querySelectorAll('[role="tab"][aria-selected="true"], button[aria-current="page"], button.active, button.sem-tab-on')]
    .filter((b) => b.offsetParent !== null).map((b) => b.textContent.trim()).filter(Boolean);
  return { h1, sel };
});
const strip = (page) => page.evaluate(() => document.querySelector('[data-note-strip], .sem-note-strip')?.textContent?.trim() ?? null);

try {
  for (const theme of THEMES) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.setViewport({ width: 1280, height: 760, deviceScaleFactor: 1 });
    await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tab=plan&conn=1&tier=pro`, { waitUntil: 'networkidle0', timeout: 40000 });
    await page.waitForSelector('nav button, [data-tab]', { timeout: 15000 });
    await wait(1200);
    await applyTheme(page, theme);
    await send(page, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    const call = (tool) => send(page, { type: 'assistantCall', payload: { tool, at: new Date().toISOString(), sessionId: 'uishot', assistant: { key: 'claude', name: 'Claude Code', initials: 'CC' } } });
    const note = (id, say, place, how) => send(page, { type: 'studioShow', payload: { id, say, how, place, at: new Date().toISOString(), by: 'assistant', auto: true, sessionId: 'uishot' } });
    const t0 = Date.now();
    await call('ai_readiness_scan');
    await note('sn-follow0000001', 'Your assistant is checking how ready the model is for AI.', 'review/ai-readiness', 'Open Review, then AI readiness.');
    await wait(600);
    const first = await onScreen(page);
    console.log(`t=${Date.now() - t0} ms`, JSON.stringify(first), '|', await strip(page));
    await page.screenshot({ path: join(OUT, `follow-${theme}-1-readiness.png`) });
    await wait(Math.max(0, 1000 - (Date.now() - t0)));
    await call('bpa_scan');
    await note('sn-follow0000002', 'Your assistant is checking the model against best practice.', 'review/model-quality', 'Open Review, then Model quality.');
    await wait(Math.max(0, 2600 - (Date.now() - t0)));
    const second = await onScreen(page);
    console.log(`t=${Date.now() - t0} ms`, JSON.stringify(second), '|', await strip(page));
    await page.screenshot({ path: join(OUT, `follow-${theme}-2-bpa.png`) });
    expect(first.h1.join(' ').match(/readiness/i) !== null || first.sel.join(' ').match(/readiness/i) !== null, `${theme}: the Studio is on AI readiness after the first call (${JSON.stringify(first)})`);
    expect(JSON.stringify(second).match(/model quality|best practice|BPA/i) !== null, `${theme}: 2.6 s in, the Studio is on Model quality, where the second call works (${JSON.stringify(second)})`);
    expect(errors.length === 0, `${theme}: no page errors ${errors.join(' | ')}`);
    await page.close();
  }
} finally {
  await browser.close();
  server.close();
}
if (failures.length) { console.error(`${failures.length} check(s) failed`); process.exit(1); }
console.log('drive follow: all checks passed');
