// The Learn tour in the running Studio (1.4, contract section 9). The rules are in tour-model.ts; this file binds them to the
// notes store, the driver store, the host and a small bar at the foot of the page.
//
// THE FLOW. Help's Learn list (or the first run's "Show me around") calls startTour(chapterId). The host copies the demo model to a
// scratch folder, opens it, remembers the model that was open, and answers `learnReady` with the demo session. The player then
// plays each step as a note with by "tour" through the one notes store, so the strip, the screen move and the cursor are the
// ones the assistant's notes use. Leaving the tour asks the host to reopen the earlier model.
//
// WHAT THE TOUR NEVER DOES. It calls no engine tool and no assistant, it writes nothing to the person's own model, and it does
// not press anything: the cursor shows the control and stops. The person's drive mode is borrowed (the screen must move for the
// tour whatever the setting was) and given back when the tour ends.
import { useSyncExternalStore } from 'react';
import { createRoot } from 'react-dom/client';
import { runHostCommand } from '../bridge';
import { driverStore, type DriveMode } from './driver-store';
import { notesStore } from './notes-runtime';
import placeMap from './places.json';
import chapterIndex from '../../../assets/learn/index.json';
import planChapter from '../../../assets/learn/plan.json';
import askChapter from '../../../assets/learn/ask.json';
import editChapter from '../../../assets/learn/edit.json';
import reviewChapter from '../../../assets/learn/review.json';
import publishChapter from '../../../assets/learn/publish.json';
import recorded from '../../../assets/learn/contoso-shop/answers.json';
import { createTourPlayer, endLine, type ChapterEntry, type RecordedAnswer, type TourChapter } from './tour-model';
import { registerLearn, setLearnView, useLearn, type LearnView, type LearnRuntime } from './learn-state';
import './studio-tour.css';

export { startTour } from './learn-state';

export const CHAPTERS: readonly ChapterEntry[] = chapterIndex.chapters;
const SCRIPTS: Record<string, TourChapter> = {
  plan: planChapter as TourChapter, ask: askChapter as TourChapter, edit: editChapter as TourChapter,
  review: reviewChapter as TourChapter, publish: publishChapter as TourChapter,
};
export const RECORDED_ANSWERS: readonly RecordedAnswer[] = recorded.answers as RecordedAnswer[];

const START_FAILED = 'The demo model could not open. Your own model is unchanged.';

const player = createTourPlayer({
  setTimer: (fn, ms) => window.setTimeout(fn, ms),
  clearTimer: (handle) => window.clearTimeout(handle as number),
  push: (note) => notesStore.push(note),
  canMove: () => driverStore.canMoveScreen(),
  places: placeMap.places,
  now: () => new Date(),
});

let runtime: LearnRuntime = { phase: 'idle', chapterId: null, message: null };
let borrowedMode: DriveMode | null = null;
let offDriver: (() => void) | null = null;
function publish(next?: Partial<LearnRuntime>) {
  if (next) runtime = { ...runtime, ...next };
  setLearnView({ runtime, tour: player.getSnapshot() });
}
player.subscribe(() => publish());

function stepOf(chapterId: string | null, index: number) {
  return chapterId ? SCRIPTS[chapterId]?.steps[index] : undefined;
}

// ---- the host's replies ----------------------------------------------------------------------
let wired = false;
function ensureWire() {
  if (wired || typeof window === 'undefined') return;
  wired = true;
  window.addEventListener('message', (event: MessageEvent) => {
    const m = event.data as { type?: string; chapter?: string; sessionId?: string; message?: string } | undefined;
    if (!m || typeof m.type !== 'string' || !m.type.startsWith('learn')) return;
    if (m.type === 'learnReady' && runtime.phase === 'starting' && m.chapter === runtime.chapterId && SCRIPTS[m.chapter]) {
      begin(SCRIPTS[m.chapter], m.sessionId ?? '');
    } else if (m.type === 'learnFailed' && runtime.phase === 'starting') {
      publish({ phase: 'failed', message: m.message || START_FAILED });
    } else if (m.type === 'learnCancelled' && runtime.phase === 'starting') {
      publish({ phase: 'idle', chapterId: null, message: null });
    } else if (m.type === 'learnEnded' && runtime.phase !== 'idle') {
      // The host ended the tour (another model opened). Close the bar without asking the host to leave again.
      endHere();
    }
  });
}

function begin(chapter: TourChapter, sessionId: string) {
  // The screen has to move for the tour whatever the person's setting was. It is given back in leaveTour.
  if (borrowedMode === null) borrowedMode = driverStore.getSnapshot().mode;
  driverStore.setMode('claudeDrives');
  driverStore.letClaudeDrive();
  notesStore.reset();
  offDriver?.();
  // Taking the wheel, or switching to I drive, stops the tour where it is. Continue gives it back.
  offDriver = driverStore.subscribe(() => { if (!driverStore.canMoveScreen()) player.interrupt(); });
  publish({ phase: 'running', chapterId: chapter.id, message: null });
  player.start(chapter, sessionId);
}

/** Starts a chapter (learn-state.ts startTour reaches this). */
function startChapter(chapterId: string): void {
  if (!SCRIPTS[chapterId]) return;
  ensureWire();
  ensureRoot();
  player.stop();
  publish({ phase: 'starting', chapterId, message: null });
  runHostCommand('semanticus.learn.start', chapterId);
}

/** Leaves the tour: the host reopens the model that was open before, the person's drive mode comes back, the notes are cleared. */
export function leaveTour(): void {
  const wasRunning = runtime.phase === 'running' || runtime.phase === 'failed' || runtime.phase === 'starting';
  endHere();
  if (wasRunning) runHostCommand('semanticus.learn.end');
}

function endHere(): void {
  player.stop();
  offDriver?.(); offDriver = null;
  if (borrowedMode !== null) { driverStore.setMode(borrowedMode); borrowedMode = null; }
  notesStore.reset();
  publish({ phase: 'idle', chapterId: null, message: null });
}

function continueTour() {
  driverStore.setMode('claudeDrives');
  driverStore.letClaudeDrive();
  player.resume();
}

function nextChapterOf(id: string | null): ChapterEntry | undefined {
  const at = CHAPTERS.findIndex((c) => c.id === id);
  return at >= 0 ? CHAPTERS[at + 1] : undefined;
}

// ---- the bar ---------------------------------------------------------------------------------------------------------
registerLearn({ chapters: CHAPTERS, examples: RECORDED_ANSWERS, start: startChapter });

let root: HTMLElement | null = null;
function ensureRoot() {
  if (root || typeof document === 'undefined') return;
  root = document.createElement('div');
  root.setAttribute('data-testid', 'tour-root');
  document.body.appendChild(root);
  createRoot(root).render(<TourBar />);
}

export function TourBarView({ view: v, assistantConnected, onNext, onContinue, onLeave, onChapter }: {
  view: LearnView; assistantConnected: boolean;
  onNext: () => void; onContinue: () => void; onLeave: () => void; onChapter: (id: string) => void;
}) {
  const { runtime: rt, tour } = v;
  if (rt.phase === 'idle') return null;
  const chapter = rt.chapterId ? SCRIPTS[rt.chapterId] : undefined;
  const next = nextChapterOf(rt.chapterId);
  let body: React.ReactNode;
  if (rt.phase === 'starting') {
    body = <span role="status" className="st-say">Opening the demo model.</span>;
  } else if (rt.phase === 'failed') {
    body = <>
      <span role="alert" className="st-say">{rt.message || START_FAILED}</span>
      <button type="button" className="sem-btn sem-btn-sm" data-testid="tour-leave" onClick={onLeave}>Close</button>
    </>;
  } else if (tour.status === 'done' && chapter) {
    body = <>
      <span role="status" className="st-say" data-testid="tour-end">{endLine(chapter, assistantConnected)}</span>
      <span className="st-acts">
        {next && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="tour-next-chapter" onClick={() => onChapter(next.id)}>Next: {next.title}</button>}
        <button type="button" className="sem-btn sem-btn-sm" data-testid="tour-leave" onClick={onLeave}>Leave the tour</button>
      </span>
    </>;
  } else if (tour.status === 'paused') {
    body = <>
      <span role="status" className="st-say" data-testid="tour-paused">You took the wheel. The tour waits for you.</span>
      <span className="st-acts">
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="tour-continue" onClick={onContinue}>Continue</button>
        <button type="button" className="sem-btn sem-btn-sm" data-testid="tour-leave" onClick={onLeave}>Leave the tour</button>
      </span>
    </>;
  } else {
    body = <>
      <span role="status" className="st-say" data-testid="tour-step">
        {chapter?.title ?? 'Learn'}, step {Math.min(tour.index + 1, tour.total)} of {tour.total}
        {tour.waitingForPress ? '. Press the highlighted control.' : ''}
      </span>
      <span className="st-acts">
        <button type="button" className="sem-btn sem-btn-sm" data-testid="tour-next" onClick={onNext}>Next</button>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="tour-leave" onClick={onLeave}>Leave the tour</button>
      </span>
    </>;
  }
  return <div className="st-bar" data-testid="tour-bar" data-tour-bar role="region" aria-label="Learn Semanticus">{body}</div>;
}

function TourBar() {
  const v = useLearn();
  const driver = useSyncExternalStore(driverStore.subscribe, driverStore.getSnapshot, driverStore.getSnapshot);
  return <TourBarView view={v} assistantConnected={driver.assistantConnected}
    onNext={() => { const snap = player.getSnapshot(); if (snap.status === 'paused') continueTour(); else player.next(); }}
    onContinue={continueTour} onLeave={leaveTour} onChapter={startChapter} />;
}
