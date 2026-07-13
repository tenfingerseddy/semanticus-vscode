// Shared lineage wire types + kind glyph/color maps, used by lineage.tsx (lists), lineagegraph.tsx (the Obsidian-style
// force graph) and lineagetree.tsx (the expand-on-click DAG). Single source of truth so the three views can't drift.
// Mirrors Semanticus.Engine/Lineage/LineageProtocol.cs (camelCased over the JSON-RPC door).

import { useCallback, useEffect, useRef, useState } from 'react';

export interface LineageNode { ref: string; name: string; kind: string; table?: string; isHidden?: boolean; detail?: string }
export interface LineageEdge { from: string; to: string; kind: string }
export interface LineageResult { nodes: LineageNode[]; edges: LineageEdge[]; caveat?: string }

export const KIND_GLYPH: Record<string, string> = {
  measure: 'ƒ', column: '▦', calcColumn: '∑', table: '⊞', calcTable: '∑', calcGroup: '⊟',
  relationship: '⇄', hierarchy: '⤵', source: '⛁', unresolved: '?', level: '•',
  // The report layer (downstream of the model — where a field is actually shown).
  report: '◰', page: '▭', visual: '◫',
};
export const KIND_COLOR: Record<string, string> = {
  measure: '#2ED47A', column: '#3fb0e6', calcColumn: '#17B3A3', relationship: '#e0b341',
  hierarchy: '#e0b341', table: 'var(--sem-muted)', source: '#4E7CA8', unresolved: 'var(--sem-bad)',
  // Report layer = a warm family, visually distinct from the cool model kinds.
  report: '#ff7043', page: '#ffa726', visual: '#ffca28',
};

// Human label for a node kind (legend / tooltips).
export const KIND_LABEL: Record<string, string> = {
  measure: 'Measure', column: 'Column', calcColumn: 'Calc column', table: 'Table', calcTable: 'Calc table',
  calcGroup: 'Calc group', relationship: 'Relationship', hierarchy: 'Hierarchy', source: 'Source',
  unresolved: 'Unresolved', level: 'Level', report: 'Report', page: 'Report page', visual: 'Visual',
};

// Rank search matches so the thing a builder means floats to the top: exact-name beats prefix beats substring beats a
// table-only match, and within the same tier, business objects (measures/tables) rank above columns above structural
// nodes above sources. Shared by the graph + tree search boxes. Returns up to `limit` LineageNodes, best first.
const KIND_SEARCH_PRIORITY: Record<string, number> = {
  measure: 0, table: 1, calcTable: 1, report: 1, calcColumn: 2, column: 2, page: 2, visual: 2,
  calcGroup: 3, hierarchy: 3, relationship: 3, level: 3, source: 4, unresolved: 5,
};
export function rankNodeMatches<T extends { name: string; kind: string; table?: string }>(nodes: T[], query: string, limit = 8): T[] {
  const q = query.trim().toLowerCase();
  if (!q) return [];
  const scored: { n: T; s: number }[] = [];
  for (const n of nodes) {
    const name = (n.name ?? '').toLowerCase();
    const hay = (n.name + ' ' + (n.table ?? '')).toLowerCase();
    if (!hay.includes(q)) continue;
    const tier = name === q ? 0 : name.startsWith(q) ? 1 : name.includes(q) ? 2 : 3;   // 3 = matched only via table
    scored.push({ n, s: tier * 10 + (KIND_SEARCH_PRIORITY[n.kind] ?? 6) });
  }
  scored.sort((a, b) => a.s - b.s);
  return scored.slice(0, limit).map((x) => x.n);
}

// ---- Slicer facets --------------------------------------------------------------------------------
// The lineage views let a builder scope to a SUBSET of the model via multi-select "slicer" dropdowns (a single
// search box picks one node — not flexible enough). Two facets, shared by both views: Tables and Fields.

// Kinds that count as a "field" for the Fields slicer — the low-level columns/measures a builder picks to scope to.
export const FIELD_KINDS = new Set(['measure', 'column', 'calcColumn', 'calcTable']);

export interface FacetOption { value: string; label: string; sub?: string; kind?: string }

// Tables slicer options: one per distinct table (from every node's home table + table-kind node names), alphabetical,
// with a member count so a builder sees a table's size before picking it.
export function tableFacetOptions(nodes: LineageNode[]): FacetOption[] {
  const count = new Map<string, number>();
  for (const n of nodes) if (n.table) count.set(n.table, (count.get(n.table) ?? 0) + 1);
  for (const n of nodes) if ((n.kind === 'table' || n.kind === 'calcTable') && !count.has(n.name)) count.set(n.name, 0);
  return [...count.keys()].sort((a, b) => a.localeCompare(b)).map((t) => ({ value: t, label: t, sub: (count.get(t) ?? 0) + ' fields' }));
}

// Fields slicer options: every measure/column (the low-level objects), labelled with home table + kind glyph so a
// builder can pick specific fields. Sorted by table then name (feels grouped without a header).
export function fieldFacetOptions(nodes: LineageNode[]): FacetOption[] {
  return nodes.filter((n) => FIELD_KINDS.has(n.kind))
    .map((n) => ({ value: n.ref, label: n.name, sub: n.table, kind: n.kind }))
    .sort((a, b) => (a.sub ?? '').localeCompare(b.sub ?? '') || a.label.localeCompare(b.label));
}

// A "slicer" scope predicate shared by both views: given the picked tables + fields, is this node in scope? Empty
// selections = everything (no slicing). Tables match a node's home table (so a table's whole column/measure family
// comes along) or a table node's own name; fields match by exact ref. The two facets UNION — pick these tables AND
// also these specific fields — which is predictable, WYSIWYG slicing.
export function makeScope(tables: Set<string>, fields: Set<string>) {
  const active = tables.size > 0 || fields.size > 0;
  return {
    active,
    has(n: LineageNode): boolean {
      if (!active) return true;
      if (fields.has(n.ref)) return true;
      if (tables.size) {
        if (n.table && tables.has(n.table)) return true;
        if ((n.kind === 'table' || n.kind === 'calcTable') && tables.has(n.name)) return true;
      }
      return false;
    },
  };
}

// Resolve a KIND_COLOR entry to a concrete color: ECharts canvas can't read CSS custom properties, so a
// `var(--…)` value is looked up against the document root (with a fallback) before it reaches a chart option.
export function resolveColor(v: string | undefined, fallback = '#9aa0aa'): string {
  if (!v) return fallback;
  if (v.startsWith('var(')) {
    const name = v.slice(4, -1).trim();
    const got = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return got || fallback;
  }
  return v;
}

// Make a chart/canvas container fill the remaining viewport height, so the lineage visuals are DYNAMIC to the window
// (not a fixed 540px box leaving dead space). Measures the element's top edge and stretches it to the bottom (minus a
// small margin). Recomputes on viewport resize via a ResizeObserver on the document element (fires when the VS Code
// panel / webview resizes) plus a window resize listener; `extraDeps` forces a remeasure when toolbars above it grow
// (e.g. a control row wraps). Put the returned ref on the chart's wrapper div and feed the height to the chart.
export function useFillHeight(minH = 420, extraDeps: unknown[] = []): [(el: HTMLDivElement | null) => void, number] {
  const elRef = useRef<HTMLDivElement | null>(null);
  const [h, setH] = useState(minH);

  const recompute = useCallback(() => {
    const el = elRef.current; if (!el) return;
    const top = el.getBoundingClientRect().top;   // viewport-relative — shrinks (can go negative) when <main> is scrolled
    // Cap at the viewport (minus a header allowance) so a measurement taken while the scroll container is scrolled
    // can't overshoot into a taller-than-window chart. The cap only binds when top < ~56px (i.e. scrolled); at rest
    // (toolbars push top well below that) the exact fill-to-bottom value is used.
    const next = Math.max(minH, Math.min(Math.round(window.innerHeight - top - 16), Math.round(window.innerHeight - 72)));
    setH((prev) => (Math.abs(prev - next) > 1 ? next : prev));   // guard: avoid a resize→setState→resize loop
  }, [minH]);

  // A CALLBACK ref (not a plain RefObject) so we measure the instant the element ATTACHES to the DOM. This is the fix:
  // the sub-views render a loading placeholder — with NO fill element — until the async model graph arrives, so the
  // measured element only mounts on the data-ready re-render, long after a one-shot mount effect measured against
  // nothing and gave up (leaving the height pinned at minH). Measuring on attach lets EVERY caller fill from the first
  // real paint without threading a data-ready value through `extraDeps` — the latent trap that made only the Graph view
  // fill (its edgeKindsPresent.length flips on load, forcing a remeasure) while the Tree view, lacking such a dep, did not.
  const setRef = useCallback((el: HTMLDivElement | null) => {
    elRef.current = el;
    if (el) requestAnimationFrame(recompute);
  }, [recompute]);

  useEffect(() => {
    const raf = requestAnimationFrame(recompute);
    window.addEventListener('resize', recompute);
    const ro = new ResizeObserver(recompute);
    ro.observe(document.documentElement);
    return () => { cancelAnimationFrame(raf); window.removeEventListener('resize', recompute); ro.disconnect(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [recompute, ...extraDeps]);

  return [setRef, h];
}
