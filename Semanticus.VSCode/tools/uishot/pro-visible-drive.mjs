// Pro notes and recorded Jev choices, in the real Studio bundle with the uishot mock engine.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { makeStartPlanState, startPlanView } from './fixtures/startplan-fixture.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');
const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const out = process.env.OUTDIR || resolve(webRoot, 'shots', 'pro-visible');
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const path = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!path.startsWith(webRoot) || !existsSync(path) || statSync(path).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[extname(path)] || 'application/octet-stream' }); res.end(readFileSync(path));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const scratch = mkdtempSync(join(resolve(process.env.UISHOT_SCRATCH || '/tmp'), 'pro-visible-browser-'));
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', `--user-data-dir=${scratch}`] });
const results = [];
try {
  for (const theme of ['dark', 'light']) {
    for (const [name, say, upgrade] of (process.env.ONLY_MARKS ? [] : [
      ['before', 'Your assistant is running the ready part of your plan.', false],
      ['pro-fixes', 'Pro applied 12 best-practice fixes in one step. One undo takes them back.', false],
      ['free-fixes', 'Pro would apply 12 best-practice fixes in one step.', true],
      ['jev-build', 'Pro built the model with 4 Jev choices in one step. One undo takes the build back.', false],
    ])) {
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
      await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tab=plan&tier=${upgrade ? 'free' : 'pro'}`, { waitUntil: 'networkidle0' });
      await applyTheme(page, theme);
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'driveMode', mode: 'iDrive', assistantConnected: true } })));
      await page.evaluate(({ say, upgrade, name }) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'studioShow', payload: {
        id: `sn-pro-${name}`, say, upgrade, proStep: name !== 'before', auto: true, by: 'assistant',
        how: 'Open Plan. The top rows are the work worth doing next.', place: 'plan', at: new Date().toISOString(), sessionId: 'mock-session',
      } } })), { say, upgrade, name });
      await page.waitForFunction(say => document.querySelector('[data-testid="note-say"]')?.textContent === say, {}, say);
      assert.equal(!!await page.$('[data-testid="note-upgrade"]'), upgrade);
      if (upgrade) assert.equal(await page.$eval('[data-testid="note-upgrade"]', el => el.textContent), 'Unlock Pro');
      assert.deepEqual(errors, []);
      const file = join(out, `${name}-${theme}.png`);
      await page.screenshot({ path: file });
      results.push(file);
      await page.close();
    }
    for (const width of [1280, 900]) {
      const page = await browser.newPage();
      const view = startPlanView(makeStartPlanState({ decide: 'ready', granted: true }));
      await page.evaluateOnNewDocument(view => {
        window.__UISHOT__ = { session: { sessionId: 'mock-session', source: view.destination.modelPath,
          modelName: 'Contoso Consulting', revision: 1, tables: 0, measures: 0, liveBound: false } };
        let harness;
        Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => harness, set: value => {
          harness = value;
          value.setResponse('getStartPlan', view);
        } });
      }, view);
      await page.setViewport({ width, height: 1100, deviceScaleFactor: 1 });
      await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tab=plan&tier=pro`, { waitUntil: 'networkidle0' });
      await page.waitForSelector('[data-testid="sp-page"]');
      await applyTheme(page, theme);
      assert.equal(await page.$eval('.sp-row[data-who="jev"][data-kept="yes"] [data-who="jev"]', el => el.textContent.includes('Jev chose this')), true);
      const unsureMark = await page.$eval('.sp-row[data-who="jev"][data-kept="no"] [data-who="jev"]', el => el.textContent);
      assert.ok(unsureMark.includes('Jev asks you'));
      assert.doesNotMatch(unsureMark, /chose/);
      await page.$eval('.sp-row[data-who="jev"]', row => row.scrollIntoView({ block: 'center' }));
      await new Promise(r => setTimeout(r, 400));
      await page.$eval('.sp-row[data-who="jev"]', row => row.scrollIntoView({ block: 'center' }));
      if (width > 1000) {
        const gap = await page.$eval('.sp-row[data-who="jev"]', row =>
          row.querySelector('.sp-row-t').getBoundingClientRect().left - Math.max(...Array.from(row.querySelector('.sp-who').children,
            child => child.getBoundingClientRect().right)));
        assert.ok(gap >= 8, `the Jev mark needs a gap before the title; found ${gap}px`);
      }
      const file = join(out, `jev-mark-${width}-${theme}.png`);
      await page.screenshot({ path: file });
      results.push(file);
      await page.close();
    }
  }
  writeFileSync(join(out, 'results.json'), JSON.stringify(results, null, 2));
  console.log(`PASS: Pro and Free note strips, Jev marks, both themes. ${results.length} screenshots in ${out}`);
} finally {
  await browser.close();
  rmSync(scratch, { recursive: true, force: true });
  await new Promise(r => server.close(r));
}
