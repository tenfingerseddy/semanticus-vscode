// KNOWN 22: a rename carries its measure update; an explicit untick shows the service harm without blocking the press.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdirSync, readFileSync } from 'node:fs';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = process.env.OUTDIR || join(tmpdir(), 'semanticus-publish-rename');
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const path = resolve(root, '.' + new URL(req.url, 'http://localhost').pathname);
  if (!path.startsWith(root + sep)) { res.writeHead(403).end(); return; }
  try { res.setHeader('content-type', types[extname(path)] || 'application/octet-stream'); res.end(readFileSync(path)); }
  catch { res.writeHead(404).end(); }
});
await new Promise((done) => server.listen(0, '127.0.0.1', done));
let browser;
try {
  browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, defaultViewport: null, protocolTimeout: 30000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'] });
  for (const width of [1440, 1090]) {
    const page = await browser.newPage();
    const cdp = await page.createCDPSession();
    await cdp.send('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
    await page.evaluateOnNewDocument(() => {
      const column = { ref: 'column:Sales/Amount', objectType: 'Column', name: 'Amount', table: 'Sales', action: 'Update', leftText: JSON.stringify({ name: 'Amount' }), rightText: JSON.stringify({ name: 'Value' }) };
      const measure = { ref: 'measure:Sales/Total', objectType: 'Measure', name: 'Total', table: 'Sales', action: 'Update', renameDependencies: [column.ref], leftText: JSON.stringify({ name: 'Total', expression: 'SUM(Sales[Amount])' }), rightText: JSON.stringify({ name: 'Total', expression: 'SUM(Sales[Value])' }) };
      window.__published = [];
      window.__UISHOT__ = {
        modelDiff: { created: 0, updated: 2, deleted: 0, equal: 0, items: [column, measure] },
        applyDiff: (p) => { if (p[3]) window.__published.push(p[2]); return { applied: !!p[3], count: p[2].length, appliedRefs: p[2], failedRefs: [], target: 'Contoso Sales', note: 'Published.', restorePointId: p[3] ? 'rename-proof' : null }; },
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?live=1&conn=1&tab=deploy`, { waitUntil: 'networkidle0' });
    await page.waitForSelector('[data-testid="publish-row"] input');
    const rows = () => page.evaluate(() => [...document.querySelectorAll('[data-testid="publish-row"]')].map((r) => ({ ref: r.dataset.ref, checked: r.querySelector('input').checked })));
    const toggle = (ref) => page.evaluate((key) => document.querySelector(`[data-ref="${key}"] input`).click(), ref);
    assert.equal((await rows()).filter((r) => r.checked).length, 2);
    await page.screenshot({ path: join(out, `rename-${width}-included.png`) });
    await toggle('measure:Sales/Total');
    await page.waitForSelector('[data-testid="publish-rename-warning"]');
    assert.equal(await page.$eval('[data-testid="publish-rename-warning"]', (e) => e.textContent), 'Publishing this rename without Total breaks Total on Contoso Sales.');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: join(out, `rename-${width}-warning.png`) });
    await toggle('column:Sales/Amount');
    await toggle('column:Sales/Amount');
    await page.waitForFunction(() => [...document.querySelectorAll('[data-testid="publish-row"] input')].every((e) => e.checked));
    assert.equal(await page.$('[data-testid="publish-rename-warning"]'), null);
    await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => b.textContent.startsWith('Publish 2 changes')).click());
    await page.waitForFunction(() => window.__published.length === 1);
    assert.deepEqual((await page.evaluate(() => window.__published[0])).sort(), ['column:Sales/Amount', 'measure:Sales/Total']);
    await page.close();
  }
  console.log('Publish rename driver passed at 1440 and 1090; both updates sent in one press.');
} finally {
  if (browser) await browser.close();
  await new Promise((done) => server.close(done));
}
