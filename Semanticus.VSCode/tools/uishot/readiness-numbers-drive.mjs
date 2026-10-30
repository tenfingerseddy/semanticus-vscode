// Drive the real readiness controls with count fixtures. Live numbers are checked separately by the engine tests.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = resolve(process.env.OUTDIR || join(root, 'shots/readiness-numbers'));
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer((req, res) => {
  const path = normalize(join(root, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!path.startsWith(root) || !existsSync(path) || statSync(path).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[extname(path)] || 'application/octet-stream' });
  res.end(readFileSync(path));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-gpu'],
  userDataDir: join(out, 'browser') });
const checks = [];
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1100, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(() => {
    window.__r1Writes = [];
    window.__r1Live = 0;
    let harness;
    Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => harness, set: value => {
      harness = value;
      const card = value.response('aiReadinessScan');
      card.findings = [{ ruleId: 'AVG-OF-RATIO', ruleTitle: 'An average of percentages can give a wrong total', category: 'BestPractice', severity: 'High', fix: 'Proposal', objectRef: 'measure:Sales/Margin %', objectName: 'Margin %', message: 'This measure averages row percentages. Compare both numbers before choosing.' }];
      value.setResponse('aiReadinessScan', card);
      value.setResponse('aiReadinessScanLive', () => { window.__r1Live++; return card; });
      value.setResponse('getFixPrompt', { prompt: 'Compare both numbers before choosing.', ratioOffer: {
        sessionId: 'r1-model', revision: 42, originalExpression: 'AVERAGEX(Sales, DIVIDE(Sales[Profit], Sales[Revenue]))', expression: 'DIVIDE(SUM(Sales[Profit]), SUM(Sales[Revenue]))', groupedBy: "'Product'[Category]",
        rows: [{ scope: 'All data', before: 0.14, after: 0.08 }, { scope: 'Electronics', before: 0.1655, after: 0.0583 }]
      } });
      value.setResponse('setDax', params => { window.__r1Writes.push(params); return { changed: true, revision: 43 }; });
    } });
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tab=readiness&expand=all&tier=pro`, { waitUntil: 'networkidle0' });
  const click = async text => {
    await page.waitForFunction(t => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === t), {}, text);
    await page.evaluate(t => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === t).click(), text);
  };
  await click('Compare totals');
  await page.waitForFunction(() => document.body.innerText.includes('0.1655') && document.body.innerText.includes('0.0583'));
  assert.equal((await page.evaluate(() => window.__r1Writes)).length, 0);
  checks.push('The offer shows both numbers before any write.');
  await page.screenshot({ path: join(out, 'comparison-wide.png'), fullPage: true });
  await page.setViewport({ width: 1090, height: 1000, deviceScaleFactor: 1 });
  await page.screenshot({ path: join(out, 'comparison-narrow.png'), fullPage: true });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  checks.push('The comparison fits at 1090 pixels.');
  await click('Keep row average');
  assert.equal((await page.evaluate(() => window.__r1Writes)).length, 0);
  checks.push('Keeping the average writes nothing.');
  await click('Compare totals');
  await click('Use totals');
  await page.waitForFunction(() => window.__r1Writes.length === 1);
  const writes = await page.evaluate(() => window.__r1Writes);
  assert.deepEqual(writes[0], ['measure:Sales/Margin %', 'DIVIDE(SUM(Sales[Profit]), SUM(Sales[Revenue]))', 'human', 'r1-model', 42, 'AVERAGEX(Sales, DIVIDE(Sales[Profit], Sales[Revenue]))']);
  checks.push('Using totals writes the shown formula with its original expression and model version.');
  await click('Check live data');
  await page.waitForFunction(() => window.__r1Live === 1);
  checks.push('The live-data control reaches the live scan.');
  assert.deepEqual(errors, []);
  writeFileSync(join(out, 'results.json'), JSON.stringify({ checks, errors, writes }, null, 2));
  process.stdout.write(`PASS: ${checks.length} readiness control checks. Screenshots: ${out}\n`);
} finally { await browser.close(); await new Promise(r => server.close(r)); }
