// The assistant's run of checks, as the Validation page sees it (show-checks, 12 Oct). The note moves the page; this says what the page
// shows once it is there: "running" while the assistant's call is open, then the run the assistant measured, or why there is none.
// Pure, so test/studio-wire.test.mjs drives every step. The page keeps one of these and folds a finished run into its own state.

export type AssistantRun = {
  phase: 'idle' | 'running' | 'done' | 'failed';
  startedAt: number | null;
  /** The engine's whole run (TestSuiteRunResult), the same object the page's own Run button gets back. */
  run: any | null;
  label: string | null;
  error: string | null;
  /** The activity that made this state, so the same result twice changes nothing. */
  seq: number | null;
};

export const NO_ASSISTANT_RUN: AssistantRun = { phase: 'idle', startedAt: null, run: null, label: null, error: null, seq: null };

/** A call that never reports back (the assistant stopped, the engine restarted) stops saying "running" after this long. */
export const RUN_WAIT_MS = 5 * 60_000;

export const CHECK_RUN_TOOL = 'run_tests';
export const CHECK_RUN_KIND = 'run_tests';

type Finished = { seq?: number; kind?: string; ok?: boolean; label?: string; error?: string | null; result?: any };

export function runStarted(view: AssistantRun, call: { tool: string; at: string }, now: number): AssistantRun {
  if (call.tool !== CHECK_RUN_TOOL) return view;
  const at = Date.parse(call.at);
  return { ...NO_ASSISTANT_RUN, phase: 'running', startedAt: Number.isFinite(at) ? at : now };
}

export function runFinished(view: AssistantRun, event: Finished): AssistantRun {
  if (event.kind !== CHECK_RUN_KIND) return view;
  if (event.seq !== undefined && view.seq === event.seq && view.phase !== 'running') return view;
  const result = event.result && typeof event.result === 'object' ? event.result : null;
  const error = event.error || result?.error || (!result ? 'The run did not finish.' : null);
  if (error) return { phase: 'failed', startedAt: view.startedAt, run: null, label: event.label ?? null, error, seq: event.seq ?? null };
  return { phase: 'done', startedAt: view.startedAt, run: result, label: event.label ?? null, error: null, seq: event.seq ?? null };
}

export function runStale(view: AssistantRun, now: number): AssistantRun {
  return view.phase === 'running' && view.startedAt !== null && now - view.startedAt >= RUN_WAIT_MS ? NO_ASSISTANT_RUN : view;
}

/** The person pressed Run themselves: their run is the one on screen now. */
export function personRan(_view: AssistantRun): AssistantRun {
  return NO_ASSISTANT_RUN;
}

/** The one line above the results: what the assistant is doing, did, or could not do. Null when there is nothing to say. */
export function runLine(view: AssistantRun): string | null {
  if (view.phase === 'running') return 'Your assistant is running your checks.';
  if (view.phase === 'failed') return view.error;
  if (view.phase === 'done') return view.label;
  return null;
}

/** A scan the assistant asked for: the page that shows it scans again at once, so the person sees the result and not an older card.
 *  Keyed by the MCP tool; the value is the page's route id (route.ts). */
export const SCAN_PAGE_OF_TOOL: Record<string, 'bpa' | 'readiness'> = { bpa_scan: 'bpa', ai_readiness_scan: 'readiness' };
