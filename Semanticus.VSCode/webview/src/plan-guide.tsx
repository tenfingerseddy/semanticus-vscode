import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { whenFirstRunClosed } from './firstrun-gate';
import { requestFirstRun, requestGuideFlags, rpc, setGuideFlag, type GuideFlags } from './bridge';
import { cssZoomFactor } from './toolrow';
import './plan-guide.css';

// ===================================================================================================
// The Plan's first-open guidance (Plan-heart contract 10, mockup section 7). Two things, shown once each:
//
//   - the TOUR, six steps pinned to real parts of the Plan by data-tour attributes, for a new person;
//   - the WHAT CHANGED card, the few lines that changed how things work, for someone who used the old app.
//
// Which one shows is decided once, when the Plan mounts, from two facts: the host's once-flags
// (context.globalState, through bridge.ts) and whether this model's Plan history is empty. After that the
// guide gets out of the way. Help brings either back ("Show the tour", "What changed in this version"), and
// so does the "?" on the Plan header, which opens Help at the Plan.
//
// It is UI only on purpose. Guidance points at pixels, so there is no agent-door twin to build: the
// assistant's equivalent is the server instructions sentence and the semanticus skill (contract 6.3).
//
// THE ANCHORS. The Plan's own files carry them (plan.tsx, plan-strip.tsx, plan-row.tsx, plan-history.tsx):
//   data-tour="strip"         the strip
//   data-tour="row"           the first row of the list
//   data-tour="doer"          that row's doer mark
//   data-tour="field"         "Describe a task or an aim"
//   data-tour="queue-switch"  "Tasks for your assistant" On / Paused
//   data-tour="history-row"   the newest History row, and data-tour="history" on the History switch
// A step whose anchor is missing still shows, centred near the top with no ring, rather than vanishing.
// ===================================================================================================

/** The card this build ships. A content id, not the package version: a release with no card has no entry here. */
// m11-1 replaces plan-heart-1: it covers every area this release changed, not only the Plan, so a person who closed
// the first card still sees the new lines once. Every line is a claim in tools/release-claims.mjs, which names the code
// each one needs; the integrator runs it after the last feature lane merges.
export const WHAT_CHANGED_CARD_ID = 'm11-1';
export const WHAT_CHANGED: Record<string, { lines: string[] }> = {
  'm11-1': {
    lines: [
      'The Plan is now where you start. The Overview is gone.',
      'Edit History moved into the Plan. Open History at the top.',
      'Workflow runs show as tasks in the Plan.',
      "One switch pauses your assistant's tasks.",
      'Connections are on one page, with a SQL endpoint for each model.',
      'Workflows is one page. With Pro, a workflow you describe in words becomes steps.',
      'With Pro, Ask your data, under Explore, answers a question about your numbers. If it can be read two ways, it names the other.',
      'With Pro, Jev picks column formats and base measures, and chooses what Copilot sees.',
    ],
  },
};

export interface TourStep {
  /** data-tour names, tried in order; the first one on the page is the anchor. */
  anchor: string[];
  title: string;
  body?: string;
  /** Step three: the four doers, one line each, instead of a paragraph. */
  how?: boolean;
  /** The Plan view the step's anchor lives in. */
  view?: 'work' | 'history';
  /** A data-tour block the card must sit below, not over: step one's card used to cover the model summary line. */
  clearOf?: string;
  align?: 'start' | 'end';
}

export const PLAN_TOUR: TourStep[] = [
  { anchor: ['strip'], clearOf: 'facts', title: 'The work on your model',
    body: 'One track each for Semanticus, you and your assistant: done, on it and waiting. Click a track to see only its tasks.' },
  { anchor: ['row'], title: 'Every row answers three questions',
    body: 'Where it came from, who does it, and where it stands. The link on the right opens the page where it gets done.' },
  { anchor: ['doer', 'row'], title: 'How Semanticus works', how: true },
  { anchor: ['field'], title: 'Add your own',
    body: 'Describe a task or an aim in your own words. It becomes a task. Hand it to your assistant, or do it yourself.' },
  { anchor: ['queue-switch'], title: "Your assistant's tasks", align: 'end',
    body: 'Your assistant picks up its tasks the next time it checks in. Pause them here and keep talking to it directly.' },
  { anchor: ['history-row', 'history'], title: 'What changed, and Undo', view: 'history', align: 'end',
    body: 'History lists every change and who made it, newest first. Undo to here takes a change back, with any made after it. Restore brings back a saved version.' },
];

/** The step only a person who has not connected an assistant sees: the header button that does it. */
export const CONNECT_STEP: TourStep = { anchor: ['assistant-chip'], title: 'Connect your assistant',
  body: 'Open Your assistant in the header and choose Connect your assistant. Then your assistant can do the tasks that need words.' };

/** The tour for this person: the six steps, plus Connect your assistant just before the one about its tasks while
 *  nothing is connected. Once connected the step is gone, so the tour never tells someone to do what they have done. */
export function tourSteps(connected: boolean): TourStep[] {
  if (connected) return PLAN_TOUR;
  const at = PLAN_TOUR.findIndex((s) => s.anchor[0] === 'queue-switch');
  return [...PLAN_TOUR.slice(0, at), CONNECT_STEP, ...PLAN_TOUR.slice(at)];
}

export type GuideMode = 'none' | 'tour' | 'card';
export type GuideFlagName = 'planTour' | 'whatChanged';
export interface GuideState { mode: GuideMode; step: number }
export type GuideAction = { type: 'next' } | { type: 'back' } | { type: 'close' } | { type: 'showTour' } | { type: 'showCard' };

/**
 * What shows on first open. A new person (no tour flag, an empty Plan history) gets the tour. An existing
 * person (no tour flag, some history) gets this build's card, once. Anything unknown shows nothing: guidance
 * that cannot remember being closed must not appear, because it would appear every time.
 *
 * The card's flag is checked before the history because either guide showing sets it (shownFlags). Which guide a
 * person gets is decided once per build, about the person; the history only says which one on that first open.
 * Without this, a person first shown the card got the tour on the next empty model, and an assistant creating
 * models restarted the tour on every one (contract 10: nothing shows twice).
 */
export function decideGuide(flags: GuideFlags | null, historyEmpty: boolean | null, cardId: string | null): GuideMode {
  if (!flags || historyEmpty === null || flags.planTour || flags.whatChanged) return 'none';
  if (historyEmpty) return 'tour';
  if (!cardId || !WHAT_CHANGED[cardId]) return 'none';
  return 'card';
}

/** The flags that showing a guide sets at once. Either one showing means this build's card is done with. */
export function shownFlags(mode: GuideMode): GuideFlagName[] {
  return mode === 'none' ? [] : ['whatChanged'];
}

/**
 * One click. Closing the tour, from any step or with Done, sets its flag. Done on the card sets the card's flag
 * and the tour's: an existing person has been offered the tour on the card itself, and is not a new person the
 * next time they open a model with no history.
 */
export function guideStep(state: GuideState, action: GuideAction, count: number = PLAN_TOUR.length): { state: GuideState; set: GuideFlagName[] } {
  const last = count - 1;
  switch (action.type) {
    case 'next': return { state: { ...state, step: Math.min(last, state.step + 1) }, set: [] };
    case 'back': return { state: { ...state, step: Math.max(0, state.step - 1) }, set: [] };
    case 'close':
      if (state.mode === 'tour') return { state: { mode: 'none', step: 0 }, set: ['planTour'] };
      if (state.mode === 'card') return { state: { mode: 'none', step: 0 }, set: ['whatChanged', 'planTour'] };
      return { state, set: [] };
    case 'showTour': return { state: { mode: 'tour', step: 0 }, set: state.mode === 'card' ? ['whatChanged'] : [] };
    case 'showCard': return { state: { mode: 'card', step: 0 }, set: [] };
  }
}

export interface Box { left: number; top: number; right: number; bottom: number }
const EDGE = 16;
const ANCHOR_GAP = 18;

/** The Plan view a tour step lives in: every step but the last is on Work. */
export function tourView(step: number, steps: TourStep[] = PLAN_TOUR): 'work' | 'history' { return steps[step]?.view ?? 'work'; }

/** Where the step card sits: under its anchor, or above it when there is no room below; inside the frame.
 *  `clearBottom` is the bottom of a block the card must not cover; the card then starts 12 below it. */
export function placeTourCard(anchor: Box | null, card: { width: number; height: number }, bounds: Box,
  align: 'start' | 'end' = 'start', clearBottom?: number): { left: number; top: number } {
  const maxLeft = bounds.right - EDGE - card.width;
  const clampLeft = (x: number) => Math.max(bounds.left + EDGE, Math.min(maxLeft, x));
  if (!anchor) return { left: clampLeft(bounds.left + (bounds.right - bounds.left - card.width) / 2), top: bounds.top + 96 };
  let top = Math.max(anchor.bottom + ANCHOR_GAP, clearBottom != null ? clearBottom + 12 : 0);
  if (top + card.height > bounds.bottom - EDGE) top = Math.max(bounds.top + EDGE, anchor.top - ANCHOR_GAP - card.height);
  return { left: clampLeft(align === 'end' ? anchor.right - card.width : anchor.left), top };
}

// ---- Help's way back in -------------------------------------------------------------------------------------
// Help can be on any page. It asks for the tour or the card and, when it is not already on the Plan, goes there.
// The request is held here until the Plan's guide takes it, so a Plan that mounts AFTER the ask still sees it.
const GUIDE_EVENT = 'semanticus:plan-guide';
let pendingRequest: 'tour' | 'card' | null = null;
export function requestPlanGuide(mode: 'tour' | 'card'): void {
  pendingRequest = mode;
  try { window.dispatchEvent(new Event(GUIDE_EVENT)); } catch { /* no window: the request waits for the Plan */ }
}
export function takePlanGuideRequest(): 'tour' | 'card' | null {
  const r = pendingRequest;
  pendingRequest = null;
  return r;
}

// ---- the pieces a person sees -------------------------------------------------------------------------------

/** The doer marks, drawn the way the Plan's rows draw them. */
function Mark({ kind }: { kind: 'semanticus' | 'assistant' | 'you' | 'jev' }) {
  return <svg className="plan-tour-mark" viewBox="0 0 12 12" width="12" height="16" aria-hidden="true">
    {kind === 'semanticus' && <rect x="2" y="2" width="8" height="8" fill="currentColor" />}
    {kind === 'assistant' && <path d="M6 1l5 5-5 5-5-5z" fill="currentColor" />}
    {kind === 'you' && <circle cx="6" cy="6" r="4" fill="currentColor" />}
    {kind === 'jev' && <path d="M6 0.5l1.4 4.1 4.1 1.4-4.1 1.4L6 11.5 4.6 7.4 0.5 6l4.1-1.4z" fill="currentColor" />}
  </svg>;
}

export type GuideClick = 'next' | 'back' | 'close' | 'showTour';

export function TourCard({ step, onAct, style, cardRef, steps = PLAN_TOUR }: {
  step: number; onAct: (a: GuideClick) => void; style?: React.CSSProperties; cardRef?: React.Ref<HTMLDivElement>; steps?: TourStep[];
}) {
  const s = steps[step];
  const last = step === steps.length - 1;
  return <div ref={cardRef} className="plan-tour" role="dialog" aria-label="Plan tour" style={style}
    onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onAct('close'); } }}>
    <div className="plan-tour-head">
      <span className="plan-tour-pips" aria-hidden="true">{steps.map((_, i) => <i key={i} className={i <= step ? 'is-on' : undefined} />)}</span>
      <span className="plan-tour-count">{step + 1} of {steps.length}</span>
      <button type="button" className="plan-tour-close" onClick={() => onAct('close')}>Close</button>
    </div>
    <h4 className="plan-tour-title">{s.title}</h4>
    {s.how
      ? <div className="plan-tour-how">
          <span><Mark kind="semanticus" /><span><b>Semanticus</b> makes some changes itself, the same way every time.</span></span>
          <span><Mark kind="assistant" /><span><b>Your assistant</b> writes the ones that need words.</span></span>
          <span><Mark kind="jev" /><span><b>Jev</b> picks what comes first. Pro only.</span></span>
          <span><Mark kind="you" /><span><b>You</b> approve, and every change can be undone.</span></span>
        </div>
      : <p className="plan-tour-body">{s.body}</p>}
    <div className="plan-tour-foot">
      {last && <span className="plan-tour-note">Reopen it from ? or Help.</span>}
      <span className="plan-tour-acts">
        {step > 0 && <button type="button" className="sem-btn sem-btn-sm" onClick={() => onAct('back')}>Back</button>}
        {last
          ? <button type="button" data-tour-primary="" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => onAct('close')}>Done</button>
          : <button type="button" data-tour-primary="" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => onAct('next')}>Next</button>}
      </span>
    </div>
  </div>;
}

export function WhatChangedCard({ cardId, onAct }: { cardId: string; onAct: (a: GuideClick) => void }) {
  const card = WHAT_CHANGED[cardId];
  if (!card) return null;
  return <section className="plan-wc" aria-label="What changed in this version">
    <h4 className="plan-wc-title">What changed <span className="plan-wc-sub">in this version</span></h4>
    <ul className="plan-wc-list">{card.lines.map((line) => <li key={line}>{line}</li>)}</ul>
    <span className="plan-wc-acts">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => onAct('showTour')}>Show the tour</button>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => onAct('close')}>Done</button>
    </span>
  </section>;
}

/** The step's anchor and its rank in the step's list: 0 is the part the step is about, higher is a fallback. */
function findAnchor(step: TourStep): { el: Element; rank: number } | null {
  for (let rank = 0; rank < step.anchor.length; rank += 1) {
    const el = document.querySelector(`[data-tour="${step.anchor[rank]}"]`);
    if (el) return { el, rank };
  }
  return null;
}

type Placed = { card: { left: number; top: number }; ring: Box | null };
const samePlace = (a: Placed | null, b: Placed) => !!a && JSON.stringify(a) === JSON.stringify(b);
/** How long a new step keeps measuring: its anchor may draw late or change size as the page settles. */
const SETTLE_FRAMES = 45;

/** The step card and the ring around its anchor, placed in the zoomed Studio root's own pixels. */
function TourOverlay({ step, onAct, steps }: { step: number; onAct: (a: GuideClick) => void; steps: TourStep[] }) {
  const cardRef = useRef<HTMLDivElement>(null);
  const [place, setPlace] = useState<Placed | null>(null);
  const anchorRef = useRef<Element | null>(null);
  const [anchorEl, setAnchorEl] = useState<Element | null>(null);
  const measure = useCallback((scroll: boolean) => {
    const found = findAnchor(steps[step]);
    const anchor = found?.el ?? null;
    const host = (cardRef.current?.closest('.studio-root') ?? document.querySelector('.studio-root')) as HTMLElement | null;
    const z = cssZoomFactor(host ?? cardRef.current);
    if (anchor && scroll && anchor !== anchorRef.current) (anchor as HTMLElement).scrollIntoView?.({ block: 'nearest' });
    anchorRef.current = anchor;
    setAnchorEl(anchor);
    const b = host?.getBoundingClientRect();
    const bounds: Box = b ? { left: b.left / z, top: b.top / z, right: b.right / z, bottom: b.bottom / z }
      : { left: 0, top: 0, right: window.innerWidth / z, bottom: window.innerHeight / z };
    const c = cardRef.current?.getBoundingClientRect();
    const size = c && c.width > 0 ? { width: c.width / z, height: c.height / z } : { width: 400, height: 180 };
    const a = anchor?.getBoundingClientRect();
    const ring = a ? { left: a.left / z, top: a.top / z, right: a.right / z, bottom: a.bottom / z } : null;
    const clearEl = steps[step].clearOf ? document.querySelector(`[data-tour="${steps[step].clearOf}"]`) : null;
    const clearBottom = clearEl ? clearEl.getBoundingClientRect().bottom / z : undefined;
    const next: Placed = { card: placeTourCard(ring, size, bounds, steps[step].align, clearBottom), ring };
    setPlace((old) => (samePlace(old, next) ? old : next));
  }, [step, steps]);
  // A step's anchor can draw late (step six opens History, whose rows arrive on their own call) and can change
  // size as the page settles (a switch that goes bold when pressed). So a new step keeps measuring for a moment,
  // moving from a fallback to the part it is about as soon as that appears, and then settles; a missing anchor
  // leaves the card centred with no ring.
  useLayoutEffect(() => {
    anchorRef.current = null;
    let frames = 0; let frame = 0;
    const look = () => { measure(true); if (++frames < SETTLE_FRAMES) frame = requestAnimationFrame(look); };
    look();
    return () => cancelAnimationFrame(frame);
  }, [measure]);
  useEffect(() => {
    const again = () => measure(false);
    window.addEventListener('resize', again);
    window.addEventListener('scroll', again, true);
    const watch = typeof ResizeObserver === 'function' ? new ResizeObserver(again) : null;
    if (watch && anchorEl) watch.observe(anchorEl);
    return () => { window.removeEventListener('resize', again); window.removeEventListener('scroll', again, true); watch?.disconnect(); };
  }, [measure, anchorEl]);
  // Focus goes to Next (or Done) once the card is placed: a hidden card cannot take focus, and without it the first
  // step would not hear Escape or the keyboard at all.
  const placed = !!place;
  useEffect(() => { if (placed) cardRef.current?.querySelector<HTMLElement>('[data-tour-primary]')?.focus({ preventScroll: true }); }, [step, placed]);
  const pad = 6;
  return <>
    {place?.ring && <div className="plan-tour-ring" aria-hidden="true" style={{
      left: place.ring.left - pad, top: place.ring.top - pad,
      width: place.ring.right - place.ring.left + pad * 2, height: place.ring.bottom - place.ring.top + pad * 2,
    }} />}
    <TourCard step={step} steps={steps} onAct={onAct} cardRef={cardRef}
      style={place ? { left: place.card.left, top: place.card.top } : { visibility: 'hidden', left: 0, top: 0 }} />
  </>;
}

/**
 * Mounted once on the Plan, above the strip. It draws the What changed card in place and the tour over the page.
 * `onView` lets the tour open History for its last step and put the page back afterwards.
 */
export function PlanGuide({ onView }: { onView?: (view: 'work' | 'history') => void }) {
  const [state, setState] = useState<GuideState>({ mode: 'none', step: 0 });
  const asked = useRef(false);
  // Whether an assistant is connected, asked when a tour opens. Unknown counts as connected: fewer steps, never a wrong one.
  const [steps, setSteps] = useState<TourStep[]>(PLAN_TOUR);
  const askSteps = () => { void requestFirstRun().then((s) => setSteps(tourSteps(s ? s.connected : true))); };
  const write = (set: GuideFlagName[]) => {
    for (const f of new Set(set)) setGuideFlag(f, f === 'whatChanged' ? WHAT_CHANGED_CARD_ID : undefined);
  };
  const act = (a: GuideAction) => { const r = guideStep(state, a, steps.length); write(r.set); if (r.state.mode === 'tour' && state.mode !== 'tour') askSteps(); setState(r.state); };

  useEffect(() => {
    let alive = true;
    const take = () => {
      const want = takePlanGuideRequest();
      if (!want) return;
      asked.current = true;
      if (want === 'card' && !WHAT_CHANGED[WHAT_CHANGED_CARD_ID]) return;
      if (want === 'tour') askSteps();
      setState({ mode: want, step: 0 });
    };
    take();
    window.addEventListener(GUIDE_EVENT, take);
    if (!asked.current) {
      const empty = rpc<{ rows?: unknown[] }>('getPlanHistory', null, null, null, 1)
        .then((v) => (Array.isArray(v?.rows) ? v.rows.length === 0 : null), () => null);
      // The tour waits for the first-run choice to close: two cards at once fight for the keyboard.
      void Promise.all([requestGuideFlags(WHAT_CHANGED_CARD_ID), empty, whenFirstRunClosed()]).then(([flags, historyEmpty]) => {
        if (!alive || asked.current) return;
        const mode = decideGuide(flags, historyEmpty, WHAT_CHANGED_CARD_ID);
        if (mode === 'none') return;
        write(shownFlags(mode));
        if (mode === 'tour') askSteps();
        setState({ mode, step: 0 });
      });
    }
    return () => { alive = false; window.removeEventListener(GUIDE_EVENT, take); };
  }, []);

  // The last step's anchor lives in History, so the tour opens it there and puts the page back when it leaves.
  const movedView = useRef(false);
  const wantView = state.mode === 'tour' ? tourView(state.step, steps) : null;
  useEffect(() => {
    if (!onView) return;
    // A tour opened from History must still start on Work, where its first five steps live (UAT-J9-S10a-006).
    if (wantView === 'work') { onView('work'); }
    else if (wantView === 'history') { movedView.current = true; onView('history'); }
    else if (movedView.current) { movedView.current = false; onView('work'); }
  }, [wantView, onView]);

  if (state.mode === 'card') return <WhatChangedCard cardId={WHAT_CHANGED_CARD_ID} onAct={(a) => act({ type: a === 'showTour' ? 'showTour' : 'close' })} />;
  if (state.mode === 'tour') return <TourOverlay step={state.step} steps={steps} onAct={(a) => act({ type: a === 'showTour' ? 'showTour' : a })} />;
  return null;
}
