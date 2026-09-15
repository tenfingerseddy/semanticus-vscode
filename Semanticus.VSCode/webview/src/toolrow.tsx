import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';

// ===================================================================================================
// THE ONE TOOL ROW.
//
// Kane's rule, accepted 2026-09-14 from the "Compact top" proposal: every tool gets ONE 40px row of
// controls. Hints leave the row and become a tooltip plus a line in the page notes. Counts, legends and
// status lines become quiet chips INSIDE the canvas, never a row of their own.
//
// Measured in the built app at 1366x768 before this existed: Model > Diagram spent 121px on three rows of
// its own chrome and gave the canvas 450px of a 768px window; Lineage spent 284px (Tree, six rows) and
// 316px (Graph, seven rows) and left canvases 219px and 200px tall. A row of chrome costs the same on
// every screen; the canvas is what shrinks.
//
// Two rules make "one row" real rather than aspirational:
//   1. The row REFUSES to wrap (flex-wrap: nowrap in styles.css). A control that does not fit cannot fall
//      to a second line, so the only way to fit is to fold.
//   2. A group that does not fit folds into a MENU before the row runs out of room. `useToolRow` measures
//      the row itself (not the window) and reports `narrow`, because the row's width is what the controls
//      actually get — a side panel or a narrower editor column changes it without the window moving.
// ===================================================================================================

// Below this row width the foldable groups collapse into menus. Chosen from the measured natural widths of
// the Diagram and Lineage rows: both fit at 1366 and neither fits at 1000 with every group spelled out.
export const TOOL_ROW_FOLD_WIDTH = 1000;
// A canvas shorter than this has room for one chip, not two. The second chip folds into the first one's
// tooltip rather than stacking chips over the picture the person came to look at.
export const CANVAS_CHIP_MIN_H = 260;

// A callback ref plus the element's measured width. It is a CALLBACK ref, not a RefObject, so the observer
// attaches the instant the element does: these rows render before the model graph arrives, and a mount
// effect that reads a ref too early gives up and never measures again.
export function useMeasuredWidth(): [(el: HTMLElement | null) => void, number] {
  const [w, setW] = useState(0);
  const ro = useRef<ResizeObserver | null>(null);
  const ref = useCallback((el: HTMLElement | null) => {
    ro.current?.disconnect();
    ro.current = null;
    if (!el) return;
    const read = () => setW((prev) => { const next = Math.round(el.getBoundingClientRect().width); return Math.abs(prev - next) > 1 ? next : prev; });
    read();
    const obs = new ResizeObserver(read);
    obs.observe(el);
    ro.current = obs;
  }, []);
  useEffect(() => () => ro.current?.disconnect(), []);
  return [ref, w];
}

// The canvas box, as a PERSON sees it.
//
// `height` is the VISIBLE height and `bottomInset` is how far the canvas runs past the bottom of the area it
// is shown in. They are not the same as the element's own box, and the difference is not theoretical: measured
// in the built app at 1366x768, the Diagram canvas element ends at y=767 while the scrolling area holding it
// ends at y=731, so its last 36px sit behind the bottom bar. React Flow's own zoom cluster already loses its
// third button to that strip. A chip pinned to `bottom: 12px` of the element would be pinned to a strip nobody
// can see, so the chips are offset by the inset instead, and that offset becomes 0 by itself on the day the
// page frame stops overflowing.
// How far an element runs past the bottom of the area it is shown in, and where it stops being visible.
// Shared so every canvas answers the question the same way.
export function visibleBottom(el: HTMLElement): number {
  const r = el.getBoundingClientRect();
  let host: HTMLElement | null = el.parentElement;
  let limit = window.innerHeight;
  while (host) {
    const s = getComputedStyle(host);
    if (/(auto|scroll|hidden|clip)/.test(s.overflowY)) { limit = Math.min(limit, host.getBoundingClientRect().bottom - (parseFloat(s.paddingBottom) || 0)); break; }
    host = host.parentElement;
  }
  return Math.min(r.bottom, limit, window.innerHeight);
}
export function visibleBottomInset(el: HTMLElement): number {
  return Math.max(0, Math.round(el.getBoundingClientRect().bottom - visibleBottom(el)));
}

export function useCanvasBox(): [(el: HTMLElement | null) => void, { height: number; bottomInset: number }] {
  const [box, setBox] = useState({ height: 0, bottomInset: 0 });
  const el = useRef<HTMLElement | null>(null);
  const ro = useRef<ResizeObserver | null>(null);

  const read = useCallback(() => {
    const node = el.current;
    if (!node) return;
    const r = node.getBoundingClientRect();
    const bottom = visibleBottom(node);
    const next = { height: Math.max(0, Math.round(bottom - r.top)), bottomInset: Math.max(0, Math.round(r.bottom - bottom)) };
    setBox((prev) => (Math.abs(prev.height - next.height) > 1 || Math.abs(prev.bottomInset - next.bottomInset) > 1 ? next : prev));
  }, []);

  const ref = useCallback((node: HTMLElement | null) => {
    ro.current?.disconnect();
    ro.current = null;
    el.current = node;
    if (!node) return;
    read();
    const obs = new ResizeObserver(read);
    obs.observe(node);
    ro.current = obs;
  }, [read]);

  // A resize changes the box; a scroll of the host moves it without resizing anything, so both are watched.
  useEffect(() => {
    const onAny = () => read();
    window.addEventListener('resize', onAny);
    window.addEventListener('scroll', onAny, true);
    return () => { window.removeEventListener('resize', onAny); window.removeEventListener('scroll', onAny, true); ro.current?.disconnect(); };
  }, [read]);

  return [ref, box];
}

// The inset as an inline custom property, so the shared chip classes offset themselves in CSS.
export function canvasInsetStyle(bottomInset: number): React.CSSProperties {
  return { ['--sem-canvas-inset' as string]: `${bottomInset}px` } as React.CSSProperties;
}

// The row's own measurement, and the two decisions that come out of it.
//
// `narrow` is the DECLARED breakpoint: below TOOL_ROW_FOLD_WIDTH a row folds whether or not it happened to
// fit, so the same screen width always gives the same row. `level` is the MEASURED one: it counts up while
// the controls actually on the row are wider than the room they have, so a row can never be one control too
// long for a screen nobody measured. A tool folds its groups in priority order, lowest value first.
//
// Why measure the children rather than read scrollWidth: the row clips on the X axis, which means it is not a
// scroll container, so scrollWidth would report the clipped width and the overflow would be invisible to us.
// Children are flex: 0 0 auto, so each one's box IS its natural width, and their sum is what the row needs.
export function useToolRow(maxFold = 1): { ref: (el: HTMLElement | null) => void; width: number; narrow: boolean; level: number } {
  const el = useRef<HTMLElement | null>(null);
  const [width, setWidth] = useState(0);
  const [level, setLevel] = useState(0);

  const ref = useCallback((node: HTMLElement | null) => { el.current = node; if (node) setWidth(Math.round(node.getBoundingClientRect().width)); }, []);

  useEffect(() => {
    const node = el.current;
    if (!node) return;
    // A width change starts the decision again from nothing folded, so the row unfolds when there is room.
    const obs = new ResizeObserver(() => {
      const next = Math.round(node.getBoundingClientRect().width);
      setWidth((prev) => { if (Math.abs(prev - next) <= 1) return prev; setLevel(0); return next; });
    });
    obs.observe(node);
    return () => obs.disconnect();
  }, [width > 0]);   // re-attach once the element exists

  useLayoutEffect(() => {
    const node = el.current;
    if (!node || level >= maxFold) return;
    const style = getComputedStyle(node);
    const pad = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
    const gap = parseFloat(style.columnGap || style.gap) || 0;
    const kids = [...node.children] as HTMLElement[];
    const need = kids.reduce((sum, k) => sum + k.getBoundingClientRect().width, 0) + gap * Math.max(0, kids.length - 1) + pad;
    if (need > node.getBoundingClientRect().width + 1) setLevel((n) => (n < maxFold ? n + 1 : n));
  });

  // width 0 = not measured yet. Treat that as wide so the first paint shows the full row and folds only if the
  // measurement says so; folding first and unfolding after would flash a menu onto every wide screen.
  //
  // `narrow` is true on EITHER signal: the declared breakpoint, so the same width always gives the same row,
  // or the measurement, so a row that is one control too long for a screen nobody thought about folds anyway.
  return { ref, width, narrow: (width > 0 && width < TOOL_ROW_FOLD_WIDTH) || level >= 1, level };
}

export function ToolRow({ rowRef, children }: { rowRef?: (el: HTMLElement | null) => void; children: React.ReactNode }) {
  return <div className="sem-toolrow" ref={rowRef as React.Ref<HTMLDivElement>}>{children}</div>;
}

// A hairline between groups of controls. Decoration only, so it is hidden from a screen reader.
export function RowSep() {
  return <span className="sem-toolrow-sep" aria-hidden />;
}

// The quiet word that names the group after it ("Show"). Not a control.
export function RowLabel({ children }: { children: React.ReactNode }) {
  return <span className="sem-toolrow-label">{children}</span>;
}

// ONE COORDINATE SPACE FOR EVERY ANCHORED POPOVER.
//
// Studio's zoom is CSS `zoom` on .studio-root, so the whole app is drawn at 80, 100 or 130 percent.
// getBoundingClientRect() answers in SCREEN pixels (already scaled). A position:fixed element inside that
// zoomed root reads its own left/top in the root's LOCAL pixels, which the browser then multiplies by the zoom
// again. Measuring in one space and writing in the other scales the position twice.
//
// Measured 2026-09-14 in the built app, Model > Lineage > Tree > Show at 1000px and 130%: inline left 651.8px
// landed at screen x=847.3, the menu ended at x=1065.7 in a 1000px viewport (65.7px of it off-screen), and it
// sat 46.7px below its button instead of 5px. At 80% the same menu opened 14.6px ABOVE the button and 540px to
// its left. At 100%, where the two spaces are the same, it had always been right.
//
// So: read the effective zoom once, divide every measurement by it, and clamp against the zoomed root's own
// box divided the same way. Shared, because the Create segment's menu on the area row has the same job.

// The effective CSS zoom at this element: the browser's own reading where it offers one, otherwise the product
// of the zoom on this element and its ancestors. Never returns 0.
export function cssZoomFactor(el: Element | null | undefined): number {
  if (!el) return 1;
  const measured = (el as HTMLElement & { currentCSSZoom?: number }).currentCSSZoom;
  if (typeof measured === 'number' && measured > 0) return measured;
  let factor = 1;
  for (let node: Element | null = el; node; node = node.parentElement) {
    const own = parseFloat(getComputedStyle(node).zoom);
    if (own > 0) factor *= own;
  }
  return factor > 0 ? factor : 1;
}

// Where a fixed popover should sit to be directly under its trigger and wholly inside the Studio root, in the
// CSS pixels a fixed child of that root is written in. `popup` may be unmeasured on the first pass; 220 is the
// width to assume until it has a box of its own.
export function anchorUnder(trigger: Element | null | undefined, popup: Element | null | undefined,
  align: 'start' | 'end' = 'start'): { left: number; top: number } | null {
  if (!trigger) return null;
  const z = cssZoomFactor(trigger);
  const b = trigger.getBoundingClientRect();
  const root = trigger.closest('.studio-root');
  const bounds = root ? root.getBoundingClientRect() : null;
  const minLeft = (bounds ? bounds.left : 0) / z + 8;
  const maxRight = (bounds ? bounds.right : window.innerWidth) / z - 8;
  const width = popup ? popup.getBoundingClientRect().width / z : 220;
  const wanted = align === 'end' ? b.right / z - width : b.left / z;
  return { left: Math.max(minLeft, Math.min(wanted, maxRight - width)), top: b.bottom / z + 4 };
}

// A group of controls folded behind one button. The popover is position:fixed and placed against the
// button's own box, because the row clips horizontally (that is what stops a long row from pushing the page
// sideways) and an absolutely-positioned popover inside it would be cut off at the row's edge.
export function RowMenu({ label, title, badge, children, align = 'start' }: {
  label: string;
  title?: string;
  badge?: number;
  children: React.ReactNode;
  align?: 'start' | 'end';
}) {
  const [open, setOpen] = useState(false);
  const btn = useRef<HTMLButtonElement>(null);
  const pop = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState<{ left: number; top: number } | null>(null);

  useLayoutEffect(() => {
    if (!open) { setBox(null); return; }
    const placed = anchorUnder(btn.current, pop.current, align);
    if (placed) setBox(placed);
  }, [open, align]);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      const t = e.target as Node;
      if (btn.current?.contains(t) || pop.current?.contains(t)) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { setOpen(false); btn.current?.focus(); } };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDoc); document.removeEventListener('keydown', onKey); };
  }, [open]);

  return (
    <>
      <button ref={btn} className="sem-btn sem-btn-sm" aria-expanded={open} aria-haspopup="menu" title={title}
        onClick={() => setOpen((o) => !o)}>
        {label}
        {badge ? <span className="tnum sem-toolrow-badge">{badge}</span> : null}
        <span aria-hidden style={{ opacity: 0.7 }}>▾</span>
      </button>
      {open && (
        <div ref={pop} role="menu" aria-label={label} className="sem-rowmenu-pop"
          style={box ? { left: box.left, top: box.top } : { left: -9999, top: -9999 }}>
          {children}
        </div>
      )}
    </>
  );
}

// A quiet chip floating inside a canvas. `at="start"` sits at the bottom-left, clear of the zoom controls
// React Flow pins there; `at="end"` sits at the bottom-right. Both read as a note on the picture, not as
// another bar: surface tokens, a 1px border, 11px text.
export function CanvasChip({ at = 'start', title, tone, note, children, ...rest }: {
  at?: 'start' | 'end' | 'top';
  title?: string;
  tone?: 'warn' | 'bad';
  note?: boolean;
  children: React.ReactNode;
} & React.HTMLAttributes<HTMLDivElement>) {
  const cls = ['sem-canvas-chip', `sem-canvas-chip-${at}`];
  if (tone) cls.push(`sem-canvas-chip-${tone}`);
  if (note) cls.push('sem-canvas-chip-note');
  return <div className={cls.join(' ')} title={title} {...rest}>{children}</div>;
}

// Several chips stacked at one corner (the lineage graph puts its selection panel above its counts). The
// stack owns the corner so the chips inside it position themselves normally.
export function CanvasChipStack({ at = 'start', children }: { at?: 'start' | 'end'; children: React.ReactNode }) {
  return <div className={`sem-canvas-chips sem-canvas-chips-${at}`}>{children}</div>;
}
