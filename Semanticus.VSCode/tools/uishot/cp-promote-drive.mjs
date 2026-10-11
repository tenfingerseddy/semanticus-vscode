// Acceptance driver for the cp/promote-account lane: Publish > Promote, driven through the
// sign-in failure Kane hit on the Yoga (2026-09-14) and out the other side.
//
// The harness answers a Fabric read per sign-in mode, the way a real token acquisition does: the Azure
// command line fails with Azure.Identity's own words, a browser sign-in succeeds. ?promote=azcli starts
// the page on a destination that was opened with the command line, so the failure, the plain line, the
// Sign in action and the recovery are all one run.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = req.url || '/';
  const p = normalize(join(webRoot, decodeURIComponent(url.split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
const browser = await puppeteer.launch({ executablePath, headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';

// Each job: where to go (Published is the Publish area's only tool, so pressing the area lands on it and there is
// no tab to press), then the buttons to press in order, then what the page must show (`expect`, judged below).

// What each job's page must show once its steps have run. A value is matched exactly, { has } is a substring the
// text must contain, { min } a lowest count, and null means the thing must be absent. The driver used only to NOTE a
// missing area or button and print what it saw, so when the "Changes" area was renamed Publish every job stopped
// short of the page and it still exited 0 (verifier, m11/header-stable round 2). Now a note or a miss fails the run.
const judge = (want, seen) => {
  const misses = [];
  for (const [k, w] of Object.entries(want || {})) {
    const path = k.split('.'); let v = seen; for (const p of path) v = v == null ? undefined : v[p];
    const ok = w === null ? v == null
      : typeof w === 'object' && 'has' in w ? typeof v === 'string' && v.includes(w.has)
      : typeof w === 'object' && 'min' in w ? typeof v === 'number' && v >= w.min
      : v === w;
    if (!ok) misses.push(`${k} is ${JSON.stringify(v)}, expected ${JSON.stringify(w)}`);
  }
  return misses;
};
const NOT_SIGNED_IN = 'not signed in to the Azure command line';
const JOBS = [
  { slug: 'promote-azcli-failure', area: 'Publish', q: 'promote=azcli', press: ['Promote', 'Find my pipelines'],
    expect: { problem: { has: NOT_SIGNED_IN }, fix: 'Sign in', pickerValue: 'azcli', pipelines: 0 } },
  { slug: 'promote-azcli-signed-in', area: 'Publish', q: 'promote=azcli', press: ['Promote', 'Find my pipelines', 'Sign in'],
    expect: { problem: null, fix: null, pickerValue: 'interactive', pipelines: { min: 1 } } },
  { slug: 'promote-default-picker', area: 'Publish', press: ['Promote'],
    expect: { problem: null, pickerValue: 'interactive', pipelines: 0 } },
  { slug: 'promote-default-found', area: 'Publish', press: ['Promote', 'Find my pipelines'],
    expect: { problem: null, pickerValue: 'interactive', pipelines: { min: 1 }, go: 'Promote 3 items from Test to Production', steps: 0, tokenShown: false } },
];

const clickText = (label) => (t) => {
  const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  if (b) b.click();
  return !!b;
};

const results = [];
for (const job of JOBS) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 900, deviceScaleFactor: 2 });
  let note = '';
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${job.q ? '?' + job.q : ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('nav button', { timeout: 15000 });
    await wait(1200);
    const okArea = await page.evaluate((a) => { const b = [...document.querySelectorAll('nav button')].find((x) => (x.textContent || '').trim() === a); if (b) b.click(); return !!b; }, job.area);
    if (!okArea) note += `area "${job.area}" not found; `;
    await wait(900);
    if (job.tab) {
      const okTab = await page.evaluate(clickText(), job.tab);
      if (!okTab) note += `tab "${job.tab}" not found; `;
    }
    await wait(1200);
    for (const label of job.press) {
      const ok = await page.evaluate(clickText(), label);
      if (!ok) note += `button "${label}" not found; `;
      await wait(1400);
    }
    // What the page actually says, so a shot cannot be reported as proving something it does not show.
    const seen = await page.evaluate(() => {
      const t = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').trim() : null; };
      const picker = document.querySelector('[data-testid="account-picker"] select');
      return {
        problem: t('[data-testid="promote-problem"]'),
        go: t('[data-testid="promote-go"]'),   // the one write button: the check ran by itself, so it already names the move
        steps: [...document.querySelectorAll('button')].filter((b) => /^(Preview|Confirm)/.test((b.textContent || '').trim())).length,
        tokenShown: /confirm token|PROD-AB12CD34EF/.test(document.body.innerText),
        fix: t('[data-testid="promote-signin-fix"]'),
        pickerValue: picker ? picker.value : null,
        pickerLabel: picker && picker.selectedOptions[0] ? picker.selectedOptions[0].textContent : null,
        pipelines: [...document.querySelectorAll('button')].map((b) => (b.textContent || '').trim()).filter((x) => x === 'Contoso Sales' || x === 'Finance').length,
      };
    });
    const out = join(OUT, `${LABEL}-${job.slug}-1366x900.png`);
    await page.screenshot({ path: out, fullPage: false });
    results.push({ job: job.slug, seen, errors, note, out });
  } catch (e) {
    results.push({ job: job.slug, errors, note: note + 'FATAL: ' + e.message });
  } finally {
    await page.close();
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, `results-promote-${LABEL}.json`), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
const failures = results.flatMap((r) => [
  ...(r.note ? [`${r.job}: ${r.note.trim()}`] : []),
  ...(r.seen ? judge(JOBS.find((j) => j.slug === r.job).expect, r.seen).map((m) => `${r.job}: ${m}`) : [`${r.job}: nothing was read`]),
]);
console.log(failures.length ? `FAIL ${failures.length} check(s):\n  ${failures.join('\n  ')}` : `ok all ${results.length} jobs reached their page and showed what they should`);
if (failures.length) process.exit(1);
