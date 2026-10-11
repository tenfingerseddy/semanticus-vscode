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

// `expect` is what the page must show once the case has run. A case that could not reach the page it names fails
// the run, so a renamed area cannot leave every shot on the wrong page while the driver still exits 0 (verifier,
// m11/header-stable round 2). The Publish area has one tool, so pressing the area lands on it.
const CASES = [
  // Off the Publish page the header carries the one Publish button.
  { slug: 'model-home', q: 'live=1', area: null, expect: { onPublished: false, headerPublish: 'Publish' } },
  // On the Publish page the header button is hidden: the page carries its own, on the destination line.
  { slug: 'published-with-destination', q: 'live=1&conn=1', area: 'Publish', expect: { onPublished: true, headerPublish: null, goLabel: { has: 'Publish ' }, advancedGone: true } },
  // The real journey: stand elsewhere, press the header Publish, and land on the Publish page, no review card.
  // conn=1: the harness reaches the destination only with a connection (harness.html compareModels), and with the
  // destination unreadable the header Publish is rightly disabled.
  { slug: 'published-from-header', q: 'live=1&conn=1', area: null, pressPublish: true, settle: 3500, expect: { onPublished: true, headerPublish: null, goLabel: { has: 'Publish ' } } },
  // No destination: one Choose destination button on the page and no list.
  { slug: 'published-no-destination', q: 'pub=nodest', area: 'Publish', expect: { onPublished: true, chooseDestination: true, noList: true } },
];

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
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${job.q ? '?' + job.q : ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('nav button', { timeout: 15000 });
      await wait(1200);
      if (job.area) {
        const okArea = await page.evaluate((a) => { const b = [...document.querySelectorAll('nav button')].find((x) => (x.textContent || '').trim() === a); if (b) b.click(); return !!b; }, job.area);
        if (!okArea) note += `area "${job.area}" not found; `;
        await wait(800);
        if (job.tab) {
          const okTab = await page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, job.tab);
          if (!okTab) note += `tab "${job.tab}" not found; `;
        }
      }
      if (job.pressPublish) {
        const pressed = await page.evaluate(() => { const b = document.querySelector('.studio-chrome [data-testid="publish-button"]'); if (b) b.click(); return !!b; });
        if (!pressed) note += 'header Publish not found; ';
        await wait(1500);
      }
      await wait(job.settle || 2500);
      const probe = await probeHeader(page);
      probe.pageState = await page.evaluate(() => ({
        chooseDestination: document.body.innerText.includes('No publish destination yet'),
        chooseButtons: [...document.querySelectorAll('main button')].filter((b) => (b.textContent || '').trim() === 'Choose destination' && b.getClientRects().length).length,
        noList: !document.querySelector('[data-testid="publish-list"]') && !document.querySelector('[data-testid="publish-go"]'),
        goLabel: (document.querySelector('[data-testid="publish-go"]')?.textContent || '').trim() || null,
        modes: ['Changes', 'Earlier versions', 'Promote', 'Advanced'].filter((m) => [...document.querySelectorAll('main button')].some((b) => (b.textContent || '').trim() === m && b.getClientRects().length)),
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
// A case that could not do what it set out to do, or landed somewhere other than it expected, fails the run.
const caseFails = [];
for (const r of results) {
  const where = `${r.slug} ${r.vp}`;
  if (r.note) caseFails.push(`${where}: ${r.note.trim()}`);
  const want = CASES.find((c) => c.slug === r.slug).expect;
  const st = r.probe && r.probe.pageState;
  if (!want) continue;
  if (!st) { caseFails.push(`${where}: no page state was read`); continue; }
  const onPage = st.modes.includes('Changes');
  if (want.onPublished !== undefined && onPage !== want.onPublished)
    caseFails.push(`${where}: ${want.onPublished ? 'not on' : 'unexpectedly on'} the Publish page (its tabs on screen: ${JSON.stringify(st.modes)})`);
  if (want.advancedGone && st.modes.includes('Advanced')) caseFails.push(`${where}: an Advanced tab is still on the page`);
  if (want.headerPublish !== undefined && r.probe.publishInHeader !== want.headerPublish)
    caseFails.push(`${where}: the header Publish button reads ${JSON.stringify(r.probe.publishInHeader)}, expected ${JSON.stringify(want.headerPublish)}`);
  if (want.goLabel && !(typeof st.goLabel === 'string' && st.goLabel.includes(want.goLabel.has)))
    caseFails.push(`${where}: the page's one button reads ${JSON.stringify(st.goLabel)}, expected it to contain ${JSON.stringify(want.goLabel.has)}`);
  if (want.chooseDestination && !(st.chooseDestination && st.chooseButtons === 1))
    caseFails.push(`${where}: the page should offer exactly one Choose destination button (found ${st.chooseButtons})`);
  if (want.noList && !st.noList) caseFails.push(`${where}: a list or a Publish button is showing with no destination`);
}
console.log(caseFails.length ? `FAIL the published cases did not land where they should:\n  ${caseFails.join('\n  ')}` : 'ok every published case reached Published and showed what it should');

// The assistant's feed mark arriving must never refold the header (coordinator, m11 header-stable). The mark on
// "Your assistant" appears the first time the assistant runs something; drawn by being ADDED, it widened the chip by
// about 10px and a header on the edge dropped a fold level in front of the person. Swept every 4px from 1300 to 720
// at 100%, and at 1000px under 80% Studio zoom (a 1250px header, where the shell journey caught it): the chip keeps
// its width and the header keeps its fold when the mark arrives.
const markJumps = [];
{
  const page = await browser.newPage();
  await page.setViewport({ width: 1300, height: 800, deviceScaleFactor: 1 });
  // The harness fires activity of its own on timers, which may or may not land. Hold every activity message until
  // the "before" readings are taken, so the scene starts with no mark on every run.
  await page.evaluateOnNewDocument(() => {
    window.addEventListener('message', (e) => { if (e.data && e.data.type === 'activity' && !window.__allowActivity) e.stopImmediatePropagation(); }, true);
  });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?live=1&conn=1&tier=pro`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.studio-chrome', { timeout: 15000 });
  await wait(1500);
  // Whether the mark is PAINTED is read off real pixels, not off computed style: a badge with a transparent fill, or a
  // ::before that never draws, has every computed property of a visible one (verifier round 2, mutants M3 and M4). The
  // mark's own box is shot at 4x and the pixels in its disc that are the text colour are counted.
  const decoder = await browser.newPage();
  const paintOf = async (m) => {
    if (!m.rect) return { n: 0, tot: 0 };
    const [l, t, r, b] = m.rect;
    const clip = { x: l - 1, y: t - 1, width: r - l + 2, height: b - t + 2, scale: 4 };
    // The pixels are decoded in a second page, which can take the tab focus; a capture of a page that is not in front waits for a frame
    // that may never come (the 112th capture hung on the join), so this page is brought back to the front before every shot.
    await page.bringToFront();
    const b64 = await page.screenshot({ clip, encoding: 'base64' });
    const px = await decoder.evaluate(async (data) => {
      const img = new Image(); img.src = 'data:image/png;base64,' + data; await img.decode();
      const c = document.createElement('canvas'); c.width = img.width; c.height = img.height;
      const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
      return { w: img.width, h: img.height, d: Array.from(ctx.getImageData(0, 0, img.width, img.height).data) };
    }, b64);
    const cx = (l + r) / 2, cy = (t + b) / 2, rad = (r - l) / 2 - 0.35;
    const sx = clip.width / px.w, sy = clip.height / px.h;
    let n = 0, tot = 0;
    for (let y = 0; y < px.h; y++) for (let x = 0; x < px.w; x++) {
      const X = clip.x + (x + 0.5) * sx, Y = clip.y + (y + 0.5) * sy;
      if ((X - cx) ** 2 + (Y - cy) ** 2 > rad * rad) continue;
      tot++;
      const i = (y * px.w + x) * 4;
      if (Math.hypot(px.d[i] - m.fg[0], px.d[i + 1] - m.fg[1], px.d[i + 2] - m.fg[2]) < 40) n++;
    }
    return { n, tot };
  };
  const chipOf = async () => { const m = await page.evaluate(() => {
    const h = document.querySelector('.studio-chrome');
    const chip = [...h.querySelectorAll('button[aria-haspopup="menu"]')].find((b) => (b.textContent || '').includes('Your assistant'));
    // The chip as it would be with no mark in it at all: the mark must take zero layout width (verifier, 0fd813af:
    // reserving its width folded the header 13px early while idle). And whether the mark is actually PAINTED.
    const markEl = chip && chip.querySelector('.studio-feed-mark');
    let bare = null;
    if (markEl) { const was = markEl.style.display; markEl.style.display = 'none'; bare = Math.round(chip.getBoundingClientRect().width * 100) / 100; markEl.style.display = was; }
    const mr = markEl ? markEl.getBoundingClientRect() : null;
    const probe = document.createElement('span'); probe.style.color = 'var(--sem-fg)'; h.appendChild(probe);
    const fg = getComputedStyle(probe).color; probe.remove();
    return { bare, rect: mr ? [mr.left, mr.top, mr.right, mr.bottom] : null,
      fg: fg.slice(fg.indexOf('(') + 1, fg.indexOf(')')).split(/[ ,/]+/).slice(0, 3).map(Number), fold: h.dataset.fold + (h.dataset.brandFolded ? 'b' : ''), chip: chip ? Math.round(chip.getBoundingClientRect().width * 100) / 100 : null,
      mark: !!chip && !!chip.querySelector('.studio-feed-mark[data-live]') };
  });
  const paint = await paintOf(m);
  // Painted: most of the disc is the text colour. Unpainted: next to none of it is.
  return { ...m, paint, painted: paint.tot > 0 && paint.n >= paint.tot * 0.6, blank: paint.n <= paint.tot * 0.05 };
  };
  const widths = []; for (let w = 1300; w >= 720; w -= 4) widths.push(w);
  const before = {};
  for (const w of widths) { await page.setViewport({ width: w, height: 800, deviceScaleFactor: 1 }); await wait(120); before[w] = await chipOf(); }
  const zoomed = async () => {
    await page.setViewport({ width: 1000, height: 800, deviceScaleFactor: 1 });
    await page.evaluate(() => { const el = document.querySelector('.studio-chrome'); for (let i = 0; i < 2; i++) el.dispatchEvent(new WheelEvent('wheel', { deltaY: 1, ctrlKey: true, bubbles: true, cancelable: true })); });
    await wait(700);
    const r = await chipOf();
    await page.keyboard.down('Control'); await page.keyboard.press('0'); await page.keyboard.up('Control');
    await wait(400);
    return r;
  };
  for (const w of widths) {
    if (before[w].chip !== before[w].bare) markJumps.push(`${w}px idle: the mark takes ${(before[w].chip - before[w].bare).toFixed(2)}px of the chip`);
    if (!before[w].blank) markJumps.push(`${w}px idle: the mark is painted with no activity (${before[w].paint.n} of ${before[w].paint.tot} disc pixels)`);
  }
  const beforeZoomed = await zoomed();
  if (beforeZoomed.mark || Object.values(before).some((r) => r.mark)) markJumps.push('the mark was already drawn before any activity; the scene proves nothing');
  await page.evaluate(() => { window.__allowActivity = true; });
  await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'activity', payload: { seq: 1, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true } }));
  await wait(500);
  for (const w of widths) {
    await page.setViewport({ width: w, height: 800, deviceScaleFactor: 1 }); await wait(120);
    const after = await chipOf();
    if (!after.mark) { markJumps.push(`${w}px: the mark did not appear`); break; }
    if (!after.painted) markJumps.push(`${w}px live: the mark is not painted (${after.paint.n} of ${after.paint.tot} disc pixels are the text colour)`);
    if (after.chip !== after.bare) markJumps.push(`${w}px live: the mark takes ${(after.chip - after.bare).toFixed(2)}px of the chip`);
    if (after.chip !== before[w].chip) markJumps.push(`${w}px: the chip went from ${before[w].chip} to ${after.chip}px`);
    if (after.fold !== before[w].fold) markJumps.push(`${w}px: the fold went from ${before[w].fold} to ${after.fold}`);
  }
  const afterZoomed = await zoomed();
  if (afterZoomed.fold !== beforeZoomed.fold || afterZoomed.chip !== beforeZoomed.chip)
    markJumps.push(`1000px at 80%: fold ${beforeZoomed.fold} -> ${afterZoomed.fold}, chip ${beforeZoomed.chip} -> ${afterZoomed.chip}px`);

  // The Live activity list reads each entry by its words, not "bullet Ran DAX query": the list's mark is decoration
  // (verifier round 2, activity.tsx). Read from Chromium's accessibility tree, which is what a screen reader consumes.
  await page.setViewport({ width: 1300, height: 800, deviceScaleFactor: 1 }); await wait(300);
  await page.evaluate(() => {
    const h = document.querySelector('.studio-chrome');
    [...h.querySelectorAll('button[aria-haspopup="menu"]')].find((b) => (b.textContent || '').includes('Your assistant')).click();
  });
  await page.waitForSelector('[role="menu"] [role="menuitem"]', { timeout: 10000 });
  await page.evaluate(() => [...document.querySelectorAll('[role="menuitem"]')].find((b) => (b.textContent || '').includes('Live activity')).click());
  await page.waitForFunction(() => [...document.querySelectorAll('.studio-header-pop')].some((el) => (el.textContent || '').includes('Ran DAX query')), { timeout: 10000 });
  const cdp = await page.createCDPSession();
  await cdp.send('Accessibility.enable');
  const { nodes } = await cdp.send('Accessibility.getFullAXTree');
  const named = nodes.filter((n) => !n.ignored && n.name && typeof n.name.value === 'string');
  const entries = named.filter((n) => n.name.value.includes('Ran DAX query'));
  const bullets = named.filter((n) => n.name.value.includes('\u2022')).map((n) => `${n.role && n.role.value} "${n.name.value}"`);
  if (!entries.length) markJumps.push('the open Live activity list has no entry named for its run, so the bullet check read nothing');
  for (const b of bullets) markJumps.push(`the open Live activity list reads a bullet to a screen reader: ${b}`);
  await cdp.detach();
  await page.close(); await decoder.close();
}
console.log(markJumps.length ? `FAIL the feed mark moves the header, or is read or painted wrong:\n  ${markJumps.slice(0, 12).join('\n  ')}` : 'ok the feed mark arriving changes no chip width and no fold level');

// A resize that moves the header by less than a whole pixel still re-measures the fold (verifier round 2). The
// ResizeObserver used to compare the header's ROUNDED clientWidth, so a step that left it on the same whole pixel (a
// real Free lane at 130%, 1051 to 1050: 808 both times) kept a fold that no longer fit, 2b where only 3b fits.
// Swept at 130%, Free and Pro, every 1px of window and, inside each, a quarter pixel at a time (a fractional padding on
// the page, so the header shrinks by less than a pixel with no mutation inside it). Each step's fold is compared with a
// forced fresh measure of the same header. Whole-pixel window steps alone did not land a threshold inside a skipped
// step in this harness's fonts; the quarter steps do.
const staleFolds = [];
for (const tier of ['free', 'pro']) {
  const page = await browser.newPage();
  await page.setViewport({ width: 1180, height: 800, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?live=1&conn=1${tier === 'pro' ? '&tier=pro' : ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.studio-chrome', { timeout: 15000 });
  await wait(1500);
  await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'studioZoom', level: 130 }));
  await page.waitForFunction(() => getComputedStyle(document.querySelector('.studio-root')).zoom === '1.3', { timeout: 10000 });
  await wait(700);
  const frames = () => page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 30)))));
  const read = () => page.evaluate(() => { const h = document.querySelector('.studio-chrome'); return h.dataset.fold + (h.dataset.brandFolded ? 'b' : ''); });
  for (let w = 1180; w >= 980; w--) for (const pad of [0, 0.25, 0.5, 0.75]) {
    if (pad === 0) await page.setViewport({ width: w, height: 800, deviceScaleFactor: 1 });
    await page.evaluate((px) => { document.documentElement.style.paddingRight = px ? `${px}px` : ''; }, pad);
    await frames();
    const shown = await read();
    // A style write inside the header is a mutation the fold listens to, so it measures afresh at this exact width.
    await page.evaluate(() => { const c = document.querySelector('.studio-chrome').firstElementChild; c.style.setProperty('--fold-probe', String(Math.random())); });
    await frames();
    const fresh = await read();
    if (shown !== fresh) staleFolds.push(`${tier} ${w - pad}px at 130%: the header shows fold ${shown}, a fresh measure gives ${fresh}`);
  }
  await page.close();
}
console.log(staleFolds.length ? `FAIL a sub-pixel resize kept a stale fold:\n  ${staleFolds.slice(0, 12).join('\n  ')}` : 'ok every quarter-pixel resize at 130% re-measures the fold');

await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
for (const r of results) {
  const p = r.probe || {};
  console.log(`${r.slug} ${r.vp} fold=${p.fold} find=${p.findATool} conn=${p.connections} gear=${p.gear} more=${p.more} areas=${(p.areas || []).length} chip=${p.runChip ? p.runChip.width + 'px' : 'none'} overflow=${(p.overflowing || []).length} pagePublish=${p.pagePublishButtons} headerPublish=${JSON.stringify(p.publishInHeader)} errors=${r.errors.length} ${r.note}`);
}
if (markJumps.length || caseFails.length || staleFolds.length) process.exit(1);
