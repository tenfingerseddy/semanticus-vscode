// The Link (1.5, design page section 1, option A): the two dots from the logo are the character. When Semanticus works, the dots
// travel along the S; when a job is done they meet in the middle, like a relationship forming between two tables, then go home.
// 16 px in the status line and the driver bar, 200 px on the first-run card.
//
// Performance (brief: nothing may slow the Studio). The page's mock moved the dots from a requestAnimationFrame loop. Here every state
// is one set of Web Animations, sampled once from the same motion functions and then handed to the browser: no JavaScript runs per
// frame, nothing reads layout, and the browser stops drawing them when the Studio is hidden. The dots move by transform; the trails,
// the bar and the ring change opacity and dash only. At 16 px the idle breath (a twelfth of a pixel) is left out, so an idle Link
// in the status line costs nothing at all.
import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties } from 'react';
import { linkWork, type LinkState, type LinkWork } from './link-state';
import { setPersonalityPoster, usePersonalitySettings, type PersonalitySettingMessage } from './settings';
import { VERB_HOLD_MS, verbFor } from './verbs';

export type { LinkState } from './link-state';

// ---- the S, measured by hand so no DOM is needed (the page measured it with getPointAtLength) ---------------------------------
export const S_PATH = 'M19 5.5H8.25a3.25 3.25 0 0 0 0 6.5h7.5a3.25 3.25 0 0 1 0 6.5H5';
const R = 3.25, ARC = Math.PI * R;
const SEGS = [10.75, ARC, 7.5, ARC, 10.75];
const TOTAL = SEGS.reduce((a, b) => a + b, 0);

/** The point at fraction s (0 to 1) along the S, in the 24 by 24 view box. */
export function pointOnS(s: number): [number, number] {
  let d = Math.min(1, Math.max(0, s)) * TOTAL;
  if (d <= SEGS[0]) return [19 - d, 5.5];
  d -= SEGS[0];
  if (d <= SEGS[1]) { const a = -Math.PI / 2 - d / R; return [8.25 + R * Math.cos(a), 8.75 + R * Math.sin(a)]; }
  d -= SEGS[1];
  if (d <= SEGS[2]) return [8.25 + d, 12];
  d -= SEGS[2];
  if (d <= SEGS[3]) { const a = -Math.PI / 2 + d / R; return [15.75 + R * Math.cos(a), 15.25 + R * Math.sin(a)]; }
  d -= SEGS[3];
  return [15.75 - Math.min(d, SEGS[4]), 18.5];
}

// ---- the motion, ported from the design page's LinkChar.tick ----------------------------------------------------------------
const lerp = (a: number, b: number, k: number) => a + (b - a) * k;
const clamp = (v: number, a: number, b: number) => Math.max(a, Math.min(b, v));
const easeIO = (k: number) => (k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2);
const easeOut = (k: number) => 1 - Math.pow(1 - k, 3);
const TAU = Math.PI * 2;

export type LinkFrame = {
  a: number; b: number;           // where the two dots are along the S
  ra: number; rb: number;         // their radii
  bar: number; barW: number | null;
  ring: number; ringR: number;
  dashed: boolean;                // thinking: the S dashes
  lit: boolean;                   // success: the S turns jade
};

export const SUCCESS_S = 1.9;
/** One loop of each state in seconds; success plays once. Reduced motion keeps only the working bar's fade. */
export function periodOf(state: LinkState, reduced: boolean, small: boolean): number {
  if (state === 'success') return SUCCESS_S;
  if (reduced) return state === 'working' ? 1.6 : 0;
  if (state === 'working') return 2.4;
  if (state === 'thinking') return 1.8;
  if (state === 'idle') return small ? 0 : 3.2;
  return 0;
}

/** The frame of `state` at `t` seconds since it began. */
export function frameAt(state: LinkState, t: number, reduced: boolean, r0: number): LinkFrame {
  const f: LinkFrame = { a: 0, b: 1, ra: r0, rb: r0, bar: 0.6, barW: null, ring: 0, ringR: 2, dashed: false, lit: false };
  if (state === 'idle') {
    if (!reduced) { const w = Math.sin(t * TAU / 3.2); f.ra = r0 * (1 + 0.08 * w); f.rb = r0 * (1 - 0.08 * w); }
  } else if (state === 'error') {
    f.bar = 0.4;
  } else if (state === 'working') {
    if (reduced) { f.a = 0.22; f.b = 0.78; f.bar = 0.4 + 0.5 * (0.5 + 0.5 * Math.sin(t * TAU / 1.6)); }
    else { const u = (t % 2.4) / 2.4, tri = u < 0.5 ? u * 2 : 2 - u * 2, e = easeIO(tri); f.a = e; f.b = 1 - e; f.bar = 0.5 + 0.5 * Math.max(0, 1 - Math.abs(e - 0.5) * 10); }
  } else if (state === 'thinking') {
    f.dashed = true;
    if (reduced) { f.a = 0.36; f.b = 0.64; f.bar = 0.9; }
    else { const w = Math.sin(t * TAU / 0.9); f.a = 0.32 + 0.1 * w; f.b = 0.68 + 0.1 * w; f.barW = 1.6 + 0.8 * (0.5 + 0.5 * Math.sin(t * TAU / 0.6)); f.bar = 1; }
  } else if (state === 'success') {
    const p = t / SUCCESS_S;
    if (reduced) { f.a = f.b = 0.5; f.ra = f.rb = r0 * 1.3; f.bar = 1; f.barW = 2.6; f.ring = p < 1 ? 0.6 : 0; f.ringR = 5; f.lit = p < 1; }
    else if (p < 0.32) { const k = easeIO(p / 0.32); f.a = lerp(0, 0.5, k); f.b = lerp(1, 0.5, k); }
    else if (p < 0.72) { f.a = f.b = 0.5; const k = (p - 0.32) / 0.4; f.ra = f.rb = r0 * (1 + 0.45 * Math.sin(Math.min(1, k * 2.2) * Math.PI)); f.bar = 1; f.barW = 1.6 + 1.2 * (1 - k); f.ring = Math.max(0, 1 - k * 1.3); f.ringR = 2 + 6 * easeOut(k); f.lit = true; }
    else if (p < 1) { const k = easeIO((p - 0.72) / 0.28); f.a = lerp(0.5, 0, k); f.b = lerp(0.5, 1, k); f.bar = lerp(1, 0.6, k); f.lit = true; }
  }
  return f;
}

const FPS = 30;
type Track = { el: string; frames: Keyframe[] };
/**
 * Every element's keyframes for one loop (or the one success play), with the first 0.45 s blended from where the dots were, as the
 * page's switch blend does. `from` is null for a fresh mount. Pure: the tests read these frames.
 */
export function keyframesFor(state: LinkState, reduced: boolean, small: boolean, from: { a: number; b: number } | null): { duration: number; tracks: Track[] } {
  const r0 = small ? 2.3 : 1.9, sw = small ? 2.2 : 1.6;
  const P = periodOf(state, reduced, small);
  const n = Math.max(1, Math.round(P * FPS));
  const blend = from && state !== 'success' ? from : null;
  const frames: LinkFrame[] = [];
  for (let i = 0; i <= n; i++) {
    const t = P ? (i / n) * P : 0;
    const f = frameAt(state, t, reduced, r0);
    if (blend && !reduced) { const k = easeOut(clamp(t / 0.45, 0, 1)); f.a = lerp(blend.a, f.a, k); f.b = lerp(blend.b, f.b, k); }
    if (state === 'success' && !reduced && from && t / SUCCESS_S < 0.32) {
      // Start the meeting from wherever the dots were when the job ended, not from the ends.
      const k = easeIO(t / SUCCESS_S / 0.32); f.a = lerp(from.a, 0.5, k); f.b = lerp(from.b, 0.5, k);
    }
    frames.push(f);
  }
  const dt = P ? P / n : 1;
  const dot = (key: 'a' | 'b', rk: 'ra' | 'rb') => frames.map((f) => { const [x, y] = pointOnS(f[key]); return { transform: `translate(${x.toFixed(3)}px, ${y.toFixed(3)}px) scale(${(f[rk] / r0).toFixed(4)})` }; });
  const trail = (key: 'a' | 'b') => frames.map((f, i) => {
    const prev = frames[Math.max(0, i - 1)][key], v = i ? (f[key] - prev) / dt : 0;
    const len = reduced ? 0 : Math.min(0.16, Math.abs(v) * 0.11);
    return { strokeDasharray: `${len.toFixed(4)} 3`, strokeDashoffset: String((v >= 0 ? -(f[key] - len) : -f[key]).toFixed(4)), opacity: String(Math.min(1, Math.abs(v) * 1.4).toFixed(3)) };
  });
  const tracks: Track[] = [
    { el: 'dotA', frames: dot('a', 'ra') },
    { el: 'dotB', frames: dot('b', 'rb') },
    { el: 'trailA', frames: trail('a') },
    { el: 'trailB', frames: trail('b') },
    { el: 'bar', frames: frames.map((f) => ({ opacity: String(f.bar.toFixed(3)), strokeWidth: String(f.barW ?? sw) })) },
    { el: 'ring', frames: frames.map((f) => ({ opacity: String(f.ring.toFixed(3)), r: String(f.ringR.toFixed(3)) })) },
    { el: 'lit', frames: frames.map((f) => ({ opacity: f.lit ? '1' : '0' })) },
  ];
  return { duration: P * 1000, tracks };
}

/** Where the dots are `t` seconds into a state: what the next state blends from. */
export function dotsAt(state: LinkState, t: number, reduced: boolean): { a: number; b: number } {
  if (state === 'success' && t >= SUCCESS_S) return { a: 0, b: 1 };
  const P = periodOf(state, reduced, false);
  const f = frameAt(state, P ? t % P : t, reduced, 1.9);
  return { a: f.a, b: f.b };
}

// ---- the character ----------------------------------------------------------------------------------------------------------
type Els = Record<'dotA' | 'dotB' | 'trailA' | 'trailB' | 'bar' | 'ring' | 'lit' | 'base', SVGElement | null>;

export function LinkMark({ state, size = 16, reduced: forceReduced, className, style, title }: {
  state: LinkState; size?: number; reduced?: boolean; className?: string; style?: CSSProperties; title?: string;
}) {
  const settings = usePersonalitySettings();
  const reduced = forceReduced ?? settings.reducedMotion;
  const small = size < 32;
  const r0 = small ? 2.3 : 1.9, sw = small ? 2.2 : 1.6;
  const els = useRef<Els>({ dotA: null, dotB: null, trailA: null, trailB: null, bar: null, ring: null, lit: null, base: null });
  const last = useRef<{ state: LinkState; at: number; reduced: boolean } | null>(null);

  useLayoutEffect(() => {
    const now = performance.now();
    const prev = last.current;
    const from = prev ? dotsAt(prev.state, (now - prev.at) / 1000, prev.reduced) : null;
    last.current = { state, at: now, reduced };
    const nodes = els.current;
    const running: Animation[] = [];
    const { duration, tracks } = keyframesFor(state, reduced, small, from);
    const canAnimate = typeof (nodes.dotA as unknown as { animate?: unknown })?.animate === 'function';
    for (const track of tracks) {
      const el = nodes[track.el as keyof Els];
      if (!el) continue;
      if (!canAnimate || duration === 0) {
        // A still state: the first frame is the whole picture.
        const f = track.frames[0] as Record<string, string>;
        for (const [k, v] of Object.entries(f)) (el.style as unknown as Record<string, string>)[k] = v;
        continue;
      }
      for (const [k] of Object.entries(track.frames[0])) (el.style as unknown as Record<string, string>)[k] = '';
      const loop = state !== 'success';
      // A blended start plays once, then the clean loop takes over exactly where it ends (it was made later, so it wins when it starts).
      if (from && loop && !reduced) {
        running.push(el.animate(track.frames, { duration, iterations: 1, fill: 'forwards' }));
        running.push(el.animate(keyframesFor(state, reduced, small, null).tracks.find((x) => x.el === track.el)!.frames, { duration, delay: duration, iterations: Infinity }));
      } else {
        running.push(el.animate(track.frames, { duration, iterations: loop ? Infinity : 1, fill: 'forwards' }));
      }
    }
    if (nodes.base && canAnimate) {
      if (state === 'thinking' && !reduced) running.push(nodes.base.animate([{ strokeDashoffset: '0' }, { strokeDashoffset: '-0.057' }], { duration: 475, iterations: Infinity }));
    }
    return () => { for (const a of running) a.cancel(); };
  }, [state, reduced, small]);

  const dashed = state === 'thinking';
  const set = (k: keyof Els) => (el: SVGElement | null) => { els.current[k] = el; };
  return <svg viewBox="0 0 24 24" width={size} height={size} aria-hidden={title ? undefined : true} role={title ? 'img' : undefined} aria-label={title}
    className={`linkchar${state === 'error' ? ' is-error' : ''}${className ? ` ${className}` : ''}`} data-link-state={state} data-link-reduced={reduced ? 'yes' : undefined}
    style={{ ['--linkchar-sw' as string]: String(sw), ...style }}>
    <path ref={set('base')} className="linkchar-base" d={S_PATH} pathLength={1}
      strokeDasharray={dashed ? '.035 .022' : undefined} opacity={dashed ? 0.7 : 1} />
    <path ref={set('lit')} className="linkchar-lit" d={S_PATH} style={{ opacity: 0 }} />
    <path ref={set('bar')} className="linkchar-bar" d="M8.25 12h7.5" />
    <circle ref={set('ring')} className="linkchar-ring" cx={12} cy={12} r={2} style={{ opacity: 0 }} />
    <path ref={set('trailA')} className="linkchar-trail" d={S_PATH} pathLength={1} style={{ opacity: 0 }} />
    <path ref={set('trailB')} className="linkchar-trail" d={S_PATH} pathLength={1} style={{ opacity: 0 }} />
    <circle ref={set('dotA')} className="linkchar-dot" cx={0} cy={0} r={r0} />
    <circle ref={set('dotB')} className="linkchar-dot" cx={0} cy={0} r={r0} />
  </svg>;
}

// ---- the real work, fed from the Studio's own signals ---------------------------------------------------------------------------
type Off = () => void;
export type PersonalityBridge = {
  onAssistantCall: (fn: (c: { tool: string }) => void) => Off;
  onActivity: (fn: (e: { origin?: string; ok: boolean; error?: string; kind?: string; words?: { sentence?: string } | null }) => void) => Off;
  onProgress: (fn: (p: { opKey?: string; done: number; total: number }) => void) => Off;
  onPlanRunProgress: (fn: (v: unknown) => void) => Off;
  postToHost: unknown;
};
let wired = false;
/** App calls this once with the bridge: The Link hears the work, and the personality switches write through the host. The listeners
 *  live for the page, like the bridge itself. This file imports no bridge, so a test can mount a Link without a VS Code host. */
export function wirePersonality(b: PersonalityBridge): void {
  if (wired) return;
  wired = true;
  b.onAssistantCall((c) => linkWork.call(c.tool));
  b.onActivity((e) => linkWork.activity({ origin: e.origin, ok: e.ok, error: e.error, kind: e.kind, words: e.words }));
  b.onProgress((p) => linkWork.progress(p));
  b.onPlanRunProgress(() => linkWork.progress({ opKey: 'plan', done: NaN, total: NaN }));
  // The bridge stamps every message with the page identity; its typed post has no settings message, so this narrows the call.
  setPersonalityPoster(b.postToHost as (m: PersonalitySettingMessage) => void);
}

export function useLinkWork(): LinkWork {
  return useSyncExternalStore(linkWork.subscribe, linkWork.getSnapshot, linkWork.getSnapshot);
}

/** The Link alone, bound to the real work (the driver bar). */
export function WorkLink({ size = 16 }: { size?: number }) {
  const work = useLinkWork();
  return <LinkMark state={work.state} size={size} />;
}

// ---- the status line ------------------------------------------------------------------------------------------------------------
const clock = (ms: number) => { const s = Math.max(0, Math.floor(ms / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };

/** What the status line says for one moment. Pure, so the tests can read it. */
export function statusWords(work: LinkWork, step: number, quiet: boolean): string {
  if (work.state === 'working' || work.state === 'thinking') return verbFor(work.group, step, quiet, work.job * 3);
  if (work.state === 'success') return work.words ? `Done. ${work.words}` : 'Done';
  if (work.state === 'error') return work.error ? `Stopped. ${work.error}` : 'Stopped';
  return '';
}

/** A tiny hook that steps while `on` and the Studio is visible, and not at all otherwise. */
export function useStepper(on: boolean, ms: number, resetKey: unknown): number {
  const [step, setStep] = useState(0);
  useEffect(() => { setStep(0); }, [resetKey]);
  useEffect(() => {
    if (!on) return undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const arm = () => { if (timer) clearTimeout(timer); timer = undefined; if (!document.hidden) timer = setTimeout(() => { setStep((n) => n + 1); arm(); }, ms); };
    document.addEventListener('visibilitychange', arm);
    arm();
    return () => { if (timer) clearTimeout(timer); document.removeEventListener('visibilitychange', arm); };
  }, [on, ms]);
  return step;
}

/** The words, rolling up when they change: the old line leaves upward as the new one rises in (transform and opacity only). */
export function RollingWords({ text, reduced }: { text: string; reduced: boolean }) {
  const [shown, setShown] = useState<{ now: string; was: string | null; n: number }>({ now: text, was: null, n: 0 });
  useEffect(() => { setShown((s) => (s.now === text ? s : { now: text, was: s.now, n: s.n + 1 })); }, [text]);
  const nowRef = useRef<HTMLSpanElement>(null);
  const wasRef = useRef<HTMLSpanElement>(null);
  useLayoutEffect(() => {
    if (!shown.was || reduced) return undefined;
    const a = nowRef.current?.animate?.([{ transform: 'translateY(100%)', opacity: 0 }, { transform: 'none', opacity: 1 }], { duration: 300, easing: 'ease-out' });
    const b = wasRef.current?.animate?.([{ transform: 'none', opacity: 1 }, { transform: 'translateY(-100%)', opacity: 0 }], { duration: 260, easing: 'ease-in', fill: 'forwards' });
    if (b) b.onfinish = () => setShown((s) => (s.n === shown.n ? { ...s, was: null } : s));
    return () => { a?.cancel(); };
  }, [shown.n]);  // eslint-disable-line react-hooks/exhaustive-deps
  return <span className="sem-status-words">
    <span ref={nowRef} key={`n${shown.n}`} className="sem-status-word">{shown.now}</span>
    {shown.was && !reduced && <span ref={wasRef} key={`w${shown.n}`} className="sem-status-word is-leaving" aria-hidden="true">{shown.was}</span>}
  </span>;
}

/** The status line's Link and working verb: the right end of the Studio's bottom bar. */
export function LinkStatus() {
  const work = useLinkWork();
  const settings = usePersonalitySettings();
  const quiet = settings.personality === 'quiet';
  const running = work.state === 'working' || work.state === 'thinking';
  const step = useStepper(running, VERB_HOLD_MS, work.job);
  const tick = useStepper(running, 1000, work.job);
  void tick;
  const words = statusWords(work, step, quiet);
  const elapsed = running && work.startedAt !== null ? Date.now() - work.startedAt : 0;
  const title = work.state === 'idle' ? 'Semanticus is ready' : undefined;
  return <div className="sem-status-link" data-testid="link-status" data-link-work={work.state} title={title}>
    {/* The Link is last, at the bar's very end, so a longer or shorter verb never moves it: the words grow to its left. */}
    {words && <RollingWords text={words} reduced={settings.reducedMotion} />}
    {running && work.progress && <span className="sem-status-meta sem-num">{work.progress.done} of {work.progress.total}</span>}
    {running && elapsed >= 2000 && <span className="sem-status-meta sem-num">{clock(elapsed)}</span>}
    <LinkMark state={work.state} size={16} />
    {/* The verb is decoration; the plain fact is said once to a screen reader, not every 2.3 seconds. */}
    <span className="sr-only" role="status">{running ? 'Working' : work.state === 'success' ? 'Done' : work.state === 'error' ? words : ''}</span>
  </div>;
}
