// Render the real Studio bundle with the uishot engine fixture. No desktop or live model is used.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const { default: puppeteer } = await import('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const out = process.env.OUTDIR || resolve(root, 'tools/uishot/shots/studio-driver');
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer((request, response) => {
  const file = resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
  if (!file.startsWith(root + '/')) { response.writeHead(404); response.end(); return; }
  try { response.writeHead(200, { 'content-type': types[extname(file)] || 'application/octet-stream' }); response.end(readFileSync(file)); }
  catch { response.writeHead(404); response.end(); }
});
await new Promise((done) => server.listen(0, '127.0.0.1', done));
let browser;
const results = [];
try {
  browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true,
    args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'] });
  for (const theme of ['dark', 'light']) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.setViewport({ width: 1440, height: 900 });
    await page.evaluateOnNewDocument(() => {
      window.__driverNow = 100000;
      Date.now = () => window.__driverNow;
      let acquire;
      Object.defineProperty(window, 'acquireVsCodeApi', {
        get: () => acquire,
        set: (original) => { acquire = () => {
          const api = original(); const post = api.postMessage;
          api.postMessage = (message) => {
            post(message);
            if (message.type === 'studioReady') window.dispatchEvent(new MessageEvent('message', {
              data: { type: 'driveMode', mode: 'iDrive', assistantConnected: false },
            }));
          };
          return api;
        }; },
      });
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html`, { waitUntil: 'networkidle0' });
    await applyTheme(page, theme);
    const send = (message) => page.evaluate((data) => window.dispatchEvent(new MessageEvent('message', { data })), message);
    const state = () => page.evaluate(() => window.__uishotHarness.messages.filter((message) => message.type === 'driverState').at(-1));
    assert.equal(await page.$('[data-testid="studio-driver-bar"]'), null);
    assert.equal((await state()).mode, 'iDrive');
    if (theme === 'dark') await page.screenshot({ path: join(out, 'before-dark.png') });
    await send({ type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    await page.waitForSelector('[data-testid="studio-driver-bar"]');
    const capture = async (name, words) => {
      await page.waitForFunction((text) => document.querySelector('[data-testid="studio-driver-bar"] [role="status"]')?.textContent === text, {}, words);
      const measured = await page.$eval('[data-testid="studio-driver-bar"]', (bar) => {
        const row = bar.getBoundingClientRect();
        const buttons = [...bar.querySelectorAll('button')].map((button) => ({ text: button.textContent, selected: button.getAttribute('aria-pressed') }));
        return { words: bar.querySelector('[role="status"]').textContent,
          color: getComputedStyle(bar.querySelector('[role="status"]')).color,
          overflow: bar.scrollWidth > bar.clientWidth, height: row.height, buttons };
      });
      assert.equal(measured.overflow, false);
      const file = `${name}-${theme}.png`;
      await page.screenshot({ path: join(out, file) });
      results.push({ file, ...measured });
    };
    await send({ type: 'studioShow', payload: { id: 'sn-test', say: 'Open Plan.', at: new Date(100000).toISOString() } });
    assert.equal((await state()).state, 'claudeDriving');
    await capture('claude', 'Claude is driving. Click anything to take the wheel.');
    await page.evaluate(() => document.querySelector('main').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
    assert.equal((await state()).state, 'personHasWheel');
    await capture('person', 'You have the wheel. Your next message to Claude gives it back.');
    // The strip and bar are controls for the presentation itself. They do not take the wheel.
    await page.click('[data-testid="driver-resume"]');
    assert.equal((await state()).state, 'claudeDriving');
    await page.evaluate(() => document.querySelector('[data-studio-note-strip]').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
    assert.equal((await state()).state, 'claudeDriving');
    await page.evaluate(() => document.querySelector('main').dispatchEvent(new KeyboardEvent('keydown', { key: 'x', bubbles: true })));
    assert.equal((await state()).state, 'personHasWheel');
    await send({ type: 'assistantCall', payload: { tool: 'session_info', sessionId: 'fixture', at: new Date(107999).toISOString() } });
    assert.equal((await state()).state, 'personHasWheel');
    await send({ type: 'assistantCall', payload: { tool: 'session_info', sessionId: 'fixture', at: new Date(108000).toISOString() } });
    assert.equal((await state()).state, 'claudeDriving');
    await page.click('[data-testid="driver-mode-iDrive"]');
    assert.equal((await state()).state, 'idle');
    assert.equal((await state()).mode, 'iDrive');
    await capture('i-drive', 'I drive. Claude works but does not move your screen.');
    await send({ type: 'studioShow', payload: {} });
    await send({ type: 'assistantCall', payload: { at: new Date(130000).toISOString() } });
    assert.equal((await state()).mode, 'iDrive');
    assert.equal((await state()).state, 'idle');
    await page.click('[data-testid="driver-mode-claudeDrives"]');
    assert.equal((await state()).state, 'idle');
    const modeMessages = await page.evaluate(() => window.__uishotHarness.messages.filter((message) => message.type === 'setDriveMode'));
    assert.deepEqual(modeMessages.map((message) => message.mode), ['iDrive', 'claudeDrives']);
    await send({ type: 'personInput', at: 130000 });
    assert.equal((await state()).state, 'personHasWheel');
    await page.setViewport({ width: 780, height: 900 });
    await capture('person-narrow', 'You have the wheel. Your next message to Claude gives it back.');
    assert.deepEqual(errors, []);
    await page.close();
  }
  writeFileSync(join(out, 'results.json'), JSON.stringify({ passed: true, results }, null, 2) + String.fromCharCode(10));
  console.log(`PASS: driver controls, messages, 8 s quiet gap, and ${results.length} screenshots. ${out}`);
} finally {
  if (browser) await browser.close();
  await new Promise((done) => server.close(done));
}
