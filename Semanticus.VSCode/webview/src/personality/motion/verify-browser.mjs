// Uses the repository's isolated uishot browser. No desktop and no live tenant.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from '../../../../tools/uishot/browser.mjs';
import { THEMES } from '../../../../tools/uishot/themes.mjs';
requireSupportedNode();
const require = createRequire(import.meta.url);
const puppeteer = require('../../../../tools/uishot/node_modules/puppeteer-core');
const { createServer } = await import('../../../node_modules/vite/dist/node/index.js');
const output = process.argv[2] ?? '/tmp/semanticus-joint-review/m15/motion/shots';
await mkdir(output, { recursive: true });
const server = await createServer({ root: fileURLToPath(new URL('../../../', import.meta.url)), server: { host: '127.0.0.1', port: 0 }, logLevel: 'error' });
await server.listen();
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--force-color-profile=srgb'] });
try {
  const page = await browser.newPage();
  await page.setViewport({ width: 1000, height: 950, deviceScaleFactor: 1 });
  const errors = [];
  page.on('pageerror', error => errors.push(String(error)));
  await page.goto(`${server.resolvedUrls.local[0]}src/personality/motion/fixture.html`, { waitUntil: 'networkidle0' });
  await page.waitForSelector('[data-testid="motion-next"]');
  for (const theme of ['dark', 'light']) {
    await page.evaluate(({ vars, bodyClass }) => {
      for (const [name, value] of Object.entries(vars)) document.documentElement.style.setProperty(name, value);
      document.body.className = bodyClass;
    }, THEMES[theme]);
    const started = await page.evaluate(() => { window.motionFixture.play(); return performance.now(); });
    for (const at of [40, 300, 700, 1450, 2100]) {
      const elapsed = await page.evaluate(start => performance.now() - start, started);
      await new Promise(resolve => setTimeout(resolve, Math.max(0, at - elapsed)));
      await page.screenshot({ path: `${output}/${theme}-${at}.png`, fullPage: true });
    }
    assert.equal(await page.$eval('[data-testid="motion-bulk"] .sem-motion-count', e => e.textContent), '885');
    assert.match(await page.$eval('[data-testid="motion-bulk"]', e => e.textContent), /in 1.2 s/);
    assert.equal(await page.$eval('[data-testid="motion-grade"]', e => e.getAttribute('aria-label')), 'Grade A');
    assert.equal(await page.$eval('[data-testid="motion-undo"] .sem-text-meta', e => e.textContent.replace(/\s/g, '')), 'Historychange3737');
    assert.equal(await page.$eval('[data-testid="motion-jev"] li', e => e.textContent), 'Sales[OrderDate]Jev chose this92%');
    assert.deepEqual(await page.evaluate(() => window.motionFixture.sounds), []);
  }
  await page.evaluate(() => { window.motionFixture.quiet(true); window.motionFixture.sound(true); window.motionFixture.play(); });
  await new Promise(resolve => setTimeout(resolve, 350));
  assert.equal(await page.evaluate(() => document.getAnimations().filter(a => a.playState === 'running').length), 0);
  assert.equal(await page.evaluate(() => [...document.querySelectorAll('[data-testid="motion-sparkle"]')].filter(e => getComputedStyle(e).display !== 'none').length), 0);
  assert.deepEqual(await page.evaluate(() => window.motionFixture.sounds), []);
  await page.screenshot({ path: `${output}/quiet.png`, fullPage: true });
  await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: 'reduce' }]);
  await page.evaluate(() => { window.motionFixture.quiet(false); window.motionFixture.sound(false); window.motionFixture.play(); });
  await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(await page.evaluate(() => document.getAnimations().filter(a => a.playState === 'running').length), 0);
  assert.equal(await page.$eval('[data-testid="motion-bulk"] .sem-motion-count', e => e.textContent), '885');
  await page.screenshot({ path: `${output}/reduced.png`, fullPage: true });
  await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: 'no-preference' }]);
  await page.evaluate(() => { window.motionFixture.play(); });
  await new Promise(resolve => setTimeout(resolve, 100));
  await page.evaluate(() => window.motionFixture.hide(true));
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.equal(await page.evaluate(() => document.getAnimations().filter(a => a.playState === 'running').length), 0);
  assert.equal(await page.$eval('[data-testid="motion-bulk"] .sem-motion-count', e => e.textContent), '885');
  await page.evaluate(() => { window.motionFixture.hide(false); window.motionFixture.sound(true); window.motionFixture.play(); });
  await new Promise(resolve => setTimeout(resolve, 500));
  assert.ok(await page.evaluate(() => window.motionFixture.sounds.includes('snap')));
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, value: true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await new Promise(resolve => setTimeout(resolve, 60));
  assert.equal(await page.evaluate(() => document.getAnimations().filter(a => a.playState === 'running').length), 0);
  await page.evaluate(() => {
    delete document.hidden;
    document.dispatchEvent(new Event('visibilitychange'));
    window.motionFixture.production(true); window.motionFixture.sound(false); window.motionFixture.play();
  });
  await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(await page.$eval('[data-testid="motion-publish"] [data-testid="motion-sparkle"]', e => getComputedStyle(e).display), 'none');
  await page.evaluate(() => { window.motionFixture.error(true); window.motionFixture.play(); });
  await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(await page.$eval('[data-testid="motion-publish"]', e => e.getAnimations({ subtree: true }).filter(a => a.playState === 'running').length), 0);
  await page.evaluate(() => window.motionFixture.hide(true));
  await new Promise(resolve => setTimeout(resolve, 60));
  assert.deepEqual(errors, []);
  await writeFile(`${output}/browser-results.json`, JSON.stringify({ assertions: 'passed', themes: ['dark', 'light'], framesMs: [40, 300, 700, 1450, 2100], quiet: 'passed', reduced: 'passed', hidden: 'passed', sounds: 'passed', errors }, null, 2));
  console.log('Motion browser checks passed. Dark/light frames, Quiet, reduced motion, hidden state and sound checked.');
} finally { await browser.close(); await server.close(); }
