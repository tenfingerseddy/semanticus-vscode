import { useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { rpc, onDidChange } from './bridge';

// ===================================================================================================
// <ObjectBrowser> — Studio backbone B. ONE virtualized, filterable, group-by browser over the model's
// objects (tables · measures · columns · hierarchies), reused by the field-parameter picker, the RLS
// column picker, and (later) the perspective axis. Presentation-only: the host owns what a pick/drop
// means (onPick / onPickMany), so the same browser serves every builder. It is also the shared in-webview
// DRAG SOURCE — each row emits its DAX ref as text/plain (the exact contract DaxEditor's drop handler and
// the DAX-Lab wells already consume), so a drag lands in any <DaxField> for free (native tree→webview drag
// is blocked by VS Code; this is the honoured alternative). Every drag has a non-drag equivalent (click /
// checkbox), so drag is never the only path — required for the analyst tier + accessibility.
// ===================================================================================================

export type ObjectKind = 'measure' | 'column' | 'hierarchy' | 'table';
export interface BrowserNode {
  ref: string;            // engine object ref (measure:… / column:… / hierarchy:… / table:…) — used for picks + dedupe
  name: string;
  kind: ObjectKind;
  table: string;
  folder?: string;        // display folder path ('\'-separated), '' at root
  dataType?: string;      // columns only
  isHidden?: boolean;
  isKey?: boolean;        // key columns only
}

// wire shape from get_model_objects (camelCase; mirrors Semanticus.Engine/Protocol.cs ModelObjects)
interface WireTable { ref: string; name: string; isHidden: boolean; isCalculationGroup: boolean }
interface WireMeasure { ref: string; name: string; table: string; displayFolder?: string; isHidden?: boolean }
interface WireColumn { ref: string; name: string; table: string; displayFolder?: string; dataType?: string; isKey?: boolean; isHidden?: boolean }
interface WireHierarchy { ref: string; name: string; table: string; displayFolder?: string; isHidden?: boolean; levels?: string[] }
interface WireModelObjects { tables: WireTable[]; measures: WireMeasure[]; columns: WireColumn[]; hierarchies: WireHierarchy[] }

// The DAX reference for a node (what a drag emits / a DaxField insert receives). Hierarchies have no DAX form → null.
export function daxRefOf(n: BrowserNode): string | null {
  if (n.kind === 'measure') return `[${n.name}]`;
  if (n.kind === 'column') return `'${n.table}'[${n.name}]`;
  if (n.kind === 'table') return `'${n.name}'`;
  return null;
}

// Shared model read for the browser: one get_model_objects round-trip → a flat BrowserNode[]. Refreshes on a
// FOREIGN model change (the browser is read-only, so it has no echo of its own to ignore — any didChange is worth a
// reload, debounced). Returns tables in engine order too (for stable group headers).
export function useBrowserData(): { nodes: BrowserNode[]; tables: WireTable[]; err: string | null; reload: () => void } {
  const [nodes, setNodes] = useState<BrowserNode[]>([]);
  const [tables, setTables] = useState<WireTable[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const reload = useMemo(() => async () => {
    try {
      const o = await rpc<WireModelObjects>('getModelObjects');
      const out: BrowserNode[] = [];
      for (const m of o.measures ?? []) out.push({ ref: m.ref, name: m.name, kind: 'measure', table: m.table, folder: m.displayFolder, isHidden: m.isHidden });
      for (const c of o.columns ?? []) out.push({ ref: c.ref, name: c.name, kind: 'column', table: c.table, folder: c.displayFolder, dataType: c.dataType, isKey: c.isKey, isHidden: c.isHidden });
      for (const h of o.hierarchies ?? []) out.push({ ref: h.ref, name: h.name, kind: 'hierarchy', table: h.table, folder: h.displayFolder, isHidden: h.isHidden });
      setNodes(out); setTables(o.tables ?? []); setErr(null);
    } catch (e) { setErr(String((e as Error).message ?? e)); }
  }, []);
  useEffect(() => {
    void reload();
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void reload(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, [reload]);
  return { nodes, tables, err, reload };
}

type GroupBy = 'table' | 'folder' | 'flat';
const KIND_GLYPH: Record<ObjectKind, { g: string; color: string }> = {
  measure: { g: 'ƒ', color: 'var(--sem-accent)' },
  column: { g: '▦', color: '#9cdcfe' },
  hierarchy: { g: '⛼', color: '#4ec9b0' },
  table: { g: '▤', color: 'var(--sem-muted)' },
};
const ROW_H = 28;

// A flattened positional row: either a (collapsible) group header or a node — one array feeds the virtualizer.
type Row = { t: 'group'; key: string; label: string; count: number; open: boolean } | { t: 'node'; node: BrowserNode; groupKey: string };

export function ObjectBrowser({ kinds = ['measure', 'column'], multiSelect = false, dragEnabled = true, initialGroupBy = 'table', scopeTable, pickedRefs, height = 360, onPick, onPickMany, emptyHint }: {
  kinds?: ObjectKind[];
  multiSelect?: boolean;
  dragEnabled?: boolean;
  initialGroupBy?: GroupBy;
  scopeTable?: string;                     // when set, that table's objects sort first (RLS / table-scoped pickers)
  pickedRefs?: Set<string>;                // refs already chosen upstream — dimmed + excluded from "add all"
  height?: number;
  onPick?: (node: BrowserNode) => void;    // primary click (analyst default)
  onPickMany?: (nodes: BrowserNode[]) => void;   // multi-select action bar
  emptyHint?: string;
}) {
  const { nodes, err } = useBrowserData();
  const [query, setQuery] = useState('');
  const [groupBy, setGroupBy] = useState<GroupBy>(initialGroupBy);
  const [showHidden, setShowHidden] = useState(false);
  const [kindOn, setKindOn] = useState<Set<ObjectKind>>(new Set(kinds));
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [sel, setSel] = useState<Set<string>>(new Set());
  const lastClicked = useRef<number>(-1);
  const parentRef = useRef<HTMLDivElement>(null);

  // available kind chips = the intersection of the requested kinds and what the model actually has
  const availableKinds = useMemo(() => kinds.filter((k) => nodes.some((n) => n.kind === k)), [kinds, nodes]);

  // filter: requested kinds ∩ enabled chips, hidden toggle, and a fuzzy match on name/table/folder
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return nodes.filter((n) => kinds.includes(n.kind) && kindOn.has(n.kind)
      && (showHidden || !n.isHidden)
      && (!q || n.name.toLowerCase().includes(q) || n.table.toLowerCase().includes(q) || (n.folder ?? '').toLowerCase().includes(q)));
  }, [nodes, kinds, kindOn, showHidden, query]);

  // group + sort, then flatten to positional rows (respecting collapse). scopeTable floats to the top.
  const rows = useMemo<Row[]>(() => {
    const keyOf = (n: BrowserNode) => groupBy === 'flat' ? '' : groupBy === 'folder' ? (n.folder || '(no folder)') : n.table;
    const groups = new Map<string, BrowserNode[]>();
    for (const n of filtered) { const k = keyOf(n); (groups.get(k) ?? groups.set(k, []).get(k)!).push(n); }
    const order = [...groups.keys()].sort((a, b) => {
      if (scopeTable) { if (a === scopeTable) return -1; if (b === scopeTable) return 1; }
      return a.localeCompare(b);
    });
    const out: Row[] = [];
    if (groupBy === 'flat') {
      for (const n of [...filtered].sort((a, b) => a.name.localeCompare(b.name))) out.push({ t: 'node', node: n, groupKey: '' });
      return out;
    }
    for (const k of order) {
      const items = groups.get(k)!.sort((a, b) => a.name.localeCompare(b.name));
      const open = !collapsed.has(k);
      out.push({ t: 'group', key: k, label: k, count: items.length, open });
      if (open) for (const n of items) out.push({ t: 'node', node: n, groupKey: k });
    }
    return out;
  }, [filtered, groupBy, collapsed, scopeTable]);

  const nodeRows = useMemo(() => rows.filter((r): r is Extract<Row, { t: 'node' }> => r.t === 'node'), [rows]);

  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => parentRef.current, estimateSize: () => ROW_H, overscan: 14 });

  const toggleGroup = (k: string) => setCollapsed((s) => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });
  const toggleKind = (k: ObjectKind) => setKindOn((s) => { const n = new Set(s); n.has(k) ? n.delete(k) : n.add(k); return n; });

  // multi-select on the CHECKBOX: shift extends a range within the visible node rows; plain click toggles one.
  const onCheck = (node: BrowserNode, e: React.MouseEvent) => {
    e.stopPropagation();
    const idx = nodeRows.findIndex((r) => r.node.ref === node.ref);
    setSel((s) => {
      const n = new Set(s);
      if (e.shiftKey && lastClicked.current >= 0) {
        const [lo, hi] = [Math.min(lastClicked.current, idx), Math.max(lastClicked.current, idx)];
        for (let i = lo; i <= hi; i++) n.add(nodeRows[i].node.ref);
      } else { n.has(node.ref) ? n.delete(node.ref) : n.add(node.ref); }
      return n;
    });
    lastClicked.current = idx;
  };
  const selectedNodes = useMemo(() => nodeRows.map((r) => r.node).filter((n) => sel.has(n.ref) && !pickedRefs?.has(n.ref)), [nodeRows, sel, pickedRefs]);

  const drag = (node: BrowserNode) => (e: React.DragEvent) => {
    // If part of a multi-selection, drag the whole bag (newline-joined DAX refs); else just this node.
    const bag = sel.has(node.ref) && selectedNodes.length > 1 ? selectedNodes : [node];
    const text = bag.map(daxRefOf).filter((x): x is string => !!x).join('\n');
    if (!text) { e.preventDefault(); return; }   // e.g. a lone hierarchy has no DAX form
    e.dataTransfer.setData('text/plain', text);
    e.dataTransfer.effectAllowed = 'copy';
  };

  if (err) return <div className="text-[12px]" style={{ color: 'var(--sem-bad)' }}>{err}</div>;

  return (
    <div className="flex flex-col min-h-0" style={{ height }}>
      {/* toolbar: search · group-by · kind chips · hidden */}
      <div className="flex items-center gap-1.5 flex-wrap pb-1.5 shrink-0">
        <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search fields… ( / )" spellCheck={false} data-ob-search
          className="text-[12px] px-2 py-1 rounded-md outline-none flex-1 min-w-[120px]" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        <div className="flex items-center gap-0.5">
          {(['table', 'folder', 'flat'] as GroupBy[]).map((g) => (
            <button key={g} onClick={() => setGroupBy(g)} title={`Group by ${g}`}
              className="text-[10px] px-1.5 py-0.5 rounded-md font-medium capitalize"
              style={groupBy === g ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>{g}</button>
          ))}
        </div>
      </div>
      <div className="flex items-center gap-1.5 flex-wrap pb-1.5 shrink-0">
        {availableKinds.map((k) => (
          <Chip key={k} active={kindOn.has(k)} onClick={() => toggleKind(k)}>
            <span style={{ color: KIND_GLYPH[k].color }}>{KIND_GLYPH[k].g}</span> {k === 'hierarchy' ? 'hierarchies' : k + 's'}
          </Chip>
        ))}
        {nodes.some((n) => n.isHidden) && <Chip active={showHidden} onClick={() => setShowHidden((v) => !v)}>hidden</Chip>}
        <span className="text-[11px] tnum ml-auto" style={{ color: 'var(--sem-muted)' }}>
          {filtered.length === nodes.length ? `${nodes.length} object${nodes.length === 1 ? '' : 's'}` : `${filtered.length} of ${nodes.length}`}
        </span>
      </div>

      {/* virtualized rows */}
      <div ref={parentRef} className="overflow-auto rounded-lg flex-1 min-h-0" style={{ border: '1px solid var(--sem-border)' }}>
        {rows.length === 0 ? (
          <div className="text-[12px] p-3" style={{ color: 'var(--sem-muted)' }}>{query ? 'No matching objects.' : (emptyHint ?? 'No objects.')}</div>
        ) : (
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((vr) => {
              const row = rows[vr.index];
              const common = { position: 'absolute' as const, top: vr.start, left: 0, width: '100%', height: ROW_H };
              if (row.t === 'group') {
                return (
                  <button key={vr.key} onClick={() => toggleGroup(row.key)} style={common}
                    className="flex items-center gap-1.5 px-2 text-left" >
                    <span className="inline-block w-3 text-[9px]" style={{ transform: row.open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
                    <span className="text-[12px] font-semibold truncate">{row.label}</span>
                    <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>({row.count})</span>
                  </button>
                );
              }
              const n = row.node;
              const picked = pickedRefs?.has(n.ref);
              const checked = sel.has(n.ref);
              return (
                <div key={vr.key} style={{ ...common, opacity: picked ? 0.4 : 1 }}
                  draggable={dragEnabled && daxRefOf(n) !== null} onDragStart={dragEnabled ? drag(n) : undefined}
                  onClick={() => !picked && onPick?.(n)}
                  title={daxRefOf(n) ?? n.ref}
                  className={'group flex items-center gap-1.5 pl-2 pr-2 cursor-pointer hover:bg-[var(--sem-surface-2)] ' + (groupBy === 'flat' ? '' : 'pl-5')}>
                  {multiSelect && (
                    <button onClick={(e) => onCheck(n, e)} aria-checked={checked} role="checkbox"
                      className="w-3.5 h-3.5 rounded inline-flex items-center justify-center text-[9px] shrink-0"
                      style={{ background: checked ? 'var(--sem-accent)' : 'transparent', color: '#fff', border: '1px solid ' + (checked ? 'var(--sem-accent)' : 'var(--sem-border)') }}>{checked ? '✓' : ''}</button>
                  )}
                  <span className="shrink-0 w-3 text-center" style={{ color: KIND_GLYPH[n.kind].color }}>{KIND_GLYPH[n.kind].g}</span>
                  <span className="text-[12px] truncate flex-1">{n.name}
                    {groupBy !== 'table' && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}> · {n.table}</span>}
                    {n.isKey && <span className="text-[9px] ml-1 px-1 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>key</span>}
                  </span>
                  {n.dataType && <span className="text-[10px] shrink-0" style={{ color: 'var(--sem-muted)' }}>{n.dataType}</span>}
                  {!picked && <span className="text-[10px] shrink-0 opacity-0 group-hover:opacity-100" style={{ color: 'var(--sem-muted)' }}>add +</span>}
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* multi-select action bar */}
      {multiSelect && selectedNodes.length > 0 && (
        <div className="flex items-center gap-2 pt-1.5 shrink-0">
          <span className="text-[11px]" style={{ color: 'var(--sem-fg)' }}>{selectedNodes.length} selected</span>
          <button onClick={() => setSel(new Set())} className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>clear</button>
          <button onClick={() => { onPickMany?.(selectedNodes); setSel(new Set()); }}
            className="text-[11px] px-2 py-0.5 rounded-md font-medium ml-auto" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>
            Add {selectedNodes.length} selected
          </button>
        </div>
      )}
    </div>
  );
}

function Chip({ children, active, onClick }: { children: React.ReactNode; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} className="text-[10px] px-1.5 py-0.5 rounded-md font-medium inline-flex items-center gap-1"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', opacity: 0.6 }}>
      {children}
    </button>
  );
}
