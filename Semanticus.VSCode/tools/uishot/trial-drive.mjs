// Acceptance driver for the free Pro month (m15 trial, option B). Every screen, light and dark, at 1440 and 400 wide,
// through the real Studio and the harness's stand-in for the Pro page's host (harness.html, ?trial=, __uishotTrialConfirm).
// Each shot is also measured: the words that must be there, no sideways scroll, no em dash, no console errors.
//
//   OUTDIR=<dir> TMPDIR=<dir> node tools/uishot/trial-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'trial');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `trial-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-heart-fixture.mjs'), 'utf8').replace(/^export /gm, '');
// A free plan with rows, so the Plan's own Unlock Pro is there to press (cp-plan-drive's seed, cut to the one read).
const PLAN_SEED = `
${FIXTURE_SRC}
let __plan = makeHeartPlan({ mode: 'plain', free: true });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h,
  set: (v) => { __h = v; v.setResponse('getModelPlan', () => __plan); } });
`;
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const click = (page, sel) => page.evaluate((s) => { const el = document.querySelector(s); if (!el) return false; el.click(); return true; }, sel);
async function startWith(page, email) {
  await page.waitForSelector('[data-testid="trial-email"]', { timeout: 5000 });
  await page.focus('[data-testid="trial-email"]');
  await page.keyboard.type(email);
  await click(page, '[data-testid="trial-start"]');
  await wait(600);
}

const SCENES = [
  { slug: 'start-offer', q: 'coldNav=pro', want: ['Try Pro free for 30 days', 'Start free month', 'Email only. No card.'] },
  { slug: 'start-waiting', q: 'coldNav=pro', run: (p) => startWith(p, 'ana@contoso.com'), want: ['Check your email and press the link. This page updates by itself.'] },
  { slug: 'start-on', q: 'coldNav=pro', run: async (p) => { await startWith(p, 'ana@contoso.com'); await p.evaluate(() => window.__uishotTrialConfirm()); await wait(1200); }, want: ['Your Pro month', 'Pro is on for this computer.'] },
  { slug: 'locked-offer', q: 'conn=1&live=1&coldNav=plan', run: async (p) => { await p.waitForSelector('[data-testid="plan-unlock"]', { timeout: 8000 }); await click(p, '[data-testid="plan-unlock"]'); await wait(800); }, want: ['Run the plan', 'It runs as soon as Pro is on.'] },
  { slug: 'locked-runs', q: 'conn=1&live=1&coldNav=plan', run: async (p) => {
      await p.waitForSelector('[data-testid="plan-unlock"]', { timeout: 8000 }); await click(p, '[data-testid="plan-unlock"]'); await wait(600);
      await startWith(p, 'ana@contoso.com');
      p.__before = await p.evaluate(() => (window.__uishotHarness.messages || []).length);
      await p.evaluate(() => window.__uishotTrialConfirm()); await wait(1800);
    }, root: 'main', want: [], check: async (p) => {
      const after = await p.evaluate((n) => (window.__uishotHarness.messages || []).slice(n).filter((m) => m && m.type === 'rpc').map((m) => m.method), p.__before);
      const onPlan = await p.evaluate(() => !!document.querySelector('[data-testid="plan-page"]'));
      if (!onPlan) return 'did not go back to the Plan';
      return after.some((m) => /plan|Plan|apply|Apply|run|Run/.test(m) && !/getModelPlan|getEntitlement|getTrialTally/.test(m)) ? null : `the pressed thing did not run; rpc after confirm: ${after.join(',')}`;
    } },
  { slug: 'day1', q: 'coldNav=pro&trial=day1', want: ['Your first week with Pro', 'Apply many changes at once', 'Done'] },
  { slug: 'during', q: 'coldNav=pro&trial=during', want: ['Your Pro month', '1,240', 'changes applied in 14 steps', 'things Jev chose for you', 'Nothing here is sent anywhere.', 'Keep Pro'] },
  { slug: 'last3', q: 'coldNav=pro&trial=last3', want: ['Pro trial ends in 3 days.', 'Keep Pro'] },
  { slug: 'last3-plan', q: 'coldNav=plan&trial=last3', root: 'main', want: ['Pro trial ends in 3 days.'] },
  { slug: 'keep', q: 'coldNav=pro&trial=during', run: async (p) => { await click(p, '[data-testid="trial-keep"]'); await wait(400); }, want: ['Buy Pro', 'Monthly', 'Yearly'] },
  { slug: 'end', q: 'coldNav=pro&trial=end', want: ['Your Pro month', 'You are back on Semanticus Free. Nothing you made is lost.', 'What is different now', 'Keep Pro', 'Not now'] },
  { slug: 'used', q: 'coldNav=pro', run: (p) => startWith(p, 'used@contoso.com'), want: ['Your free month is used', 'Buy Pro'] },
  { slug: 'used-after-end', q: 'coldNav=pro&trial=end', run: async (p) => { await click(p, '[data-testid="trial-keep"]'); await wait(400); }, want: ['Your free month is used', 'It ran from 11 October to 10 November 2026.', 'Buy Pro'] },
];
const SIZES = [{ w: 1440, h: 900 }, { w: 400, h: 860 }];
const THEMES = ['light', 'dark'];
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);
const results = [];
for (const scene of SCENES) {
  if (ONLY.length && !ONLY.includes(scene.slug)) continue;
  for (const theme of THEMES) for (const size of SIZES) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await page.setViewport({ width: size.w, height: size.h, deviceScaleFactor: 1 });
    const record = { slug: scene.slug, theme, width: size.w, ok: true, problems: [] };
    const fail = (why) => { record.ok = false; record.problems.push(why); };
    try {
      if (/coldNav=plan/.test(scene.q)) await page.evaluateOnNewDocument(PLAN_SEED);
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${scene.q}`, { waitUntil: 'networkidle0', timeout: 45000 });
      await page.waitForSelector('.studio-root', { timeout: 20000 });
      await applyTheme(page, theme);
      await wait(1400);
      if (scene.run) await scene.run(page);
      const seen = await page.evaluate((rootSel) => {
        const root = document.querySelector(rootSel) || document.body;
        const main = document.querySelector('main');
        return {
          text: root.innerText,
          sideways: main ? main.scrollWidth - main.clientWidth : 0,
          docSideways: document.documentElement.scrollWidth - document.documentElement.clientWidth,
          over: [...root.querySelectorAll('button, .pp-big, .pp-lbl, h1, h2')].filter((el) => el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflow !== 'visible').map((el) => el.textContent.trim().slice(0, 40)),
        };
      }, scene.root ?? '[data-testid="pro-page"]');
      for (const w of scene.want) if (!seen.text.includes(w)) fail(`missing "${w}"`);
      // The Studio header itself is wider than 400 px today (not this lane's); the page body must not be.
      if (seen.sideways > 1) fail(`the page scrolls sideways by ${seen.sideways}px`);
      record.headerSideways = seen.docSideways;
      if (/—/.test(seen.text)) fail('an em dash');
      if (/one click/i.test(seen.text)) fail('says one click');
      if (seen.over.length) fail(`clipped: ${seen.over.join(' | ')}`);
      if (scene.check) { const why = await scene.check(page); if (why) fail(why); }
      if (errors.length) fail(errors.join(' | '));
      const file = join(OUT, `${scene.slug}-${theme}-${size.w}.png`);
      await page.screenshot({ path: file, fullPage: true });
      record.png = file;
    } catch (e) {
      fail(String(e?.message ?? e));
      try { await page.screenshot({ path: join(OUT, `${scene.slug}-${theme}-${size.w}-FAILED.png`) }); } catch { /* nothing */ }
    }
    results.push(record);
    console.log(`${record.ok ? 'ok  ' : 'FAIL'} ${scene.slug} ${theme} ${size.w}${record.problems.length ? '  ' + record.problems.join('; ') : ''}`);
    await page.close();
  }
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
await browser.close();
server.close();
const bad = results.filter((r) => !r.ok).length;
console.log(`trial-drive: ${results.length - bad} of ${results.length} passed`);
process.exit(bad ? 1 : 0);
