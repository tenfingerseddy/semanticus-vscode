import { createContext, useContext, useEffect, useRef, useState } from 'react';
import { onActivity, type ActivityEvent } from './bridge';

export type { ActivityEvent };

// Which Studio tab renders each op's result — lets the feed deep-link and tabs reflect the agent's run.
export const KIND_TAB: Record<string, string> = {
  // The DAX Lab hub owns every query/perf op (author · run · debug · pivot · profile · plan · benchmark · verify).
  run_dax: 'daxlab', evaluate_and_log: 'daxlab', profile_dax: 'daxlab', benchmark_dax: 'daxlab',
  benchmark_coldwarm: 'daxlab', clear_cache: 'daxlab', capture_query_plan: 'daxlab',
  verify_equivalence: 'daxlab', preview_table: 'data', pivot_measure: 'daxlab', vpaq_scan: 'stats',
  get_doc_model: 'docs', set_doc_section: 'docs',
  get_spec: 'spec', set_spec: 'spec', clear_spec: 'spec', build_model_from_spec: 'spec',
  autogenerate_spec_from_model: 'spec', autogenerate_spec_from_fabric: 'spec', load_spec: 'spec',
  git_commit: 'deploy', git_push: 'deploy', git_pull: 'deploy', git_checkout: 'deploy',
  git_branch: 'deploy', git_clone: 'deploy', model_diff: 'compare', apply_model_diff: 'compare', cherry_pick: 'compare',
  preview_deploy: 'deploy', deploy_stage: 'deploy', deployment_history: 'deploy',
  fabric_git_status: 'deploy', fabric_git_connection: 'deploy', fabric_git_commit: 'deploy',
  fabric_git_update: 'deploy', fabric_git_connect: 'deploy', fabric_git_disconnect: 'deploy',
  cicd_publish: 'deploy', cicd_generate: 'deploy',
  health_delta: 'readiness',   // the post-commit health evidence record (feature #4) — review lands on AI Readiness
  review_reconcile_mapping: 'tests', reconcile_measure: 'tests',
};
// Display names for deep-link tooltips — the UI never speaks internal tab ids. `compare` is an alias
// into Deploy (App.tsx routes it there), so its destination reads as Deploy too.
const TAB_LABEL: Record<string, string> = {
  daxlab: 'DAX Lab', data: 'Data', stats: 'Storage', docs: 'Docs', spec: 'Model Spec',
  deploy: 'Deploy', compare: 'Deploy', readiness: 'AI Readiness', tests: 'Tests',
};
// A single neutral marker per feed entry — the op's text label carries the meaning (no colorful
// pictographs in any product surface).
const FEED_MARK = '•';

/** A feed entry the human explicitly CLICKED. Carries a nonce, not just the event: clicking the newest entry
 *  again (already reflected) must still re-fire, so identity can't be the seq. */
interface PinnedActivity { event: ActivityEvent; nonce: number; }
interface ActivityState {
  feed: ActivityEvent[];
  latestByKind: Record<string, ActivityEvent>;
  pinned: PinnedActivity | null;
  pin: (e: ActivityEvent) => void;
}
const Ctx = createContext<ActivityState>({ feed: [], latestByKind: {}, pinned: null, pin: () => {} });

// Subscribes once to the live `model/activity` stream and keeps a recent feed + the latest event per op kind,
// so the header indicator, the deep-link feed, and each tab's "Claude ran this" reflection share one source.
export function ActivityProvider({ children }: { children: React.ReactNode }) {
  const [feed, setFeed] = useState<ActivityEvent[]>([]);
  const [latestByKind, setLatest] = useState<Record<string, ActivityEvent>>({});
  // The pin lives HERE (not on the feed component) because the destination tab mounts AFTER the click navigates
  // to it — it must be able to read the clicked run when it first renders.
  const [pinned, setPinned] = useState<PinnedActivity | null>(null);
  const pinNonce = useRef(0);
  const pin = (e: ActivityEvent) => setPinned({ event: e, nonce: ++pinNonce.current });
  useEffect(() => onActivity((e) => {
    setFeed((f) => [e, ...f].slice(0, 60));
    setLatest((m) => (m[e.kind] && m[e.kind].seq >= e.seq ? m : { ...m, [e.kind]: e }));   // newest-per-kind, by server Seq
  }), []);
  return <Ctx.Provider value={{ feed, latestByKind, pinned, pin }}>{children}</Ctx.Provider>;
}
export function useActivity() { return useContext(Ctx); }

/** The latest agent run of `kind` (or undefined). A tab uses this to reflect what Claude just ran. */
export function useClaudeResult(kind: string): ActivityEvent | undefined {
  return useActivity().latestByKind[kind];
}

// "Last applied seq per kind", at MODULE scope so it survives a tab's unmount/remount (a tab's local result state
// is destroyed on tab switch). Without this, re-mounting a tab would replay the persisted last agent event over a
// result the human has since produced — silently clobbering it. Keyed by kind; each kind maps to one tab.
const appliedSeq = new Map<string, number>();
// Pins are tracked by NONCE, not seq: an explicit click on an OLDER entry sits below appliedSeq (so the seq guard
// would wrongly suppress it), and a click on the newest entry must re-fire even though it was already reflected.
// Module scope for the same remount reason as appliedSeq — a consumed pin must not replay on the next mount.
const appliedPin = new Map<string, number>();

/** Run `apply(event)` exactly once per genuinely-NEW agent run of `kind` — including across tab remounts, so an
 *  already-consumed event is never replayed. The tab still reflects Claude's latest run the first time it mounts.
 *  An explicitly PINNED run (the human clicked that entry in the live feed) wins over "newest of this kind": the
 *  tab replays exactly the run that was asked for. */
export function useClaudeReflection(kind: string, apply: (e: ActivityEvent) => void) {
  const { latestByKind, pinned } = useActivity();
  const e = latestByKind[kind];
  const p = pinned && pinned.event.kind === kind ? pinned : null;
  const applyRef = useRef(apply); applyRef.current = apply;
  useEffect(() => {
    // An unconsumed pin takes precedence — replay the exact run the human clicked, once. Once consumed we fall
    // through, so a genuinely-new agent run of this kind still reflects instead of being blocked by a stale pin.
    if (p && appliedPin.get(kind) !== p.nonce) {
      appliedPin.set(kind, p.nonce);
      // Keep appliedSeq MONOTONIC and mark the CURRENTLY-armed latest as applied too. A pin must suppress the
      // newest event that exists at pin time — not just its own seq: if the clicked entry is older than an
      // as-yet-unapplied latestByKind[kind], a tab remount would fall through (pin already consumed) and replay
      // that newer event, silently replacing the run the human clicked. Only activity strictly newer than
      // "latest at pin time" is real new agent work and SHOULD take over; recording e?.seq draws that line.
      appliedSeq.set(kind, Math.max(appliedSeq.get(kind) ?? -1, p.event.seq, e?.seq ?? -1));
      applyRef.current(p.event);
      return;
    }
    if (!e) return;
    if ((appliedSeq.get(kind) ?? -1) >= e.seq) return;   // already applied (this mount or a prior one) — don't replay
    appliedSeq.set(kind, e.seq);
    applyRef.current(e);
  }, [kind, e?.seq, p?.nonce]); // eslint-disable-line react-hooks/exhaustive-deps
}

// Header indicator + deep-link feed: a pulsing AI Assistant chip showing the latest op; click to drop a feed of
// recent runs, each of which opens its tab. A pending permission keeps the chip visible even before the first run.
export function LiveActivity({ onOpen, pendingApprovalCount = 0, firstPendingApprovalId }: { onOpen: (tab: string, approvalId?: string) => void; pendingApprovalCount?: number; firstPendingApprovalId?: string }) {
  const { feed, pin } = useActivity();
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false); };
    window.addEventListener('mousedown', close);
    return () => window.removeEventListener('mousedown', close);
  }, [open]);
  if (feed.length === 0 && pendingApprovalCount === 0) return null;
  const latest = feed[0];
  const approvalLabel = `${pendingApprovalCount} permission${pendingApprovalCount === 1 ? '' : 's'} waiting for approval. Open Permissions.`;
  return (
    <div ref={wrap} className="relative shrink-0">
      <button onClick={() => { if (feed.length) setOpen((o) => !o); else onOpen('permissions', firstPendingApprovalId); }} title={latest?.label ? `AI Assistant · ${latest.label}` : 'AI Assistant'}
        className="flex items-center gap-1.5 text-[11px] px-2 py-1 rounded-md"
        style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' }}>
        <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} />
        <span className="font-semibold">AI Assistant</span>
        {feed.length > 0 && <span className="shrink-0" style={{ color: 'var(--sem-muted)' }}>{FEED_MARK}</span>}
      </button>
      {pendingApprovalCount > 0 && (
        <button onClick={(e) => { e.stopPropagation(); setOpen(false); onOpen('permissions', firstPendingApprovalId); }}
          aria-label={approvalLabel} title={approvalLabel}
          className="absolute -right-2 -top-2 min-w-4 h-4 px-1 rounded-full text-[9px] leading-4 font-bold tnum"
          style={{ background: 'var(--sem-warn)', color: 'var(--sem-on-warn)', boxShadow: '0 0 0 2px var(--sem-bg)' }}>
          {pendingApprovalCount > 99 ? '99+' : pendingApprovalCount}
        </button>
      )}
      {open && (
        <div className="absolute right-0 top-full mt-1 z-50 w-96 rounded-lg shadow-xl overflow-hidden"
          style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
          <div className="px-3 py-2 text-[11px] uppercase tracking-wide font-semibold flex items-center gap-2"
            style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>
            <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} />
            Live activity · AI Assistant
          </div>
          <div className="max-h-96 overflow-auto">
            {feed.map((e) => {
              // A policy refusal carries its exact ledger id straight to Permissions. An op whose result renders
              // in a tab pins BEFORE navigating so the destination reflects THIS run, not merely the newest one
              // of the same kind. Everything else (metadata edits, workflow steps, ...) has no owning tab — those
              // entries are inert rather than dumping the click into an unrelated tab.
              const tab = KIND_TAB[e.kind];
              const body = (
                <>
                  <span className="shrink-0">{FEED_MARK}</span>
                  <span className="min-w-0 flex-1">
                    <span className="text-[12px] font-medium">{e.label}</span>
                    {e.query && <span className="text-[11px] font-mono block truncate" style={{ color: 'var(--sem-muted)' }}>{e.query}</span>}
                  </span>
                  {e.error ? <span className="text-[10px] shrink-0" style={{ color: 'var(--sem-bad)' }}>error</span>
                    : e.rowCount != null ? <span className="text-[10px] tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>{e.rowCount} rows</span>
                    : e.elapsedMs != null ? <span className="text-[10px] tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>{e.elapsedMs}ms</span> : null}
                </>
              );
              if (!e.approvalId && !tab) return (
                <div key={e.seq} className="w-full text-left px-3 py-1.5 flex items-center gap-2"
                  style={{ borderBottom: '1px solid var(--sem-border)' }}>
                  {body}
                </div>
              );
              return (
                <button key={e.seq} onClick={() => {
                  if (e.approvalId) onOpen('permissions', e.approvalId);
                  else { pin(e); onOpen(tab); }
                  setOpen(false);
                }}
                  className="w-full text-left px-3 py-1.5 flex items-center gap-2 hover:bg-[var(--sem-surface-2)]"
                  title={e.approvalId ? 'Open the exact permission request waiting for approval' : `Open this run in the ${TAB_LABEL[tab] ?? tab} tab`}
                  style={{ borderBottom: '1px solid var(--sem-border)' }}>
                  {body}
                </button>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}

// In-tab attribution shown above a result the agent produced — "▸ Claude · Profiled server timings — <query>".
export function ClaudeRanBanner({ event, onClear }: { event: ActivityEvent | null | undefined; onClear?: () => void }) {
  if (!event) return null;
  return (
    <div className="flex items-center gap-2 text-[11px] px-2.5 py-1.5 rounded-md"
      style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' }}>
      <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: 'var(--sem-accent)' }} />
      <span className="font-semibold shrink-0">AI Assistant</span>
      <span className="shrink-0">{event.label}</span>
      {event.query && <span className="font-mono truncate" style={{ color: 'var(--sem-muted)' }}>{event.query}</span>}
      {event.elapsedMs != null && <span className="tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>· {event.elapsedMs}ms</span>}
      {event.error && <span className="shrink-0" style={{ color: 'var(--sem-bad)' }}>· {event.error}</span>}
      {onClear && <button onClick={onClear} className="ml-auto shrink-0" style={{ color: 'var(--sem-muted)' }} title="Dismiss">✕</button>}
    </div>
  );
}
