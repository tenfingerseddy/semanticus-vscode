// The edge (drive-edge.tsx): fade in, trough, peak, handback and reduced motion frames, light and dark, at 1440 and 400. Real Studio bundle, engine fixture, no desktop.
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
const out = process.env.OUTDIR || resolve(root, 'tools/uishot/shots/drive-edge');
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
  for (const theme of ['dark', 'light']) for (const width of [1440, 400]) for (const reduced of [false, true]) {
    if (reduced && width === 400) continue;
    const tag = `${theme}-${width}${reduced ? '-reduced' : ''}`;
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: reduced ? 'reduce' : 'no-preference' }]);
    await page.setViewport({ width, height: 900 });
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
              data: { type: 'driveMode', mode: 'iDrive', assistantConnected: true },
            }));
          };
          return api;
        }; },
      });
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html`, { waitUntil: 'networkidle0' });
    await applyTheme(page, theme);
    if (width === 400) await page.setViewport({ width, height: 900 });
    const send = (message) => page.evaluate((data) => window.dispatchEvent(new MessageEvent('message', { data })), message);
    const edge = () => page.$('[data-testid="studio-drive-edge"]');
    const barRect = () => page.$eval('[data-testid="studio-driver-bar"]', (bar) => { const r = bar.getBoundingClientRect(); return [r.x, r.y, r.width, r.height].join(); });
    // A frame strip: the top of the Studio, where the line and the assistant's tile sit together, plus the whole frame.
    const shot = (name) => { const file = `${name}-${tag}.png`; results.push(file); return page.screenshot({ path: join(out, file) }); };
    const strip = (name) => { const file = `strip-${name}-${tag}.png`; results.push(file); return page.screenshot({ path: join(out, file), clip: { x: 0, y: 0, width, height: 90 } }); };
    const animations = () => page.evaluate(() => document.querySelector('.sc-edge')?.getAnimations().length ?? -1);
    const opacity = () => page.$eval('.sc-edge', (e) => Number(getComputedStyle(e).opacity));
    // Freeze every running animation at an instant, so a frame in the fade or the pulse can be read.
    const freeze = (ms) => page.evaluate((t) => document.querySelector('.sc-edge').getAnimations().forEach((a) => { a.pause(); a.currentTime = t; }), ms);
    const call = () => send({ type: 'assistantCall', payload: { at: new Date(100000).toISOString(), tool: 'get_model_summary',
      assistant: { key: 'claude', name: 'Claude Code', initials: 'CC', version: '2.1.0' } } });
    await page.waitForSelector('[data-testid="studio-driver-bar"]');
    assert.equal(await edge(), null, 'I drive: no line');

    // Connected and Claude drives, but nothing has called yet: still no line.
    await page.click('[data-testid="driver-mode-claudeDrives"]');
    assert.equal(await edge(), null, 'waiting for an assistant: no line');
    // The assistant starts working: the line fades in beside the tile.
    await call();
    await page.waitForSelector('[data-testid="studio-drive-edge"]');
    await page.waitForSelector('[data-testid="assistant-sign"][data-state="working"]');
    const n = await animations();
    if (reduced) {
      assert.equal(n, 0, 'reduced motion: a still line, nothing animated');
      assert.equal(Math.round((await opacity()) * 100), 40, 'reduced motion: the still line is at rest strength');
      await shot('rest'); await strip('rest');
    } else {
      assert.equal(n, 2, 'one fade in and one breath');
      await freeze(150);
      assert.ok((await opacity()) > 0 && (await opacity()) < 0.25, 'mid fade in');
      await strip('fade-in');
      await freeze(300);
      assert.equal(Math.round((await opacity()) * 100), 25, 'the trough of the breath');
      await shot('trough'); await strip('trough');
      await freeze(300 + 1800);
      assert.equal(Math.round((await opacity()) * 100), 50, 'the top of the breath');
      await shot('peak'); await strip('peak');
    }
    const withLine = await barRect();
    await page.addStyleTag({ content: '.sc-edge { display: none !important; }' });
    assert.equal(await barRect(), withLine, 'the line moves no layout');
    await page.evaluate(() => { document.querySelectorAll('style').forEach((x) => { if (x.textContent.includes('.sc-edge { display: none')) x.remove(); }); });
    const probe = await page.evaluate(() => ({
      corner: document.elementFromPoint(1, 1)?.className?.toString() ?? '',
      style: getComputedStyle(document.querySelector('[data-testid="studio-drive-edge"]')).pointerEvents,
      border: getComputedStyle(document.querySelector('.sc-edge')).borderTopWidth,
    }));
    assert.equal(probe.style, 'none');
    assert.equal(probe.border, '1px');
    assert.ok(!probe.corner.includes('sc-edge'), 'the pointer sees what is under the line');
    const size = await page.$eval('.sc-edge', (e) => [e.offsetWidth, e.offsetHeight]);
    assert.deepEqual(size, [width, 900], 'the frame covers the Studio and is not resized');

    // Handback by a click anywhere in the Studio: the click still reaches its target, and the line fades out.
    await page.evaluate(() => { window.__hit = 0; document.querySelector('main').addEventListener('click', () => { window.__hit += 1; }); });
    const target = await page.$eval('main', (main) => { const r = main.getBoundingClientRect(); return { x: r.x + 3, y: r.y + r.height - 3 }; });
    await page.mouse.click(target.x, target.y);
    assert.equal(await page.evaluate(() => window.__hit), 1, 'the click reached the page under the line');
    assert.equal(await page.$eval('[data-testid="studio-driver-bar"] [role="status"]', (n) => n.textContent), 'You have the wheel. Your next message to Claude Code gives it back.');
    if (reduced) {
      assert.equal(await edge(), null, 'reduced motion: gone at once');
    } else {
      await page.waitForSelector('[data-edge="fade"]');
      await page.waitForFunction(() => !document.querySelector('[data-testid="studio-drive-edge"]'), { timeout: 3000 });
    }
    await strip('back');

    // Let Claude drive gives the wheel back: the line shows again. Then I drive removes it.
    await page.click('[data-testid="driver-resume"]');
    await call();
    await page.waitForSelector('[data-testid="studio-drive-edge"]');
    await page.click('[data-testid="driver-mode-iDrive"]');
    await page.waitForFunction(() => !document.querySelector('[data-testid="studio-drive-edge"]'), { timeout: 3000 });
    assert.deepEqual(errors, []);
    await page.close();
  }
  writeFileSync(join(out, 'results.json'), JSON.stringify({ passed: true, results }, null, 2) + String.fromCharCode(10));
  console.log(`PASS: edge fade in, breath, fade out, click-through, reduced motion, ${results.length} screenshots. ${out}`);
} finally {
  if (browser) await browser.close();
  await new Promise((done) => server.close(done));
}
