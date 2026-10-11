// Injected into a rendered page by screencheck.mjs. window.__semMeasure(rootSelector) reads the layout numbers
// the screen check judges (screen-judge.mjs); window.__semAnnotate(measurement, title) draws them on the page
// for a human. Moved here from the 23 September 2026 visual audit, which measured all 22 Studio pages with it.
//
// A "card" is any box the eye reads as a panel: a visible border on 3 or more sides, or a filled background
// that differs from what it sits on, at least 120 x 32, and not a control. `bordered` separates a card from a
// flat inset (filled, no border), which is the approved way to group things inside a card. Gaps are measured
// between NEIGHBOUR cards that share the same parent card (or the page), and a gap with text or a control
// inside it is not an empty gap between cards, so it is skipped. Numbers are raw, not rounded; the judge
// applies the half-pixel tolerance.
window.__semMeasure = function (rootSel) {
  const root = (rootSel && document.querySelector(rootSel)) || document.querySelector('main') || document.body;
  const R = root.getBoundingClientRect();
  const CONTROL = new Set(['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'A', 'SVG', 'svg', 'PATH', 'OPTION', 'CANVAS', 'IMG']);
  const transparent = (c) => !c || c === 'transparent' || /rgba\([^)]*,\s*0\)$/.test(c);
  const bgOf = (el) => { for (let e = el; e; e = e.parentElement) { const b = getComputedStyle(e).backgroundColor; if (!transparent(b)) return b; } return 'rgb(255,255,255)'; };
  const visible = (el) => { const cs = getComputedStyle(el); return cs.visibility !== 'hidden' && cs.display !== 'none' && +cs.opacity > 0.05; };
  const borderSides = (cs) => ['Top', 'Right', 'Bottom', 'Left'].filter((s) => parseFloat(cs['border' + s + 'Width']) > 0 && cs['border' + s + 'Style'] !== 'none' && !transparent(cs['border' + s + 'Color'])).length;
  const isCard = (el) => {
    const r = el.getBoundingClientRect();
    // A control is a card only when it is card-sized (the Pro page's plan choices are buttons drawn as cards).
    const bigControl = (el.tagName === 'BUTTON' || el.tagName === 'LABEL') && r.width >= 160 && r.height >= 56;
    if (!bigControl && (CONTROL.has(el.tagName) || el.closest('button,[role="menuitem"],.cm-editor,.react-flow,svg,table'))) return false;
    if (bigControl && el.parentElement && el.parentElement.closest('button')) return false;
    if (r.width < 120 || r.height < 32 || !visible(el)) return false;
    const cs = getComputedStyle(el);
    if (borderSides(cs) >= 3) return true;
    const bg = cs.backgroundColor;
    return !transparent(bg) && !!el.parentElement && bg !== bgOf(el.parentElement);
  };
  const all = [...root.querySelectorAll('*')].filter(isCard);
  const set = new Set(all);
  const parentOf = (el) => { for (let e = el.parentElement; e && e !== root; e = e.parentElement) if (set.has(e)) return e; return root; };
  const depthOf = (el) => { let d = 0; for (let p = parentOf(el); p !== root; p = parentOf(p)) d++; return d; };
  const name = (el) => el.tagName.toLowerCase() + (el.getAttribute('data-testid') ? '[' + el.getAttribute('data-testid') + ']' : '')
    + (el.classList.length ? '.' + [...el.classList].filter((c) => !c.includes(':') && !c.includes('[')).slice(0, 3).join('.') : '');

  // Content boxes (text lines and controls). A gap with any of these inside it is not an empty gap.
  const blocks = [];
  { const tw = document.createTreeWalker(root, NodeFilter.SHOW_TEXT); let n, k = 0;
    while ((n = tw.nextNode()) && k < 8000) { k++; if (!n.textContent.trim() || !n.parentElement || !visible(n.parentElement)) continue;
      const rg = document.createRange(); rg.selectNodeContents(n); for (const rr of rg.getClientRects()) if (rr.width > 0) blocks.push({ x: rr.left, y: rr.top, r: rr.right, b: rr.bottom }); }
    // A segmented control is one control whose visible edge is its own hairline box; the buttons inside it sit
    // 2px in (border plus padding), so without the box the margin would be read off the first button.
    for (const c of root.querySelectorAll('button,input,select,textarea,.sem-seg')) { const rr = c.getBoundingClientRect(); if (rr.width > 0 && visible(c)) blocks.push({ x: rr.left, y: rr.top, r: rr.right, b: rr.bottom }); } }
  const empty = (x, y, r, b) => !blocks.some((q) => q.x < r - 1 && q.r > x + 1 && q.y < b - 1 && q.b > y + 1);

  // Where the card's content starts, from the inside of its left border: its real inner padding.
  const insetLeft = (el) => {
    const r = el.getBoundingClientRect(); let l = Infinity;
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT); let n, k = 0;
    while ((n = walker.nextNode()) && k < 400) {
      k++; if (!n.textContent.trim() || !n.parentElement || !visible(n.parentElement)) continue;
      const range = document.createRange(); range.selectNodeContents(n);
      const rr = range.getBoundingClientRect(); if (!rr.width) continue;
      l = Math.min(l, rr.left - r.left);
    }
    for (const c of el.querySelectorAll('button,input,select,textarea,svg')) { const rr = c.getBoundingClientRect(); if (!rr.width || !visible(c)) continue; l = Math.min(l, rr.left - r.left); }
    return isFinite(l) ? l - (parseFloat(getComputedStyle(el).borderLeftWidth) || 0) : null;
  };

  const cards = all.map((el, i) => {
    const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); const p = parentOf(el);
    // parent: the index of the card it sits in (absent on the page); innerX and innerR: inside its own left and
    // right border, so the judge can tell a band that spans its card from an inset set in from it.
    return { i, el, name: name(el), depth: depthOf(el), ...(p === root ? {} : { parent: all.indexOf(p) }), bordered: borderSides(cs) >= 3, filled: !transparent(cs.backgroundColor),
      x: r.left, y: r.top, r: r.right, b: r.bottom, w: r.width, h: r.height, insetLeft: insetLeft(el),
      innerX: r.left + (parseFloat(cs.borderLeftWidth) || 0), innerR: r.right - (parseFloat(cs.borderRightWidth) || 0) };
  });
  const groups = new Map();
  for (const c of cards) { const p = parentOf(c.el); if (!groups.has(p)) groups.set(p, []); groups.get(p).push(c); }
  const vgaps = [], hgaps = [];
  const ovX = (a, b) => Math.min(a.r, b.r) - Math.max(a.x, b.x);
  const ovY = (a, b) => Math.min(a.b, b.b) - Math.max(a.y, b.y);
  for (const [, sibs] of groups) {
    for (const a of sibs) {
      let best = null;
      for (const b of sibs) if (b !== a && b.y >= a.b - 1 && ovX(a, b) > Math.min(a.w, b.w) * 0.3) if (!best || b.y < best.y) best = b;
      if (best && best.y - a.b < 120 && empty(Math.max(a.x, best.x), a.b, Math.min(a.r, best.r), best.y))
        vgaps.push({ a: a.i, b: best.i, gap: best.y - a.b, depth: a.depth, x: Math.max(a.x, best.x) + 12, y1: a.b, y2: best.y });
      best = null;
      for (const b of sibs) if (b !== a && b.x >= a.r - 1 && ovY(a, b) > Math.min(a.h, b.h) * 0.3) if (!best || b.x < best.x) best = b;
      if (best && best.x - a.r < 120 && empty(a.r, Math.max(a.y, best.y), best.x, Math.min(a.b, best.b)))
        hgaps.push({ a: a.i, b: best.i, gap: best.x - a.r, depth: a.depth, y: Math.max(a.y, best.y) + 12, x1: a.r, x2: best.x });
    }
  }
  // The page's content box: inside the root's border, and left of its scrollbar if it has one.
  const contentLeft = R.left + root.clientLeft;
  const contentRight = contentLeft + (root.clientWidth || R.width);
  const textLeft = blocks.length ? Math.min(...blocks.map((q) => q.x)) - contentLeft : null;
  return {
    rootName: name(root), viewport: { w: innerWidth, h: innerHeight },
    root: { x: R.left, y: R.top, r: R.right, b: R.bottom, contentLeft, contentRight },
    cards: cards.map(({ el, ...c }) => c), vgaps, hgaps, textLeft,
  };
};

// Draw the measured gaps onto the page: each distinct gap value gets its own colour.
window.__semAnnotate = function (m, title) {
  const layer = document.createElement('div');
  layer.id = '__sem_annot';
  Object.assign(layer.style, { position: 'fixed', inset: '0', pointerEvents: 'none', zIndex: 2147483647, font: '600 11px/1 system-ui, sans-serif' });
  const PAL = ['#ff3b6b', '#ffb020', '#20c3ff', '#b36bff', '#3ddc84', '#ff7a1a', '#00e5c0', '#ff5cf0', '#c8ff2e', '#6b8bff'];
  const all = [...m.vgaps, ...m.hgaps].map((g) => Math.round(g.gap));
  const colour = new Map(); [...new Set(all)].sort((a, b) => a - b).forEach((v, i) => colour.set(v, v === 12 ? '#3ddc84' : PAL[i % PAL.length]));
  const H = innerHeight;
  const add = (css, text) => { const d = document.createElement('div'); Object.assign(d.style, { position: 'absolute' }, css); if (text != null) d.textContent = text; layer.appendChild(d); return d; };
  const tag = (x, y, v, c) => add({ left: x + 'px', top: y + 'px', background: c, color: '#111', padding: '2px 4px', borderRadius: '3px', boxShadow: '0 0 0 1px rgba(0,0,0,.5)' }, v + 'px');
  for (const c of m.cards) if (c.depth <= 1 && c.y < H) add({ left: c.x + 'px', top: c.y + 'px', width: c.w + 'px', height: c.h + 'px', outline: '1px dashed rgba(255,255,255,.35)' });
  for (const g of m.vgaps) { if (g.y1 > H) continue; const v = Math.round(g.gap); const c = colour.get(v);
    add({ left: g.x + 'px', top: g.y1 + 'px', width: '4px', height: Math.max(2, g.y2 - g.y1) + 'px', background: c }); tag(g.x + 7, (g.y1 + g.y2) / 2 - 7, v, c); }
  for (const g of m.hgaps) { if (g.y > H) continue; const v = Math.round(g.gap); const c = colour.get(v);
    add({ left: g.x1 + 'px', top: g.y + 'px', width: Math.max(2, g.x2 - g.x1) + 'px', height: '4px', background: c }); tag((g.x1 + g.x2) / 2 - 10, g.y + 7, v, c); }
  const leg = add({ right: '12px', bottom: '12px', background: 'rgba(10,12,16,.92)', color: '#fff', padding: '8px 10px', borderRadius: '6px', display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap', maxWidth: '520px', boxShadow: '0 0 0 1px rgba(255,255,255,.2)' });
  leg.appendChild(Object.assign(document.createElement('span'), { textContent: (title || '') + '  gaps between cards:' }));
  for (const [v, c] of colour) { const s = document.createElement('span'); Object.assign(s.style, { background: c, color: '#111', padding: '2px 4px', borderRadius: '3px' }); s.textContent = v + 'px'; leg.appendChild(s); }
  document.body.appendChild(layer);
};

// Every visible text box under the root whose colour is below 4.5:1 against what it actually sits on. The
// background is found by walking up to the first opaque fill and compositing every translucent layer above it;
// the text colour is composited over that, and faded by the opacity of the element and its ancestors, so a
// dimmed label is judged as it is seen. Disabled controls are left out, as WCAG leaves them out. Text inside
// an svg or a canvas is drawn, not laid out, and is not read.
window.__semContrast = function (rootSel, min) {
  min = min || 4.5;
  const root = (rootSel && document.querySelector(rootSel)) || document.querySelector('main') || document.body;
  const parse = (s) => {
    let m = /^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/.exec(s.trim());
    if (m) return [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : (m[4].endsWith('%') ? parseFloat(m[4]) / 100 : +m[4])];
    m = /^color\(srgb\s+([\d.e-]+)\s+([\d.e-]+)\s+([\d.e-]+)(?:\s*\/\s*([\d.]+%?))?\s*\)$/.exec(s.trim());
    if (m) return [+m[1] * 255, +m[2] * 255, +m[3] * 255, m[4] === undefined ? 1 : (m[4].endsWith('%') ? parseFloat(m[4]) / 100 : +m[4])];
    return null;
  };
  const over = (top, base) => { const a = top[3]; return [top[0] * a + base[0] * (1 - a), top[1] * a + base[1] * (1 - a), top[2] * a + base[2] * (1 - a), 1]; };
  const lin = (c) => { const v = c / 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
  const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
  const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
  const bgOf = (el) => {
    const layers = [];
    for (let e = el; e; e = e.parentElement) {
      const c = parse(getComputedStyle(e).backgroundColor) || [0, 0, 0, 0];
      if (c[3] > 0) layers.push(c);
      if (c[3] >= 1) break;
    }
    let base = [255, 255, 255, 1];
    if (layers.length && layers[layers.length - 1][3] >= 1) base = layers.pop();
    for (let i = layers.length - 1; i >= 0; i--) base = over(layers[i], base);
    return base;
  };
  const opacityOf = (el) => { let o = 1; for (let e = el; e; e = e.parentElement) o *= +getComputedStyle(e).opacity; return o; };
  const name = (el) => el.tagName.toLowerCase() + (el.getAttribute('data-testid') ? '[' + el.getAttribute('data-testid') + ']' : '')
    + (el.classList.length ? '.' + [...el.classList].filter((c) => !c.includes(':') && !c.includes('[')).slice(0, 2).join('.') : '');
  const fmt = (c) => `rgb(${Math.round(c[0])},${Math.round(c[1])},${Math.round(c[2])})`;
  const seen = new Set(); const out = [];
  const tw = document.createTreeWalker(root, NodeFilter.SHOW_TEXT); let n, k = 0;
  while ((n = tw.nextNode()) && k < 20000) {
    k++;
    const el = n.parentElement;
    if (!el || seen.has(el) || !n.textContent.trim()) continue;
    seen.add(el);
    if (el.closest('svg,canvas,:disabled,[aria-disabled="true"],[hidden]')) continue;
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none') continue;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) continue;
    const bg = bgOf(el);
    const fg0 = parse(cs.color); if (!fg0) continue;
    const fg = over([fg0[0], fg0[1], fg0[2], fg0[3] * opacityOf(el)], bg);
    const q = ratio(fg, bg);
    if (q < min) out.push({ name: name(el), text: n.textContent.trim().slice(0, 40), ratio: Math.round(q * 100) / 100, fg: fmt(fg), bg: fmt(bg) });
  }
  return out;
};
