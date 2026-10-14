// The assistant runs the other Validation tools and the page follows (testspeed2, 13 Oct). A single try_test, a recorded run, a saved check:
// one sentence above the results, and the page reads again what the tool changed. Real Studio on the mocked engine, dark and light.
// Run: node tools/uishot/validation-tools-drive.mjs   (build the webview first). OUTDIR=<dir> THEMES=dark,light. Exits 1 on a failed check.
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
const OUT = process.env.OUTDIR || join(here, 'shots/validation-tools');
const THEMES = (process.env.THEMES || 'dark,light').split(',');
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
const expect = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) failures.push(what); };
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 120000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb'] });
const send = (page, data) => page.evaluate((m) => window.dispatchEvent(new MessageEvent('message', { data: m })), data);
const title = (page) => page.evaluate(() => [...document.querySelectorAll('h1')].filter((h) => h.offsetParent !== null).map((h) => h.textContent.trim()));
const strip = (page) => page.evaluate(() => document.querySelector('[data-assistant-run]')?.textContent?.trim() ?? null);
const phase = (page) => page.evaluate(() => document.querySelector('[data-assistant-run]')?.getAttribute('data-assistant-run') ?? null);

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
    const shots = [];
    const shoot = async (name) => { const f = join(OUT, `${name}-${theme}.png`); await page.screenshot({ path: f }); shots.push(f); };

        // 1. Validation is on screen with a run on it.
    await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tab=tests&conn=1&tier=pro`, { waitUntil: 'networkidle0', timeout: 40000 });
    await wait(1500);
    await applyTheme(page, theme);
    expect((await title(page)).includes('Validation'), `${theme}: Validation is open (${await title(page)})`);
    const note = (p) => p.evaluate(() => { const n = document.querySelector('[data-assistant-note]'); return n ? { kind: n.getAttribute('data-assistant-note'), text: n.textContent.trim() } : null; });
    expect(await note(page) === null, `${theme}: no assistant note before the assistant does anything`);
    await shoot('1-before');

    // 2. The assistant tries one check: the engine sends one activity with one sentence.
    await send(page, { type: 'activity', payload: { seq: 51, kind: 'try_test', origin: 'agent', ok: true, label: 'Tried a check: Total Sales ties to the GL. The measure matched the number you trust.' } });
    await page.waitForFunction(() => document.querySelector('[data-assistant-note]'), { timeout: 8000 }).catch(() => null);
    await wait(300);
    const first = await note(page);
    expect(first && first.kind === 'try_test' && /Tried a check: Total Sales ties to the GL\./.test(first.text), `${theme}: the single try shows as one sentence (${first && first.text})`);
    await shoot('2-tried');

    // 3. A recorded run: the sentence changes and the history is read again.
    const before = await page.evaluate(() => window.__uishotHarness?.calls?.filter?.((c) => c.method === 'listTestRuns').length ?? null);
    await send(page, { type: 'activity', payload: { seq: 52, kind: 'record_test_run', origin: 'agent', ok: true, label: 'Saved the run to history.' } });
    await wait(500);
    const second = await note(page);
    expect(second && second.kind === 'record_test_run' && /Saved the run to history/.test(second.text), `${theme}: the recorded run shows (${second && second.text})`);
    const after = await page.evaluate(() => window.__uishotHarness?.calls?.filter?.((c) => c.method === 'listTestRuns').length ?? null);
    if (before !== null) expect(after > before, `${theme}: the history was read again (${before} to ${after})`);
    await shoot('3-recorded');

    // 4. A failed one says why.
    await send(page, { type: 'activity', payload: { seq: 53, kind: 'record_test_run', origin: 'agent', ok: false, label: 'The run was not saved.', error: 'Only a full run can be recorded.' } });
    await wait(400);
    const third = await note(page);
    expect(third && /Only a full run can be recorded/.test(third.text), `${theme}: a failure says why (${third && third.text})`);
    await shoot('4-failed');
    expect(errors.length === 0, `${theme}: no page errors ${errors.join(' | ')}`);
    await page.close();
  }
} finally {
  await browser.close();
  server.close();
}
if (failures.length) { console.error(`\n${failures.length} check(s) failed`); process.exit(1); }
console.log('\nvalidation tools drive passed');
