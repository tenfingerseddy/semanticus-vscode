// Scripted INTERACTIVE shots of the DAX Lab -> New test handoff (UX11), so the carried filters and the
// edited-formula choice can be REVIEWED as pixels rather than asserted only as source text.
//
// It reuses the uishot harness (real headless Chromium, engine mocked in-page — see shot.mjs / harness.html),
// seeds the persisted webview state so DAX Lab opens on a FILTERED visual with an EDITED query, then drives
// the real React bundle: open "Save measure check", pick a shape, screenshot the drawer.
//
// Usage:  cd Semanticus.VSCode/tools/uishot && npm run build:webview --prefix ../.. && node drive.mjs
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, extname, normalize } from 'node:path';
import { existsSync, mkdirSync, statSync, readFileSync } from 'node:fs';
import { createServer } from 'node:http';

// The floor check runs BEFORE puppeteer loads — see the same note in shot.mjs.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;

const __dir = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(__dir, '..', '..');
const outDir = join(__dir, 'shots');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };

if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.');
  process.exit(1);
}

function startServer() {
  const server = createServer((req, res) => {
    try {
      const urlPath = decodeURIComponent((req.url || '/').split('?')[0].split('#')[0]);
      const filePath = normalize(join(webRoot, urlPath));
      if (!filePath.startsWith(webRoot) || !existsSync(filePath) || statSync(filePath).isDirectory()) {
        res.writeHead(404); res.end('not found'); return;
      }
      res.writeHead(200, { 'content-type': TYPES[extname(filePath).toLowerCase()] || 'application/octet-stream' });
      res.end(readFileSync(filePath));
    } catch (e) { res.writeHead(500); res.end(String(e)); }
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r(server)));
}

// The harness session id, which is what DAX Lab's per-model persistence keys are suffixed with.
const KEY = 'uishot';
const FILTERED_VISUAL = {
  rows: [{ kind: 'column', ref: "'Product'[Category]", name: 'Category', table: 'Product' }],
  cols: [],
  values: [{ kind: 'measure', ref: '[Total Sales]', name: 'Total Sales', table: 'Sales' }],
  filters: [
    { ref: "'Date'[Year]", name: 'Year', family: 'number', filter: { kind: 'number', op: 'eq', a: 2026, b: null } },
    { ref: "'Customer'[Region]", name: 'Region', family: 'text', filter: { kind: 'text', op: 'eq', value: 'North', picked: [] } },
  ],
};
// Not the canonical EVALUATE ROW("Total Sales", [Total Sales]) the lab seeds, and not the measure's saved
// formula either — so the handoff must treat it as an edit and the drawer must ask which one to check.
const EDITED_QUERY = 'EVALUATE\nROW("Total Sales", CALCULATE(SUM(Sales[SalesAmount]) * 1.05, Sales[Channel] = "Online"))';

const STATE = {
  'input:lab.mode.uishot': 'query',
  [`input:lab.config.${KEY}`]: FILTERED_VISUAL,
  [`input:lab.testMeasure.${KEY}`]: 'measure:Sales/Total Sales',
  [`input:lab.bq.${KEY}`]: EDITED_QUERY,
};

// ONE page, both captures: open the menu, pick a shape, shoot, close the drawer, repeat. A second page load
// of the same harness proved flaky, and driving one page is also closer to what a person actually does.
async function drive(browser, port) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 2 });
  const url = `http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&state=${encodeURIComponent(JSON.stringify(STATE))}#${encodeURIComponent('DAX Lab')}`;
  await page.goto(url, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 30000 });
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').includes('Save measure check')), { timeout: 30000, polling: 250 });

  const clickByText = (needle) => page.evaluate((text) => {
    const hit = [...document.querySelectorAll('button')].find((b) => (b.textContent || '').includes(text));
    if (!hit) throw new Error('no button matching: ' + text);
    hit.click();
  }, needle);

  const results = [];
  for (const { name, choice, label } of [
    { name: 'handoff-expected-total.png', choice: 'Check a trusted answer', label: 'expected total' },
    { name: 'handoff-source-sql.png', choice: 'Compare with source SQL', label: 'source SQL' },
  ]) {
    await clickByText('Save measure check');
    await new Promise((r) => setTimeout(r, 250));
    await clickByText(choice);
    await page.waitForSelector('aside', { timeout: 15000 });
    await new Promise((r) => setTimeout(r, 600));
    const out = join(outDir, name);
    await page.screenshot({ path: out });
    // A textarea's VALUE is not its textContent, and the carried filters live in a textarea — read both, and
    // take the LAST aside so the Studio's own rails cannot be mistaken for the drawer.
    const drawer = await page.evaluate(() => {
      const panels = [...document.querySelectorAll('aside')];
      const panel = panels[panels.length - 1];
      if (!panel) return '';
      const fields = [...panel.querySelectorAll('textarea, input')].map((f) => f.value || '').join(' | ');
      return (panel.textContent || '') + ' | ' + fields;
    });
    results.push({ label, out, drawer });
    console.log(`  ok ${out}`);
    await clickByText('Close');
    await new Promise((r) => setTimeout(r, 300));
  }
  if (errors.length) errors.forEach((e) => console.log('  ! ' + e));
  await page.close();
  return results;
}

mkdirSync(outDir, { recursive: true });
const server = await startServer();
const port = server.address().port;
const executablePath = findBrowser();
const browser = await puppeteer.launch({
  executablePath,
  headless: executablePath.endsWith('/chromium') ? true : 'shell',
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
try {
  // Probe what the drawer actually SAYS, so a wrong-but-pretty screenshot cannot pass unnoticed.
  for (const { label, drawer } of await drive(browser, port)) {
    const carried = drawer.includes("'Date'[Year] = 2026") && drawer.includes("'Customer'[Region]");
    console.log(`  ${label}: filters carried = ${carried}; asks which formula = ${drawer.includes('Which formula is this check about?')}; authored line = ${drawer.includes('Authored against')}`);
  }
} finally {
  await browser.close();
  server.close();
}
