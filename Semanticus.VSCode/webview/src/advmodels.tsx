import { lazy, memo, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { rpc, onDidChange } from './bridge';
import { useFillHeight } from './hooks';
import { DaxField } from './daxeditor';
import { inferFormatMode } from './formatMode';
import { ObjectBrowser, useBrowserData, type BrowserNode } from './objectbrowser';
import { Caret } from './ui';
import { DraftMark, EditMovedBanner, sameValue, useEditBase, useHoldDraft } from './editbase';
import { draftModel, draftStore, heldDraft } from './drafts';
import { useKeptText } from './keptwords';
import { OrphanDrafts } from './orphandrafts';
import { GiveToAssistant, type PlanTaskRequest } from './sendtask';
import { MenuPop } from './menupop';

// Power Query is one of this page's sub-pages (Kane, 8 Oct), so its page loads when its button is pressed, not with the others.
// Made on first use, not at import, so loading this file asks for nothing.
let mcodeLazy: React.LazyExoticComponent<typeof import('./mcode').MCodeView> | null = null;
const lazyMCode = () => (mcodeLazy ??= lazy(() => import('./mcode').then((m) => ({ default: m.MCodeView }))));

// ===================================================================================================
// Advanced Modelling tab (Studio v2). A killer-UX authoring surface for constructs reachable today
// only via the tree + property grid + raw DAX. v1 ships the two areas whose engine primitives are
// newest — Perspectives (the objects×perspectives membership matrix) and a Field-parameter builder —
// both dual-drive: every action calls a typed engine op (origin='human') the user's Claude can also
// drive over MCP. Calc groups / RLS-OLS / calendars panels follow.
// ===================================================================================================

interface PerspectiveInfo { ref: string; name: string; description?: string; members: string[]; }
interface MeasureRow { ref: string; name: string; table: string; }
interface ColumnRow { ref: string; name: string; table: string; isHidden?: boolean; dataType?: string; }
type TableModel = { name: string; ref: string; columns: ColumnRow[]; measures: MeasureRow[]; isCalculationGroup?: boolean };
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

type Area = 'mcode' | 'calendars' | 'calcgroups' | 'fieldparams' | 'rls' | 'perspectives' | 'daxlib';
// ONE order, for the buttons and for everything that jumps here (Kane, 8 Oct): Power Query first, then the rest, most used first.
const AREA_TABS: [Area, string][] = [
  ['mcode', 'Power Query'], ['calendars', 'Calendars'], ['calcgroups', 'Calculation groups'], ['fieldparams', 'Field parameters'],
  ['rls', 'RLS / OLS'], ['perspectives', 'Perspectives'], ['daxlib', 'DaxLib'],
];
const AREAS: Area[] = AREA_TABS.map(([id]) => id);
// Every sub-page has ONE stable id, and these are the older spellings of them. The Plan and the ask box
// jump straight to a sub-page by id, so an id that silently missed would drop the person on whichever
// sub-page was open last with nothing to say it had ignored them.
const AREA_ALIASES: Record<string, Area> = { rlsols: 'rls' };
export function advancedAreaOf(id: string): Area | null {
  if ((AREAS as string[]).includes(id)) return id as Area;
  return AREA_ALIASES[id] ?? null;
}

// What each sub-page gives the assistant, in plain words. One task per page: the assistant reads the model, says what it would
// change, and waits for the person's yes before it writes (undo is the safety net after that).
const AREA_TASKS: Record<Area, { words: string; brief: string }> = {
  mcode: { words: 'Help with Power Query', brief: 'I want help with the Power Query (M) queries in this model. Read the queries, tell me what you would change, and wait for my yes before you write anything.' },
  calendars: { words: 'Set up calendars', brief: 'I want help with calendars in this model. Look at the date columns and any calendars that exist, tell me what you would add, and wait for my yes before you write anything.' },
  calcgroups: { words: 'Set up calculation groups', brief: 'I want help with calculation groups in this model, such as year to date or percent of total. Tell me what you would add, and wait for my yes before you write anything.' },
  fieldparams: { words: 'Set up a field parameter', brief: 'I want help with a field parameter in this model, a slicer that switches which measures or columns a visual shows. Ask me which fields to include, and wait for my yes before you write anything.' },
  rls: { words: 'Set up row security', brief: 'I want help with row security (RLS) and visibility (OLS) in this model. Look at the roles and tables, tell me what you would change, and wait for my yes before you write anything.' },
  perspectives: { words: 'Set up perspectives', brief: 'I want help with perspectives in this model. Tell me which named views would help the people who use it, and wait for my yes before you write anything.' },
  daxlib: { words: 'Add DaxLib functions', brief: 'I want help finding ready-made DAX functions on daxlib.org that suit this model. Tell me which packages you would install, and wait for my yes before you install anything.' },
};
const areaTask = (area: Area): PlanTaskRequest => ({
  words: AREA_TASKS[area].words, brief: AREA_TASKS[area].brief, sourcePage: 'advmodels', objects: [],
  doneWhen: 'assistant_says', originLink: { kind: 'page', ref: 'advmodels' },
});

export function AdvancedModelsView({ navArea, onNavConsumed, mcodeTarget }: {
  navArea?: { area: string; nonce: number } | null; onNavConsumed?: (nonce: number) => void;
  /** A Model-tree "Edit M code" jump: the table (and partition) the Power Query page opens on. */
  mcodeTarget?: { table: string; partitionId?: string; nonce: number } | null;
} = {}) {
  const [area, setArea] = useState<Area>('mcode');
  // A Model-tree "Advanced Modelling ▸ New …" jump lands here on the right builder. Guard the string against the
  // Area union (defensive — the nav payload is external); nonce makes a repeat jump to the same area re-fire.
  useEffect(() => {
    if (!navArea) return;
    const wanted = advancedAreaOf(navArea.area);
    if (wanted) setArea(wanted);
    onNavConsumed?.(navArea.nonce);
  }, [navArea?.nonce]);  // eslint-disable-line react-hooks/exhaustive-deps
  // An "Edit M code" jump has a table in it: the page is Power Query, whichever sub-page was open.
  useEffect(() => { if (mcodeTarget?.table) setArea('mcode'); }, [mcodeTarget?.nonce]);  // eslint-disable-line react-hooks/exhaustive-deps
  const strip = (
    <div className="sem-inline">
      <div className="sem-seg flex-wrap" aria-label="Advanced modelling">
        {AREA_TABS.map(([id, label]) => <Seg key={id} active={area === id} onClick={() => setArea(id)}>{label}</Seg>)}
      </div>
      {/* The assistant is the first door to a page of modelling (D4): one Give per sub-page, not one per field. */}
      <div className="ml-auto"><GiveToAssistant key={area} sendKey={`advmodels/${area}`} build={() => areaTask(area)} /></div>
    </div>
  );
  // Power Query brings its own full-height page, so it sits under the strip instead of inside the grid.
  if (area === 'mcode') {
    const MCodeView = lazyMCode();
    return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="sem-page shrink-0" style={{ paddingBottom: 0 }}>{strip}</div>
      <div className="min-h-0 flex-1"><Suspense fallback={<div className="sem-page"><div className="sem-text-meta">Loading Power Query…</div></div>}><MCodeView navTarget={mcodeTarget} /></Suspense></div>
    </div>
    );
  }
  return (
    // Instrument (23 September): the header card that only repeated the tab's name is gone. The sub-page switch
    // sits on the page as a strip, and the builder is the one card under it.
    <div className="sem-page">
      {strip}
      {area === 'perspectives' ? <PerspectivesPanel />
        : area === 'fieldparams' ? <FieldParamsPanel />
        : area === 'calcgroups' ? <CalcGroupsPanel />
        : area === 'calendars' ? <CalendarsPanel />
        : area === 'rls' ? <RlsOlsPanel />
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
      const [cols, meas, objs] = await Promise.all([
        rpc<ColumnRow[]>('listColumns'),
        rpc<MeasureRow[]>('listMeasures'),
        rpc<{ tables: { name: string; isCalculationGroup?: boolean }[] }>('getModelObjects').catch(() => ({ tables: [] as { name: string; isCalculationGroup?: boolean }[] })),
      ]);
      const calc = new Set((objs?.tables ?? []).filter((t) => t.isCalculationGroup).map((t) => t.name));
      const by = new Map<string, TableModel>();
      const tbl = (name: string) => {
        let t = by.get(name);
        if (!t) { t = { name, ref: 'table:' + name, columns: [], measures: [], isCalculationGroup: calc.has(name) }; by.set(name, t); }
        return t;
      };
      for (const c of cols ?? []) tbl(c.table).columns.push(c);
      for (const m of meas ?? []) tbl(m.table).measures.push(m);
      for (const n of calc) tbl(n);
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
// The object×perspective membership matrix — rebuilt for large models (Issue 2). Two performance fixes, one honesty fix:
// (1) OPTIMISTIC toggle: the check flips in local state instantly; the op fires in the background; success reloads
//     so a table cascade shows every child tick and the header counts member objects, not ticked rows.
// (2) didChange always reloads (debounced): undo and an agent edit must redraw, never skip the echo.
// (3) VIRTUALIZED + MEMOIZED: the <table> becomes a virtualized row list (constant DOM) with a React.memo'd cell,
//     so a toggle re-renders exactly one cell — not tens of thousands.
const P_ROW_H = 30, P_LABEL_W = 260, P_COL_W = 96;
const P_GLYPH: Record<string, { g: string; color: string }> = {
  // The glyph's shape says the kind; the colour is the muted text colour (Instrument).
  measure: { g: 'ƒ', color: 'var(--sem-muted)' }, column: { g: '▦', color: 'var(--sem-muted)' }, hierarchy: { g: '⛼', color: 'var(--sem-muted)' },
};
type PRow = { kind: 'table'; name: string; ref: string; count: number; open: boolean } | { kind: 'obj'; node: BrowserNode };

// One membership checkbox. Memoized on (checked, stable onToggle) so a toggle re-renders only the flipped cell.
const PerspCell = memo(function PerspCell({ pRef, objRef, checked, label, onToggle }: {
  pRef: string; objRef: string; checked: boolean; label: string; onToggle: (p: string, o: string, next: boolean) => void;
}) {
  return (
    <button onClick={() => onToggle(pRef, objRef, !checked)} role="checkbox" aria-checked={checked} aria-label={label}
      className={'xe-check' + (checked ? ' is-on' : '')}>
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
  const timer = useRef<number | undefined>(undefined);
  const parentRef = useRef<HTMLDivElement>(null);
  // The space rule: the tick grid runs to the page bottom and its columns share the card's width, instead of a
  // 520 px box of 96 px columns in a quarter of the card.
  const [fillRef, fillH] = useFillHeight<HTMLDivElement>(320, 16, persp?.length ?? -1);
  const [boxW, setBoxW] = useState(0);
  useEffect(() => {
    const el = fillRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setBoxW(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, [fillRef, persp?.length]);

  const loadPersp = useCallback(async () => {
    try { setPersp(await rpc<PerspectiveInfo[]>('getPerspectives')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }, []);
  useEffect(() => {
    void loadPersp();
    const off = onDidChange(() => {
      // Always reload: including a table cascades to its fields, and undo must redraw the ticks and the count.
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
    void rpc('setPerspectiveMember', pRef, objRef, include)
      .then(() => loadPersp())   // server membership includes the table cascade, so ticks and the count stay honest
      .catch((e) => {
        setErr(String((e as Error).message ?? e));
        void loadPersp();
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

  if (!persp) return <Panel><div className="sem-text-meta">{err ?? 'Loading perspectives…'}</div></Panel>;

  const cols = persp;
  // Each perspective takes a share of the card's width (the space rule, ratified 27 September 2026), with 96 px as the
  // floor: past that the grid is wider than the card and scrolls sideways inside it. A 240 px cap was the first try,
  // and it left a two-perspective model using 39% of the card with the rest blank, which is the fault the rule names.
  const colW = cols.length ? Math.max(P_COL_W, Math.floor((boxW - P_LABEL_W) / cols.length)) : P_COL_W;
  const totalW = P_LABEL_W + cols.length * colW;

  return (
    <Panel>
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      <div className="flex items-start gap-3 mb-3 flex-wrap">
        <div className="sem-stack xe-grow">
          <SectionTitle>Perspectives</SectionTitle>
          <span className="sem-text-meta">
            A named view of the model. It does not limit who sees data.
          </span>
        </div>
        <div className="flex items-center gap-2">
          {cols.length > 0 && (
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="filter objects…" spellCheck={false}
              className="sem-field" style={{ width: 160 }} />
          )}
          {creating ? (
            <span className="flex items-center gap-1">
              <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="perspective name…"
                onKeyDown={(e) => { if (e.key === 'Enter') createPerspective(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                className="sem-field" style={{ width: 170 }} />
              <MiniButton disabled={!newName.trim()} onClick={createPerspective}>Add</MiniButton>
            </span>
          ) : <Button onClick={() => setCreating(true)}>+ Perspective</Button>}
        </div>
      </div>

      {cols.length === 0 ? (
        <div className="sem-text-meta">No perspectives yet. Add one, give it a name and choose the fields to show.</div>
      ) : (
        <div ref={(el) => { parentRef.current = el; fillRef.current = el; }} className="overflow-auto" style={{ maxHeight: fillH, width: '100%' }}>
          <div style={{ minWidth: totalW, position: 'relative' }}>
            {/* sticky perspective (column) headers */}
            <div className="sticky top-0 z-10 flex" style={{ background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)', height: P_ROW_H + 8 }}>
              <div className="px-2 py-1 sem-text-meta flex items-center" style={{ width: P_LABEL_W }}>Object</div>
              {cols.map((p) => (
                <div key={p.ref} className="flex flex-col items-center justify-center px-1" style={{ width: colW }}>
                  <span className="sem-text-meta truncate max-w-full" style={{ color: 'var(--sem-fg)' }} title={p.name}>{p.name}</span>
                  <span className="flex items-center gap-1">
                    <span className="sem-text-meta sem-num" title="objects included">{p.members.length}</span>
                    <button onClick={() => removePerspective(p.ref)} title="Delete perspective" aria-label={`Delete perspective ${p.name}`} className="sem-text-meta">✕</button>
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
                    <div key={vr.key} className="flex items-center" style={{ ...common, borderTop: '1px solid var(--sem-rule)' }}>
                      <button onClick={() => toggleExpand(row.name)} className="flex items-center gap-2 text-left px-2" style={{ width: P_LABEL_W }}>
                        <span className="inline-flex w-3 justify-center"><Caret open={row.open} /></span>
                        <span className="text-xs font-medium truncate">{row.name}</span>
                        <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>({row.count})</span>
                      </button>
                      {cols.map((p) => (
                        <div key={p.ref} className="flex items-center justify-center" style={{ width: colW }}>
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
                    <div className="flex items-center gap-2 pl-8 pr-2 text-xs truncate" style={{ width: P_LABEL_W, opacity: n.isHidden ? 0.55 : 1 }}>
                      <span style={{ color: gl.color }}>{gl.g}</span><span className="truncate">{n.name}</span>
                    </div>
                    {cols.map((p) => (
                      <div key={p.ref} className="flex items-center justify-center" style={{ width: colW }}>
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
  // The space rule: the picker runs to the page bottom (380 px is the floor), and Selected fields stretches beside it.
  const [pickerRef, pickerH] = useFillHeight<HTMLDivElement>(380);
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
      await rpc<string>('createFieldParameter', n, items);
      setMsg({ ok: true, text: `Created table ${n} with ${picked.length} field${picked.length === 1 ? '' : 's'}.` });
      setName(''); setPicked([]);
    } catch (e) { setMsg({ ok: false, text: String((e as Error).message ?? e) }); }
    finally { setBusy(false); }
  };

  return (
    <div className="flex flex-col gap-3">
      <Panel>
        <div className="flex items-center gap-2 mb-3">
          <SectionTitle>New field parameter</SectionTitle>
          <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>A slicer that switches which measures or columns a visual shows.</span>
        </div>
        <div className="flex items-center gap-2 mb-3">
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="parameter name (e.g. 'Metric')…"
            className="sem-field flex-1" />
          <Button primary disabled={busy || !name.trim() || picked.length === 0} onClick={() => void create()}>
            {busy ? 'Creating…' : 'Create'}
          </Button>
        </div>
        {msg && <Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner>}
      </Panel>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
        <Panel>
          <SectionTitle>Selected fields (in order)</SectionTitle>
          {picked.length === 0 ? (
            <div className="text-xs mt-2" style={{ color: 'var(--sem-muted)' }}>Nothing picked yet.</div>
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
                  <span className="text-(length:--sem-text-label) tnum w-4 text-center" style={{ color: 'var(--sem-muted)' }}>{i + 1}</span>
                  <span className="shrink-0" style={{ color: 'var(--sem-muted)' }}>{p.kind === 'measure' ? 'ƒ' : '▦'}</span>
                  <input value={p.label} onChange={(e) => relabel(p.ref, e.target.value)} title={`${p.name} · ${p.table} · slicer label`}
                    className="sem-field sem-field-sm flex-1 min-w-0" />
                  <span className="text-(length:--sem-text-label) shrink-0 truncate max-w-[90px]" style={{ color: 'var(--sem-muted)' }} title={p.table}>{p.table}</span>
                  <button onClick={() => move(i, -1)} disabled={i === 0} className="text-(length:--sem-text-label) disabled:opacity-30" title="Up">↑</button>
                  <button onClick={() => move(i, 1)} disabled={i === picked.length - 1} className="text-(length:--sem-text-label) disabled:opacity-30" title="Down">↓</button>
                  <button onClick={() => remove(p.ref)} className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }} title="Remove">✕</button>
                </div>
              ))}
            </div>
          )}
        </Panel>

        <Panel>
          <SectionTitle>Add a measure or column</SectionTitle>
          <div className="mt-2" ref={pickerRef}>
            <ObjectBrowser kinds={['measure', 'column']} multiSelect dragEnabled pickedRefs={pickedRefs} height={pickerH}
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

const NEW_ITEM_EXPR = 'CALCULATE ( SELECTEDMEASURE () )';

export function CalcGroupsPanel() {
  const [groups, setGroups] = useState<CalcGroupInfo[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [movedWords, setMovedWords] = useState(0);   // bumped when typed words are moved onto another item: the cards open again and read them
  const timer = useRef<number | undefined>(undefined);
  // A card keeps its open forms across a rename of its group (join 11 solui round 2, U2 c): the ref is the group's name, so a rename is a new ref. A gone ref and a new ref
  // read as one rename when they pair up with nobody else: first by the very same item names (each side the only match of the other, so two renames in one reload pair up too),
  // then, when as many gone as new groups are left, one to one in list order (round 3: a group renamed together with its items in one reload has no item name in common, and the page
  // cannot tell that from one group deleted and another created; the words are held in the draft store either way, so the key only keeps focus and the open form). Anything else is a new card.
  const cardKeys = useRef(new Map<string, { key: string; items: string[] }>());
  const cardSeq = useRef(0);
  const keyFor = (gs: CalcGroupInfo[]): { keys: Map<string, string>; renames: Array<[string, string]> } => {
    const itemsOf = (g: CalcGroupInfo) => g.items.map((i) => i.name);
    const sameItems = (a: string[], b: string[]) => a.length === b.length && a.every((n) => b.includes(n));
    const before = cardKeys.current, next = new Map<string, { key: string; items: string[] }>();
    const fresh = gs.filter((g) => !before.has(g.ref));
    const gone = [...before.entries()].filter(([ref]) => !gs.some((g) => g.ref === ref));
    const paired = new Map<string, string>(), used = new Set<string>();   // new ref -> the card key it keeps; the gone refs already paired
    const renames: Array<[string, string]> = [];   // [gone ref, new ref]: the words held under the old ref move to the new one
    const pair = (g: CalcGroupInfo, [ref, v]: [string, { key: string; items: string[] }]) => { paired.set(g.ref, v.key); used.add(ref); renames.push([ref, g.ref]); };
    for (const g of fresh) {
      const same = gone.filter(([, v]) => sameItems(v.items, itemsOf(g)));
      if (same.length !== 1) continue;
      if (fresh.filter((f) => sameItems(same[0][1].items, itemsOf(f))).length === 1) pair(g, same[0]);
    }
    const freshLeft = fresh.filter((g) => !paired.has(g.ref)), goneLeft = gone.filter(([ref]) => !used.has(ref));
    if (freshLeft.length > 0 && freshLeft.length === goneLeft.length) freshLeft.forEach((g, i) => pair(g, goneLeft[i]));
    for (const g of gs) next.set(g.ref, { key: before.get(g.ref)?.key ?? paired.get(g.ref) ?? `g${++cardSeq.current}`, items: itemsOf(g) });
    cardKeys.current = next;
    return { keys: new Map([...next.entries()].map(([ref, v]) => [ref, v.key])), renames };
  };
  const [keys, setKeys] = useState(new Map<string, string>());

  async function load() {
    try {
      const gs = await rpc<CalcGroupInfo[]>('listCalculationGroups');
      const { keys: next, renames } = keyFor(gs);
      // A card that keeps its key across a rename now reads its words under the new ref: move them there BEFORE the card draws (round 3), so nothing is written under the new ref first.
      const m = draftModel();
      if (m) for (const [from, to] of renames) draftStore.moveScope(m, `calcgroup|${from}`, `calcgroup|${to}`);
      setKeys(next); setGroups(gs); setErr(null);
    }
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

  if (!groups) return <Panel><div className="text-xs" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading calculation groups…'}</div></Panel>;

  return (
    <div className="flex flex-col gap-3">
      <Panel>
        {err && <Banner color="var(--sem-bad)">{err}</Banner>}
        <div className="flex items-center gap-2">
          <SectionTitle>Calculation groups</SectionTitle>
          <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
            Reuse one calculation across many measures, like year to date.
          </span>
          <div className="ml-auto">
            {creating ? (
              <span className="flex items-center gap-1">
                <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="group name…"
                  onKeyDown={(e) => { if (e.key === 'Enter') createGroup(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                  className="sem-field" style={{ width: 170 }} />
                <MiniButton disabled={!newName.trim()} onClick={createGroup}>Add</MiniButton>
              </span>
            ) : <Button onClick={() => setCreating(true)}>+ Group</Button>}
          </div>
        </div>
        {groups.length === 0 && <div className="text-(length:--sem-text-body) mt-3" style={{ color: 'var(--sem-muted)' }}>No calculation groups yet. Add one (it becomes a special single-column table).</div>}
      </Panel>

      {/* Words typed into an item that is not listed now (an item has no id that survives a rename, so a rename looks the same as a delete): kept, and asked about (join 11, W3). */}
      <OrphanDrafts prefix="calcitem|" live={groups.flatMap((g) => g.items.map((i) => i.ref))} what="calculation item"
        nameOf={(ref) => ref.replace(/^calcitem:/, '').replace('/', ' / ')}
        targetsFor={() => groups.flatMap((g) => g.items.map((i) => ({ id: i.ref, label: `${g.name} / ${i.name}` })))}
        onMoved={() => setMovedWords((n) => n + 1)} />

      {/* Words typed into a group's precedence box or its + Item form for a group that is not listed now (a group renamed on its own, or deleted): kept, and asked about. */}
      <OrphanDrafts prefix="calcgroup|" live={groups.map((g) => g.ref)} what="calculation group"
        nameOf={(ref) => ref.replace(/^table:/, '')}
        targetsFor={() => groups.map((g) => ({ id: g.ref, label: g.name }))}
        onMoved={() => setMovedWords((n) => n + 1)} />

      {groups.map((g) => <CalcGroupCard key={`${keys.get(g.ref) ?? g.ref}:${movedWords}`} group={g} reload={load} onError={fail} />)}
    </div>
  );
}

function CalcGroupCard({ group, reload, onError }: { group: CalcGroupInfo; reload: () => void; onError: (e: unknown) => void }) {
  // Words typed in the precedence box stay when the assistant sets another value underneath them (R-B): the newer value is named under the box, and a blur is not a decision.
  // Round 3: the precedence words and the + Item form are held in the draft store under the group (`calcgroup|<ref>`), so a card that is drawn anew (a rename the page could not pair, a swapped tab, a restart) opens on them.
  const scope = `calcgroup|${group.ref}`;
  const { text: prec, setText: setPrec, moved: precMoved, useLive: usePrecLive } = useKeptText(String(group.precedence), undefined, { hold: { scope, field: 'precedence', label: group.name } });
  const [heldItem] = useState(() => heldDraft<{ name: string; expr: string; fmt: string }, null>(scope, 'item')?.value);
  const [adding, setAdding] = useState(!!heldItem);
  const [iName, setIName] = useState(heldItem?.name ?? '');
  const [iExpr, setIExpr] = useState(heldItem?.expr ?? NEW_ITEM_EXPR);
  const [iFmt, setIFmt] = useState(heldItem?.fmt ?? '');
  const [iValid, setIValid] = useState(true);
  const form = useRef({ iName, iExpr, iFmt });   // what the form holds when an add comes back
  form.current = { iName, iExpr, iFmt };
  useEffect(() => {
    const m = draftModel();
    if (!m) return;
    if (adding && (iName !== '' || iFmt !== '' || iExpr !== NEW_ITEM_EXPR)) draftStore.set(m, scope, 'item', { value: { name: iName, expr: iExpr, fmt: iFmt }, base: null, label: group.name });
    else draftStore.clear(m, scope, 'item');   // Cancel, or an add the engine took
  }, [adding, iName, iExpr, iFmt, scope]);   // eslint-disable-line react-hooks/exhaustive-deps

  const commitPrec = (decided = false) => {
    if (precMoved !== null && !decided) return;   // the box holds words typed over a value that has since moved: Keep mine or Use the new one decides
    const n = parseInt(prec, 10);
    if (!Number.isFinite(n) || n === group.precedence) { setPrec(String(group.precedence)); return; }
    // The write names the precedence the box started from; the engine compares it inside its own write.
    void rpc('setCalcGroupPrecedence', group.ref, n, 'human', String(group.precedence)).then(reload).catch(onError);
  };
  const reset = () => { setIName(''); setIExpr(NEW_ITEM_EXPR); setIFmt(''); setAdding(false); };
  const addItem = () => {
    const n = iName.trim(); if (!n || !iValid) return;
    // create the item, then (if a template supplied a format) set its format string in a follow-up op.
    void rpc<string>('createCalculationItem', group.ref, n, iExpr)
      .then((newRef) => (iFmt && newRef ? rpc('setCalcItemFormatString', newRef, `"${iFmt}"`, 'human', '') : Promise.resolve()))
      // R-B: the form ends its words only when the engine took the add AND the form still holds exactly the words that were sent (words typed while it was on its way stay).
      .then(() => { const f = form.current; if (f.iName.trim() === n && f.iExpr === iExpr && f.iFmt === iFmt) reset(); reload(); }).catch(onError);
  };
  const useTemplate = (t: typeof CALC_ITEM_TEMPLATES[number]) => { if (!iName.trim()) setIName(t.label); setIExpr(t.expr); setIFmt(t.fmt ?? ''); };
  const delGroup = () => void rpc('deleteObject', group.ref).then(reload).catch(onError);

  return (
    <Panel>
      <div className="flex items-center gap-2 mb-2">
        <span className="text-(length:--sem-text-body) font-semibold">{group.name}</span>
        <label className="text-(length:--sem-text-label) flex items-center gap-1 ml-2" style={{ color: 'var(--sem-muted)' }} title="Order. A higher number applies first when calculation groups are combined.">
          Order
          <input value={prec} onChange={(e) => setPrec(e.target.value)} onBlur={() => commitPrec()}
            onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
            inputMode="numeric" className="sem-field sem-field-sm w-16 tnum"
 />
        </label>
        {precMoved !== null && <span className="text-(length:--sem-text-label)" role="alert" data-testid="calc-precedence-moved" style={{ color: 'var(--sem-warn)' }}>
          The precedence is {precMoved} now. Your {prec} is kept. <MiniButton onClick={() => commitPrec(true)}>Keep mine</MiniButton> <MiniButton onClick={usePrecLive}>Use {precMoved}</MiniButton>
        </span>}
        <div className="ml-auto flex items-center gap-2">
          {adding ? null : <MiniButton onClick={() => setAdding(true)}>+ Item</MiniButton>}
          <MiniButton onClick={delGroup}>Delete group</MiniButton>
        </div>
      </div>

      {adding && (
        <div className="flex flex-col gap-2 mb-3 p-2 rounded" style={{ background: 'var(--sem-surface-2)' }}>
          <div className="flex items-center gap-2 flex-wrap">
            <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>templates:</span>
            {CALC_ITEM_TEMPLATES.map((t) => (
              <button key={t.label} onClick={() => useTemplate(t)} title={t.expr}
                className="sem-btn sem-btn-sm">{t.label}</button>
            ))}
          </div>
          <input autoFocus value={iName} onChange={(e) => setIName(e.target.value)} placeholder="item name (e.g. 'YTD')…"
            className="sem-field" />
          <DaxField value={iExpr} onChange={setIExpr} scope="calcitem" minHeight={72} onValidity={(v) => setIValid(v)}
            ariaLabel="New calculation item expression" askContext="a calculation-item expression that wraps SELECTEDMEASURE()" />
          <FormatStringControl value={iFmt ? `"${iFmt}"` : ''} onSave={(raw) => setIFmt(raw.replace(/^"|"$/g, ''))} inline />
          <div className="flex items-center gap-2">
            <MiniButton disabled={!iName.trim() || !iValid} onClick={addItem}>Add item</MiniButton>
            <MiniButton onClick={reset}>Cancel</MiniButton>
          </div>
        </div>
      )}

      {group.items.length === 0 ? (
        <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No items yet. Add one (its DAX wraps SELECTEDMEASURE()).</div>
      ) : (
        <div className="flex flex-col gap-3">
          {group.items.map((it) => <CalcItemRow key={it.ref} item={it} reload={reload} onError={onError} />)}
        </div>
      )}
    </Panel>
  );
}

export function CalcItemRow({ item, reload, onError }: { item: CalcItemInfo; reload: () => void; onError: (e: unknown) => void }) {
  // Words typed here and not saved are held above the row (drafts.ts, the drafts class): a tab switch, a restarted engine and a reload keep them.
  const heldExpr = heldDraft<string>(`calcitem|${item.ref}`, 'expression'), heldFmt = heldDraft<string>(`calcitem|${item.ref}`, 'format');
  const [expr, setExpr] = useState(heldExpr ? heldExpr.value : (item.expression ?? ''));
  const [valid, setValid] = useState(true);
  // Adopt an external (other-door / agent) edit to THIS item ONLY when the user has no pending local edit,
  // so a live didChange refreshes a row you're merely viewing and never clobbers a draft you're typing. A draft that is
  // still there when the item moved is NOT saved over the other change: the guard asks first (UAT stale-copy class).
  // Renames remount the row (ref-keyed) → server wins.
  // Save reads the item's expression now and the write names it as the value it replaces: the engine compares it inside its own mutation (Sol join 6, C4).
  const guard = useEditBase<string>({ current: item.expression ?? '', draft: expr, editing: true, reseed: setExpr, persist: { scope: `calcitem|${item.ref}`, field: 'expression' },
    readNow: async () => (await rpc<string | null>('getDax', item.ref)) ?? '' });
  const dirtyExpr = expr !== (item.expression ?? '');

  const saveExpr = () => {
    if (!dirtyExpr || !valid) return;
    // R-B: the write carries `expr`; the draft ends only if the words held are still exactly those (guard.saved), and words typed while it was on its way stay.
    void guard.beforeSave().then((ok) => ok ? rpc('setDax', item.ref, expr, 'human', null, null, guard.confirmed() ?? '').then(() => { guard.saved(expr); reload(); }) : undefined)
      .catch(async (e) => { if (!(await guard.refused(e))) onError(e); });
  };
  // The format has its own start, read and fence, the same as the expression above: its draft lives here (the control only shows it), so a
  // change from the other door while words are typed shows the Keep mine / Use the new one choice and never replaces them (Sol join 6, round 2).
  const [fmt, setFmt] = useState(heldFmt ? heldFmt.value : (item.formatStringExpression ?? ''));
  const fmtGuard = useEditBase<string>({ current: item.formatStringExpression ?? '', draft: fmt, editing: true, reseed: setFmt, persist: { scope: `calcitem|${item.ref}`, field: 'format' },
    readNow: async () => (await rpc<Array<{ name: string; value?: string }> | null>('getObjectProperties', item.ref))?.find((p) => p.name === 'FormatStringExpression')?.value ?? '' });
  const saveFmt = (raw: string) => {
    const typedThen = fmtGuard.draftNow();
    // R-B: the box takes the saved value only when nothing newer was typed while the write was on its way; newer words stay, over the saved value.
    void fmtGuard.beforeSave().then((ok) => ok ? rpc('setCalcItemFormatString', item.ref, raw, 'human', fmtGuard.confirmed() ?? '').then(() => { if (sameValue(fmtGuard.draftNow(), typedThen)) setFmt(raw); fmtGuard.saved(raw); reload(); }) : undefined)
      .catch(async (e) => { if (!(await fmtGuard.refused(e))) onError(e); });
  };
  const del = () => void rpc('deleteObject', item.ref).then(reload).catch(onError);

  return (
    // A row inside the group's card, split by a hairline: no card inside a card.
    <div className="border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-1">
        <span className="text-xs font-medium">{item.name}</span>
        <DraftMark show={guard.drafted || fmtGuard.drafted} />
        <div className="ml-auto flex items-center gap-2">
          {dirtyExpr && <MiniButton disabled={!valid} onClick={saveExpr}>{valid ? 'Save DAX' : 'Fix errors'}</MiniButton>}
          <button onClick={del} className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }} title="Delete item">✕</button>
        </div>
      </div>
      {guard.moved && <EditMovedBanner what="calculation item" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} />}
      <DaxField value={expr} onChange={setExpr} scope="calcitem" minHeight={64} onValidity={(v) => setValid(v)}
        ariaLabel={`${item.name} expression`} askContext={`the '${item.name}' calculation-item expression`} />
      <div className="mt-2">
        {fmtGuard.moved && <EditMovedBanner what="calculation item format" theirs={fmtGuard.theirs} onKeepMine={fmtGuard.keepMine} onUseNew={fmtGuard.useNew} />}
        <FormatStringControl value={item.formatStringExpression ?? ''} draft={fmt} onDraft={setFmt} onSave={saveFmt} />
      </div>
    </div>
  );
}

// The calc-item format-string control: an analyst STATIC picker (common formats, written as a quoted literal) vs a
// pro-dev DYNAMIC expression (a format-string DAX expression, e.g. SELECTEDMEASUREFORMATSTRING). It infers its
// starting mode from the stored value (a pure "…" literal → static; anything else → dynamic). Writes the SAME
// set_calc_item_format_string op either way — the difference is only whether the value is a quoted literal.
//
// The words typed in the Dynamic box belong to whoever holds `draft` (an editor that reads the live value at Save, like the calculation item row). When the
// stored value moves under typed words the control keeps them: only an untyped box follows the stored value (the old effect replaced them without a word).
function FormatStringControl({ value, onSave, inline, draft, onDraft }: { value: string; onSave: (raw: string) => void; inline?: boolean; draft?: string; onDraft?: (text: string) => void }) {
  const literal = /^\s*"(.*)"\s*$/.exec(value);
  const [mode, setMode] = useState<'static' | 'dynamic'>(() => inferFormatMode(value));
  const [ownDyn, setOwnDyn] = useState(value);
  const dyn = draft ?? ownDyn;
  const setDyn = onDraft ?? setOwnDyn;
  const startedAt = useRef(value);   // the stored value the box was last in step with
  useEffect(() => {
    const typed = draft !== undefined && draft !== startedAt.current && draft !== value;   // words the person typed, not the value (or their own save of it)
    startedAt.current = value;
    if (typed) return;   // typed words stay; the holder of the draft shows the moved choice
    setMode(inferFormatMode(value));
    if (draft === undefined) setOwnDyn(value);
  }, [value]);
  const staticVal = literal ? literal[1] : '';
  const isPreset = FORMAT_PRESETS.some((p) => p.value === staticVal);

  return (
    <div className="flex items-center gap-2 flex-wrap">
      <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>format</span>
      <div className="flex items-center gap-1">
        <button onClick={() => setMode('static')} className="text-(length:--sem-text-label) px-2 py-0 rounded-(--sem-radius-control) font-medium"
          style={mode === 'static' ? { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontWeight: 'var(--sem-weight-strong)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)' }}>Static</button>
        <button onClick={() => { setMode('dynamic'); if (mode !== 'dynamic' && draft === undefined) setDyn(value); }} className="text-(length:--sem-text-label) px-2 py-0 rounded-(--sem-radius-control) font-medium"
          style={mode === 'dynamic' ? { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontWeight: 'var(--sem-weight-strong)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)' }}>Dynamic</button>
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
          {dyn !== value && <div className="mt-1"><MiniButton onClick={() => onSave(dyn)}>Save format</MiniButton></div>}
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
const calendarMappingAllowed = (col: ColumnRow, unit: string) =>
  unit !== 'Date' || /date|time/i.test(col.dataType ?? '');
const TEMPLATES: { id: string; label: string; hint: string }[] = [
  { id: 'gregorian', label: 'Gregorian', hint: 'Year / Quarter / Month / Day' },
  { id: 'fiscal', label: 'Fiscal', hint: 'FY starting a chosen month' },
  { id: 'iso', label: 'ISO', hint: 'ISO-8601 weeks (Thursday rule)' },
  { id: '445', label: '4-4-5', hint: 'retail 4-4-5 periods over ISO weeks' },
  { id: '13period', label: '13-Period', hint: '13 four-week periods' },
];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export function CalendarsPanel() {
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

  if (!list) return <Panel><div className="text-xs" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading calendars…'}</div></Panel>;

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
      <div className="flex flex-col gap-3">
        {msg && <Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner>}
        <Panel>
          <SectionTitle>Calendars need compatibility level 1701+</SectionTitle>
          <div className="text-xs mt-2" style={{ color: 'var(--sem-fg)' }}>
            Calendars define the periods used by date calculations. This model uses compatibility level <span className="tnum font-semibold">{list.compatibilityLevel}</span>.
          </div>
          <div className="text-(length:--sem-text-label) mt-2" style={{ color: 'var(--sem-muted)' }}>
            You can still use the classic date-table approach below. To author calendars, raise the level to 1701.
          </div>
          {!confirmUpgrade ? (
            <div className="mt-3"><Button primary onClick={() => setConfirmUpgrade(true)}>Upgrade to 1701</Button></div>
          ) : (
            <div className="mt-3 sem-inset" role="alert">
              <div className="xe-msg xe-msg-warn xe-strong">Confirm the one-way upgrade</div>
              <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>
                Older tools may no longer open this model after its compatibility level is raised.
              </div>
              <div className="flex items-center gap-2 mt-3">
                <Button primary disabled={upgrading} onClick={() => void upgrade()}>{upgrading ? 'Upgrading…' : 'Confirm upgrade'}</Button>
                <Button disabled={upgrading} onClick={() => setConfirmUpgrade(false)}>Cancel</Button>
              </div>
            </div>
          )}
        </Panel>
        {/* The same keys as in the branch below: a compatibility level change (the assistant's, or the Plan's) swaps the branch, and keyed children are the same
            components, so the classic date table form keeps what was typed in it (R-B). */}
        <ClassicDateTable key="classic-date-table" tables={tables} setMsg={setMsg} />
      </div>
    );
  }

  const byTable = list.calendars.slice().sort((a, b) => a.table.localeCompare(b.table) || a.name.localeCompare(b.name));

  return (
    <div className="flex flex-col gap-3">
      {msg && <Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner>}
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}

      <TemplateQuickStart tables={tables} setMsg={setMsg} onDone={load} />

      <Panel>
        <div className="flex items-center gap-2 mb-2">
          <SectionTitle>Calendars in this model</SectionTitle>
        </div>
        {byTable.length === 0 ? (
          <div className="text-(length:--sem-text-body)" style={{ color: 'var(--sem-muted)' }}>No calendars yet. Use a template above to create one (Gregorian / Fiscal / ISO / 4-4-5 / 13-Period).</div>
        ) : (
          <div className="flex flex-col gap-3">
            {byTable.map((cal) => (
              <CalendarCard key={cal.table + '|' + cal.name} cal={cal}
                columns={tables.find((t) => t.name === cal.table)?.columns ?? []}
                setMsg={setMsg} onDone={load} />
            ))}
          </div>
        )}
      </Panel>

      <ClassicDateTable key="classic-date-table" tables={tables} setMsg={setMsg} />
    </div>
  );
}

type MsgSetter = (m: { ok: boolean; text: string } | null) => void;

// The two kinds of time-intelligence shift, for the template picker's tooltip.
const SHIFT_HELP = 'Fixed functions (DATEADD, SAMEPERIODLASTYEAR) shift the date sideways along the date axis. Flexible functions shift by the calendar, so a shift respects Year, Quarter and Month even when you slice by week. Calendar-aware functions take the calendar name, for example TOTALYTD ( [Sales], \'Fiscal\' ).';

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
        <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Adds the columns it needs. You can undo it.</span>
      </div>
      {/* The help fold about Fixed and Flexible shifts is a tooltip on the picker now (Kane's minimal pass, A9). */}
      <div className="sem-seg flex-wrap mt-2" title={SHIFT_HELP}>
        {TEMPLATES.map((t) => (
          <button key={t.id} type="button" onClick={() => setTemplate(t.id)} title={t.hint}
            aria-pressed={template === t.id} className="sem-seg-item">
            {t.label}
          </button>
        ))}
      </div>
      <div className="text-(length:--sem-text-label) mt-2" style={{ color: 'var(--sem-muted)' }}>{TEMPLATES.find((t) => t.id === template)?.hint}</div>
      <div className="flex items-center gap-2 flex-wrap mt-3">
        {template === 'fiscal' && (
          <label className="text-(length:--sem-text-label) flex items-center gap-2" style={{ color: 'var(--sem-muted)' }}>
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
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [menu, setMenu] = useState(false);
  const fail = (e: unknown) => setMsg({ ok: false, text: String((e as Error).message ?? e) });

  const units = cal.groups.filter((g) => g.timeUnit != null);
  const bucket = cal.groups.find((g) => g.timeUnit == null);
  const mappingOptions = columns.filter((c) => unit === TIME_RELATED || calendarMappingAllowed(c, unit));

  // true only when the engine took the change: the pick stays on screen on a refusal (R-B).
  const tag = (column: string, timeUnit: string | null, associated: boolean, remove: boolean): Promise<boolean> => {
    setMsg(null);
    return rpc<CalendarResult>('tagCalendarColumn', cal.table, cal.name, column, timeUnit, associated, remove)
      .then(() => { onDone(); return true; }).catch((e) => { fail(e); return false; });
  };
  const addMapping = () => {
    const c = col.trim(); if (!c) return;
    const row = columns.find((x) => x.name === c);
    const category = unit === TIME_RELATED ? null : unit;
    if (category && row && !calendarMappingAllowed(row, category)) {
      setMsg({ ok: false, text: `Cannot map '${c}' (${row.dataType || 'unknown type'}) as the date column. Pick a date or date/time column.` });
      return;
    }
    void tag(c, category, assoc, false).then((ok) => { if (ok) { setCol(''); setAssoc(false); setAdding(false); } });
  };
  const del = () => {
    if (!confirmDelete) { setConfirmDelete(true); return; }
    setMsg(null); setDeleting(true);
    void rpc('deleteCalendar', cal.table, cal.name)
      .then(() => { setConfirmDelete(false); onDone(); })
      .catch(fail)
      .finally(() => setDeleting(false));
  };

  return (
    <div className="sem-inset">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-(length:--sem-text-body) font-semibold">{cal.name}</span>
        <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>on {cal.table}</span>
        {cal.description && <span className="text-(length:--sem-text-label) truncate" style={{ color: 'var(--sem-muted)' }}>· {cal.description}</span>}
        {/* The template already makes the mappings, so adding one by hand and deleting the calendar are rare: one menu. */}
        {!confirmDelete && <span className="ml-auto relative">
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-label={`More for ${cal.name}`} aria-haspopup="menu" aria-expanded={menu} onClick={() => setMenu((o) => !o)}>⋯</button>
          {menu && <MenuPop align="right" onClose={() => setMenu(false)} items={[
            ...(adding ? [] : [{ label: 'Add mapping', detail: 'Map one more column to a calendar category.', act: () => setAdding(true) }]),
            { label: 'Delete calendar', detail: 'The columns are kept. You can undo this.', act: del },
          ]} />}
        </span>}
      </div>

      {confirmDelete && (
        <div className="mb-2 xe-subrow" role="alert">
          <div className="sem-chip sem-chip-warn" style={{ color: 'var(--sem-fg)' }}>Delete calendar '{cal.name}' from {cal.table}?</div>
          <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>The columns themselves are kept. You can undo this.</div>
          <div className="flex items-center gap-2 mt-2">
            <MiniButton disabled={deleting} onClick={del}>{deleting ? 'Deleting…' : 'Delete calendar'}</MiniButton>
            <MiniButton disabled={deleting} onClick={() => setConfirmDelete(false)}>Keep this calendar</MiniButton>
          </div>
        </div>
      )}

      {adding && (
        <div className="flex items-center gap-2 flex-wrap mb-2 xe-subrow">
          <Select value={col} onChange={setCol} placeholder="column…" options={mappingOptions.map((c) => ({ value: c.name, label: c.name }))} />
          <Select value={unit} onChange={(v) => { setUnit(v); setCol(''); }} compact options={[{ value: TIME_RELATED, label: 'time-related columns' }, ...TIME_UNITS.map((u) => ({ value: u, label: calendarUnitLabel(u) }))]} />
          <label className="text-(length:--sem-text-label) flex items-center gap-2" style={{ color: 'var(--sem-muted)' }}>
            <Check checked={assoc} onChange={() => setAssoc((v) => !v)} /> associated
          </label>
          <MiniButton disabled={!col.trim()} onClick={addMapping}>Add</MiniButton>
          <MiniButton onClick={() => { setAdding(false); setCol(''); setAssoc(false); }}>Cancel</MiniButton>
        </div>
      )}

      {units.length === 0 && !bucket ? (
        <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No mappings yet. Add a column to a calendar category.</div>
      ) : (
        <div className="flex flex-col gap-1">
          {units.map((g) => (
            <div key={g.timeUnit} className="flex items-center gap-2 text-xs">
              <span className="inline-block w-[130px] shrink-0 font-medium capitalize">{calendarUnitLabel(g.timeUnit!)}</span>
              <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>→</span>
              {g.primaryColumn ? (
                <span className="flex items-center gap-1 px-2 py-0 rounded font-mono text-(length:--sem-text-label)" style={{ background: 'var(--sem-surface)' }}>
                  {g.primaryColumn}
                  <button onClick={() => tag(g.primaryColumn!, null, false, true)} title="Remove mapping" style={{ color: 'var(--sem-muted)' }}>✕</button>
                </span>
              ) : <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>(no primary)</span>}
              {g.associatedColumns.map((a) => (
                <span key={a} className="flex items-center gap-1 px-2 py-0 rounded font-mono text-(length:--sem-text-label)" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)' }}>
                  +{a}
                  <button onClick={() => tag(a, null, false, true)} title="Remove associated column" style={{ color: 'var(--sem-muted)' }}>✕</button>
                </span>
              ))}
            </div>
          ))}
          {bucket && bucket.timeRelatedColumns.length > 0 && (
            <div className="flex items-center gap-2 text-xs flex-wrap">
              <span className="inline-block w-[130px] shrink-0" style={{ color: 'var(--sem-muted)' }}>time-related</span>
              <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>→</span>
              {bucket.timeRelatedColumns.map((c) => (
                <span key={c} className="flex items-center gap-1 px-2 py-0 rounded font-mono text-(length:--sem-text-label)" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)' }}>
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
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-2 text-left w-full">
        <span className="inline-flex w-3 justify-center"><Caret open={open} /></span>
        <SectionTitle>Classic date table</SectionTitle>
        <span className="text-(length:--sem-text-label) ml-1" style={{ color: 'var(--sem-muted)' }}>Older way</span>
      </button>
      {open && (
        <div className="flex flex-col gap-3 mt-3">
          <div>
            <div className="text-(length:--sem-text-label) mb-2" style={{ color: 'var(--sem-muted)' }}>Generate a date table, optionally marked as the model's date table.</div>
            <div className="flex items-center gap-2 flex-wrap">
              <TextInput value={dtName} onChange={setDtName} placeholder="table name" style={{ width: 160 }} />
              <label className="flex items-center gap-2 text-xs" style={{ color: 'var(--sem-fg)' }}>
                <Check checked={dtMark} onChange={() => setDtMark((v) => !v)} /> mark as date table
              </label>
              <button onClick={() => setAdv((v) => !v)} className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{adv ? '▾' : '▸'} range</button>
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
            <div className="text-(length:--sem-text-label) mb-2" style={{ color: 'var(--sem-muted)' }}>Mark an existing table as the date table.</div>
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

function isRoleMemberIdentity(name: string): boolean {
  const s = name.trim();
  if (!s || s.length > 256) return false;
  if (/[!?*<>|"\n\r\t]/.test(s)) return false;
  if (/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(s)) return true;
  const at = s.indexOf('@');
  if (at > 0 && at < s.length - 1 && s.indexOf('@', at + 1) < 0 && s.indexOf(' ') < 0) return s.indexOf('.', at + 1) > at + 1;
  const slash = s.indexOf('\\');
  if (slash > 0 && slash < s.length - 1 && s.indexOf('\\', slash + 1) < 0) return s.indexOf(' ') < 0;
  return /[A-Za-z0-9]/.test(s);
}

function roleLossLines(role: RoleInfo): string[] {
  const lines: string[] = [];
  for (const f of role.tableFilters ?? []) lines.push(`row filter on ${f.table}`);
  for (const o of role.objectPermissions ?? []) {
    if (o.metadataPermission) lines.push(`table visibility on ${o.table}`);
    for (const c of o.columns ?? []) lines.push(`column visibility on ${o.table}[${c.column}]`);
  }
  const n = (role.members ?? []).length;
  if (n === 1) lines.push('1 member');
  else if (n > 1) lines.push(`${n} members`);
  return lines;
}

export function RlsOlsPanel() {
  const { tables } = useModelObjects();
  const [roles, setRoles] = useState<RoleInfo[] | null>(null);
  const [sel, setSel] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const newNameNow = useRef(''); newNameNow.current = newName;   // what the box holds when the engine answers
  const [confirmDel, setConfirmDel] = useState(false);
  const [opened, setOpened] = useState<Set<string>>(() => new Set());   // tables whose Add filter was pressed: they show the full row from then on
  const [movedWords, setMovedWords] = useState(0);   // bumped when typed words are moved onto another role or table: its boxes open again and read them
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
  useEffect(() => { setConfirmDel(false); }, [sel]);

  const createRole = () => {
    const n = newName.trim(); if (!n) return;
    // The box ends only when it still holds the name that was sent: a name typed while the write was on its way stays (the next Add sends it).
    void rpc('createRole', n, 'Read').then(() => { if (newNameNow.current.trim() === n) { setNewName(''); setCreating(false); } setSel(n); return load(); }).catch(fail);
  };
  const deleteRole = (name: string) => {
    setConfirmDel(false);
    void rpc('deleteRole', name).then(load).catch(fail);
  };

  if (!roles) return <Panel><div className="text-xs" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Loading roles…'}</div></Panel>;
  const role = roles.find((r) => r.name === sel) ?? null;

  return (
    <div className="flex flex-col gap-3">
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      {/* Words typed for a role (a row filter, a member name) are held by the role's NAME, and a role has no id that survives a rename. When the assistant renames or removes
          a role or a table, the words are not dropped and not shown on another role on a guess: they are listed here, and the person chooses (join 11 solui, R-B). */}
      <OrphanDrafts prefix="rowfilter|" live={tables.length ? roles.flatMap((r) => tables.map((t) => `${r.name}|${t.name}`)) : null} what="row filter"
        nameOf={(id) => id.replace('|', ' / ')}
        targetsFor={() => roles.flatMap((r) => tables.map((t) => ({ id: `${r.name}|${t.name}`, label: `${r.name} / ${t.name}` })))}
        onMoved={() => setMovedWords((n) => n + 1)} />
      <OrphanDrafts prefix="rolemember|" live={roles.map((r) => r.name)} what="role"
        nameOf={(id) => id} targetsFor={() => roles.map((r) => ({ id: r.name, label: r.name }))}
        onMoved={() => setMovedWords((n) => n + 1)} />
      <div className="grid grid-cols-1 lg:grid-cols-[minmax(15rem,18rem)_1fr] gap-3">
        {/* roles rail */}
        <Panel>
          <div className="flex items-center gap-2 mb-2 min-w-0">
            <SectionTitle>Roles</SectionTitle>
            <div className="ml-auto shrink-0">{creating ? null : <MiniButton onClick={() => setCreating(true)}>+ Role</MiniButton>}</div>
          </div>
          {creating && (
            <div className="flex items-center gap-1 mb-2 min-w-0">
              <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="role name…"
                onKeyDown={(e) => { if (e.key === 'Enter') createRole(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                className="sem-field flex-1 min-w-0" />
              <span className="shrink-0"><MiniButton disabled={!newName.trim()} onClick={createRole}>Add</MiniButton></span>
            </div>
          )}
          {roles.length === 0 ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No roles yet.</div> : (
            <div className="flex flex-col gap-1">
              {roles.map((r) => (
                <button key={r.name} onClick={() => setSel(r.name)} className="flex items-center gap-2 py-1 px-2 rounded text-left text-xs"
                  style={{ background: r.name === sel ? 'var(--sem-surface-2)' : 'transparent', color: 'var(--sem-fg)' }}>
                  <span className="font-medium truncate flex-1">{r.name}</span>
                  <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{(r.tableFilters?.length ?? 0) + (r.objectPermissions?.length ?? 0) || ''}</span>
                </button>
              ))}
            </div>
          )}
        </Panel>

        {/* role detail */}
        {!role ? <Panel><div className="sem-text-meta">Select or add a role.</div></Panel> : (
          <div className="flex flex-col gap-3 min-w-0">
            <Panel>
              <div className="flex items-center gap-3 flex-wrap">
                <span className="text-(length:--sem-text-body) font-semibold">{role.name}</span>
                <label className="text-(length:--sem-text-label) flex items-center gap-2" style={{ color: 'var(--sem-muted)' }}>
                  model permission
                  <Select value={role.modelPermission} onChange={(v) => void rpc('setRolePermission', role.name, v).then(load).catch(fail)}
                    compact options={MODEL_PERMS.map((p) => ({ value: p, label: p }))} />
                </label>
                <div className="ml-auto shrink-0">{confirmDel ? null : <MiniButton onClick={() => setConfirmDel(true)}>Delete role</MiniButton>}</div>
              </div>
              {confirmDel && (
                <div className="mt-3 sem-inset" role="alert">
                  <div className="xe-msg xe-msg-warn xe-strong">Delete the role {role.name}?</div>
                  <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>
                    {roleLossLines(role).length
                      ? 'This also removes: ' + roleLossLines(role).join(', ') + '. You can undo this.'
                      : 'You can undo this.'}
                  </div>
                  <div className="flex items-center gap-2 mt-3">
                    <MiniButton onClick={() => deleteRole(role.name)}>Delete role</MiniButton>
                    <MiniButton onClick={() => setConfirmDel(false)}>Cancel</MiniButton>
                  </div>
                </div>
              )}
              {/* Keyed by its role: what was typed for one role is never added to another one the page falls back to (the assistant renamed or removed the selected role). */}
              <Members key={`${role.name}:${movedWords}`} role={role} reload={load} onError={fail} />
            </Panel>

            <Panel>
              <SectionTitle>Table security</SectionTitle>
              <div className="text-(length:--sem-text-label) mt-1 mb-2" style={{ color: 'var(--sem-muted)' }}>A row filter (RLS) limits rows. Visibility (OLS) hides a table or column.</div>
              {/* Key by role+table so switching roles REMOUNTS each row and re-seeds its RLS-filter draft from the
                  selected role's server value — otherwise an unsaved draft typed under one role could bleed onto
                  (and be saved against) another role. */}
              <div className="flex flex-col gap-3">
                {tables.map((t) => {
                  // A table with no filter, default visibility and nothing typed is one line. It opens into the same row on Add filter.
                  const own = role.objectPermissions?.find((o) => o.table === t.name);
                  const used = !!role.tableFilters?.find((f) => f.table === t.name && f.filterExpression.trim())
                    || (own?.columns?.length ?? 0) > 0 || !!heldDraft<string>(`rowfilter|${role.name}|${t.name}`) || opened.has(`${role.name}|${t.ref}`);
                  return used
                    ? <TableSecurityRow key={`${role.name}|${t.ref}:${movedWords}`} role={role} table={t} reload={load} onError={fail} />
                    : <EmptyTableSecurity key={`${role.name}|${t.ref}:${movedWords}`} role={role} table={t} reload={load} onError={fail}
                        onOpen={() => setOpened((cur) => new Set(cur).add(`${role.name}|${t.ref}`))} />;
                })}
              </div>
            </Panel>
          </div>
        )}
      </div>
    </div>
  );
}

function Members({ role, reload, onError }: { role: RoleInfo; reload: () => void; onError: (e: unknown) => void }) {
  // A name typed and not added is held for THIS role (drafts.ts): another role, a tab switch and a reload keep it, and a role that is renamed or removed lists it above.
  const scope = `rolemember|${role.name}`;
  const [name, setName] = useState(() => heldDraft<string>(scope)?.value ?? '');
  useHoldDraft({ persist: { scope }, draft: name, base: '', active: true });
  const [hint, setHint] = useState<string | null>(null);
  const add = () => {
    const n = name.trim(); if (!n) return;
    if (!isRoleMemberIdentity(n)) {
      setHint('That is not a user or group name. Use an email like person@company.com, a group name, or an object id.');
      return;
    }
    if ((role.members ?? []).some((m) => m.toLowerCase() === n.toLowerCase())) {
      setHint('That member is already on this role.');
      return;
    }
    setHint(null);
    // R-B: the box empties only when it still holds the name that was added (more typed meanwhile stays).
    void rpc('setRoleMember', role.name, n, true).then(() => { setName((cur) => (cur.trim() === n ? '' : cur)); reload(); }).catch(onError);
  };
  const remove = (m: string) => void rpc('setRoleMember', role.name, m, false).then(reload).catch(onError);
  return (
    <div className="mt-2">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>members</span>
        {(role.members ?? []).map((m) => (
          <span key={m} className="text-(length:--sem-text-label) flex items-center gap-1 px-2 py-0 rounded" style={{ background: 'var(--sem-surface-2)' }}>
            {m}<button onClick={() => remove(m)} style={{ color: 'var(--sem-muted)' }}>✕</button>
          </span>
        ))}
        <input value={name} onChange={(e) => { setName(e.target.value); setHint(null); }} placeholder="add member (email / group)…"
          onKeyDown={(e) => { if (e.key === 'Enter') add(); }}
          className="sem-field sem-field-sm" style={{ width: 180 }} />
        <MiniButton disabled={!name.trim()} onClick={add}>Add</MiniButton>
      </div>
      {hint && <div className="xe-msg xe-msg-warn mt-1">{hint}</div>}
    </div>
  );
}

// A table with nothing set for this role: one line, with the table's visibility and a button that opens the filter editor.
function EmptyTableSecurity({ role, table, onOpen, reload, onError }: { role: RoleInfo; table: TableModel; onOpen: () => void; reload: () => void; onError: (e: unknown) => void }) {
  const tableOls = role.objectPermissions?.find((o) => o.table === table.name)?.metadataPermission ?? 'Default';
  return (
    <div className="flex items-center gap-3 min-w-0" title={table.isCalculationGroup ? 'Visibility settings are not used on a calculation group' : undefined}>
      <span className="text-xs font-medium truncate">{table.name}</span>
      <span className="sem-text-meta shrink-0">All rows</span>
      <div className="ml-auto flex items-center gap-2 shrink-0">
        {!table.isCalculationGroup && (
          <label className="text-(length:--sem-text-label) flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>
            Visibility
            <Select value={tableOls} compact options={OLS_PERMS.map((p) => ({ value: p, label: p }))}
              onChange={(v) => void rpc('setTableObjectPermission', role.name, table.ref, v).then(reload).catch(onError)} />
          </label>
        )}
        <MiniButton onClick={onOpen}>Add filter</MiniButton>
      </div>
    </div>
  );
}

export function TableSecurityRow({ role, table, reload, onError }: { role: RoleInfo; table: TableModel; reload: () => void; onError: (e: unknown) => void }) {
  const serverFilter = role.tableFilters?.find((f) => f.table === table.name)?.filterExpression ?? '';
  const tableOls = role.objectPermissions?.find((o) => o.table === table.name)?.metadataPermission ?? 'Default';
  const colPerms = role.objectPermissions?.find((o) => o.table === table.name)?.columns ?? [];
  // A filter typed and not saved is held above the row, by role and table (drafts.ts, the drafts class).
  const heldFilter = heldDraft<string>(`rowfilter|${role.name}|${table.name}`);
  const [filter, setFilter] = useState(heldFilter ? heldFilter.value : serverFilter);
  const [valid, setValid] = useState(true);
  const [issues, setIssues] = useState(0);
  const [open, setOpen] = useState(false);
  const [builder, setBuilder] = useState(false);
  const [bCol, setBCol] = useState('');
  const [colFilter, setColFilter] = useState('');
  // The filter follows an edit from the other door while nothing is typed; a typed draft is never saved over it unasked.
  // Save reads this role's filter for this table now and the write names it as the value it replaces: the engine compares it inside its own mutation (Sol join 6, C4).
  const guard = useEditBase<string>({ current: serverFilter, draft: filter, editing: true, scope: `${role.name}/${table.name}`, reseed: setFilter, persist: { scope: `rowfilter|${role.name}|${table.name}` },
    readNow: async () => (await rpc<RoleInfo[]>('listRoles')).find((r) => r.name === role.name)?.tableFilters?.find((f) => f.table === table.name)?.filterExpression ?? '' });
  // Name the held words by role and table, so the list of words whose role or table is gone can still say what they were for.
  useEffect(() => { const m = draftModel(); if (guard.drafted && m) draftStore.setLabel(m, `rowfilter|${role.name}|${table.name}`, '', `${role.name} / ${table.name}`); }, [guard.drafted, role.name, table.name]);
  const dirty = filter !== serverFilter;
  const hasFilter = serverFilter.trim().length > 0;
  const filterOk = valid && issues === 0;

  const saveFilter = () => {
    if (!dirty || !filterOk) return;
    void guard.beforeSave().then((ok) => ok ? rpc('setTablePermission', role.name, table.ref, filter, 'human', guard.confirmed() ?? '').then(() => { guard.saved(filter); reload(); }) : undefined)
      .catch(async (e) => { if (!(await guard.refused(e))) onError(e); });
  };
  const setTblOls = (v: string) => void rpc('setTableObjectPermission', role.name, table.ref, v).then(reload).catch(onError);
  const setColOls = (colRef: string, v: string) => void rpc('setColumnObjectPermission', role.name, colRef, v).then(reload).catch(onError);
  const colOlsOf = (name: string) => colPerms.find((c) => c.column === name)?.metadataPermission ?? 'Default';
  // The analyst "build a rule" on-ramp: compose the common RLS predicates without hand-typing DAX. Pro devs
  // ignore this and edit the raw <DaxField> (which already autocompletes this table's columns + the security helpers).
  const rule = (dax: string) => { setFilter(dax); setBuilder(false); };
  const visibleCols = table.columns.filter((c) => !colFilter.trim() || c.name.toLowerCase().includes(colFilter.trim().toLowerCase()));

  return (
    <div className="sem-inset" title={table.isCalculationGroup ? 'Visibility settings are not used on a calculation group' : undefined}>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-2 shrink-0" title="Column-level security (OLS)">
          <span className="inline-flex w-3 justify-center"><Caret open={open} /></span>
          <span className="text-xs font-medium">{table.name}</span>
          <DraftMark show={guard.drafted} />
          {hasFilter && <span className="w-1.5 h-1.5 rounded-full" title="Has a row filter" style={{ background: 'var(--sem-accent)' }} />}
        </button>
        <div className="ml-auto flex items-center gap-2">
          <MiniButton onClick={() => setBuilder((b) => !b)}>Build a rule ▾</MiniButton>
          {table.isCalculationGroup ? null : (
            <label className="text-(length:--sem-text-label) flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>
              Visibility
              <Select value={tableOls} onChange={setTblOls} compact options={OLS_PERMS.map((p) => ({ value: p, label: p }))} />
            </label>
          )}
        </div>
      </div>

      {builder && (
        <div className="flex items-center gap-2 flex-wrap mt-2 xe-subrow">
          <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>restrict rows where</span>
          <Select value={bCol} onChange={setBCol} placeholder="column…" compact options={table.columns.map((c) => ({ value: c.name, label: c.name }))} />
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] = USERPRINCIPALNAME ()`)}>= current user</MiniButton>
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] IN { "Value 1", "Value 2" }`)}>in a list…</MiniButton>
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] = "Value"`)}>= a value…</MiniButton>
          <MiniButton disabled={!bCol} onClick={() => rule(`[${bCol}] = LOOKUPVALUE ( Users[${bCol}], Users[Email], USERPRINCIPALNAME () )`)}>via a lookup…</MiniButton>
        </div>
      )}

      <div className="mt-2">
        <DaxField value={filter} onChange={setFilter} scope="rls" table={table.name} minHeight={52}
                placeholder="DAX condition, for example [Amount] > 0. Leave blank to allow all rows."
          onValidity={(v, n) => { setValid(v); setIssues(n); }} ariaLabel={`Row filter for ${table.name}`} askContext={`an RLS row-filter for the '${table.name}' table`} />
        {guard.moved && <EditMovedBanner what="row filter" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} />}
        <div className="mt-1 flex items-center gap-2">
          {dirty && <MiniButton disabled={!filterOk} onClick={saveFilter}>{filterOk ? 'Save filter' : 'Fix errors'}</MiniButton>}
          {dirty && <button onClick={() => setFilter(serverFilter)} className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>revert</button>}
        </div>
      </div>

      {open && !table.isCalculationGroup && (
        <div className="mt-2 pl-6 flex flex-col gap-1">
          <div className="flex items-center gap-2 mb-1">
            <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>column visibility (OLS)</span>
            {table.columns.length > 10 && (
              <input value={colFilter} onChange={(e) => setColFilter(e.target.value)} placeholder="filter columns…" spellCheck={false}
                className="sem-field sem-field-sm ml-auto" style={{ width: 160 }} />
            )}
          </div>
          {table.columns.length === 0 ? <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>no columns</span> :
            visibleCols.map((c) => (
              <div key={c.ref} className="flex items-center gap-2 text-(length:--sem-text-label)">
                <span className="flex-1 truncate" style={{ color: 'var(--sem-fg)' }}>{c.name}</span>
                <Select value={colOlsOf(c.name)} onChange={(v) => setColOls(c.ref, v)} compact options={OLS_PERMS.map((p) => ({ value: p, label: p }))} />
              </div>
            ))}
          {visibleCols.length === 0 && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>no columns match.</span>}
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
  // The space rule: the two boxes fill the room between the search card and "Installed in this model" instead of
  // stopping at 420 px with the page blank under them. The page itself is what gets the height; the feed and the
  // preview then take the slack inside it. Measuring the feed's own height down to the page bottom was the first
  // try and it was wrong twice over: a short feed still stopped at its content, and a long one ran past the
  // Installed panel and pushed it, and its Uninstall buttons, below the fold. A box cannot size itself from the
  // page bottom when something else has to sit there too.
  const [pageRef, pageH] = useFillHeight<HTMLDivElement>(560, 0, results?.length ?? -1);
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
    <div ref={pageRef} className="flex flex-col gap-3" style={{ height: pageH }}>
      <Panel className="shrink-0">
        <div className="flex items-center gap-2">
          <SectionTitle>DaxLib packages</SectionTitle>
          <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
            Add ready-made DAX functions from daxlib.org.
          </span>
        </div>
        <div className="flex items-center gap-2 mt-3">
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="search packages (name / description)…" spellCheck={false}
            onKeyDown={(e) => { if (e.key === 'Enter') void search(query); }}
            className="sem-field flex-1" />
          <Button onClick={() => void search(query)} disabled={searching}>{searching ? 'Searching…' : 'Search'}</Button>
        </div>
        {msg && <div className="mt-3"><Banner color={msg.ok ? 'var(--sem-good)' : 'var(--sem-bad)'}>{msg.text}</Banner></div>}
      </Panel>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 flex-1" style={{ minHeight: 420 }}>
        {/* feed */}
        <Panel className="flex flex-col min-h-0">
          {!results ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
            : results.length === 0 ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No packages found.</div> : (
              <div className="flex flex-col gap-1 flex-1 min-h-0 overflow-y-auto">
                {results.map((p) => (
                  <button key={p.id + '@' + p.version} onClick={() => void openDetail(p.id)}
                    className="flex flex-col items-start gap-1 py-1 px-2 rounded text-left"
                    style={{ background: p.id === sel ? 'var(--sem-surface-2)' : 'transparent' }}>
                    <span className="flex items-center gap-2 w-full">
                      <span className="text-xs font-medium truncate flex-1">{p.id}</span>
                      {installedIds.has(p.id.toLowerCase()) && <span className="sem-chip sem-chip-ok">installed</span>}
                      <span className="text-(length:--sem-text-label) tnum" style={{ color: 'var(--sem-muted)' }}>{p.version}</span>
                    </span>
                    {p.description && <span className="text-(length:--sem-text-label) truncate w-full" style={{ color: 'var(--sem-muted)' }}>{p.description}</span>}
                  </button>
                ))}
              </div>
            )}
        </Panel>

        {/* detail + install */}
        <Panel className="flex flex-col min-h-0">
          {!sel ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Select a package to preview its functions and install.</div>
            : detailBusy || !detail ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Loading {sel}…</div> : (
              <div className="flex flex-col gap-3 flex-1 min-h-0">
                {/* The package's own text scrolls when a long function list needs it; Install sits under that box, never inside it. */}
                <div className="flex flex-col gap-3 flex-1 min-h-0 overflow-y-auto">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-semibold truncate">{detail.id}</span>
                  <span className="text-(length:--sem-text-label) tnum" style={{ color: 'var(--sem-muted)' }}>{detail.version}</span>
                  {detail.projectUrl && <a href={detail.projectUrl} target="_blank" rel="noreferrer" className="text-(length:--sem-text-label) xe-link">↗ project</a>}
                </div>
                {detail.description && <div className="text-xs" style={{ color: 'var(--sem-fg)' }}>{detail.description}</div>}
                {detail.authors?.length ? <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>by {detail.authors.join(', ')}</div> : null}
                {detail.dependencies?.length ? (
                  <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>depends on: {detail.dependencies.map((d) => d.id + (d.version ? ' ' + d.version : '')).join(', ')}</div>
                ) : null}
                <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>
                  {detail.functionCount ?? detail.functionNames?.length ?? 0} function(s), added to the model on install:
                </div>
                <div className="flex flex-wrap gap-1">
                  {(detail.functionNames ?? []).map((fn) => (
                    <span key={fn} className="text-(length:--sem-text-label) px-2 py-0 rounded font-mono" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>{fn}</span>
                  ))}
                </div>
                </div>
                <div className="flex items-center gap-2 mt-2 shrink-0">
                  <label className="flex items-center gap-2 text-xs" style={{ color: 'var(--sem-fg)' }}>
                    <Check checked={replace} onChange={() => setReplace((v) => !v)} /> overwrite existing
                  </label>
                  <div className="ml-auto flex items-center gap-2">
                    {installedIds.has(detail.id.toLowerCase()) && <MiniButton onClick={() => uninstall(detail.id)}>Uninstall</MiniButton>}
                    <Button primary disabled={busy} onClick={() => void install()}>{busy ? 'Installing…' : 'Install'}</Button>
                  </div>
                </div>
              </div>
            )}
        </Panel>
      </div>

    </div>
  );
}

// ---- local primitives -----------------------------------------------------------------------------
function Panel({ children, className }: { children: React.ReactNode; className?: string }) {
  return <div className={'sem-card' + (className ? ' ' + className : '')}>{children}</div>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <span className="sem-card-title">{children}</span>;
}
function Button({ children, onClick, primary, disabled }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="sem-btn sem-btn-sm">
      {children}
    </button>
  );
}
function Seg({ children, active, onClick }: { children: React.ReactNode; active?: boolean; onClick?: () => void }) {
  return (
    <button type="button" onClick={onClick} aria-pressed={!!active} className="sem-seg-item">
      {children}
    </button>
  );
}
// A message is a sentence with a coloured dot, not coloured text on a tinted box (Instrument).
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="xe-msg mb-3" style={{ '--xe-dot': color } as React.CSSProperties}>{children}</div>;
}
function TextInput({ value, onChange, placeholder, mono, style }: { value: string; onChange: (v: string) => void; placeholder?: string; mono?: boolean; style?: React.CSSProperties }) {
  return (
    <input value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} spellCheck={false}
      className={'sem-field ' + (mono ? 'sem-num' : '')}
      style={style} />
  );
}
function Select({ value, onChange, placeholder, options, compact }: { value: string; onChange: (v: string) => void; placeholder?: string; options: { value: string; label: string }[]; compact?: boolean }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)}
      className="sem-field" style={{ minWidth: compact ? 0 : 180 }}>
      {placeholder !== undefined && <option value="" disabled>{placeholder}</option>}
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}
function Check({ checked, onChange }: { checked: boolean; onChange: () => void }) {
  return (
    <button onClick={onChange} aria-checked={checked} role="checkbox"
      className={'xe-check' + (checked ? ' is-on' : '')}>
      {checked ? '✓' : ''}
    </button>
  );
}
