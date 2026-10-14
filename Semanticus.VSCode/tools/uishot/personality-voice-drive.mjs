// The real Studio bundle, with the same engine fixtures as other uishot drivers.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync, mkdirSync } from 'node:fs';
import { dirname, resolve, join, normalize, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = '/tmp/semanticus-joint-review/m15/voice/shots';
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer((req, res) => {
  const file = normalize(join(root, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!file.startsWith(root) || !existsSync(file) || statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[extname(file)] || 'application/octet-stream' });
  const content = readFileSync(file);
  // The stock harness seeds unrelated History edits. This lane supplies its own successful edits.
  res.end(file.endsWith('harness.html') ? content.toString().replace('var EDITS = [', 'var EDITS = cfg.voice ? [] : [') : content);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'] });
const errors = [];
async function scene(theme, name, config = {}, action) {
  if (process.env.VOICE_SCENE && process.env.VOICE_SCENE !== name) return;
  const page = await browser.newPage();
  page.on('pageerror', e => errors.push(`${name}: ${e.message}`));
  await page.setViewport({ width: 1440, height: 900 });
  await page.evaluateOnNewDocument((cfg) => {
    window.__UISHOT__ = { ...cfg, voice: true };
    if (cfg.friday) Date.prototype.getDay = () => 5;
  }, config);
  await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?g=mini${name === 'first-publish' || name === 'friday-test' ? '&conn=1' : ''}`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('header.studio-chrome');
  await applyTheme(page, theme);
  if (action) {
    try { await action(page); }
    catch (error) {
      await page.screenshot({ path: join(out, `failed-${name}-${theme}.png`) });
      console.error(await page.evaluate(() => ({ hidden: document.hidden, strip: document.querySelector('[data-testid="note-strip"]')?.textContent, receipt: document.querySelector('[data-testid="said-line"]')?.textContent })), errors);
      throw error;
    }
  }
  await page.screenshot({ path: join(out, `${name}-${theme}.png`) });
  await page.close();
  console.log(`PASS ${name} ${theme}`);
}
const activity = (page, kind, result, ok = true) => page.evaluate((kind, result, ok) => window.__uishotHarness.dispatch({ type: 'activity', payload: { kind, result, ok, origin: 'agent', seq: 990 } }), kind, result, ok);
const text = page => page.$eval('[data-testid="personality-message"]', el => el.textContent.trim());
const waitForLine = page => page.waitForSelector('[data-testid="personality-message"]');
try {
  for (const theme of ['dark', 'light']) {
    await scene(theme, 'before');
    await scene(theme, 'grade-a', {}, async page => {
      await activity(page, 'ai_readiness_scan', { grade: 'A', gatedBy: [] });
      await waitForLine(page);
      assert.equal(await text(page), 'Grade A. Copilot will read this like a good book.');
    });
    await scene(theme, 'quiet', {}, async page => {
      await activity(page, 'ai_readiness_scan', { grade: 'A' });
      await waitForLine(page);
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'personalitySettings', personality: 'quiet' } })));
      await page.waitForFunction(() => document.querySelector('[data-testid="personality-message"]')?.textContent.trim() === 'Grade A.');
    });
    await scene(theme, 'no-measures', { session: { sessionId: 'uishot', modelName: 'Contoso', tables: 5, measures: 0 } }, async page => {
      await waitForLine(page);
      assert.equal(await text(page), 'No measures yet. Every good model started as a pile of columns.');
    });
    await scene(theme, 'huge-model', { session: { sessionId: 'uishot', modelName: 'Contoso', tables: 731, measures: 1200 } }, async page => {
      await waitForLine(page);
      assert.equal(await text(page), '731 tables. I have read them all, so you do not have to.');
    });
    await scene(theme, 'zero-findings', {}, async page => {
      await activity(page, 'bpa_scan', { violationCount: 0, ruleCount: 31, ruleErrors: [] });
      await waitForLine(page);
      assert.equal(await text(page), 'Zero findings. The best practice check has nothing left to say. Enjoy the quiet.');
    });
    await scene(theme, 'undo', {}, async page => {
      await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 104, origin: 'human', replay: 'undo', deltas: [{ ref: 'measure:Sales/Test', kind: 'remove' }] } }));
      await waitForLine(page);
      assert.equal(await text(page), 'Undone. As if it never happened.');
    });
    await scene(theme, 'bulk', {}, async page => {
      await page.evaluate(() => {
        window.__uishotHarness.dispatch({ type: 'assistantCall', payload: { sessionId: 'uishot', call: 'bulk-words', tool: 'write_words', at: new Date().toISOString() } });
        window.__uishotHarness.dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 104, origin: 'agent', label: 'write words', deltas: [{ kind: 'update', ref: 'culture:en-US', props: ['Content'] }], words: { verb: 'added synonyms to', noun: 'fields', count: 885, targets: [], sentence: 'Your assistant added synonyms to 885 fields.' } } });
      });
      await page.waitForFunction(() => document.querySelector('[data-testid="said-line"]')?.textContent.includes('Synonyms added to 885 fields. Copilot has more words to work with.'));
      assert.equal(await page.$$eval('[data-testid="personality-message"]', els => els.length), 0);
      await page.screenshot({ path: join(out, `bulk-on-${theme}.png`) });
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'personalitySettings', personality: 'quiet' } })));
      await page.waitForFunction(() => document.querySelector('[data-testid="said-line"]')?.textContent.includes('Your assistant added synonyms to 885 fields.'));
    });
    await scene(theme, 'wait', {}, async page => {
      await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'assistantCall', payload: { sessionId: 'uishot', call: 'refresh', tool: 'refresh_partition', at: new Date().toISOString() } }));
      await page.waitForSelector('[data-testid="personality-message"]', { timeout: 12000 });
      assert.equal(await text(page), 'Still refreshing. The service is counting very carefully.');
      await page.screenshot({ path: join(out, `wait-visible-${theme}.png`) });
      await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
      await page.waitForFunction(() => !document.querySelector('[data-testid="personality-message"]'));
      await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); document.dispatchEvent(new Event('visibilitychange')); });
    });
    await scene(theme, 'error', {}, async page => {
      await activity(page, 'ai_readiness_scan', { grade: 'A' });
      await waitForLine(page);
      await activity(page, 'ai_readiness_scan', { error: 'The check failed.' }, false);
      await page.waitForFunction(() => !document.querySelector('[data-testid="personality-message"]'));
    });
    await scene(theme, 'returned-error', {}, async page => {
      await activity(page, 'ai_readiness_scan', { grade: 'A' });
      await waitForLine(page);
      await activity(page, 'ai_readiness_scan', { error: 'The check failed.' });
      await page.waitForFunction(() => !document.querySelector('[data-testid="personality-message"]'));
    });
    await scene(theme, 'production', {}, async page => {
      await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'assistantCall', payload: { sessionId: 'uishot', call: 'publish-prod', tool: 'deploy_live', at: new Date().toISOString() } }));
      await activity(page, 'ai_readiness_scan', { grade: 'A' });
      assert.equal(await page.$$eval('[data-testid="personality-message"]', els => els.length), 0);
    });
    await scene(theme, 'delete', {}, async page => {
      await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'assistantCall', payload: { sessionId: 'uishot', call: 'delete', tool: 'delete_object', at: new Date().toISOString() } }));
      await activity(page, 'ai_readiness_scan', { grade: 'A' });
      assert.equal(await page.$$eval('[data-testid="personality-message"]', els => els.length), 0);
    });
    await scene(theme, 'first-publish', { modelDiff: { identical: false, items: [{ ref: 'measure:Sales/Revenue', name: 'Revenue', table: 'Sales', objectType: 'Measure', action: 'Update', leftText: '{"expression":"SUM(Sales[Amount])"}', rightText: '{"expression":"SUM(Sales[OldAmount])"}' }] }, connections: [
      { id: 'cn1', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Finance', database: 'Finance', label: 'prod' },
      { id: 'cn2', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20UAT', database: 'Contoso', modelName: 'Contoso Sales', label: 'uat' },
    ] }, async page => {
      await page.click('[data-testid="publish-button"]');
      await page.waitForFunction(() => document.querySelector('[data-testid="publish-go"]') && !document.querySelector('[data-testid="publish-go"]').disabled);
      await page.click('[data-testid="publish-go"]');
      await page.waitForSelector('[data-testid="publish-result"]');
      await page.waitForFunction(() => document.querySelector('[data-testid="personality-message"]')?.textContent.trim() === 'Your first publish. The model is out in the world now.', { timeout: 3000 });
    });
    await scene(theme, 'friday-test', { friday: true, state: { personalityVoice: { published: true, used: [], lastJoke: null } }, modelDiff: { identical: false, items: [{ ref: 'measure:Sales/Revenue', name: 'Revenue', table: 'Sales', objectType: 'Measure', action: 'Update', leftText: '{"expression":"SUM(Sales[Amount])"}', rightText: '{"expression":"SUM(Sales[OldAmount])"}' }] }, connections: [
      { id: 'cn1', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Finance', database: 'Finance', label: 'prod' },
      { id: 'cn2', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20UAT', database: 'Contoso', modelName: 'Contoso Sales', label: 'uat' },
    ] }, async page => {
      await page.click('[data-testid="publish-button"]');
      await page.waitForFunction(() => document.querySelector('[data-testid="publish-go"]') && !document.querySelector('[data-testid="publish-go"]').disabled);
      await page.click('[data-testid="publish-go"]');
      await waitForLine(page);
      assert.equal(await text(page), 'Published 1 change to Contoso Sales. Your copy did not change.');
      assert.equal(await page.$eval('[data-testid="personality-message"]', el => el.dataset.joke), 'false');
    });
  }
  assert.deepEqual(errors, []);
  console.log('PASS all voice scenes, both themes; no browser errors');
} finally { await browser.close(); server.close(); }
