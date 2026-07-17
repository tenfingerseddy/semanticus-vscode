// Renders the built Studio webview bundle in headless Chromium with a mocked engine (harness.html),
// drives each tab, and writes one PNG per view into ./shots so the UI can be reviewed/iterated
// without launching VS Code. Usage: node shoot.mjs [tabName]
import { chromium } from 'playwright';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const only = process.argv[2]; // optional: screenshot just one tab
const harness = pathToFileURL(path.resolve('harness.html')).href;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const TABS = [
  { name: 'AI Readiness', file: '1-readiness', async after(p) { await sleep(400); } },
  { name: 'Optimize', file: '1a-optimize', async after(p) { await sleep(500); } },
  { name: 'BPA', file: '1b-bpa', async after(p) { await sleep(400); } },
  { name: 'Diagram', file: '2-diagram', async after(p) { await p.waitForSelector('.react-flow__node', { timeout: 4000 }).catch(() => {}); await sleep(700); } },
  { name: 'Storage', file: '3-storage', async after(p) { await click(p, 'Scan storage'); await sleep(700); } },
  { name: 'Data', file: '4-data', async after(p) { await click(p, 'Sales'); await sleep(500); } },
  { name: 'DAX Query', file: '5-daxquery', async after(p) { await click(p, '🐞 Debug'); await sleep(500); } },
  { name: 'DAX Lab', file: '6-daxlab', async after(p) { await click(p, '▶ Benchmark'); await sleep(300); await click(p, '⏱ Profile'); await sleep(300); await click(p, '✓ Verify'); await sleep(500); } },
  { name: 'Pivot', file: '7-pivot', async after(p) { await click(p, '▶ Pivot'); await sleep(500); } },
];

async function click(page, text) {
  const btn = page.getByRole('button', { name: text, exact: false }).first();
  if (await btn.count()) { await btn.click().catch(() => {}); }
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1340, height: 920 }, deviceScaleFactor: 2 });
const errs = [];
page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text()); });
page.on('pageerror', (e) => errs.push('PAGEERROR: ' + e.message));

await page.goto(harness);
await sleep(600); // initial mount + sessionInfo + scan

for (const t of TABS) {
  if (only && t.name !== only) continue;
  await page.getByRole('button', { name: t.name, exact: true }).first().click().catch(() => {});
  await sleep(250);
  await t.after(page);
  await page.screenshot({ path: `shots/${t.file}.png` });
  console.log('shot:', t.file);
}

await browser.close();
if (errs.length) { console.log('\nconsole errors:\n' + [...new Set(errs)].slice(0, 20).join('\n')); }
else console.log('\nno console errors');
