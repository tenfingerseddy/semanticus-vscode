// Driven proof for the Diagram's no-overlap rule (Kane, 25 Sep): expanding or collapsing table cards, one or all,
// moves the cards so none overlaps another, and a layout change keeps each card's expanded or collapsed state.
// Serves the built bundle, drives the real harness (?g=stack: a star schema plus a tall column of eight calculated
// and field-parameter tables) with real clicks, measures every card on the live DOM, and writes a PNG per step in
// Dark Modern and Light Modern. Exits non-zero when any step shows an overlap or a lost expansion.
//   node diagram-fit-drive.mjs            (OUTDIR=<dir> to choose where the PNGs and results.json land)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-fit');
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

// Every card in FLOW coordinates (the node's translate, plus its rendered size divided by the zoom), which cards
// overlap, and which cards are open. Flow coordinates, not screen ones, so two probes compare across a re-fit.
const probe = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = vp ? Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]) : 1;
  const cards = [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform) || [0, 0, 0];
    const r = el.getBoundingClientRect();
    return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom,
      open: !!el.querySelector('button[aria-label^="Hide the columns of"]') };
  });
  const overlaps = [];
  for (let i = 0; i < cards.length; i++) for (let j = i + 1; j < cards.length; j++) {
    const a = cards[i], b = cards[j];
    if (a.x < b.x + b.w - 0.5 && b.x < a.x + a.w - 0.5 && a.y < b.y + b.h - 0.5 && b.y < a.y + a.h - 0.5) overlaps.push(a.id + ' / ' + b.id);
  }
  return { cards, overlaps, open: cards.filter((c) => c.open).map((c) => c.id).sort() };
});
const clickText = (page, text) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, text);
const toggle = (page, table) => page.evaluate((n) => {
  const b = document.querySelector(`button[aria-label="Show the columns of ${n}"], button[aria-label="Hide the columns of ${n}"]`);
  if (b) b.click(); return !!b;
}, table);
const fit = async (page) => { await clickText(page, 'Fit'); await wait(700); };

const ONE = ['Metrics', 'Measure Picker', 'Sales'];   // opened one at a time; Metrics sits in the middle of the tall stack
const results = [];
let failed = 0;
const check = (theme, step, ok, detail) => { results.push({ theme, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${theme} ${step}${detail ? ': ' + detail : ''}`); };

for (const theme of ['dark', 'light']) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 2 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?g=stack&tab=diagram`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.react-flow__node', { timeout: 20000 });
  await wait(3500);
  await applyTheme(page, theme);
  const shot = async (name) => { const p = join(OUT, `${theme}-${name}.png`); await page.screenshot({ path: p }); return p; };

  await fit(page);
  const start = await probe(page);
  await shot('1-collapsed');
  check(theme, 'collapsed start has no overlap', start.overlaps.length === 0, start.overlaps.join(', '));

  await clickText(page, 'Expand all'); await wait(1500); await fit(page);
  const all = await probe(page);
  await shot('2-expand-all');
  check(theme, 'expand all opens every card', all.open.length === all.cards.length, `${all.open.length}/${all.cards.length} open`);
  check(theme, 'expand all has no overlap', all.overlaps.length === 0, all.overlaps.join(', '));

  await clickText(page, 'Collapse all'); await wait(1500); await fit(page);
  const back = await probe(page);
  await shot('3-collapse-all');
  const moved = back.cards.filter((c) => { const s = start.cards.find((x) => x.id === c.id); return !s || Math.abs(s.x - c.x) > 1 || Math.abs(s.y - c.y) > 1; }).map((c) => c.id);
  check(theme, 'collapse all has no overlap', back.overlaps.length === 0, back.overlaps.join(', '));
  check(theme, 'collapse all returns every card to where it started', moved.length === 0, moved.join(', '));

  for (const n of ONE) { await toggle(page, n); await wait(900); }
  await fit(page);
  const some = await probe(page);
  await shot('4-expand-three');
  check(theme, 'expanding one card at a time has no overlap', some.overlaps.length === 0, some.overlaps.join(', '));
  check(theme, 'the three chosen cards are open', JSON.stringify(some.open) === JSON.stringify([...ONE].sort()), some.open.join(', '));

  // Typing in an open card's column filter must move nothing, and must not change that card's height.
  const typeFilter = (q) => page.evaluate((v) => {
    const card = document.querySelector('.react-flow__node[data-id="Metrics"]');
    const input = card && card.querySelector('input');
    if (!input) return false;
    const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    set.call(input, v); input.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  }, q);
  const same = (a, b) => a.cards.every((c) => { const o = b.cards.find((x) => x.id === c.id); return o && Math.abs(o.x - c.x) <= 1 && Math.abs(o.y - c.y) <= 1 && Math.abs(o.h - c.h) <= 1; });
  for (const q of ['Metrics', 'MetricsField1', 'zzz']) {
    const typed = await typeFilter(q); await wait(900);
    const f = await probe(page);
    if (q === 'zzz') await shot('4b-filter-no-match');
    check(theme, `typing "${q}" in a column filter moves no card and keeps the card's height`, typed && same(f, some), typed ? '' : 'no filter box');
  }
  await typeFilter(''); await wait(900);
  const cleared = await probe(page);
  check(theme, 'clearing the filter leaves every card where it was', same(cleared, some), '');

  for (const [label, name] of [['Layered', '5-layered'], ['Vertical', '6-vertical'], ['Bus matrix', '7-bus-matrix']]) {
    await clickText(page, label); await wait(1800);
    const after = await probe(page);
    await shot(name);
    check(theme, `${label} keeps the open cards open`, JSON.stringify(after.open) === JSON.stringify(some.open), after.open.join(', ') || 'none open');
    check(theme, `${label} has no overlap`, after.overlaps.length === 0, after.overlaps.join(', '));
  }

  // A saved diagram switch: make a new diagram, put an open table and a closed one on it, then switch back.
  await clickText(page, '＋ New'); await wait(400);
  await page.keyboard.press('Enter'); await wait(400);
  for (const n of ['Metrics', 'Date']) {
    await page.evaluate((v) => {
      const s = [...document.querySelectorAll('select')].find((x) => x.title === 'Add a table to this diagram');
      if (!s) return;
      const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
      set.call(s, v); s.dispatchEvent(new Event('change', { bubbles: true }));
    }, n);
    await wait(900);
  }
  await fit(page);
  const custom = await probe(page);
  await shot('8-custom-diagram');
  check(theme, 'a new saved diagram shows Metrics open and Date closed', JSON.stringify(custom.open) === JSON.stringify(['Metrics']), custom.open.join(', ') || 'none open');
  check(theme, 'the new diagram has no overlap', custom.overlaps.length === 0, custom.overlaps.join(', '));
  await page.evaluate(() => {
    const s = [...document.querySelectorAll('select')].find((x) => x.title === 'Switch diagram');
    const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
    set.call(s, 'all'); s.dispatchEvent(new Event('change', { bubbles: true }));
  });
  await wait(1800);
  const home = await probe(page);
  await shot('9-back-to-all-tables');
  check(theme, 'switching back to All tables keeps the same cards open', JSON.stringify(home.open) === JSON.stringify(some.open), home.open.join(', ') || 'none open');
  check(theme, 'All tables after the switch has no overlap', home.overlaps.length === 0, home.overlaps.join(', '));
  check(theme, 'no page errors', errors.length === 0, errors.join(' | '));
  await page.close();
}
// Journey m11 F17. In a Studio about 790px wide, the first open framed the whole bus matrix at once, which put card
// text at about 4px, and the Arrange menu stayed open after Expand all was picked from it. The first frame keeps a
// readable zoom (the smallest card text at least 9 screen px), and the menu closes on a pick.
for (const g of ['', 'g=stack&']) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 792, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${g}tab=diagram`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.react-flow__node', { timeout: 20000 });
  await wait(3500);
  const label = `first open at 792 (${g ? 'stack' : 'default'} graph)`;
  const read = await page.evaluate(() => {
    const vp = document.querySelector('.react-flow__viewport');
    const zoom = vp ? Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]) : 1;
    // The smallest text a card draws, as it lands on screen.
    const sizes = [...document.querySelectorAll('.react-flow__node *')].filter((e) => e.childNodes.length && [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim()))
      .map((e) => parseFloat(getComputedStyle(e).fontSize) * zoom);
    const pane = document.querySelector('.react-flow').getBoundingClientRect();
    const onScreen = [...document.querySelectorAll('.react-flow__node')].filter((n) => { const r = n.getBoundingClientRect(); return r.right > pane.left && r.left < pane.right && r.bottom > pane.top && r.top < pane.bottom; }).length;
    return { zoom, smallest: sizes.length ? Math.min(...sizes) : 0, onScreen, arrangeOpen: !!document.querySelector('[role="menu"][aria-label="Arrange"]') };
  });
  await page.screenshot({ path: join(OUT, `dark-first-open-792${g ? '-stack' : ''}.png`) });
  check('dark', `${label}: card text is readable`, read.smallest >= 9, `zoom ${read.zoom.toFixed(2)}, smallest card text ${read.smallest.toFixed(1)}px`);
  check('dark', `${label}: cards are on screen`, read.onScreen > 0, `${read.onScreen} on screen`);
  check('dark', `${label}: the Arrange menu starts closed`, !read.arrangeOpen, '');
  const opened = await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith('Arrange')); if (b) b.click(); return !!b; });
  await wait(300);
  check('dark', `${label}: Arrange is a menu at this width`, opened && await page.evaluate(() => !!document.querySelector('[role="menu"][aria-label="Arrange"]')), '');
  await clickText(page, 'Expand all'); await wait(1200);
  check('dark', `${label}: picking Expand all closes the Arrange menu`, !(await page.evaluate(() => !!document.querySelector('[role="menu"][aria-label="Arrange"]'))), '');
  check('dark', `${label}: Expand all still opened the cards`, (await probe(page)).open.length > 0, '');
  // Journey m11-2: a layout switch refitted the whole diagram and the text went back to about 4px. Each switch keeps
  // the readable zoom; Fit on the row is still the way to see everything at once.
  for (const layout of ['Vertical', 'Layered', 'Bus matrix']) {
    await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith('Arrange')); if (b) b.click(); });
    await wait(300);
    const picked = await clickText(page, layout); await wait(1800);
    const after = await page.evaluate(() => {
      const vp = document.querySelector('.react-flow__viewport');
      const zoom = vp ? Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]) : 1;
      const sizes = [...document.querySelectorAll('.react-flow__node *')].filter((e) => [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim()))
        .map((e) => parseFloat(getComputedStyle(e).fontSize) * zoom);
      return { zoom, smallest: sizes.length ? Math.min(...sizes) : 0 };
    });
    check('dark', `${label}: ${layout} keeps card text readable`, picked && after.smallest >= 9, `zoom ${after.zoom.toFixed(2)}, smallest card text ${after.smallest.toFixed(1)}px`);
  }
  check('dark', `${label}: no page errors`, errors.length === 0, errors.join(' | '));
  await page.close();
}

// UAT-S11-1-004: with saved positions in the engine (?lay=1) and a first open of the Diagram, one drag of Sales
// saved all eleven tables, overwriting the other tables' stored positions with the canvas's own arrangement. A drag
// saves only the table it moved, and opening the Diagram saves nothing.
{
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?lay=1&tab=diagram`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.react-flow__node[data-id="Sales"]', { timeout: 20000 });
  await wait(3500);
  const calls = () => page.evaluate(() => (window.__saveLayoutCalls || []).map((c) => c.slice()));
  const opened = await calls();
  check('dark', 'opening the Diagram saves no table position', opened.length === 0, opened.map((c) => c.join(', ')).join(' | '));
  const dragOnce = async (name, dx, dy) => {
    const box = await page.evaluate((n) => {
      const el = document.querySelector(`.react-flow__node[data-id="${n}"]`);
      const r = el.getBoundingClientRect();
      return { x: r.left + Math.min(40, r.width / 2), y: r.top + 12 };
    }, name);
    await page.mouse.move(box.x, box.y);
    await page.mouse.down();
    for (let i = 1; i <= 8; i++) { await page.mouse.move(box.x + (dx * i) / 8, box.y + (dy * i) / 8); await wait(30); }
    await page.mouse.up();
    await wait(1200);
  };
  await dragOnce('Sales', 140, 60);
  const first = (await calls()).slice(opened.length);
  await page.screenshot({ path: join(OUT, 'dark-drag-saves-only-moved.png') });
  const sent = [...new Set(first.flat())].sort();
  check('dark', 'the first drag of Sales saves only Sales', first.length > 0 && JSON.stringify(sent) === JSON.stringify(['Sales']), sent.join(', ') || 'nothing saved');
  await dragOnce('Date', -80, 90);
  const second = (await calls()).slice(opened.length + first.length);
  const sent2 = [...new Set(second.flat())].sort();
  check('dark', 'a second drag of Date saves only Date', second.length > 0 && JSON.stringify(sent2) === JSON.stringify(['Date']), sent2.join(', ') || 'nothing saved');
  check('dark', 'drag scene: no page errors', errors.length === 0, errors.join(' | '));
  await page.close();
}

await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
console.log(`${results.length - failed}/${results.length} checks passed; shots in ${OUT}`);
process.exit(failed ? 1 : 0);
