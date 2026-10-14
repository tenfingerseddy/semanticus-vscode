// Browser proof of The Link (1.5): the status line with working verbs, the driver bar, the Help switches and the 200 px Link on the
// first-run card, in the real Studio bundle with the uishot engine fixture. Every animation is captured as a frame strip by pausing
// the page's Web Animations and stepping their time, so the frames are exact, not a race with the clock.
//   OUTDIR=<dir> [THEMES=dark,light] node tools/uishot/link-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const { default: puppeteer } = await import('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(here, 'shots', 'link');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark,light').split(',');
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
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };

const seed = (firstRun) => `
${FIXTURE_SRC}
const __plan = makePlan({});
window.__host = [];
const reply = (m) => setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: m })), 0);
let realApi;
Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
  get: () => function () {
    const api = realApi(); const post = api.postMessage;
    api.postMessage = function (msg) {
      const t = msg && msg.type;
      window.__host.push(msg);
      if (t === 'getFirstRun') { reply({ type: 'firstRun', id: msg.id, show: ${firstRun}, connected: ${!firstRun} }); return; }
      if (t === 'firstRunConnect') { setTimeout(() => reply({ type: 'firstRunConnected', id: msg.id, ok: true }), 2500); return; }
      if (t === 'firstRunClose' || t === 'setGuideFlag') return;
      if (t === 'getGuideFlags') { reply({ type: 'guideFlags', id: msg.id, planTour: false, whatChanged: false }); return; }
      if (t === 'setPersonalitySetting') return;
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

async function open(ctx, theme, firstRun) {
  const page = await ctx.newPage();
  page.on('pageerror', (e) => { failed += 1; console.error('pageerror: ' + e.message); });
  await page.setViewport({ width: 1280, height: 800, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(firstRun));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(800);
  return page;
}
const send = (page, data) => page.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), data);
const settings = (page, s) => send(page, { type: 'personalitySettings', personality: 'on', sounds: false, soundVolume: 40, reduceMotion: 'off', ...s });
const call = (page, tool) => send(page, { type: 'assistantCall', payload: { tool, at: new Date().toISOString() } });
const activity = (page, e) => send(page, { type: 'activity', payload: { seq: 1, kind: 'run_dax', origin: 'agent', ...e } });
const rect = (page, sel) => page.$eval(sel, (el) => { const r = el.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height }; });
const pad = (r, p) => ({ x: Math.max(0, r.x - p), y: Math.max(0, r.y - p), width: r.width + 2 * p, height: r.height + 2 * p });
const png = async (page, clip, scale = 1) => (await page.screenshot({ clip: { ...clip, scale }, encoding: 'base64' }));

/** Pause every animation inside `sel` and capture `n` frames across `ms` milliseconds of its time. */
async function frames(page, sel, ms, n, clip, scale, fromZero = false) {
  const out = [];
  await page.evaluate((s) => { for (const a of document.querySelector(s).getAnimations({ subtree: true })) a.pause(); }, sel);
  const start = fromZero ? 0 : await page.evaluate((s) => Math.min(...document.querySelector(s).getAnimations({ subtree: true }).map((a) => Number(a.currentTime) || 0)), sel);
  for (let i = 0; i < n; i++) {
    const t = start + (i * ms) / (n - 1 || 1);
    await page.evaluate((s, time) => {
      for (const a of document.querySelector(s).getAnimations({ subtree: true })) a.currentTime = time;
    }, sel, t);
    out.push({ label: `${Math.round(t - start)} ms`, src: await png(page, clip, scale) });
  }
  // Resume: a looping animation plays on; a one-shot one is finished (play() on a finished animation would replay it).
  await page.evaluate((s) => { for (const a of document.querySelector(s).getAnimations({ subtree: true })) { if (a.effect.getComputedTiming().iterations === Infinity) a.play(); else a.finish(); } }, sel);
  return out;
}

/** One PNG that lays captures out in a row with a caption under each, on the theme's own background. */
async function strip(ctx, file, title, items, bg, fg, cell = 0) {
  const page = await ctx.newPage();
  const html = `<!doctype html><meta charset=utf-8><body style="margin:0;background:${bg};color:${fg};font:13px system-ui,sans-serif">
    <div style="padding:12px 16px 4px;font-weight:600">${title}</div>
    <div style="display:flex;flex-wrap:wrap;gap:12px;padding:8px 16px 16px;align-items:flex-end">
    ${items.map((it) => `<figure style="margin:0;display:grid;gap:6px;justify-items:center"><img src="data:image/png;base64,${it.src}" style="${cell ? `width:${cell}px;` : ''}image-rendering:auto;border:1px solid ${fg}22"><figcaption>${it.label}</figcaption></figure>`).join('')}
    </div></body>`;
  await page.setViewport({ width: 1400, height: 400, deviceScaleFactor: 1 });
  await page.setContent(html, { waitUntil: 'load' });
  const h = await page.evaluate(() => document.body.scrollHeight);
  await page.setViewport({ width: 1400, height: h, deviceScaleFactor: 1 });
  await page.screenshot({ path: join(OUT, file) });
  await page.close();
}

const ctx = await browser.createBrowserContext();
for (const theme of THEMES) {
  console.log(`== ${theme}`);
  const [bg, fg] = theme === 'dark' ? ['#1F1F1F', '#CCCCCC'] : ['#FFFFFF', '#3B3B3B'];
  const page = await open(ctx, theme, false);
  await settings(page, {});
  await send(page, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
  await page.waitForSelector('[data-testid="link-status"]');
  await page.waitForSelector('[data-testid="studio-driver-bar"] .linkchar');
  const bar = await rect(page, '.studio-contextbar');
  const barClip = { x: bar.width / 2, y: bar.y - 2, width: bar.width / 2, height: bar.height + 4 };
  const lineShots = [];
  check(await page.$eval('[data-testid="link-status"]', (e) => e.dataset.linkWork) === 'idle', 'the status Link starts idle');
  lineShots.push({ label: 'Idle', src: await png(page, barClip, 2) });
  await page.screenshot({ path: join(OUT, `${theme}-studio-idle.png`) });

  // Working: a DAX query.
  await call(page, 'run_dax');
  await wait(400);
  const words1 = await page.$eval('.sem-status-word:not(.is-leaving)', (e) => e.textContent);
  check(/CALCULATE|filter|SUMX|VAR|RETURN|ALL|KEEPFILTERS|context|DIVIDE|SWITCH|Iterating|Measuring/.test(words1), `a DAX job shows a DAX verb (${words1})`);
  lineShots.push({ label: 'Working (DAX)', src: await png(page, barClip, 2) });
  await page.screenshot({ path: join(OUT, `${theme}-studio-working.png`) });
  const linkSel = '[data-testid="link-status"] .linkchar';
  const lr = pad(await rect(page, linkSel), 3);
  await strip(ctx, `${theme}-frames-working-16.png`, `Working, 16 px, one 2.4 s loop (${theme})`, await frames(page, linkSel, 2400, 9, lr, 6), bg, fg);
  const dr = pad(await rect(page, '[data-testid="studio-driver-bar"]'), 0);
  await page.screenshot({ path: join(OUT, `${theme}-driverbar-working.png`), clip: { x: 0, y: dr.y, width: 640, height: dr.height } });
  await wait(2400);
  const words2 = await page.$eval('.sem-status-word:not(.is-leaving)', (e) => e.textContent);
  check(words2 !== words1, `the verb moves on calmly (${words1} then ${words2})`);
  lineShots.push({ label: 'Next verb, 2.3 s later', src: await png(page, barClip, 2) });
  // Thinking: past 8 seconds.
  await wait(6000);
  check(await page.$eval('[data-testid="link-status"]', (e) => e.dataset.linkWork) === 'thinking', 'past 8 seconds it thinks hard');
  lineShots.push({ label: 'Thinking hard (past 8 s)', src: await png(page, barClip, 2) });
  await strip(ctx, `${theme}-frames-thinking-16.png`, `Thinking hard, 16 px, one 1.8 s loop (${theme})`, await frames(page, linkSel, 1800, 7, pad(await rect(page, linkSel), 3), 6), bg, fg);
  check(Math.abs((await rect(page, linkSel)).x - (lr.x + 3)) < 0.5, 'the Link has not moved while the verbs changed');
  // Success: the result lands, the quiet passes.
  await activity(page, { ok: true, words: { verb: 'ran', noun: 'query', count: 1, targets: [], sentence: 'Ran the query: 1,204 rows.' } });
  await page.waitForFunction(() => document.querySelector('[data-testid="link-status"]').dataset.linkWork === 'success', { timeout: 6000 });
  await wait(350);
  lineShots.push({ label: 'Success', src: await png(page, barClip, 2) });
  const okFrames = await frames(page, linkSel, 1900, 9, pad(await rect(page, linkSel), 3), 6, true);
  await strip(ctx, `${theme}-frames-success-16.png`, `Success, 16 px, 1.9 s, plays once (${theme})`, okFrames, bg, fg);
  await page.waitForFunction(() => document.querySelector('[data-testid="link-status"]').dataset.linkWork === 'idle', { timeout: 6000 });
  check(true, 'success rests to idle');
  // Error.
  await call(page, 'run_dax');
  await wait(200);
  await activity(page, { ok: false, error: 'The query names a column that does not exist.' });
  await wait(300);
  const errWords = await page.$eval('.sem-status-word:not(.is-leaving)', (e) => e.textContent);
  check(errWords === 'Stopped. The query names a column that does not exist.', `error says what broke, no joke (${errWords})`);
  check(await page.$eval(linkSel, (e) => e.getAnimations({ subtree: true }).length) === 0, 'error is still');
  lineShots.push({ label: 'Error (grey, still)', src: await png(page, barClip, 2) });
  // Quiet: plain words.
  await settings(page, { personality: 'quiet' });
  await call(page, 'edit_objects');
  await wait(400);
  const quietWords = await page.$eval('.sem-status-word:not(.is-leaving)', (e) => e.textContent);
  check(quietWords === 'Editing the model', `Quiet says the plain words (${quietWords})`);
  lineShots.push({ label: 'Quiet', src: await png(page, barClip, 2) });
  await wait(3200);
  // Reduced motion.
  await settings(page, { reduceMotion: 'on' });
  await call(page, 'create_relationship');
  await wait(300);
  check(await page.$eval(linkSel, (e) => e.dataset.linkReduced === 'yes'), 'reduced motion reaches the Link');
  lineShots.push({ label: 'Reduced motion, working', src: await png(page, barClip, 2) });
  await strip(ctx, `${theme}-frames-reduced-16.png`, `Reduced motion, working, 16 px: dots still, the bar fades (${theme})`, await frames(page, linkSel, 1600, 5, pad(await rect(page, linkSel), 3), 6), bg, fg);
  await strip(ctx, `${theme}-statusline.png`, `The status line, every state (${theme})`, lineShots, bg, fg, 640);
  await settings(page, {});
  await wait(3000);
  // Help: Personality and Sounds.
  await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === 'Help')?.click());
  await page.waitForSelector('.studio-help-switch');
  const menu = pad(await rect(page, '.studio-help-menu'), 8);
  await page.screenshot({ path: join(OUT, `${theme}-help-switches.png`), clip: menu });
  await page.evaluate(() => [...document.querySelectorAll('.studio-help-switch button')].find((b) => b.textContent === 'Quiet')?.click());
  await wait(100);
  const sent = await page.evaluate(() => window.__host.filter((m) => m.type === 'setPersonalitySetting').map((m) => `${m.key}=${m.value}`));
  check(sent.includes('personality=quiet'), `Help > Personality > Quiet writes the setting (${sent.join(', ')})`);
  check(await page.$eval('.studio-help-switch button.is-on', (b) => b.textContent) === 'Quiet', 'the switch shows Quiet at once');
  await page.screenshot({ path: join(OUT, `${theme}-help-switches-quiet.png`), clip: menu });
  await page.close();

  // The 200 px Link on the first-run card.
  const fr = await open(ctx, theme, true);
  await settings(fr, {});
  await fr.waitForSelector('.first-run-link .linkchar');
  await wait(300);
  await fr.screenshot({ path: join(OUT, `${theme}-firstrun-idle.png`) });
  const big = pad(await rect(fr, '.first-run-link .linkchar'), 6);
  const bigSel = '.first-run-link';
  await strip(ctx, `${theme}-frames-idle-200.png`, `Idle, 200 px, the dots breathe, one 3.2 s loop (${theme})`, await frames(fr, bigSel, 3200, 5, big, 1), bg, fg, 150);
  await fr.evaluate(() => [...document.querySelectorAll('.first-run button')].find((b) => /Connect/.test(b.textContent))?.click());
  await wait(500);
  check(await fr.$eval('.first-run-link .linkchar', (e) => e.dataset.linkState) === 'working', 'the welcome Link works while the connect runs');
  await strip(ctx, `${theme}-frames-working-200.png`, `Working, 200 px, one 2.4 s loop (${theme})`, await frames(fr, bigSel, 2400, 7, big, 1), bg, fg, 150);
  await fr.waitForSelector('.first-run[aria-label="Your assistant is connected"]', { timeout: 6000 });
  const big2 = pad(await rect(fr, '.first-run-link .linkchar'), 6);
  await strip(ctx, `${theme}-frames-success-200.png`, `Success, 200 px: the dots meet, the bar lights, they go home (${theme})`, await frames(fr, bigSel, 1900, 9, big2, 1, true), bg, fg, 150);
  await fr.screenshot({ path: join(OUT, `${theme}-firstrun-done.png`) });
  await fr.close();
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'result.txt'), failed ? `${failed} FAILED\n` : 'all checks passed\n');
console.log(failed ? `${failed} FAILED` : 'link drive: all checks passed');
process.exit(failed ? 1 : 0);
