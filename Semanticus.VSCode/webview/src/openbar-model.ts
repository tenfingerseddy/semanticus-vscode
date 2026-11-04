// The Connections bar's rules and words (cx2, option A of the Connect mock-up, Kane 3 Nov 11:44).
//
// While a model opens, the engine tells the window each step as it starts (progress/didChange, opKey "open": "Signing in",
// "Connecting to the workspace", ...), with the step number and how many there are. The bar shows "Step n of 5 · <step> · <s> s" and
// five equal marks. It never shows a percent: the engine knows the step, not how long the next one takes. The page counts the seconds.
// An event with no words ends the steps. An event says when Cancel can no longer change nothing (the new model is already open).

export const OPEN_PROGRESS_KEY = 'open';

export interface OpenProgressEvent {
  opKey: string; runId: string; done: number; total: number;
  note?: string | null; origin?: string | null; cancellable?: boolean | null;
}

/** One open the window started: what it opens, and where the engine has got to. */
export interface OpenRun {
  target: string;          // the model's name
  where: string;           // "in Contoso Sales", or the file's place
  runId: string | null;    // the engine's id for this open, from its first event: what Cancel names
  step: number;            // 0 until the engine names a step
  total: number;
  words: string;
  since: number;           // when the open started (page clock, ms)
  stepSince: number;       // when the step now showing started
  cancellable: boolean;
  ended: boolean;          // the engine sent its last event; the open's answer is on its way
  cancelling: boolean;     // Cancel was pressed; the engine's answer is on its way
}

export function startOpenRun(target: string, where: string, total: number, now: number): OpenRun {
  return { target, where, runId: null, step: 0, total, words: '', since: now, stepSince: now, cancellable: true, ended: false, cancelling: false };
}

/** Fold one engine event into the run. Another kind of progress, an assistant's open, another run's events and a late event that
 *  would move the step back change nothing. */
export function foldOpenProgress(run: OpenRun | null, e: OpenProgressEvent | null | undefined, now: number): OpenRun | null {
  if (!run || !e || e.opKey !== OPEN_PROGRESS_KEY) return run;
  if (e.origin && e.origin !== 'human') return run;
  if (run.runId && e.runId !== run.runId) return run;
  const bound = run.runId ?? e.runId;
  const words = (e.note ?? '').trim();
  if (!words) return { ...run, runId: bound, ended: true, cancellable: e.cancellable === true ? run.cancellable : false };
  if (e.done < run.step) return { ...run, runId: bound };
  const moved = e.done !== run.step || words !== run.words;
  return {
    ...run, runId: bound, step: e.done, total: e.total > 0 ? e.total : run.total, words,
    stepSince: moved ? now : run.stepSince,
    cancellable: e.cancellable !== false,
  };
}

const seconds = (ms: number) => Math.max(0, Math.floor(ms / 1000));

/** "Step 2 of 5 · Connecting to the workspace · 3 s". Before the engine names a step: "Starting the open · 0 s". */
export function openStepLine(run: OpenRun, now: number): string {
  const s = `${seconds(now - run.since)} s`;
  // The engine has finished; the window is showing the new model (cx3: the bar kept "Connecting for queries" for 6 s while it was not).
  if (run.ended) return `${SHOWING_STEP} · ${s}`;
  if (run.step <= 0 || !run.words) return `Starting the open · ${s}`;
  return `Step ${run.step} of ${run.total} · ${run.words} · ${s}`;
}

/** One mark per step: done, now, or next. */
export function openPips(run: OpenRun): ('done' | 'now' | 'next')[] {
  const out: ('done' | 'now' | 'next')[] = [];
  for (let k = 1; k <= Math.max(1, run.total); k++) out.push(run.ended || k < run.step ? 'done' : k === run.step ? 'now' : 'next');
  return out;
}

export const BROWSER_WAIT = 'Finish the sign-in in your browser. The open goes on when you are done.';
const BROWSER_AFTER_MS = 3000;

/** A browser sign-in waits on the person in another window, for up to two minutes (tester note 4). After a moment on that step the
 *  bar says where to look, or the open looks frozen again. A sign-in with no browser (a service identity) never says it. */
export function browserWaitLine(run: OpenRun | null, now: number, interactive: boolean): string | null {
  if (!run || !interactive || run.step !== 1 || run.ended) return null;
  return now - run.stepSince >= BROWSER_AFTER_MS ? BROWSER_WAIT : null;
}

export const stoppedLine = (name: string) => `Stopped. ${name} did not open, and nothing changed.`;
export const pendingLine = (name: string) => `Cancel this open to open ${name} instead.`;
export const tooLateLine = (name: string) => `${name} was already open, so it was not stopped.`;
export const LATE_STEP = 'The model is open. It can no longer be stopped.';
export const SHOWING_STEP = 'Showing the model';

/** The line under the bar for a row picked while an open runs. Only while Cancel can still make way for it: after the point of no return
 *  the bar has no Cancel, so it never says "Cancel this open" beside "It can no longer be stopped." (cx3, tester note 2). */
export function pickDuringOpenLine(run: OpenRun | null, pickedName: string | null | undefined): string | null {
  if (!run || !pickedName || pickedName === run.target || !run.cancellable || run.ended) return null;
  return pendingLine(pickedName);
}

/** True when an open's failure is the engine's answer to Cancel (its own sentence), so the bar shows it as a stop, not a fault. */
export const isStoppedAnswer = (message: string | null | undefined): boolean => /did not open, and nothing changed/i.test(message || '');
