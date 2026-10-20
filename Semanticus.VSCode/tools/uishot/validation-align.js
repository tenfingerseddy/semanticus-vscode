// Alignment measure for Validation A, run in the page (page.evaluate). Adapted from the minimal prototype's align.js (proto/minimal 43479008).
// The placed block of .vc-page (the column) starts and ends on the 12 column lines to 0.5 px; the blocks inside the column share its left
// edge (cards also its right edge); vertical gaps between stacked blocks are the token gap; a card's direct content, an opened check's
// content and a panel's content start on the content edge. Returns counts plus the first offenders.
() => {
  const page = document.querySelector('.vc-page');
  if (!page) return null;
  const px = (v) => parseFloat(v) || 0;
  const R = (e) => e.getBoundingClientRect();
  const vis = (e) => { const s = getComputedStyle(e); const r = R(e); return s.display !== 'none' && s.position !== 'fixed' && s.position !== 'absolute' && r.width > 0 && r.height > 0; };
  const ps = getComputedStyle(page), pr = R(page);
  const left = pr.left + px(ps.paddingLeft), right = pr.right - px(ps.paddingRight), gap = px(ps.columnGap), rowGap = px(ps.rowGap);
  const colw = (right - left - 11 * gap) / 12;
  const L = Array.from({ length: 12 }, (_, k) => left + k * (colw + gap)), Rr = Array.from({ length: 12 }, (_, k) => left + (k + 1) * colw + k * gap);
  const near = (v, xs) => Math.min(...xs.map((x) => Math.abs(x - v)));
  const name = (e) => (e.getAttribute('data-testid') || String(e.className).split(' ').filter((c) => c.startsWith('va-') || c.startsWith('sem-')).slice(0, 2).join('.') || e.tagName).slice(0, 40);
  const out = { offGrid: [], column: [], gaps: [], inner: [] };
  const placed = [...page.children].filter(vis);
  for (const e of placed) { const r = R(e); if (near(r.left, L) > 0.5 || near(r.right, Rr) > 0.5) out.offGrid.push(`${name(e)} ${near(r.left, L).toFixed(1)}/${near(r.right, Rr).toFixed(1)}`); }
  for (const col of placed.filter((e) => e.classList.contains('va-col'))) {
    const cr = R(col), kids = [...col.children].filter(vis);
    for (const ch of kids) {
      const r = R(ch);
      if (Math.abs(r.left - cr.left) > 0.5) out.column.push(`${name(ch)} left ${(r.left - cr.left).toFixed(1)}`);
      if (ch.classList.contains('sem-card') && Math.abs(r.right - cr.right) > 0.5) out.column.push(`${name(ch)} right ${(r.right - cr.right).toFixed(1)}`);
    }
    // The head is a row: its first block starts on the column's left edge and its last ends on the right edge.
    for (const head of kids.filter((e) => e.classList.contains('va-head'))) {
      const hk = [...head.children].filter(vis);
      if (hk.length && Math.abs(R(hk[0]).left - cr.left) > 0.5) out.column.push('head first left');
      if (hk.length && Math.abs(R(hk[hk.length - 1]).right - cr.right) > 0.5) out.column.push('head last right');
    }
    for (let i = 1; i < kids.length; i++) {
      const d = R(kids[i]).top - R(kids[i - 1]).bottom;
      if (Math.abs(d - rowGap) > 0.5) out.gaps.push(`${name(kids[i - 1])} to ${name(kids[i])} ${d.toFixed(1)}`);
    }
  }
  const contentEdge = (card) => { const cs = getComputedStyle(card), cr = R(card); return cr.left + px(cs.borderLeftWidth) + px(cs.paddingLeft); };
  for (const card of [...page.querySelectorAll('.sem-card.va-panel, .sem-card.va-empty')].filter(vis)) {
    const cl = contentEdge(card);
    for (const ch of [...card.children].filter(vis)) { const r = R(ch); if (Math.abs(r.left - cl) > 0.5) out.inner.push(`${name(card)} > ${name(ch)} left ${(r.left - cl).toFixed(1)}`); }
  }
  // An opened check: its content sits on one left edge, the edge of the name column.
  for (const det of [...page.querySelectorAll('.va-detail')].filter(vis)) {
    const cs = getComputedStyle(det), cl = R(det).left + px(cs.paddingLeft);
    for (const ch of [...det.children].filter(vis)) { const r = R(ch); if (Math.abs(r.left - cl) > 0.5) out.inner.push(`${name(det)} > ${name(ch)} left ${(r.left - cl).toFixed(1)}`); }
  }
  // The name of every line starts on one edge.
  const names = [...page.querySelectorAll('.va-row .va-name')].filter(vis).map((e) => R(e).left);
  if (names.length && Math.max(...names) - Math.min(...names) > 0.5) out.inner.push('row names start on different edges');
  return { faults: out.offGrid.length + out.column.length + out.gaps.length + out.inner.length, ...out };
}
