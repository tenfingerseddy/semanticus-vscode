// Alignment measure for the Query notebook (run in the page, page.evaluate). Written for option C (16 Oct), replacing the card-grid measure.
// The head and the notebook card must start and end on the page's 12-column lines to half a pixel; every row of the notebook must start its
// text, end its number and end its time on the same three lines; the open row's tabs and view must start on the row's text edge; the gap
// between the head, the timing line and the notebook is the token gap. Returns counts plus the first offenders.
() => {
  const page = document.querySelector('.qn-page');
  if (!page) return null;
  const px = (v) => parseFloat(v) || 0;
  const pcs = getComputedStyle(page), pr = page.getBoundingClientRect();
  const left = pr.left + px(pcs.paddingLeft), right = pr.right - px(pcs.paddingRight);
  const gap = px(pcs.columnGap), rowGap = px(pcs.rowGap);
  const colw = (right - left - 11 * gap) / 12;
  const lines = Array.from({ length: 12 }, (_, k) => ({ l: left + k * (colw + gap), r: left + (k + 1) * colw + k * gap }));
  const near = (v, xs) => Math.min(...xs.map((x) => Math.abs(x - v)));
  const R = (e) => e.getBoundingClientRect();
  const vis = (e) => { const s = getComputedStyle(e); const r = R(e); return s.display !== 'none' && r.width > 0 && r.height > 0; };
  const name = (e) => (e.getAttribute('data-testid') || String(e.className).split(' ').slice(0, 2).join('.')).slice(0, 40);
  const out = { blocks: 0, rows: 0, colw: +colw.toFixed(2), gap, offGrid: [], ragged: [], gaps: [], inner: [] };
  const blocks = [...page.children].filter((e) => vis(e) && !e.classList.contains('sr-only') && (e.classList.contains('qn-head') || e.classList.contains('qn-book') || e.classList.contains('qn-timing')));
  out.blocks = blocks.length;
  for (const e of blocks) {
    const r = R(e), dl = near(r.left, lines.map((x) => x.l)), dr = near(r.right, lines.map((x) => x.r));
    if (dl > 0.5 || dr > 0.5) out.offGrid.push(`${name(e)} left ${dl.toFixed(1)} right ${dr.toFixed(1)}`);
  }
  for (let i = 1; i < blocks.length; i++) {
    const d = R(blocks[i]).top - R(blocks[i - 1]).bottom;
    if (Math.abs(d - rowGap) > 0.5) out.gaps.push(`${name(blocks[i - 1])} to ${name(blocks[i])} gap ${d.toFixed(1)}`);
  }
  // The head's controls share one centre line.
  const head = page.querySelector('.qn-head');
  if (head && vis(head)) {
    const kids = [...head.children].filter(vis).filter((k) => !k.classList.contains('qn-grow'));
    const mids = kids.map((k) => R(k).top + R(k).height / 2);
    if (mids.length > 1 && Math.max(...mids) - Math.min(...mids) > 0.5) out.ragged.push(`head controls differ in centre line by ${(Math.max(...mids) - Math.min(...mids)).toFixed(1)}`);
  }
  // Every row: text, number and time on the same three lines.
  const lineEls = [...page.querySelectorAll('.qn-line')].filter(vis);
  out.rows = lineEls.length;
  const edge = (sel, side) => lineEls.map((l) => { const e = l.querySelector(sel); return e && vis(e) ? R(e)[side] : null; }).filter((v) => v !== null);
  for (const [sel, side, label] of [['.qn-line-text', 'left', 'text left'], ['.qn-line-n', 'right', 'number right'], ['.qn-line-t', 'right', 'time right'], ['.qn-row-acts', 'right', 'actions right'], ['.qn-chev', 'left', 'chevron left']]) {
    const v = edge(sel, side);
    if (v.length > 1 && Math.max(...v) - Math.min(...v) > 0.5) out.ragged.push(`${label} differs by ${(Math.max(...v) - Math.min(...v)).toFixed(1)} across ${v.length} rows`);
  }
  // The open row: its tabs and its view start where the row's text starts; nothing in the view starts left of that.
  const body = page.querySelector('.qn-body');
  const textLeft = lineEls.length ? (edge('.qn-line-text', 'left')[0] ?? null) : null;
  if (body && vis(body) && textLeft !== null) {
    const bcs = getComputedStyle(body);
    const content = R(body).left + px(bcs.paddingLeft);
    if (Math.abs(content - textLeft) > 0.5) out.inner.push(`body content starts ${(content - textLeft).toFixed(1)} from the row text`);
    for (const ch of [...body.querySelectorAll(':scope > .qn-tabs, :scope > .qn-view > *')].filter(vis)) {
      const d = R(ch).left - textLeft;
      if (Math.abs(d) > 0.5) out.inner.push(`${name(ch)} starts ${d.toFixed(1)} from the row text`);
      if (R(ch).right - (R(body).right - px(bcs.paddingRight)) > 0.5) out.inner.push(`${name(ch)} ends past the body`);
    }
  }
  return out;
}
