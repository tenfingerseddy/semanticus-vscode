import { useCallback, useEffect, useRef, useState } from 'react';
import { rpc, onDidChange, selectInProperties, focusSelectInProperties } from './bridge';
import { RevealBtn, rowKeyProps } from './objectactions';
import { ResultGrid } from './grid';
import type { ModelGraph, GraphTable } from './diagram';
import { useConnection, ConnectBar } from './connection';
import { QueryStalenessChip } from './contextbar';
import type { ResultSet } from './wire';
import { useClaudeReflection, ClaudeRanBanner, type ActivityEvent } from './activity';
import { isSignInError, SIGN_IN_AGAIN } from './authcopy';

// A table preview names one table, so every header repeating "Sales[...]" is noise in DAX's notation.
// Show the field name; keep the full reference in the header's tooltip.
const fieldName = (raw: string) => { const m = /^.*\[(.+)\]$/.exec(raw.trim()); return m ? m[1] : raw; };

// `target` is a table handed in from elsewhere (a Model-tree "Preview data" right-click) — its nonce changes on
// every navigation so re-selecting the same table re-fires the preview.
export function DataPreviewView({ target }: { target?: { table: string; nonce: number } | null }) {
  const { conn, session, connectXmla, busy: connBusy } = useConnection();
  const [tables, setTables] = useState<GraphTable[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [topN, setTopN] = useState(200);
  const [res, setRes] = useState<ResultSet | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [claudeEvent, setClaudeEvent] = useState<ActivityEvent | null>(null);

  // Reflect the user's Claude previewing a table on the SAME session, live (attributed).
  useClaudeReflection('preview_table', (e) => {
    if (e.result) { setRes(e.result as ResultSet); if (e.target) setSelected(e.target); setErr(e.error ?? null); setClaudeEvent(e); }
  });

  // The table list is metadata (getModelGraph) — available even when no live engine is connected. Reload it on
  // every model/didChange: a rename/add/drop after mount would otherwise leave a stale row whose ref is obsolete,
  // so activating it feeds the selection bus (and the preview query) a name the model no longer has. Versioned so
  // a slower earlier response can't restore an older list over a newer one; a failed read drops to empty (fail
  // closed) rather than keep stale rows carrying obsolete refs.
  const tablesSeq = useRef(0);
  const loadTables = useCallback(() => {
    const seq = ++tablesSeq.current;
    setTables([]);   // fail closed: drop the old rows BEFORE the refetch so a renamed/dropped table's obsolete-ref row can't be activated mid-reload
    rpc<ModelGraph>('getModelGraph')
      .then((g) => { if (seq === tablesSeq.current) setTables(g.tables); })
      .catch(() => { if (seq === tablesSeq.current) setTables([]); });
  }, []);
  useEffect(() => {
    loadTables();
    const off = onDidChange(() => loadTables());
    return () => off();
  }, [loadTables]);

  // When a live engine connects, auto-preview the first (largest/first-listed) table so the grid shows data
  // immediately instead of an empty "pick a table" prompt. Fires once per connect; pick any other table to switch.
  useEffect(() => {
    if (conn?.connected && !selected && tables.length > 0) void preview(tables[0].name);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conn?.connected, tables.length]);

  // Navigated in from the tree: select the target table and preview it (once connected — the row preview is a live
  // query). Keyed on the nonce so a repeat "Preview data" on the same table re-runs; reruns on connect so a target
  // chosen offline previews as soon as a live engine attaches.
  useEffect(() => {
    if (!target?.table) return;
    setSelected(target.table);
    if (conn?.connected) void preview(target.table);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target?.nonce, conn?.connected]);

  // Latest-wins guard: clicking table A then B quickly must not let A's slower previewTable overwrite B's rows.
  const previewSeq = useRef(0);
  async function preview(name: string, n = topN) {
    const seq = ++previewSeq.current;
    setSelected(name); setBusy(true); setErr(null); setClaudeEvent(null);
    try {
      // Ask for one row more than is shown: "More rows exist" is said only when that extra row came back, never for a table of exactly n rows.
      const r = await rpc<ResultSet>('previewTable', name, n + 1);
      if (seq !== previewSeq.current) return;   // a newer table selection superseded this query — drop its result
      if (r.error) { setErr(r.error); setRes(null); }
      else setRes(r.rows.length > n ? { ...r, rows: r.rows.slice(0, n), rowCount: n, truncated: true } : r);
    }
    catch (e) { if (seq === previewSeq.current) setErr(String((e as Error).message ?? e)); }
    finally { if (seq === previewSeq.current) setBusy(false); }
  }

  return (
    // On the page grid (One Visual Language): the table list is a card two columns wide, the preview takes the
    // other ten, and the result grid is the preview's card, so both edges sit on the columns.
    <div className="sem-page xe-fill">
      {/* table list */}
      <div className="sem-card sem-card-flush sem-span-2 overflow-auto">
        <div className="sem-card-head sticky top-0" style={{ background: 'var(--sem-ground)' }}>
          <span className="sem-card-title">Tables</span><span className="sem-text-meta sem-num">{tables.length}</span>
        </div>
        {tables.map((t) => {
          // Selecting a table always feeds the selection bus (Properties follows even offline); the live row
          // preview still needs a connection. The keyboard control (rowKeyProps = role=button) sits on the NAME
          // span — never on the row div, whose nested Reveal button would then live inside a role=button (wrong
          // ARIA + Enter/Space on Reveal bubbling into the row action). The row keeps the mouse onClick.
          const activate = () => { selectInProperties(t.ref); if (conn?.connected && !busy) void preview(t.name); };
          return (
          <div key={t.ref} data-object-ref={t.ref} onClick={activate} aria-current={selected === t.name ? 'true' : undefined}
            className="group sem-list-row xe-pick-row cursor-pointer"
            style={{ color: conn?.connected ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>
            {/* Instrument: a table's kind is a quiet word after its name, not a coloured dot. */}
            <span className="truncate flex-1" style={{ color: t.isHidden ? 'var(--sem-muted)' : undefined }} {...rowKeyProps(activate, () => focusSelectInProperties(t.ref))}>
              {t.name}{(t.isDateTable || t.isCalculated) && <span className="sem-text-meta"> {t.isDateTable ? 'date' : 'calculated'}</span>}
            </span>
            <span className="sem-text-meta sem-num group-hover:hidden group-focus-within:hidden">{t.columns}</span>
            <RevealBtn objRef={t.ref} className="hidden group-hover:inline-block group-focus-within:inline-block" />
          </div>
          );
        })}
      </div>

      {/* preview */}
      <div className="sem-span-10 min-h-0 flex flex-col gap-3">
        <div className="flex items-center gap-3">
          <div className="sem-card-title">{selected ?? 'Data'}</div>
          <div className="ml-auto flex items-center gap-2">
            {conn?.connected && (
              <select value={topN} onChange={(e) => { const n = Number(e.target.value); setTopN(n); if (selected) preview(selected, n); }}
                aria-label="How many rows" className="sem-field">
                {[200, 1000, 5000, 20000].map((n) => <option key={n} value={n}>{n.toLocaleString()} rows</option>)}
              </select>
            )}
          </div>
        </div>
        <ConnectBar hint="Connect a model that holds data to see rows." />
        {/* The preview runs against the published model — flag when unsaved edits mean the rows omit your staged work. */}
        <QueryStalenessChip />
        {err && (
          <div className="sem-stack">
            <div className="xe-error" role="alert">{err}</div>
            {isSignInError(err) && session?.liveEndpoint && (
              <button type="button" data-testid="signin-again" disabled={connBusy} className="sem-btn sem-btn-sm sem-btn-primary self-start"
                onClick={() => {
                  void connectXmla(session.liveEndpoint!, session.liveDatabase ?? '', 'interactive', session.currentTenant ?? null)
                    .then((r) => { if (r.ok && selected) void preview(selected); else if (r.message) setErr(r.message); });
                }}>{SIGN_IN_AGAIN}</button>
            )}
          </div>
        )}
        {claudeEvent && <ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} />}
        {res ? (
          <>
            {/* One count line: the grid's own "N rows" beside its filter, plus "More rows exist." when cut at the top-N. */}
            <div className="flex-1 min-h-0"><ResultGrid key={selected ?? ''} columns={res.columns.map((c) => ({ ...c, name: fieldName(c.name) }))} rows={res.rows} height="100%" truncated={res.truncated} /></div>
          </>
        ) : (
          // Offline, the connect line above already says it; connected, say what to do next.
          conn?.connected && <div className="flex-1 flex items-center justify-center sem-text-meta">Pick a table to preview its rows.</div>
        )}
      </div>
    </div>
  );
}
