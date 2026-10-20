import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { rpc, runHostCommand } from './bridge';
import { LinkMark, RollingWords, statusWords, useLinkWork, useStepper } from './personality/link';
import { linkWork } from './personality/link-state';
import { usePersonalitySettings } from './personality/settings';
import { VERB_HOLD_MS } from './personality/verbs';
import { DoerMark } from './plan-row';
import { PlanFigures } from './plan-figures';
import { slotOf, driveValue, CONNECT_LABEL, STEP_FADE_MS, type Grade, type Slot } from './plan-calm-model';
import type { ReadingCard } from './reading-card-model';
import type { PlanStrip } from './plan-model';
import { setDriveMode } from './studio/driver-wire';
import { driverStore, useDriver, assistantIsActive, ASSISTANT_ACTIVE_MS, type DriveMode } from './studio/driver-store';
import { useNextActions, useNextStep, useNextStepLoad } from './studio/next-line';

// ===================================================================================================
// The Plan's header (1.5, Kane's pick C "Header does it all", 12 Oct 2026). One line:
//
//   Plan  AI ready A 91 | Best practice D 64 | Plan done 40%  | one live slot     drives / I drive   Work History   [Run the plan]
//
// The three small figures (plan-figures.tsx, Kane's pick A, 12 Oct): the AI readiness grade is the reading card's fast first read (engine readingCard,
// which the assistant reads as reading_card; fixThem is its fix_them) and it flips when the dots of the Link meet; Best practice and Plan done
// come from the engine's strip (bestPracticeScore, tasksDone, tasksAll). The slot says one thing at a time: your own press (the Link and a verb in the
// colour of who works, "3 of 10"), the end of a run ("Done. ... Undo"), work somebody else is doing, or the assistant's next
// suggested step with its "Ask your assistant" press. The drive switch is the driver bar folded in, and it also pauses the
// assistant's Plan tasks: I drive = the engine's queue paused (a person drives: nothing is handed to the assistant behind them).
// ===================================================================================================

/** How many times the first read of the AI readiness grade is tried, and the wait between tries (ms): a reopened model may not be ready on the first. */
export const GRADE_READS = 4;
export const GRADE_RETRY_MS = 1500;

const readGrade = async (): Promise<Grade | null> => {
  try {
    const c = await rpc<ReadingCard | null>('readingCard', false);
    return c && c.grade ? { letter: c.grade, score: typeof c.score === 'number' ? c.score : null } : null;
  } catch { return null; }
};

/** The grade for the header: the first read of a model holds the Link (its dots meet when the grade lands), and a later change reads again quietly.
 *  `fallback` is the Plan's own letter for an engine that has no reading card. */
export function usePlanGrade(sessionId: string, changeNonce: number, fallback: string | null) {
  const [grade, setGrade] = useState<Grade | null>(null);
  const generation = useRef(0);
  useEffect(() => {
    const mine = ++generation.current;
    setGrade(null);
    linkWork.hold('reading-card', true);
    // After a reopen the first read can miss (the model is still loading): the letter alone would show, so read again a few times for its number.
    void (async () => {
      for (let tries = 0; tries < GRADE_READS; tries++) {
        const g = await readGrade();
        if (generation.current !== mine) return;
        if (g) { setGrade(g); return; }
        await new Promise((r) => setTimeout(r, GRADE_RETRY_MS));
        if (generation.current !== mine) return;
      }
    })().finally(() => linkWork.hold('reading-card', false));
    return () => { generation.current++; linkWork.hold('reading-card', false); };
  }, [sessionId]);
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return undefined; }
    const mine = generation.current;
    const t = setTimeout(() => { void readGrade().then((g) => { if (generation.current === mine && g) setGrade(g); }); }, 600);
    return () => clearTimeout(t);
  }, [changeNonce]);
  /** Read the grade now (after a run) and show it; the answer is what the Done line says. */
  const refresh = useCallback(async () => { const g = await readGrade(); if (g) setGrade(g); return g; }, []);
  return { grade: grade ?? (fallback ? { letter: fallback, score: null } : null), refresh };
}

/** drives / I drive. It is the driver bar's switch (same store, same host message) and it also sets the assistant's queue, so one switch covers both. */
export function DriveSwitch({ queue, onQueue }: { queue: 'on' | 'paused'; onQueue: (q: 'on' | 'paused') => void }) {
  const driver = useDriver();
  if (!driver.assistantConnected) return null;
  const wheel = driver.mode === 'claudeDrives' && driver.state === 'personHasWheel';
  const value = driveValue(driver.mode, queue, wheel);
  const driverName = 'Your assistant';   // one name for the assistant on every surface, whichever one is connected (UAT round 2, N23)
  const waiting = driver.mode === 'claudeDrives' && !driver.assistantWorking;
  const choose = (mode: DriveMode) => {
    if (value === mode && driver.mode === mode) return;
    setDriveMode(mode, false);
    if (mode === 'claudeDrives' && wheel) driverStore.letClaudeDrive();   // the person gives the wheel back by pressing it
    const want = mode === 'iDrive' ? 'paused' : 'on';
    if (queue !== want) onQueue(want);
  };
  return <span className="sem-seg pc-switch" role="group" aria-label="Who drives" data-tour="drive-switch">
    {(['claudeDrives', 'iDrive'] as const).map((mode) => <button type="button" key={mode} className="sem-seg-item" data-testid={`driver-mode-${mode}`}
      aria-pressed={value === mode}
      style={value === mode ? { color: mode === 'iDrive' ? 'var(--sem-actor-you)' : waiting ? 'var(--sem-fg)' : 'var(--sem-actor-assistant)', background: 'var(--sem-surface-2)', boxShadow: 'inset 0 0 0 1px currentColor' } : { color: 'var(--sem-muted)' }}
      onClick={() => choose(mode)}>{mode === 'claudeDrives' ? `${driverName} drives` : 'I drive'}</button>)}
  </span>;
}

/** The one slot. The Link is always its first thing, so the eye knows where the page talks. */
export function LiveSlot({ press, receipt, receiptActions }: {
  /** Run the plan or one row is running: the verb and "3 of 10". */
  press: { verb: string; count: string; fraction: number } | null;
  receipt: { kind: 'ran' | 'said'; text: string } | null;
  /** Undo, and Save when it is offered, for the end-of-run line. */
  receiptActions: ReactNode;
}) {
  const work = useLinkWork();
  const driver = useDriver();
  const settings = usePersonalitySettings();
  // "Working on your model" ends by the clock, not by an event: draw again when the assistant's active window is over, so the slot never sticks.
  const [, redraw] = useState(0);
  useEffect(() => {
    if (driver.lastAssistantCall === null) return undefined;
    const left = driver.lastAssistantCall + ASSISTANT_ACTIVE_MS - Date.now();
    if (left <= 0) return undefined;
    const t = setTimeout(() => redraw((n) => n + 1), left + 50);
    return () => clearTimeout(t);
  }, [driver.lastAssistantCall]);
  const { view } = useNextStep();
  useNextStepLoad();
  const quiet = settings.personality === 'quiet';
  const running = work.state === 'working' || work.state === 'thinking';
  const step = useStepper(running && !press, VERB_HOLD_MS, work.job);
  const slot: Slot = slotOf({
    press: press ? { verb: press.verb, count: press.count } : null,
    receipt,
    work: running ? { who: work.who === 'assistant' ? 'assistant' : 'person', verb: statusWords(work, step, quiet), progress: work.progress ? `${work.progress.done} of ${work.progress.total}` : null } : null,
    driver: { connected: driver.assistantConnected, mode: driver.mode, wheel: driver.state === 'personHasWheel', assistantWorking: assistantIsActive(driver, Date.now()), name: driver.assistant?.name ?? 'Your assistant' },
    hasNext: !!view && view.on && !view.hidden && !!view.step && (view.step.ask ?? '').trim() !== '',
    nextNeedsAssistant: !!view?.step && !view.step.doIt,
  });
  const who = slot.kind === 'press' || slot.kind === 'work' ? slot.who : slot.kind === 'next' ? 'assistant' : slot.kind === 'done' ? 'semanticus' : null;
  return <span className={`pc-slot${who ? ` pc-who-${who}` : ''}`} data-testid="plan-slot" data-slot={slot.kind}>
    <LinkMark state={work.state} size={20} className="pc-link" />
    {/* The verb is decoration: a screen reader hears the plain fact once, not every 2.3 seconds. */}
    <span className="sr-only" role="status">{slot.kind === 'press' || slot.kind === 'work' ? 'Working' : ''}</span>
    {(slot.kind === 'press' || slot.kind === 'work') && <>
      <span className="pc-who-name">{slot.who === 'assistant' ? 'Your assistant' : 'Semanticus'}</span>
      <span className="pc-verb"><RollingWords text={slot.verb} reduced={settings.reducedMotion} /></span>
      {slot.count && <span className="sem-num plan-muted pc-count" data-testid="plan-slot-count">{slot.count}</span>}
    </>}
    {slot.kind === 'done' && <><span className="pc-done" role="status" data-testid="plan-slot-done">{slot.text}</span>{receiptActions}</>}
    {slot.kind === 'said' && <span className="pc-said" role="status" data-testid="plan-slot-said">{slot.text}</span>}
    {slot.kind === 'wheel' && <><span className="pc-said">{slot.text}</span>
      <button type="button" className="sem-btn sem-btn-sm" data-testid="driver-resume" onClick={() => driverStore.letClaudeDrive()}>Let your assistant drive</button></>}
    {slot.kind === 'quiet' && <span className="plan-muted pc-quiet" data-testid="plan-slot-quiet">{slot.text}</span>}
    {slot.kind === 'next' && <NextSuggestion />}
    {slot.kind === 'connect' && <><span className="plan-muted pc-quiet" data-testid="plan-slot-connect">{slot.text}</span>
      <button type="button" className="sem-btn sem-btn-sm pc-ask" data-testid="plan-connect" onClick={() => runHostCommand('semanticus.connectClaudeCode')}>{CONNECT_LABEL}</button></>}
  </span>;
}

/** The assistant's next suggested step: its words in muted text, and a small "Ask your assistant" press. The slot stays while the
 *  next step is worked out (the store holds it), and a REAL change is one quiet cross-fade (STEP_FADE_MS). Reduced motion: no fade. */
function NextSuggestion() {
  const { view } = useNextStep();
  const { reducedMotion } = usePersonalitySettings();
  const { busy, mine, buttons, press } = useNextActions(view ?? null);
  const step = view?.step;
  // The words that are fading out: set when the step on show changes to another, never on the first paint.
  const last = useRef<{ id: string; ask: string } | null>(null);
  const [out, setOut] = useState<{ id: string; ask: string } | null>(null);
  const id = view?.id ?? null;
  useEffect(() => {
    const prev = last.current;
    last.current = step && id ? { id, ask: step.ask } : prev;
    if (!prev || !step || !id || prev.id === id || reducedMotion) return undefined;
    setOut(prev);
    const t = setTimeout(() => setOut(null), STEP_FADE_MS);
    return () => clearTimeout(t);
  }, [id, step, reducedMotion]);
  if (!view || !step) return null;
  const assistants = !step.doIt;
  return <>
    <span className="pc-xfade">
      {out && <span className="pc-next pc-next-out" aria-hidden="true" data-testid="plan-next-out"><span className="pw-trunc">{out.ask}</span></span>}
      <span key={view.id} className={`pc-next${assistants ? ' pc-next-assistant' : ''}${out ? ' pc-next-in' : ''}`} title={step.why ?? undefined} data-testid="plan-next-words">
        {assistants && <DoerMark doer="assistant" />}<span className="pw-trunc">{step.ask}</span></span>
    </span>
    <span className="pc-next-acts" data-testid="plan-next-acts">
      {mine
        ? <span className={mine.failed ? 'nx-failed' : 'nx-done'} role="status" data-testid="next-done">{mine.text}</span>
        : buttons.map((b) => <button key={b.act} type="button" disabled={busy} data-testid={`next-${b.act}`} data-keeps-wheel={b.act === 'ask' ? '' : undefined}
          className="sem-btn sem-btn-sm pc-ask" onClick={() => press(b.act)}>{b.label}</button>)}
    </span>
  </>;
}

export function PlanHead({ view, onView, grade, strip, slot, drive, run }: {
  view: 'work' | 'hist'; onView: (v: 'work' | 'hist') => void;
  grade: Grade | null; strip: PlanStrip | null | undefined; slot: ReactNode; drive: ReactNode; run: ReactNode;
}) {
  // Flat children, so a narrow Studio can put the Run button beside the title, the figures under it and the switch under the slot (plan-calm.css).
  return <header className="pw-head pc-head" data-testid="plan-head" data-tour="head">
    <h1 className="sem-page-title">Plan</h1>
    <PlanFigures grade={grade} strip={strip} />
    <span className="pc-slot-host">{slot}</span>
    <span className="pc-drive">{drive}</span>
    <span className="sem-seg pc-views" role="group" aria-label="Work or History">
      <button type="button" className="sem-seg-item" aria-pressed={view === 'work'} data-testid="plan-view-work" onClick={() => onView('work')}>Work</button>
      <button type="button" className="sem-seg-item" aria-pressed={view === 'hist'} data-testid="plan-view-hist" data-tour="history" onClick={() => onView('hist')}>History</button>
    </span>
    <span className="pc-run">{run}</span>
  </header>;
}
