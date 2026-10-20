// Alignment measure for Review > Copilot test (copied from tools/query-proto/align.js, class names changed to ct-), run in the page (page.evaluate). Every card on the page grid must start and end on one of the 12
// column lines to the pixel, cards that share a row must share a top, a bottom and a title line, vertical gaps between
// stacked cards must be the token gap, and every card's direct content must start at the card's content edge (or sit on
// its subgrid's lines). Returns counts plus the first offenders.
() => {
  const page = document.querySelector('.ct-page');
  if (!page) return null;
  const px = (v) => parseFloat(v) || 0;
  const pcs = getComputedStyle(page), pr = page.getBoundingClientRect();
  const left = pr.left + px(pcs.paddingLeft), right = pr.right - px(pcs.paddingRight);
  const gap = px(pcs.columnGap), rowGap = px(pcs.rowGap);
  const colw = (right - left - 11 * gap) / 12;
  const lines = Array.from({ length: 12 }, (_, k) => ({ l: left + k * (colw + gap), r: left + (k + 1) * colw + k * gap }));
  const near = (v, xs) => Math.min(...xs.map((x) => Math.abs(x - v)));
  const vis = (e) => { const s = getComputedStyle(e); const r = e.getBoundingClientRect(); return s.display !== 'none' && s.position !== 'fixed' && s.position !== 'absolute' && r.width > 0 && r.height > 0; };
  const kids = [...page.children].filter(vis);
  const name = (e) => (e.getAttribute('data-testid') || String(e.className).split(' ').slice(0, 2).join('.')).slice(0, 40);
  const R = (e) => e.getBoundingClientRect();
  const out = { cards: kids.length, colw: +colw.toFixed(2), gap, offGrid: [], ragged: [], titleLines: [], gaps: [], inner: [] };
  for (const e of kids) {
    const r = R(e), dl = near(r.left, lines.map((x) => x.l)), dr = near(r.right, lines.map((x) => x.r));
    if (dl > 0.5 || dr > 0.5) out.offGrid.push(`${name(e)} left ${dl.toFixed(1)} right ${dr.toFixed(1)}`);
  }
  // Rows: cards whose tops match.
  const rows = [];
  for (const e of kids) { const t = R(e).top; const row = rows.find((g) => Math.abs(g.top - t) <= 0.5); if (row) row.items.push(e); else rows.push({ top: t, items: [e] }); }
  for (const g of rows) {
    if (g.items.length < 2) continue;
    const bs = g.items.map((e) => R(e).bottom);
    if (Math.max(...bs) - Math.min(...bs) > 0.5) out.ragged.push(g.items.map(name).join(' | '));
    const ts = g.items.map((e) => e.querySelector('.ct-title')).filter(Boolean).map((t) => R(t).top);
    if (ts.length > 1 && Math.max(...ts) - Math.min(...ts) > 0.5) out.titleLines.push(g.items.map(name).join(' | '));
  }
  // Part lines: cards of one row that are built from the same parts (checks) put each part on the same line.
  out.partLines = [];
  for (const g of rows) {
    const cards = g.items.filter((e) => e.classList.contains('ct-nonexistent') && !e.classList.contains('ct-sub'));
    if (cards.length < 2) continue;
    const n = Math.min(...cards.map((e) => e.children.length));
    for (let i = 0; i < n; i++) {
      const ts = cards.map((e) => R(e.children[i]).top);
      if (Math.max(...ts) - Math.min(...ts) > 0.5) out.partLines.push(`${cards.map(name).join(' | ')} part ${i + 1}`);
    }
  }
  // Vertical gaps: for each card, the nearest card above that overlaps it sideways.
  for (const e of kids) {
    const r = R(e);
    const above = kids.filter((o) => o !== e && R(o).bottom <= r.top + 0.5 && R(o).left < r.right - 1 && R(o).right > r.left + 1);
    if (!above.length) continue;
    const b = Math.max(...above.map((o) => R(o).bottom));
    if (Math.abs(r.top - b - rowGap) > 0.5) out.gaps.push(`${name(e)} gap ${(r.top - b).toFixed(1)}`);
  }
  // Inner edges: a card's direct children start at its content edge, unless the card is a subgrid (then on its lines).
  for (const card of kids.filter((e) => e.classList.contains('sem-card')))  {
    const cs = getComputedStyle(card), cr = R(card);
    const cl = cr.left + px(cs.borderLeftWidth) + px(cs.paddingLeft), crr = cr.right - px(cs.borderRightWidth) - px(cs.paddingRight);
    const sub = cs.gridTemplateColumns && card.classList.contains('ct-sub');
    for (const ch of [...card.children].filter(vis)) {
      const r = R(ch);
      if (sub) {
        const okL = near(r.left, [cl, ...lines.map((x) => x.l)]) <= 0.5, okR = near(r.right, [crr, ...lines.map((x) => x.r)]) <= 0.5;
        if (!okL || !okR) out.inner.push(`${name(card)} > ${name(ch)} off subgrid`);
      } else if (Math.abs(r.left - cl) > 0.5 || r.right - crr > 0.5) out.inner.push(`${name(card)} > ${name(ch)} left ${(r.left - cl).toFixed(1)} right ${(r.right - crr).toFixed(1)}`);
    }
  }
  return out;
}
