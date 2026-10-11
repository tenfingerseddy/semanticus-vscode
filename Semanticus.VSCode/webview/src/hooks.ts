import { useCallback, useRef, useState, useEffect, useLayoutEffect, type SetStateAction } from 'react';
import { rpc, copyText, loadState, saveState, onDidChange } from './bridge';

/**
 * A draggable, persisted panel size. Returns the current size (px) and a pointer-down handler to attach to a
 * drag grip; the size survives tab switches + reloads (via usePersistedState). `axis:'x'` resizes width,
 * `'y'` height; `dir` is +1 when dragging right/down grows the panel, -1 when it shrinks it (e.g. a bottom
 * panel grows as the splitter moves UP → dir:-1).
 */
export function useResizable(key: string, initial: number, opts: { axis: 'x' | 'y'; dir?: 1 | -1; min: number; max: number }) {
  const { axis, dir = 1, min, max } = opts;
  const [size, setSize] = usePersistedState(key, initial);
  const sizeRef = useRef(size);
  sizeRef.current = size;
  const onPointerDown = useCallback((e: React.PointerEvent) => {
    e.preventDefault();
    const start = axis === 'x' ? e.clientX : e.clientY;
    const base = sizeRef.current;
    const el = e.currentTarget as HTMLElement;
    el.setPointerCapture(e.pointerId);
    const move = (ev: PointerEvent) => {
      const cur = axis === 'x' ? ev.clientX : ev.clientY;
      setSize(Math.max(min, Math.min(max, base + dir * (cur - start))));
    };
    const up = () => { el.removeEventListener('pointermove', move); el.removeEventListener('pointerup', up); };
    el.addEventListener('pointermove', move); el.addEventListener('pointerup', up);
  }, [axis, dir, min, max, setSize]);
  return [size, onPointerDown, setSize] as const;
}

// Component state that survives Studio tab switches (which UNMOUNT the tab) and webview reloads, by mirroring
// to the webview's persisted state. Used by the code-entry surfaces (DAX Query / Lab / Pivot) so a typed query
// isn't lost when you change tabs. Drop-in replacement for useState.
//
// THE KEY CAN CHANGE WHILE MOUNTED. Pages key their state by the open model (Ask your data, the DAX Lab), and the
// model can change under an open page (an assistant's create_model). The value belongs to its key: a new key reads
// ITS saved value (or the initial one) in the same render, and the old value is never written under the new key.
// It used to read the key once, so the last model's question stayed on screen and was saved as the new model's
// (verify-askdata defect 4).
export function usePersistedState<T>(key: string, initial: T) {
  const [held, setHeld] = useState<{ key: string; v: T }>(() => ({ key, v: loadState('input:' + key, initial) }));
  let current = held;
  if (held.key !== key) {
    current = { key, v: loadState('input:' + key, initial) };
    setHeld(current);
  }
  useEffect(() => { saveState('input:' + current.key, current.v); }, [current.key, current.v]);
  const keyRef = useRef(key);
  keyRef.current = key;
  // A set lands on the key that is current when it is called; one queued for a key that has gone is dropped.
  const setV = useCallback((next: SetStateAction<T>) => {
    const forKey = keyRef.current;
    setHeld((h) => h.key !== forKey ? h
      : { key: h.key, v: typeof next === 'function' ? (next as (prev: T) => T)(h.v) : next });
  }, []);
  return [current.v, setV] as const;
}

type FixState = 'fixing' | 'copied' | 'done';
interface FixPrompt { prompt: string }

/** Drop stale "fixed" ticks. Undo, redo, or any other model change can restore the row; the tick must not stick (D-078). */
export function dropDoneFixState(state: Record<string, FixState>): Record<string, FixState> {
  let changed = false;
  const next: Record<string, FixState> = {};
  for (const [k, v] of Object.entries(state)) {
    if (v === 'done') { changed = true; continue; }
    next[k] = v;
  }
  return changed ? next : state;
}

/**
 * The per-finding fix / ask-Claude state machine shared by the AI-Readiness findings list and the
 * BPA tab — identical except for the two RPC method names. `fix` applies a deterministic safe fix;
 * `ask` copies a grounded remediation prompt for the user's Claude. `keyOf` keys state by ruleId+ref.
 */
export function useFixState(fixMethod: string, promptMethod: string) {
  const [state, setState] = useState<Record<string, FixState>>({});
  useEffect(() => onDidChange(() => setState((s) => dropDoneFixState(s))), []);
  const keyOf = (ruleId: string, objectRef: string) => `${ruleId}::${objectRef}`;

  async function fix(ruleId: string, objectRef: string, afterFix?: () => void) {
    const k = keyOf(ruleId, objectRef);
    setState((s) => ({ ...s, [k]: 'fixing' }));
    try {
      await rpc(fixMethod, ruleId, objectRef, 'human');
      setState((s) => ({ ...s, [k]: 'done' }));
      afterFix?.();
    } catch {
      setState((s) => { const n = { ...s }; delete n[k]; return n; });
    }
  }

  async function ask(ruleId: string, objectRef: string) {
    const k = keyOf(ruleId, objectRef);
    try {
      const fp = await rpc<FixPrompt>(promptMethod, ruleId, objectRef);
      if (await copyText(fp.prompt)) {
        setState((s) => ({ ...s, [k]: 'copied' }));
        window.setTimeout(() => setState((s) => { const n = { ...s }; if (n[k] === 'copied') delete n[k]; return n; }), 1600);
      }
    } catch { /* ignore */ }
  }

  return { state, keyOf, fix, ask };
}

// ---- one best-effort engine read ---------------------------------------------------------------
// Lived in modelhome.tsx until 22 September 2026, when AI instructions became a page of its own and two
// files needed the same three lines. It is a generic hook and this is a generic hook file; nothing about
// it was ever about the Overview.

export type Remote<T> =
  | { status: 'loading' }
  /** The caller decided not to ask. Distinct from loading, which would sit on a spinner for ever, and from
   *  error, which would claim something went wrong. */
  | { status: 'off' }
  | { status: 'error'; message: string }
  | { status: 'ready'; value: T };

export function messageOf(error: unknown) { return error instanceof Error ? error.message : String(error); }

/** One best-effort engine read with its own state. The page never waits on it, and a failure stays inside
 *  the card that asked for it. */
export function useRemote<T>(load: () => Promise<T>, keys: unknown[], skip?: boolean): [Remote<T>, () => void] {
  const [state, setState] = useState<Remote<T>>({ status: 'loading' });
  const [retry, setRetry] = useState(0);
  const generation = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  useEffect(() => {
    const mine = ++generation.current;
    // SUPPRESSED, not filtered. A card that belongs to a feature this plan does not reach must not make the
    // call at all: hiding the answer still spends the round trip and still asks the engine for paid data.
    if (skip) { setState({ status: 'off' }); return; }
    setState({ status: 'loading' });
    loadRef.current().then(
      (value) => { if (mine === generation.current) setState({ status: 'ready', value }); },
      (error: unknown) => { if (mine === generation.current) setState({ status: 'error', message: messageOf(error) }); },
    );
    return () => { if (generation.current === mine) generation.current++; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...keys, retry, skip]);
  return [state, useCallback(() => setRetry((n) => n + 1), [])];
}

/** How many `getBoundingClientRect` pixels one of this element's own CSS pixels takes, MEASURED and never assumed.
 *
 *  Two zoom models are in use and they disagree about the Studio text size (Ctrl + wheel, which is CSS zoom on the
 *  app root). VS Code starts its Chromium with standardized zoom switched off (its `out/main.js` passes
 *  `--disable-blink-features=...StandardizedBrowserZoom`), so inside a VS Code webview a rect is already in the
 *  element's own pixels, `currentCSSZoom` does not exist, and the answer here is 1 at every text size. A Chromium
 *  with standardized zoom on (the headless harness, and Chrome itself) scales a rect by every zoom above the element.
 *  Reading the zoom and dividing by it is right in the second model and wrong in the one that ships: in a real VS Code
 *  window at 80% the window-sized pages ran 127 px past the page bottom that way, and at 120% stopped 159 px short.
 *  The element's rect over its own laid-out width is right in both, which is why it is measured. */
function scaleOf(el: Element): number {
  const cs = getComputedStyle(el);
  let own = parseFloat(cs.width);
  if (cs.boxSizing !== 'border-box') {
    own += (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0)
      + (parseFloat(cs.borderLeftWidth) || 0) + (parseFloat(cs.borderRightWidth) || 0);
  }
  const rect = el.getBoundingClientRect().width;
  return own > 0 && rect > 0 ? rect / own : 1;
}

/** Where a box's top would be if it were not stuck. A `position: sticky` box stops moving once it sticks, so its
 *  rect top no longer says how far down the page it starts; read with it unstuck for the one measurement, which moves
 *  nothing on screen because both are in the flow and the frame is not painted in between. Static and not relative,
 *  because a relative box still honours its `top`. */
function naturalTop(el: HTMLElement): number {
  if (getComputedStyle(el).position !== 'sticky') return el.getBoundingClientRect().top;
  const was = el.style.position;
  el.style.position = 'static';
  const top = el.getBoundingClientRect().top;
  el.style.position = was;
  return top;
}

/** The space rule (27 September 2026): a list that must scroll inside itself runs to the bottom of the page instead
 *  of stopping at a fixed height with the page empty under it. Returns a ref for the box and the height that takes
 *  it to the bottom of the page's scroll region (less the page margin and `inset`, the card padding under it), never
 *  less than `floor`. Re-measured when the window or the box's own column changes size, and when `key` changes (pass
 *  something that changes when the box first mounts, if it mounts after the page does).
 *
 *  The height is the one the box needs with the page at its top, whatever the page is scrolled to when it is taken.
 *  A sticky box (Docs' settings and preview) is re-measured whenever the page's content grows, which is usually while
 *  the page is scrolled, and measuring where it had stuck took every scrolled pixel off its height: a long narrative
 *  left both columns at their 240 px floor with 500 to 750 px of empty page under them, and the settings column hid
 *  its own Reset to defaults in a box that scrolled. */
export function useFillHeight<T extends HTMLElement>(floor: number, inset = 16, key: unknown = null): [React.RefObject<T | null>, number] {
  const ref = useRef<T | null>(null);
  const [height, setHeight] = useState(floor);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const main = el.closest('main');
      if (!main) return;
      const ms = getComputedStyle(main);
      const margin = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--sem-page-margin')) || 24;
      // Only the distance is a rect; the padding, the margin, the inset and scrollTop are the box's own pixels.
      const gap = (main.getBoundingClientRect().bottom - naturalTop(el)) / scaleOf(el);
      setHeight(Math.max(floor, Math.floor(gap - main.scrollTop - (parseFloat(ms.paddingBottom) || 0) - margin - inset)));
    };
    measure();
    window.addEventListener('resize', measure);
    const ro = new ResizeObserver(measure);
    if (el.parentElement) ro.observe(el.parentElement);
    return () => { window.removeEventListener('resize', measure); ro.disconnect(); };
  }, [floor, inset, key]);
  return [ref, height];
}

/** The properties that decide where a text wraps. Copied onto the twin so it breaks its lines exactly where the
 *  real box does; everything else about the twin is set by `twinHeight` itself. */
const WRAP_STYLES = ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant', 'fontStretch', 'lineHeight',
  'letterSpacing', 'wordSpacing', 'textTransform', 'textIndent', 'whiteSpace', 'wordBreak', 'overflowWrap',
  'tabSize', 'direction', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft'] as const;

let twin: HTMLTextAreaElement | null = null;

/** The width the box's text wraps in, unrounded. `clientWidth` is a whole pixel and the box is not: a 3-of-12 column
 *  in a 1530 px Studio is 327.5 px, and a twin half a pixel wider than the text it stands for breaks its lines in
 *  other places (a real VS Code window cut 40 px off a Docs narrative that way). The box's own scrollbar is left in on
 *  purpose: it only shows while the box is at its cap, and a box given less than its cap has none. */
function textWidth(el: HTMLTextAreaElement, cs: CSSStyleDeclaration): number {
  const w = parseFloat(cs.width);
  if (cs.boxSizing !== 'border-box') return w;
  return w - (parseFloat(cs.borderLeftWidth) || 0) - (parseFloat(cs.borderRightWidth) || 0)
    - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
}

/** Every CSS zoom from the box up to the body, so the twin (which lives in the body) lays its text out at the size
 *  the box does. Line breaks are not quite proportional across font sizes, so a twin at 13 px standing for a box
 *  drawn at 15.6 px could break a line somewhere else. */
function zoomAbove(el: Element): number {
  let z = 1;
  for (let a: Element | null = el; a && a !== document.body; a = a.parentElement) {
    const v = parseFloat(getComputedStyle(a).zoom);
    if (Number.isFinite(v) && v > 0) z *= v;
  }
  return z;
}

/** The height this text needs at `inner` px of room, measured on a hidden twin, in the same units a `scrollHeight`
 *  on the real box would report: its content plus its padding, and never less than `rows` lines, which is the floor
 *  a box's `rows` promise (an empty AI instructions editor is 12 rows, an empty Docs narrative box 4).
 *
 *  A twin, and never the box itself, because reading the box's own content height means collapsing it first, and
 *  that is the fault this replaces. The twin is a textarea rather than a div so a trailing empty line, soft wrapping
 *  and the last line's descender are the browser's own answer and not an approximation of it. */
function twinHeight(el: HTMLTextAreaElement, cs: CSSStyleDeclaration, inner: number, value: string, rows: number): number {
  twin ??= document.createElement('textarea');
  twin.setAttribute('aria-hidden', 'true');
  twin.tabIndex = -1;
  twin.readOnly = true;
  twin.wrap = el.wrap || 'soft';
  twin.rows = rows;
  twin.style.cssText = 'position:absolute;top:0;left:-99999px;visibility:hidden;overflow:hidden;resize:none;'
    + 'border:0;height:auto;min-height:0;max-height:none;box-sizing:content-box;';
  for (const p of WRAP_STYLES) twin.style[p] = cs[p];
  twin.style.zoom = String(zoomAbove(el));
  twin.style.width = `${inner}px`;
  twin.value = value;
  document.body.appendChild(twin);
  const h = twin.scrollHeight;
  twin.remove();
  return h;
}

/** The height the box must have for its whole text to show at the width the box has RIGHT NOW, never less than its
 *  rows. Returns 0 when the box has no width at all (it is hidden, or not laid out yet): "leave the height alone". */
function neededHeight(el: HTMLTextAreaElement, cs: CSSStyleDeclaration): number {
  const inner = textWidth(el, cs);
  if (!(inner > 0)) return 0;
  const text = twinHeight(el, cs, inner, el.value, el.rows || 2);
  if (cs.boxSizing === 'border-box') return text + (parseFloat(cs.borderTopWidth) || 0) + (parseFloat(cs.borderBottomWidth) || 0);
  return text - (parseFloat(cs.paddingTop) || 0) - (parseFloat(cs.paddingBottom) || 0);
}

/** Grow the box until it hides none of its text, by the box's OWN answer: `scrollHeight` past `clientHeight` is
 *  exactly the text it is hiding, at the width it has now, so nothing here is estimated. Giving the box its height
 *  can bring a scrollbar back around it and narrow it, and a narrower box needs more height, so this goes round until
 *  nothing is hidden. It stops at the box's cap (`max-height`), where scrolling itself is what the cap is for. */
function growToText(el: HTMLTextAreaElement, cs: CSSStyleDeclaration): void {
  for (let pass = 0; pass < 4; pass++) {
    const hidden = el.scrollHeight - el.clientHeight;
    if (hidden <= 0) return;
    const was = el.offsetHeight;
    el.style.height = `${parseFloat(cs.height) + hidden}px`;
    if (el.offsetHeight <= was) return;
  }
}

/** Whether `next` is `prev` with something inserted at one place, the shape of every key typed and every paste. */
function isInsertion(prev: string, next: string): boolean {
  const n = prev.length;
  if (next.length < n) return false;
  let head = 0;
  while (head < n && prev.charCodeAt(head) === next.charCodeAt(head)) head++;
  let tail = 0;
  while (tail < n - head && prev.charCodeAt(n - 1 - tail) === next.charCodeAt(next.length - 1 - tail)) tail++;
  return head + tail === n;
}

/** Bring the caret back into view when the fit has left it outside, and only then.
 *
 *  Holding the scroll positions is what keeps the page still while a person types, and it is also what stops the
 *  browser following the caret: pasting a long text into an empty box left the pane at its top with the caret 600 px
 *  below anything visible, and typing went on out of sight until the person scrolled. The browser follows the caret
 *  itself before the fit runs, so this is called only when the fit changed the box's height, and it moves an
 *  ancestor by the least that brings the caret back. A caret already in view moves nothing, which is why typing at the
 *  end of a long text still does not move the page. Rects and the box's own pixels are kept apart with `scaleOf`,
 *  which is what makes this right in both zoom models. */
function followCaret(el: HTMLTextAreaElement, cs: CSSStyleDeclaration): void {
  if (document.activeElement !== el) return;
  const caret = el.selectionEnd ?? el.value.length;
  const line = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) || 16;
  const padTop = parseFloat(cs.paddingTop) || 0;
  const padY = padTop + (parseFloat(cs.paddingBottom) || 0);
  // How far down the box the caret's line ends, in the box's own pixels. At the end of the text that is the whole
  // text, which the box itself reports (at most its rows when the text is shorter than them, and bringing the whole
  // box into view is right then). Anywhere else it is the text before the caret, measured on the twin at one row.
  let above: number;
  if (caret >= el.value.length) above = el.scrollHeight - padY;
  else {
    const inner = textWidth(el, cs);
    if (!(inner > 0)) return;
    above = twinHeight(el, cs, inner, el.value.slice(0, caret), 1) - padY;
  }
  const scale = scaleOf(el);
  const bottom = el.getBoundingClientRect().top + ((parseFloat(cs.borderTopWidth) || 0) + padTop + above) * scale;
  const top = bottom - line * scale;
  for (let a: Element | null = el.parentElement; a && a !== document.documentElement; a = a.parentElement) {
    if (!/(auto|scroll)/.test(getComputedStyle(a).overflowY) || a.scrollHeight <= a.clientHeight) continue;
    const r = a.getBoundingClientRect();
    const own = scaleOf(a);
    if (bottom > r.bottom) a.scrollTop += (bottom - r.bottom) / own;
    else if (top < r.top) a.scrollTop -= (r.top - top) / own;
  }
}

/** Size a textarea to its text without moving the page underneath it, and without ever leaving it shorter than its
 *  text or shorter than its rows.
 *
 *  THE BOX IS NEVER COLLAPSED TO BE MEASURED. `height: auto` used to be how its content height was read, and that is
 *  a trap twice over. It moves the page: for that instant the box is short, every scrolling ancestor loses that much
 *  scroll range, the browser clamps its position to the new smaller maximum, and restoring the height does not
 *  restore the positions, so a person typing at the end of a long text watched the page jump up on every key. And in
 *  a real window it cuts the text off: while the box is short the column or pane around it stops overflowing and
 *  gives back its scrollbar's width (17 to 21 px in VS Code), so the text is measured that much WIDER than it will
 *  be and the height that comes back is too small. These boxes hide their overflow, so the end of the text is then
 *  simply gone with no scrollbar to reach it: 60 px of the AI instructions editor and 800 px of a Docs narrative,
 *  measured in a real VS Code window on 27 September 2026.
 *
 *  So a box only ever GROWS by its own answer (`growToText`), which is exact and costs no more than the layout the
 *  browser does for the key anyway. It is exact at the width the box has when it is fitted and at no other, so
 *  `useAutoGrow` re-fits on any width other than the one of its last fit: round 4 compared with the observer's
 *  previous report instead, and a box narrowed before that report kept its wider height with the end of its text cut
 *  off. It is measured on the hidden twin only when it may need to SHRINK (`remeasure`: a cut or a deleted selection,
 *  a width changed, the box first showed; a Backspace waits for a pause in the typing), and the box's own answer then
 *  has the last word, so a twin that is off by a line can leave the box a line too tall but never a line short.
 *  Measuring the twin on every key laid out the whole text two or three more times and made each key in a
 *  50,000-character narrative take about 68 ms. A pure insertion can, rarely, take a line away (a new place to break
 *  lets an earlier line hold more), and a Backspace can, which leaves the box taller than its text until the next
 *  remeasure; that is slack, never a cut.
 *
 *  The ancestors' scroll positions are still held, because a box that SHRINKS clamps them the same way. */
function fitToText(el: HTMLTextAreaElement, remeasure: boolean): void {
  const held: Array<[Element, number]> = [];
  for (let a: Element | null = el.parentElement; a && a !== document.documentElement; a = a.parentElement) {
    if (a.scrollTop > 0) held.push([a, a.scrollTop]);
  }
  const doc = document.scrollingElement;
  if (doc && doc.scrollTop > 0) held.push([doc, doc.scrollTop]);
  const cs = getComputedStyle(el);
  const before = el.offsetHeight;
  if (remeasure) {
    const need = neededHeight(el, cs);
    if (need > 0) el.style.height = `${need}px`;
  }
  if (el.clientWidth > 0) growToText(el, cs);
  fittedAt.set(el, textWidth(el, cs));
  for (const [a, top] of held) if (a.scrollTop !== top) a.scrollTop = top;
  if (el.offsetHeight !== before) followCaret(el, cs);
}

/** The text width each box was last fitted at, read the same way the observer reads it, so the two always compare in
 *  the same units in both zoom models. */
const fittedAt = new WeakMap<HTMLTextAreaElement, number>();

/** Whether `next` is `prev` with a few characters taken out at one place and no line break among them: one Backspace
 *  or Delete, or a word with Ctrl held. A cut or a deleted selection is bigger and is re-measured at once. */
function isKeyDeletion(prev: string, next: string): boolean {
  const gone = prev.length - next.length;
  if (gone <= 0 || gone > 32) return false;
  let head = 0;
  while (head < next.length && prev.charCodeAt(head) === next.charCodeAt(head)) head++;
  if (prev.slice(head + gone) !== next.slice(head)) return false;
  return !prev.slice(head, head + gone).includes('\n');
}

/** How long typing must pause before a box that only lost a few characters is measured on the twin again. Longer than
 *  the gap between repeated keys (30 to 40 ms for a held key, 100 to 200 ms for quick taps), short enough that the
 *  slack is gone by the time a person looks up. Someone deleting more slowly than this pays for one twin a key, which
 *  is what round 4 paid on every key, and has the time for it. */
const SHRINK_PAUSE_MS = 300;

/** A textarea that grows with its text (its `rows` stay the floor), so a long text shows in full and the page scrolls
 *  instead of the box: the space rule, 27 September 2026. Re-fits on every change and whenever the box's width is not
 *  the width it was last fitted at. The box's own ResizeObserver is the one signal for every way that happens: the
 *  window resizing, a pane or a page scrollbar beside it, and the Studio text size (Ctrl + wheel), which is CSS zoom
 *  on the app root and fires no window resize. Watching `window` alone left a zoomed page's text cut off with no
 *  scrollbar, because these boxes are overflow hidden. Only a width change re-fits, so writing the height can never
 *  set the observer off again; the width is unrounded, so a change of a fraction of a pixel (which can move a line
 *  break) counts. */
export function useAutoGrow(value: string, active = true): React.RefObject<HTMLTextAreaElement | null> {
  const ref = useRef<HTMLTextAreaElement | null>(null);
  const fitted = useRef<string | null>(null);
  const shrink = useRef<ReturnType<typeof setTimeout> | null>(null);
  useLayoutEffect(() => {
    const el = ref.current;
    if (shrink.current !== null) { clearTimeout(shrink.current); shrink.current = null; }
    if (!el || !active) { fitted.current = null; return; }
    const prev = fitted.current;
    fitted.current = value;
    if (prev !== null && isKeyDeletion(prev, value)) {
      // A Backspace can only leave the box too tall, never too short (anything it hides, growToText shows at once),
      // so the twin waits for a pause in the typing instead of laying out the whole text on every key.
      fitToText(el, false);
      shrink.current = setTimeout(() => { shrink.current = null; if (el.isConnected) fitToText(el, true); }, SHRINK_PAUSE_MS);
    } else fitToText(el, prev === null || !isInsertion(prev, value));
  }, [value, active]);
  useEffect(() => () => { if (shrink.current !== null) clearTimeout(shrink.current); }, []);
  // Its own effect, so a keystroke re-fits the box without tearing down and rebuilding the observer. The width is
  // compared with the width of the box's last fit, and never with the observer's previous report: the first report
  // can arrive after the box was narrowed (a later box grew in the same commit and brought the page scrollbar), and a
  // box that took that report as its starting point kept the height it had at the wider width, with the end of its
  // text cut off (120 px of a Docs Overview in a real window, 28 September 2026).
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || !active || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => {
      if (textWidth(el, getComputedStyle(el)) !== fittedAt.get(el)) fitToText(el, true);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [active]);
  return ref;
}
