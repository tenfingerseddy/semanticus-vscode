import { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import { onActivity, onDidChange, runHostCommand, type ActivityEvent } from './bridge';
import type { SaidGroup } from './studio/said-model';
import { appendEdit, saidCopy } from './studio/said-model';
import { ConnectAssistantsPanel } from './connect-assistants';
import { PANEL_TITLE } from './connect-assistants-model';

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
  addEdit: (group: SaidGroup) => void;
  feed: ActivityEvent[];
  latestByKind: Record<string, ActivityEvent>;
  pinned: PinnedActivity | null;
  pin: (e: ActivityEvent) => void;
}
const Ctx = createContext<ActivityState>({ feed: [], latestByKind: {}, pinned: null, pin: () => {}, addEdit: () => {} });

// Subscribes once to the live `model/activity` stream and keeps a recent feed + the latest event per op kind,
// so the header indicator, the deep-link feed, and each tab's "Claude ran this" reflection share one source.
export function ActivityProvider({ children }: { children: React.ReactNode }) {
  const [feed, setFeed] = useState<ActivityEvent[]>([]);
  const [latestByKind, setLatest] = useState<Record<string, ActivityEvent>>({});
  // The pin lives HERE (not on the feed component) because the destination tab mounts AFTER the click navigates
  // to it — it must be able to read the clicked run when it first renders.
  const [pinned, setPinned] = useState<PinnedActivity | null>(null);
  const pinNonce = useRef(0);
  const editSeq = useRef(0);
  const editEpoch = useRef(0);
  const editBoundaries = useRef<{ sessionId: string; revision: number; epoch: number }[]>([]);
  useEffect(() => onDidChange(n => {
    if (n.replay || ((n.by ?? (n.origin === 'agent' ? 'assistant' : n.origin)) !== 'assistant'
      && n.cause?.approvedBy !== 'assistant')) {
      editBoundaries.current.push({ sessionId: n.sessionId, revision: n.revision, epoch: ++editEpoch.current });
      editBoundaries.current = editBoundaries.current.slice(-64);
    }
  }), []);
  const addEdit = useCallback((group: SaidGroup) => {
    const epoch = [...editBoundaries.current].reverse().find(b => b.sessionId === group.sessionId && b.revision <= group.lastRevision)?.epoch ?? 0;
    const event: ActivityEvent = { seq: --editSeq.current, kind: 'edit', origin: 'agent', label: saidCopy(group), target: group.id, ok: true, words: group.words, editEpoch: `${group.sessionId}:${epoch}` };
    setFeed((f) => appendEdit(f, event));
  }, []);
  const pin = (e: ActivityEvent) => setPinned({ event: e, nonce: ++pinNonce.current });
  useEffect(() => onActivity((e) => {
    setFeed((f) => [e, ...f].slice(0, 60));
    setLatest((m) => (m[e.kind] && m[e.kind].seq >= e.seq ? m : { ...m, [e.kind]: e }));   // newest-per-kind, by server Seq
  }), []);
  return <Ctx.Provider value={{ feed, latestByKind, pinned, pin, addEdit }}>{children}</Ctx.Provider>;
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

/** One row of the chip's menu. `note` is the right-hand text: a count, a state, nothing. */
interface ChipItem { id: string; label: string; note?: string; title?: string; run: () => void; }

// Header chip + its menu: the assistant's own corner of the header. Kane retired the gear that sat beside
// Connections (2026-09-14) because it named nothing and doubled up with the tabs, so assistant permissions
// live here now, beside the live feed and the one command that wires the assistant up in the first place.
// The chip no longer hides itself before the first run: it owns Permissions and Connect Claude Code, and a
// person who has not connected yet is exactly the person who needs both.
export function LiveActivity({ onOpen, pendingApprovalCount = 0, firstPendingApprovalId, planCount = null, onPlanCount }: {
  onOpen: (tab: string, approvalId?: string) => void; pendingApprovalCount?: number; firstPendingApprovalId?: string;
  /** The assistant track's Ready plus On it on the Plan (contract 11.4). Null draws no count. */
  planCount?: number | null; onPlanCount?: () => void;
}) {
  const { feed, pin } = useActivity();
  const [open, setOpen] = useState(false);
  // The menu is the first thing the chip opens; the live feed is one pick in, where it used to be the whole popover.
  const [view, setView] = useState<'menu' | 'feed' | 'connect'>('menu');
  const [highlight, setHighlight] = useState(0);
  const wrap = useRef<HTMLDivElement>(null);
  const chipRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const feedRef = useRef<HTMLDivElement>(null);
  const itemRefs = useRef<(HTMLButtonElement | null)[]>([]);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false); };
    window.addEventListener('mousedown', close);
    return () => window.removeEventListener('mousedown', close);
  }, [open]);
  useEffect(() => { if (open) { setView('menu'); setHighlight(0); } }, [open]);
  // Focus follows whichever surface is mounted. The feed REPLACES the menu, so the menu node is already gone by
  // the time this runs after a pick: focusing it there put the ring on an unmounting node and it fell to the
  // document, which left the feed open with no keyboard way out (Astra finding 4).
  useEffect(() => {
    if (!open) return;
    if (view === 'feed') { feedRef.current?.focus(); return; }
    if (view === 'connect') return;   // the panel puts the ring on its first button once the list has loaded
    if (highlight >= 0) itemRefs.current[highlight]?.focus(); else menuRef.current?.focus();
  }, [open, view, highlight]);
  const closeAndReturnFocus = () => { setOpen(false); chipRef.current?.focus(); };

  const latest = feed[0];
  const approvalLabel = `${pendingApprovalCount} permission${pendingApprovalCount === 1 ? '' : 's'} waiting for approval. Open Permissions.`;
  // What the webview can actually PROVE about the assistant. Activity events and approval requests both arrive
  // only through the MCP door, so either one is proof it is wired to this session. Nothing in the webview can see
  // the folder's .mcp.json, so the un-proven side says so in the tooltip rather than claiming a checked negative.
  const assistantSeen = feed.length > 0 || pendingApprovalCount > 0;
  const connectTitle = assistantSeen
    ? 'Your assistant has used this session. Running this again rewrites the connection file in this folder.'
    : 'Semanticus has not seen your assistant use this session. If you already wired it up, it may simply not have run anything yet.';

  const items: ChipItem[] = [];
  if (pendingApprovalCount > 0) items.push({
    id: 'approvals', label: `${pendingApprovalCount} approval${pendingApprovalCount === 1 ? '' : 's'} waiting`,
    title: approvalLabel, run: () => onOpen('permissions', firstPendingApprovalId),
  });
  items.push({ id: 'feed', label: 'Live activity', note: feed.length ? String(feed.length) : undefined,
    title: 'What your assistant has run on this model', run: () => setView('feed') });
  items.push({ id: 'permissions', label: 'Permissions', title: 'What your assistant is allowed to do, and what it must ask for first',
    run: () => onOpen('permissions') });
  // The palette command is called Connect AI Assistant and the id still says claudeCode, but product copy never
  // names a vendor's model: it says "your assistant" (docs/product-copy-style.md, enforced by copy-rules).
  // Once the assistant has been seen, the wiring is done and the row would only say "connected": it leaves the menu.
  // The palette command (Connect Your Assistant) still rewrites the connection file for anyone who needs it again.
  if (!assistantSeen) items.push({ id: 'connect', label: 'Connect your assistant', title: connectTitle,
    run: () => runHostCommand('semanticus.connectClaudeCode') });
  // The same press for the person's other assistants (Claude Desktop, Codex, Cursor, Claude Code in a terminal). It stays in the menu
  // after the first assistant is seen, because a second assistant is exactly when a person comes looking for it.
  items.push({ id: 'connectOther', label: PANEL_TITLE, title: 'Add Semanticus to another assistant on this computer',
    run: () => setView('connect') });

  const pick = (item: ChipItem, fromKeyboard: boolean) => {
    item.run();
    if (item.id === 'feed' || item.id === 'connectOther') return;   // stays open one level in; the focus effect moves the ring into it
    setOpen(false);
    if (fromKeyboard) chipRef.current?.focus(); else chipRef.current?.blur();
  };

  // The feed is a LIST, so Tab keeps walking its entries (the header lane's decision, kept). What it may not do
  // is outlive the keyboard: Astra, 2026-09-14, tabbed past the last entry and Escape then did nothing while the
  // chip still read aria-expanded="true". Whichever surface is open closes the moment focus lands outside this
  // chip and its popovers. Focus that went nowhere while the WINDOW lost focus is not a person leaving.
  const closeOnFocusLeaving = (e: React.FocusEvent) => {
    if (!open) return;
    const next = e.relatedTarget as Node | null;
    if (wrap.current?.contains(next)) return;
    if (!next && !document.hasFocus()) return;
    setOpen(false);
  };
  return (
    <div ref={wrap} className="relative flex shrink-0 items-center gap-1" onBlur={closeOnFocusLeaving}>
      <button ref={chipRef} type="button" data-tour="assistant-chip" aria-haspopup="menu" aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setOpen(true); }
          else if (e.key === 'Escape' && open) { e.preventDefault(); setOpen(false); }
        }}
        title={latest?.label ? `Your assistant · ${latest.label}` : 'Your assistant'}
        // Instrument: a hairline button like its neighbours in the header, not a tinted chip. On the tint, the
        // muted feed mark measured 4.1:1 in Light Modern.
        className="studio-header-action studio-feed-mark-host gap-2">
        {/* The dot only pulses while there is something live to pulse about; idle it is a plain muted mark. */}
        <span className={`w-1 h-1 rounded-full${feed.length > 0 ? ' animate-pulse' : ''}`}
          style={{ background: feed.length > 0 ? 'var(--sem-actor-assistant)' : 'var(--sem-muted)' }} />
        <span>Your assistant</span>
        {/* A badge on the chip's corner, drawn by CSS (styles.css .studio-feed-mark) and positioned absolutely: it takes
            no layout width idle or live, so the assistant waking never refolds the header, and the idle header folds
            no earlier than it would with no mark at all (m11 header-stable). The chip's text stays "Your assistant". */}
        <span className="studio-feed-mark" aria-hidden="true" data-live={feed.length > 0 ? '' : undefined} />
      </button>
      {/* The one count the chip carries for the Plan: its tasks ready for your assistant or being done by it. Pressing it
          opens the Plan filtered to that track (Plan at the heart, contract 11.4). Zero draws nothing. */}
      {planCount != null && planCount > 0 && onPlanCount && (
        <button type="button" data-testid="assistant-plan-count" onClick={(e) => { e.stopPropagation(); setOpen(false); onPlanCount(); }}
          aria-label={`${planCount} task${planCount === 1 ? '' : 's'} for your assistant on the Plan. Open them.`}
          title={`${planCount} task${planCount === 1 ? '' : 's'} for your assistant on the Plan`}
          className="flex h-6 items-center px-1 text-xs sem-num" style={{ color: 'var(--sem-fg)' }}>
          {planCount > 99 ? '99+' : planCount}
        </button>
      )}
      {pendingApprovalCount > 0 && (
        <button onClick={(e) => { e.stopPropagation(); setOpen(false); onOpen('permissions', firstPendingApprovalId); }}
          aria-label={approvalLabel} title={approvalLabel}
          // Instrument: the count waiting for you is a number beside the button with a dot in your blue, not
          // a badge pinned over its corner. It is still its own button, and still goes to the approvals.
          className="flex h-6 items-center gap-1 px-1 text-xs sem-num" style={{ color: 'var(--sem-fg)' }}>
          <span className="w-1 h-1 rounded-full" aria-hidden style={{ background: 'var(--sem-actor-you)' }} />
          {pendingApprovalCount > 99 ? '99+' : pendingApprovalCount}
        </button>
      )}
      {open && view === 'menu' && (
        <div ref={menuRef} tabIndex={-1} role="menu" aria-label="Your assistant" className="studio-chip-menu studio-header-pop absolute right-0 top-full mt-1"
          onKeyDown={(e) => {
            if (e.key === 'Escape') { e.preventDefault(); closeAndReturnFocus(); }
            else if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight((h) => (h + 1 + items.length) % items.length); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight((h) => (h <= 0 ? items.length : h) - 1); }
            else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); const it = items[highlight]; if (it) pick(it, true); }
            else if (e.key === 'Tab') { e.preventDefault(); closeAndReturnFocus(); }
          }}>
          {items.map((item, i) => <button key={item.id} ref={(el) => { itemRefs.current[i] = el; }} role="menuitem" type="button"
            title={item.title} className={i === highlight ? 'is-highlighted' : undefined}
            onMouseEnter={() => setHighlight(i)} onClick={() => pick(item, false)}>
            <span>{item.label}</span>
            {item.note && <span className="studio-chip-menu-note">{item.note}</span>}
          </button>)}
        </div>
      )}
      {open && view === 'connect' && (
        <div className="studio-header-pop absolute right-0 top-full mt-1 rounded-(--sem-radius-card) shadow-xl overflow-hidden outline-none"
          style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}
          onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setView('menu'); } }}>
          <ConnectAssistantsPanel onBack={() => setView('menu')} />
        </div>
      )}
      {open && view === 'feed' && (
        <div ref={feedRef} tabIndex={-1} className="studio-header-pop absolute right-0 top-full mt-1 w-96 rounded-(--sem-radius-card) shadow-xl overflow-hidden outline-none"
          onKeyDown={(e) => {
            if (e.key === 'Escape') { e.preventDefault(); closeAndReturnFocus(); }
            // Left/Backspace walk back to the menu the same way the Back button does, so the feed is not a one-way
            // door for the keyboard. Tab stays native: the feed is a LIST, and trapping it would make sixty
            // entries unreachable.
            else if (e.key === 'ArrowLeft' || e.key === 'Backspace') { e.preventDefault(); setView('menu'); }
          }}
          style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
          <div className="px-3 py-2 text-xs font-semibold flex items-center gap-2"
            style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>
            <span className={`w-1 h-1 rounded-full${feed.length > 0 ? ' animate-pulse' : ''}`}
              style={{ background: feed.length > 0 ? 'var(--sem-actor-assistant)' : 'var(--sem-muted)' }} />
            Live activity · your assistant
            <button type="button" onClick={() => setView('menu')} className="sem-btn sem-btn-sm sem-btn-quiet ml-auto"
              title="Back to the assistant menu">Back</button>
          </div>
          {feed.length === 0 && <div className="px-3 py-3 text-xs" style={{ color: 'var(--sem-muted)' }}>
            Nothing yet. Anything your assistant runs on this model shows up here.
          </div>}
          <div className="max-h-96 overflow-auto">
            {feed.map((e) => {
              // A policy refusal carries its exact ledger id straight to Permissions. An op whose result renders
              // in a tab pins BEFORE navigating so the destination reflects THIS run, not merely the newest one
              // of the same kind. Everything else (metadata edits, workflow steps, ...) has no owning tab — those
              // entries are inert rather than dumping the click into an unrelated tab.
              const tab = KIND_TAB[e.kind];
              const body = (
                <>
                  {/* Decoration: the entry is read by its words, never as "bullet Ran DAX query" (verifier round 2). */}
                  <span className="shrink-0" aria-hidden="true">{FEED_MARK}</span>
                  <span className="min-w-0 flex-1">
                    <span className="text-xs font-medium">{e.label}</span>
                    {e.query && <span className="text-xs font-mono block truncate" style={{ color: 'var(--sem-muted)' }}>{e.query}</span>}
                  </span>
                  {e.error ? <span className="text-(length:--sem-text-label) shrink-0" style={{ color: 'var(--sem-bad)' }}>error</span>
                    : e.rowCount != null ? <span className="text-(length:--sem-text-label) tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>{e.rowCount} rows</span>
                    : e.elapsedMs != null ? <span className="text-(length:--sem-text-label) tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>{e.elapsedMs}ms</span> : null}
                </>
              );
              if (!e.approvalId && !tab) return (
                <div key={e.seq} className="w-full text-left px-3 py-2 flex items-center gap-2"
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
                  className="w-full text-left px-3 py-2 flex items-center gap-2 hover:bg-[var(--sem-hover)]"
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
// queryShownAbove: the page already shows this query in full, so the strip does not repeat it.
export function ClaudeRanBanner({ event, onClear, queryShownAbove }: { event: ActivityEvent | null | undefined; onClear?: () => void; queryShownAbove?: boolean }) {
  if (!event) return null;
  return (
    <div className="sem-inset flex items-center gap-2 text-xs" style={{ color: 'var(--sem-fg)' }}>
      <span className="w-1 h-1 rounded-full shrink-0" style={{ background: 'var(--sem-fg)' }} />
      <span className="font-semibold shrink-0">Your assistant</span>
      <span className="shrink-0">{queryShownAbove ? 'ran this query' : event.label}</span>
      {event.query && !queryShownAbove && <span className="font-mono truncate" style={{ color: 'var(--sem-muted)' }}>{event.query}</span>}
      {event.elapsedMs != null && <span className="tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>· {event.elapsedMs}ms</span>}
      {event.error && <span className="shrink-0" style={{ color: 'var(--sem-bad)' }}>· {event.error}</span>}
      {onClear && <button onClick={onClear} className="ml-auto shrink-0" style={{ color: 'var(--sem-muted)' }} title="Dismiss">✕</button>}
    </div>
  );
}
