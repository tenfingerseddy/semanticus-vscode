// Driven proof for the Publish page (round 1, lane P1): the Changes tab with its one button, the label as the
// guard, the parent-needed rule, the result line with Undo publish, an unreachable destination, no destination,
// Earlier versions and Promote with one button each. Every scene is a PNG plus a probe of the live DOM, and the
// run fails when a scene does not show what it should. Read-only against the app; it writes PNGs and one JSON file.
//
//   node publish-p1-drive.mjs            -> $OUTDIR (default /tmp/semanticus-joint-review/r1/publish-p1/after)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || '/tmp/semanticus-joint-review/r1/publish-p1/after';
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
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); };
const results = [];

async function open(q, w = 1440, h = 900) {
  const page = await browser.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') page.errors.push('console: ' + m.text()); });
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${q}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(3500);   // the deep link is sent repeatedly until the session lands; the page then reads its destination
  return page;
}
const text = (page, sel) => page.evaluate((s) => { const e = document.querySelector(s); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; }, sel);
const count = (page, sel) => page.evaluate((s) => document.querySelectorAll(s).length, sel);
const clickSel = (page, sel) => page.evaluate((s) => { const e = document.querySelector(s); if (e) e.click(); return !!e; }, sel);
const clickText = (page, label) => page.evaluate((l) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === l && x.getClientRects().length); if (b) b.click(); return !!b; }, label);
const rowTick = (page, name) => page.evaluate((n) => {
  const row = [...document.querySelectorAll('[data-testid="publish-row"]')].find((r) => (r.textContent || '').includes(n));
  const box = row && row.querySelector('input[type="checkbox"]'); if (box) box.click(); return !!box;
}, name);
const shot = async (page, slug) => { const out = join(OUT, `${slug}.png`); await page.screenshot({ path: out }); return out; };
const goLabel = (page) => text(page, '[data-testid="publish-go"]');
const record = (slug, page, extra = {}) => results.push({ slug, errors: page.errors, ...extra });

// 1. The list, ticked, and the button at the top reading "Publish N changes to X".
{
  const page = await open('live=1&conn=1&tab=deploy');
  const label = await goLabel(page);
  expect(label === 'Publish 7 changes to Contoso Sales', `scene 1: the button reads ${JSON.stringify(label)}`);
  expect(await count(page, '[data-testid="publish-row"]') === 8, 'scene 1: eight rows');
  expect(await count(page, '[data-testid="publish-confirm"]') === 0, 'scene 1: no review card');
  const tabs = await page.evaluate(() => [...document.querySelectorAll('[aria-label="Publish views"] button')].map((b) => b.textContent.trim()));
  expect(JSON.stringify(tabs) === JSON.stringify(['Changes', 'Earlier versions', 'Promote']), `scene 1: tabs ${JSON.stringify(tabs)}`);
  expect(await page.evaluate(() => !document.querySelector('.studio-chrome [data-testid="publish-button"]')), 'scene 1: the header Publish is hidden on the page');
  expect(await page.evaluate(() => !/Advanced|Compare again|Validate selection|Source|Target/.test(document.querySelector('main')?.innerText || document.body.innerText)), 'scene 1: no Advanced, no Compare again, no Validate, no pickers');
  // (textContent runs the header's parts together, so the checks above read them without spaces.)
  // The button sits on the destination line, top right.
  const where = await page.evaluate(() => { const b = document.querySelector('[data-testid="publish-go"]').getBoundingClientRect(); const h = document.querySelector('[data-testid="deploy-header"]').getBoundingClientRect(); return { top: b.top, right: b.right, hTop: h.top, hBottom: h.bottom, vw: innerWidth }; });
  expect(where.top >= where.hTop - 2 && where.top <= where.hBottom && where.right > where.vw - 60, `scene 1: the button is top right on the destination line ${JSON.stringify(where)}`);
  const line = await text(page, '[data-testid="deploy-header"]');
  expect(/^To Contoso SalesUATas megan@contoso\.comChange/.test(line || ''), `scene 1: header line ${JSON.stringify(line)}`);
  await shot(page, '1-changes-list'); record('1', page, { label, line }); await page.close();
}

// 2. Untick one row: the count drops. Tick a removal: the label says "and remove 1".
{
  const page = await open('live=1&conn=1&tab=deploy');
  await rowTick(page, 'CustomerKey'); await wait(200);
  const six = await goLabel(page);
  expect(six === 'Publish 6 changes to Contoso Sales', `scene 2: after one untick ${JSON.stringify(six)}`);
  await rowTick(page, 'Legacy Margin'); await wait(200);
  const rem = await goLabel(page);
  expect(rem === 'Publish 6 changes and remove 1 from Contoso Sales', `scene 2: with a removal ${JSON.stringify(rem)}`);
  const note = await page.evaluate(() => [...document.querySelectorAll('[data-testid="publish-row"]')].find((r) => r.textContent.includes('Legacy Margin')).textContent.replace(/\s+/g, ' '));
  expect(/only on Contoso Sales\. Tick to remove it there/.test(note), `scene 2: the removal row says what ticking does: ${note}`);
  await shot(page, '2-untick-and-removal'); record('2', page, { six, rem }); await page.close();
}

// 3. A row that needs its parent: the note and "Tick ... too", left out of the count; ticking the parent clears it.
{
  const page = await open('live=1&conn=1&tab=deploy');
  await rowTick(page, 'Returns \u00b7 new table'); await wait(200);   // untick the new Returns table
  const five = await goLabel(page);
  const needs = await text(page, '[data-testid="publish-needs"]');
  expect(five === 'Publish 5 changes to Contoso Sales', `scene 3: Returns and Return Rate both leave the count ${JSON.stringify(five)}`);
  expect(needs === 'Needs the Returns table, which is not ticked. Tick Returns too', `scene 3: the note ${JSON.stringify(needs)}`);
  expect(await page.evaluate(() => { const r = [...document.querySelectorAll('[data-testid="publish-row"]')].find((x) => x.textContent.includes('Return Rate')); const b = r.querySelector('input'); return b.disabled && !b.checked; }), 'scene 3: the blocked row cannot be ticked');
  await shot(page, '3-needs-parent');
  await clickSel(page, '[data-testid="publish-tick-parent"]'); await wait(200);
  const seven = await goLabel(page);
  expect(seven === 'Publish 7 changes to Contoso Sales' && await count(page, '[data-testid="publish-needs"]') === 0, `scene 3: ticking Returns clears it ${JSON.stringify(seven)}`);
  record('3', page, { five, needs, seven }); await page.close();
}

// 4. The press: ONE press from the Publish tab to "Published", then Undo publish.
{
  const page = await open('live=1&conn=1&tab=deploy');
  let presses = 0;
  presses += (await clickSel(page, '[data-testid="publish-go"]')) ? 1 : 0;
  await wait(1500);
  const result = await text(page, '[data-testid="publish-result"]');
  expect(/^Published 7 changes to Contoso Sales\. Your copy did not change\.Undo publish$/.test(result || ''), `scene 4: result line ${JSON.stringify(result)}`);
  expect(presses === 1, 'scene 4: one press');
  expect(await page.evaluate(() => !document.querySelector('input[placeholder*="reason" i], input[placeholder*="anyway" i]')), 'scene 4: no reason box');
  await shot(page, '4a-published');
  await clickSel(page, '[data-testid="publish-undo"]'); await wait(1500);
  const back = await text(page, '[data-testid="publish-result"]');
  expect(/^Contoso Sales is back to how it was before \d\d:\d\d\.$/.test(back || ''), `scene 4: after Undo ${JSON.stringify(back)}`);
  await shot(page, '4b-undone'); record('4', page, { presses, result, back }); await page.close();
}

// 5. Destination unreachable: "Could not reach X." and Sign in, no list, no button.
{
  const page = await open('live=1&tab=deploy&pub=signin');
  const t = await text(page, '[data-testid="publish-unreachable"]');
  expect(/^Could not reach Contoso Sales\./.test(t || '') && /Sign in$/.test(t || ''), `scene 5: ${JSON.stringify(t)}`);
  expect(await count(page, '[data-testid="publish-list"]') === 0 && await count(page, '[data-testid="publish-go"]') === 0, 'scene 5: no list and no button');
  await shot(page, '5-unreachable'); record('5', page, { t }); await page.close();
}

// 6. No destination: one Choose destination button.
{
  const page = await open('pub=nodest&tab=deploy');
  const n = await page.evaluate(() => [...document.querySelectorAll('main button')].filter((b) => (b.textContent || '').trim() === 'Choose destination' && b.getClientRects().length).length);
  expect(n === 1, `scene 6: ${n} Choose destination buttons`);
  expect(await count(page, '[data-testid="publish-list"]') === 0 && await count(page, '[data-testid="publish-go"]') === 0, 'scene 6: no list, no Publish button');
  await shot(page, '6-no-destination'); record('6', page, { n }); await page.close();
}

// 7. Earlier versions and Promote: one button each, no Confirm step.
{
  const page = await open('live=1&conn=1&tab=deploy&deploy=versions');
  const go = await text(page, '[data-testid="restore-go"]');
  const detail = await text(page, '[data-testid="versions-detail"]');
  expect(/^Restore Contoso Sales to \d{1,2} [A-Z][a-z]{2} \d\d:\d\d$/.test(go || ''), `scene 7: restore button ${JSON.stringify(go)}`);
  expect(/This puts back 3 objects and removes 2 objects/.test(detail || ''), `scene 7: detail ${JSON.stringify(detail)}`);
  expect(await page.evaluate(() => ![...document.querySelectorAll('button')].some((b) => /^(Preview|Confirm)/.test((b.textContent || '').trim()))), 'scene 7: no Preview or Confirm button');
  await shot(page, '7a-earlier-versions'); record('7a', page, { go, detail }); await page.close();
}
{
  const page = await open('live=1&conn=1&tab=deploy&deploy=promote');
  const go = await text(page, '[data-testid="promote-go"]');
  expect(go === 'Promote 3 items from Test to Production', `scene 7: promote button ${JSON.stringify(go)}`);
  const body = await page.evaluate(() => document.body.innerText);
  expect(!/promote anyway|override|confirm token|reason \(required/i.test(body), 'scene 7: no tick box, no reason, no token line');
  await shot(page, '7b-promote'); record('7b', page, { go }); await page.close();
}

// 8. A row the destination would refuse: the reason in plain words, left out of the count.
{
  const page = await open('live=1&conn=1&tab=deploy&pub=refuse');
  const label = await goLabel(page);
  const why = await text(page, '[data-testid="publish-refused"]');
  expect(label === 'Publish 6 changes to Contoso Sales', `scene 8: ${JSON.stringify(label)}`);
  expect(/different object with this name/.test(why || ''), `scene 8: ${JSON.stringify(why)}`);
  await shot(page, '8-refused-row'); record('8', page, { label, why }); await page.close();
}

// 9. A race: the engine's plain sentence, the list read again, no reason box.
{
  const page = await open('live=1&conn=1&tab=deploy&pub=race');
  await clickSel(page, '[data-testid="publish-go"]'); await wait(1500);
  const line = await text(page, '[data-testid="publish-result"]');
  expect(/^Contoso Sales changed while you were publishing\. Nothing was written\. The list is updated, press Publish again\.$/.test(line || ''), `scene 9: ${JSON.stringify(line)}`);
  expect(await goLabel(page) === 'Publish 7 changes to Contoso Sales', 'scene 9: the label is back to the list');
  await shot(page, '9-race'); record('9', page, { line }); await page.close();
}

// 10. A narrow window.
{
  const page = await open('live=1&conn=1&tab=deploy', 900, 800);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  expect(!overflow, 'scene 10: no horizontal scroll at 900 wide');
  await shot(page, '10-narrow'); record('10', page, { overflow }); await page.close();
}

await browser.close(); server.close();
for (const r of results) for (const e of r.errors) failures.push(`scene ${r.slug}: ${e}`);
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures }, null, 2));
console.log(failures.length ? `FAIL ${failures.length} check(s):\n  ${failures.join('\n  ')}` : `ok all ${results.length} scenes showed what they should`);
if (failures.length) process.exit(1);
