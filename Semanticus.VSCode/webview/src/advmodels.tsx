import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { rpc, onDidChange } from './bridge';
import { DaxField } from './daxeditor';
import { ObjectBrowser, useBrowserData, type BrowserNode } from './objectbrowser';

// ===================================================================================================
// Advanced Modelling tab (Studio v2). A killer-UX authoring surface for constructs reachable today
// only via the tree + property grid + raw DAX. v1 ships the two areas whose engine primitives are
// newest — Perspectives (the objects×perspectives membership matrix) and a Field-parameter builder —
// both dual-drive: every action calls a typed engine op (origin='human') the user's Claude can also
// drive over MCP. Calc groups / RLS-OLS / calendars panels follow.
// ===================================================================================================

interface PerspectiveInfo { ref: string; name: string; description?: string; members: string[]; }
interface MeasureRow { ref: string; name: string; table: string; }
interface ColumnRow { ref: string; name: string; table: string; isHidden?: boolean; }
type TableModel = { name: string; ref: string; columns: ColumnRow[]; measures: MeasureRow[]; };
interface CalcItemInfo { ref: string; name: string; ordinal: number; expression: string; formatStringExpression?: string; }
interface CalcGroupInfo { ref: string; name: string; precedence: number; items: CalcItemInfo[]; }
interface DaxLibPackage { id: string; version: string; description?: string; authors?: string[]; tags?: string[]; downloads?: number; projectUrl?: string; }
interface DaxLibDependency { id: string; version?: string; }
interface DaxLibPackageDetail { id: string; version: string; description?: string; authors?: string[]; tags?: string[]; downloads?: number; releaseNotes?: string; projectUrl?: string; repositoryUrl?: string; published?: string; dependencies?: DaxLibDependency[]; functionNames?: string[]; functionCount?: number; }
interface DaxLibInstalled { packageId: string; version: string; functions?: string[]; authors?: string; by?: string; when?: string; }
interface DaxLibInstallResult { revision: number; functions?: string[]; skipped?: string[]; dependenciesInstalled?: string[]; warning?: string; }
// Calendar-based time intelligence (CL 1701+). Mirrors Semanticus.Engine/Protocol.cs (camelCase over the wire).
interface CalendarGroupInfo { timeUnit?: string | null; primaryColumn?: string; associatedColumns: string[]; timeRelatedColumns: string[]; }
interface CalendarInfo { table: string; name: string; description?: string; groups: CalendarGroupInfo[]; }
interface CalendarListResult { calendars: CalendarInfo[]; compatibilityLevel: number; calendarsSupported: boolean; note?: string; }
interface CalendarResult { revision: number; table: string; calendar: string; createdColumns?: { ref: string; name: string }[]; mappings?: string[]; skipped?: string[]; note?: string; }

type Area = 'perspectives' | 'fieldparams' | 'calcgroups' | 'calendars' | 'rlsols' | 'daxlib';
const AREAS: Area[] = ['perspectives', 'fieldparams', 'calcgroups', 'calendars', 'rlsols', 'daxlib'];

export function AdvancedModelsView({ navArea }: { navArea?: { area: string; nonce: number } | null } = {}) {
  const [area, setArea] = useState<Area>('perspectives');
  // A Model-tree "Advanced Modelling ▸ New …" jump lands here on the right builder. Guard the string against the
  // Area union (defensive — the nav payload is external); nonce makes a repeat jump to the same area re-fire.
  useEffect(() => {
    if (navArea && (AREAS as string[]).includes(navArea.area)) setArea(navArea.area as Area);
  }, [navArea?.nonce]);  // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className="sem-evidence-page flex flex-col gap-4">
      <Panel>
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex-1 min-w-0">
            <div className="text-[15px] font-semibold">Advanced Modelling</div>
            <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
              Author the advanced building blocks, guided and live. Your AI Assistant can build these too.
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-1">
            <Seg active={area === 'perspectives'} onClick={() => setArea('perspectives')}>Perspectives</Seg>
            <Seg active={area === 'fieldparams'} onClick={() => setArea('fieldparams')}>Field parameters</Seg>
            <Seg active={area === 'calcgroups'} onClick={() => setArea('calcgroups')}>Calc groups</Seg>
            <Seg active={area === 'calendars'} onClick={() => setArea('calendars')}>Calendars</Seg>
            <Seg active={area === 'rlsols'} onClick={() => setArea('rlsols')}>RLS / OLS</Seg>
            <Seg active={area === 'daxlib'} onClick={() => setArea('daxlib')}>DaxLib</Seg>
          </div>
        </div>
      </Panel>
      {area === 'perspectives' ? <PerspectivesPanel />
        : area === 'fieldparams' ? <FieldParamsPanel />
        : area === 'calcgroups' ? <CalcGroupsPanel />
        : area === 'calendars' ? <CalendarsPanel />
        : area === 'rlsols' ? <RlsOlsPanel />
        : <DaxLibPanel />}
    </div>
  );
}

// ---- shared model read (tables → their columns + measures) ----------------------------------------
function useModelObjects(): { tables: TableModel[]; err: string | null; reload: () => void } {
  const [tables, setTables] = useState<TableModel[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const reload = useMemo(() => async () => {
    try {
      const [cols, meas] = await Promise.all([rpc<ColumnRow[]>('listColumns'), rpc<MeasureRow[]>('listMeasures')]);
      const by = new Map<string, TableModel>();
      const tbl = (name: string) => {
        let t = by.get(name);
        if (!t) { t = { name, ref: 'table:' + name, columns: [], measures: [] }; by.set(name, t); }
        return t;
      };
      for (const c of cols ?? []) tbl(c.table).columns.push(c);
      for (const m of meas ?? []) tbl(m.table).measures.push(m);
      setTables([...by.values()].sort((a, b) => a.name.localeCompare(b.name)));
      setErr(null);
    } catch (e) { setErr(String((e as Error).message ?? e)); }
  }, []);
  useEffect(() => { void reload(); }, [reload]);
  return { tables, err, reload };
}

// ===================================================================================================
// Perspectives — the objects × perspectives include/exclude matrix.
// ===================================================================================================
// The object×perspective membership matrix — rebuilt for large models (Issue 2). Three root causes, three fixes:
// (1) OPTIMISTIC toggle: the check flips in local state instantly + set_perspective_member fires in the background
//     (no blocking rpc + full refetch per click); an error reverts + reconciles from the server.
// (2) FOREIGN-ONLY reload: my own optimistic edits already updated local state, so their didChange echo is skipped;
//     only an agent edit (always foreign) or a second client's edit triggers a debounced reload.
// (3) VIRTUALIZED + MEMOIZED: the <table> becomes a virtualized row list (constant DOM) with a React.memo'd cell,
//     so a toggle re-renders exactly one cell — not tens of thousands.
const P_ROW_H = 30, P_LABEL_W = 260, P_COL_W = 96;
const P_GLYPH: Record<string, { g: string; color: string }> = {
  measure: { g: 'ƒ', color: 'var(--sem-accent)' }, column: { g: '▦', color: '#9cdcfe' }, hierarchy: { g: '⛼', color: '#4ec9b0' },
};
type PRow = { kind: 'table'; name: string; ref: string; count: number; open: boolean } | { kind: 'obj'; node: BrowserNode };

// One membership checkbox. Memoized on (checked, stable onToggle) so a toggle re-renders only the flipped cell.
const PerspCell = memo(function PerspCell({ pRef, objRef, checked, label, onToggle }: {
  pRef: string; objRef: string; checked: boolean; label: string; onToggle: (p: string, o: string, next: boolean) => void;
}) {
  return (
    <button onClick={() => onToggle(pRef, objRef, !checked)} role="checkbox" aria-checked={checked} aria-label={label}
      className="w-4 h-4 rounded inline-flex items-center justify-center text-[10px] transition-colors"
      style={{ background: checked ? 'var(--sem-accent)' : 'transparent', color: '#fff', border: '1px solid ' + (checked ? 'var(--sem-accent)' : 'var(--sem-border)') }}>
      {checked ? '✓' : ''}
    </button>
  );
});

function PerspectivesPanel() {
  const { nodes, tables } = useBrowserData();
  const [persp, setPersp] = useState<PerspectiveInfo[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [query, setQuery] = useState('');
  const pendingSelf = useRef(0);
  const timer = useRef<number | undefined>(undefined);
  const parentRef = useRef<HTMLDivElement>(null);

  const loadPersp = useCallback(async () => {
    try { setPersp(await rpc<PerspectiveInfo[]>('getPerspectives')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }, []);
  useEffect(() => {
    void loadPersp();
    const off = onDidChange((n) => {
      // Skip my own optimistic echo; reload on a foreign change (agent, or a second human client).
      if (n.origin !== 'agent' && pendingSelf.current > 0) { pendingSelf.current--; return; }
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => { void loadPersp(); }, 350);
    });
    return () => { off(); window.clearTimeout(timer.current); };
  }, [loadPersp]);

  const memberSets = useMemo(() => {
    const map = new Map<string, Set<string>>();
    for (const p of persp ?? []) map.set(p.ref, new Set(p.members));
    return map;
  }, [persp]);

  // Optimistic: flip local membership now; fire the op; on failure revert + reconcile from the server.
  const toggle = useCallback((pRef: string, objRef: string, include: boolean) => {
    setPersp((cur) => cur?.map((p) => p.ref !== pRef ? p
      : { ...p, members: include ? [...new Set([...p.members, objRef])] : p.members.filter((m) => m !== objRef) }) ?? cur);
    pendingSelf.current++;
    void rpc('setPerspectiveMember', pRef, objRef, include).catch((e) => {
      pendingSelf.current = Math.max(0, pendingSelf.current - 1);
      setErr(String((e as Error).message ?? e));
      void loadPersp();   // reconcile the failed cell from the truth
    });
  }, [loadPersp]);

  const createPerspective = () => {
    const n = newName.trim(); if (!n) return;
    void rpc('createPerspective', n).then(() => { setNewName(''); setCreating(false); return loadPersp(); })
      .catch((e) => setErr(String((e as Error).message ?? e)));
  };
  const removePerspective = (pRef: string) =>
    void rpc('deleteObject', pRef).then(loadPersp).catch((e) => setErr(String((e as Error).message ?? e)));
  const toggleExpand = (name: string) =>
    setExpanded((s) => { const n = new Set(s); n.has(name) ? n.delete(name) : n.add(name); return n; });

  const byTable = useMemo(() => {
    const m = new Map<string, BrowserNode[]>();
    for (const n of nodes) (m.get(n.table) ?? m.set(n.table, []).get(n.table)!).push(n);
    return m;
  }, [nodes]);

  const tableList = useMemo(() => tables.length ? tables
    : [...byTable.keys()].sort().map((n) => ({ ref: 'table:' + n, name: n, isHidden: false, isCalculationGroup: false })), [tables, byTable]);

  // flatten (filtered, expanded) tables + their objects into positional rows — search auto-expands matches.
  const rows = useMemo<PRow[]>(() => {
    const q = query.trim().toLowerCase();
    const out: PRow[] = [];
    for (const t of tableList) {
      const kids = byTable.get(t.name) ?? [];
      const tableMatch = !q || t.name.toLowerCase().includes(q);
      const matchKids = q ? kids.filter((k) => k.name.toLowerCase().includes(q)) : kids;
      if (!tableMatch && matchKids.length === 0) continue;
      const open = q ? true : expanded.has(t.name);
      out.push({ kind: 'table', name: t.name, ref: t.ref, count: kids.length, open });
      if (open) for (const k of (tableMatch ? kids : matchKids)) out.push({ kind: 'obj', node: k });
    }
    return out;
  }, [tableList, byTable, expanded, query]);

  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => parentRef.current, estimateSize: () => P_ROW_H, overscan: 14 });

  if (!persp) return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading perspectives…'}</div></Panel>;

  const cols = persp;
  const totalW = P_LABEL_W + cols.length * P_COL_W;

  return (
    <Panel>
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      <div className="flex items-center gap-2 mb-3 flex-wrap">
        <SectionTitle>Perspectives</SectionTitle>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          curated subsets for a focused Q&A / report view: check an object to include it (a table cascades to its fields)
        </span>
        <div className="ml-auto flex items-center gap-1.5">
          {cols.length > 0 && (
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="filter objects…" spellCheck={false}
              className="text-[12px] px-2 py-1 rounded outline-none" style={{ width: 160, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          )}
          {creating ? (
            <span className="flex items-center gap-1">
              <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="perspective name…"
                onKeyDown={(e) => { if (e.key === 'Enter') createPerspective(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                className="text-[12px] px-2 py-1 rounded outline-none" style={{ width: 170, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
              <MiniButton disabled={!newName.trim()} onClick={createPerspective}>Add</MiniButton>
            </span>
          ) : <Button onClick={() => setCreating(true)}>+ Perspective</Button>}
        </div>
      </div>

      {cols.length === 0 ? (
        <div className="text-[13px]" style={{ color: 'var(--sem-muted)' }}>No perspectives yet. Add one to start curating a view.</div>
      ) : (
        <div ref={parentRef} className="overflow-auto rounded-lg" style={{ border: '1px solid var(--sem-border)', maxHeight: 520, width: 'fit-content', maxWidth: '100%' }}>
          <div style={{ minWidth: totalW, position: 'relative' }}>
            {/* sticky perspective (column) headers */}
            <div className="sticky top-0 z-10 flex" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)', height: P_ROW_H + 8 }}>
              <div className="px-2 py-1 text-[11px] font-medium flex items-center" style={{ width: P_LABEL_W, color: 'var(--sem-muted)' }}>Object</div>
              {cols.map((p) => (
                <div key={p.ref} className="flex flex-col items-center justify-center px-1" style={{ width: P_COL_W }}>
                  <span className="text-[11px] font-medium truncate max-w-[86px]" title={p.name}>{p.name}</span>
                  <span className="flex items-center gap-1">
                    <span className="text-[9px] tnum" style={{ color: 'var(--sem-muted)' }} title="objects included">{p.members.length}</span>
                    <button onClick={() => removePerspective(p.ref)} title="Delete perspective" className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>✕</button>
                  </span>
                </div>
              ))}
            </div>
            {/* virtualized body */}
            <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
              {virtualizer.getVirtualItems().map((vr) => {
                const row = rows[vr.index];
                const common = { position: 'absolute' as const, top: vr.start, left: 0, height: P_ROW_H, width: totalW };
                if (row.kind === 'table') {
                  return (
                    <div key={vr.key} className="flex items-center" style={{ ...common, borderTop: '1px solid var(--sem-border)' }}>
                      <button onClick={() => toggleExpand(row.name)} className="flex items-center gap-1.5 text-left px-2" style={{ width: P_LABEL_W }}>
                        <span className="inline-block w-3 text-[10px]" style={{ transform: row.open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
                        <span className="text-[12px] font-medium truncate">{row.name}</span>
                        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>({row.count})</span>
                      </button>
                      {cols.map((p) => (
                        <div key={p.ref} className="flex items-center justify-center" style={{ width: P_COL_W }}>
                          <PerspCell pRef={p.ref} objRef={row.ref} checked={memberSets.get(p.ref)?.has(row.ref) ?? false}
                            label={`${row.name} in ${p.name}`} onToggle={toggle} />
                        </div>
                      ))}
                    </div>
                  );
                }
                const n = row.node;
                const gl = P_GLYPH[n.kind] ?? P_GLYPH.column;
                return (
                  <div key={vr.key} className="flex items-center" style={{ ...common, background: 'var(--sem-surface-2)' }}>
                    <div className="flex items-center gap-1.5 pl-9 pr-2 text-[12px] truncate" style={{ width: P_LABEL_W, opacity: n.isHidden ? 0.55 : 1 }}>
                      <span style={{ color: gl.color }}>{gl.g}</span><span className="truncate">{n.name}</span>
                    </div>
                    {cols.map((p) => (
                      <div key={p.ref} className="flex items-center justify-center" style={{ width: P_COL_W }}>
                        <PerspCell pRef={p.ref} objRef={n.ref} checked={memberSets.get(p.ref)?.has(n.ref) ?? false}
                          label={`${n.name} in ${p.name}`} onToggle={toggle} />
                      </div>
                    ))}
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      )}
    </Panel>
  );
}

// ===================================================================================================
// Field-parameter builder — pick measures/columns in order, name it, create the field parameter.
// ===================================================================================================
type Pick = { ref: string; name: string; table: string; kind: 'measure' | 'column'; label: string };

function FieldParamsPanel() {
  const [name, setName] = useState('');
  const [picked, setPicked] = useState<Pick[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const dragIdx = useRef<number | null>(null);
  const [dragOver, setDragOver] = useState<number | null>(null);

  const pickedRefs = useMemo(() => new Set(picked.map((p) => p.ref)), [picked]);
  const toPick = (n: BrowserNode): Pick => ({ ref: n.ref, name: n.name, table: n.table, kind: n.kind === 'measure' ? 'measure' : 'column', label: n.name });
  const add = (n: BrowserNode) => setPicked((cur) => cur.some((p) => p.ref === n.ref) ? cur : [...cur, toPick(n)]);
  const addMany = (ns: BrowserNode[]) => setPicked((cur) => {
    const have = new Set(cur.map((p) => p.ref));
    return [...cur, ...ns.filter((n) => !have.has(n.ref)).map(toPick)];
  });
  const remove = (ref: string) => setPicked((cur) => cur.filter((p) => p.ref !== ref));
  const relabel = (ref: string, label: string) => setPicked((cur) => cur.map((p) => p.ref === ref ? { ...p, label } : p));
  const move = (i: number, d: -1 | 1) => setPicked((cur) => {
    const j = i + d; if (j < 0 || j >= cur.length) return cur;
    const n = cur.slice(); [n[i], n[j]] = [n[j], n[i]]; return n;
  });
  // Drag-to-reorder within the picked list (pure in-webview; no engine call until Create).
  const reorder = (from: number, to: number) => setPicked((cur) => {
    if (from === to || from < 0 || to < 0 || from >= cur.length || to >= cur.length) return cur;
    const n = cur.slice(); const [it] = n.splice(from, 1); n.splice(to, 0, it); return n;
  });

  const create = async () => {
    const n = name.trim();
    if (!n || picked.length === 0) return;
    setBusy(true); setMsg(null);
    try {
      const items = picked.map((p) => ({ objectRef: p.ref, label: p.label.trim() || p.name }));
      const ref = await rpc<string>('createFieldParameter', n, items);
      setMsg({ ok: true, text: `Created ${ref} with ${picked.length} field${picked.length === 1 ? '' : 's'}.` });
      setName(''); setPicked([]);
    } catch (e) { setMsg({ ok: false, text: String((e as Error).message ?? e) }); }
    finally { setBusy(false); }
  };

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-2 mb-3">
          <SectionTitle>New field parameter</SectionTitle>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>a slicer that swaps which measures/columns a visual shows: pick fields, rename their slicer labels, order them</span>
        </div>
        <div className="flex items-center gap-2 mb-3">
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="parameter name (e.g. 'Metric')…"
            className="text-[13px] px-2.5 py-1.5 rounded outline-none flex-1" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <Button primary disabled={busy || !name.trim() || picked.length === 0} onClick={() => void create()}>
            {busy ? 'Creating…' : `Create (${picked.length})`}
          </Button>
        </div>
        {msg && <Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner>}
      </Panel>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <Panel>
          <SectionTitle>Selected fields (in order)</SectionTitle>
          {picked.length === 0 ? (
            <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>Pick measures or columns from the browser →  (click, or multi-select and add, or drag)</div>
          ) : (
            <div className="flex flex-col gap-1 mt-2">
              {picked.map((p, i) => (
                <div key={p.ref} draggable
                  onDragStart={() => { dragIdx.current = i; }}
                  onDragOver={(e) => { e.preventDefault(); setDragOver(i); }}
                  onDragLeave={() => setDragOver((o) => (o === i ? null : o))}
                  onDrop={(e) => { e.preventDefault(); if (dragIdx.current !== null) reorder(dragIdx.current, i); dragIdx.current = null; setDragOver(null); }}
                  className="flex items-center gap-2 py-1 px-2 rounded"
                  style={{ background: 'var(--sem-surface-2)', border: '1px solid ' + (dragOver === i ? 'var(--sem-accent)' : 'transparent') }}>
                  <span className="cursor-grab select-none" title="Drag to reorder" style={{ color: 'var(--sem-muted)' }}>⋮⋮</span>
                  <span className="text-[11px] tnum w-4 text-center" style={{ color: 'var(--sem-muted)' }}>{i + 1}</span>
                  <span className="shrink-0" style={{ color: p.kind === 'measure' ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>{p.kind === 'measure' ? 'ƒ' : '▦'}</span>
                  <input value={p.label} onChange={(e) => relabel(p.ref, e.target.value)} title={`${p.name} · ${p.table} · slicer label`}
                    className="text-[12px] px-1.5 py-0.5 rounded outline-none flex-1 min-w-0" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
                  <span className="text-[10px] shrink-0 truncate max-w-[90px]" style={{ color: 'var(--sem-muted)' }} title={p.table}>{p.table}</span>
                  <button onClick={() => move(i, -1)} disabled={i === 0} className="text-[11px] disabled:opacity-30" title="Up">↑</button>
                  <button onClick={() => move(i, 1)} disabled={i === picked.length - 1} className="text-[11px] disabled:opacity-30" title="Down">↓</button>
                  <button onClick={() => remove(p.ref)} className="text-[11px]" style={{ color: 'var(--sem-muted)' }} title="Remove">✕</button>
                </div>
              ))}
            </div>
          )}
        </Panel>

        <Panel>
          <SectionTitle>Add a measure or column</SectionTitle>
          <div className="mt-2">
            <ObjectBrowser kinds={['measure', 'column']} multiSelect dragEnabled pickedRefs={pickedRefs} height={380}
              onPick={add} onPickMany={addMany} emptyHint="No measures or columns in this model." />
          </div>
        </Panel>
      </div>
    </div>
  );
}

// ===================================================================================================
// Calculation groups — create groups, set precedence, author/reorder items, set per-item format.
// ===================================================================================================
// One-click calc-item templates (the analyst on-ramp): each wraps SELECTEDMEASURE() over the standard
// 'Date'[Date] convention + a sensible format. The DAX editor's live markers flag it if the model's date
// column differs, and pro devs edit the body freely afterward.
const CALC_ITEM_TEMPLATES: { label: string; expr: string; fmt?: string }[] = [
  { label: 'YTD', expr: "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )" },
  { label: 'QTD', expr: "CALCULATE ( SELECTEDMEASURE (), DATESQTD ( 'Date'[Date] ) )" },
  { label: 'MTD', expr: "CALCULATE ( SELECTEDMEASURE (), DATESMTD ( 'Date'[Date] ) )" },
  { label: 'PY', expr: "CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )" },
  { label: 'YoY %', expr: "VAR _cur = SELECTEDMEASURE ()\nVAR _py = CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )\nRETURN DIVIDE ( _cur - _py, _py )", fmt: '0.0%' },
  { label: 'Running total', expr: "CALCULATE ( SELECTEDMEASURE (), FILTER ( ALL ( 'Date'[Date] ), 'Date'[Date] <= MAX ( 'Date'[Date] ) ) )" },
];
// Common static format strings for the analyst format-string picker (empty = inherit the base measure's format).
// Exported so the Spec tab's measure format-string dropdown reuses the SAME curated list (single source).
export const FORMAT_PRESETS: { value: string; label: string }[] = [
  { value: '', label: '(inherit base format)' },
  { value: '#,0', label: '#,0  (whole)' },
  { value: '#,0.00', label: '#,0.00' },
  { value: '0.0%', label: '0.0%  (percent)' },
  { value: '0.00%', label: '0.00%' },
  { value: '$#,0', label: '$#,0  (currency)' },
  { value: '$#,0.00', label: '$#,0.00' },
  { value: '0', label: '0  (integer)' },
];

function CalcGroupsPanel() {
  const [groups, setGroups] = useState<CalcGroupInfo[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const timer = useRef<number | undefined>(undefined);

  async function load() {
    try { setGroups(await rpc<CalcGroupInfo[]>('listCalculationGroups')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void load();
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void load(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, []);
  const fail = (e: unknown) => setErr(String((e as Error).message ?? e));

  const createGroup = () => {
    const n = newName.trim(); if (!n) return;
    void rpc('createCalculationGroup', n).then(() => { setNewName(''); setCreating(false); return load(); }).catch(fail);
  };

  if (!groups) return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading calculation groups…'}</div></Panel>;

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        {err && <Banner color="var(--sem-bad)">{err}</Banner>}
        <div className="flex items-center gap-2">
          <SectionTitle>Calculation groups</SectionTitle>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            reusable calculations (time-intelligence, currency, % of total) applied over SELECTEDMEASURE(); higher precedence applies first
          </span>
          <div className="ml-auto">
            {creating ? (
              <span className="flex items-center gap-1">
                <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="group name…"
                  onKeyDown={(e) => { if (e.key === 'Enter') createGroup(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                  className="text-[12px] px-2 py-1 rounded outline-none" style={{ width: 170, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
                <MiniButton disabled={!newName.trim()} onClick={createGroup}>Add</MiniButton>
              </span>
            ) : <Button onClick={() => setCreating(true)}>+ Group</Button>}
          </div>
        </div>
        {groups.length === 0 && <div className="text-[13px] mt-3" style={{ color: 'var(--sem-muted)' }}>No calculation groups yet. Add one (it becomes a special single-column table).</div>}
      </Panel>

      {groups.map((g) => <CalcGroupCard key={g.ref} group={g} reload={load} onError={fail} />)}
    </div>
  );
}

function CalcGroupCard({ group, reload, onError }: { group: CalcGroupInfo; reload: () => void; onError: (e: unknown) => void }) {
  const [prec, setPrec] = useState(String(group.precedence));
  const [adding, setAdding] = useState(false);
  const [iName, setIName] = useState('');
  const [iExpr, setIExpr] = useState('CALCULATE ( SELECTEDMEASURE () )');
  const [iFmt, setIFmt] = useState('');
  const [iValid, setIValid] = useState(true);

  const commitPrec = () => {
    const n = parseInt(prec, 10);
    if (!Number.isFinite(n) || n === group.precedence) { setPrec(String(group.precedence)); return; }
    void rpc('setCalcGroupPrecedence', group.ref, n).then(reload).catch(onError);
  };
  const reset = () => { setIName(''); setIExpr('CALCULATE ( SELECTEDMEASURE () )'); setIFmt(''); setAdding(false); };
  const addItem = () => {
    const n = iName.trim(); if (!n || !iValid) return;
    // create the item, then (if a template supplied a format) set its format string in a follow-up op.
    void rpc<string>('createCalculationItem', group.ref, n, iExpr)
      .then((newRef) => (iFmt && newRef ? rpc('setCalcItemFormatString', newRef, `"${iFmt}"`) : Promise.resolve()))
      .then(() => { reset(); reload(); }).catch(onError);
  };
  const useTemplate = (t: typeof CALC_ITEM_TEMPLATES[number]) => { if (!iName.trim()) setIName(t.label); setIExpr(t.expr); setIFmt(t.fmt ?? ''); };
  const delGroup = () => void rpc('deleteObject', group.ref).then(reload).catch(onError);

  return (
    <Panel>
      <div className="flex items-center gap-2 mb-2">
        <span className="text-[13px] font-semibold">{group.name}</span>
        <span className="text-[10px] uppercase px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>calc group</span>
        <label className="text-[11px] flex items-center gap-1 ml-2" style={{ color: 'var(--sem-muted)' }} title="Higher precedence applies first when calc groups are combined">
          precedence
          <input value={prec} onChange={(e) => setPrec(e.target.value)} onBlur={commitPrec}
            onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
            inputMode="numeric" className="text-[11px] px-1.5 py-0.5 rounded outline-none w-16 tnum"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        </label>
        <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>higher applies first</span>
        <div className="ml-auto flex items-center gap-1.5">
          {adding ? null : <MiniButton onClick={() => setAdding(true)}>+ Item</MiniButton>}
          <MiniButton onClick={delGroup}>Delete group</MiniButton>
        </div>
      </div>

      {adding && (
        <div className="flex flex-col gap-1.5 mb-3 p-2 rounded" style={{ background: 'var(--sem-surface-2)' }}>
          <div className="flex items-center gap-1.5 flex-wrap">
            <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>templates:</span>
            {CALC_ITEM_TEMPLATES.map((t) => (
              <button key={t.label} onClick={() => useTemplate(t)} title={t.expr}
                className="text-[10px] px-1.5 py-0.5 rounded-md font-medium" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>{t.label}</button>
            ))}
          </div>
          <input autoFocus value={iName} onChange={(e) => setIName(e.target.value)} placeholder="item name (e.g. 'YTD')…"
            className="text-[12px] px-2 py-1 rounded outline-none" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <DaxField value={iExpr} onChange={setIExpr} scope="calcitem" minHeight={72} onValidity={(v) => setIValid(v)}
            ariaLabel="New calculation item expression" askContext="a calculation-item expression that wraps SELECTEDMEASURE()" />
          <FormatStringControl value={iFmt ? `"${iFmt}"` : ''} onSave={(raw) => setIFmt(raw.replace(/^"|"$/g, ''))} inline />
          <div className="flex items-center gap-1.5">
            <MiniButton disabled={!iName.trim() || !iValid} onClick={addItem}>Add item</MiniButton>
            <MiniButton onClick={reset}>Cancel</MiniButton>
          </div>
        </div>
      )}

      {group.items.length === 0 ? (
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No items yet. Add one (its DAX wraps SELECTEDMEASURE()).</div>
      ) : (
        <div className="flex flex-col gap-2">
          {group.items.map((it) => <CalcItemRow key={it.ref} item={it} reload={reload} onError={onError} />)}
        </div>
      )}
    </Panel>
  );
}

function CalcItemRow({ item, reload, onError }: { item: CalcItemInfo; reload: () => void; onError: (e: unknown) => void }) {
  const [expr, setExpr] = useState(item.expression ?? '');
  const [valid, setValid] = useState(true);
  // Adopt an external (other-door / agent) edit to THIS item ONLY when the user has no pending local edit —
  // so a live didChange refreshes a row you're merely viewing, but never clobbers a draft you're typing (and a
  // stale draft can't be re-saved over someone else's change). Renames remount the row (ref-keyed) → server wins.
  const lastExpr = useRef(item.expression ?? '');
  useEffect(() => { const sv = item.expression ?? ''; setExpr((cur) => (cur === lastExpr.current ? sv : cur)); lastExpr.current = sv; }, [item.expression]);
  const dirtyExpr = expr !== (item.expression ?? '');

  const saveExpr = () => { if (dirtyExpr && valid) void rpc('setDax', item.ref, expr).then(reload).catch(onError); };
  const saveFmt = (raw: string) => void rpc('setCalcItemFormatString', item.ref, raw).then(reload).catch(onError);
  const del = () => void rpc('deleteObject', item.ref).then(reload).catch(onError);

  return (
    <div className="rounded p-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-1">
        <span className="text-[12px] font-medium">{item.name}</span>
        <span className="text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>#{item.ordinal}</span>
        <div className="ml-auto flex items-center gap-1.5">
          <MiniButton disabled={!dirtyExpr || !valid} onClick={saveExpr}>{dirtyExpr ? (valid ? 'Save DAX' : 'Fix errors') : 'Saved'}</MiniButton>
          <button onClick={del} className="text-[11px]" style={{ color: 'var(--sem-muted)' }} title="Delete item">✕</button>
        </div>
      </div>
      <DaxField value={expr} onChange={setExpr} scope="calcitem" minHeight={64} onValidity={(v) => setValid(v)}
        ariaLabel={`${item.name} expression`} askContext={`the '${item.name}' calculation-item expression`} />
      <div className="mt-1.5">
        <FormatStringControl value={item.formatStringExpression ?? ''} onSave={saveFmt} />
      </div>
    </div>
  );
}

// The calc-item format-string control: an analyst STATIC picker (common formats, written as a quoted literal) vs a
// pro-dev DYNAMIC expression (a format-string DAX expression, e.g. SELECTEDMEASUREFORMATSTRING). It infers its
// starting mode from the stored value (a pure "…" literal → static; anything else → dynamic). Writes the SAME
// set_calc_item_format_string op either way — the difference is only whether the value is a quoted literal.
function FormatStringControl({ value, onSave, inline }: { value: string; onSave: (raw: string) => void; inline?: boolean }) {
  const literal = /^\s*"(.*)"\s*$/.exec(value);
  const [mode, setMode] = useState<'static' | 'dynamic'>(value && !literal ? 'dynamic' : 'static');
  const [dyn, setDyn] = useState(value);
  const staticVal = literal ? literal[1] : '';
  const isPreset = FORMAT_PRESETS.some((p) => p.value === staticVal);

  return (
    <div className="flex items-center gap-2 flex-wrap">
      <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>format</span>
      <div className="flex items-center gap-0.5">
        <button onClick={() => setMode('static')} className="text-[10px] px-1.5 py-0.5 rounded-md font-medium"
          style={mode === 'static' ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)' }}>Static</button>
        <button onClick={() => { setMode('dynamic'); setDyn(value); }} className="text-[10px] px-1.5 py-0.5 rounded-md font-medium"
          style={mode === 'dynamic' ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)' }}>Dynamic</button>
      </div>
      {mode === 'static' ? (
        // Selecting a preset writes it immediately as a quoted literal (empty = inherit → clears the expression).
        <Select value={isPreset ? staticVal : ''} compact
          onChange={(v) => onSave(v ? `"${v}"` : '')}
          options={FORMAT_PRESETS.map((p) => ({ value: p.value, label: p.label }))} />
      ) : (
        <div className={inline ? 'w-full' : 'flex-1 min-w-[220px]'}>
          <DaxField value={dyn} onChange={setDyn} scope="formatstring" minHeight={48}
            placeholder='e.g. SELECTEDMEASUREFORMATSTRING or "0.0%"' ariaLabel="Format-string expression" askContext="a calc-item format-string expression" />
          <div className="mt-1"><MiniButton disabled={dyn === value} onClick={() => onSave(dyn)}>{dyn === value ? 'Saved' : 'Save format'}</MiniButton></div>
        </div>
      )}
    </div>
  );
}

// ===================================================================================================
// Calendars — calendar-based time intelligence (CL 1701+). A calendar maps a date table's columns to
// TimeUnit categories (Year/Quarter/Month/Week/Date + recurring …OfYear variants), which is what lets
// DAX shift HIERARCHICALLY (flexible functions clear context by the category graph) instead of the
// classic lateral date-column shift. Several calendars (Gregorian + Fiscal + ISO + 4-4-5 + 13-period)
// coexist on one table. The obsolete generate_date_table flow survives below as a clearly-secondary
// "classic" affordance. All actions are dual-drive: each calls a typed engine op the user's Claude drives too.
// ===================================================================================================
// Absolute + recurring TimeUnits (mapping picker); 'time-related' = the untagged bucket (IsWorkingDay, HolidayName…).
const TIME_UNITS = [
  'Year', 'Semester', 'SemesterOfYear', 'Quarter', 'QuarterOfYear', 'QuarterOfSemester',
  'Month', 'MonthOfYear', 'MonthOfSemester', 'MonthOfQuarter',
  'Week', 'WeekOfYear', 'WeekOfSemester', 'WeekOfQuarter', 'WeekOfMonth',
  'Date', 'DayOfYear', 'DayOfSemester', 'DayOfQuarter', 'DayOfMonth', 'DayOfWeek',
];
const TIME_RELATED = 'time-related';   // UI label for the untagged bucket (engine wants timeUnit=null)
const calendarUnitLabel = (unit: string) => unit.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
const TEMPLATES: { id: string; label: string; hint: string }[] = [
  { id: 'gregorian', label: 'Gregorian', hint: 'Year / Quarter / Month / Day' },
  { id: 'fiscal', label: 'Fiscal', hint: 'FY starting a chosen month' },
  { id: 'iso', label: 'ISO', hint: 'ISO-8601 weeks (Thursday rule)' },
  { id: '445', label: '4-4-5', hint: 'retail 4-4-5 periods over ISO weeks' },
  { id: '13period', label: '13-Period', hint: '13 four-week periods' },
];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function CalendarsPanel() {
  const { tables } = useModelObjects();
  const [list, setList] = useState<CalendarListResult | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const [confirmUpgrade, setConfirmUpgrade] = useState(false);
  const [upgrading, setUpgrading] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  const fail = (e: unknown) => setMsg({ ok: false, text: String((e as Error).message ?? e) });

  async function load() {
    try { setList(await rpc<CalendarListResult>('listCalendars', null)); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void load();
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void load(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, []);

  if (!list) return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading calendars…'}</div></Panel>;

  // CL gate: calendars need compatibility level 1701+. Below that, the whole feature is inert — show the
  // explainer + a one-way upgrade CTA instead of controls that would only error.
  if (!list.calendarsSupported) {
    const upgrade = async () => {
      setMsg(null);
      setUpgrading(true);
      try {
        await rpc('setCompatibilityLevel', 1701);
        await load();
        setConfirmUpgrade(false);
        setMsg({ ok: true, text: 'Compatibility level raised to 1701. Calendars are now available.' });
      } catch (e) {
        fail(e);
      } finally {
        setUpgrading(false);
      }
    };
    return (
      <div className="flex flex-col gap-4">
        {msg && <Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner>}
        <Panel>
          <SectionTitle>Calendars need compatibility level 1701+</SectionTitle>
          <div className="text-[12px] mt-2" style={{ color: 'var(--sem-fg)' }}>
            Calendar-based time intelligence is a modern metadata feature. This model is at compatibility
            level <span className="tnum font-semibold">{list.compatibilityLevel}</span>.
          </div>
          <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>
            You can still use the classic date-table approach below. To author calendars, raise the level to 1701.
          </div>
          {!confirmUpgrade ? (
            <div className="mt-3"><Button primary onClick={() => setConfirmUpgrade(true)}>Upgrade to 1701</Button></div>
          ) : (
            <div className="mt-3 rounded-lg p-3" role="alert" style={{ background: 'var(--sem-bg)', border: '1px solid var(--sem-warn)' }}>
              <div className="text-[12px] font-semibold" style={{ color: 'var(--sem-warn)' }}>Confirm the one-way upgrade</div>
              <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>
                Older tools may no longer open this model after its compatibility level is raised.
              </div>
              <div className="flex items-center gap-2 mt-3">
                <Button primary disabled={upgrading} onClick={() => void upgrade()}>{upgrading ? 'Upgrading…' : 'Confirm upgrade'}</Button>
                <Button disabled={upgrading} onClick={() => setConfirmUpgrade(false)}>Cancel</Button>
              </div>
            </div>
          )}
        </Panel>
        <ShiftExplainer />
        <ClassicDateTable tables={tables} setMsg={setMsg} />
      </div>
    );
  }

  const byTable = list.calendars.slice().sort((a, b) => a.table.localeCompare(b.table) || a.name.localeCompare(b.name));

  return (
    <div className="flex flex-col gap-4">
      {msg && <Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner>}
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}

      <TemplateQuickStart tables={tables} setMsg={setMsg} onDone={load} />

      <Panel>
        <div className="flex items-center gap-2 mb-2">
          <SectionTitle>Calendars in this model</SectionTitle>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            each calendar maps columns to calendar categories, the graph that drives calendar-aware DAX (compatibility level {list.compatibilityLevel})
          </span>
        </div>
        {byTable.length === 0 ? (
          <div className="text-[13px]" style={{ color: 'var(--sem-muted)' }}>No calendars yet. Use a template above to create one (Gregorian / Fiscal / ISO / 4-4-5 / 13-Period).</div>
        ) : (
          <div className="flex flex-col gap-2">
            {byTable.map((cal) => (
              <CalendarCard key={cal.table + '|' + cal.name} cal={cal}
                columns={tables.find((t) => t.name === cal.table)?.columns ?? []}
                setMsg={setMsg} onDone={load} />
            ))}
          </div>
        )}
      </Panel>

      <ShiftExplainer />
      <ClassicDateTable tables={tables} setMsg={setMsg} />
    </div>
  );
}

type MsgSetter = (m: { ok: boolean; text: string } | null) => void;

// ---- template quick-start: the modern one-step calendar setup (define_calendar_from_template) ----
function TemplateQuickStart({ tables, setMsg, onDone }: { tables: TableModel[]; setMsg: MsgSetter; onDone: () => void }) {
  const [template, setTemplate] = useState('gregorian');
  const [fiscalStart, setFiscalStart] = useState(7);   // July (the default the engine uses)
  const [table, setTable] = useState('');
  const [dateCol, setDateCol] = useState('');
  const [busy, setBusy] = useState(false);
  const cols = useMemo(() => tables.find((t) => t.name === table)?.columns ?? [], [tables, table]);
  const fail = (e: unknown) => setMsg({ ok: false, text: String((e as Error).message ?? e) });

  const gen = async () => {
    setBusy(true); setMsg(null);
    try {
      const r = await rpc<CalendarResult>('defineCalendarFromTemplate', template,
        table.trim() || null, dateCol.trim() || null, fiscalStart, null, null, null);
      const parts = [`${r.calendar} on ${r.table}`];
      if (r.createdColumns?.length) parts.push(`${r.createdColumns.length} column(s) created`);
      if (r.mappings?.length) parts.push(`${r.mappings.length} mapping(s)`);
      if (r.skipped?.length) parts.push(`${r.skipped.length} skipped`);
      setMsg({ ok: true, text: (r.note ? r.note + ' · ' : '') + parts.join(' · ') + '.' });
      onDone();
    } catch (e) { fail(e); } finally { setBusy(false); }
  };

  return (
    <Panel>
      <div className="flex items-center gap-2 mb-1">
        <SectionTitle>New calendar from a template</SectionTitle>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>generates missing columns and their calendar-category mappings in one undoable step. The modern replacement for a date table</span>
      </div>
      <div className="flex items-center gap-1.5 flex-wrap mt-2">
        {TEMPLATES.map((t) => (
          <button key={t.id} onClick={() => setTemplate(t.id)} title={t.hint}
            className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-colors"
            style={template === t.id ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            {t.label}
          </button>
        ))}
      </div>
      <div className="text-[11px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>{TEMPLATES.find((t) => t.id === template)?.hint}</div>
      <div className="flex items-center gap-2 flex-wrap mt-3">
        {template === 'fiscal' && (
          <label className="text-[11px] flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
            FY starts
            <Select value={String(fiscalStart)} onChange={(v) => setFiscalStart(parseInt(v, 10))} compact
              options={MONTHS.map((m, i) => ({ value: String(i + 1), label: m }))} />
          </label>
        )}
        <Select value={table} onChange={(v) => { setTable(v); setDateCol(''); }} placeholder="target table (default Date)…"
          options={tables.map((t) => ({ value: t.name, label: t.name }))} />
        <Select value={dateCol} onChange={setDateCol} placeholder="date column (auto)…"
          options={cols.map((c) => ({ value: c.name, label: c.name }))} />
        <div className="ml-auto"><Button primary disabled={busy} onClick={() => void gen()}>{busy ? 'Generating…' : 'Create calendar'}</Button></div>
      </div>
    </Panel>
  );
}

// ---- one calendar: its mappings + an inline mapping editor ----
function CalendarCard({ cal, columns, setMsg, onDone }: { cal: CalendarInfo; columns: ColumnRow[]; setMsg: MsgSetter; onDone: () => void }) {
  const [adding, setAdding] = useState(false);
  const [col, setCol] = useState('');
  const [unit, setUnit] = useState('Date');
  const [assoc, setAssoc] = useState(false);
  const fail = (e: unknown) => setMsg({ ok: false, text: String((e as Error).message ?? e) });

  const units = cal.groups.filter((g) => g.timeUnit != null);
  const bucket = cal.groups.find((g) => g.timeUnit == null);

  const tag = (column: string, timeUnit: string | null, associated: boolean, remove: boolean) => {
    setMsg(null);
    void rpc<CalendarResult>('tagCalendarColumn', cal.table, cal.name, column, timeUnit, associated, remove)
      .then(() => { onDone(); }).catch(fail);
  };
  const addMapping = () => {
    const c = col.trim(); if (!c) return;
    tag(c, unit === TIME_RELATED ? null : unit, assoc, false);
    setCol(''); setAssoc(false); setAdding(false);
  };
  const del = () => {
    if (!window.confirm(`Delete calendar '${cal.name}' from ${cal.table}? The columns themselves are kept.`)) return;
    setMsg(null);
    void rpc('deleteCalendar', cal.table, cal.name).then(() => { onDone(); }).catch(fail);
  };

  return (
    <div className="rounded p-2.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-2">
        <span className="text-[13px] font-semibold">{cal.name}</span>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>on {cal.table}</span>
        {cal.description && <span className="text-[11px] truncate" style={{ color: 'var(--sem-muted)' }}>· {cal.description}</span>}
        <div className="ml-auto flex items-center gap-1.5">
          {!adding && <MiniButton onClick={() => setAdding(true)}>+ Mapping</MiniButton>}
          <MiniButton onClick={del}>Delete calendar</MiniButton>
        </div>
      </div>

      {adding && (
        <div className="flex items-center gap-2 flex-wrap mb-2 p-2 rounded" style={{ background: 'var(--sem-bg)', border: '1px solid var(--sem-border)' }}>
          <Select value={col} onChange={setCol} placeholder="column…" options={columns.map((c) => ({ value: c.name, label: c.name }))} />
          <Select value={unit} onChange={setUnit} compact options={[{ value: TIME_RELATED, label: 'time-related columns' }, ...TIME_UNITS.map((u) => ({ value: u, label: calendarUnitLabel(u) }))]} />
          <label className="text-[11px] flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
            <Check checked={assoc} onChange={() => setAssoc((v) => !v)} /> associated
          </label>
          <MiniButton disabled={!col.trim()} onClick={addMapping}>Add</MiniButton>
          <MiniButton onClick={() => { setAdding(false); setCol(''); setAssoc(false); }}>Cancel</MiniButton>
        </div>
      )}

      {units.length === 0 && !bucket ? (
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No mappings yet. Add a column to a calendar category.</div>
      ) : (
        <div className="flex flex-col gap-1">
          {units.map((g) => (
            <div key={g.timeUnit} className="flex items-center gap-2 text-[12px]">
              <span className="inline-block w-[130px] shrink-0 font-medium capitalize" style={{ color: 'var(--sem-accent)' }}>{calendarUnitLabel(g.timeUnit!)}</span>
              <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>→</span>
              {g.primaryColumn ? (
                <span className="flex items-center gap-1 px-1.5 py-0.5 rounded font-mono text-[11px]" style={{ background: 'var(--sem-surface)' }}>
                  {g.primaryColumn}
                  <button onClick={() => tag(g.primaryColumn!, null, false, true)} title="Remove mapping" style={{ color: 'var(--sem-muted)' }}>✕</button>
                </span>
              ) : <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>(no primary)</span>}
              {g.associatedColumns.map((a) => (
                <span key={a} className="flex items-center gap-1 px-1.5 py-0.5 rounded font-mono text-[11px]" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)' }}>
                  +{a}
                  <button onClick={() => tag(a, null, false, true)} title="Remove associated column" style={{ color: 'var(--sem-muted)' }}>✕</button>
                </span>
              ))}
            </div>
          ))}
          {bucket && bucket.timeRelatedColumns.length > 0 && (
            <div className="flex items-center gap-2 text-[12px] flex-wrap">
              <span className="inline-block w-[130px] shrink-0" style={{ color: 'var(--sem-muted)' }}>time-related</span>
              <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>→</span>
              {bucket.timeRelatedColumns.map((c) => (
                <span key={c} className="flex items-center gap-1 px-1.5 py-0.5 rounded font-mono text-[11px]" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)' }}>
                  {c}
                  <button onClick={() => tag(c, null, false, true)} title="Remove from bucket" style={{ color: 'var(--sem-muted)' }}>✕</button>
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ---- collapsible help: Fixed vs Flexible time-intelligence shifts ----
function ShiftExplainer() {
  const [open, setOpen] = useState(false);
  return (
    <Panel>
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 text-left w-full">
        <span className="inline-block w-3 text-[10px]" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
        <SectionTitle>Fixed vs Flexible shifts: help</SectionTitle>
      </button>
      {open && (
        <div className="text-[12px] mt-2 flex flex-col gap-2" style={{ color: 'var(--sem-fg)' }}>
          <div><span className="font-semibold">Fixed</span> functions (<span className="font-mono text-[11px]">DATEADD</span>, <span className="font-mono text-[11px]">SAMEPERIODLASTYEAR</span>) shift the date <span style={{ color: 'var(--sem-muted)' }}>laterally</span>: one period offset along the date axis, ignorant of any hierarchy.</div>
          <div><span className="font-semibold">Flexible</span> functions do <span style={{ color: 'var(--sem-muted)' }}>hierarchical</span> shifts: they clear filter context by the calendar's category graph (the shifted unit's dependencies and dependents), so a shift respects Year ⊃ Quarter ⊃ Month even when you slice by week.</div>
          <div>Calendar-aware overloads take the <span className="font-semibold">calendar name</span>: <span className="font-mono text-[11px]">TOTALYTD ( [Sales], 'Fiscal' )</span> resolves YTD against the Fiscal calendar's mappings, not the model's default date column.</div>
        </div>
      )}
    </Panel>
  );
}

// ---- classic date table (obsolete approach, kept for classic models) ----
interface GenResult { created?: { ref: string; name: string }[]; skipped?: string[]; note?: string; }
function ClassicDateTable({ tables, setMsg }: { tables: TableModel[]; setMsg: MsgSetter }) {
  const [open, setOpen] = useState(false);
  const [dtName, setDtName] = useState('Date');
  const [dtMark, setDtMark] = useState(true);
  const [dtStart, setDtStart] = useState('');
  const [dtEnd, setDtEnd] = useState('');
  const [adv, setAdv] = useState(false);
  const [busyDt, setBusyDt] = useState(false);
  const fail = (e: unknown) => setMsg({ ok: false, text: String((e as Error).message ?? e) });

  const genDate = async () => {
    const n = dtName.trim(); if (!n) return;
    setBusyDt(true); setMsg(null);
    try {
      const r = await rpc<GenResult>('generateDateTable', n, dtStart.trim() || null, dtEnd.trim() || null, dtMark);
      setMsg({ ok: true, text: r.note || `Date table: ${(r.created?.length ?? 0)} created${r.skipped?.length ? `, ${r.skipped.length} skipped` : ''}.` });
    } catch (e) { fail(e); } finally { setBusyDt(false); }
  };

  const [mdTable, setMdTable] = useState('');
  const mdCols = useMemo(() => tables.find((t) => t.ref === mdTable)?.columns ?? [], [tables, mdTable]);
  const [mdCol, setMdCol] = useState('');
  const markDate = async () => {
    if (!mdTable || !mdCol) return;
    setMsg(null);
    try { await rpc('markDateTable', mdTable, mdCol); setMsg({ ok: true, text: `Marked ${mdTable} as the date table (key '${mdCol}').` }); }
    catch (e) { fail(e); }
  };

  return (
    <Panel>
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 text-left w-full">
        <span className="inline-block w-3 text-[10px]" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
        <SectionTitle>Classic date table</SectionTitle>
        <span className="text-[11px] ml-1" style={{ color: 'var(--sem-muted)' }}>the older Gregorian approach; prefer a calendar above for new models</span>
      </button>
      {open && (
        <div className="flex flex-col gap-3 mt-3">
          <div>
            <div className="text-[11px] mb-1.5" style={{ color: 'var(--sem-muted)' }}>generate a calculated calendar (Year/Quarter/Month/Day…), optionally marked as the model's date table</div>
            <div className="flex items-center gap-2 flex-wrap">
              <TextInput value={dtName} onChange={setDtName} placeholder="table name" style={{ width: 160 }} />
              <label className="flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--sem-fg)' }}>
                <Check checked={dtMark} onChange={() => setDtMark((v) => !v)} /> mark as date table
              </label>
              <button onClick={() => setAdv((v) => !v)} className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{adv ? '▾' : '▸'} range</button>
              <div className="ml-auto"><Button disabled={busyDt || !dtName.trim()} onClick={() => void genDate()}>{busyDt ? 'Generating…' : 'Generate'}</Button></div>
            </div>
            {adv && (
              <div className="flex items-center gap-2 mt-2">
                <TextInput value={dtStart} onChange={setDtStart} placeholder="start DAX (default 5y back)" mono style={{ flex: 1 }} />
                <TextInput value={dtEnd} onChange={setDtEnd} placeholder="end DAX (default end of this year)" mono style={{ flex: 1 }} />
              </div>
            )}
          </div>
          <div>
            <div className="text-[11px] mb-1.5" style={{ color: 'var(--sem-muted)' }}>mark an existing table as the date table</div>
            <div className="flex items-center gap-2">
              <Select value={mdTable} onChange={(v) => { setMdTable(v); setMdCol(''); }} placeholder="table…" options={tables.map((t) => ({ value: t.ref, label: t.name }))} />
              <Select value={mdCol} onChange={setMdCol} placeholder="date/key column…" options={mdCols.map((c) => ({ value: c.name, label: c.name }))} />
              <div className="ml-auto"><Button disabled={!mdTable || !mdCol} onClick={() => void markDate()}>Mark as date table</Button></div>
            </div>
          </div>
        </div>
      )}
    </Panel>
  );
}

// ===================================================================================================
// RLS / OLS — a role designer: model permission, members, per-table row-filter DAX (RLS) + table/
// column object-level security (OLS).
// ===================================================================================================
interface ColPerm { column: string; metadataPermission: string; }
interface ObjPerm { table: string; metadataPermission?: string; columns?: ColPerm[]; }
interface TableFilter { table: string; filterExpression: string; }
interface RoleInfo { name: string; description?: string; modelPermission: string; tableFilters?: TableFilter[]; members?: string[]; objectPermissions?: ObjPerm[]; }
const MODEL_PERMS = ['None', 'Read', 'ReadRefresh', 'Refresh', 'Administrator'];
const OLS_PERMS = ['Default', 'Read', 'None'];

function RlsOlsPanel() {
  const { tables } = useModelObjects();
  const [roles, setRoles] = useState<RoleInfo[] | null>(null);
  const [sel, setSel] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const timer = useRef<number | undefined>(undefined);
  const fail = (e: unknown) => setErr(String((e as Error).message ?? e));

  async function load() {
    try { const r = await rpc<RoleInfo[]>('listRoles'); setRoles(r); setErr(null); setSel((cur) => cur && r.some((x) => x.name === cur) ? cur : (r[0]?.name ?? null)); }
    catch (e) { fail(e); }
  }
  useEffect(() => {
    void load();
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void load(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, []);

  const createRole = () => {
    const n = newName.trim(); if (!n) return;
    void rpc('createRole', n, 'Read').then(() => { setNewName(''); setCreating(false); setSel(n); return load(); }).catch(fail);
  };
  const deleteRole = (name: string) => void rpc('deleteRole', name).then(load).catch(fail);

  if (!roles) return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading roles…'}</div></Panel>;
  const role = roles.find((r) => r.name === sel) ?? null;

  return (
    <div className="flex flex-col gap-4">
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      <div className="grid grid-cols-1 lg:grid-cols-[220px_1fr] gap-4">
        {/* roles rail */}
        <Panel>
          <div className="flex items-center gap-2 mb-2">
            <SectionTitle>Roles</SectionTitle>
            <div className="ml-auto">{creating ? null : <MiniButton onClick={() => setCreating(true)}>+ Role</MiniButton>}</div>
          </div>
          {creating && (
            <div className="flex items-center gap-1 mb-2">
              <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="role name…"
                onKeyDown={(e) => { if (e.key === 'Enter') createRole(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                className="text-[12px] px-2 py-1 rounded outline-none flex-1" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
              <MiniButton disabled={!newName.trim()} onClick={createRole}>Add</MiniButton>
            </div>
          )}
          {roles.length === 0 ? <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No roles yet.</div> : (
            <div className="flex flex-col gap-0.5">
              {roles.map((r) => (
                <button key={r.name} onClick={() => setSel(r.name)} className="flex items-center gap-2 py-1 px-2 rounded text-left text-[12px]"
                  style={{ background: r.name === sel ? 'var(--sem-accent-soft)' : 'transparent', color: 'var(--sem-fg)' }}>
                  <span className="font-medium truncate flex-1">{r.name}</span>
                  <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{(r.tableFilters?.length ?? 0) + (r.objectPermissions?.length ?? 0) || ''}</span>
                </button>
              ))}
            </div>
          )}
        </Panel>

        {/* role detail */}
        {!role ? <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Select or add a role.</div></Panel> : (
          <div className="flex flex-col gap-4">
            <Panel>
              <div className="flex items-center gap-3 flex-wrap">
                <span className="text-[13px] font-semibold">{role.name}</span>
                <label className="text-[11px] flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
                  model permission
                  <Select value={role.modelPermission} onChange={(v) => void rpc('setRolePermission', role.name, v).then(load).catch(fail)}
                    compact options={MODEL_PERMS.map((p) => ({ value: p, label: p }))} />
                </label>
                <div className="ml-auto"><MiniButton onClick={() => deleteRole(role.name)}>Delete role</MiniButton></div>
              </div>
              <Members role={role} reload={load} onError={fail} />
            </Panel>

            <Panel>
              <SectionTitle>Table security: row filter (RLS) + visibility (OLS)</SectionTitle>
              <div className="text-[11px] mt-0.5 mb-2" style={{ color: 'var(--sem-muted)' }}>a DAX row filter restricts ROWS; OLS hides the table/columns entirely (compatibility level 1400+)</div>
              {/* Key by role+table so switching roles REMOUNTS each row and re-seeds its RLS-filter draft from the
                  selected role's server value — otherwise an unsaved draft typed under one role could bleed onto
                  (and be saved against) another role. */}
              <div className="flex flex-col gap-2">
                {tables.map((t) => (
                  <TableSecurityRow key={role.name + '|' + t.ref} role={role} table={t} reload={load} onError={fail} />
                ))}
              </div>
            </Panel>
          </div>
        )}
      </div>
    </div>
  );
}

function Members({ role, reload, onError }: { role: RoleInfo; reload: () => void; onError: (e: unknown) => void }) {
  const [name, setName] = useState('');
  const add = () => { const n = name.trim(); if (!n) return; void rpc('setRoleMember', role.name, n, true).then(() => { setName(''); reload(); }).catch(onError); };
  const remove = (m: string) => void rpc('setRoleMember', role.name, m, false).then(reload).catch(onError);
  return (
    <div className="flex items-center gap-2 mt-2 flex-wrap">
      <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>members</span>
      {(role.members ?? []).map((m) => (
        <span key={m} className="text-[11px] flex items-center gap-1 px-2 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)' }}>
          {m}<button onClick={() => remove(m)} style={{ color: 'var(--sem-muted)' }}>✕</button>
        </span>
      ))}
      <input value={name} onChange={(e) => setName(e.target.value)} placeholder="add member (UPN / group)…"
        onKeyDown={(e) => { if (e.key === 'Enter') add(); }}
        className="text-[11px] px-1.5 py-0.5 rounded outline-none" style={{ width: 180, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      <MiniButton disabled={!name.trim()} onClick={add}>Add</MiniButton>
    </div>
  );
}

function TableSecurityRow({ role, table, reload, onError }: { role: RoleInfo; table: TableModel; reload: () => void; onError: (e: unknown) => void }) {
  const serverFilter = role.tableFilters?.find((f) => f.table === table.name)?.filterExpression ?? '';
  const tableOls = role.objectPermissions?.find((o) => o.table === table.name)?.metadataPermission ?? 'Default';
  const colPerms = role.objectPermissions?.find((o) => o.table === table.name)?.columns ?? [];
  const [filter, setFilter] = useState(serverFilter);
  const [valid, setValid] = useState(true);
  const last = useRef(serverFilter);
  const [open, setOpen] = useState(false);
  const [builder, setBuilder] = useState(false);
  const [bCol, setBCol] = useState('');
  const [colFilter, setColFilter] = useState('');
  useEffect(() => { setFilter((cur) => (cur === last.current ? serverFilter : cur)); last.current = serverFilter; }, [serverFilter]);
  const dirty = filter !== serverFilter;
  const hasFilter = serverFilter.trim().length > 0;

  const saveFilter = () => { if (dirty && valid) void rpc('setTablePermission', role.name, table.ref, filter).then(reload).catch(onError); };
  const setTblOls = (v: string) => void rpc('setTableObjectPermission', role.name, table.ref, v).then(reload).catch(onError);
  const setColOls = (colRef: string, v: string) => void rpc('setColumnObjectPermission', role.name, colRef, v).then(reload).catch(onError);
  const colOlsOf = (name: string) => colPerms.find((c) => c.column === name)?.metadataPermission ?? 'Default';
  // The analyst "build a rule" on-ramp: compose the common RLS predicates without hand-typing DAX. Pro devs
  // ignore this and edit the raw <DaxField> (which already autocompletes this table's columns + the security helpers).
  const rule = (dax: string) => { setFilter(dax); setBuilder(false); };
  const visibleCols = table.columns.filter((c) => !colFilter.trim() || c.name.toLowerCase().includes(colFilter.trim().toLowerCase()));

  return (
    <div className="rounded p-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 shrink-0" title="Column-level security (OLS)">
          <span className="inline-block w-3 text-[10px]" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
          <span className="text-[12px] font-medium">{table.name}</span>
          {hasFilter && <span className="w-1.5 h-1.5 rounded-full" title="Has a row filter" style={{ background: 'var(--sem-accent)' }} />}
        </button>
        <div className="ml-auto flex items-center gap-1.5">
          <MiniButton onClick={() => setBuilder((b) => !b)}>Build a rule ▾</MiniButton>
          <label className="text-[10px] flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>
            OLS
            <Select value={tableOls} onChange={setTblOls} compact options={OLS_PERMS.map((p) => ({ value: p, label: p }))} />
          </label>
        </div>
      </div>

      {builder && (
        <div className="flex items-center gap-2 flex-wrap mt-2 p-2 rounded" style={{ background: 'var(--sem-bg)', border: '1px solid var(--sem-border)' }}>
          <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>restrict rows where</span>
          <Select value={bCol} onChange={setBCol} placeholder="column…" compact options={table.columns.map((c) => ({ value: c.name, label: c.name }))} />
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] = USERPRINCIPALNAME ()`)}>= current user</MiniButton>
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] IN { "Value 1", "Value 2" }`)}>in a list…</MiniButton>
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] = "Value"`)}>= a value…</MiniButton>
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] = LOOKUPVALUE ( Users[${bCol}], Users[Email], USERPRINCIPALNAME () )`)}>via a lookup…</MiniButton>
        </div>
      )}

      <div className="mt-2">
        <DaxField value={filter} onChange={setFilter} scope="rls" table={table.name} minHeight={52}
          placeholder="row filter DAX, e.g. [Region] = USERPRINCIPALNAME()  (blank = all rows)"
          onValidity={(v) => setValid(v)} ariaLabel={`Row filter for ${table.name}`} askContext={`an RLS row-filter for the '${table.name}' table`} />
        <div className="mt-1 flex items-center gap-2">
          <MiniButton disabled={!dirty || !valid} onClick={saveFilter}>{dirty ? (valid ? 'Save filter' : 'Fix errors') : 'Saved'}</MiniButton>
          {dirty && <button onClick={() => setFilter(serverFilter)} className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>revert</button>}
        </div>
      </div>

      {open && (
        <div className="mt-2 pl-6 flex flex-col gap-1">
          <div className="flex items-center gap-2 mb-1">
            <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>column visibility (OLS)</span>
            {table.columns.length > 10 && (
              <input value={colFilter} onChange={(e) => setColFilter(e.target.value)} placeholder="filter columns…" spellCheck={false}
                className="text-[11px] px-1.5 py-0.5 rounded outline-none ml-auto" style={{ width: 160, background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
            )}
          </div>
          {table.columns.length === 0 ? <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>no columns</span> :
            visibleCols.map((c) => (
              <div key={c.ref} className="flex items-center gap-2 text-[11px]">
                <span className="flex-1 truncate" style={{ color: 'var(--sem-fg)' }}>{c.name}</span>
                <Select value={colOlsOf(c.name)} onChange={(v) => setColOls(c.ref, v)} compact options={OLS_PERMS.map((p) => ({ value: p, label: p }))} />
              </div>
            ))}
          {visibleCols.length === 0 && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>no columns match.</span>}
        </div>
      )}
    </div>
  );
}

// ===================================================================================================
// DaxLib package manager — browse daxlib.org (the "app store for DAX UDFs") + install a package's UDFs.
// Browse/detail are anonymous read-only network calls; install is FREE (Kane 2026-07-04 — adoption funnel; one atomic,
// undoable transaction that adds all the package's functions and pulls in its dependencies, deps-first).
// ===================================================================================================
function DaxLibPanel() {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<DaxLibPackage[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [sel, setSel] = useState<string | null>(null);
  const [detail, setDetail] = useState<DaxLibPackageDetail | null>(null);
  const [detailBusy, setDetailBusy] = useState(false);
  const [installed, setInstalled] = useState<DaxLibInstalled[]>([]);
  const [replace, setReplace] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const timer = useRef<number | undefined>(undefined);
  // Monotonic request tokens: each network call (search / package-detail) does independent async I/O with NO
  // ordering guarantee, so a slower earlier response could clobber a newer one. Apply a result only if it is still
  // the latest of its kind — otherwise a stale detail could leave the Install button targeting the wrong package.
  const searchSeq = useRef(0);
  const detailSeq = useRef(0);
  const fail = (e: unknown) => setMsg({ ok: false, text: String((e as Error).message ?? e) });

  const search = async (text: string) => {
    const mine = ++searchSeq.current;
    setSearching(true);
    try { const r = await rpc<DaxLibPackage[]>('daxLibSearch', text.trim(), 0, 0); if (mine !== searchSeq.current) return; setResults(r); }
    catch (e) { if (mine === searchSeq.current) { fail(e); setResults([]); } }
    finally { if (mine === searchSeq.current) setSearching(false); }
  };
  const loadInstalled = async () => {
    try { setInstalled((await rpc<DaxLibInstalled[]>('daxLibListInstalled')) ?? []); }
    catch { setInstalled([]); }   // no model open / none installed — not an error worth a banner
  };
  useEffect(() => {
    void search('');
    void loadInstalled();
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void loadInstalled(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, []);

  const openDetail = async (id: string) => {
    const mine = ++detailSeq.current;
    setSel(id); setDetail(null); setDetailBusy(true); setMsg(null);
    try { const d = await rpc<DaxLibPackageDetail>('daxLibPackageInfo', id, null); if (mine !== detailSeq.current) return; setDetail(d); }
    catch (e) { if (mine === detailSeq.current) fail(e); }
    finally { if (mine === detailSeq.current) setDetailBusy(false); }
  };

  const install = async () => {
    if (!detail) return;
    setBusy(true); setMsg(null);
    try {
      const r = await rpc<DaxLibInstallResult>('daxLibInstall', detail.id, detail.version, replace);
      const n = r.functions?.length ?? 0;
      const parts = [`Installed ${n} function${n === 1 ? '' : 's'} from ${detail.id} ${detail.version}`];
      if (r.skipped?.length) parts.push(`${r.skipped.length} skipped (already present)`);
      if (r.dependenciesInstalled?.length) parts.push(`+${r.dependenciesInstalled.length} dependency package${r.dependenciesInstalled.length === 1 ? '' : 's'}`);
      if (r.warning) parts.push(r.warning);
      setMsg({ ok: true, text: parts.join(' · ') + '.' });
      await loadInstalled();
    } catch (e) { fail(e); }
    finally { setBusy(false); }
  };

  const uninstall = (id: string) => {
    setMsg(null);
    void rpc('daxLibUninstall', id).then(() => { setMsg({ ok: true, text: `Uninstalled ${id}.` }); return loadInstalled(); }).catch(fail);
  };

  const installedIds = useMemo(() => new Set(installed.map((i) => i.packageId.toLowerCase())), [installed]);

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-2">
          <SectionTitle>DaxLib packages</SectionTitle>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            browse daxlib.org (the app store for DAX user-defined functions) and install a package’s functions into this model
          </span>
        </div>
        <div className="flex items-center gap-2 mt-3">
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="search packages (name / description)…" spellCheck={false}
            onKeyDown={(e) => { if (e.key === 'Enter') void search(query); }}
            className="text-[13px] px-2.5 py-1.5 rounded outline-none flex-1" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <Button onClick={() => void search(query)} disabled={searching}>{searching ? 'Searching…' : 'Search'}</Button>
        </div>
        {msg && <div className="mt-3"><Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner></div>}
      </Panel>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* feed */}
        <Panel>
          <SectionTitle>Feed</SectionTitle>
          {!results ? <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
            : results.length === 0 ? <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>No packages found.</div> : (
              <div className="flex flex-col gap-0.5 mt-2 max-h-[420px] overflow-y-auto">
                {results.map((p) => (
                  <button key={p.id + '@' + p.version} onClick={() => void openDetail(p.id)}
                    className="flex flex-col items-start gap-0.5 py-1.5 px-2 rounded text-left"
                    style={{ background: p.id === sel ? 'var(--sem-accent-soft)' : 'transparent' }}>
                    <span className="flex items-center gap-2 w-full">
                      <span className="text-[12px] font-medium truncate flex-1">{p.id}</span>
                      {installedIds.has(p.id.toLowerCase()) && <span className="text-[10px] px-1 rounded" style={{ background: 'var(--sem-good)', color: 'var(--sem-on-accent)' }}>installed</span>}
                      <span className="text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{p.version}</span>
                    </span>
                    {p.description && <span className="text-[11px] truncate w-full" style={{ color: 'var(--sem-muted)' }}>{p.description}</span>}
                  </button>
                ))}
              </div>
            )}
        </Panel>

        {/* detail + install */}
        <Panel>
          {!sel ? <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Select a package to preview its functions and install.</div>
            : detailBusy || !detail ? <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading {sel}…</div> : (
              <div className="flex flex-col gap-2">
                <div className="flex items-center gap-2">
                  <span className="text-[14px] font-semibold truncate">{detail.id}</span>
                  <span className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{detail.version}</span>
                  {detail.projectUrl && <a href={detail.projectUrl} target="_blank" rel="noreferrer" className="text-[11px]" style={{ color: 'var(--sem-accent)' }}>↗ project</a>}
                </div>
                {detail.description && <div className="text-[12px]" style={{ color: 'var(--sem-fg)' }}>{detail.description}</div>}
                {detail.authors?.length ? <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>by {detail.authors.join(', ')}</div> : null}
                {detail.dependencies?.length ? (
                  <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>depends on: {detail.dependencies.map((d) => d.id + (d.version ? ' ' + d.version : '')).join(', ')}</div>
                ) : null}
                <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>
                  {detail.functionCount ?? detail.functionNames?.length ?? 0} function(s), added to the model on install:
                </div>
                <div className="flex flex-wrap gap-1 max-h-[180px] overflow-y-auto">
                  {(detail.functionNames ?? []).map((fn) => (
                    <span key={fn} className="text-[11px] px-1.5 py-0.5 rounded font-mono" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>{fn}</span>
                  ))}
                </div>
                <div className="flex items-center gap-2 mt-2">
                  <label className="flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--sem-fg)' }}>
                    <Check checked={replace} onChange={() => setReplace((v) => !v)} /> overwrite existing
                  </label>
                  <div className="ml-auto flex items-center gap-1.5">
                    <Button primary disabled={busy} onClick={() => void install()}>{busy ? 'Installing…' : 'Install'}</Button>
                  </div>
                </div>
              </div>
            )}
        </Panel>
      </div>

      {installed.length > 0 && (
        <Panel>
          <SectionTitle>Installed in this model</SectionTitle>
          <div className="flex flex-col gap-1 mt-2">
            {installed.map((i) => (
              <div key={i.packageId} className="flex items-center gap-2 py-1 px-2 rounded" style={{ background: 'var(--sem-surface-2)' }}>
                <span className="text-[12px] font-medium">{i.packageId}</span>
                <span className="text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{i.version}</span>
                <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>· {i.functions?.length ?? 0} function(s)</span>
                <div className="ml-auto"><MiniButton onClick={() => uninstall(i.packageId)}>Uninstall</MiniButton></div>
              </div>
            ))}
          </div>
        </Panel>
      )}
    </div>
  );
}

// ---- local primitives -----------------------------------------------------------------------------
function Panel({ children }: { children: React.ReactNode }) {
  return <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <span className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{children}</span>;
}
function Button({ children, onClick, primary, disabled }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
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
function Seg({ children, active, onClick }: { children: React.ReactNode; active?: boolean; onClick?: () => void }) {
  return (
    <button onClick={onClick} className="text-[12px] px-3 py-1 rounded-lg font-medium transition-colors"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px] mb-3" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color }}>{children}</div>;
}
function TextInput({ value, onChange, placeholder, mono, style }: { value: string; onChange: (v: string) => void; placeholder?: string; mono?: boolean; style?: React.CSSProperties }) {
  return (
    <input value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} spellCheck={false}
      className={'text-[12px] px-2 py-1 rounded outline-none ' + (mono ? 'font-mono' : '')}
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', ...style }} />
  );
}
function Select({ value, onChange, placeholder, options, compact }: { value: string; onChange: (v: string) => void; placeholder?: string; options: { value: string; label: string }[]; compact?: boolean }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)}
      className="text-[12px] px-2 py-1 rounded outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', minWidth: compact ? 0 : 180 }}>
      {placeholder !== undefined && <option value="" disabled>{placeholder}</option>}
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}
function Check({ checked, onChange }: { checked: boolean; onChange: () => void }) {
  return (
    <button onClick={onChange} aria-checked={checked} role="checkbox"
      className="w-4 h-4 rounded inline-flex items-center justify-center text-[10px] transition-colors"
      style={{ background: checked ? 'var(--sem-accent)' : 'transparent', color: '#fff', border: '1px solid ' + (checked ? 'var(--sem-accent)' : 'var(--sem-border)') }}>
      {checked ? '✓' : ''}
    </button>
  );
}
