import { useEffect, useMemo, useState } from 'react';
import { rpc, onDidChange } from './bridge';
import { RowSep, ToolRow } from './toolrow';
import type { ModelGraph, GraphRelationship } from './diagram';

// An audit grid over every relationship: endpoints, cardinality, cross-filter direction, active flag — with
// amber flags for the smells (bidirectional, inactive, many-to-many). Reuses the read-only model graph; live.
type SortKey = 'fromTable' | 'toTable';
const card = (c: string) => (c === 'Many' ? '*' : c === 'One' ? '1' : '?');

// `modeSwitch` is the Canvas / Relationships pair, owned by DiagramView. It leads this view's ONE tool row, so
// the two views of the Diagram tab share a single 40px strip rather than stacking a switcher row above a
// controls row (Kane's compact-top rule, 2026-09-14).
export function RelationshipsView({ modeSwitch }: { modeSwitch?: React.ReactNode } = {}) {
  const [graph, setGraph] = useState<ModelGraph | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [onlyIssues, setOnlyIssues] = useState(false);
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 }>({ key: 'fromTable', dir: 1 });

  async function load() {
    try { setGraph(await rpc<ModelGraph>('getModelGraph')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void load();
    let t: number | undefined;
    const off = onDidChange(() => { window.clearTimeout(t); t = window.setTimeout(() => void load(), 300); });
    return () => { off(); window.clearTimeout(t); };
  }, []);

  const issue = (r: GraphRelationship) => r.crossFilter === 'BothDirections' || !r.isActive || (r.fromCardinality === 'Many' && r.toCardinality === 'Many');

  const view = useMemo(() => {
    if (!graph) return [];
    const f = filter.trim().toLowerCase();
    let r = graph.relationships;
    if (f) r = r.filter((x) => (x.fromTable + ' ' + x.fromColumn + ' ' + x.toTable + ' ' + x.toColumn).toLowerCase().includes(f));
    if (onlyIssues) r = r.filter(issue);
    const { key, dir } = sort;
    return [...r].sort((a, b) => dir * (a[key] || '').localeCompare(b[key] || '', undefined, { sensitivity: 'base' }) || (a.toTable || '').localeCompare(b.toTable || ''));
  }, [graph, filter, onlyIssues, sort]);

  const stats = useMemo(() => graph && ({
    total: graph.relationships.length,
    bidi: graph.relationships.filter((r) => r.crossFilter === 'BothDirections').length,
    inactive: graph.relationships.filter((r) => !r.isActive).length,
    manyMany: graph.relationships.filter((r) => r.fromCardinality === 'Many' && r.toCardinality === 'Many').length,
  }), [graph]);

  if (err) return <div className="sem-page"><div className="xe-error" role="alert">{err}</div></div>;
  if (!graph) return <div className="sem-page sem-text-meta">Loading relationships…</div>;

  const Th = ({ k, label }: { k: SortKey; label: string }) => (
    <th onClick={() => setSort((s) => ({ key: k, dir: s.key === k && s.dir === 1 ? -1 : 1 }))}
      className="text-left font-semibold px-3 py-1 cursor-pointer select-none whitespace-nowrap"
      style={{ color: 'var(--sem-muted)', position: 'sticky', top: 0, background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)' }}>
      {label}{sort.key === k ? (sort.dir === 1 ? ' ▲' : ' ▼') : ''}
    </th>
  );
  const Plain = ({ label, w }: { label: string; w?: string }) => (
    <th className="text-left font-semibold px-3 py-1 whitespace-nowrap" style={{ width: w, color: 'var(--sem-muted)', position: 'sticky', top: 0, background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)' }}>{label}</th>
  );

  return (
    <div className="h-full flex flex-col">
      <ToolRow>
        {modeSwitch}
        {modeSwitch ? <RowSep /> : null}
        <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter the list…" aria-label="Filter the list"
          className="sem-toolrow-input" style={{ minWidth: 180 }} />
        <label className="flex items-center gap-2 cursor-pointer select-none" style={{ color: 'var(--sem-fg)' }}>
          <input type="checkbox" checked={onlyIssues} onChange={(e) => setOnlyIssues(e.target.checked)} />
          Problems only
        </label>
        {stats && (
          <div className="sem-toolrow-end flex items-center gap-4" style={{ color: 'var(--sem-muted)' }}>
            <Stat label="links" value={stats.total} />
            <Stat label="filter both ways" value={stats.bidi} warn={stats.bidi > 0} />
            <Stat label="switched off" value={stats.inactive} />
            <Stat label="many to many" value={stats.manyMany} warn={stats.manyMany > 0} />
          </div>
        )}
      </ToolRow>
      <div className="flex-1 min-h-0 xe-edge-box">
        <table className="w-full text-xs xe-edge-table" style={{ borderCollapse: 'collapse', color: 'var(--sem-fg)' }}>
          <thead>
            <tr>
              <Th k="fromTable" label="From (many)" />
              <Th k="toTable" label="To (one)" />
              <Plain label="Cardinality" w="12%" />
              <Plain label="Cross-filter" w="14%" />
              <Plain label="Active" w="10%" />
            </tr>
          </thead>
          <tbody>
            {view.map((r, i) => {
              const bidi = r.crossFilter === 'BothDirections';
              return (
                <tr key={r.name || i} style={{ borderBottom: '1px solid var(--sem-rule)' }}>
                  <td className="px-3 py-1 align-top"><Endpoint table={r.fromTable} col={r.fromColumn} /></td>
                  <td className="px-3 py-1 align-top"><Endpoint table={r.toTable} col={r.toColumn} /></td>
                  <td className="px-3 py-1 align-top tnum">
                    <span className={r.fromCardinality === 'Many' && r.toCardinality === 'Many' ? 'xe-dot xe-dot-warn' : undefined}>
                      {card(r.fromCardinality)} : {card(r.toCardinality)}
                    </span>
                  </td>
                  <td className={'px-3 py-1 align-top' + (bidi ? ' xe-dot xe-dot-warn' : '')} style={bidi ? undefined : { color: 'var(--sem-muted)' }}>
                    {bidi ? 'both ↔' : 'single →'}
                  </td>
                  <td className="px-3 py-1 align-top">
                    {r.isActive ? <span title="Active" aria-label="Active">✓</span> : <span className="xe-dot xe-dot-warn" title="Switched off">off</span>}
                  </td>
                </tr>
              );
            })}
            {view.length === 0 && (
              <tr><td colSpan={5} className="px-3 py-6 text-center" style={{ color: 'var(--sem-muted)' }}>No relationships match.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Endpoint({ table, col }: { table: string; col: string }) {
  return <span><span className="font-medium">{table}</span><span style={{ color: 'var(--sem-muted)' }}>[{col}]</span></span>;
}
function Stat({ label, value, warn }: { label: string; value: number; warn?: boolean }) {
  return <span className="flex items-baseline gap-1"><span className={'font-semibold tnum' + (warn ? ' xe-dot xe-dot-warn' : '')}>{value}</span>{label}</span>;
}
