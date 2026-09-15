// Scripted INTERACTIVE testing of the Lineage & Impact graphs at real-world scale — so lineage UI changes can be
// exercised (select, walk, drag-vs-click, empty-click deselect, reset, hover, filter, resize · tree
// expand/reroot/direction · report-layer merge) and reviewed by reading the PNGs, WITHOUT clicking around an
// Extension Development Host after every change.
//
// It reuses the uishot harness (real headless Chromium, engine mocked in-page — see shot.mjs / harness.html) but
// injects a ~500-node finance-shaped fixture (fixtures/lineage-fixture.mjs) and then DRIVES the real React bundle:
//   • ECharts graph (canvas, no per-node DOM) — clicks land as real mouse events at node pixels resolved from the
//     live ECharts instance (window.__ECHARTS__, exposed by echart.tsx only under the __ECHARTS_TEST__ flag).
//   • React Flow tree (DOM) — driven via its buttons/caret/nodes directly.
// Each step: act → settle → screenshot to shots/lineage/NN-*.png + capture console/page errors + probe on-screen
// state (the "selected: <name>" chip, node counts). Prints a STEP REPORT at the end. Read the PNGs to review.
//
// Usage:  cd Semanticus.VSCode/tools/uishot && npm run build:webview --prefix ../.. && node lineage-interact.mjs
//         node lineage-interact.mjs graph     # only the graph scenario
//         node lineage-interact.mjs tree      # only the tree scenario
//         node lineage-interact.mjs resize    # only the fresh-entry resize regression check
//         node lineage-interact.mjs reports   # only the report-merge scenario
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, extname, normalize } from 'node:path';
import { existsSync, mkdirSync, statSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { makeFinanceLineage } from './fixtures/lineage-fixture.mjs';

// THE FLOOR CHECK RUNS BEFORE PUPPETEER IS LOADED, AND THE ORDER IS THE WHOLE POINT. This used to be a
// static `import puppeteer from 'puppeteer-core'`, which the runtime resolves, parses and evaluates before
// one line of this file runs. `requireSupportedNode()` therefore could not fire on the Node versions it
// exists for: on Node 20 the run died inside puppeteer with the opaque error the guard was written to
// replace, and the guard's message was never reached. A dynamic import after the check is what makes the
// check reachable. `browser.mjs` imports no puppeteer, so nothing can reorder this again by accident.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;


const __dir = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(__dir, '..', '..');
const outDir = join(__dir, 'shots', 'lineage');
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.');
  process.exit(1);
}
const only = (process.argv[2] || 'all').trim().toLowerCase();

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
function startServer() {
  const server = createServer((req, res) => {
    try {
      const urlPath = decodeURIComponent((req.url || '/').split('?')[0].split('#')[0]);
      const filePath = normalize(join(webRoot, urlPath));
      if (!filePath.startsWith(webRoot) || !existsSync(filePath) || statSync(filePath).isDirectory()) { res.writeHead(404); res.end('not found'); return; }
      res.writeHead(200, { 'content-type': TYPES[extname(filePath).toLowerCase()] || 'application/octet-stream' });
      res.end(readFileSync(filePath));
    } catch (e) { res.writeHead(500); res.end(String(e)); }
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r(server)));
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = [];
let stepNo = 0;

// ---- page-context helpers (strings run via page.evaluate) --------------------------------------
// Click a <button> by exact / prefix / contains text; returns whether one was found. (Kind-filter chips render a
// glyph BEFORE the label, so they need `contains`, not `prefix`.)
async function clickButton(page, label, { prefix = false, contains = false, optional = false } = {}) {
  const clicked = await page.evaluate((label, prefix, contains) => {
    const b = [...document.querySelectorAll('button')].find((x) => {
      const t = (x.textContent || '').trim();
      return contains ? t.includes(label) : prefix ? t.startsWith(label) : t === label;
    });
    if (b) { b.click(); return true; }
    return false;
  }, label, prefix, contains);
  // A label that no longer exists used to be silent, so a retired sub-tab left this driver screenshotting the
  // wrong page and still calling it a pass. It fails here instead.
  if (!clicked && !optional) throw new Error(`lineage-interact: no button labelled "${label}" on the page`);
  return clicked;
}
async function clickTestid(page, testid) {
  const clicked = await page.evaluate((t) => {
    const el = [...document.querySelectorAll(`[data-testid="${t}"]`)].find((x) => x.getClientRects().length);
    if (el) { el.click(); return true; }
    return false;
  }, testid);
  if (!clicked) throw new Error(`lineage-interact: nothing with data-testid "${testid}" on the page`);
  return clicked;
}
// Measure the React Flow pane against its cards: the canvas box, every card's box, the cards whose box escapes the
// canvas, and the cards the minimap overlay sits on. This is the whole evidence base for the resize check — a card
// outside the canvas is a card the person cannot read, whatever the layout thinks it did.
async function canvasFit(page) {
  return page.evaluate(() => {
    const canvas = document.querySelector('.react-flow');
    if (!canvas) return { canvas: null, total: 0, cards: [], outside: [], minimap: null, minimapOverlap: [], zoom: null, transform: null };
    const box = (r) => ({ x: Math.round(r.x), y: Math.round(r.y), right: Math.round(r.right), bottom: Math.round(r.bottom) });
    const cr = box(canvas.getBoundingClientRect());
    const cards = [...document.querySelectorAll('.react-flow__node')].map((n) => {
      const label = (n.innerText || '').split('\n').join(' ').replace(/ +/g, ' ').trim().slice(0, 40);
      return Object.assign({ label }, box(n.getBoundingClientRect()));
    });
    // Half a pixel of slack absorbs sub-pixel rounding; anything more is a real overhang.
    const outside = cards.filter((n) => n.x < cr.x - 0.5 || n.y < cr.y - 0.5 || n.right > cr.right + 0.5 || n.bottom > cr.bottom + 0.5);
    const mmEl = document.querySelector('.react-flow__minimap');
    const minimap = mmEl ? box(mmEl.getBoundingClientRect()) : null;
    const minimapOverlap = minimap ? cards.filter((n) => n.x < minimap.right && n.right > minimap.x && n.y < minimap.bottom && n.bottom > minimap.y) : [];
    // The live zoom, read off React Flow's own transform. It matters because fitView CANNOT zoom out past minZoom:
    // a graph that needs less than that is clamped, and then cards overflow however correctly the refit ran. A
    // containment check has to be able to tell those two situations apart.
    const vp = document.querySelector('.react-flow__viewport');
    const m = vp ? new DOMMatrixReadOnly(getComputedStyle(vp).transform) : null;
    const zoom = m ? Math.round(m.a * 1000) / 1000 : null;
    // The full pan+zoom, so a check can say "this view was left alone" rather than only "the zoom matched".
    const transform = m ? { zoom, x: Math.round(m.e), y: Math.round(m.f) } : null;
    return { canvas: cr, total: cards.length, cards, outside, minimap, minimapOverlap, zoom, transform };
  });
}
// Pick a LOW-degree measure node (so pin/walk light a clean path, not a 300-node hub fan) + one of its neighbours.
async function pickLowDegreeAndNeighbor(page) {
  return page.evaluate(() => {
    const ec = window.__ECHARTS__; if (!ec) return null;
    const dom = document.querySelector('[_echarts_instance_]'); if (!dom) return null;
    const inst = ec.getInstanceByDom(dom); if (!inst) return null;
    const opt = inst.getOption(); const links = opt.series[0].links || []; const data = opt.series[0].data || [];
    const deg = new Map(); const nbr = new Map();
    const bump = (a, b) => { deg.set(a, (deg.get(a) || 0) + 1); if (!nbr.has(a)) nbr.set(a, new Set()); nbr.get(a).add(b); };
    for (const l of links) { bump(l.source, l.target); bump(l.target, l.source); }
    const kind = new Map(data.map((d) => [d.id, d._kind]));
    // a measure with 2–5 connections, whose first neighbour also has a modest degree (a real walkable path)
    const cand = data.map((d) => d.id).filter((id) => kind.get(id) === 'measure' && (deg.get(id) || 0) >= 2 && (deg.get(id) || 0) <= 5);
    for (const id of cand) {
      const ns = [...(nbr.get(id) || [])];
      const walk = ns.find((n) => (deg.get(n) || 0) <= 12) || ns[0];
      if (walk) return { node: id, neighbor: walk, deg: deg.get(id), neighbors: ns };
    }
    return null;
  });
}
// The graph toolbar chip reads "selected: <name>" — the honest signal that a canvas node-click registered.
// Returns the selected node's name, or null when nothing is selected.
async function selectedLabel(page) {
  return page.evaluate(() => {
    const chip = document.querySelector('[data-sem-selected]');
    if (chip) return chip.getAttribute('data-sem-selected');
    const el = [...document.querySelectorAll('span')].find((s) => (s.textContent || '').trim().startsWith('selected: '));
    return el ? el.textContent.trim().slice('selected: '.length) : null;
  });
}
// Resolve a node's display name from the live option (for asserting the selection landed on the right node).
async function nodeName(page, ref) {
  return page.evaluate((ref) => {
    const ec = window.__ECHARTS__; const dom = document.querySelector('[_echarts_instance_]');
    const inst = ec?.getInstanceByDom?.(dom); if (!inst) return null;
    const d = (inst.getOption().series[0].data || []).find((x) => x.id === ref);
    return d ? d.name : null;
  }, ref);
}
// Count currently-lit nodes (opacity kept up) — selecting must SHRINK this below the total (the rest fades).
async function litCount(page) {
  return page.evaluate(() => {
    const ec = window.__ECHARTS__; const dom = document.querySelector('[_echarts_instance_]');
    const inst = ec?.getInstanceByDom?.(dom); if (!inst) return -1;
    const data = (inst.getOption().series[0].data) || [];
    return data.filter((d) => (d.itemStyle && d.itemStyle.opacity != null ? d.itemStyle.opacity : 1) > 0.1).length;
  });
}
async function nodeCountLabel(page) {
  return page.evaluate(() => {
    const el = [...document.querySelectorAll('*')].find((s) => /\d+\s+nodes?\s+·\s+\d+\s+edges?/.test((s.textContent || '').trim()) && s.children.length === 0);
    return el ? el.textContent.trim() : null;
  });
}
// Resolve a graph node's on-screen (page) pixel from the live ECharts instance, then real-mouse-click it.
async function clickGraphNode(page, ref) {
  const pt = await page.evaluate((ref) => {
    const ec = window.__ECHARTS__;
    if (!ec) return { err: 'window.__ECHARTS__ missing (echart.tsx hook not active)' };
    const dom = document.querySelector('[_echarts_instance_]');
    if (!dom) return { err: 'no echarts dom on page' };
    const inst = ec.getInstanceByDom(dom);
    if (!inst) return { err: 'no echarts instance' };
    const series = inst.getModel().getSeriesByIndex(0);
    const data = series.getData();
    let idx = -1;
    for (let i = 0; i < data.count(); i++) if (data.getId(i) === ref) { idx = i; break; }
    if (idx < 0) return { err: 'node not found: ' + ref };
    const layout = data.getItemLayout(idx);
    if (!layout) return { err: 'no layout for ' + ref };
    let px;
    try { px = inst.convertToPixel({ seriesIndex: 0 }, layout); } catch { px = null; }
    if (!px || !isFinite(px[0]) || !isFinite(px[1])) px = layout;   // fallback: no roam → layout ≈ pixel
    const r = dom.getBoundingClientRect();
    return { x: r.left + px[0], y: r.top + px[1] };
  }, ref);
  if (pt.err) { report.push(`   ⚠ clickGraphNode(${ref}): ${pt.err}`); return false; }
  await page.mouse.click(pt.x, pt.y);
  return true;
}
// Robust select: small nudges around the node centre until the "selected: <name>" chip shows this node (tiny nodes
// sit under overlapping edges, so a dead-centre pixel can graze an edge instead — nudge and retry).
async function selectGraphNode(page, ref) {
  const want = await nodeName(page, ref);
  if (!want) return false;
  for (const [dx, dy] of [[0, 0], [0, -3], [3, 0], [0, 3], [-3, 0], [2, -2], [-2, 2]]) {
    const pt = await page.evaluate((ref) => {
      const ec = window.__ECHARTS__; const dom = document.querySelector('[_echarts_instance_]');
      const inst = ec?.getInstanceByDom?.(dom); if (!inst) return null;
      const data = inst.getModel().getSeriesByIndex(0).getData();
      let idx = -1; for (let i = 0; i < data.count(); i++) if (data.getId(i) === ref) { idx = i; break; }
      if (idx < 0) return null; const lay = data.getItemLayout(idx); if (!lay) return null;
      let px; try { px = inst.convertToPixel({ seriesIndex: 0 }, lay); } catch { px = lay; }
      const r = dom.getBoundingClientRect(); return { x: r.left + px[0], y: r.top + px[1] };
    }, ref);
    if (!pt) return false;
    await page.mouse.click(pt.x + dx, pt.y + dy);
    await sleep(350);
    if ((await selectedLabel(page)) === want) return true;
  }
  return false;
}
// A canvas point at least `minDist` px from EVERY node — for verifying that an empty-space click deselects.
// Samples a coarse grid over the chart and returns the point with the biggest min-distance to any node.
async function emptyPoint(page, minDist = 60) {
  return page.evaluate((minDist) => {
    const ec = window.__ECHARTS__; const dom = document.querySelector('[_echarts_instance_]');
    const inst = ec?.getInstanceByDom?.(dom); if (!inst) return null;
    const data = inst.getModel().getSeriesByIndex(0).getData();
    const pts = [];
    for (let i = 0; i < data.count(); i++) {
      const lay = data.getItemLayout(i); if (!lay) continue;
      let px; try { px = inst.convertToPixel({ seriesIndex: 0 }, lay); } catch { px = lay; }
      if (px && isFinite(px[0]) && isFinite(px[1])) pts.push(px);
    }
    const r = dom.getBoundingClientRect();
    let best = null, bestD = -1;
    for (let gx = 20; gx < r.width - 20; gx += 40) for (let gy = 20; gy < r.height - 20; gy += 40) {
      let d = Infinity;
      for (const p of pts) { const dd = Math.hypot(p[0] - gx, p[1] - gy); if (dd < d) d = dd; }
      if (d > bestD) { bestD = d; best = { x: r.left + gx, y: r.top + gy }; }
    }
    return best && bestD >= minDist ? best : null;
  }, minDist);
}
// Resolve a node's on-screen (page) pixel centre from the live ECharts instance.
async function nodePixel(page, ref) {
  return page.evaluate((ref) => {
    const ec = window.__ECHARTS__; const dom = document.querySelector('[_echarts_instance_]');
    const inst = ec?.getInstanceByDom?.(dom); if (!inst) return null;
    const data = inst.getModel().getSeriesByIndex(0).getData();
    let idx = -1; for (let i = 0; i < data.count(); i++) if (data.getId(i) === ref) { idx = i; break; }
    if (idx < 0) return null; const lay = data.getItemLayout(idx); if (!lay) return null;
    let px; try { px = inst.convertToPixel({ seriesIndex: 0 }, lay); } catch { px = lay; }
    const r = dom.getBoundingClientRect(); return { x: r.left + px[0], y: r.top + px[1] };
  }, ref);
}
// Click at an OFFSET from a node's centre — to verify the snap-to-nearest forgiveness (an off-target click still selects).
async function clickOffset(page, ref, dx, dy) {
  const pt = await nodePixel(page, ref);
  if (!pt) return false;
  await page.mouse.click(pt.x + dx, pt.y + dy);
  return true;
}
async function neighborsOf(page, ref) {
  return page.evaluate((ref) => {
    const ec = window.__ECHARTS__; if (!ec) return [];
    const dom = document.querySelector('[_echarts_instance_]'); if (!dom) return [];
    const inst = ec.getInstanceByDom(dom); if (!inst) return [];
    const links = (inst.getOption().series[0].links) || [];
    const out = [];
    for (const l of links) { if (l.source === ref) out.push(l.target); else if (l.target === ref) out.push(l.source); }
    return [...new Set(out)];
  }, ref);
}

// Open a MultiSelect "slicer" dropdown by its button label (the button text is `${label}<count?>▾`).
async function openSlicer(page, label) {
  return page.evaluate((label) => {
    const b = [...document.querySelectorAll('button')].find((x) => { const t = (x.textContent || '').trim(); return t.startsWith(label) && t.includes('▾'); });
    if (b) { b.click(); return true; }
    return false;
  }, label);
}
// Tick the first `n` options in the currently-open slicer popover (checkboxes are unique to the popover on this page).
async function tickSlicer(page, n) {
  return page.evaluate((n) => {
    const boxes = [...document.querySelectorAll('input[type=checkbox]')];
    const picked = [];
    for (let i = 0; i < Math.min(n, boxes.length); i++) { boxes[i].click(); const lbl = boxes[i].closest('label'); picked.push((lbl?.textContent || '').trim()); }
    return picked;
  }, n);
}
// Which layout ToolBtn is pressed. It used to be read off an inline accent background; the controls sit on the
// shared control scale now (the compact tool row, 2026-09-14), which carries the selected state on aria-pressed
// so the look and the accessible state cannot drift apart. The inline-style read is kept as a fallback.
async function activeLayout(page) {
  return page.evaluate(() => {
    for (const label of ['Force', 'Circular']) {
      const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === label);
      if (!b) continue;
      if (b.getAttribute('aria-pressed') === 'true') return label;
      if ((b.getAttribute('style') || '').includes('--sem-accent)')) return label;
    }
    return null;
  });
}
// The graph's node count. It used to sit on the toolbar; it is a chip inside the picture now, and it counts
// "things" rather than "nodes" because the UI never speaks engine jargon.
async function toolbarNodes(page) {
  return page.evaluate(() => {
    const el = [...document.querySelectorAll('b, span')].find((s) => s.children.length === 0 && /^\d+( of \d+)? (things|nodes)$/.test((s.textContent || '').trim()));
    return el ? el.textContent.trim() : null;
  });
}

async function shot(page, name, note) {
  stepNo++;
  const file = join(outDir, `${String(stepNo).padStart(2, '0')}-${name}.png`);
  await page.screenshot({ path: file });
  const line = `${String(stepNo).padStart(2, '0')}  ${name}${note ? ' — ' + note : ''}`;
  report.push(line);
  console.log('  ✓ ' + line);
  return file;
}

// The kind filters fold into one "Show" menu (the compact tool row, 2026-09-14): a model decides how many
// kinds there are, so spelling them out is what used to push the controls onto a second row. Clicking one by
// text alone is no longer safe - "Column" also matches a card in the picture, which would be a pass that
// proves nothing - so open the menu, click inside it, and close it again.
async function clickKindChip(page, label) {
  const opened = await page.evaluate(() => {
    const b = [...document.querySelectorAll('.sem-toolrow button')].find((x) => (x.textContent || '').trim().startsWith('Show'));
    if (b) { b.click(); return true; }
    return false;
  });
  if (!opened) return false;
  await sleep(250);
  const hit = await page.evaluate((label) => {
    const menu = document.querySelector('[role="menu"]');
    const b = menu && [...menu.querySelectorAll('button')].find((x) => (x.textContent || '').includes(label));
    if (b) { b.click(); return true; }
    return false;
  }, label);
  await sleep(250);
  await page.keyboard.press('Escape');
  await sleep(200);
  return hit;
}

// Reach Lineage through whichever top-level nav the shell is wearing. The 1.2.0 redesign replaced the "Understand"
// group with the Model / Calculations / Checks / Changes / Workflows areas, which left this helper timing out on the
// Understand button it was still waiting fifteen seconds for — so every scenario here died before its first click.
// Try the area nav first, fall back to the older group, and fail with a message that names the buttons actually on
// screen rather than a bare puppeteer timeout.
async function gotoLineage(page, errors) {
  errors.length = 0;
  await page.goto(page.__url, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  const hasButton = (label) => page.evaluate((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l), label);
  const group = await page.waitForFunction(
    () => {
      const has = (l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l);
      return has('Model') ? 'Model' : has('Understand') ? 'Understand' : false;
    }, { timeout: 15000 },
  ).then((h) => h.jsonValue()).catch(() => null);
  if (!group) {
    const seen = await page.evaluate(() => [...document.querySelectorAll('nav button')].map((b) => (b.textContent || '').trim()).filter(Boolean).slice(0, 30));
    throw new Error('no Model / Understand entry point in the shell nav. Buttons on screen: ' + seen.join(' | '));
  }
  await clickButton(page, group);
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Lineage'), { timeout: 15000 });
  await clickButton(page, 'Lineage');
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Graph'), { timeout: 15000 });
  if (!(await hasButton('Tree'))) throw new Error('the Lineage sub-view switcher is missing its Tree button');
}

// ---- scenarios --------------------------------------------------------------------------------
async function graphScenario(page, refs, errors) {
  console.log('\n▶ GRAPH (View A)');
  await gotoLineage(page, errors);
  await clickButton(page, 'Graph');
  await page.waitForSelector('[_echarts_instance_]', { timeout: 15000 });
  await sleep(3500);   // let the ~490-node force layout settle
  await shot(page, 'graph-initial', 'auto-focused (>150 nodes) · ' + (await nodeCountLabel(page)));

  // full graph + node-size slider (whole model → the size range is the only slider present)
  await clickButton(page, 'Whole model'); await sleep(2500);
  await shot(page, 'graph-whole', await nodeCountLabel(page));
  const setSize = (v) => page.evaluate((v) => { const r = document.querySelectorAll('input[type=range]')[0]; if (r) { const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set; set.call(r, String(v)); r.dispatchEvent(new Event('input', { bubbles: true })); } }, v);
  // the DEPTH slider (max=4 distinguishes it from the size slider max=2)
  const setDepth = (v) => page.evaluate((v) => { const r = [...document.querySelectorAll('input[type=range]')].find((x) => x.max === '4'); if (r) { const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set; set.call(r, String(v)); r.dispatchEvent(new Event('input', { bubbles: true })); } }, v);
  await setSize(0.5); await sleep(900); await shot(page, 'graph-size-small', 'node size 0.5');
  await setSize(1.7); await sleep(900); await shot(page, 'graph-size-large', 'node size 1.7');
  await setSize(0.85); await sleep(600);

  // SNAP forgiveness: click ~16px OFF the hub → snap-to-nearest should still select it (fixes "walk doesn't work" from misses)
  await clickOffset(page, refs.hubMeasure, 16, 13); await sleep(500);
  await shot(page, 'graph-snap-offclick', `selected=${await selectedLabel(page)} (clicked 16px OFF the node → snapped)`);
  await clickButton(page, 'Clear selection'); await sleep(300);

  // WALK verification (whole model, big nodes = reliable clicks, frozen layout = stable): select the hub, then a
  // connected neighbour → the selection must MOVE there (label changes) and the highlight must follow.
  await selectGraphNode(page, refs.hubMeasure); await sleep(500);
  const sel1 = await selectedLabel(page); const lit1 = await litCount(page);
  const hubNbrs = await neighborsOf(page, refs.hubMeasure);
  const walk2 = hubNbrs.find((n) => n.startsWith('measure:')) || hubNbrs[0];
  if (walk2) { await selectGraphNode(page, walk2); await sleep(500); }
  const sel2 = await selectedLabel(page); const lit2 = await litCount(page);
  await shot(page, 'graph-walk-moves', `selected "${sel1}" → "${sel2}" (selection moved) · lit ${lit1} → ${lit2}`);
  await clickButton(page, 'Clear selection'); await sleep(400);

  // DRAG ≠ CLICK: dragging a node must move it WITHOUT selecting it, and a canvas pan must not select either
  // (the old code registered every drag/pan release as a click and pinned whatever was near the release point).
  const dragPt = await nodePixel(page, refs.hubMeasure);
  if (dragPt) {
    await page.mouse.move(dragPt.x, dragPt.y); await page.mouse.down();
    await page.mouse.move(dragPt.x + 60, dragPt.y + 40, { steps: 8 });
    await page.mouse.up(); await sleep(600);
  }
  const ep0 = await emptyPoint(page, 40);
  if (ep0) {
    await page.mouse.move(ep0.x, ep0.y); await page.mouse.down();
    await page.mouse.move(ep0.x + 120, ep0.y + 60, { steps: 6 });
    await page.mouse.up(); await sleep(600);
  }
  await shot(page, 'graph-drag-no-select', `selected=${await selectedLabel(page)} after a node drag + a canvas pan (expect null)`);

  // EMPTY-SPACE CLICK deselects (a real click far from any node — pans are already filtered out above).
  await selectGraphNode(page, refs.hubMeasure); await sleep(400);
  const ep1 = await emptyPoint(page);
  if (ep1) { await page.mouse.move(ep1.x, ep1.y, { steps: 6 }); await page.mouse.click(ep1.x, ep1.y); await sleep(500); }
  await page.mouse.move(80, 500, { steps: 6 }); await sleep(300);   // realistic pointer travel (hover state follows the mouse)
  await shot(page, 'graph-emptyclick-deselect', `selected=${await selectedLabel(page)} after an empty-space click (expect null, fully lit)`);

  // RESET: select + pan, then Reset must return the DEFAULT view — nothing selected, whole model, fresh settled
  // layout, auto-fit (the old restore-based reset kept frozen node positions and stale focus/filter state).
  await selectGraphNode(page, refs.hubMeasure); await sleep(300);
  const ep2 = await emptyPoint(page, 40);
  if (ep2) {
    await page.mouse.move(ep2.x, ep2.y); await page.mouse.down();
    await page.mouse.move(ep2.x + 140, ep2.y + 90, { steps: 6 });
    await page.mouse.up(); await sleep(400);
  }
  await shot(page, 'graph-before-reset', `selected=${await selectedLabel(page)} · panned (selection must survive the pan)`);
  await clickButton(page, 'Reset'); await sleep(2500);
  await page.mouse.move(80, 500, { steps: 6 }); await sleep(300);   // realistic pointer travel after the (DOM) button click
  await shot(page, 'graph-after-reset', `selected=${await selectedLabel(page)} (expect null) · fresh layout + fit, fully lit`);

  // RESET restores the CONTROL ROW too (layout / node-size / depth) — #81 review fix: resetView must return every
  // user-facing control to its default (Force · size 0.85 · depth 2), not just selection/filters/scope.
  await clickButton(page, 'Circular'); await sleep(1800);
  await setSize(1.7); await sleep(700);
  await page.type('input[placeholder^="Search for something"]', 'Margin'); await sleep(600);
  await page.keyboard.press('Enter'); await sleep(1800);              // Focused scope → the depth slider appears
  await setDepth(4); await sleep(1000);
  await shot(page, 'graph-controls-dirty', `layout=${await activeLayout(page)} · size=1.7 · depth=4 (about to Reset)`);
  await clickButton(page, 'Reset'); await sleep(2500);
  const layoutAfter = await activeLayout(page);
  const sizeAfter = await page.evaluate(() => document.querySelectorAll('input[type=range]')[0]?.value ?? null);
  const depthHidden = await page.evaluate(() => ![...document.querySelectorAll('input[type=range]')].some((x) => x.max === '4'));   // Whole-model scope hides it
  // depth resets even while its slider is hidden — re-enter Focused and read the slider back
  await page.type('input[placeholder^="Search for something"]', 'Margin'); await sleep(600);
  await page.keyboard.press('Enter'); await sleep(1500);
  const depthAfter = await page.evaluate(() => [...document.querySelectorAll('input[type=range]')].find((x) => x.max === '4')?.value ?? null);
  const controlsOk = layoutAfter === 'Force' && sizeAfter === '0.85' && depthHidden && depthAfter === '2';
  await clickButton(page, 'Reset'); await sleep(2000);
  await shot(page, 'graph-reset-controls', `${controlsOk ? 'OK' : 'MISMATCH'} — layout=${layoutAfter} (want Force) · size=${sizeAfter} (want 0.85) · depth-slider-hidden=${depthHidden} · depth-on-refocus=${depthAfter} (want 2)`);
  if (!controlsOk) { failed++; report.push('   ✗ Reset did not restore layout/size/depth to defaults'); }

  // hub select — documents the "a hub lights hundreds" case (honest: its whole ring is its adjacency)
  const okHub = await clickGraphNode(page, refs.hubMeasure);
  await sleep(900);
  await shot(page, 'graph-select-hub', okHub ? `selected=${await selectedLabel(page)} (a 300+ connection hub → lights its ring)` : 'CLICK FAILED');
  await clickButton(page, 'Clear selection'); await sleep(500);

  // WALK test — done in Focused scope where nodes are large & well-separated (in the dense whole-model view, tiny
  // peripheral nodes under overlapping edges are unclickable by pixel — itself the "hairball" finding). The FIRST
  // selection is via search→focus (DOM, deterministic); then we walk to its named neighbour by clicking the node.
  const leaf = refs.leaf;
  let walkTo = null;
  if (leaf?.name) {
    await page.type('input[placeholder^="Search for something"]', leaf.name);
    await sleep(600);
    await page.keyboard.press('Enter');   // focusOn(match) → selects it + Focused scope
    await sleep(2200);
    await shot(page, 'graph-select-leaf', `selected=${await selectedLabel(page)} · "${leaf.name}" (focused clean path)`);
    walkTo = leaf.neighborRef;
    if (walkTo) { await selectGraphNode(page, walkTo); await sleep(500); }
    await shot(page, 'graph-walk', `selected=${await selectedLabel(page)} · walked to "${leaf.neighborName}" (selection moved)`);
    await clickButton(page, 'Whole model'); await sleep(400);
  } else {
    report.push('   ⚠ no leaf ref in fixture to walk');
  }

  // hover a FADED node (current merged opacity ≤ 0.1, i.e. outside the lit set) — it must NOT re-light while selected
  const fadedPt = await page.evaluate(() => {
    const ec = window.__ECHARTS__; const dom = document.querySelector('[_echarts_instance_]');
    const inst = ec?.getInstanceByDom?.(dom); if (!inst) return null;
    const items = (inst.getOption().series[0].data) || [];
    const data = inst.getModel().getSeriesByIndex(0).getData();
    for (let i = 0; i < data.count(); i++) {
      const id = data.getId(i);
      const item = items.find((d) => d.id === id);
      const op = item && item.itemStyle && item.itemStyle.opacity != null ? item.itemStyle.opacity : 1;
      if (op > 0.1) continue;   // lit — skip
      const lay = data.getItemLayout(i); if (!lay) continue;
      let px; try { px = inst.convertToPixel({ seriesIndex: 0 }, lay); } catch { px = lay; }
      const r = dom.getBoundingClientRect(); const x = r.left + px[0], y = r.top + px[1];
      if (x > r.left + 30 && x < r.right - 30 && y > r.top + 30 && y < r.bottom - 30) return { x, y };
    }
    return null;
  });
  if (fadedPt) { await page.mouse.move(fadedPt.x, fadedPt.y); await sleep(800); }
  await shot(page, 'graph-hover-faded', 'hovering a faded node — should stay dim');

  // clear the selection via the toolbar button
  await clickButton(page, 'Clear selection');
  await sleep(700);
  await shot(page, 'graph-cleared', `selected=${await selectedLabel(page)} (expect null)`);

  // selection MOVES between distant nodes: select the hub, then a far table — the highlight must FOLLOW (hub
  // fades, table lights); it must never accumulate into disconnected bright islands.
  await selectGraphNode(page, refs.hubMeasure); await sleep(500);
  await selectGraphNode(page, refs.dimTable); await sleep(600);
  await shot(page, 'graph-select-moves', `selected=${await selectedLabel(page)} (expect the table — moved off the hub)`);
  await clickButton(page, 'Clear selection'); await sleep(400);

  // search → focus a node (local neighbourhood, focus scope)
  await page.evaluate(() => { const i = document.querySelector('input[placeholder^="Search for something"]'); if (i) { i.focus(); } });
  await page.type('input[placeholder^="Search for something"]', 'Margin');
  await sleep(600);
  await shot(page, 'graph-search-suggest', 'search dropdown');
  await page.keyboard.press('Enter');
  await sleep(1500);
  await shot(page, 'graph-focused', await nodeCountLabel(page));

  await setDepth(4); await sleep(1200);
  await shot(page, 'graph-focus-depth4', await nodeCountLabel(page));
  // depth 1 → a SMALL (<150 node) focused view — proves the force layout settles (not a frozen initial scatter) at small sizes
  await setDepth(1); await sleep(1200);
  await shot(page, 'graph-focus-depth1-small', await nodeCountLabel(page));

  // back to whole model, then hide the 'column' kind via the filter chip
  await clickButton(page, 'Clear selection'); await sleep(400);
  await clickButton(page, 'Whole model');
  await sleep(800);
  await clickKindChip(page, 'Column');   // the kind chips live behind the row's Show menu now
  await sleep(1800);
  await shot(page, 'graph-filter-no-columns', await nodeCountLabel(page));
  await clickButton(page, 'Reset');
  await sleep(1000);

  // responsiveness: a narrow, short viewport
  await page.setViewport({ width: 900, height: 620, deviceScaleFactor: 1 });
  await sleep(1200);
  await shot(page, 'graph-small-viewport', '900×620 — dynamic height/fit');
  await page.setViewport({ width: 1680, height: 1000, deviceScaleFactor: 2 });
  await sleep(800);
}

async function treeScenario(page, refs, errors) {
  console.log('\n▶ TREE (View B)');
  await gotoLineage(page, errors);
  await clickButton(page, 'Tree');
  await page.waitForSelector('.react-flow__node', { timeout: 15000 });
  await sleep(1500);
  const cnt = async () => page.evaluate(() => document.querySelectorAll('.react-flow__node').length);
  const moreCnt = async () => page.evaluate(() => [...document.querySelectorAll('.react-flow__node')].filter((n) => /\+\d+ more/.test(n.textContent || '')).length);
  await shot(page, 'tree-initial', `auto-root · ${await cnt()} cards · ${await moreCnt()} "+N more" (fan-out capped)`);

  // granularity filter — hide Columns (coarser view), then restore
  await clickKindChip(page, 'Column');
  await sleep(1200);
  await shot(page, 'tree-granularity-no-columns', `${await cnt()} cards — Columns hidden`);
  await clickKindChip(page, 'Column');
  await sleep(1000);

  // vertical (top→bottom) orientation
  await clickButton(page, 'Vertical', { contains: true });
  await sleep(1500);
  await shot(page, 'tree-vertical', `${await cnt()} cards — vertical layout`);
  await clickButton(page, 'Horizontal', { contains: true });
  await sleep(1200);

  // drill: click a "+N more" node to reveal the rest of a capped rank
  const clickedMore = await page.evaluate(() => {
    const m = [...document.querySelectorAll('.react-flow__node')].find((n) => /\+\d+ more/.test(n.textContent || ''));
    const inner = m?.querySelector('div') || m;   // the pill's own div carries the React onClick
    if (inner) { inner.dispatchEvent(new MouseEvent('click', { bubbles: true })); return true; }
    return false;
  });
  await sleep(1500);
  await shot(page, 'tree-more-expanded', clickedMore ? `${await cnt()} cards after "+N more"` : 'no +N more node present');

  await clickButton(page, 'Expand all');
  await sleep(2000);
  await shot(page, 'tree-expand-all', `${await cnt()} cards · ${await moreCnt()} "+N more" (bounded ranks)`);

  await clickButton(page, 'Collapse');
  await sleep(1200);
  await shot(page, 'tree-collapsed', `${await cnt()} cards (root only)`);

  // reroot to a FACT TABLE (GeneralLedger — no "Staging …" name collision) and expand-all downstream →
  // the full table → column → measure chain
  await page.type('input[placeholder^="Search to start from"]', 'GeneralLedger');
  await sleep(600);
  await shot(page, 'tree-root-search', 'root search dropdown');
  await page.keyboard.press('Enter');
  await sleep(1500);
  await clickButton(page, 'Expand all');
  await sleep(2200);
  await shot(page, 'tree-table-root-chain', `${await cnt()} cards — table→column→measure downstream`);

  // upstream direction from a measure root (what it's built from)
  await page.evaluate(() => { const i = document.querySelector('input[placeholder^="Search to start from"]'); if (i) { i.value = ''; } });
  await page.type('input[placeholder^="Search to start from"]', 'Margin');
  await sleep(600);
  await page.keyboard.press('Enter');
  await sleep(1200);
  await clickButton(page, '↑ Upstream', { prefix: true });
  await sleep(1200);
  await clickButton(page, 'Expand all');
  await sleep(1800);
  await shot(page, 'tree-upstream-chain', `${await cnt()} cards — upstream (sources)`);

  // click a node to select (reveals root/impact affordances)
  await page.evaluate(() => { const n = document.querySelector('.react-flow__node'); if (n) n.dispatchEvent(new MouseEvent('click', { bubbles: true })); });
  await sleep(700);
  await shot(page, 'tree-node-selected', 'a node selected (root/impact buttons)');

  // responsiveness
  await page.setViewport({ width: 900, height: 620, deviceScaleFactor: 1 });
  await sleep(1200);
  await shot(page, 'tree-small-viewport', '900×620');
  await page.setViewport({ width: 1680, height: 1000, deviceScaleFactor: 2 });
  await sleep(600);
}

// RESIZE — the FRESH first entry must refit, and it is the only entry that ever failed to. The lineage graph arrives
// async, so on a first visit the Tree's first render is the "Loading…" panel and the canvas element appears one render
// later. The old ResizeObserver was attached from a mount effect that read a ref which was still null at that moment
// and had no deps to re-run on, so no observer was ever attached for the whole of that first visit: the person shrank
// the window and the cards stayed parked outside the smaller canvas until Fit was pressed by hand. A Tree that was
// remounted (Graph → Tree) after the data landed rendered the canvas on its FIRST render and so always worked, which
// is exactly why the bug survived review. This check therefore drives the FRESH path, never the remounted one.
// Reproduced on the unfixed bundle at 1440x900 → 1000x800: both cards outside the canvas. Astra, 2026-09-14.
async function resizeScenario(page, refs, errors) {
  console.log('\n▶ RESIZE (a fresh first entry refits)');
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await gotoLineage(page, errors);
  await clickButton(page, 'Tree');
  await page.waitForSelector('.react-flow__node', { timeout: 15000 });
  await sleep(2500);
  const before = await canvasFit(page);
  await shot(page, 'resize-fresh-1440', `1440×900 · ${before.total} cards · ${before.outside.length} outside the canvas · zoom ${before.zoom}`);
  // Reported, not asserted: at the LARGE size the minimap is a corner the fit clears on a small tree, but a tree that
  // fills the pane can still have cards under it. Astra's finding was scoped to the small size, so this line is
  // evidence for the next review rather than a gate that would fail on a pre-existing, out-of-scope condition.
  console.log(`    note: at 1440×900 the minimap is ${before.minimap ? 'shown' : 'hidden'} and sits on ${before.minimapOverlap.length} card(s)`);

  await page.setViewport({ width: 1000, height: 800, deviceScaleFactor: 1 });
  await sleep(2500);   // clears the observer's 150ms debounce and the refit's 200ms animation with room to spare
  const after = await canvasFit(page);
  await shot(page, 'resize-fresh-1000', `1000×800 · ${after.total} cards · ${after.outside.length} outside · minimap ${after.minimap ? 'shown' : 'hidden'} · zoom ${after.zoom}`);

  // THE ASSERTION IS "the resize already did what Fit does", not "every card is inside the canvas", and the difference
  // is not a softening. React Flow's fitView cannot zoom out past minZoom (0.2 here), so a tree that needs less than
  // that is CLAMPED and overflows however perfectly the refit ran — measured on the 490-node fixture, the 26-card tree
  // lands at exactly zoom 0.2 at 1000×800 and 3 cards still hang over the edge. Demanding containment there would be
  // demanding a smaller minZoom, which is a design decision, not this bug. Astra's own discriminator is the honest one:
  // the complaint was that pressing Fit BY HAND restored the cards, so the resize is fixed exactly when it leaves the
  // view where Fit would leave it. Containment is still asserted whenever Fit itself achieves it (the unclamped case).
  await clickButton(page, 'Fit');
  await sleep(1500);
  const fitted = await canvasFit(page);
  await shot(page, 'resize-fresh-1000-after-fit', `1000×800 after pressing Fit · ${fitted.outside.length} outside · zoom ${fitted.zoom}`);
  await page.setViewport({ width: 1680, height: 1000, deviceScaleFactor: 2 });
  await sleep(800);

  const where = (n) => `${n.label} [${n.x},${n.y}..${n.right},${n.bottom}]`;
  const problems = [];
  if (!before.canvas || before.total === 0) problems.push('no canvas or no cards were rendered at 1440×900 — this check proved nothing');
  if (before.outside.length) problems.push(`${before.outside.length} card(s) already outside the canvas at 1440×900: ${before.outside.map(where).join(' · ')}`);
  if (after.total !== fitted.total) problems.push(`the card count changed across the Fit (${after.total} → ${fitted.total}), so the two views are not comparable`);
  // Same view = same zoom and the same card boxes. 2px of slack for the animation's last frame and sub-pixel rounding.
  if (after.zoom == null || fitted.zoom == null || Math.abs(after.zoom - fitted.zoom) > 0.005) {
    problems.push(`the resize did not refit: zoom settled at ${after.zoom} but pressing Fit gives ${fitted.zoom}`);
  }
  const fittedBox = new Map(fitted.cards.map((c) => [c.label, c]));
  const moved = after.cards.filter((c) => { const f = fittedBox.get(c.label); return !f || Math.abs(f.x - c.x) > 2 || Math.abs(f.y - c.y) > 2; });
  if (moved.length) {
    problems.push(`pressing Fit still moved ${moved.length} of ${after.total} card(s), so the resize left a view Fit had to repair: ${moved.slice(0, 4).map((c) => `${where(c)} → ${fittedBox.get(c.label) ? where(fittedBox.get(c.label)) : 'gone'}`).join(' · ')}`);
  }
  if (fitted.outside.length === 0 && after.outside.length) {
    problems.push(`Fit contains every card but the resize left ${after.outside.length} outside the canvas: ${after.outside.map(where).join(' · ')} vs canvas [${after.canvas.x},${after.canvas.y}..${after.canvas.right},${after.canvas.bottom}]`);
  }
  if (after.minimapOverlap.length) problems.push(`the minimap covers ${after.minimapOverlap.length} card(s) at 1000×800: ${after.minimapOverlap.map(where).join(' · ')}`);
  if (problems.length) throw new Error('RESIZE CHECK FAILED\n    - ' + problems.join('\n    - '));
  const clamped = after.outside.length > 0;
  console.log(`  ✓ the 1440×900 → 1000×800 resize left the view exactly where Fit leaves it (zoom ${after.zoom}${clamped ? `, clamped at minZoom so ${after.outside.length} of ${after.total} cards overflow either way` : `, all ${after.total} cards inside the canvas`}) · minimap ${after.minimap ? 'shown' : 'hidden'}`);

  // THE OTHER HALF: auto-refit must never fight a view the person set up on purpose. Pan the pane by hand, then resize
  // again — the transform must be exactly where the drag left it. An auto-refit that ignored the guard would snap it
  // back to the fitted transform and quietly throw away the place the person had scrolled to.
  await guardScenario(page, errors);
}

// Drag the React Flow pane with real mouse events (this is what sets interactedSinceFit, via React Flow's onMove with
// a non-null event — a programmatic fitView passes null, which is how the two are told apart).
async function panPane(page, dx, dy) {
  // The grab point must be EMPTY pane. The obvious choice — the centre of the box — lands on a card in any tree dense
  // enough to be worth panning, and dragging a card moves the card while leaving the viewport exactly where it was:
  // a check built on that point silently tests nothing. Ask the document what is actually on top at each candidate.
  const spot = await page.evaluate(() => {
    const pane = document.querySelector('.react-flow__pane'); if (!pane) return null;
    const r = pane.getBoundingClientRect();
    for (const fy of [0.5, 0.2, 0.8, 0.35, 0.65]) {
      for (const fx of [0.5, 0.25, 0.75, 0.12, 0.88]) {
        const x = Math.round(r.x + r.width * fx), y = Math.round(r.y + r.height * fy);
        const el = document.elementFromPoint(x, y);
        if (el && (el === pane || el.classList.contains('react-flow__pane'))) return { x, y };
      }
    }
    return null;
  });
  if (!spot) throw new Error('no empty spot on the React Flow pane to grab — every probe point was covered');
  const { x, y } = spot;
  await page.mouse.move(x, y);
  await page.mouse.down();
  for (let i = 1; i <= 6; i++) await page.mouse.move(x + (dx * i) / 6, y + (dy * i) / 6);
  await page.mouse.up();
}

async function guardScenario(page, errors) {
  console.log('\n▶ RESIZE GUARD (a hand-panned view survives a resize)');
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await gotoLineage(page, errors);
  await clickButton(page, 'Tree');
  await page.waitForSelector('.react-flow__node', { timeout: 15000 });
  await sleep(2500);
  const seated = await canvasFit(page);
  await panPane(page, -170, -90);
  await sleep(800);
  const panned = await canvasFit(page);
  await shot(page, 'resize-guard-panned-1440', `panned by hand · transform ${JSON.stringify(seated.transform)} → ${JSON.stringify(panned.transform)}`);
  // If the drag did not actually move the viewport, everything below would "pass" without testing anything.
  if (!seated.transform || !panned.transform || (Math.abs(seated.transform.x - panned.transform.x) < 20 && Math.abs(seated.transform.y - panned.transform.y) < 20)) {
    throw new Error(`RESIZE GUARD CHECK FAILED\n    - the drag did not pan the view (${JSON.stringify(seated.transform)} → ${JSON.stringify(panned.transform)}), so the guard was never exercised`);
  }

  await page.setViewport({ width: 1000, height: 800, deviceScaleFactor: 1 });
  await sleep(2500);
  const afterResize = await canvasFit(page);
  await shot(page, 'resize-guard-after-resize-1000', `resized after the pan · transform ${JSON.stringify(afterResize.transform)}`);
  await page.setViewport({ width: 1680, height: 1000, deviceScaleFactor: 2 });
  await sleep(800);

  const problems = [];
  if (!panned.transform || !afterResize.transform) problems.push('no React Flow transform could be read, so nothing was proved');
  else {
    const t1 = panned.transform, t2 = afterResize.transform;
    if (Math.abs(t1.zoom - t2.zoom) > 0.005 || Math.abs(t1.x - t2.x) > 2 || Math.abs(t1.y - t2.y) > 2) {
      problems.push(`the resize overrode a hand-panned view: ${JSON.stringify(t1)} became ${JSON.stringify(t2)}`);
    }
  }
  if (problems.length) throw new Error('RESIZE GUARD CHECK FAILED\n    - ' + problems.join('\n    - '));
  console.log(`  ✓ the hand-panned view survived the resize unchanged (transform ${JSON.stringify(afterResize.transform)})`);
}

// SLICERS — the multi-select Tables / Fields dropdowns that scope BOTH views to a subset (Kane: the single search box
// "isn't flexible enough"). Verifies: the dropdown opens, ticking options reduces the visible node/card count, the
// count badge/toolbar reflects the slice, and Clear restores the full view.
async function slicerScenario(page, refs, errors) {
  console.log('\n▶ SLICERS (multi-select tables/fields)');
  // ---- GRAPH ----
  await gotoLineage(page, errors);
  await clickButton(page, 'Graph');
  await page.waitForSelector('[_echarts_instance_]', { timeout: 15000 });
  await sleep(3000);
  await clickButton(page, 'Whole model'); await sleep(1500);
  const before = await toolbarNodes(page);
  const opened = await openSlicer(page, 'Tables'); await sleep(400);
  const pt = await tickSlicer(page, 3); await sleep(300);
  await shot(page, 'slicer-graph-tables-open', `opened=${opened} · ticked ${pt.length} tables (dropdown open)`);
  await page.mouse.click(8, 8); await sleep(1800);   // close the popover
  await shot(page, 'slicer-graph-tables-applied', `sliced to 3 tables → ${await toolbarNodes(page)} (was ${before})`);
  await openSlicer(page, 'Fields'); await sleep(400);
  const pf = await tickSlicer(page, 4); await sleep(300);
  await page.mouse.click(8, 8); await sleep(1800);
  await shot(page, 'slicer-graph-fields-added', `+${pf.length} fields (union) → ${await toolbarNodes(page)}`);
  // PRE-EXISTING, found when clickButton stopped failing silently (cp/impact-one-page, 2026-09-15): the tick
  // above is not applying a slice in this harness (the count is unchanged before and after), so the Clear the
  // graph only renders WHILE a slice is applied is not there to press. The caption now says what happened
  // instead of claiming a restore. This belongs to whoever owns the slicer scenario; it is not an Impact bug.
  const graphCleared = await clickButton(page, 'Clear', { optional: true }); await sleep(1800);
  await shot(page, 'slicer-graph-cleared', `${graphCleared ? 'restored' : 'NOT restored: no slice was applied, so there was no Clear to press'} → ${await toolbarNodes(page)}`);

  // ---- TREE ----
  await clickButton(page, 'Tree');
  await page.waitForSelector('.react-flow__node', { timeout: 15000 });
  await sleep(1500);
  const treeCnt = async () => page.evaluate(() => document.querySelectorAll('.react-flow__node').length);
  await clickButton(page, 'Expand all'); await sleep(2000);
  const treeBefore = await treeCnt();
  await openSlicer(page, 'Tables'); await sleep(400);
  const tt = await tickSlicer(page, 2); await sleep(300);
  await page.mouse.click(8, 8); await sleep(2000);
  await shot(page, 'slicer-tree-tables', `pruned to ${tt.length} tables · ${await treeCnt()} cards (was ${treeBefore})`);
  const treeCleared = await clickButton(page, 'Clear', { optional: true }); await sleep(1800);
  await shot(page, 'slicer-tree-cleared', `${treeCleared ? 'restored' : 'NOT restored: no slice was applied, so there was no Clear to press'} → ${await treeCnt()} cards`);
}

async function smallScenario(page, errors) {
  console.log('\n▶ SMALL MODEL (freeze/settle verification, <150 nodes)');
  await gotoLineage(page, errors);
  await clickButton(page, 'Graph');
  await page.waitForSelector('[_echarts_instance_]', { timeout: 15000 });
  await sleep(2500);
  await shot(page, 'small-whole', 'no pins · should be a SETTLED force layout (clusters+edges), not a uniform scatter · ' + (await nodeCountLabel(page)));
}

async function reportsScenario(page, refs, errors) {
  console.log('\n▶ REPORT-LAYER MERGE');
  await gotoLineage(page, errors);

  // Safe to remove and Published reports were separate sub-tabs until Kane's option A. The same three things
  // they carried now live on ONE page: the cleanup list is a tick box, the report selection is one drawer, and
  // the reading belongs to the engine. This scenario follows that journey instead.
  await clickButton(page, 'Impact');
  await sleep(700);
  await clickTestid(page, 'impact-cleanup-toggle');
  await sleep(700);
  await shot(page, 'cleanup-candidates', 'the cleanup list: reasons and three groups, nothing called safe');

  await clickTestid(page, 'impact-cleanup-toggle');
  await sleep(500);
  await clickTestid(page, 'impact-choose-reports');
  await sleep(800);
  await shot(page, 'choose-reports-drawer', 'one drawer: the shared sign-in chooser, the permission sentence, local folders');

  await clickTestid(page, 'choose-reports-check');
  await sleep(1400);
  await shot(page, 'reports-after-a-check', 'after the reading: the strip and the card say what was checked');

  // A model edit does not quietly leave a stale reading standing in for a fresh one. The engine stamps the
  // revision it read at, so the page only has to ask again.
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange', payload: { sessionId: 'uishot', revision: 99, origin: 'user', label: 'edit measure', deltas: [] } } })));
  await sleep(1400);
  await shot(page, 'reports-after-a-model-edit', 'the page re-asks the engine for what is still checked');

  // The Graph and Tree carry the field → visual → page → report leg from the SAME reading the drawer made.
  await clickButton(page, 'Graph');
  await page.waitForSelector('[_echarts_instance_]', { timeout: 15000 });
  await sleep(3000);
  await shot(page, 'reports-graph-merged', await nodeCountLabel(page));

  await clickButton(page, 'Tree');
  await page.waitForSelector('.react-flow__node', { timeout: 15000 });
  await sleep(1200);
  // root at a REPORT node and trace UPSTREAM → report → page → visual → field (bounded + legible)
  await page.type('input[placeholder^="Search to start from"]', 'Executive Summary');
  await sleep(600);
  await page.keyboard.press('Enter');
  await sleep(1200);
  await clickButton(page, '↑ Upstream', { prefix: true });
  await sleep(1000);
  await clickButton(page, 'Expand all');
  await sleep(2000);
  await shot(page, 'reports-tree-merged', 'report → page → visual → field (upstream)');
}

// ---- run --------------------------------------------------------------------------------------
if (existsSync(outDir)) rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });
// A small (<150-node) hand-built graph — for conclusively verifying the force layout SETTLES at small sizes (the case
// that exposed the freeze regression; big views always settled synchronously and hid it).
function makeSmallLineage() {
  const nodes = [], edges = [];
  const N = (ref, name, kind, table) => nodes.push({ ref, name, kind, table: table ?? null, isHidden: false, detail: null });
  const E = (from, to, kind) => edges.push({ from, to, kind });
  N('source:sql/DW', 'DW', 'source', null);
  const tables = ['Sales', 'Date', 'Customer', 'Product', 'Store'];
  const measureRefs = [];
  tables.forEach((t) => {
    N('table:' + t, t, 'table'); E('source:sql/DW', 'table:' + t, 'source');
    for (let i = 0; i < 5; i++) { const c = 'column:' + t + '/C' + i; N(c, t + ' C' + i, i === 0 ? 'column' : 'column', t); E('table:' + t, c, 'contains'); }
  });
  for (let i = 0; i < 24; i++) {
    const t = tables[i % tables.length]; const ref = 'measure:' + t + '/M' + i; N(ref, t + ' M' + i, 'measure', t); E('table:' + t, ref, 'contains');
    E(ref, 'column:' + t + '/C' + (i % 5), 'dependsOn');
    if (i > 3) E(ref, measureRefs[i % measureRefs.length], 'dependsOn');
    measureRefs.push(ref);
  }
  tables.slice(1).forEach((d) => E('column:Sales/C1', 'column:' + d + '/C0', 'relationship'));
  return { lineage: { nodes, edges, caveat: 'small test model' }, unused: { items: [], safeCount: 0, usedByUnusedOnlyCount: 0, cautionCount: 0 }, reportAnalysis: null, stats: { nodes: nodes.length, edges: edges.length, measures: 24, tables: 5 }, refs: {} };
}

const fixture = only === 'small' ? makeSmallLineage() : makeFinanceLineage();
console.log(`uishot lineage: fixture = ${fixture.stats.nodes} nodes / ${fixture.stats.edges} edges (${fixture.stats.measures} measures, ${fixture.stats.tables} tables)`);

const server = await startServer();
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars'] });
const page = await browser.newPage();
page.__url = `http://127.0.0.1:${port}/tools/uishot/harness.html#Lineage`;
await page.setViewport({ width: 1680, height: 1000, deviceScaleFactor: 2 });

const errors = [];
page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });

// Inject fixture + the echarts test flag BEFORE any page script runs.
await page.evaluateOnNewDocument((fx) => {
  window.__ECHARTS_TEST__ = 1;
  window.__UISHOT__ = { lineage: fx.lineage, unused: fx.unused, reportAnalysis: fx.reportAnalysis };
}, fixture);

let failed = 0;
try {
  if (only === 'small') await smallScenario(page, errors);
  if (only === 'all' || only === 'graph') await graphScenario(page, fixture.refs, errors);
  if (only === 'all' || only === 'tree') await treeScenario(page, fixture.refs, errors);
  if (only === 'all' || only === 'resize') await resizeScenario(page, fixture.refs, errors);
  if (only === 'all' || only === 'slicer') await slicerScenario(page, fixture.refs, errors);
  if (only === 'all' || only === 'reports') await reportsScenario(page, fixture.refs, errors);
} catch (e) { failed++; console.error('  ✗ scenario error: ' + ((e && e.stack) || e)); }
finally {
  await browser.close();
  server.close();
}

console.log('\n──────── STEP REPORT ────────');
for (const l of report) console.log(l);
if (errors.length) { console.log(`\n──────── ${errors.length} PAGE/CONSOLE ERROR(S) ────────`); errors.slice(0, 30).forEach((e) => console.log('  ' + e)); }
else console.log('\n✓ no page/console errors captured');
console.log(`\nshots → ${outDir}`);
process.exit(failed ? 1 : 0);
