// Acceptance driver for Semanticus Pro inside the app (the mockup Kane approved on 23 September 2026).
//
// Every shot is the REAL Studio driven through the mock engine and the harness's stand-in for the Pro page's
// host (harness.html, __uishotPro), at 1440 by 900 and at 1000 by 800. The host stand-in answers the page's
// clicks with the states extension.ts sends; what it cannot prove is the host flows themselves (checkout,
// SecretStorage, the engine restart), which test/pro-in-app.test.mjs and test/pro-billing.test.mjs cover.
//
// WHAT IS PROVEN HERE, per scene, by measurement rather than by eye:
//   - the chip reads Get Pro on free and Pro on Pro, sits between Help and Your assistant, and is on screen;
//   - Help has exactly one Semanticus Pro item, and it opens the page;
//   - the chip, Help and a locked page's Unlock Pro all land on the Pro page, and Done returns;
//   - each page state renders its own words (free, key open, waiting, Pro on, Pro, the three key results,
//     not open yet), and the page never scrolls sideways.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/pro-in-app-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'pro-in-app');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `proapp-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);
const SIZES = [{ w: 1440, h: 900 }, { w: 1000, h: 800 }];

const clickText = (page, selector, text) => page.evaluate((selector, text) => {
  const el = [...document.querySelectorAll(selector)].find((x) => (x.textContent || '').trim() === text);
  if (!el) return false;
  el.click();
  return true;
}, selector, text);
const click = (page, selector) => page.evaluate((selector) => {
  const el = document.querySelector(selector);
  if (!el) return false;
  el.click();
  return true;
}, selector);
async function typeKey(page, key) {
  await page.waitForSelector('[data-testid="pro-key"]', { timeout: 5000 });
  await page.focus('[data-testid="pro-key"]');
  await page.keyboard.type(key);
  await click(page, '[data-testid="pro-activate"]');
  await wait(900);
}
async function openPageByChip(page) {
  if (!(await click(page, '[data-testid="pro-chip"]'))) throw new Error('no chip to click');
  await page.waitForSelector('[data-testid="pro-page"]', { timeout: 5000 });
  await wait(300);
}

const SCENES = [
  { slug: 'header-free-help', q: '', desc: 'Overview on free: Get Pro beside Help, and Help open with its one Pro item',
    run: async (page) => { await clickText(page, 'button[aria-label="Help"]', 'Help') || await click(page, 'button[aria-label="Help"]'); await wait(400); } },
  { slug: 'header-pro', q: 'tier=pro', desc: 'Overview on Pro: the quiet Pro chip' },
  { slug: 'help-opens-page', q: '', desc: 'Help > Semanticus Pro opens the Pro page',
    run: async (page) => {
      await click(page, 'button[aria-label="Help"]'); await wait(300);
      if (!(await clickText(page, '[role="menu"] button', 'Semanticus ProFree'))) throw new Error('no Semanticus Pro item in Help');
      await page.waitForSelector('[data-testid="pro-page"]', { timeout: 5000 }); await wait(300);
    }, expectPage: 'idle' },
  { slug: 'page-free', q: '', desc: 'The Pro page, free, opened from the chip', run: openPageByChip, expectPage: 'idle' },
  { slug: 'page-free-key', q: '', desc: 'Have a license key? opened',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-key-open"]'); await wait(300); }, expectPage: 'idle' },
  { slug: 'page-monthly', q: '', desc: 'Monthly picked',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-plan-monthly"]'); await wait(300); }, expectPage: 'idle' },
  { slug: 'page-waiting', q: '', desc: 'After Buy Pro: waiting for payment in the browser',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-buy"]'); await wait(500); }, expectPage: 'waiting' },
  { slug: 'page-on', q: '', desc: 'The license arrived: Pro is on, and the chip turned Pro',
    run: async (page) => {
      await openPageByChip(page); await click(page, '[data-testid="pro-buy"]'); await wait(500);
      await page.evaluate(() => window.__uishotPro.finish()); await wait(1400);
    }, expectPage: 'on', expectChip: 'Pro' },
  { slug: 'page-pro', q: 'tier=pro', desc: 'Coming back later with Pro', run: openPageByChip, expectPage: 'pro' },
  { slug: 'key-ok', q: '', desc: 'A good key: Pro turns on',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-key-open"]'); await typeKey(page, 'eyJzdWIiOiJ5b3UifQ.c2ln'); await wait(800); },
    expectPage: 'on', expectChip: 'Pro' },
  { slug: 'key-invalid', q: '', desc: 'A key that is not valid',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-key-open"]'); await typeKey(page, 'bad'); }, expectText: 'That key is not valid.' },
  { slug: 'key-expired', q: '', desc: 'An expired license',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-key-open"]'); await typeKey(page, 'old'); }, expectText: 'That license has expired.' },
  { slug: 'page-not-open', q: 'pay=closed', desc: 'Buy Pro before payment is open: the notice, and the key field open with the cursor in it',
    run: async (page) => { await openPageByChip(page); await click(page, '[data-testid="pro-buy"]'); await wait(500); },
    expectText: 'Buying in the app is not open yet.', expectFocus: 'pro-key' },
  { slug: 'unlock-locked', q: 'tab=workflows', desc: 'The locked Workflows page: Unlock Pro opens the Pro page', settle: 2800,
    run: async (page) => {
      await page.screenshot({ path: join(OUT, `unlock-locked-before-${page.viewport().width}.png`) });
      if (!(await click(page, '[data-testid="pro-preview-unlock"]'))) throw new Error('no Unlock Pro on the locked page');
      await page.waitForSelector('[data-testid="pro-page"]', { timeout: 5000 }); await wait(300);
    }, expectPage: 'idle' },
  { slug: 'done-returns', q: '', desc: 'Done returns to where you were (Overview)',
    run: async (page) => {
      await openPageByChip(page);
      if (!(await click(page, '[data-testid="settings-done"]'))) throw new Error('no Done on the Pro page');
      await wait(500);
    }, expectNoPage: true },
];

const results = [];
for (const scene of SCENES) {
  if (ONLY.length && !ONLY.includes(scene.slug)) continue;
  for (const size of SIZES) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await page.setViewport({ width: size.w, height: size.h, deviceScaleFactor: 1 });
    const record = { slug: scene.slug, width: size.w, desc: scene.desc, ok: true, problems: [] };
    try {
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${scene.q}`, { waitUntil: 'networkidle0', timeout: 45000 });
      await page.waitForSelector('.studio-root', { timeout: 20000 });
      await wait(scene.settle ?? 1400);
      if (scene.run) await scene.run(page);
      const seen = await page.evaluate(() => {
        const chip = document.querySelector('[data-testid="pro-chip"]');
        const help = document.querySelector('button[aria-label="Help"]');
        const assistant = [...document.querySelectorAll('.studio-actions button')].find((b) => /Your assistant|assistant/i.test(b.getAttribute('title') || b.textContent || ''));
        const pp = document.querySelector('[data-testid="pro-page"]');
        const main = document.querySelector('main');
        const r = (el) => el ? el.getBoundingClientRect() : null;
        return {
          chip: chip ? { text: chip.textContent.trim(), cls: chip.className, rect: r(chip).toJSON() } : null,
          helpRight: help ? r(help).right : null,
          assistantLeft: assistant ? r(assistant).left : null,
          pageState: pp ? pp.getAttribute('data-state') : null,
          text: pp ? pp.innerText : '',
          arearow: (document.querySelector('.studio-arearow') || {}).innerText || '',
          proItems: [...document.querySelectorAll('[role="menu"] button')].filter((b) => /Semanticus Pro|Get Pro|Manage subscription/.test(b.textContent)).map((b) => b.textContent.trim()),
          sideways: main ? main.scrollWidth - main.clientWidth : 0,
          docSideways: document.documentElement.scrollWidth - document.documentElement.clientWidth,
          focus: document.activeElement ? document.activeElement.getAttribute('data-testid') : null,
          proMessages: (window.__uishotHarness.messages || []).filter((m) => m && m.type === 'pro').map((m) => m.action),
          hostCommands: (window.__uishotHarness.messages || []).filter((m) => m && m.type === 'runCommand').map((m) => m.command),
        };
      });
      Object.assign(record, { seen });
      const fail = (why) => { record.ok = false; record.problems.push(why); };
      if (!seen.chip) fail('no chip in the header');
      else {
        if (seen.chip.rect.width < 20 || seen.chip.rect.right > size.w) fail('the chip is not fully on screen');
        if (seen.helpRight != null && seen.chip.rect.left < seen.helpRight) fail('the chip is not right of Help');
        const wantChip = scene.expectChip ?? (/tier=pro/.test(scene.q) ? 'Pro' : 'Get Pro');
        if (seen.chip.text !== wantChip) fail(`chip reads "${seen.chip.text}", wanted "${wantChip}"`);
      }
      if (scene.slug === 'header-free-help') {
        if (seen.proItems.length !== 1 || !/^Semanticus Pro/.test(seen.proItems[0])) fail(`Help's Pro items: ${JSON.stringify(seen.proItems)}`);
      }
      if (scene.expectPage && seen.pageState !== scene.expectPage) fail(`page state ${seen.pageState}, wanted ${scene.expectPage}`);
      if (scene.expectPage && !/Semanticus Pro/.test(seen.arearow)) fail(`the row reads "${seen.arearow}"`);
      if (scene.expectNoPage && seen.pageState) fail('Done did not leave the page');
      if (scene.expectText && !seen.text.includes(scene.expectText)) fail(`missing "${scene.expectText}"`);
      if (scene.expectFocus && seen.focus !== scene.expectFocus) fail(`focus on ${seen.focus}, wanted ${scene.expectFocus}`);
      if (seen.sideways > 1 || seen.docSideways > 1) fail(`scrolls sideways by ${Math.max(seen.sideways, seen.docSideways)}px`);
      if (/\u2014/.test(seen.text)) fail('an em dash on the page');
      if (seen.hostCommands.some((c) => /upgradeToPro|manageSubscription/.test(c))) fail(`reached an old licence command: ${seen.hostCommands}`);
      if (errors.length) fail(errors.join(' | '));
      // One Visual Language: once per scene, the page measured at 1440x900 against its screen baseline. When
      // the Pro page is open its own box is the page; otherwise the Studio page under the header is.
      if (size === SIZES[0]) {
        const screen = await checkScreen(page, `pro-in-app:${scene.slug}`, { root: seen.pageState ? '[data-testid="pro-page"]' : null });
        for (const f of screen.failures) fail(f);
      }
      await page.screenshot({ path: join(OUT, `${scene.slug}-${size.w}.png`), fullPage: false });
    } catch (e) {
      record.ok = false; record.problems.push(String(e?.message ?? e));
      try { await page.screenshot({ path: join(OUT, `${scene.slug}-${size.w}-FAILED.png`) }); } catch { /* nothing */ }
    }
    results.push(record);
    console.log(`${record.ok ? 'PASS' : 'FAIL'}  ${scene.slug} @${size.w}  ${record.ok ? '' : record.problems.join('; ')}`);
    await page.close();
  }
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
await browser.close();
server.close();
const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} scenes passed. Shots in ${OUT}`);
process.exit(failed.length ? 1 : 0);
