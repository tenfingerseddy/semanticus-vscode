// Alignment, run in the page: the placed blocks of .cal-page start and end on the 12 column lines; the blocks inside a placed
// column share its left and right edges; a card's direct content starts at its content edge; inside a ladder every level name
// starts on one line and every column chip on another.
() => {
  const page = document.querySelector('.cal-page');
  if (!page) return null;
  const px = (v) => parseFloat(v) || 0;
  const R = (e) => e.getBoundingClientRect();
  const vis = (e) => { const s = getComputedStyle(e); const r = R(e); return s.display !== 'none' && s.position !== 'fixed' && s.position !== 'absolute' && r.width > 0 && r.height > 0; };
  const ps = getComputedStyle(page), pr = R(page);
  const left = pr.left + px(ps.paddingLeft), right = pr.right - px(ps.paddingRight), gap = px(ps.columnGap);
  const colw = (right - left - 11 * gap) / 12;
  const L = Array.from({ length: 12 }, (_, k) => left + k * (colw + gap)), Rr = Array.from({ length: 12 }, (_, k) => left + (k + 1) * colw + k * gap);
  const near = (v, xs) => Math.min(...xs.map((x) => Math.abs(x - v)));
  const name = (e) => (e.getAttribute('data-testid') || String(e.className).split(' ').filter((c) => c.startsWith('cal-') || c.startsWith('sem-')).slice(0, 2).join('.') || e.tagName).slice(0, 40);
  const out = { offGrid: [], column: [], inner: [] };
  const wide = window.innerWidth <= 900;
  const placed = [...page.children].filter(vis).filter((e) => e.classList.contains('cal-main') || e.classList.contains('cal-side') || e.classList.contains('cal-wide') || e.classList.contains('sem-card'));
  for (const e of placed) { const r = R(e); if (near(r.left, L) > 0.5 || near(r.right, Rr) > 0.5) out.offGrid.push(`${name(e)} ${near(r.left, L).toFixed(1)}/${near(r.right, Rr).toFixed(1)}`); }
  for (const col of placed.filter((e) => e.classList.contains('cal-main') || e.classList.contains('cal-side'))) {
    const cr = R(col);
    for (const ch of [...col.children].filter(vis)) {
      const r = R(ch);
      if (Math.abs(r.left - cr.left) > 0.5) out.column.push(`${name(col)} > ${name(ch)} left ${(r.left - cr.left).toFixed(1)}`);
      if (ch.classList.contains('sem-card') && Math.abs(r.right - cr.right) > 0.5) out.column.push(`${name(col)} > ${name(ch)} right ${(r.right - cr.right).toFixed(1)}`);
    }
  }
  for (const card of [...page.querySelectorAll('.sem-card:not(.sem-card-flush)')].filter(vis)) {
    const cs = getComputedStyle(card), cr = R(card);
    const cl = cr.left + px(cs.borderLeftWidth) + px(cs.paddingLeft);
    for (const ch of [...card.children].filter(vis)) { const r = R(ch); if (Math.abs(r.left - cl) > 0.5) out.inner.push(`${name(card)} > ${name(ch)} left ${(r.left - cl).toFixed(1)}`); }
  }
  for (const lad of [...page.querySelectorAll('.cal-ladder')].filter(vis)) {
    for (const cls of ['.cal-level', '.cal-rung > .cal-col, .cal-rung > .cal-rel']) {
      const xs = [...lad.querySelectorAll(cls)].filter(vis).map((e) => Math.round(R(e).left * 2) / 2);
      if (xs.length && Math.max(...xs) - Math.min(...xs) > 0.5) out.inner.push(`${name(lad)} ${cls.split(',')[0]} spread ${(Math.max(...xs) - Math.min(...xs)).toFixed(1)}`);
    }
  }
  void wide;
  return { faults: out.offGrid.length + out.column.length + out.inner.length, ...out };
}
