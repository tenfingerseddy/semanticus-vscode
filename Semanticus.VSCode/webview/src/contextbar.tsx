import { useEffect, useState } from 'react';
import { useConnection, type ModelConnectionContext, type SessionInfo } from './connection';
import type { ConnectionStatus } from './wire';
import type { ModelRef } from './compare';
import type { HubView } from './connectionshub';

// The Studio footer answers the roles users otherwise conflate:
//   what am I EDITING · what answers TESTS/QUERIES · where will I PUBLISH?
// This matters because they genuinely can disagree: staged (unsaved) edits live in the open model, but DAX/DMV/preview
// run against the last DEPLOYED/published model — so a query can silently reflect a DIFFERENT model than the one you're
// editing. The bar DISCLOSES that; it never blocks. Identity segments open the shared Connections drawer; Review
// changes remains a separate seeded diff action. UI copy stays in human terms ("unsaved edits", "the model you're querying") — never engine
// vocabulary.
//
// Identity comes from the engine's connectionContext. The immediate sync warning still reads two live facts:
//   • `session` (SessionInfo) — the model you're EDITING (source / liveBound / liveEndpoint / liveDatabase / unsaved).
//   • `conn` (ConnectionStatus) — the engine your queries actually RUN against. connect/disconnect update THIS
//     synchronously; the session copy only catches up on the next model edit — so the querying/sync verdict must read
//     `conn`, never a stale session field, or the footer keeps saying Offline while the tabs are already live.

const norm = (x?: string) => (x || '').trim().toLowerCase().replace(/[\\/]+$/, '');
const basename = (p?: string) => { const t = (p || '').replace(/[\\/]+$/, ''); const i = Math.max(t.lastIndexOf('/'), t.lastIndexOf('\\')); return i >= 0 ? t.slice(i + 1) : t; };
// A readable end for a long XMLA endpoint: its final path segment (the workspace name), else the host. DECODED: this
// result is used as a NAME, and a workspace whose name has spaces in it is stored percent-encoded, so taking the
// segment verbatim printed an address where a name belongs (Kane, 2026-09-15 — the Connections hub had the same
// defect in its own helper). A malformed escape keeps the text as stored: a poor name, but the only hint there is.
// Top-level so the naming contract test can extract and RUN it rather than only match the source.
function shortEndpoint(ep?: string): string {
  const t = (ep || '').replace(/[\\/]+$/, ''); if (!t) return '';
  const raw = t.slice(t.lastIndexOf('/') + 1);
  let seg = raw;
  try { seg = decodeURIComponent(raw); } catch { seg = raw; }
  if (seg && !seg.includes(':') && !seg.includes('.')) return seg;
  try { return new URL(t.replace(/^powerbi:\/\//, 'https://')).host || seg || t; } catch { return seg || t; }
}

// Are we editing and querying provably the SAME live instance? Only claim it when we can prove it from the attached
// query engine's data source (a local file path can never equal a localhost query source, so this stays false there —
// correctly "can't prove same").
function sameInstance(s: SessionInfo, conn: ConnectionStatus | null): boolean {
  if (!conn?.connected) return false;
  const q = norm(conn.dataSource);
  return !!q && (q === norm(s.liveEndpoint) || q === norm(s.source));
}

// The load-bearing sync verdict. Derive ONLY what's provable — we never claim "in sync" (saved-to-disk is not deployed,
// and we can't prove equality without a diff; the bar offers that diff instead). "Connected?" comes from `conn` (the
// live query engine), not the session's lagging copy.
type SyncTone = 'neutral' | 'warn';
function syncVerdict(s: SessionInfo | null, conn: ConnectionStatus | null): { tone: SyncTone; label: string; detail: string } {
  if (!s || !conn?.connected) return { tone: 'neutral', label: 'Offline', detail: 'No live connection, so queries are unavailable.' };
  // A live connection + staged edits is the real hazard: the staged changes are NOT in the model you're querying — even
  // when you're querying the very instance you opened, because unsaved changes haven't been pushed to it yet.
  if (s.hasUnsavedChanges) return { tone: 'warn', label: 'Unsaved edits not queried', detail: "Unsaved edits are not in the model you're querying." };
  if (sameInstance(s, conn)) return { tone: 'neutral', label: 'Same instance', detail: 'Editing and querying the same instance.' };
  // An attached LOCAL engine (Power BI Desktop) is not a published model — the "reflects the last deploy" line is
  // only true of an XMLA endpoint. Same verdict logic as the native status-bar chip (extension.ts renderSyncChip);
  // the two must not disagree.
  if (conn.kind === 'local') return { tone: 'neutral', label: 'Local model', detail: 'Tests and queries use an attached local running model.' };
  return { tone: 'neutral', label: 'Published model', detail: 'Results come from the last published version of the model.' };
}

// Build the Compare refs a click seeds. left = what you're EDITING (the working copy, the only ref that carries staged
// edits); right prefers the explicitly selected PUBLISH destination, then falls back to what you're querying. right can
// be NULL: an attached XMLA engine names its endpoint
// but ConnectionStatus can't name the dataset, and seeding a workspace ref with an empty database makes the differ fall
// back to the endpoint's FIRST dataset — i.e. compare against a DIFFERENT model than the one attached. In that case we
// refuse to seed the target and hand back a `note` so the UI can ask the user to name the dataset, rather than silently
// diff an arbitrary one. Compare cannot load a local Power BI Desktop instance, so for a local session we diff against
// the on-disk save (which still reveals unsaved edits) rather than seed a ref that would fail to load.
export function compareSeedFromSession(s: SessionInfo, conn: ConnectionStatus | null, context?: ModelConnectionContext | null): { left: ModelRef; right: ModelRef | null; note?: string } {
  const editingLabel = s.liveBound && s.liveDatabase ? s.liveDatabase : (s.source ? basename(s.source) : (s.modelName || 'working copy'));
  const left: ModelRef = { kind: 'session', label: editingLabel };
  const publishing = context?.publishing;
  if (publishing?.available && publishing.endpoint && publishing.database) {
    return { left, right: { kind: 'workspace', endpoint: publishing.endpoint, database: publishing.database,
      authMode: publishing.authMode, label: publishing.modelName || publishing.database || 'publish destination' } };
  }
  // A live-BOUND session names BOTH its endpoint and its dataset (the open origin resolved the catalog) — safe to target.
  if (s.liveBound && s.liveEndpoint) {
    return { left, right: { kind: 'workspace', endpoint: s.liveEndpoint, database: s.liveDatabase, label: s.liveDatabase || 'published model' } };
  }
  // An ATTACHED XMLA engine on a file-backed model: we know the endpoint (conn.dataSource) but NOT the dataset. Refuse
  // to seed a workspace ref with an unknown database (see above) — return a note so the UI asks for the dataset.
  if (conn?.connected && conn.kind === 'xmla' && conn.dataSource) {
    return { left, right: null, note: `No publish destination is linked. Tests and queries use ${shortEndpoint(conn.dataSource)}, but the model there cannot be named safely. Choose Publish to in Connections before reviewing changes.` };
  }
  // A local Power BI Desktop engine can't be loaded by Compare; diff against the on-disk save, else the last commit.
  if (s.source) return { left, right: { kind: 'file', path: s.source, label: 'saved on disk' } };
  return { left, right: { kind: 'gitref', gitRef: 'HEAD', label: 'last commit' } };
}

// A full-width FOOTER pinned to the bottom of the Studio panel. Each identity is its own keyboard-reachable drawer
// shortcut; Review changes is deliberately separate so changing a connection never reads like publishing. Only the
// sync chip is tinted. Names wrap when room is tight; secondary hints yield before a model identity disappears.
// THREE roles, not four: the Reference slot left this bar on 2026-09-14. It is not a role of the session the way
// editing, testing and publishing are, it read as a fourth thing to configure before you could start, and it was
// the widest of the four while being the one almost nobody set. The engine keeps its reference slot, and the way
// in becomes an action ("Copy from another model...") rather than a permanent seat in the footer.
// State comes from the shared connection context (the single source of truth), NOT an App prop — an App-held copy went
// stale on attach/disconnect (those don't fire model/didChange) and left the footer disagreeing with the live tabs.
// Under 900px the three roles, the pill and Review changes stop fitting on one line. Tests is the slot that folds:
// it is the one people set once and read least, and the pill beside it is already about querying, so the fact has
// a natural home in that pill's tooltip. The breakpoint is written in TWO places on purpose and they must stay
// equal — the stylesheet hides the button, this decides what the tooltip then has to say.
const FOLD_TESTS_BELOW = '(max-width: 899px)';
function useTestsFolded(): boolean {
  const [folded, setFolded] = useState(() => (typeof window !== 'undefined' && window.matchMedia ? window.matchMedia(FOLD_TESTS_BELOW).matches : false));
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const query = window.matchMedia(FOLD_TESTS_BELOW);
    const apply = () => setFolded(query.matches);
    apply();
    query.addEventListener('change', apply);
    return () => query.removeEventListener('change', apply);
  }, []);
  return folded;
}

export function ContextBar({ onConnections, onReview }: { onConnections: (section?: HubView) => void; onReview: () => void }) {
  const { session, conn, context } = useConnection();
  const testsFolded = useTestsFolded();
  if (!session?.sessionId) return null;
  const s = session;
  const editing = context?.editing;
  const querying = context?.querying;
  const publishing = context?.publishing;
  const connected = !!querying?.available || !!conn?.connected;
  const editMain = editing?.modelName || s.modelName || 'Model';
  const editSub = editing?.sourceControlled ? 'local files · source controlled'
    : editing?.live ? 'live model' : (editing?.source ? basename(editing.source) : 'local files');
  const queryMain = querying?.available ? (querying.modelName || querying.database || shortEndpoint(querying.source) || 'Connected model') : 'Not connected';
  const querySub = querying?.available ? (querying.kind === 'localDesktop' || querying.kind === 'local' ? 'local running model' : 'published model') : 'tests and queries unavailable';
  const publishMain = publishing?.available ? (publishing.modelName || publishing.database || 'Published model') : 'Not linked';
  const publishSub = publishing?.available ? 'XMLA destination' : 'choose before publishing';
  const v = syncVerdict(s, conn);
  const warn = v.tone === 'warn';
  // Folded, the pill carries the Tests fact so nothing about the session becomes unreadable at a narrow width.
  const pillTitle = testsFolded ? `${v.detail} Tests: ${queryMain} (${querySub}).` : v.detail;
  const kicker = { color: 'var(--sem-muted)', fontSize: 9, letterSpacing: '0.06em' } as const;
  const segment = 'connection-role flex items-baseline gap-1.5 min-w-0 px-1.5 py-1 rounded outline-none focus-visible:outline focus-visible:outline-2 focus-visible:outline-[color:var(--sem-accent)] hover:bg-[color:var(--sem-surface-2)]';

  return (
    <div className="studio-contextbar flex items-center gap-1.5 w-full px-2.5 text-left shrink-0" style={{ minHeight: 34, background: 'var(--sem-surface)', color: 'var(--sem-fg)', borderTop: '1px solid var(--sem-border)' }}>
      <button type="button" data-testid="connections-editing" onClick={() => onConnections('open')} title="Choose what to edit" className={segment}>
        <span className="uppercase font-semibold shrink-0" style={kicker}>Editing</span>
        <span className="connection-value text-[12px] font-medium truncate min-w-0" title={editing?.source}>{editMain}</span>
        {s.hasUnsavedChanges && <span className="text-[10px] shrink-0" style={{ color: 'var(--sem-warn)' }}>● unsaved</span>}
        <span className="connection-hint text-[10px] truncate min-w-0" style={{ color: 'var(--sem-muted)' }}>{editSub}</span>
      </button>
      <span className="connection-separator text-[11px] shrink-0" style={{ color: 'var(--sem-muted)' }}>›</span>
      <button type="button" data-testid="connections-querying" onClick={() => onConnections('setup')} title="Choose the model used for tests and queries" className={segment}>
        <span className="uppercase font-semibold shrink-0" style={kicker}>Tests</span>
        <span className="connection-value text-[12px] font-medium truncate min-w-0" style={{ color: connected ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{queryMain}</span>
        <span className="connection-hint text-[10px] truncate min-w-0" style={{ color: 'var(--sem-muted)' }}>{querySub}</span>
      </button>
      <span className="connection-separator text-[11px] shrink-0" style={{ color: 'var(--sem-muted)' }}>›</span>
      <button type="button" data-testid="connections-publishing" onClick={() => onConnections('setup')} title="Choose the live model that will receive published changes" className={segment}>
        <span className="uppercase font-semibold shrink-0" style={kicker}>Publish to</span>
        <span className="connection-value text-[12px] font-medium truncate min-w-0" style={{ color: publishing?.available ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{publishMain}</span>
        <span className="connection-hint text-[10px] truncate min-w-0" style={{ color: 'var(--sem-muted)' }}>{publishSub}</span>
      </button>
      <span className={`connection-sync${warn ? ' connection-sync-warn' : ''} flex items-center gap-1.5 px-2 py-0.5 rounded-md text-[11px] font-medium shrink-0 max-w-[46%]`}
        style={warn
          ? { background: 'color-mix(in srgb, var(--sem-warn) 18%, transparent)', color: 'var(--sem-warn)', border: '1px solid color-mix(in srgb, var(--sem-warn) 45%, transparent)' }
          : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
        <span className="shrink-0">{warn ? '!' : '●'}</span>
        <span className="truncate" title={pillTitle}>{warn ? v.detail : v.label}</span>
      </span>
      <button type="button" onClick={onReview} title="Review differences before updating a test model or publishing" className="connection-review ml-auto text-[11px] font-semibold shrink-0 rounded px-2 py-1" style={{ color: 'var(--sem-accent)' }}>Review changes →</button>
    </div>
  );
}

// The in-tab staleness chip for the query surfaces (DAX Lab / Data preview / Statistics). Shown ONLY when a live query
// connection is attached AND there are unsaved edits — the exact condition under which results omit your staged work.
// One line, dismissible, never a modal. Reads the shared connection context so it lights up live as edits land.
export function QueryStalenessChip({ className = '' }: { className?: string }) {
  const { session, conn } = useConnection();
  // The one condition under which query results omit your staged work: a live query connection AND unsaved edits. Read
  // "connected?" from `conn` (the query engine), not the session copy, so an attach/disconnect is reflected at once.
  const divergent = !!(conn?.connected && session?.hasUnsavedChanges);
  const sid = session?.sessionId;
  const [dismissed, setDismissed] = useState(false);
  // Re-arm on every FRESH divergence — otherwise one dismissal silences the warning for the whole webview session,
  // including after the user saves and then makes NEW unsaved edits (the exact case this chip exists to catch). Keyed
  // on (sessionId, divergent) so a false→true transition (new edits after a save) OR a model swap un-dismisses it.
  useEffect(() => { if (divergent) setDismissed(false); }, [sid, divergent]);
  // Render nothing at all (no empty padded wrapper) unless the divergence holds and it hasn't been dismissed. The
  // wrapper carries the caller's spacing.
  if (dismissed || !divergent) return null;
  return (
    <div className={className}>
      <div className="flex items-center gap-2 text-[11px] px-2.5 py-1.5 rounded-md"
        style={{ background: 'color-mix(in srgb, var(--sem-warn) 14%, transparent)', color: 'var(--sem-warn)', border: '1px solid color-mix(in srgb, var(--sem-warn) 40%, transparent)' }}>
        <span className="shrink-0">!</span>
        <span>Results come from the connected {conn?.kind === 'local' ? 'local test model' : 'published model'}. Your unsaved edits are not included.</span>
        <button type="button" aria-label="Dismiss this notice" onClick={() => setDismissed(true)} className="ml-auto shrink-0" style={{ color: 'var(--sem-warn)', opacity: 0.8 }} title="Dismiss">✕</button>
      </div>
    </div>
  );
}
