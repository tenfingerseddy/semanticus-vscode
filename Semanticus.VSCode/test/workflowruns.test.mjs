import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  emptyRuns, reduceRuns, liveRuns, terminalRuns, mostRecentLive, mostRecentTerminal,
  runById, runForWorkflow, focusedRun, stepGateSkipped, runRank, isRunNotFound, MAX_RUNS,
} from '../webview/src/workflowruns.mjs';

// ===================================================================================================
// Behavioral coverage for the run-map reducer — the logic behind BLOCKER 2 (multi-run isolation) and
// BLOCKER 3 (frozen enforcement snapshot). Source-text regex can't catch these paths, so we execute them.
// ===================================================================================================

const run = (runId, over = {}) => ({
  runId, workflow: over.workflow ?? 'verified-measure', title: over.title ?? 'Verified measure',
  status: over.status ?? 'active', stepIndex: 0, totalSteps: 4, steps: over.steps ?? [],
  currentStep: over.currentStep ?? null, ...over,
});

// ---- add / replace / append ----------------------------------------------------------------------
{
  let s = emptyRuns();
  assert.equal(liveRuns(s).length, 0, 'empty state has no live runs');
  s = reduceRuns(s, run('A'));
  s = reduceRuns(s, run('B'));
  assert.deepEqual(liveRuns(s).map((r) => r.runId), ['A', 'B'], 'two distinct runs both stay live, in start order');

  // A transition on A REPLACES A in place (same runId) — it must not append a duplicate nor reorder.
  s = reduceRuns(s, run('A', { stepIndex: 2 }));
  assert.deepEqual(liveRuns(s).map((r) => r.runId), ['A', 'B'], 'a re-broadcast of A keeps order and does not duplicate');
  assert.equal(runById(s, 'A').stepIndex, 2, 'the re-broadcast view replaces the tracked view');
}

// ---- terminal isolation: B completing must NOT disturb A (the flicker BLOCKER) --------------------
{
  let s = reduceRuns(reduceRuns(emptyRuns(), run('A')), run('B'));
  s = reduceRuns(s, run('B', { status: 'completed' }));
  assert.deepEqual(liveRuns(s).map((r) => r.runId), ['A'], 'B going terminal removes only B from the live set');
  assert.equal(liveRuns(s).length, 1, 'the live COUNT reflects only still-active runs');
  assert.equal(mostRecentLive(s).runId, 'A', 'A stays the focused live run through B completing');
  assert.equal(mostRecentTerminal(s).runId, 'B', 'B is reachable as the most recent terminal run');
}

// ---- focus selection: explicit → most-recent-live → most-recent-terminal -------------------------
{
  let s = reduceRuns(reduceRuns(emptyRuns(), run('A')), run('B'));
  assert.equal(focusedRun(s, null).runId, 'B', 'with no selection the most-recent live run is focused');
  assert.equal(focusedRun(s, 'A').runId, 'A', 'an explicit selection is honored while it exists');
  s = reduceRuns(s, run('A', { status: 'aborted' }));
  s = reduceRuns(s, run('B', { status: 'completed' }));
  assert.equal(focusedRun(s, null).runId, 'B', 'with nothing live the most-recent terminal run is focused');
  assert.equal(focusedRun(s, 'ghost'), mostRecentTerminal(s), 'a stale selection falls back, never returns null while runs exist');
}

// ---- runForWorkflow: the playbook page shows ITS run ---------------------------------------------
{
  let s = reduceRuns(emptyRuns(), run('A', { workflow: 'make-ai-ready' }));
  s = reduceRuns(s, run('B', { workflow: 'verified-measure' }));
  assert.equal(runForWorkflow(s, 'make-ai-ready').runId, 'A', 'the playbook run is matched by workflow name');
  assert.equal(runForWorkflow(s, 'nope'), null, 'an unrun workflow has no run');
}

// ---- bound: mirrors the engine store EXACTLY; oldest TERMINAL evicted first, live never dropped (MEDIUM 7) ---
{
  assert.equal(MAX_RUNS, 8, 'the UI bound mirrors the engine store size exactly (WorkflowRunStore.MaxHeld = 8) so it never offers a receipt the engine has evicted');
  let s = emptyRuns();
  s = reduceRuns(s, run('live', { status: 'active' }));
  for (let i = 0; i < MAX_RUNS + 5; i++) s = reduceRuns(s, run(`t${i}`, { status: 'completed' }));
  assert.equal(s.runs.length, MAX_RUNS, 'the tail is bounded to exactly the engine store size — no phantom terminal receipts');
  assert.ok(runById(s, 'live'), 'the live run is never evicted by the bound');
  assert.ok(!runById(s, 't0'), 'the oldest terminal run is evicted first');
}

// ---- HIGH 6: per-step frozen gate-skip snapshot (no run-wide inference) ---------------------------
{
  assert.equal(stepGateSkipped('off'), true, 'a step whose frozen strictness is off is skipped');
  assert.equal(stepGateSkipped('hard'), false, 'a hard step is not skipped');
  assert.equal(stepGateSkipped(null), false, 'a gateless step is not "skipped"');
}

// ---- HIGH 1: ordering-safe fold — the seed/broadcast interleavings (finding 4) --------------------
// The whole seed-vs-broadcast decision lives HERE in the pure reducer, so these races are executed, not
// regex-asserted. runRank is the monotonic marker: terminal outranks every active step; higher step beats lower.
{
  assert.ok(runRank(run('X', { status: 'completed' })) > runRank(run('X', { stepIndex: 99 })),
    'any terminal state outranks every active step (a completed run can never be rewound to active)');
  assert.ok(runRank(run('X', { stepIndex: 3 })) > runRank(run('X', { stepIndex: 1 })),
    'a further-along active step outranks an earlier one');
}

// terminal-broadcast-then-stale-seed: a completed run must NOT be resurrected by a seed snapshot taken while it
// was still active but that lands after the completion broadcast.
{
  let s = emptyRuns('S1');
  s = reduceRuns(s, run('A', { status: 'completed', stepIndex: 4 }), { sessionId: 'S1' });   // completion broadcast lands first
  s = reduceRuns(s, run('A', { status: 'active', stepIndex: 1 }), { seed: true, sessionId: 'S1' });   // stale in-flight seed
  assert.equal(runById(s, 'A').status, 'completed', 'a stale seed never resurrects a completed run to active');
  assert.equal(liveRuns(s).length, 0, 'the ghost live run does not reappear');
}

// out-of-order seed responses: parallel getWorkflowRun replies arrive newest-first, but start order is stable
// because the reducer sorts by startedUtc.
{
  let s = emptyRuns('S1');
  s = reduceRuns(s, run('B', { startedUtc: '2026-07-15T00:00:02Z' }), { seed: true, sessionId: 'S1' });   // B's reply lands first
  s = reduceRuns(s, run('A', { startedUtc: '2026-07-15T00:00:01Z' }), { seed: true, sessionId: 'S1' });   // A's reply lands second
  assert.deepEqual(liveRuns(s).map((r) => r.runId), ['A', 'B'], 'runs settle into start order regardless of reply arrival order');
  assert.equal(mostRecentLive(s).runId, 'B', 'the most-recent live run is the latest-started, not the last-arrived');
}

// seed-after-live-event: a broadcast advances the run, THEN its (older) seed snapshot lands — the seed must not
// overwrite the advanced view.
{
  let s = emptyRuns('S1');
  s = reduceRuns(s, run('A', { stepIndex: 3 }), { sessionId: 'S1' });   // live broadcast advanced it
  s = reduceRuns(s, run('A', { stepIndex: 0 }), { seed: true, sessionId: 'S1' });   // seed snapshot is behind
  assert.equal(runById(s, 'A').stepIndex, 3, 'a seed never overwrites an entry a broadcast already advanced');
}

// out-of-order BROADCASTS: a reordered older broadcast is ignored (strictly-older rank), a same/newer one applies.
{
  let s = reduceRuns(emptyRuns('S1'), run('A', { stepIndex: 2 }), { sessionId: 'S1' });
  s = reduceRuns(s, run('A', { stepIndex: 1 }), { sessionId: 'S1' });   // stale, reordered delivery
  assert.equal(runById(s, 'A').stepIndex, 2, 'a strictly older broadcast does not rewind the run');
  s = reduceRuns(s, run('A', { stepIndex: 2, title: 'edited in-step' }), { sessionId: 'S1' });   // same step, fresh content
  assert.equal(runById(s, 'A').title, 'edited in-step', 'a same-rank broadcast still applies (latest content within a step wins)');
}

// model swap mid-seed: a fold stamped for a PRIOR session must not leak into the new session's map.
{
  let s = emptyRuns('S2');   // we have swapped to model S2
  s = reduceRuns(s, run('old', { status: 'active' }), { seed: true, sessionId: 'S1' });   // a straggler seed from S1
  assert.equal(s.runs.length, 0, 'a fold from a prior session is dropped — no cross-model leak');
  assert.equal(runById(s, 'old'), null, 'the stale run never appears in the new model map');
  // a fold for the current session still lands.
  s = reduceRuns(s, run('new', { status: 'active' }), { seed: true, sessionId: 'S2' });
  assert.equal(runById(s, 'new').runId, 'new', 'a current-session fold is accepted');
}

// ---- reconnect RECONCILIATION fold (HIGH): a same-session pipe reconnect misses unreplayed broadcasts -------
// The run-tracking effect is keyed on sessionId, so an ordinary reconnect (engine alive, SAME session) does not
// re-run it. A run shown live at step 1 could have completed while the pipe was down; without reconciliation the
// UI keeps a ghost live run forever. reduceRuns' reconcile fold folds the authoritative by-id refetch results.

// (1) a ghost still-active run whose by-id refetch is terminal is moved to terminal (the ghost banner clears).
{
  let s = reduceRuns(emptyRuns('S1'), run('A', { status: 'active', stepIndex: 1 }), { sessionId: 'S1' });
  assert.equal(liveRuns(s).length, 1, 'A is live before the pipe drops');
  s = reduceRuns(s, run('A', { status: 'completed', stepIndex: 4 }), { reconcile: true, sessionId: 'S1' });   // reconnect refetch
  assert.equal(runById(s, 'A').status, 'completed', 'reconciliation moves the ghost active run to terminal');
  assert.equal(liveRuns(s).length, 0, 'the ghost live run leaves the live set on reconnect');
  assert.equal(mostRecentTerminal(s).runId, 'A', 'the reconciled run is reachable as a terminal receipt');
}

// (2) reconciliation NEVER rewinds a run the live stream already advanced past the by-id refetch (rank-checked).
{
  let s = reduceRuns(emptyRuns('S1'), run('A', { status: 'active', stepIndex: 3 }), { sessionId: 'S1' });   // a fresh broadcast advanced it
  s = reduceRuns(s, run('A', { status: 'active', stepIndex: 1 }), { reconcile: true, sessionId: 'S1' });   // an older-view refetch
  assert.equal(runById(s, 'A').stepIndex, 3, 'a reconcile refetch behind the live view is ignored (never rewinds)');
}

// (3) a run the authoritative snapshot OMITS but whose by-id refetch is still active stays active (trust the newer
// by-id fetch — the reconnect path only evicts on NOT-FOUND, never on mere absence from orientation).
{
  let s = reduceRuns(emptyRuns('S1'), run('A', { status: 'active', stepIndex: 1 }), { sessionId: 'S1' });
  s = reduceRuns(s, run('A', { status: 'active', stepIndex: 2 }), { reconcile: true, sessionId: 'S1' });   // by-id still active, advanced
  assert.equal(runById(s, 'A').status, 'active', 'a run absent from orientation but active by-id stays active');
  assert.equal(runById(s, 'A').stepIndex, 2, 'the fresher by-id view is trusted');
}

// (4) NOT-FOUND eviction: the engine no longer holds the run, so a locally-ACTIVE ghost is dropped outright.
{
  let s = reduceRuns(reduceRuns(emptyRuns('S1'), run('keep', { status: 'active' }), { sessionId: 'S1' }),
    run('gone', { status: 'active' }), { sessionId: 'S1' });
  s = reduceRuns(s, run('gone'), { reconcile: true, notFound: true, sessionId: 'S1' });   // by-id refetch threw not-found
  assert.equal(runById(s, 'gone'), null, 'a not-found ghost is evicted from the map');
  assert.ok(runById(s, 'keep'), 'other tracked runs are untouched by the eviction');
  // a not-found for an unknown id is a no-op; a terminal receipt is a keep (a racing broadcast may have landed it).
  const before = reduceRuns(emptyRuns('S1'), run('done', { status: 'completed' }), { sessionId: 'S1' });
  const after = reduceRuns(before, run('done'), { reconcile: true, notFound: true, sessionId: 'S1' });
  assert.equal(runById(after, 'done').status, 'completed', 'a terminal receipt is never evicted by a not-found reconcile');
  assert.equal(reduceRuns(emptyRuns('S1'), run('never'), { reconcile: true, notFound: true, sessionId: 'S1' }).runs.length, 0, 'a not-found for an untracked id is a no-op');
}

// ---- HIGH: classify a by-id refetch outcome — only an AUTHORITATIVE not-found evicts; transport failures don't --
// The reconnect loop must distinguish "the engine no longer holds this run" (evict the ghost) from a mere pipe
// drop / timeout on the refetch. A run that COMPLETED while the pipe was down, then hit a transport error on its
// refetch, would otherwise be evicted and lose its terminal receipt + Evidence path forever. isRunNotFound is the
// classifier the reconcile loop gates eviction on — it must be TRUE only for the engine's real not-found shape.
{
  // (a) the engine's rejection shape (Workflow.cs WorkflowRunStore.Require) → not-found → evict.
  assert.equal(isRunNotFound(new Error("Workflow run 'wfr-3' not found.")), true, 'the engine rejection "Workflow run \'wfr-N\' not found." classifies as not-found');
  assert.equal(isRunNotFound("Workflow run 'wfr-9' not found."), true, 'the same message as a bare string classifies as not-found');

  // (b) TRANSPORT failures must NOT be read as absence — the entry is preserved for a later reconciliation pass.
  assert.equal(isRunNotFound(new Error('The pipe has been ended.')), false, 'a broken-pipe transport error is NOT a not-found — the run is preserved');
  assert.equal(isRunNotFound(new Error('RPC request timed out')), false, 'a timeout is NOT a not-found — the run is preserved');
  assert.equal(isRunNotFound(new Error('socket hang up')), false, 'a dropped socket is NOT a not-found — the run is preserved');
  assert.equal(isRunNotFound(new Error('Method getWorkflowRun not found')), false, 'a generic "method not found" transport error does NOT match the run-specific phrase, so it never evicts a live receipt');
  assert.equal(isRunNotFound(new Error("Failed to fetch workflow run: RPC method not found")), false, 'a PREFIXED transport error containing both "workflow run" and "not found" still does not match the anchored engine phrase');
  assert.equal(isRunNotFound(new Error("Workflow run 'wfr-3' not found. Retrying.")), false, 'a suffixed variant is not the exact engine throw and is preserved');

  // (c) a RESOLVED real run view is never a miss — it folds. A resolved not-found NOTE (defensive: today the
  // engine only rejects, but the evidence door proves the engine can resolve a note) DOES classify as not-found.
  assert.equal(isRunNotFound(run('A', { status: 'completed' })), false, 'a real run view (has status/steps) is never a not-found');
  assert.equal(isRunNotFound({ runId: 'A', note: "Workflow run 'wfr-A' not found." }), true, 'a resolved note-only view carrying the not-found phrase classifies as not-found (handled like the evidence door)');
  assert.equal(isRunNotFound({ runId: 'A', status: 'active', note: "Workflow run 'wfr-A' not found." }), false, 'a note alongside a real status is a live view, not a miss — never evicted');

  // (d) nullish / empty is never a miss.
  assert.equal(isRunNotFound(null), false, 'null is not a not-found');
  assert.equal(isRunNotFound(undefined), false, 'undefined is not a not-found');

  // (e) end-to-end: the reconcile loop's actual gating — an authoritative not-found evicts a locally-active ghost,
  // but a transport failure leaves it untouched (the completed run's receipt survives the outage).
  let s = reduceRuns(emptyRuns('S1'), run('ghost', { status: 'active' }), { sessionId: 'S1' });
  const notFoundErr = new Error("Workflow run 'ghost' not found.");
  if (isRunNotFound(notFoundErr)) s = reduceRuns(s, { runId: 'ghost' }, { reconcile: true, notFound: true, sessionId: 'S1' });
  assert.equal(runById(s, 'ghost'), null, 'an authoritative not-found evicts the ghost');

  let t = reduceRuns(emptyRuns('S1'), run('pending', { status: 'active' }), { sessionId: 'S1' });
  const transportErr = new Error('The pipe has been ended.');
  if (isRunNotFound(transportErr)) t = reduceRuns(t, { runId: 'pending' }, { reconcile: true, notFound: true, sessionId: 'S1' });
  assert.ok(runById(t, 'pending'), 'a transport failure preserves the entry — no eviction, receipt survives for a later reconciliation pass');
}

// generation guard still applies to reconcile folds: a refetch stamped for a prior session is dropped.
{
  let s = reduceRuns(emptyRuns('S2'), run('A', { status: 'active' }), { sessionId: 'S2' });
  s = reduceRuns(s, run('A', { status: 'completed' }), { reconcile: true, sessionId: 'S1' });   // straggler from the old session
  assert.equal(runById(s, 'A').status, 'active', 'a reconcile fold from a prior session cannot land in the new map');
}

// ---- source guard: App owns the always-alive subscription + seeds EVERY live run; run view is per-step -----
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (f) => readFileSync(resolve(root, f), 'utf8');
const app = read('webview/src/App.tsx');
const workflows = read('webview/src/workflows.tsx');
assert.match(app, /onWorkflowChange\(\(v\) => foldRun\(v as WorkflowRunView, \{ sessionId: sid \}\)\)/, 'App holds the always-alive run subscription and stamps each live broadcast with the current session (survives leaving the Workflows tab; generation-guarded)');
assert.match(app, /setRuns\(emptyRuns\(sid\)\); ownRunIds\.current = new Set\(\);\s*if \(!sid\) return;/, 'App resets the run map (stamped with the new session) + ownership on ANY sessionId change, then reseeds (HIGH 3 + finding 4: a model swap must not carry the prior run map)');
assert.match(app, /foldRun\(r, \{ seed: true, sessionId: sid \}\)/, 'App seeds are folded as SEEDS stamped with the session (HIGH 1: a seed never overwrites a live broadcast; finding 4: guarded against a mid-seed swap)');
assert.match(app, /getOrientation'\)[\s\S]*activeWork[\s\S]*getWorkflowRun', id\)/, 'App seeds EVERY live run from the orientation primer, not just the most recent (HIGH 4)');
assert.match(app, /rpc<WorkflowRunView>\('getWorkflowRun'\)\.then\(seed\)/, 'App keeps a single most-recent-run fallback (seeded) when orientation is unavailable');
assert.match(app, /s\.sessionId === previousSessionId\) reconcileRuns\(s\.sessionId\)/, 'App runs a run-map RECONCILIATION on a SAME-session reconnect (the sessionId-keyed effect will not re-run, so unreplayed broadcasts are reconciled against the engine)');
assert.match(app, /foldRun\(v, \{ reconcile: true, sessionId: sid \}\)/, 'the reconnect reconciliation folds each authoritative by-id refetch as a RANK-CHECKED reconcile fold (never rewinds a fresher live view)');
assert.match(app, /const evictGhost = \(id: string\) => \{ if \(!liveIds\.has\(id\)\) setRuns\(\(s\) => reduceRuns\(s, \{ runId: id \} as WorkflowRunView, \{ reconcile: true, notFound: true, sessionId: sid \}\)\); \};/, 'the ghost eviction fold uses notFound:true and fires only when the authoritative live set also omits the run');
assert.match(app, /rpc<WorkflowRunView>\('getWorkflowRun', id\)\.then\(\s*\(v\) => \{ if \(isRunNotFound\(v\)\) evictGhost\(id\); else foldRun/, 'a refetch that RESOLVES is classified: a not-found note evicts, a real run view folds');
assert.match(app, /\(e\) => \{ if \(isRunNotFound\(e\)\) evictGhost\(id\); \},/, 'a refetch that REJECTS evicts ONLY on an authoritative run-not-found — a transport failure (pipe drop/timeout) preserves the entry for a later pass, never loses a completed receipt');
assert.doesNotMatch(workflows, /started with enforcement off, so its gates are skipped/, 'the run view must not make a run-wide enforcement-off claim — the wire exposes only per-step strictness (HIGH 6)');
assert.match(workflows, /stepGateSkipped\(current\.effectiveStrictness\)/, 'the current step must render its OWN frozen gate-skip snapshot, not the model-wide flag');

console.log('workflow run-map reducer tests passed');
