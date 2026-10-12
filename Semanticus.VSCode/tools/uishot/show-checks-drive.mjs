// The assistant runs checks and the Studio follows (show-checks, Kane 12 Oct: "when the assistant runs checks in validation the screen
// doesn't appear to change"). Real Studio on the mocked engine, in Dark Modern and Light Modern. The scene posts what the host posts:
// the assistant's call (assistantCall), the engine's note (studioShow), then the run itself (activity, kind run_tests).
//   1. the Plan is on screen and Claude drives
//   2. the call and the note arrive: the Studio opens Validation and the line above the results says the assistant is running the checks
//   3. the run arrives: the grade, the counts and the results are the assistant's run, with one sentence that says what it found
// Run: node tools/uishot/show-checks-drive.mjs   (build the webview first). OUTDIR=<dir> THEMES=dark,light. Exits 1 on a failed check.
// Writes show-checks-strip-<theme>.png, the three scenes side by side.
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
const OUT = process.env.OUTDIR || join(here, 'shots/show-checks');
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

    // 1. The Plan is on screen.
    expect(!(await title(page)).includes('Validation'), `${theme}: the person starts on the Plan, not on Validation (${await title(page)})`);
    await shoot('1-plan');

    // 2. The assistant calls run_tests: the engine's call event, then its note. The Studio opens Validation and says the run has started.
    const at = new Date().toISOString();
    await send(page, { type: 'assistantCall', payload: { tool: 'run_tests', at, sessionId: 'uishot', assistant: { key: 'claude', name: 'Claude Code', initials: 'CC' } } });
    await send(page, { type: 'studioShow', payload: { id: 'sn-run000000001', say: 'Your assistant is running your checks.', how: 'Open Review, then Validation.', place: 'review/validation', at, by: 'assistant', sessionId: 'uishot', auto: true } });
    await page.waitForFunction(() => [...document.querySelectorAll('h1')].some((h) => h.offsetParent !== null && h.textContent.trim() === 'Validation'), { timeout: 8000 }).catch(() => null);
    await wait(500);
    expect((await title(page)).includes('Validation'), `${theme}: the Studio opened Validation (${await title(page)})`);
    expect(await phase(page) === 'running' && /running your checks/.test(await strip(page) || ''), `${theme}: the page says the assistant is running the checks (${await strip(page)})`);
    await shoot('2-running');

    // 3. The run arrives, the way the engine sends it: an activity with the whole run.
    const run = await page.evaluate(() => window.__uishotHarness.response('runTests', [false, 'agent', null, null]));
    await send(page, { type: 'activity', payload: { seq: 41, kind: 'run_tests', origin: 'agent', ok: true, label: `Ran your checks: grade ${run.health.grade}, ${run.health.passed} of ${run.health.checked} pass.`, elapsedMs: 4100, result: run } });
    await page.waitForFunction(() => document.querySelector('[data-assistant-run]')?.getAttribute('data-assistant-run') === 'done', { timeout: 8000 }).catch(() => null);
    await wait(400);
    expect(await phase(page) === 'done' && /Ran your checks: grade [A-F], \d+ of \d+ pass/.test(await strip(page) || ''), `${theme}: the page says what the run found (${await strip(page)})`);
    const status = await page.evaluate(() => document.querySelector('.sem-tests-status')?.textContent ?? '');
    expect(/pass/.test(status) && !/No run yet/.test(status), `${theme}: the grade and the counts are the assistant's run (${status.slice(0, 90)})`);
    const rows = await page.evaluate(() => document.body.innerText);
    expect(/Total Sales ties to the GL/.test(rows), `${theme}: the saved checks show their results`);
    await shoot('3-result');

    // 4. A run that did not happen says why, and does not leave an old grade as if it were new.
    await send(page, { type: 'assistantCall', payload: { tool: 'run_tests', at: new Date().toISOString(), sessionId: 'uishot' } });
    await send(page, { type: 'activity', payload: { seq: 42, kind: 'run_tests', origin: 'agent', ok: false, label: 'Your checks did not run.', error: 'Held for your approval.', result: null } });
    await page.waitForFunction(() => document.querySelector('[data-assistant-run]')?.getAttribute('data-assistant-run') === 'failed', { timeout: 8000 }).catch(() => null);
    expect(await phase(page) === 'failed' && /Held for your approval/.test(await strip(page) || ''), `${theme}: a run that did not happen says why (${await strip(page)})`);
    await shoot('4-did-not-run');
    expect(errors.length === 0, `${theme}: no page errors ${errors.join(' | ')}`);

    // The strip: the first three scenes side by side.
    const imgs = shots.slice(0, 3).map((f) => 'data:image/png;base64,' + readFileSync(f).toString('base64'));
    const sp = await browser.newPage();
    await sp.setViewport({ width: 3 * 640 + 40, height: 420, deviceScaleFactor: 1 });
    await sp.setContent(`<body style="margin:0;background:${theme === 'dark' ? '#111' : '#ddd'};display:flex;gap:20px;padding:10px;font:600 13px sans-serif;color:${theme === 'dark' ? '#eee' : '#222'}">${
      imgs.map((s, i) => `<div style="width:640px"><div style="padding:0 0 6px">${['1. Plan', '2. Claude runs checks', '3. The result'][i]}</div><img src="${s}" style="width:640px;border:1px solid #888"></div>`).join('')}</body>`);
    await sp.screenshot({ path: join(OUT, `show-checks-strip-${theme}.png`) });
    await sp.close();
    await page.close();
  }
} finally {
  await browser.close();
  server.close();
}
if (failures.length) { console.error(`\n${failures.length} check(s) failed`); process.exit(1); }
console.log('\nshow-checks drive passed');
