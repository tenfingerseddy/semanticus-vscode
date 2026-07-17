// ===================================================================================================
// The workflow run-map reducer — the single source of truth for "which runs are live right now".
//
// Why a MAP and not one tracked run: the engine holds up to 8 concurrent runs (Workflow.cs WorkflowRunStore),
// and each transition broadcasts ONE run view (workflow/didChange). A single `run` slot was blindly replaced by
// every broadcast, so run B completing while A was live nulled the banner, then A's next transition flipped it
// back (the flicker BLOCKER). Keyed by runId, a terminal event touches only its own run and the live COUNT stays
// honest. Lifting the subscription above the tab mount (App.tsx) means an agent-started run is captured even
// while Workflows is unmounted; getWorkflowRun seeds the most-recent run that started before we subscribed.
//
// Pure + framework-free ON PURPOSE: this is the one piece of run logic with real branching, so it lives in a
// plain ES module the node test suite imports directly (test/workflowruns.test.mjs) — behavioral coverage the
// source-text regex tests can't give. The webview imports the same file (types in workflowruns.d.mts).
// ===================================================================================================

// Mirror the engine's own bound EXACTLY (WorkflowRunStore.MaxHeld = 8). The UI must never hold more runs than the
// engine keeps: an extra terminal run the engine has already evicted would offer an Evidence report that then fails
// with "run not found". Same eviction discipline as the engine store — oldest TERMINAL run dropped first, a live
// run never evicted. (A run that ages out of both is still handled honestly at the Evidence door.)
export const MAX_RUNS = 8;

/** A run is "live" only while active; terminal runs stay for their evidence/receipt but leave the live set. */
export function isActive(run) {
  return !!run && run.status === 'active';
}

/** The empty reducer state for a given model session. `runs` is start-ordered and unique by runId (a JS array,
 *  not a Map, so it stays trivially serializable and testable). `sessionId` is the generation token: a fold
 *  stamped for a different session is dropped (a model swap must not carry the prior model's runs). */
export function emptyRuns(sessionId = null) {
  return { runs: [], sessionId: sessionId ?? null };
}

/** Monotonic PROGRESS rank of a run view: strictly rises as the run advances, and any TERMINAL state outranks
 *  every active step. This is the marker the ordering-safe fold compares — a broadcast whose rank is strictly
 *  below what we already hold for that runId is a stale/out-of-order delivery and is ignored, so a completed run
 *  can never be resurrected to active and a run can never step backwards. (Step granularity is enough: within one
 *  active step, latest content legitimately wins.) */
export function runRank(run) {
  if (!run || !run.runId) return -1;
  if (run.status && run.status !== 'active') return Number.MAX_SAFE_INTEGER;   // terminal band, above all active steps
  return typeof run.stepIndex === 'number' ? run.stepIndex : 0;
}

/** Lexicographic ISO-8601 start key (engine emits round-trip "o" timestamps, which sort chronologically). Missing
 *  timestamps collapse to '' so a stable sort leaves them in insertion order. */
function startKey(run) {
  return typeof run?.startedUtc === 'string' && run.startedUtc ? run.startedUtc : '';
}

/** Fold ONE run view into the map, ORDERING-SAFELY (HIGH 1). Three fold kinds:
 *   - a live BROADCAST (default) replaces the tracked view only when it is not strictly older by {@link runRank};
 *   - a SEED (`opts.seed`) only fills a gap and NEVER overwrites an entry already present — its snapshot may
 *     predate a broadcast that already advanced or completed the run, so replacing would resurrect a ghost;
 *   - a RECONCILE (`opts.reconcile`) is an authoritative by-id refetch after a pipe RECONNECT (broadcasts that
 *     fired while disconnected are NOT replayed, so a same-session reconnect could otherwise strand a ghost live
 *     run). Like a broadcast it MAY update an existing entry, RANK-CHECKED so it never rewinds a view the live
 *     stream already carried past the refetch — so a ghost still-active run whose refetch is terminal is moved to
 *     terminal, while a run the snapshot omits but the refetch still calls active stays active (trust the newer
 *     by-id). A NOT-FOUND refetch (`opts.notFound`, `incoming = { runId }`) means the engine no longer holds the
 *     run: a locally-ACTIVE ghost is EVICTED, but a terminal receipt we already hold is kept.
 *  A generation guard drops any fold whose `opts.sessionId` disagrees with the map's session (a swap raced the
 *  request). The map is re-sorted by start time on every fold so parallel seed/broadcast responses can never
 *  scramble the order that drives the banner, default focus and eviction. Returns a NEW state (no mutation). */
export function reduceRuns(state, incoming, opts = {}) {
  if (!incoming || !incoming.runId) return state;
  const prev = state?.runs ?? [];
  const sessionId = state?.sessionId ?? null;
  // Generation guard (finding 4): a fold requested under a prior model session must not land in the new map.
  if (opts.sessionId != null && sessionId != null && opts.sessionId !== sessionId) return state;
  const idx = prev.findIndex((r) => r.runId === incoming.runId);
  // Reconciliation eviction: a not-found by-id refetch on reconnect means the engine no longer holds the run.
  // Drop only a locally-ACTIVE ghost — a terminal entry is a receipt we keep (a racing broadcast may have landed
  // it), and an unknown id is nothing to evict. The reconnect path only asks for this when the authoritative
  // snapshot also omits the run from the live set, so a still-live run can never be evicted out from under us.
  if (opts.reconcile && opts.notFound) {
    if (idx < 0 || !isActive(prev[idx])) return state;
    return { runs: prev.slice(0, idx).concat(prev.slice(idx + 1)), sessionId };
  }
  let runs;
  if (idx < 0) {
    runs = [...prev, incoming];
  } else if (opts.seed) {
    return state;   // a seed fills gaps only; never overwrite a live entry (no stale resurrection)
  } else if (runRank(incoming) < runRank(prev[idx])) {
    return state;   // a strictly older broadcast (reordered delivery) — ignore it
  } else {
    runs = prev.slice();
    runs[idx] = incoming;
  }
  // Stable start-order: equal/absent keys keep insertion order, so real timestamps reconstruct start order
  // regardless of which async response landed first.
  runs = runs.slice().sort((a, b) => (startKey(a) < startKey(b) ? -1 : startKey(a) > startKey(b) ? 1 : 0));
  // Evict oldest terminal runs until within bound (a live run is never dropped — the engine refuses an over-cap
  // start instead, so we can't exceed the live count here). After the sort, the first terminal is the oldest.
  while (runs.length > MAX_RUNS) {
    const drop = runs.findIndex((r) => r.status !== 'active');
    if (drop < 0) break;
    runs = runs.slice(0, drop).concat(runs.slice(drop + 1));
  }
  return { runs, sessionId };
}

/** The live runs, in start order (oldest first). */
export function liveRuns(state) {
  return (state?.runs ?? []).filter(isActive);
}

/** The terminal (completed/aborted/…) runs, in start order. */
export function terminalRuns(state) {
  return (state?.runs ?? []).filter((r) => !isActive(r));
}

/** The most recently started LIVE run, or null. */
export function mostRecentLive(state) {
  const live = liveRuns(state);
  return live.length ? live[live.length - 1] : null;
}

/** The most recently started TERMINAL run, or null (drives Home's recent-run card + last-completed-run panel). */
export function mostRecentTerminal(state) {
  const term = terminalRuns(state);
  return term.length ? term[term.length - 1] : null;
}

/** A run by id, or null. */
export function runById(state, runId) {
  if (!runId) return null;
  return (state?.runs ?? []).find((r) => r.runId === runId) ?? null;
}

/** The most recent run (live or terminal) for a given workflow name — the playbook page shows its own run. */
export function runForWorkflow(state, workflowName) {
  if (!workflowName) return null;
  const matches = (state?.runs ?? []).filter((r) => r.workflow === workflowName);
  return matches.length ? matches[matches.length - 1] : null;
}

/** The run the Runs section focuses: the explicit selection when it still exists, else the most recent live run,
 *  else the most recent terminal run. So a completed run stays visible, and a new live run auto-focuses. */
export function focusedRun(state, selectedRunId) {
  return runById(state, selectedRunId) ?? mostRecentLive(state) ?? mostRecentTerminal(state);
}

/** Classify a getWorkflowRun REFETCH outcome (the reconnect reconciliation) as an AUTHORITATIVE run-not-found —
 *  the engine genuinely no longer holds the run — versus everything else. This is the guard on eviction: a run
 *  that COMPLETED while the pipe was down, then hits a TRANSPORT failure (pipe drop, timeout) on its refetch, must
 *  NOT be read as absence. Evicting on a transport error would lose the terminal receipt and its Evidence path
 *  forever; only a real not-found evicts, anything else preserves the entry for a later reconciliation pass.
 *
 *  The engine reports a miss the same two ways the evidence door does (mirror that one normalization): it REJECTS
 *  with "Workflow run '<id>' not found." (Workflow.cs WorkflowRunStore.Require — getWorkflowRun today only
 *  rejects), or — handled defensively — could RESOLVE a note-only view carrying that phrase instead of a real run
 *  view. Pass the resolved value OR the rejection reason (Error / string / object). A real run view (it carries a
 *  status or steps) is never a miss. We match the engine's SPECIFIC phrase, not a bare "not found", so a
 *  mis-worded transport error can never be mistaken for an absence and evict a live receipt. */
export function isRunNotFound(outcome) {
  if (outcome == null) return false;
  let text = '';
  if (outcome instanceof Error) text = outcome.message ?? '';
  else if (typeof outcome === 'string') text = outcome;
  // A resolved value is a miss only when it is NOT a real run view (no status/steps) but carries the not-found note.
  else if (typeof outcome === 'object' && outcome.status == null && outcome.steps == null && typeof outcome.note === 'string') text = outcome.note;
  // Anchored to the engine's EXACT throw (WorkflowRunStore.Require): a wrapped transport error that merely
  // CONTAINS the words ("Failed to fetch workflow run: RPC method not found") must never read as an absence.
  return /^Workflow run '[^']+' not found\.$/.test(String(text).trim());
}

/** Is THIS step's gate skipped? The engine freezes strictness at start (Workflow.cs), and EffectiveStrictness
 *  returns "off" for a step whose gate does not bite — whether from a run started with global enforcement off, a
 *  per-workflow/per-gate "off", or a definition default. The wire exposes only this PER-STEP snapshot: there is no
 *  run-level "enforcement was off at start" field, so the UI must speak per step and never infer a run-wide claim
 *  (an early "off" step + a later HARD step is a legitimate mix). Render THIS snapshot, never the model-wide flag. */
export function stepGateSkipped(effectiveStrictness) {
  return effectiveStrictness === 'off';
}
