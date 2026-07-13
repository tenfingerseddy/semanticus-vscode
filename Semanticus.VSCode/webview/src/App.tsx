import { useCallback, useEffect, useMemo, useRef, useState, lazy, Suspense } from 'react';
import { rpc, onDidChange, onReconnect, onActivity, onNavigate, signalReady, revealInTree, focusModelTree, type ChangeNotification } from './bridge';
import { ShortcutsOverlay, tabForKey, isTypingTarget } from './shortcuts';
import { ActivityProvider, LiveActivity, ClaudeRanBanner, useClaudeReflection, KIND_TAB, type ActivityEvent } from './activity';
import { useFixState } from './hooks';
import { useConnection, ConnectBar, type SessionInfo } from './connection';
import { ContextBar, compareSeedFromSession, QueryStalenessChip } from './contextbar';
import { ConnectionsDrawer } from './connectionsdrawer';
import type { ModelRef } from './compare';
import { VpaqTreemap, Sparkline, type VpaqColumn, type VpaqTable } from './echart';
import { DiagramView } from './diagram';
import type { ModelGraph } from './diagram';
import { AdvancedModelsView } from './advmodels';
import { GroupedFindings, WaiveControl, WaivedList, type FindingRow } from './findings';
import { useTier, isEntitlementError, LicenseButton, ProBadge, UpsellNotice } from './pro';
import { DaxModelProvider } from './daxeditor';
import { DaxLabView } from './daxlab';
import { LineageView } from './lineage';
import { SearchView } from './search';
import { DataPreviewView } from './datapreview';
import { BpaView } from './bpa';
import { OptimizeView } from './optimize';
import { SpecView } from './spec';
import { DocumentationView } from './documentation';
import { DeployView } from './deploy';
import { EvidenceView, TestsView } from './tests';
import { DataAgentView } from './dataagent';
import { HelpButton } from './help';
import { HistoryView, type EditEntry } from './history';
import { WorkflowsView } from './workflows';
import { KnowledgeView } from './knowledge';
import { PermissionsView } from './permissions';
import { InterviewSummaryChip } from './interview';
import { CustomRulesPanel } from './rulesauthor';

// M Code is the heaviest tab (the @microsoft/powerquery-* parser/formatter/language-services + the 866-symbol
// M standard-library dataset — ~1.7 MB). Lazy-load it so that whole cluster lands in its own chunk and only
// downloads when the user opens the tab, keeping the rest of Studio's startup lean.
const MCodeView = lazy(() => import('./mcode').then((m) => ({ default: m.MCodeView })));

// ---- wire types (camelCase, from the engine) -------------------------------------------------
interface CategoryScore { category: string; score: number; weight: number; applicable: number; violations: number; waived?: number; hasRules: boolean; }
interface Finding { ruleId: string; ruleTitle: string; category: string; severity: string; fix: string; objectRef: string; objectName: string; message: string; displayMessage?: string; custom?: boolean; waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean; }
interface Scorecard {
  overall: number; rawOverall: number; grade: string; gatedBy: string[];
  categories: CategoryScore[]; coverage: Record<string, number>; findings: Finding[]; safeFixCount: number; waivedCount?: number;
  caveat?: string;   // set when the score may be incomplete (e.g. Direct Lake live cardinality is resident-only)
  ruleErrors?: string[];   // custom-rule problems surfaced loudly (unparseable annotation, an eval error)
}
interface ApprovalNotice { id: string; grantedUtc?: string | null; }
// SessionInfo now lives in connection.tsx (the single shared shape, mirroring Protocol.cs) — imported above.

const GRADE_COLOR: Record<string, string> = { A: 'var(--sem-good)', B: 'var(--sem-good)', C: 'var(--sem-warn)', D: 'var(--sem-warn)', F: 'var(--sem-bad)' };
const FIX_STYLE: Record<string, { label: string; color: string }> = {
  SafeFix: { label: 'Safe fix', color: 'var(--sem-good)' },
  AiContent: { label: 'AI', color: 'var(--sem-accent)' },
  Proposal: { label: 'Review', color: 'var(--sem-muted)' },
  None: { label: 'Info', color: 'var(--sem-muted)' },
};

type StudioTab = 'readiness' | 'optimize' | 'bpa' | 'diagram' | 'advmodels' | 'mcode' | 'stats' | 'data' | 'daxlab' | 'lineage' | 'compare' | 'docs' | 'spec' | 'deploy' | 'tests' | 'evidence' | 'dataagent' | 'permissions' | 'history' | 'workflows' | 'knowledge' | 'search';

// The primary row names the five analyst intents; the secondary row keeps direct tools reachable without exposing
// product-internal machinery as navigation. IDs remain stable for host commands and persisted state. Compare and Data
// Agent stay valid compatibility routes, but are no longer primary destinations: Compare opens as Deploy's review and
// Data Agent lives under Deploy's Advanced tools. Model Spec remains primary because it uniquely owns model creation.
const TAB_GROUPS: { id: string; label: string; tabs: { id: StudioTab; label: string }[] }[] = [
  { id: 'understand', label: 'Understand', tabs: [
    { id: 'diagram', label: 'Diagram' }, { id: 'search', label: 'Search' }, { id: 'lineage', label: 'Lineage' },
    { id: 'data', label: 'Data' }, { id: 'stats', label: 'Statistics' },
  ] },
  { id: 'change', label: 'Change', tabs: [
    { id: 'spec', label: 'Model Spec' }, { id: 'advmodels', label: 'Advanced Modelling' }, { id: 'mcode', label: 'M Code' },
    { id: 'daxlab', label: 'DAX Lab' }, { id: 'optimize', label: 'Change Plan' },
  ] },
  { id: 'improve', label: 'Improve', tabs: [
    { id: 'readiness', label: 'AI Readiness' }, { id: 'bpa', label: 'BPA' },
  ] },
  { id: 'prove', label: 'Prove', tabs: [
    { id: 'tests', label: 'Tests' }, { id: 'evidence', label: 'Evidence' },
  ] },
  { id: 'ship', label: 'Ship', tabs: [
    // Docs sits in Ship by Kane's ruling (2026-07-12): generated documentation is a deliverable
    // you ship, not reference reading.
    { id: 'deploy', label: 'Deploy' }, { id: 'permissions', label: 'Permissions' }, { id: 'docs', label: 'Docs' },
  ] },
];
const TAB_TO_GROUP: Record<string, string> = Object.fromEntries(TAB_GROUPS.flatMap((g) => g.tabs.map((t) => [t.id, g.id])));
TAB_TO_GROUP.compare = 'ship';
TAB_TO_GROUP.dataagent = 'ship';
// The single source of truth for valid tab ids: every grouped tab + the standalone far-right surfaces. Used to
// VALIDATE ids arriving from the host's navigateStudio — a stale bundle receiving an id it renamed (e.g. PR #29's
// 'powerquery'→'mcode') must IGNORE the switch, not fall through the render ternary into an arbitrary tab. Derived
// from TAB_GROUPS so it can never drift from the render switch.
const STANDALONE_TABS: StudioTab[] = ['history', 'workflows', 'knowledge'];
const LEGACY_TABS: StudioTab[] = ['compare', 'dataagent'];
const VALID_TABS = new Set<string>([...TAB_GROUPS.flatMap((g) => g.tabs.map((t) => t.id)), ...STANDALONE_TABS, ...LEGACY_TABS]);
// Keyboard next/prev cycles every tab in reading order: the grouped lifecycle tabs, then the standalone
// far-right surfaces in their on-screen order (Primer · Workflows · Edit History). Wraps at both ends.
const CYCLE_TABS: StudioTab[] = [...TAB_GROUPS.flatMap((g) => g.tabs.map((t) => t.id)), 'knowledge', 'workflows', 'history'];
const FALLBACK_TAB: StudioTab = 'diagram';   // must be a real, first-of-lifecycle tab — never a mid-list default
// A pending Data-tab target (from a Model-tree "Preview data" right-click). The nonce makes a repeat navigation to
// the SAME table re-fire the preview.
type DataTarget = { table: string; nonce: number };

export function App() {
  const [session, setSession] = useState<SessionInfo | null>(null);
  const sessionRef = useRef<SessionInfo | null>(null);
  sessionRef.current = session;
  // The live connection context is the single source of truth for the attached query engine (its session copy is
  // refreshed on attach/disconnect, which App's own `session` above is not) — the Compare seed reads BOTH from here.
  const { session: liveSession, conn, context: connectionContext, connectionsOpen, openConnections, closeConnections } = useConnection();
  const [card, setCard] = useState<Scorecard | null>(null);
  const [busy, setBusy] = useState(false);
  const [activity, setActivity] = useState<EditEntry[]>([]);
  // How many of the NEWEST timeline entries are currently undone (the redo branch). Driven by undo/redo broadcasts,
  // so it stays correct no matter which door (you or the AI) drove the step. items[0..undoneCount-1] are undone.
  const [undoneCount, setUndoneCount] = useState(0);
  const undoneCountRef = useRef(0); undoneCountRef.current = undoneCount;
  const activityLenRef = useRef(0); activityLenRef.current = activity.length;
  const [trend, setTrend] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [upsell, setUpsell] = useState<string | null>(null);   // a free click on the bulk button teaches, never errors
  const tier = useTier();
  const [tab, setTab] = useState<StudioTab>('diagram');   // open on Understand → Diagram
  const [dataTarget, setDataTarget] = useState<DataTarget | null>(null);          // table to preview (tree → Data)
  const [diagramAdd, setDiagramAdd] = useState<{ tables: string[]; nonce: number } | null>(null);  // tables to drop on the Diagram (tree → "Add to Studio Diagram")
  // Tree → Studio "jump to tab" targets: a right-click in the Model tree opens the owning tab AND focuses the object
  // (lineage → impact of a ref · M Code → that table's M · Advanced Modelling → the right builder). Nonce so a repeat
  // jump to the same target re-fires.
  const [lineageTarget, setLineageTarget] = useState<{ ref: string; nonce: number } | null>(null);
  const [workflowTarget, setWorkflowTarget] = useState<{ name: string; nonce: number } | null>(null);
  const [deployRestoreTarget, setDeployRestoreTarget] = useState<{ id: string; endpoint: string; database: string; nonce: number } | null>(null);
  const [pqTarget, setPqTarget] = useState<{ table: string; partitionId?: string; nonce: number } | null>(null);
  const [advArea, setAdvArea] = useState<{ area: string; nonce: number } | null>(null);
  const [searchTarget, setSearchTarget] = useState<{ query: string; nonce: number } | null>(null);   // findInModel → "Open in Search & Replace"
  // Context-bar → Compare: seed the differ with what you're EDITING vs what you're QUERYING so a click lands on the
  // exact diff. Nonce so a repeat click re-fires; Compare only adopts a seed it hasn't consumed and never clobbers a
  // comparison the user set up by hand.
  const [compareSeed, setCompareSeed] = useState<{ left: ModelRef; right: ModelRef | null; note?: string; nonce: number } | null>(null);
  const navNonce = useRef(0);
  // Seed the Compare from LIVE connection state (the query engine + the fresh session copy), not App's lagging session,
  // so the click targets the same model the footer names. `right` may be null when the attached dataset can't be named
  // (the seed carries a note the Compare tab surfaces instead of diffing an arbitrary dataset).
  // Fresh copies for the seed path: a COLD status-bar open flushes navigation through onNavigate's first-render
  // closure (stale liveSession/session/conn), and the session may not have loaded yet at all. Read the latest via
  // refs, and if the session isn't ready, DEFER (never a timeout) — the resolver effect below seeds once it lands.
  const sessionForSeedRef = useRef<SessionInfo | null>(null);
  sessionForSeedRef.current = liveSession ?? session;
  const connRef = useRef(conn);
  connRef.current = conn;
  const connectionContextRef = useRef(connectionContext);
  connectionContextRef.current = connectionContext;
  const [seedPending, setSeedPending] = useState(false);
  const jumpToCompare = () => {
    goTab('deploy');
    const s = sessionForSeedRef.current;
    if (s?.sessionId) { const seed = compareSeedFromSession(s, connRef.current, connectionContextRef.current); setCompareSeed({ ...seed, nonce: ++navNonce.current }); setSeedPending(false); }
    else setSeedPending(true);   // session not ready — the resolver effect applies the seed when it arrives
  };
  // Apply a DEFERRED Compare seed once the session finally lands (the cold status-bar-open race). We wait for a
  // definite session (either the live copy or App's), never a timer. If it resolves with NO model open we still
  // can't seed — surface that in the Compare tab (a note) rather than drop the seed silently.
  useEffect(() => {
    if (!seedPending) return;
    const s = liveSession ?? session;
    if (!s) return;   // still loading — re-run when a session copy arrives
    setSeedPending(false);
    if (s.sessionId) { const seed = compareSeedFromSession(s, connRef.current, connectionContextRef.current); setCompareSeed({ ...seed, nonce: ++navNonce.current }); }
    else setCompareSeed({ left: { kind: 'session', label: 'working copy' }, right: null, nonce: ++navNonce.current,
      note: 'No model is open, so there is nothing to compare against what you are querying. Open a model, then try again.' });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seedPending, liveSession, session]);
  const [unseen, setUnseen] = useState<Set<string>>(new Set());   // tabs with new Claude activity (badge)
  const [pendingApprovals, setPendingApprovals] = useState<ApprovalNotice[]>([]);
  const pendingApprovalCount = pendingApprovals.length;
  const [permissionTarget, setPermissionTarget] = useState<{ id: string; nonce: number } | null>(null);
  const tabRef = useRef(tab); tabRef.current = tab;
  const rescanTimer = useRef<number | undefined>(undefined);

  const refreshPendingApprovals = useCallback(() => rpc<ApprovalNotice[]>('listPendingApprovals')
    .then((items) => setPendingApprovals((items ?? []).filter((x) => !x.grantedUtc))), []);

  // Approval requests can arrive through the MCP door while the human is anywhere in Studio. Poll at the shell
  // level so the callout is not trapped inside the Permissions tab that the human does not yet know to open.
  // With no model session there can be no actionable request; standing the poll down also avoids reconnect noise.
  useEffect(() => {
    if (!session?.sessionId) { setPendingApprovals([]); return; }
    let cancelled = false;
    const pull = () => rpc<ApprovalNotice[]>('listPendingApprovals')
      .then((items) => { if (!cancelled) setPendingApprovals((items ?? []).filter((x) => !x.grantedUtc)); })
      .catch(() => undefined);   // preserve the last known count through a transient engine reconnect
    void pull();
    const iv = window.setInterval(() => { void pull(); }, 5000);
    return () => { cancelled = true; window.clearInterval(iv); };
  }, [session?.sessionId]);

  // Badge a tab when Claude runs something on it while you're elsewhere; switching to a tab clears its badge.
  useEffect(() => onActivity((e) => {
    // Legacy ids badge their VISIBLE hosts: compare/dataagent render inside Deploy, spec inside
    // Change Plan. Without the remap the badge lands on a tab that no longer exists in the nav.
    const ALIAS: Record<string, string> = { dataagent: 'deploy', compare: 'deploy', spec: 'optimize' };
    const raw = KIND_TAB[e.kind];
    const t = raw ? (ALIAS[raw] ?? raw) : raw;
    const visibleTab = ALIAS[tabRef.current] ?? tabRef.current;
    if (t && t !== visibleTab) setUnseen((s) => { const n = new Set(s); n.add(t); return n; });
  }), []);
  const goTab = (t: string, approvalId?: string) => {
    // Validate against the known tab set (single source of truth). An unknown id — e.g. a host on a newer/older
    // protocol sending a renamed tab a stale bundle doesn't have — is IGNORED (never a mystery landing on some
    // arbitrary tab), and logged so version skew is visible in devtools instead of silent.
    if (!VALID_TABS.has(t)) { console.warn(`[Studio] ignoring navigation to unknown tab id '${t}' (host/webview version skew?)`); return; }
    if (t === 'compare') t = 'deploy';   // compatibility alias: comparison is Deploy's Push changes review
    if (t === 'permissions' && approvalId) setPermissionTarget({ id: approvalId, nonce: ++navNonce.current });
    setUnseen((s) => { if (!s.has(t)) return s; const n = new Set(s); n.delete(t); return n; });
    setTab(t as StudioTab);
  };
  // Group memory lives HERE (not in Shell) so the keyboard group jumps (Ctrl+Shift+1–5) and the group
  // buttons share it: entering a group returns you to the tab you last used in it, not always its first.
  const lastInGroup = useRef<Record<string, StudioTab>>({});
  useEffect(() => {
    const visibleTab = tab === 'dataagent' ? 'deploy' : tab;
    const g = TAB_TO_GROUP[visibleTab]; if (g) lastInGroup.current[g] = visibleTab;
  }, [tab]);
  const goGroup = (gid: string) => { const g = TAB_GROUPS.find((x) => x.id === gid); if (g) goTab(lastInGroup.current[g.id] ?? g.tabs[0].id); };
  // Next/prev Studio tab (Ctrl+Alt+←/→ via the host keybinding, so it works with focus inside OR outside the webview).
  const cycleTab = (dir: 1 | -1) => {
    const i = CYCLE_TABS.indexOf(tabRef.current);
    goTab(CYCLE_TABS[((i < 0 ? 0 : i) + dir + CYCLE_TABS.length) % CYCLE_TABS.length]);
  };
  // The '?' keyboard-shortcuts cheat sheet (also opened by the "Semanticus: Keyboard Shortcuts" command).
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  // "Review as a plan →" from AI Readiness / BPA: jump to Change Plan and seed it (the view auto-proposes only if the
  // plan is empty, so an in-progress plan is never clobbered). Turns Change Plan into the apply-mode of the findings.
  const [planSeed, setPlanSeed] = useState(0);
  const reviewAsPlan = () => { setPlanSeed((n) => n + 1); goTab('optimize'); };
  const openWorkflow = (name: string) => { setWorkflowTarget({ name, nonce: ++navNonce.current }); goTab('workflows'); };

  // The host navigates us here (e.g. a Model-tree "Preview data" right-click): switch tabs and, for Data, hand the
  // target table to the Data view (nonce so re-selecting the same table re-previews). signalReady lets the host
  // flush a navigation it queued while opening Studio cold.
  useEffect(() => {
    signalReady();
    return onNavigate((m) => {
      // Keyboard/command pseudo-targets first — these aren't tabs, so they must never reach goTab.
      if (m.tab === 'shortcuts') { setShortcutsOpen(true); return; }
      if (m.tab === 'cycle:next') { cycleTab(1); return; }
      if (m.tab === 'cycle:prev') { cycleTab(-1); return; }
      if (m.tab?.startsWith('group:')) { goGroup(m.tab.slice('group:'.length)); return; }
      if (m.tab === 'readiness' && m.target === 'rescan') { void scan(); }   // the palette/keyboard "run a scan" — then land on the tab
      if (m.tab === 'data' && m.target) {
        const table = m.target.startsWith('table:') ? m.target.slice('table:'.length) : m.target;
        setDataTarget({ table, nonce: ++navNonce.current });
      }
      if (m.tab === 'diagram' && m.addTables?.length) setDiagramAdd({ tables: m.addTables, nonce: ++navNonce.current });
      if (m.tab === 'lineage' && m.target) setLineageTarget({ ref: m.target, nonce: ++navNonce.current });
      if (m.tab === 'mcode' && m.target) {
        // 'partition:<Table>/<Name>' pinpoints WHICH partition was clicked; derive the table for the picker and
        // keep the full ref so the M lane can land on that exact partition. 'table:<T>' selects the table only.
        const rest = m.target.startsWith('partition:') ? m.target.slice('partition:'.length) : null;
        const slash = rest ? rest.indexOf('/') : -1;
        if (rest && slash > 0) setPqTarget({ table: rest.slice(0, slash), partitionId: m.target, nonce: ++navNonce.current });
        else setPqTarget({ table: m.target.startsWith('table:') ? m.target.slice('table:'.length) : m.target, nonce: ++navNonce.current });
      }
      if (m.tab === 'advmodels' && m.target) setAdvArea({ area: m.target.startsWith('area:') ? m.target.slice('area:'.length) : m.target, nonce: ++navNonce.current });
      // The native sync status-bar item → a seeded Compare (same as the footer click): compute the seed from
      // live connection state and land on Review. jumpToCompare already seeds + goTab('compare'), so return early.
      if (m.tab === 'compare' && m.target === 'seed') { jumpToCompare(); return; }
      // '' is a real target here: "just focus the find box" (Ctrl+F) — only undefined means no hand-off.
      if (m.tab === 'search' && m.target != null) setSearchTarget({ query: m.target, nonce: ++navNonce.current });
      if (m.tab) goTab(m.tab);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // The in-Studio half of the keyboard suite: gestures VS Code keybindings must NOT own (see shortcuts.tsx —
  // unmodified '?' would fire while typing, and Ctrl+Alt+letter package bindings collide with AltGr typing on
  // European layouts). Everything here checks the target is not editable, so typing is never hijacked.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.isComposing) return;
      if (!e.ctrlKey && !e.metaKey && !e.altKey && e.key === '?' && !isTypingTarget(e.target)) {
        e.preventDefault(); setShortcutsOpen((v) => !v); return;
      }
      if (!e.ctrlKey || !e.altKey || e.metaKey || isTypingTarget(e.target)) return;
      if (e.key.toLowerCase() === 'z') { e.preventDefault(); void rpc(e.shiftKey ? 'redo' : 'undo').catch(() => undefined); return; }   // the shared model timeline (both doors see it); empty-stack pops are no-ops
      if (e.shiftKey) return;
      if (e.key.toLowerCase() === 't') { e.preventDefault(); focusModelTree(); return; }
      const t = tabForKey(e);
      if (t) { e.preventDefault(); if (t === 'readiness') void scan(); goTab(t); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function scan() {
    try {
      const c = await rpc<Scorecard>('aiReadinessScan');
      setCard(c);
      setTrend((t) => (t.length && t[t.length - 1] === c.overall ? t : [...t, c.overall].slice(-40)));
      setError(null);
    } catch (e) { setError(String((e as Error).message ?? e)); }
  }
  async function refreshSession() {
    try { const s = await rpc<SessionInfo>('sessionInfo'); setSession(s); return s; } catch { return null; }
  }

  useEffect(() => {
    (async () => { const s = await refreshSession(); if (s?.sessionId) await scan(); })();
    // Opening a file/project, local running model, or XMLA model swaps the engine session without emitting
    // model/didChange. Refresh App's authoritative copy as well as ConnectionProvider's copy, then reset the
    // model-scoped shell state. The session key below remounts every tab so a view cannot retain model A's graph,
    // tests, forms, or async state while the native tree already shows model B.
    const offReconnect = onReconnect(() => {
      const previousSessionId = sessionRef.current?.sessionId;
      void (async () => {
        const s = await refreshSession();
        // A failed refresh is not evidence of a model swap. Preserve the current shell until the host
        // confirms a replacement session, otherwise a transient RPC failure would erase local history.
        if (s?.sessionId && s.sessionId !== previousSessionId) {
          setCard(null); setError(null); setUpsell(null);
          setActivity([]); setUndoneCount(0); setPendingApprovals([]);
        }
        if (s?.sessionId) await scan();
      })();
    });
    const off = onDidChange((n: ChangeNotification) => {
      // Undo/redo broadcast as their own didChange (label 'undo'/'redo'). Interpret them as moving the rollback
      // boundary rather than appending a node — so the timeline reads like a stack-state view (undone entries dim,
      // redo restores them). A fresh edit clears the redo branch (TE2's UndoManager drops the RedoStack), so the
      // undone entries can never come back — drop them and start the new entry on top.
      // The engine broadcasts undo/redo even on an EMPTY stack (a no-op pop still fires the event), reachable via the
      // MCP undo door or a second client — so clamp to [0, edits]. Without the upper cap an extra undo would inflate
      // undoneCount past the entry count and desync the timeline (negative "edits" tile, off-by-one redo).
      if (n.label === 'undo') { setUndoneCount((c) => Math.min(c + 1, activityLenRef.current)); }
      else if (n.label === 'redo') { setUndoneCount((c) => Math.max(0, c - 1)); }
      else {
        setActivity((a) => [{ revision: n.revision, sessionId: n.sessionId, origin: n.origin, label: n.label, count: n.deltas?.length ?? 0, refs: (n.deltas ?? []).map((d) => d.ref).filter(Boolean).slice(0, 12), ts: Date.now() }, ...a.slice(undoneCountRef.current)].slice(0, 200));
        setUndoneCount(0);
      }
      window.clearTimeout(rescanTimer.current);
      rescanTimer.current = window.setTimeout(() => { void scan(); void refreshSession(); }, 350);
    });
    return () => { off(); offReconnect(); window.clearTimeout(rescanTimer.current); };
  }, []);

  async function applySafeFixes() {
    setBusy(true); setUpsell(null);
    try { const r = await rpc<{ scorecard: Scorecard }>('applySafeFixes'); if (r?.scorecard) setCard(r.scorecard); await refreshSession(); }
    catch (e) {
      // A free click on the bulk button gets the plain invitation, not a raw exception in a red banner.
      if (isEntitlementError(e)) setUpsell(`Each fix below is free. Apply them one at a time, as many as you like. Pro applies all ${card?.safeFixCount ?? 0} in one undoable step and re-checks your score for you.`);
      else setError(String((e as Error).message ?? e));
    }
    finally { setBusy(false); }
  }

  return (
    <DaxModelProvider key={session?.sessionId ?? 'no-model'}>
    <ActivityProvider>
    <ShortcutsOverlay open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
    <ConnectionsDrawer open={connectionsOpen} onClose={closeConnections} />
    <Shell tab={tab === 'dataagent' ? 'deploy' : tab} onTab={goTab} onGroup={goGroup} onShortcuts={() => setShortcutsOpen(true)} unseen={unseen} historyCount={activity.length - undoneCount} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={pendingApprovals[0]?.id} onConnections={openConnections} onJumpToCompare={jumpToCompare}>
      <>
      {/* A test run can carry substantial cell evidence, so keep the view alive across Studio navigation instead
          of copying it into persisted webview state. The session key still discards it when a different model opens. */}
      {session?.sessionId && <div className={tab === 'tests' ? 'h-full' : 'hidden'}><TestsView key={session.sessionId} /></div>}
      {!session?.sessionId ? (
        <Empty />
      ) : tab === 'evidence' ? (
        <EvidenceView key={session.sessionId} />
      ) : tab === 'readiness' ? (
        <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
          {error && <Banner color="var(--sem-bad)">{error}</Banner>}
          {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
          {card?.gatedBy?.length ? <Banner color="var(--sem-bad)">{card.gatedBy.join(' · ')}</Banner> : null}
          {card?.caveat ? <Banner color="var(--sem-warn)">{card.caveat}</Banner> : null}
          {card?.ruleErrors?.length ? <Banner color="var(--sem-warn)">{card.ruleErrors.join(' · ')}</Banner> : null}
          {card ? <Hero card={card} trend={trend} busy={busy} tier={tier} onSafe={applySafeFixes} onRescan={scan} onReviewAsPlan={reviewAsPlan} /> : <Loading />}
          {card && <Categories card={card} />}
          {/* Improve keeps the latest behavioral signal; Prove owns the complete interview evidence and actions. */}
          {card && <InterviewSummaryChip onOpen={() => goTab('tests')} />}
          {card && <Findings card={card} onRescan={scan} />}
          {card && <CustomRulesPanel kind="readiness" onChanged={() => void scan()} />}
        </div>
      ) : tab === 'history' ? (
        <HistoryView items={activity} undoneCount={undoneCount} sessionId={session?.sessionId}
          onOpenRollback={(point) => { setDeployRestoreTarget({ id: point.id, endpoint: point.endpoint, database: point.database, nonce: ++navNonce.current }); goTab('deploy'); }} />
      ) : tab === 'workflows' ? (
        <WorkflowsView navTarget={workflowTarget} />
      ) : tab === 'knowledge' ? (
        <KnowledgeView onOpenWorkflows={() => goTab('workflows')} />
      ) : tab === 'optimize' ? (
        <OptimizeView seedNonce={planSeed} />
      ) : tab === 'spec' ? (
        <SpecView session={session} />
      ) : tab === 'bpa' ? (
        <BpaView onReviewAsPlan={reviewAsPlan} />
      ) : tab === 'diagram' ? (
        <DiagramView addTables={diagramAdd} />
      ) : tab === 'advmodels' ? (
        <AdvancedModelsView navArea={advArea} />
      ) : tab === 'mcode' ? (
        <Suspense fallback={<Loading />}>
          <MCodeView navTarget={pqTarget} />
        </Suspense>
      ) : tab === 'stats' ? (
        <StatsView />
      ) : tab === 'data' ? (
        <DataPreviewView target={dataTarget} />
      ) : tab === 'lineage' ? (
        <LineageView navTarget={lineageTarget} onOpenPlan={() => goTab('optimize')} onOpenTests={() => goTab('tests')} onOpenWorkflow={openWorkflow} />
      ) : tab === 'search' ? (
        <SearchView navQuery={searchTarget} onOpenPlan={() => goTab('optimize')} />
      ) : tab === 'docs' ? (
        <DocumentationView />
      ) : tab === 'deploy' ? (
        <DeployView key="deploy" seed={compareSeed} dataAgent={<DataAgentView />} restoreTarget={deployRestoreTarget} onRestoreConsumed={() => setDeployRestoreTarget(null)} />
      ) : tab === 'tests' ? (
        null
      ) : tab === 'permissions' ? (
        <PermissionsView focusApproval={permissionTarget} onApprovalChanged={() => { void refreshPendingApprovals(); }} />
      ) : tab === 'dataagent' ? (
        // Distinct key + no seed: the legacy route must land ON the Data Agent. A shared fiber would
        // ignore initialMode on re-render, and a lingering compare seed would flip the mode to push.
        <DeployView key="dataagent" dataAgent={<DataAgentView />} initialMode="advanced" initialAdvanced="dataagent" />
      ) : tab === 'daxlab' ? (
        <DaxLabView />
      ) : (
        // Explicit, safe fallback: every valid tab is enumerated above and goTab rejects unknown ids, so this is
        // unreachable in practice — but if it ever fires it lands on the first-of-lifecycle tab, never a random
        // mid-list one (the old `: <DaxLabView/>` catch-all was itself the "lands on DAX Lab" skew bug).
        FALLBACK_TAB === 'diagram' ? <DiagramView addTables={diagramAdd} /> : null
      )}
      </>
    </Shell>
    </ActivityProvider>
    </DaxModelProvider>
  );
}

// The Semanticus brand mark — a compact abacus. An Ink rounded tile with three rods and nine beads;
// the three Signal-green beads trace a diagonal (matches the brand pack mark). Idle beads are muted grey.
function BrandMark() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-label="Semanticus" className="shrink-0">
      <rect x="1" y="1" width="22" height="22" rx="6" fill="#0D1117" />
      <rect x="1.5" y="1.5" width="21" height="21" rx="5.5" stroke="#EAEEF2" strokeOpacity="0.1" />
      <g stroke="#39424E" strokeWidth="1.2" strokeLinecap="round">
        <line x1="5" y1="7.5" x2="19" y2="7.5" /><line x1="5" y1="12" x2="19" y2="12" /><line x1="5" y1="16.5" x2="19" y2="16.5" />
      </g>
      <g fill="#5A6675">
        <circle cx="11" cy="7.5" r="2" /><circle cx="15" cy="7.5" r="2" />
        <circle cx="7" cy="12" r="2" /><circle cx="15" cy="12" r="2" />
        <circle cx="7" cy="16.5" r="2" /><circle cx="11" cy="16.5" r="2" />
      </g>
      <g fill="#2ED47A">
        <circle cx="7" cy="7.5" r="2" /><circle cx="11" cy="12" r="2" /><circle cx="15" cy="16.5" r="2" />
      </g>
    </svg>
  );
}

function Shell({ tab, onTab, onGroup, onShortcuts, unseen, historyCount, pendingApprovalCount, firstPendingApprovalId, onConnections, onJumpToCompare, children }: { tab: StudioTab; onTab: (t: string, approvalId?: string) => void; onGroup: (gid: string) => void; onShortcuts: () => void; unseen: Set<string>; historyCount: number; pendingApprovalCount: number; firstPendingApprovalId?: string; onConnections: () => void; onJumpToCompare: () => void; children: React.ReactNode }) {
  // Edit History and Workflows are standalone (no intent group) — a different axis from the five task intents
  // (a session-wide record / a cross-cutting playbook library). When either is active NO group is highlighted and the
  // secondary tab-row is hidden — they're full-bleed surfaces, not sub-tabs of anything.
  // Group memory (return to the tab you last used in a group) lives in App, shared with the keyboard jumps.
  const isHistory = tab === 'history';
  const isStandalone = isHistory || tab === 'workflows' || tab === 'knowledge';
  const activeGroup = isStandalone ? null : (TAB_TO_GROUP[tab] ?? TAB_GROUPS[0].id);
  const group = TAB_GROUPS.find((g) => g.id === activeGroup) ?? TAB_GROUPS[0];

  return (
    <div className="h-full flex flex-col">
      {/* primary row — analyst intents (left) + the standalone cross-cutting affordances (right) */}
      <header className="flex items-center gap-3 px-4 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
        <BrandMark />
        <div className="font-semibold tracking-tight">Semanticus Studio</div>
        <nav className="flex items-center gap-1 ml-3">
          {TAB_GROUPS.map((g, i) => (
            <GroupTab key={g.id} active={g.id === activeGroup} title={`${g.label} (Ctrl+Shift+${i + 1})`}
              unseen={g.id !== activeGroup && g.tabs.some((t) => unseen.has(t.id))}
              onClick={() => onGroup(g.id)}>{g.label}</GroupTab>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-3">
          <KnowledgeTab active={tab === 'knowledge'} onClick={() => onTab('knowledge')} />
          <WorkflowsTab active={tab === 'workflows'} onClick={() => onTab('workflows')} />
          <HistoryTab active={isHistory} count={historyCount} onClick={() => onTab('history')} />
          <LicenseButton />
          <span className="w-px h-5" style={{ background: 'var(--sem-border)' }} />
          <HelpButton tab={tab} onGo={(t) => onTab(t as StudioTab)} onShortcuts={onShortcuts} />
          <LiveActivity onOpen={onTab} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={firstPendingApprovalId} />
        </div>
      </header>
      {/* secondary row — the active group's tabs (hidden for the standalone Edit History / Workflows surfaces) */}
      {!isStandalone && (
        <div className="flex items-center gap-1 px-4 py-1.5 border-b" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          {group.tabs.map((t) => (
            <Tab key={t.id} active={tab === t.id} unseen={unseen.has(t.id)} onClick={() => onTab(t.id)}>
              {t.label}
              {t.id === 'permissions' && pendingApprovalCount > 0 && (
                <span className="ml-1 inline-flex min-w-4 h-4 px-1 items-center justify-center rounded-full text-[9px] leading-none font-bold tnum"
                  style={{ background: 'var(--sem-warn)', color: 'var(--sem-on-warn)' }} title={`${pendingApprovalCount} permission${pendingApprovalCount === 1 ? '' : 's'} waiting`}>
                  {pendingApprovalCount > 99 ? '99+' : pendingApprovalCount}
                </span>
              )}
            </Tab>
          ))}
        </div>
      )}
      <main className="flex-1 overflow-auto min-h-0">{children}</main>
      {/* Context bar — a FOOTER pinned to the bottom of the Studio panel (VS Code convention: ambient state lives at the
          bottom). flex:none so long tab content scrolls inside <main> above and never pushes it off screen. It replaces
          the old flat "name · N measures · N tables" header line: it separates what you're EDITING from what you're
          TESTING and PUBLISHING and flags when staged edits are absent from the queried model. Identity segments open
          Connections; Review changes alone opens the seeded diff. */}
      <ContextBar onConnections={onConnections} onReview={onJumpToCompare} />
    </div>
  );
}

// Standalone, always-visible Edit-History affordance — pinned far-right, separate from the intent groups
// because the co-authoring timeline is a session-wide record (a different axis from "what you do").
// The live count badge makes accumulating edits an ambient signal of co-authoring without opening the tab.
function HistoryTab({ active, count, onClick }: { active: boolean; count: number; onClick: () => void }) {
  return (
    <button onClick={onClick} title="Edit History: every change this session, yours and the AI Assistant's, on one undoable timeline"
      className="relative flex items-center gap-1.5 text-[12.5px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={active
        ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
        : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M3 3v5h5" /><path d="M3.05 13A9 9 0 1 0 6 5.3L3 8" /><path d="M12 7v5l3 2" />
      </svg>
      <span>Edit History</span>
      {count > 0 && (
        <span className="text-[10px] tnum rounded-full px-1.5 text-center" style={{ minWidth: 16, lineHeight: '15px',
          ...(active ? { background: 'rgba(6,33,15,0.20)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' }) }}>
          {count}
        </span>
      )}
    </button>
  );
}

// Standalone Workflows affordance — pinned far-right beside Edit History, separate from the Understand→Change→Improve→Prove→Ship
// lifecycle groups because a workflow library is a cross-cutting concern (playbooks that orchestrate the lifecycle),
// not a stage within it. Same button language as Edit History so the two standalone surfaces read as a pair.
function WorkflowsTab({ active, onClick }: { active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} title="Workflows: playbooks that chain steps with verified gates. Run one here, or let your AI Assistant run it."
      className="relative flex items-center gap-1.5 text-[12.5px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={active
        ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
        : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M9 6h11" /><path d="M9 12h11" /><path d="M9 18h11" /><circle cx="4" cy="6" r="1.6" /><circle cx="4" cy="12" r="1.6" /><circle cx="4" cy="18" r="1.6" />
      </svg>
      <span>Workflows</span>
    </button>
  );
}

// The Primer is the human-facing orientation document. Learning machinery remains behind it as a supplier.
function KnowledgeTab({ active, onClick }: { active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} title="Primer: the open model's shared orientation document"
      className="relative flex items-center gap-1.5 text-[12.5px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={active
        ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
        : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M12 2a7 7 0 0 0-4 12.7V17a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1v-2.3A7 7 0 0 0 12 2Z" /><path d="M9 21h6" />
      </svg>
      <span>Primer</span>
    </button>
  );
}

// Primary lifecycle-group button (filled-accent when active). The secondary Tab below uses the lighter soft-accent style,
// giving a clear two-tier hierarchy.
function GroupTab({ active, onClick, children, unseen, title }: { active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; title?: string }) {
  return (
    <button onClick={onClick} title={title} className="relative text-[13px] px-3 py-1 rounded-md font-semibold transition-colors"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { color: 'var(--sem-muted)' }}>
      {children}
      {unseen && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="The AI Assistant ran something in this group" />}
    </button>
  );
}

function Tab({ active, onClick, children, unseen }: { active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean }) {
  return (
    <button onClick={onClick} className="relative text-[12px] px-2.5 py-1 rounded-md font-medium"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)' }}>
      {children}
      {unseen && !active && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="The AI Assistant ran something here" />}
    </button>
  );
}

function Hero({ card, trend, busy, tier, onSafe, onRescan, onReviewAsPlan }: { card: Scorecard; trend: number[]; busy: boolean; tier: string; onSafe: () => void; onRescan: () => void; onReviewAsPlan?: () => void }) {
  const color = GRADE_COLOR[card.grade] ?? 'var(--sem-muted)';
  return (
    <Panel>
      <div className="flex items-center gap-5">
        <div className="flex flex-col items-center justify-center w-24 h-24 rounded-2xl" style={{ background: 'var(--sem-surface-2)', boxShadow: `inset 0 0 0 2px ${color}` }}>
          <div className="text-5xl font-bold leading-none" style={{ color }}>{card.grade}</div>
          <div className="text-[11px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>{card.overall.toFixed(0)}/100</div>
        </div>
        <div className="flex-1 min-w-0">
          <div className="text-[15px] font-semibold">AI Readiness {card.overall.toFixed(1)}</div>
          <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
            {card.findings.length} findings · {card.safeFixCount} safe fixes available
          </div>
          {trend.length >= 2 && (
            <div className="mt-1 -mb-1" style={{ maxWidth: 320 }}><Sparkline values={trend} /></div>
          )}
          <div className="flex flex-wrap gap-1.5 mt-2">
            {Object.entries(card.coverage).map(([k, v]) => (
              <span key={k} className="text-[11px] px-2 py-0.5 rounded-full tnum" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
                {prettyKey(k)} {v}%
              </span>
            ))}
          </div>
        </div>
        <div className="flex flex-col gap-2">
          <Button primary disabled={busy || card.safeFixCount === 0} onClick={onSafe}
            title={tier === 'free'
              ? `Pro applies all ${card.safeFixCount} in one undoable step and re-checks your score. Each fix below stays free, one at a time.`
              : `Apply every safe fix in one undoable step and re-check the score.`}>
            {busy ? 'Applying…' : `Apply ${card.safeFixCount} safe fix${card.safeFixCount === 1 ? '' : 'es'}`}
            <ProBadge show={tier === 'free'} variant="onAccent" />
          </Button>
          {onReviewAsPlan && card.findings.length > 0 && (
            <Button onClick={onReviewAsPlan} title="Review every fix as one change plan, then apply in bulk">Review as a plan →</Button>
          )}
          <Button onClick={onRescan}>Re-scan</Button>
        </div>
      </div>
    </Panel>
  );
}

function Categories({ card }: { card: Scorecard }) {
  const cats = card.categories.filter((c) => c.hasRules);
  return (
    <Panel>
      <SectionTitle>Categories</SectionTitle>
      <div className="flex flex-col gap-2.5 mt-2">
        {cats.map((c) => {
          const col = c.score >= 90 ? 'var(--sem-good)' : c.score >= 70 ? 'var(--sem-warn)' : 'var(--sem-bad)';
          return (
            <div key={c.category} className="flex items-center gap-3">
              <div className="w-36 text-[12px] shrink-0">{c.category}</div>
              <div className="flex-1 h-2 rounded-full overflow-hidden" style={{ background: 'var(--sem-surface-2)' }}>
                <div className="h-full rounded-full transition-all" style={{ width: `${c.score}%`, background: col }} />
              </div>
              <div className="w-12 text-right text-[12px] tnum" style={{ color: 'var(--sem-muted)' }}>{c.score.toFixed(0)}</div>
              <div className="w-20 text-right text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{c.violations}/{c.applicable}</div>
            </div>
          );
        })}
      </div>
    </Panel>
  );
}

const sevNum = (s: string) => (s === 'Critical' || s === 'High' ? 3 : s === 'Medium' ? 2 : 1);

function Findings({ card, onRescan }: { card: Scorecard; onRescan: () => void }) {
  const [filter, setFilter] = useState<'all' | 'SafeFix' | 'AiContent' | 'Proposal'>('all');
  const [waiveErr, setWaiveErr] = useState<string | null>(null);
  const { state: working, keyOf, fix, ask } = useFixState('applyFix', 'getFixPrompt');

  const activeFindings = card.findings.filter((f) => !f.waived);
  const waivedFindings = card.findings.filter((f) => f.waived);
  const counts = activeFindings.reduce((m, f) => { m[f.fix] = (m[f.fix] ?? 0) + 1; return m; }, {} as Record<string, number>);
  const list = activeFindings.filter((f) => filter === 'all' || f.fix === filter);
  const byKey = new Map(card.findings.map((f) => [f.objectRef + '|' + f.ruleId, f]));
  const toRow = (f: Finding): FindingRow => ({
    // Provenance in the rule header: a finding from a model-embedded (user-authored) rule says so.
    ruleId: f.ruleId, ruleName: f.ruleTitle + (f.custom ? ' (custom rule)' : ''), category: f.category, severity: sevNum(f.severity),
    // Prefer the analyst rendering (agent-only op hint removed server-side); fall back to the raw message when absent
    // OR blank (defence in depth: a blank displayMessage must never render as an empty finding).
    objectRef: f.objectRef, objectName: f.objectName, message: f.displayMessage?.trim() ? f.displayMessage : f.message,
    waived: f.waived, waiverReason: f.waiverReason, waiverRuleLevel: f.waiverRuleLevel,
    tag: { label: (FIX_STYLE[f.fix] ?? FIX_STYLE.None).label, color: (FIX_STYLE[f.fix] ?? FIX_STYLE.None).color },
  });
  const rows: FindingRow[] = list.map(toRow);
  const waivedRows: FindingRow[] = waivedFindings.map(toRow);
  const run = (p: Promise<unknown>) => { setWaiveErr(null); void p.then(onRescan).catch((e) => setWaiveErr(String((e as Error).message ?? e))); };

  const waive = (r: FindingRow, reason: string) => run(rpc('waiveFinding', 'air', r.ruleId, r.objectRef, reason));
  const waiveRule = (ruleId: string, reason: string) => run(rpc('waiveFinding', 'air', ruleId, '*', reason));
  // un-waive routes to the rule-level waiver ('*') when waived model-wide, else the single instance.
  const unwaive = (r: FindingRow) => run(rpc('unwaiveFinding', 'air', r.ruleId, r.waiverRuleLevel ? '*' : r.objectRef));

  const actions = (r: FindingRow) => {
    const f = byKey.get(r.objectRef + '|' + r.ruleId); if (!f) return null;
    const st = working[keyOf(f.ruleId, f.objectRef)];
    // Every finding gets a fix affordance: a deterministic Apply for SafeFix, and the free "Ask AI" for
    // everything else (AiContent AND Proposal/Review — get_fix_prompt's default branch builds a grounded
    // prompt for any rule). A finding with no button is a dead end the free-tier promise forbids.
    const fixBtn = f.fix === 'SafeFix'
      ? (st === 'done'
        ? <span className="text-[10px]" style={{ color: 'var(--sem-good)' }}>fixed ✓</span>
        : <MiniButton disabled={st === 'fixing'} onClick={() => fix(f.ruleId, f.objectRef, onRescan)}>{st === 'fixing' ? '…' : 'Apply'}</MiniButton>)
      : <MiniButton onClick={() => ask(f.ruleId, f.objectRef)}>{st === 'copied' ? 'Copied ✓' : 'Ask AI'}</MiniButton>;
    return <span className="flex items-center gap-1">{fixBtn}<WaiveControl onWaive={(reason) => waive(r, reason)} onUnwaive={() => unwaive(r)} /></span>;
  };
  // The rule-header un-waive removes the MODEL-WIDE ('*') waiver — the mirror of "Waive rule" made from the
  // same spot it was made (it was a dead no-op before the 2026-07-07 hook-fix batch).
  const ruleActions = (ruleId: string) => <WaiveControl label="Waive rule" title="Accept every instance of this rule, model-wide (Pro)" onWaive={(reason) => waiveRule(ruleId, reason)} onUnwaive={() => run(rpc('unwaiveFinding', 'air', ruleId, '*'))} />;

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-2">
          <SectionTitle>Findings <span style={{ color: 'var(--sem-muted)' }}>({activeFindings.length}{card.waivedCount ? ` · ${card.waivedCount} waived` : ''})</span></SectionTitle>
          <div className="ml-auto flex gap-1">
            <Chip active={filter === 'all'} onClick={() => setFilter('all')}>All</Chip>
            <Chip active={filter === 'SafeFix'} onClick={() => setFilter('SafeFix')}>Safe {counts.SafeFix ?? 0}</Chip>
            <Chip active={filter === 'AiContent'} onClick={() => setFilter('AiContent')}>AI {counts.AiContent ?? 0}</Chip>
            <Chip active={filter === 'Proposal'} onClick={() => setFilter('Proposal')}>Review {counts.Proposal ?? 0}</Chip>
          </div>
        </div>
        {waiveErr && <div className="mt-2 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{waiveErr}</div>}
        <div className="mt-2"><GroupedFindings rows={rows} renderActions={actions} renderRuleActions={(ruleId) => ruleActions(ruleId)} /></div>
      </Panel>
      <WaivedList rows={waivedRows} onUnwaive={unwaive} />
    </div>
  );
}

// ---- small primitives ------------------------------------------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border p-4 ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function Chip({ children, active, onClick }: { children: React.ReactNode; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} className="text-[10px] px-1.5 py-0.5 rounded-md font-medium"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
      {children}
    </button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color, border: `1px solid color-mix(in srgb,${color} 40%, transparent)` }}>{children}</div>;
}
function Loading() { return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Scanning model…</div></Panel>; }
function Empty() {
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">No model open</div>
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Open a model from the Semanticus tree (Open Model…), then this dashboard scores it for AI readiness and lets you and the AI Assistant fix it together.</div>
      </div>
    </div>
  );
}

// ---- Statistics (VertiPaq storage) -----------------------------------------------------------
interface VpaqReport { modelSize: number; columnCount: number; tables: VpaqTable[]; topColumns: VpaqColumn[]; isDirectLake?: boolean; caveat?: string; error?: string; }

function fmtMB(bytes: number) { const mb = bytes / 1024 / 1024; return mb >= 1 ? mb.toFixed(1) + ' MB' : (bytes / 1024).toFixed(0) + ' KB'; }
function fmtInt(n: number) { return (n ?? 0).toLocaleString(); }
const ENC_COLOR: Record<string, string> = { Hash: '#7C8BA5', Value: '#36c98b', RLE: '#e0b341' };

function StatsView() {
  const { conn } = useConnection();
  const [report, setReport] = useState<VpaqReport | null>(null);
  const [meta, setMeta] = useState<ModelGraph | null>(null);   // model metadata — available OFFLINE (no live engine)
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [claudeEvent, setClaudeEvent] = useState<ActivityEvent | null>(null);

  // Model metadata (table / column / measure counts) is a metadata read that works with no live engine — so the
  // tab shows a real overview offline instead of a blank "attach" screen. Storage size/encoding still need a scan.
  useEffect(() => { rpc<ModelGraph>('getModelGraph').then(setMeta).catch(() => undefined); }, []);

  // Reflect the user's Claude running vpaq_scan live (attributed); the human's own Scan clears the attribution.
  useClaudeReflection('vpaq_scan', (e) => {
    if (e.result) { setReport(e.result as VpaqReport); setErr(e.error ?? null); setClaudeEvent(e); }
  });

  async function scan() {
    setBusy(true); setErr(null); setClaudeEvent(null);
    try { const r = await rpc<VpaqReport>('vertiPaqScan', 200); if (r.error) setErr(r.error); else setReport(r); }
    catch (e) { setErr(String((e as Error).message ?? e)); } finally { setBusy(false); }
  }
  // Auto-scan once a live engine connects — the storage view is ready immediately (Refresh re-runs it).
  useEffect(() => { if (conn?.connected && !report && !busy) void scan();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conn?.connected]);

  // Derived analytics (from what the DMV gives us): total rows, the largest table, and the encoding mix by
  // size across the scanned (largest) columns — the "what dominates storage" picture.
  const totalRows = report ? report.tables.reduce((n, t) => n + (t.rows || 0), 0) : 0;
  const largest = report?.tables[0];
  const encMix = useMemo(() => {
    if (!report) return [] as { enc: string; size: number; pct: number }[];
    const by = new Map<string, number>();
    for (const c of report.topColumns) by.set(c.encoding, (by.get(c.encoding) || 0) + c.totalSize);
    const tot = [...by.values()].reduce((a, b) => a + b, 0) || 1;
    return [...by.entries()].map(([enc, size]) => ({ enc, size, pct: Math.round((100 * size) / tot) })).sort((a, b) => b.size - a.size);
  }, [report]);
  const maxTableSize = report ? Math.max(1, ...report.tables.map((t) => t.size)) : 1;
  const maxColSize = report ? Math.max(1, ...report.topColumns.map((c) => c.totalSize)) : 1;

  return (
    <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
      <Panel>
        <div className="flex items-start gap-3">
          <div className="flex-1 min-w-0">
            <div className="text-[13px] font-semibold">Storage · VertiPaq</div>
            <div className="mt-1"><ConnectBar hint="Storage analysis reads live VertiPaq storage stats from the model's query engine." /></div>
          </div>
          {conn?.connected && <Button primary disabled={busy} onClick={scan}>{busy ? 'Scanning…' : report ? 'Refresh' : 'Scan storage'}</Button>}
        </div>
        {err && <div className="mt-2"><Banner color="var(--sem-bad)">{err}</Banner></div>}
      </Panel>

      {/* VertiPaq stats come from the published model's query engine, not your staged edits — flag any divergence. */}
      <QueryStalenessChip />

      {claudeEvent && <ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} />}

      {report?.isDirectLake && report.caveat && <Banner color="var(--sem-warn)">{report.caveat}</Banner>}

      {/* Offline overview — model metadata while no live storage scan exists yet (before connect, or pre-scan). */}
      {!report && meta && (
        <>
          <Panel>
            <div className="flex items-baseline gap-6 flex-wrap">
              <div><div className="text-3xl font-bold tnum">{meta.tables.length}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>tables</div></div>
              <div><div className="text-xl font-semibold tnum">{fmtInt(meta.tables.reduce((n, t) => n + t.columns, 0))}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>columns</div></div>
              <div><div className="text-xl font-semibold tnum">{fmtInt(meta.tables.reduce((n, t) => n + t.measures, 0))}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>measures</div></div>
              <div><div className="text-xl font-semibold tnum">{meta.relationships.length}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>relationships</div></div>
              <div className="flex-1 min-w-[160px] text-[11px]" style={{ color: 'var(--sem-muted)' }}>Choose a test model above for storage size, row counts and VertiPaq encoding.</div>
            </div>
          </Panel>
          <Panel>
            <SectionTitle>Tables <span style={{ color: 'var(--sem-muted)' }}>({meta.tables.length})</span></SectionTitle>
            <div className="mt-1">
              <SortableTable
                rows={meta.tables}
                filterText={(t) => t.name}
                filterPlaceholder="Filter tables…"
                initialSort={{ key: 'columns', dir: -1 }}
                maxHeight={520}
                onRowClick={(t) => revealInTree('table:' + t.name)}
                cols={[
                  { key: 'name', label: 'Table', sortVal: (t) => t.name, render: (t) => <span className="font-medium" style={{ opacity: t.isHidden ? 0.55 : 1 }}>{t.name}</span> },
                  { key: 'columns', label: 'Cols', align: 'right', sortVal: (t) => t.columns, render: (t) => <span className="tnum">{t.columns}</span> },
                  { key: 'measures', label: 'Measures', align: 'right', sortVal: (t) => t.measures, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.measures}</span> },
                  { key: 'kind', label: 'Kind', sortVal: (t) => (t.isDateTable ? 'date' : t.isCalculated ? 'calc' : t.isHidden ? 'hidden' : ''), render: (t) => (
                    t.isDateTable ? <span className="text-[10px] px-1 rounded" style={{ background: 'var(--sem-warn)', color: '#000' }}>DATE</span>
                    : t.isCalculated ? <span className="text-[10px] px-1 rounded" style={{ background: 'var(--sem-good)', color: '#000' }}>CALC</span>
                    : t.isHidden ? <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>hidden</span> : <span /> ) },
                ]}
              />
            </div>
          </Panel>
        </>
      )}

      {report && (
        <>
          <Panel>
            <div className="flex items-baseline gap-6 flex-wrap">
              <div><div className="text-3xl font-bold tnum">{fmtMB(report.modelSize)}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>model size</div></div>
              <div><div className="text-xl font-semibold tnum">{report.tables.length}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>tables</div></div>
              <div><div className="text-xl font-semibold tnum">{report.columnCount}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>columns</div></div>
              <div><div className="text-xl font-semibold tnum">{report.isDirectLake ? 'Not applicable' : fmtInt(totalRows)}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{report.isDirectLake ? 'rows (resident-only)' : 'total rows'}</div></div>
              {largest && <div><div className="text-xl font-semibold truncate" style={{ maxWidth: 180 }}>{largest.name}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>largest table · {largest.pctOfModel}%</div></div>}
              {encMix.length > 0 && (
                <div className="min-w-[200px] flex-1">
                  <div className="flex h-2.5 rounded overflow-hidden" style={{ background: 'var(--sem-surface-2)' }}>
                    {encMix.map((e) => <div key={e.enc} style={{ width: e.pct + '%', background: ENC_COLOR[e.enc] || 'var(--sem-muted)' }} title={`${e.enc} · ${fmtMB(e.size)} · ${e.pct}%`} />)}
                  </div>
                  <div className="flex gap-2.5 mt-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>
                    {encMix.map((e) => <span key={e.enc}><span style={{ color: ENC_COLOR[e.enc] || 'var(--sem-muted)' }}>■</span> {e.enc} {e.pct}%</span>)}
                  </div>
                </div>
              )}
            </div>
          </Panel>
          <Panel>
            <div className="flex items-center gap-2">
              <SectionTitle>Storage map</SectionTitle>
              <div className="ml-auto text-[10px] flex gap-2" style={{ color: 'var(--sem-muted)' }}>
                <span><span style={{ color: '#7C8BA5' }}>■</span> Hash</span>
                <span><span style={{ color: '#36c98b' }}>■</span> Value</span>
                <span><span style={{ color: '#e0b341' }}>■</span> RLE</span>
              </div>
            </div>
            <div className="mt-2"><VpaqTreemap tables={report.tables} columns={report.topColumns} /></div>
          </Panel>
          <Panel>
            <SectionTitle>Tables <span style={{ color: 'var(--sem-muted)' }}>({report.tables.length})</span></SectionTitle>
            <div className="mt-1">
              <SortableTable
                rows={report.tables}
                filterText={(t) => t.name}
                filterPlaceholder="Filter tables…"
                initialSort={{ key: 'size', dir: -1 }}
                onRowClick={(t) => revealInTree('table:' + t.name)}
                cols={[
                  { key: 'name', label: 'Table', sortVal: (t) => t.name, render: (t) => <span className="font-medium">{t.name}</span> },
                  { key: 'rows', label: 'Rows', align: 'right', sortVal: (t) => t.rows ?? -1, render: (t) => <span className="tnum">{t.rows == null ? 'Not available' : fmtInt(t.rows)}</span> },
                  { key: 'columns', label: 'Cols', align: 'right', sortVal: (t) => t.columns ?? 0, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.columns ?? 'Not available'}</span> },
                  { key: 'size', label: 'Size', align: 'right', sortVal: (t) => t.size, render: (t) => <SizeCell bytes={t.size} max={maxTableSize} /> },
                  { key: 'pctOfModel', label: '% model', align: 'right', sortVal: (t) => t.pctOfModel, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.pctOfModel}%</span> },
                ]}
              />
            </div>
          </Panel>
          <Panel>
            <SectionTitle>Columns <span style={{ color: 'var(--sem-muted)' }}>({report.topColumns.length})</span></SectionTitle>
            <div className="mt-1">
              <SortableTable
                rows={report.topColumns}
                filterText={(c) => c.table + ' ' + c.column}
                filterPlaceholder="Filter columns…"
                initialSort={{ key: 'totalSize', dir: -1 }}
                maxHeight={460}
                onRowClick={(c) => revealInTree('column:' + c.table + '/' + c.column)}
                cols={[
                  { key: 'column', label: 'Column', sortVal: (c) => c.column, render: (c) => <span><span className="font-medium">{c.column}</span> <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>· {c.table}</span></span> },
                  { key: 'encoding', label: 'Encoding', sortVal: (c) => c.encoding, render: (c) => <span className="text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: ENC_COLOR[c.encoding] || 'var(--sem-muted)' }}>{c.encoding}</span> },
                  { key: 'totalSize', label: 'Total', align: 'right', sortVal: (c) => c.totalSize, render: (c) => <SizeCell bytes={c.totalSize} max={maxColSize} /> },
                  { key: 'dataSize', label: 'Data', align: 'right', sortVal: (c) => c.dataSize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.dataSize != null ? fmtMB(c.dataSize) : 'Not available'}</span> },
                  { key: 'dictionarySize', label: 'Dict', align: 'right', sortVal: (c) => c.dictionarySize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.dictionarySize != null ? fmtMB(c.dictionarySize) : 'Not available'}</span> },
                  { key: 'pctOfModel', label: '% model', align: 'right', sortVal: (c) => c.pctOfModel, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.pctOfModel}%</span> },
                ]}
              />
            </div>
          </Panel>
        </>
      )}
    </div>
  );
}

// A small relative-size cell: the value + a proportional bar (max = the largest in the set).
function SizeCell({ bytes, max }: { bytes: number; max: number }) {
  return (
    <div className="flex items-center gap-2 justify-end">
      <span className="tnum">{fmtMB(bytes)}</span>
      <div className="w-14 h-1.5 rounded overflow-hidden shrink-0" style={{ background: 'var(--sem-surface-2)' }}>
        <div className="h-full rounded" style={{ width: Math.max(2, Math.round((100 * bytes) / max)) + '%', background: 'var(--sem-accent)' }} />
      </div>
    </div>
  );
}

// Type-aware comparator (numbers numeric; text natural). Nulls last.
function cmpVal(a: number | string, b: number | string): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

interface SortCol<T> { key: string; label: string; align?: 'left' | 'right'; sortVal: (r: T) => number | string; render: (r: T) => React.ReactNode; }
// A compact sortable + filterable table (non-virtualized — for the bounded stats lists). Click a header to sort
// (asc → desc), type the filter to narrow, click a row to reveal the object in the Model tree.
function SortableTable<T>({ rows, cols, initialSort, filterText, filterPlaceholder, onRowClick, maxHeight }: {
  rows: T[]; cols: SortCol<T>[]; initialSort: { key: string; dir: 1 | -1 };
  filterText?: (r: T) => string; filterPlaceholder?: string; onRowClick?: (r: T) => void; maxHeight?: number;
}) {
  const [sort, setSort] = useState(initialSort);
  const [q, setQ] = useState('');
  const view = useMemo(() => {
    let r = rows;
    const needle = q.trim().toLowerCase();
    if (needle && filterText) r = r.filter((x) => filterText(x).toLowerCase().includes(needle));
    const c = cols.find((c) => c.key === sort.key);
    if (c) r = [...r].sort((a, b) => cmpVal(c.sortVal(a), c.sortVal(b)) * sort.dir);
    return r;
  }, [rows, q, sort, cols, filterText]);
  const onHeader = (key: string) => setSort((s) => (s.key === key ? { key, dir: (s.dir === 1 ? -1 : 1) as 1 | -1 } : { key, dir: -1 }));

  return (
    <div>
      {filterText && (
        <div className="flex items-center gap-2 pb-1.5">
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={filterPlaceholder || 'Filter…'} spellCheck={false}
            className="text-[12px] px-2 py-1 rounded-md outline-none w-56" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <span className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{view.length === rows.length ? `${rows.length}` : `${view.length} of ${rows.length}`}</span>
        </div>
      )}
      <div className="overflow-auto" style={maxHeight ? { maxHeight } : undefined}>
        <table className="w-full border-collapse">
          <thead className="sticky top-0 z-10" style={{ background: 'var(--sem-surface)' }}>
            <tr>
              {cols.map((c) => {
                const active = sort.key === c.key;
                return (
                  <th key={c.key} onClick={() => onHeader(c.key)}
                    className={'text-[11px] font-semibold px-2 py-1 cursor-pointer select-none ' + (c.align === 'right' ? 'text-right' : 'text-left')}
                    style={{ color: active ? 'var(--sem-accent)' : 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)', whiteSpace: 'nowrap' }}>
                    {c.label}{active && <span className="text-[9px] ml-1">{sort.dir === 1 ? '▲' : '▼'}</span>}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {view.map((r, i) => (
              <tr key={i} onClick={() => onRowClick?.(r)}
                className={onRowClick ? 'cursor-pointer hover:bg-[var(--sem-surface-2)]' : ''}>
                {cols.map((c) => (
                  <td key={c.key} className={'text-[12px] px-2 py-1.5 ' + (c.align === 'right' ? 'text-right' : 'text-left')}
                    style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                    {c.render(r)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function prettyKey(k: string) {
  return k.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase()).replace('With', 'with').trim();
}
function shortRef(r: string) { const c = r.indexOf(':'); return c >= 0 ? r.slice(c + 1) : r; }
