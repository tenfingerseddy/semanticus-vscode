// The screen check's judge: pure arithmetic over what measure.js reads off a rendered page, no browser.
// test/visual-screen-check.test.mjs runs it in CI on planted measurements; screencheck.mjs runs it in every
// driver on real ones.
//
// Eight counts per page, one per rule Kane approved on 23 September 2026 (the contrast rule once per theme):
//   gap      a gap between two neighbouring cards (or insets) that is not --sem-gap, 12
//   margin   the page's left margin is not --sem-page-margin, 24; or a card sits closer than 24 to the right
//   padding  a bordered card's content does not start --sem-card-pad (16) in, or a flat inset's --sem-inset-pad (12);
//            a flat band that spans its bordered card edge to edge is that card's rows and wants 16
//   grid     a top-level card's left or right edge is not on a line of the 12-column grid
//   nesting  a bordered card sits inside another card (a flat inset inside a card is the approved way)
//   contrast-dark, contrast-light, contrast-hc
//            text below 4.5:1 against what it sits on, with the page in Dark Modern, Light Modern and Dark
//            High Contrast (Instrument, look-and-feel direction B, picked 23 September 2026)
// Tolerance is half a pixel everywhere, so sub-pixel layout does not fail and a whole pixel does.

export const RULES = { gap: 12, margin: 24, cardPad: 16, insetPad: 12, columns: 12, gutter: 12 };
export const TOLERANCE = 0.5;
export const THEME_IDS = ['dark', 'light', 'hc'];
export const KINDS = ['gap', 'margin', 'padding', 'grid', 'nesting', ...THEME_IDS.map((t) => `contrast-${t}`)];
export const MIN_CONTRAST = 4.5;

// Chromium reports rgb()/rgba(), and color(srgb r g b / a) for a color-mix() result. Returns [r, g, b, a], 0..255.
export function parseColor(s) {
  let m = /^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/.exec(s.trim());
  if (m) return [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : (m[4].endsWith('%') ? parseFloat(m[4]) / 100 : +m[4])];
  m = /^color\(srgb\s+([\d.e-]+)\s+([\d.e-]+)\s+([\d.e-]+)(?:\s*\/\s*([\d.]+%?))?\s*\)$/.exec(s.trim());
  if (m) return [+m[1] * 255, +m[2] * 255, +m[3] * 255, m[4] === undefined ? 1 : (m[4].endsWith('%') ? parseFloat(m[4]) / 100 : +m[4])];
  return null;
}

const lin = (c) => { const v = c / 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
const luminance = ([r, g, b]) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
export function contrastRatio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const near = (a, b) => Math.abs(a - b) <= TOLERANCE;
const fmt = (n) => String(Math.round(n * 10) / 10);

// The grid lines of the page's content box: 24 in from each side, 12 columns, a 12 px gutter.
export function gridLines(root) {
  const left = root.contentLeft + RULES.margin;
  const width = root.contentRight - root.contentLeft - 2 * RULES.margin;
  const colW = (width - (RULES.columns - 1) * RULES.gutter) / RULES.columns;
  const starts = []; const ends = [];
  for (let i = 0; i < RULES.columns; i++) { starts.push(left + i * (colW + RULES.gutter)); ends.push(left + i * (colW + RULES.gutter) + colW); }
  return { starts, ends, colW };
}

const nearestLine = (x, lines) => {
  let best = 0;
  for (let i = 1; i < lines.length; i++) if (Math.abs(lines[i] - x) < Math.abs(lines[best] - x)) best = i;
  return { index: best, off: x - lines[best] };
};

export function judge(m) {
  const counts = Object.fromEntries(KINDS.map((k) => [k, 0]));
  const details = [];
  const byIndex = new Map(m.cards.map((c) => [c.i, c]));
  const nameOf = (i) => byIndex.get(i)?.name ?? `card ${i}`;

  for (const g of [...m.vgaps, ...m.hgaps]) {
    if (near(g.gap, RULES.gap)) continue;
    counts.gap++;
    details.push(`gap ${fmt(g.gap)} between ${nameOf(g.a)} and ${nameOf(g.b)} (want ${RULES.gap})`);
  }

  const top = m.cards.filter((c) => c.depth === 0);
  const left = top.length ? Math.min(...top.map((c) => c.x)) - m.root.contentLeft : (m.textLeft == null ? null : m.textLeft);
  if (left != null && !near(left, RULES.margin)) { counts.margin++; details.push(`margin left ${fmt(left)} (want ${RULES.margin})`); }
  if (top.length) {
    const right = m.root.contentRight - Math.max(...top.map((c) => c.r));
    if (right < RULES.margin - TOLERANCE) { counts.margin++; details.push(`margin right ${fmt(right)} (want ${RULES.margin} or more)`); }
  }

  for (const c of m.cards) {
    if (c.depth > 1 || c.w < 200 || c.h < 48 || c.insetLeft == null) continue;
    // A flat band that runs its bordered card's full width (the Plan list's Needs you band, an opened row) is a
    // group of that card's rows, so it starts where the card's rows do; an inset set in from the card wants 12.
    const p = c.parent != null ? byIndex.get(c.parent) : null;
    const band = !c.bordered && p && p.bordered && p.innerX != null && near(c.x, p.innerX) && near(c.r, p.innerR);
    const want = c.bordered || band ? RULES.cardPad : RULES.insetPad;
    if (near(c.insetLeft, want)) continue;
    counts.padding++;
    details.push(`${c.name} padding ${fmt(c.insetLeft)} (want ${want})`);
  }

  const lines = gridLines(m.root);
  for (const c of top) {
    const l = nearestLine(c.x, lines.starts);
    const r = nearestLine(c.r, lines.ends);
    if (near(l.off, 0) && near(r.off, 0)) continue;
    counts.grid++;
    if (!near(l.off, 0)) details.push(`${c.name} left edge x=${fmt(c.x)} is ${fmt(Math.abs(l.off))}px off column ${l.index + 1}`);
    else details.push(`${c.name} right edge x=${fmt(c.r)} is ${fmt(Math.abs(r.off))}px off the end of column ${r.index + 1}`);
  }

  for (const c of m.cards) {
    if (c.depth >= 2 || (c.depth === 1 && c.bordered)) {
      counts.nesting++;
      details.push(`${c.name} is a card inside a card (depth ${c.depth + 1}); use a flat inset`);
    }
  }
  for (const t of THEME_IDS) {
    for (const x of m.contrast?.[t] ?? []) {
      counts[`contrast-${t}`]++;
      details.push(`${t}: "${x.text}" in ${x.name} is ${fmt(x.ratio)}:1 (want ${MIN_CONTRAST})`);
    }
  }
  return { counts, details, total: KINDS.reduce((a, k) => a + counts[k], 0) };
}

// The ratchet. A page may not get worse in any one kind; a page not in the baseline must be clean.
export function compareScreen(key, counts, base) {
  const failures = [];
  if (!base) {
    const bad = KINDS.filter((k) => counts[k] > 0);
    if (bad.length) failures.push(`${key}: not in the baseline, so it must be clean, and it has ${bad.map((k) => `${k} ${counts[k]}`).join(', ')}`);
    return { failures, lowerable: [] };
  }
  const lowerable = [];
  for (const k of KINDS) {
    const b = base[k] ?? 0;
    if (counts[k] > b) failures.push(`${key}: ${k} ${counts[k]}, baseline ${b}${b === 0 ? ' (this page was clean and may never regress)' : ''}`);
    else if (counts[k] < b) lowerable.push(k);
  }
  return { failures, lowerable };
}

// ---- the space family (Kane's space rule, ratified 27 September 2026) -----------------------------------------
// The main content of every page fills the width and height it is given. Paragraphs cap at a readable width.
// Nothing scrolls inside a box while the page still has room. Buttons are never inside a scroller.
// Unlike the eight kinds above, a page is measured at three windows (2000 x 1200, 1440 x 900 and 900 x 900),
// because every one of these faults shows at one size and hides at another. screencheck.mjs measures; this judges.
export const SPACE_SIZES = [{ id: 'wide', width: 2000, height: 1200 }, { id: 'laptop', width: 1440, height: 900 }, { id: 'narrow', width: 900, height: 900 }];
export const SPACE_KINDS = ['boxed-scroll', 'hidden-action', 'page-cap', 'fixed-box', 'primary-small', 'narrow'];
export const SPACE = { roomBelow: 80, hiddenAction: 0.75, capShare: 0.9, fixedShare: 0.8, primaryShare: 0.4 };

export function judgeSpace(sizes) {
  const counts = Object.fromEntries(SPACE_KINDS.map((k) => [k, 0]));
  const details = [];
  const seen = new Set();
  const add = (kind, id, text) => { if (seen.has(`${kind}|${id}`)) return; seen.add(`${kind}|${id}`); counts[kind]++; details.push(text); };
  for (const [size, m] of Object.entries(sizes)) {
    if (!m) continue;
    const room = !m.rootScrolls && m.emptyBelow > SPACE.roomBelow;
    // 1. A box that scrolls while there is room it could have used. The room is the box's own (slack: the emptiest
    //    of the boxes it sits in), not the page's, so a card stretched to the page bottom cannot hide the space it
    //    is wasting behind its own lower edge.
    if (!m.rootScrolls) for (const s of m.scrollers) {
      if ((s.slack ?? m.emptyBelow) <= SPACE.roomBelow) continue;
      add('boxed-scroll', s.name, `${size}: ${s.name} scrolls ${Math.round(s.hiddenPx)} px inside itself while ${Math.round(s.slack ?? m.emptyBelow)} px of the box it sits in is empty`);
    }
    // 2. A control cut off by a box that scrolls, while the page itself does not: one count per box, naming the first
    //    control. This is the ratified rule word for word, "buttons are never inside a scroller". It used to ask for
    //    80 px of empty page as well, and that tuning is why nothing flagged the Docs side panel, which fills the
    //    window and hides its own Save. Rows of a long list are exempt instead (isListRow in screencheck.mjs): a row
    //    below the fold is the list working, and asking for room was a blunt way of saying the same thing.
    if (!m.rootScrolls) for (const a of m.hiddenActions) {
      if (a.visible >= SPACE.hiddenAction || a.listRow) continue;
      add('hidden-action', a.box ?? a.name, `${size}: "${a.text}" (${a.name}) is ${Math.round(a.visible * 100)}% visible inside ${a.box ?? 'a scroller'}`);
    }
    // 5. The page's answer, tagged data-primary, is small while its content is cut off, or stops at a count while
    //    the screen has room. A short answer that shows in full is not a fault.
    if (m.primary && !m.rootScrolls) {
      if (m.primary.clipped && m.primary.h < SPACE.primaryShare * m.availH) add('primary-small', `${m.primary.name}|h`, `${size}: the main region ${m.primary.name} is ${Math.round(m.primary.h)} px of a ${Math.round(m.availH)} px page`);
      else if (m.primary.more && room) add('primary-small', `${m.primary.name}|more`, `${size}: ${m.primary.name} stops at a fixed count ("${m.primary.more}") with ${Math.round(m.emptyBelow)} px empty below`);
    }
  }
  // 3. A page frame capped narrow on a wide screen while the page scrolls. A reading column says data-readable.
  const wide = sizes.wide;
  if (wide?.rootScrolls) for (const c of wide.caps) {
    if (c.readable || c.w >= SPACE.capShare * wide.rootW) continue;
    add('page-cap', c.name, `wide: ${c.name} is capped at ${Math.round(c.w)} px of ${Math.round(wide.rootW)} (max-width ${c.maxWidth}) while the page scrolls`);
  }
  // 4. A big box with the same pixel size at 2000 x 1200 and 1440 x 900, under 80% of the wide window.
  const laptop = sizes.laptop;
  if (wide && laptop) {
    const small = new Map(laptop.boxes.map((b) => [b.path, b]));
    for (const b of wide.boxes) {
      const o = small.get(b.path);
      if (!o || Math.abs(o.w - b.w) > 1 || Math.abs(o.h - b.h) > 1) continue;
      if (b.w >= SPACE.fixedShare * wide.vw) continue;
      add('fixed-box', b.path, `${b.name} is ${Math.round(b.w)} x ${Math.round(b.h)} at both 2000 x 1200 and 1440 x 900; it does not grow with the window`);
    }
  }
  // 6. The narrow window: no sideways page scroll, no clipped button text, no button wrapped to two lines.
  const narrow = sizes.narrow;
  if (narrow) {
    if (narrow.docScrollX) add('narrow', 'scroll-x', `narrow: the page scrolls sideways (${Math.round(narrow.docScrollX)} px too wide)`);
    for (const c of narrow.clipped) add('narrow', `clip|${c.name}|${c.text}`, `narrow: "${c.text}" (${c.name}) is cut off`);
    for (const w of narrow.wrapped) add('narrow', `wrap|${w.name}|${w.text}`, `narrow: "${w.text}" (${w.name}) wraps to two lines`);
  }
  return { counts, details, total: SPACE_KINDS.reduce((a, k) => a + counts[k], 0) };
}

// The same ratchet as compareScreen, over the space kinds. A page not in the space baseline must be clean.
export function compareSpace(key, counts, base) {
  const failures = [];
  if (!base) {
    const bad = SPACE_KINDS.filter((k) => counts[k] > 0);
    if (bad.length) failures.push(`${key}: not in the space baseline, so it must be clean, and it has ${bad.map((k) => `${k} ${counts[k]}`).join(', ')}`);
    return { failures, lowerable: [] };
  }
  const lowerable = [];
  for (const k of SPACE_KINDS) {
    const b = base[k] ?? 0;
    if (counts[k] > b) failures.push(`${key}: space ${k} ${counts[k]}, baseline ${b}${b === 0 ? ' (this page was clean and may never regress)' : ''}`);
    else if (counts[k] < b) lowerable.push(k);
  }
  return { failures, lowerable };
}
