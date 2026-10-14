// What The Link shows, from the real work (1.5, design page section 1). Owned by the link lane.
//   idle      nothing running
//   working   Claude or you started a job
//   thinking  the job has run past 8 seconds, or Jev is deciding
//   success   the job is done; the dots meet, then go home (1.9 s), then idle
//   error     a step failed; the dots stop at their ends and turn grey (no joke, no sound), then idle
// The signals are the ones the Studio already has. The engine says when an assistant tool call BEGINS (assistant/call) but not when it
// ends, so a job is a run of calls and it ends on a short quiet. A call that is known to take a while (a query, a scan, a publish) stays
// open until the engine reports its result (model/activity), so a 20 second query reads as thinking, not as done after 3 seconds.
// Clock and timer are injected so the tests drive time. The store keeps at most ONE timer, set for the next moment its state can
// change, and none at all while idle: nothing ticks when nothing is happening.
import { groupForSurface, groupForTool, type VerbGroup } from './verbs';

export type LinkState = 'idle' | 'working' | 'thinking' | 'success' | 'error';
export type LinkWork = {
  state: LinkState;
  /** The verb group of the work in hand; Jev while Jev decides. Null before any job. */
  group: VerbGroup | null;
  /** Who started it: the assistant over MCP, or the person in Studio. */
  who: 'assistant' | 'person' | null;
  /** Counts up once per job, so the status line can start a fresh verb cycle and clock. */
  job: number;
  startedAt: number | null;
  endedAt: number | null;
  /** "12 of 40" when the engine reports a count. */
  progress: { done: number; total: number } | null;
  /** The engine's own plain sentence for what the job changed, when it said one ("Added synonyms to 50 fields."). */
  words: string | null;
  /** The engine's sentence for what broke. */
  error: string | null;
  jev: boolean;
};

export const THINKING_AFTER_MS = 8_000;
export const QUIET_END_MS = 2_500;
export const SUCCESS_MS = 1_900;
export const ERROR_MS = 6_000;
/** A known-slow call that never reports back still ends: a lost result must not leave the Link spinning. */
export const OPEN_CALL_CAP_MS = 120_000;
/** A slow call whose result the engine does NOT publish (a scan, a publish) cannot be waited for: it stays open a few seconds, then the Link follows the calls
 *  that really arrive. Left at the long cap, a Link ran 1 min 42 s past the end of Claude's turn (hero20 D4). */
export const SILENT_CALL_CAP_MS = 15_000;
/** The slow calls the engine reports on when they end (model/activity, ActivityEvent.Kind). Only these may hold the Link open for the long cap. */
export const REPORTS_BACK = new Set([
  'run_dax', 'profile_dax', 'benchmark_dax', 'benchmark_cold_warm', 'evaluate_and_log', 'vpaq_scan', 'verify_dax_equivalence', 'preview_table',
  'ask_data', 'explain_value', 'run_tests',
]);
/** Jev's look is capped the same way: a plan page that closed before Jev landed must not leave the Link thinking forever. */
export const JEV_CAP_MS = 120_000;

/** Calls whose result the engine publishes as model/activity (non-mutating runs) or that run long enough to matter. */
export const SLOW_TOOLS = new Set([
  'run_dax', 'profile_dax', 'benchmark_dax', 'benchmark_cold_warm', 'evaluate_and_log', 'vpaq_scan', 'run_tests', 'run_interview',
  'ai_readiness_scan', 'bpa_scan', 'deploy_live', 'deploy_stage', 'publish_new_model', 'cicd_publish', 'refresh_partition',
  'analyze_reports', 'analyze_cloud_reports', 'verify_dax_equivalence', 'make_model_ai_ready', 'build_model_from_spec',
  'start_model_from_sql', 'optimize_measure', 'capture_query_plan', 'explain_value', 'ask_data', 'preview_table', 'reconcile_measure',
]);

export type ActivitySignal = { origin?: string; ok: boolean; error?: string; kind?: string; words?: { sentence?: string } | null };

export function createLinkWork(now: () => number = () => Date.now(),
  schedule: (fn: () => void, ms: number) => () => void = (fn, ms) => {
    const t = setTimeout(fn, ms);
    (t as unknown as { unref?: () => void }).unref?.();   // Node only (the tests): a pending Link timer never holds a process open
    return () => clearTimeout(t);
  }) {
  let snap: LinkWork = { state: 'idle', group: null, who: null, job: 0, startedAt: null, endedAt: null, progress: null, words: null, error: null, jev: false };
  const listeners = new Set<() => void>();
  let jobGroup: VerbGroup | null = null;
  let lastSignal = -Infinity;
  let errorAt = -Infinity;
  let jevSince = -Infinity;
  const open: { at: number; cap: number }[] = [];   // known-slow calls still waiting for their result, with how long each may wait
  const busy = new Set<string>();          // Studio surfaces the person is running
  const holds = new Set<string>();         // reads the Studio started itself and is waiting on (the reading card)
  let holdSignal = -Infinity;              // lastSignal as the newest hold set it: still equal means no one else has signalled since
  let cancel: (() => void) | null = null;

  const emit = (next: LinkWork) => {
    const same = (Object.keys(next) as (keyof LinkWork)[]).every((k) => next[k] === snap[k]);
    if (same) return;
    snap = next;
    for (const fn of [...listeners]) { try { fn(); } catch { /* one view never stops another */ } }
  };
  const running = () => snap.state === 'working' || snap.state === 'thinking';

  const start = (who: 'assistant' | 'person', group: VerbGroup | null, t: number) => {
    jobGroup = group;
    emit({ ...snap, state: 'working', who, group: snap.jev ? 'Jev' : group, job: snap.job + 1, startedAt: t, endedAt: null,
      progress: null, words: null, error: null });
  };

  function evaluate() {
    if (cancel) { cancel(); cancel = null; }
    const t = now();
    for (let i = open.length - 1; i >= 0; i--) if (t - open[i].at >= open[i].cap) open.splice(i, 1);
    if (snap.jev && t - jevSince >= JEV_CAP_MS) emit({ ...snap, jev: false, group: jobGroup ?? 'Jev' });
    let wake = Infinity;
    if (running()) {
      const active = open.length > 0 || busy.size > 0 || holds.size > 0 || snap.jev || t - lastSignal < QUIET_END_MS;
      if (!active) {
        emit({ ...snap, state: 'success', group: jobGroup ?? snap.group, endedAt: t });
        wake = SUCCESS_MS;
      } else {
        const long = snap.jev || (snap.startedAt !== null && t - snap.startedAt >= THINKING_AFTER_MS);
        if (long !== (snap.state === 'thinking')) emit({ ...snap, state: long ? 'thinking' : 'working' });
        if (!long && snap.startedAt !== null) wake = Math.min(wake, snap.startedAt + THINKING_AFTER_MS - t);
        if (open.length === 0 && busy.size === 0 && holds.size === 0 && !snap.jev) wake = Math.min(wake, lastSignal + QUIET_END_MS - t);
        for (const o of open) wake = Math.min(wake, o.at + o.cap - t);
        if (snap.jev) wake = Math.min(wake, jevSince + JEV_CAP_MS - t);
      }
    } else if (snap.state === 'success') {
      const left = (snap.endedAt ?? t) + SUCCESS_MS - t;
      if (left <= 0) emit({ ...snap, state: 'idle' }); else wake = left;
    } else if (snap.state === 'error') {
      const left = errorAt + ERROR_MS - t;
      if (left <= 0) emit({ ...snap, state: 'idle', error: null }); else wake = left;
    }
    if (wake !== Infinity) cancel = schedule(evaluate, Math.max(16, wake));
  }

  return {
    getSnapshot: () => snap,
    subscribe(fn: () => void) { listeners.add(fn); return () => { listeners.delete(fn); }; },
    /** assistant/call: one assistant tool call began. */
    call(tool: string) {
      const t = now();
      const g = groupForTool(tool);
      lastSignal = t;
      if (SLOW_TOOLS.has(tool)) open.push({ at: t, cap: REPORTS_BACK.has(tool) ? OPEN_CALL_CAP_MS : SILENT_CALL_CAP_MS });
      if (!running()) start('assistant', g ?? jobGroup ?? null, t);
      else if (g && g !== jobGroup) { jobGroup = g; if (!snap.jev) emit({ ...snap, group: g }); }
      if (!snap.group && !snap.jev) { jobGroup = 'Readiness'; emit({ ...snap, group: 'Readiness' }); }
      evaluate();
    },
    /** model/activity: a run finished. A failure turns the Link grey; a success closes the oldest slow call. */
    activity(e: ActivitySignal) {
      const t = now();
      if (!e.ok) {
        open.length = 0;
        errorAt = t;
        emit({ ...snap, state: 'error', endedAt: t, error: e.error?.trim() || null });
        evaluate();
        return;
      }
      if (e.kind === 'health_delta') return;   // evidence that rides along with an edit, not a result
      if (e.kind === 'publish_target') return;   // says where a publish is going, not that a call ended
      const words = e.words?.sentence?.trim();
      if (!running() && !words) return;
      lastSignal = t;
      open.shift();
      if (words) {
        if (running()) emit({ ...snap, words });
        else if (snap.state === 'success') emit({ ...snap, words });
      }
      evaluate();
    },
    /** operation/progress: the engine counts through a long job. */
    progress(p: { opKey?: string; done: number; total: number }) {
      const t = now();
      lastSignal = t;
      if (!running()) start('person', p.opKey ? groupForSurface(p.opKey) : null, t);
      if (Number.isFinite(p.done) && Number.isFinite(p.total) && p.total > 0) emit({ ...snap, progress: { done: p.done, total: p.total } });
      evaluate();
    },
    /** The Studio surfaces the person has running (Lineage, DAX Lab, Storage). */
    setBusySurfaces(surfaces: readonly string[]) {
      const before = busy.size;
      const t = now();
      const added = surfaces.filter((s) => !busy.has(s));
      busy.clear(); for (const s of surfaces) busy.add(s);
      if (added.length && !running()) start('person', groupForSurface(added[0]), t);
      if (busy.size < before) lastSignal = Math.max(lastSignal, t - QUIET_END_MS + 300);   // done: show success shortly, not after a full quiet
      evaluate();
    },
    /** A read the Studio started on its own and is waiting for, by key (the reading card's first read of a model). The Link works while any hold is open
     *  and shows success shortly after the last one ends: that is the moment the dots meet and the grade has landed. */
    hold(key: string, on: boolean) {
      const t = now();
      if (on) {
        if (holds.has(key)) return;
        holds.add(key);
        if (!running()) start('person', 'Readiness', t);
        lastSignal = t; holdSignal = t;
      } else {
        if (!holds.delete(key)) return;
        // Done: the grade landed, so the dots meet now. Nothing else signalled since the hold began, so there is no quiet to wait out.
        if (holds.size === 0 && lastSignal === holdSignal) lastSignal = t - QUIET_END_MS;
      }
      evaluate();
    },
    /** The plan says Jev is still looking at this model (ModelPlanView.JevPending). */
    setJevPending(pending: boolean) {
      if (pending === snap.jev) return;
      const t = now();
      if (pending) {
        jevSince = t;
        if (!running()) { start('assistant', 'Jev', t); }
        emit({ ...snap, jev: true, group: 'Jev', state: 'thinking' });
      } else {
        lastSignal = Math.max(lastSignal, t - QUIET_END_MS + 300);
        emit({ ...snap, jev: false, group: jobGroup ?? 'Jev' });
      }
      evaluate();
    },
    /** Tests: run the evaluation as if its timer fired now. */
    evaluate,
  };
}

export const linkWork = createLinkWork();
