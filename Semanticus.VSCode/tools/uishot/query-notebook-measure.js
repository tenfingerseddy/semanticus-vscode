// First-view measure for the Query notebook, run in the page (page.evaluate with a root selector and target selectors). Counts what a person sees
// before scrolling: words and controls of the page body (Studio chrome excluded), the page scroll range, and how far a target sits below the fold.
// Same counting rules as the m16 minimal prototype (tools/minimal-proto/measure.js), so the numbers compare.
({ root, targets }) => {
  const el = document.querySelector(root);
  if (!el) return null;
  let sc = el;
  while (sc && sc !== document.body) { const s = getComputedStyle(sc); if (/(auto|scroll)/.test(s.overflowY) && sc.scrollHeight > sc.clientHeight + 2) break; sc = sc.parentElement; }
  if (!sc || sc === document.body) sc = document.scrollingElement;
  const sr = sc === document.scrollingElement ? { top: 0, bottom: innerHeight, left: 0, right: innerWidth } : sc.getBoundingClientRect();
  const view = { top: Math.max(0, sr.top), bottom: Math.min(innerHeight, sr.bottom), left: 0, right: innerWidth };
  const shown = (r) => r.width > 0 && r.height > 0 && r.bottom > view.top + 1 && r.top < view.bottom - 1 && r.right > 0 && r.left < innerWidth;
  const visible = (e) => { for (let x = e; x && x !== document.body; x = x.parentElement) { const s = getComputedStyle(x); if (s.display === 'none' || s.visibility === 'hidden' || +s.opacity === 0) return false; } return true; };
  let words = 0;
  const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
  const range = document.createRange();
  for (let n = w.nextNode(); n; n = w.nextNode()) {
    const t = n.textContent.trim(); if (!t || !n.parentElement || !visible(n.parentElement)) continue;
    if (n.parentElement.closest('svg, .sr-only, [aria-hidden="true"]')) { if (!n.parentElement.closest('svg text')) continue; }
    range.selectNodeContents(n);
    if (![...range.getClientRects()].some(shown)) continue;
    words += t.split(/\s+/).filter((x) => /[\p{L}\p{N}]/u.test(x)).length;
  }
  const ctrls = [...el.querySelectorAll('button, input, select, textarea, a[href], [role="button"], [role="tab"], [contenteditable="true"]')]
    .filter((c) => visible(c) && shown(c.getBoundingClientRect()) && !c.disabled);
  const out = { words, controls: ctrls.length, scrollRange: Math.max(0, Math.round(sc.scrollHeight - sc.clientHeight)), viewH: Math.round(view.bottom - view.top), targets: {} };
  for (const [k, sel] of Object.entries(targets || {})) {
    const t = sel.endsWith('::last') ? [...document.querySelectorAll(sel.slice(0, -6))].pop() : document.querySelector(sel);
    if (!t) { out.targets[k] = null; continue; }
    const r = t.getBoundingClientRect();
    out.targets[k] = { bottom: Math.round(r.bottom - view.top + sc.scrollTop), scrollToSee: Math.max(0, Math.round(r.bottom - view.bottom)) };
  }
  out.sideways = document.documentElement.scrollWidth > document.documentElement.clientWidth + 1;
  return out;
}
