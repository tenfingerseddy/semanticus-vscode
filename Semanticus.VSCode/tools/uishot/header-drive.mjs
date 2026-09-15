// Driven acceptance shots for the measured header fold and the one Publish action. Adapted from
// /tmp/semanticus-joint-review/integrated-acceptance/drive.mjs: it serves the built bundle, drives the real
// harness with real clicks, probes the live DOM for what is actually visible, and writes a PNG per case.
// Read-only against the app; it writes only PNGs and a JSON result file.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || '/tmp/semanticus-joint-review/lane-reports/cp-header-shots';
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
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// What the header actually shows, read off the live DOM rather than off the stylesheet.
const probeHeader = (page) => page.evaluate(() => {
  const header = document.querySelector('.studio-chrome');
  if (!header) return { error: 'no header' };
  const box = header.getBoundingClientRect();
  const shown = (el) => !!el && el.getClientRects().length > 0;
  const named = (root, label) => [...(root || document).querySelectorAll('button')]
    .find((b) => (b.textContent || '').trim() === label);
  const utilities = header.querySelector('.studio-utilities');
  const gear = header.querySelector('.studio-settings');
  const areas = [...header.querySelectorAll('nav[aria-label="Studio areas"] button')]
    .filter((b) => b.getClientRects().length > 0).map((b) => (b.textContent || '').trim());
  // Nothing inside the header may stick out past the header's own box.
  const overflowing = [...header.querySelectorAll('*')].filter((el) => {
    if (el.getClientRects().length === 0) return false;
    if (el.closest('[role="menu"]')) return false;   // open dropdowns are allowed to hang below
    const r = el.getBoundingClientRect();
    return r.right > box.right + 0.5 || r.left < box.left - 0.5;
  }).map((el) => `${el.tagName.toLowerCase()}.${(el.className || '').toString().split(' ')[0]}`);
  const publish = header.querySelector('[data-testid="publish-button"]');
  const summary = header.querySelector('.studio-permission-summary');
  return {
    fold: header.dataset.fold,
    overflowing,
    headerWidth: Math.round(box.width), headerHeight: Math.round(box.height),
    scrollWidth: header.scrollWidth,
    findATool: shown(named(utilities, 'Find a tool')),
    connections: shown(named(utilities, 'Connections')),
    gear: shown(gear),
    more: shown(named(header, 'More')),
    areas,
    publishInHeader: shown(publish) ? (publish.textContent || '').trim() : null,
    publishLines: summary ? [...summary.querySelectorAll('span')].map((s) => (s.textContent || '').trim()) : null,
    summaryUnderlineAtRest: summary ? getComputedStyle(summary).textDecorationLine : null,
    runChip: (() => { const c = header.querySelector('.studio-run-chip'); return c && c.getClientRects().length ? { text: (c.textContent || '').trim(), width: Math.round(c.getBoundingClientRect().width) } : null; })(),
    pagePublishButtons: [...document.querySelectorAll('main [data-testid="publish-button"]')].length,
  };
});

const CASES = [
  { slug: 'model-home', q: 'live=1', area: null },
  { slug: 'published-with-destination', q: 'live=1', area: 'Changes', tab: 'Published' },
  // The real journey: stand on Published, press the header Publish…, and the review must open in place.
  { slug: 'published-review', q: 'live=1', area: 'Changes', tab: 'Published', pressPublish: true, settle: 3500 },
  // The harness's own ?deploy=publish seed, which now drives the header button instead of the page one.
  { slug: 'published-review-seeded', q: 'deploy=publish', settle: 6000 },
  // No destination: the fixture pins one, so seed the harness config with an unavailable publishing role, then
  // press the header Publish… and prove it lands on the choose-a-destination card instead of a confirm.
  { slug: 'published-no-destination', q: '', area: 'Changes', tab: 'Published', noDestination: true, pressPublish: true },
];

const NO_DESTINATION_CONTEXT = {
  editing: { available: true, modelName: 'Contoso Retail', source: 'C:\\Models\\Contoso\\Contoso.SemanticModel\\definition', kind: 'file', live: false },
  querying: { available: false, live: false },
  publishing: { available: false, live: false },
  reference: { available: false, live: false },
  relationship: 'editingOnly', twoModelsInPlay: false, publishDestinationSeparateFromQuerying: false,
  summary: 'Editing Contoso Retail from local source-controlled files. No publish destination is set yet.',
};
const WIDTHS = [[1440, 900], [1280, 800], [1000, 800]];
const results = [];
for (const job of CASES) {
  for (const [vw, vh] of WIDTHS) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 2 });
    let note = '';
    try {
      if (job.noDestination) await page.evaluateOnNewDocument((ctx) => { window.__UISHOT__ = Object.assign({}, window.__UISHOT__, { connectionContext: ctx }); }, NO_DESTINATION_CONTEXT);
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${job.q ? '?' + job.q : ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('nav button', { timeout: 15000 });
      await wait(1200);
      if (job.area) {
        const okArea = await page.evaluate((a) => { const b = [...document.querySelectorAll('nav button')].find((x) => (x.textContent || '').trim() === a); if (b) b.click(); return !!b; }, job.area);
        if (!okArea) note += `area "${job.area}" not found; `;
        await wait(800);
        const okTab = await page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, job.tab);
        if (!okTab) note += `tab "${job.tab}" not found; `;
      }
      if (job.pressPublish) {
        const pressed = await page.evaluate(() => { const b = document.querySelector('.studio-chrome [data-testid="publish-button"]'); if (b) b.click(); return !!b; });
        if (!pressed) note += 'header Publish… not found; ';
        await wait(1500);
      }
      await wait(job.settle || 2500);
      const probe = await probeHeader(page);
      probe.pageState = await page.evaluate(() => ({
        chooseDestination: document.body.innerText.includes('No publish destination yet'),
        chooseButtons: [...document.querySelectorAll('main button')].filter((b) => (b.textContent || '').trim() === 'Choose destination' && b.getClientRects().length).length,
        confirmOpen: !!document.querySelector('[data-testid="publish-confirm"]'),
        modes: ['What to publish', 'Roll back', 'Promote', 'Advanced'].filter((m) => [...document.querySelectorAll('main button')].some((b) => (b.textContent || '').trim() === m && b.getClientRects().length)),
        ctrlS: document.body.innerText.includes('Ctrl+S saves what is in front of you'),
        effect: document.body.innerText.includes('Publishing writes your reviewed changes to the destination'),
      }));
      const out = join(OUT, `${job.slug}-${vw}x${vh}.png`);
      await page.screenshot({ path: out, fullPage: false });
      results.push({ slug: job.slug, vp: `${vw}x${vh}`, out, errors, note, probe });
    } catch (e) {
      results.push({ slug: job.slug, vp: `${vw}x${vh}`, out: null, errors, note: note + 'THREW: ' + e.message, probe: null });
    }
    await page.close();
  }
}
await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
for (const r of results) {
  const p = r.probe || {};
  console.log(`${r.slug} ${r.vp} fold=${p.fold} find=${p.findATool} conn=${p.connections} gear=${p.gear} more=${p.more} areas=${(p.areas || []).length} chip=${p.runChip ? p.runChip.width + 'px' : 'none'} overflow=${(p.overflowing || []).length} pagePublish=${p.pagePublishButtons} headerPublish=${JSON.stringify(p.publishInHeader)} errors=${r.errors.length} ${r.note}`);
}
