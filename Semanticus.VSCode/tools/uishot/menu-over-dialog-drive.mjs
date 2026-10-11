// A menu opened over a dialog shows above it (UAT preflight S00, UAT-S00-003, F-292).
//
// Connections opens as a dialog over the Studio but leaves the header uncovered, so every header button still
// works while it is open. UAT found that More then opened its menu BEHIND the dialog: the menu was there, and a
// click on it landed on the dialog. This driver opens Connections, then presses every header button that opens a
// menu or a popover, with a real mouse click, and asks the browser what is on top at the popover's centre and near
// each of its corners. Anything but the popover itself is a failure, and the driver exits non-zero.
//
// It runs at 1000 px, where the header folds its tools into More, and at 1440 px, where Find a tool and the rest
// are on the header itself, in the three themes the other drivers use.
//
//   OUTDIR=... node tools/uishot/menu-over-dialog-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'menu-over-dialog');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `menu-over-dialog-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = (process.env.THEMES || 'dark,light,hc').split(',');
const WIDTHS = [[1000, 800], [1440, 900]];

const connectionsOpen = (page) => page.evaluate(() => {
  const d = document.querySelector('[role="dialog"][aria-label="Connections"]');
  return !!d && d.getClientRects().length > 0;
});

// The header's popover buttons, by a stable label, and where a real mouse would press them. Only buttons the
// mouse can actually reach count: one the dialog covers cannot be pressed, which is the dialog doing its job.
const headerTriggers = (page) => page.evaluate(() => {
  const header = document.querySelector('.studio-chrome');
  if (!header) return [];
  return [...header.querySelectorAll('button[aria-haspopup]')]
    .filter((b) => b.getClientRects().length > 0)
    .map((b) => {
      const r = b.getBoundingClientRect();
      const x = r.left + r.width / 2, y = r.top + r.height / 2;
      const top = document.elementFromPoint(x, y);
      return { label: b.getAttribute('aria-label') || (b.textContent || '').trim(), x, y, reachable: !!top && (top === b || b.contains(top)) };
    });
});

// What the popover that just opened is, and what the browser says is on top at its centre and inset corners.
const probePopover = (page) => page.evaluate(() => {
  const header = document.querySelector('.studio-chrome');
  const pops = [...header.querySelectorAll('[role="menu"], [role="dialog"], [role="listbox"], .shadow-xl')]
    .filter((el) => el.getClientRects().length > 0);
  const pop = pops[pops.length - 1];
  if (!pop) return { opened: false };
  const r = pop.getBoundingClientRect();
  const inset = 6;
  const points = [
    ['centre', r.left + r.width / 2, r.top + r.height / 2],
    ['top left', r.left + inset, r.top + inset],
    ['bottom right', r.right - inset, r.bottom - inset],
  ];
  const hits = points.map(([name, x, y]) => {
    const top = document.elementFromPoint(x, y);
    const covering = top && !pop.contains(top) ? (top.closest('[role="dialog"]')?.getAttribute('aria-label') || top.tagName.toLowerCase()) : null;
    return { name, onTop: !!top && pop.contains(top), covering };
  });
  return { opened: true, name: pop.getAttribute('aria-label') || pop.getAttribute('role') || 'popover', zIndex: getComputedStyle(pop).zIndex, hits };
});

const results = [];
let failures = 0;
for (const theme of THEMES) {
  for (const [vw, vh] of WIDTHS) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 1 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?live=1`, { waitUntil: 'networkidle0', timeout: 30000 });
    await applyTheme(page, theme);
    await page.waitForSelector('.studio-chrome', { timeout: 15000 });
    await wait(1200);

    const labels = (await headerTriggers(page)).map((t) => t.label);
    for (const label of labels) {
      const scene = `${theme}-${vw}-${label.replace(/[^A-Za-z0-9]+/g, '-').toLowerCase()}`;
      const row = { scene, theme, width: vw, trigger: label, errors };
      // Connections open first, the way a person has it open: through the header's own Connections, or More.
      if (!(await connectionsOpen(page))) {
        await page.evaluate(() => {
          const header = document.querySelector('.studio-chrome');
          const direct = [...header.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === 'Connections' && b.getClientRects().length > 0);
          if (direct) { direct.click(); return; }
          header.querySelector('button[aria-label="More Studio tools"]')?.click();
        });
        await wait(300);
        await page.evaluate(() => {
          const item = [...document.querySelectorAll('.studio-chrome [role="menuitem"]')].find((b) => (b.textContent || '').trim() === 'Connections');
          item?.click();
        });
        await wait(900);
      }
      row.connectionsOpen = await connectionsOpen(page);
      const trigger = (await headerTriggers(page)).find((t) => t.label === label);
      row.reachable = !!trigger?.reachable;
      if (!row.connectionsOpen || !trigger || !trigger.reachable) {
        // A trigger the dialog covers cannot open anything over it; say so, and it is not a failure.
        row.verdict = !row.connectionsOpen ? 'FAIL: Connections did not open' : 'skipped: the dialog covers this button';
        if (!row.connectionsOpen) failures++;
        results.push(row);
        continue;
      }
      await page.mouse.click(trigger.x, trigger.y);   // a real click, on whatever is really on top there
      await wait(500);
      row.popover = await probePopover(page);
      const bad = !row.popover.opened || row.popover.hits.some((h) => !h.onTop);
      row.verdict = bad ? 'FAIL' : 'ok';
      if (bad) failures++;
      row.shot = join(OUT, `${scene}.png`);
      await page.screenshot({ path: row.shot });
      results.push(row);
      // Close it the way a person would, and leave Connections open for the next button.
      await page.keyboard.press('Escape');
      await wait(200);
      if (await page.evaluate(() => [...document.querySelectorAll('.studio-chrome [role="menu"], .studio-chrome [role="dialog"]')].some((el) => el.getClientRects().length > 0))) {
        await page.mouse.click(trigger.x, trigger.y);
        await wait(200);
      }
    }
    await page.close();
  }
}
// Journey m11 F18: with Connections open, More > Plan loaded the Plan BEHIND the dialog, so nothing seemed to happen.
// Choosing where to go from the header is leaving the dialog: it closes and the page is in front. Driven at 700px,
// where the areas are inside More, and at 1440 through the area button itself when the dialog leaves it reachable.
const areaTrips = [];
for (const [vw, vh, via] of [[700, 800, 'more'], [1440, 900, 'button']]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?live=1&tab=diagram`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.studio-chrome', { timeout: 15000 });
  await wait(1200);
  await page.evaluate(() => {
    const header = document.querySelector('.studio-chrome');
    const direct = [...header.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === 'Connections' && b.getClientRects().length > 0);
    if (direct) { direct.click(); return; }
    header.querySelector('button[aria-label="More Studio tools"]')?.click();
  });
  await wait(300);
  await page.evaluate(() => [...document.querySelectorAll('.studio-chrome [role="menuitem"]')].find((b) => (b.textContent || '').trim() === 'Connections')?.click());
  await wait(900);
  const trip = { width: vw, via, openedFirst: await connectionsOpen(page) };
  if (via === 'more') {
    const more = (await headerTriggers(page)).find((t) => t.label === 'More Studio tools');
    if (more?.reachable) await page.mouse.click(more.x, more.y);
    await wait(400);
    const item = await page.evaluate(() => {
      const b = [...document.querySelectorAll('.studio-chrome .compact-areas [role="menuitem"]')].find((x) => (x.textContent || '').trim() === 'Plan' && x.getClientRects().length > 0);
      if (!b) return null; const r = b.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    });
    trip.found = !!item;
    if (item) await page.mouse.click(item.x, item.y);
  } else {
    const at = await page.evaluate(() => {
      const b = [...document.querySelectorAll('nav[aria-label="Studio areas"] button')].find((x) => (x.textContent || '').trim() === 'Plan');
      if (!b) return null; const r = b.getBoundingClientRect(); const x = r.left + r.width / 2, y = r.top + r.height / 2;
      const top = document.elementFromPoint(x, y); return { x, y, reachable: !!top && (top === b || b.contains(top)) };
    });
    trip.found = !!at?.reachable;
    if (at?.reachable) await page.mouse.click(at.x, at.y);
  }
  await wait(900);
  trip.connectionsAfter = await connectionsOpen(page);
  trip.planInFront = await page.evaluate(() => {
    const plan = document.querySelector('[data-testid="plan-page"]');
    if (!plan) return false;
    const r = plan.getBoundingClientRect();
    const top = document.elementFromPoint(r.left + Math.min(200, r.width / 2), r.top + 40);
    return !!top && plan.contains(top);
  });
  trip.errors = errors;
  await page.screenshot({ path: join(OUT, `area-from-${via}-over-connections-${vw}.png`) });
  const ok = trip.openedFirst && trip.found && !trip.connectionsAfter && trip.planInFront && errors.length === 0;
  trip.verdict = ok ? 'ok' : 'FAIL';
  if (!ok) failures++;
  areaTrips.push(trip);
  console.log(`${trip.verdict.padEnd(6)} Plan from ${via} at ${vw} with Connections open: dialog ${trip.connectionsAfter ? 'still open' : 'closed'}, Plan ${trip.planInFront ? 'in front' : 'not in front'}${trip.found ? '' : ', Plan not found or covered'}`);
  await page.close();
}

await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, areaTrips }, null, 2));
for (const r of results) {
  const hits = r.popover?.hits?.map((h) => `${h.name}:${h.onTop ? 'on top' : 'under ' + h.covering}`).join(', ') ?? '';
  console.log(`${r.verdict.padEnd(44)} ${r.scene}  z=${r.popover?.zIndex ?? '-'}  ${hits}${r.errors.length ? '  errors=' + r.errors.length : ''}`);
}
const tried = results.filter((r) => r.popover).length;
console.log(`${tried} popovers opened over Connections, ${failures} failed.`);
if (tried === 0) { console.log('FAIL: no header popover could be tried, so nothing was proved.'); process.exit(1); }
process.exit(failures ? 1 : 0);
