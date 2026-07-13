import { useEffect, useRef, useState } from 'react';
import { rpc } from './bridge';
import { ResultGrid } from './grid';
import type { ModelGraph, GraphTable } from './diagram';
import { useConnection, ConnectBar } from './connection';
import { QueryStalenessChip } from './contextbar';
import type { ResultSet } from './wire';
import { useClaudeReflection, ClaudeRanBanner, type ActivityEvent } from './activity';

// `target` is a table handed in from elsewhere (a Model-tree "Preview data" right-click) — its nonce changes on
// every navigation so re-selecting the same table re-fires the preview.
export function DataPreviewView({ target }: { target?: { table: string; nonce: number } | null }) {
  const { conn } = useConnection();
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

  // The table list is metadata (getModelGraph) — available even when no live engine is connected.
  useEffect(() => {
    rpc<ModelGraph>('getModelGraph').then((g) => setTables(g.tables)).catch(() => undefined);
  }, []);

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
      const r = await rpc<ResultSet>('previewTable', name, n);
      if (seq !== previewSeq.current) return;   // a newer table selection superseded this query — drop its result
      if (r.error) { setErr(r.error); setRes(null); } else setRes(r);
    }
    catch (e) { if (seq === previewSeq.current) setErr(String((e as Error).message ?? e)); }
    finally { if (seq === previewSeq.current) setBusy(false); }
  }

  return (
    <div className="h-full flex">
      {/* table list */}
      <div className="w-56 shrink-0 overflow-auto border-r" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="px-3 py-2 text-[11px] uppercase tracking-wide font-semibold sticky top-0" style={{ color: 'var(--sem-muted)', background: 'var(--sem-bg)' }}>Tables ({tables.length})</div>
        {tables.map((t) => (
          <button key={t.ref} onClick={() => conn?.connected && preview(t.name)} disabled={!conn?.connected || busy}
            className="w-full text-left px-3 py-1.5 text-[12px] flex items-center gap-2 disabled:opacity-50"
            style={{ background: selected === t.name ? 'var(--sem-accent-soft)' : 'transparent', color: 'var(--sem-fg)' }}>
            <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: t.isDateTable ? 'var(--sem-warn)' : t.isCalculated ? 'var(--sem-good)' : 'var(--sem-accent)', opacity: t.isHidden ? 0.4 : 1 }} />
            <span className="truncate" style={{ opacity: t.isHidden ? 0.55 : 1 }}>{t.name}</span>
            <span className="ml-auto text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{t.columns}</span>
          </button>
        ))}
      </div>

      {/* preview */}
      <div className="flex-1 min-w-0 flex flex-col p-4 gap-3">
        <div className="flex items-center gap-3">
          <div className="text-[13px] font-semibold">{selected ? `Preview · ${selected}` : 'Data Preview'}</div>
          <div className="ml-auto flex items-center gap-2">
            {conn?.connected && (
              <select value={topN} onChange={(e) => { const n = Number(e.target.value); setTopN(n); if (selected) preview(selected, n); }}
                className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
                {[200, 1000, 5000, 20000].map((n) => <option key={n} value={n}>{n.toLocaleString()} rows</option>)}
              </select>
            )}
          </div>
        </div>
        <ConnectBar hint="Row preview runs a live top-N query (the table list is available offline)." />
        {/* The preview runs against the published model — flag when unsaved edits mean the rows omit your staged work. */}
        <QueryStalenessChip />
        {err && <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{err}</div>}
        {claudeEvent && <ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} />}
        {res ? (
          <>
            <div className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{res.rowCount} rows{res.truncated ? ' (truncated)' : ''} · {res.columns.length} cols · {res.elapsedMs} ms</div>
            <div className="flex-1 min-h-0"><ResultGrid columns={res.columns} rows={res.rows} height="100%" /></div>
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center text-[12px]" style={{ color: 'var(--sem-muted)' }}>
            {conn?.connected ? 'Pick a table to preview its rows.' : 'Connect, then pick a table.'}
          </div>
        )}
      </div>
    </div>
  );
}
