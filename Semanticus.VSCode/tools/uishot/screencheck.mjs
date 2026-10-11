// The screen check, shared by every uishot driver. A driver calls checkScreen(page, key) on a page it has
// already rendered; this measures the page with measure.js, judges it with screen-judge.mjs, and compares the
// counts with tools/uishot/visual-baseline.json. At the end the driver calls screenFailures() and fails
// if the list is not empty.
//
// ONE VIEWPORT. Every page is measured at 1440 x 900, whatever size the driver shoots at, so a page has one
// baseline and not one per window. When the driver's viewport differs, the check resizes to 1440 x 900 (keeping
// the device scale factor, which does not move CSS pixels), measures, and puts the viewport back.
//
// THREE THEMES. After the layout is measured, the page is put in Dark Modern, Light Modern and Dark High
// Contrast in turn (themes.mjs, the way VS Code themes a webview) and every text box below 4.5:1 is counted
// per theme. The page is handed back with no theme applied.
//
// THE RATCHET. A page may not get worse in any one kind (gap, margin, padding, grid, nesting, and contrast
// in each theme). A page that reached zero in a kind may never regress in it. A key not in the baseline must be clean. Lowering the
// baseline after a page gets better, or adding a new driver's key, is one run with SEMANTICUS_VISUAL_SEED=1:
// it writes lower counts and new keys, and it never raises an existing count.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { KINDS, SPACE_KINDS, SPACE_SIZES, THEME_IDS, compareScreen, compareSpace, judge, judgeSpace } from './screen-judge.mjs';
import { applyTheme } from './themes.mjs';

export const CANONICAL = { width: 1440, height: 900 };
const MEASURE = readFileSync(new URL('./measure.js', import.meta.url), 'utf8');
export const BASELINE_FILE = new URL('./visual-baseline.json', import.meta.url);
const SEED = !!process.env.SEMANTICUS_VISUAL_SEED;
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

export function readScreenBaseline() {
  if (!existsSync(BASELINE_FILE)) return { pages: {} };
  return JSON.parse(readFileSync(BASELINE_FILE, 'utf8'));
}

// Seed mode: new keys go in, lower counts go in, a higher count is never written.
function seed(key, counts) {
  const file = readScreenBaseline();
  const cur = file.pages[key];
  const next = cur ? Object.fromEntries(KINDS.map((k) => [k, Math.min(cur[k] ?? 0, counts[k])])) : { ...counts };
  if (cur && KINDS.every((k) => cur[k] === next[k])) return;
  file.pages[key] = next;
  file.pages = Object.fromEntries(Object.entries(file.pages).sort(([a], [b]) => a.localeCompare(b)));
  file.total = Object.values(file.pages).reduce((a, p) => a + KINDS.reduce((s, k) => s + (p[k] ?? 0), 0), 0);
  writeBaseline(file);
}

const ABOUT = 'Two baselines in one file. `pages` is the screen family (gap, margin, padding, grid, nesting, contrast), '
  + 'one entry per page state, measured at 1440 x 900. `space` is the space family (Kane\'s space rule), one entry per '
  + 'scene, measured at all three of 2000 x 1200, 1440 x 900 and 900 x 900; a scene\'s count is the worst it reaches at '
  + 'any of them. Counts may only go down. `spaceNotes` says why any space count above zero is recorded rather than '
  + 'fixed. See tools/uishot/screencheck.mjs.';

/** The exact bytes of a baseline file. Separate from writing it so a test can hold the shape to its promises
 *  without a baseline file to write to: a seeded run that lowers a count must carry `spaceNotes` through, and
 *  dropping that line silently threw away the reason every recorded count is recorded. */
export function baselineText(file) {
  return JSON.stringify({
    about: ABOUT,
    viewport: [CANONICAL.width, CANONICAL.height], total: file.total, pages: file.pages,
    ...(file.space ? { space: file.space } : {}),
    // Carried through a seeded run: the reason a count is not zero must not be dropped by the tool that lowers it.
    ...(file.spaceNotes ? { spaceNotes: file.spaceNotes } : {}),
  }, null, 1) + '\n';
}

function writeBaseline(file) {
  writeFileSync(BASELINE_FILE, baselineText(file));
}

// Layout on the page as the driver rendered it; then contrast with the page in each of the three themes, and
// the page handed back with no theme applied, as it was.
export async function measureScreen(page, root = null, { themes = true } = {}) {
  const has = await page.evaluate(() => typeof window.__semMeasure === 'function');
  if (!has) await page.addScriptTag({ content: MEASURE });
  const m = await page.evaluate((r) => window.__semMeasure(r), root);
  if (themes) {
    m.contrast = {};
    // Colour transitions (120 to 260 ms on controls, the Pro chip's is the slowest) would otherwise be caught
    // halfway, and the same page would score differently run to run. They are switched off while measuring.
    await page.evaluate(() => {
      const st = document.createElement('style');
      st.id = '__sem_no_motion';
      st.textContent = '*, *::before, *::after { transition: none !important; animation: none !important; }';
      document.head.appendChild(st);
    });
    try {
      for (const t of THEME_IDS) {
        await applyTheme(page, t);
        await wait(150);
        // The whole window, not only the page's own box: the header and the bottom bar are on every page too.
        m.contrast[t] = await page.evaluate(() => window.__semContrast('body'));
      }
    } finally {
      await applyTheme(page, null);
      await page.evaluate(() => document.getElementById('__sem_no_motion')?.remove());
      await wait(60);
    }
  }
  return m;
}

const results = [];

// root: a selector for the page's own box (defaults to <main>). prepare(page) may return a selector instead,
// for a root that has to be found after the resize (a dialog, the Connections hub).
export async function checkScreen(page, key, { root = null, prepare = null, settle = 500 } = {}) {
  const vp = page.viewport();
  const resize = !vp || vp.width !== CANONICAL.width || vp.height !== CANONICAL.height;
  let result;
  try {
    if (resize) { await page.setViewport({ ...(vp ?? {}), width: CANONICAL.width, height: CANONICAL.height }); await wait(settle); }
    const sel = prepare ? await prepare(page) : root;
    const m = await measureScreen(page, sel ?? root);
    const verdict = judge(m);
    const base = readScreenBaseline().pages[key];
    if (SEED) seed(key, verdict.counts);
    const cmp = compareScreen(key, verdict.counts, SEED ? readScreenBaseline().pages[key] : base);
    result = { key, ...verdict, ...cmp, baseline: base ?? null, measurement: m };
  } catch (e) {
    result = { key, counts: null, details: [], failures: [`${key}: the screen check could not measure the page (${String(e.message ?? e).slice(0, 200)})`], lowerable: [] };
  } finally {
    if (resize && vp) { await page.setViewport(vp); await wait(300); }
  }
  results.push(result);
  const c = result.counts;
  console.log(`  screen ${key.padEnd(34)} ${c ? KINDS.map((k) => `${k.replace('contrast-', 'c-')} ${c[k]}`).join('  ') : 'not measured'}${result.failures.length ? '  WORSE THAN BASELINE' : ''}`);
  for (const f of result.failures) console.log(`    ${f}`);
  if (result.failures.length) for (const d of result.details.slice(0, 12)) console.log(`      ${d}`);
  return result;
}

export const screenResults = () => results;
export const screenFailures = () => results.flatMap((r) => r.failures);

// ---- the space family ----------------------------------------------------------------------------------------
// Runs in the page. Reads what screen-judge.mjs judgeSpace needs at the current window size: whether the page
// scrolls and how much of it is empty below the content, the boxes that scroll inside themselves, controls cut
// off by a scroller, capped frames, big boxes (keyed by DOM path, so two window sizes can be compared), the
// data-primary region, and the narrow faults. The root is the page's scroll region (<main> by default).
function spaceInPage(rootSel) {
  const vw = innerWidth, vh = innerHeight;
  const root = (rootSel && document.querySelector(rootSel)) || document.querySelector('main') || document.body;
  const rr = root.getBoundingClientRect();
  const A = { l: Math.max(0, rr.left), t: Math.max(0, rr.top), r: Math.min(vw, rr.right), b: Math.min(vh, rr.bottom) };
  const vis = (el) => { const cs = getComputedStyle(el); return cs.visibility !== 'hidden' && cs.display !== 'none' && +cs.opacity > 0.05 && el.getClientRects().length > 0; };
  const name = (el) => { const id = el.getAttribute('data-testid'); const cls = (typeof el.className === 'string' ? el.className : '').split(/\s+/).filter((c) => c && !c.includes(':') && !c.includes('[')).slice(0, 3).join('.'); return `${el.tagName.toLowerCase()}${id ? `[data-testid=${id}]` : ''}${cls ? '.' + cls : ''}`; };
  const text = (el) => (el.innerText || el.value || el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 40);
  const path = (el) => { const p = []; for (let e = el; e && e !== document.body; e = e.parentElement) { const i = e.parentElement ? [...e.parentElement.children].indexOf(e) : 0; p.push(`${e.tagName.toLowerCase()}:${i}`); } return p.reverse().join('/'); };
  const scrollsY = (el) => /(auto|scroll)/.test(getComputedStyle(el).overflowY) && el.scrollHeight > el.clientHeight + 4;
  // The page scrolls when the root, or a page-sized scroller inside it (a tab's own full-height pane), does.
  const pageScrollers = [root, ...root.querySelectorAll('*')].filter((el) => { const r = el.getBoundingClientRect(); return scrollsY(el) && r.height >= (A.b - A.t) * 0.85 && r.width >= (A.r - A.l) * 0.85; });
  const pageScroller = new Set(pageScrollers);
  const rootScrolls = pageScrollers.length > 0;
  // The lowest visible content, clipped by every scroller between it and the viewport, and the same low-water mark
  // kept for every box it sits in (lowestIn), which is what tells a stretched card from a full one.
  let maxB = A.t;
  const lowestIn = new Map();
  const clipRect = (el) => { const r = el.getBoundingClientRect(); let t = r.top, b = r.bottom, l = r.left, rt = r.right; for (let a = el.parentElement; a && a !== document.documentElement; a = a.parentElement) { const cs = getComputedStyle(a); if (/(auto|scroll|hidden|clip)/.test(cs.overflowY + cs.overflowX)) { const q = a.getBoundingClientRect(); t = Math.max(t, q.top); b = Math.min(b, q.bottom); l = Math.max(l, q.left); rt = Math.min(rt, q.right); } } return { t, b, l, r: rt, h: r.height, w: r.width }; };
  for (const el of root.querySelectorAll('*')) {
    if (!vis(el)) continue;
    const cs = getComputedStyle(el);
    const media = /^(IMG|SVG|CANVAS|TEXTAREA|INPUT|SELECT|BUTTON|TABLE|PRE)$/i.test(el.tagName);
    const hasText = [...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim());
    const boxed = cs.borderBottomWidth !== '0px' || (cs.backgroundColor !== 'rgba(0, 0, 0, 0)' && cs.backgroundColor !== 'transparent');
    if (!(media || hasText || boxed)) continue;
    if (el.closest('svg') && el.tagName.toLowerCase() !== 'svg') continue;
    const c = clipRect(el);
    if (c.b <= c.t + 1 || c.r <= c.l + 1) continue;
    // A frame is not content. A card, a panel or a page wrapper carries no text and no media of its own; what is
    // IN it is the content, and its children are measured on their own. Counting a frame's lower edge as content
    // made a card stretched to the page bottom hide every fault under it: the card's own border became the lowest
    // thing on the page, so the page never "had room", and a short scrolling box inside it with Save cut off
    // scored clean. (The stretch is how AI instructions is built; this is the fault it was hiding.)
    if (boxed && !media && !hasText) continue;
    const b = Math.min(c.b, A.b);
    maxB = Math.max(maxB, b);
    for (let a = el; a && a !== root.parentElement; a = a.parentElement) { const cur = lowestIn.get(a); if (cur === undefined || b > cur) lowestIn.set(a, b); }
  }
  const emptyBelow = Math.max(0, A.b - maxB);
  // The room a box that scrolls could have grown into: the largest patch of empty space left in any of the boxes it
  // sits in, down to the page itself. The band under the page's lowest content is only the last of those, and on a
  // card stretched to the page bottom there is no such band at all: the space is INSIDE the card, above the card's
  // own edge, which is exactly where a short scrolling box hides.
  const slackFor = (el) => {
    let slack = 0;
    for (let a = el.parentElement; a && a !== root.parentElement; a = a.parentElement) {
      const low = lowestIn.get(a);
      if (low === undefined) continue;
      const q = a.getBoundingClientRect(); if (q.height < 1) continue;
      const cs = getComputedStyle(a);
      const avail = Math.min(q.bottom - (parseFloat(cs.paddingBottom) || 0) - (parseFloat(cs.borderBottomWidth) || 0), A.b);
      slack = Math.max(slack, avail - low);
    }
    return Math.round(slack);
  };
  const scrollers = [];
  for (const el of root.querySelectorAll('*')) {
    if (pageScroller.has(el) || !vis(el) || el.closest('[data-scroll-ok]')) continue;
    const r = el.getBoundingClientRect(); if (r.height < 40 || r.width < 80) continue;
    const ta = el.tagName === 'TEXTAREA' && el.scrollHeight > el.clientHeight + 4;
    if (ta || scrollsY(el)) scrollers.push({ name: name(el), h: r.height, hiddenPx: el.scrollHeight - el.clientHeight, slack: slackFor(el) });
  }
  // A row of a long list is what its box is FOR, so a row below the fold is the list working, not an action put out
  // of reach. Counting repeats alone is not enough: a panel with five sections has five Save buttons, and exempting
  // those is the D6 fault in a new coat. What separates them is whether the thing out of reach is a ROW or an ACTION.
  //   - It carries the app's own button styling: a section's Save, a row's Open. An ACTION, never exempt, wherever
  //     it sits and however many things around it repeat. This is rule 2 word for word, "buttons are never inside a
  //     scroller", and it is the only reading that survives the real page: each Docs narrative section is its own
  //     wrapper and a model has three, so "an ancestor repeats three times" exempted every Save, Clear, Copy and
  //     Give to your assistant in that column, which is the very fault the rule was written for.
  //   - Anything else that repeats three or more times: the picker's field rows and its table headers, whole rows
  //     drawn as buttons, a list of saved models. A list row, exempt.
  // Sizes were the first try at telling these apart and they cannot: a tree of table headers and field rows splits
  // its height across two shapes, so neither reached a share of the box and a real list row was flagged.
  const sig = (el) => `${el.tagName}|${typeof el.className === 'string' ? el.className : ''}`;
  const ACTION = /(^|\s)(sem-btn|sem-seg-item)(\s|$)/;
  const isListRow = (el, box) => {
    if (ACTION.test(typeof el.className === 'string' ? el.className : '')) return false;
    for (let e = el; e && e !== box; e = e.parentElement) {
      const p = e.parentElement; if (!p) break;
      let n = 0; for (const k of p.children) if (sig(k) === sig(e)) n++;
      if (n >= 3) return true;
    }
    return false;
  };
  const hiddenActions = [];
  for (const el of root.querySelectorAll('button, a[href], input:not([type=hidden]), select, textarea, [role=button]')) {
    if (!vis(el)) continue;
    const r = el.getBoundingClientRect(); if (r.width < 4 || r.height < 4) continue;
    let t = r.top, b = r.bottom; let box = null;
    for (let a = el.parentElement; a && a !== document.documentElement; a = a.parentElement) {
      if (pageScroller.has(a)) break;
      if (!scrollsY(a)) continue;
      const q = a.getBoundingClientRect(); t = Math.max(t, q.top); b = Math.min(b, q.bottom); box ??= a;
    }
    if (!box) continue;
    hiddenActions.push({ name: name(el), text: text(el), visible: Math.max(0, b - t) / r.height, box: name(box), listRow: isListRow(el, box) });
  }
  const rootW = A.r - A.l;
  const caps = [];
  for (const el of root.querySelectorAll('*')) {
    if (!vis(el)) continue; const cs = getComputedStyle(el);
    if (cs.maxWidth === 'none' || !/px|rem|em|ch/.test(cs.maxWidth)) continue;
    const r = el.getBoundingClientRect(); if (r.width < 400 || r.height < (A.b - A.t) * 0.5) continue;
    caps.push({ name: name(el), w: r.width, maxWidth: cs.maxWidth, readable: !!el.closest('[data-readable]') });
  }
  const boxes = [];
  for (const el of [root, ...root.querySelectorAll('*')]) {
    if (!vis(el)) continue; const r = el.getBoundingClientRect(); if (r.width < 400 || r.height < 300) continue;
    const cs = getComputedStyle(el);
    const boxed = cs.borderTopWidth !== '0px' || (cs.backgroundColor !== 'rgba(0, 0, 0, 0)' && cs.backgroundColor !== 'transparent');
    if (!boxed || el.closest('[data-readable]')) continue;
    boxes.push({ path: path(el), name: name(el), w: r.width, h: r.height });
  }
  const pr = root.querySelector('[data-primary]');
  let primary = null;
  if (pr && vis(pr)) {
    const more = pr.querySelector('[data-primary-more]');
    primary = { name: name(pr), h: clipRect(pr).b - clipRect(pr).t, more: more && vis(more) ? text(more) : null, clipped: [pr, ...pr.querySelectorAll('*')].some((e) => scrollsY(e)) };
  }
  // Buttons, segments and tabs only. A wrapped one is a short label (30 characters or fewer) broken onto two lines;
  // a long row title wraps by design.
  const clipped = [], wrapped = [];
  for (const el of root.querySelectorAll('.sem-btn, .sem-seg-item, [role=tab]')) {
    if (!vis(el)) continue; const r = el.getBoundingClientRect(); if (!r.width) continue;
    const cs = getComputedStyle(el); const t = text(el); if (!t) continue;
    if (el.scrollWidth > el.clientWidth + 2 && /(hidden|clip)/.test(cs.overflowX) && cs.textOverflow !== 'ellipsis') clipped.push({ name: name(el), text: t });
    const lh = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.3;
    if (cs.whiteSpace !== 'normal' || el.children.length > 2) continue;
    if (r.height > lh * 1.9 + parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + parseFloat(cs.borderTopWidth) * 2 && r.height < 120 && t.length <= 30) wrapped.push({ name: name(el), text: t });
  }
  const docScrollX = Math.max(0, document.documentElement.scrollWidth - vw, root.scrollWidth - root.clientWidth > 1 && /(auto|scroll)/.test(getComputedStyle(root).overflowX) ? root.scrollWidth - root.clientWidth : 0);
  return { vw, vh, rootW, availH: A.b - A.t, rootScrolls, emptyBelow, scrollers, hiddenActions, caps, boxes, primary, clipped: clipped.slice(0, 20), wrapped: wrapped.slice(0, 20), docScrollX };
}

export const readSpaceBaseline = () => readScreenBaseline().space ?? {};

function seedSpace(key, counts) {
  const file = readScreenBaseline();
  file.space ??= {};
  const cur = file.space[key];
  const next = cur ? Object.fromEntries(SPACE_KINDS.map((k) => [k, Math.min(cur[k] ?? 0, counts[k])])) : { ...counts };
  if (cur && SPACE_KINDS.every((k) => cur[k] === next[k])) return;
  file.space[key] = next;
  file.space = Object.fromEntries(Object.entries(file.space).sort(([a], [b]) => a.localeCompare(b)));
  writeBaseline(file);
}

const spaceResults = [];
// Measure one scene at the three space sizes, judge, and hold it to the space baseline. The page is resized in
// place (every Studio page lays out from the window); shoot(page, size) may take a picture at each size.
export async function checkSpace(page, key, { root = null, prepare = null, settle = 900, shoot = null, ratchet = true } = {}) {
  const vp = page.viewport();
  let result;
  try {
    const sizes = {};
    for (const z of SPACE_SIZES) {
      await page.setViewport({ ...(vp ?? {}), width: z.width, height: z.height });
      await wait(settle);
      const sel = prepare ? await prepare(page) : root;
      sizes[z.id] = await page.evaluate(spaceInPage, sel ?? root);
      if (shoot) await shoot(page, z);
    }
    const verdict = judgeSpace(sizes);
    const base = readSpaceBaseline()[key];
    if (SEED && ratchet) seedSpace(key, verdict.counts);
    const cmp = ratchet ? compareSpace(key, verdict.counts, SEED ? readSpaceBaseline()[key] : base) : { failures: [], lowerable: [] };
    result = { key, ...verdict, ...cmp, baseline: base ?? null, sizes };
  } catch (e) {
    result = { key, counts: null, details: [], failures: [`${key}: the space check could not measure the page (${String(e.message ?? e).slice(0, 200)})`], lowerable: [] };
  } finally {
    if (vp) { await page.setViewport(vp); await wait(300); }
  }
  if (ratchet) spaceResults.push(result);
  const c = result.counts;
  console.log(`  space  ${key.padEnd(34)} ${c ? SPACE_KINDS.map((k) => `${k} ${c[k]}`).join('  ') : 'not measured'}${result.failures.length ? '  WORSE THAN BASELINE' : ''}`);
  for (const f of result.failures) console.log(`    ${f}`);
  for (const d of result.details.slice(0, 12)) console.log(`      ${d}`);
  return result;
}
export const spaceFailures = () => spaceResults.flatMap((r) => r.failures);
