// The field wells' model and query builder for Queries' Build view (the wells' UI is querywells.tsx). Drag fields from the Fields rail into Rows / Columns
// / Measures / Filters; the wells compose a SUMMARIZECOLUMNS (+ CALCULATETABLE) query that runs through the SAME
// runDax path the editor uses — so a visual IS a query you can benchmark. Filters reuse the grid's filter
// popovers (date range/relative, numeric, text) and translate to DAX boolean predicates, so "filter context"
// is something you build by dragging, not hand-typing.

import type { DaxModel } from './dax';
import type { ColumnFilter, FilterFamily } from './gridfilter';

export type FieldKind = 'measure' | 'column' | 'table';
// `sortBy`: the column the model orders this field by (its sort-by column, same table). It is read from the model, never
// typed by the person, and it travels on the field so buildQuery can bring the column and order by it (UAT N1).
export interface WellField { kind: FieldKind; ref: string; name: string; table?: string; sortBy?: { ref: string; name: string } }
export interface PivotFilter { ref: string; name: string; family: FilterFamily; filter: ColumnFilter }
// `context`: filters the wells cannot hold, as the engine wrote them (a question's own filters, askdata-model.ts
// pivotConfigOf). Never part of the person's wells; it only travels with a result so the number's context is whole.
export interface ContextLine { name: string; value: string; dax: string }
export interface PivotConfig { rows: WellField[]; cols: WellField[]; values: WellField[]; filters: PivotFilter[]; context?: ContextLine[] }

/** Every filter a result was computed under, as DAX: the filter well's lines, then any context the wells cannot hold. */
export function contextFilterLines(c: PivotConfig): string[] {
  return [...c.filters.map(filterToDax).filter((x): x is string => !!x), ...(c.context ?? []).map((l) => l.dax)];
}

export const EMPTY_CONFIG: PivotConfig = { rows: [], cols: [], values: [], filters: [] };

// Parse the text/plain DAX reference the Fields rail puts on the drag (no rail change needed).
export function parseRef(text: string): WellField | null {
  const t = text.trim();
  let m: RegExpExecArray | null;
  if ((m = /^'([^']+)'\[(.+)\]$/.exec(t))) return { kind: 'column', ref: t, table: m[1], name: m[2] };
  if ((m = /^([A-Za-z_]\w*)\[(.+)\]$/.exec(t))) return { kind: 'column', ref: `'${m[1]}'[${m[2]}]`, table: m[1], name: m[2] };
  if ((m = /^\[(.+)\]$/.exec(t))) return { kind: 'measure', ref: t, name: m[1] };
  if ((m = /^'([^']+)'$/.exec(t))) return { kind: 'table', ref: t, name: m[1], table: m[1] };
  return null;
}

// No column type metadata in DaxModel → guess the filter family from the name. Good enough to pick the right
// editor; the user can still adjust. (A later pass can fetch real types via list_columns.)
const DATE_RE = /date|year|month|quarter|day|week|fiscal|period|time/i;
const NUM_RE = /amount|price|cost|qty|quantity|count|sum|total|value|sales|revenue|margin|rate|pct|percent|ratio|duration|ms|seconds|num|size|score|age|balance|weight/i;
export function familyByName(name: string): FilterFamily {
  if (DATE_RE.test(name)) return 'date';
  if (NUM_RE.test(name)) return 'number';
  return 'text';
}

const esc = (s: string) => s.replace(/"/g, '""');

/** Each row and column field's sort-by from the model (`sortByColumn`, a name in the field's own table). A field the model
 *  does not list keeps what it carried: a model not loaded yet says nothing, and nothing is invented. */
export function withSortBy(c: PivotConfig, model: DaxModel): PivotConfig {
  if (!model.columns.length) return c;
  const known = new Map(model.columns.map((m) => [`${m.table}|${m.name}`, m.sortByColumn]));
  const one = (f: WellField): WellField => {
    if (f.kind !== 'column' || !f.table || !known.has(`${f.table}|${f.name}`)) return f;
    const by = known.get(`${f.table}|${f.name}`);
    const { sortBy: _drop, ...rest } = f;
    return by && by !== f.name ? { ...rest, sortBy: { ref: `'${f.table}'[${by}]`, name: by } } : rest;
  };
  const rows = c.rows.map(one), cols = c.cols.map(one);
  const same = (a: WellField[], b: WellField[]) => a.every((f, i) => f === b[i]);
  return same(rows, c.rows) && same(cols, c.cols) ? c : { ...c, rows, cols };
}

/** The group-by fields in query order (rows, then columns), the sort-by columns the query brings after them, and the
 *  position in the group-by list each field's sort-by answer sits at. A sort-by column that is itself a well field is
 *  that field's own column, not brought twice. */
export function sortPlan(c: PivotConfig): { group: WellField[]; extra: { ref: string }[]; source: (number | null)[] } {
  const group = [...c.rows, ...c.cols];
  const extra: { ref: string }[] = [];
  const source = group.map((f) => {
    if (!f.sortBy) return null;
    const own = group.findIndex((g) => g.ref === f.sortBy!.ref);
    if (own >= 0) return own;
    let at = extra.findIndex((x) => x.ref === f.sortBy!.ref);
    if (at < 0) { extra.push({ ref: f.sortBy.ref }); at = extra.length - 1; }
    return group.length + at;
  });
  return { group, extra, source };
}

/** Every column a wells query groups by, in query order: the fields, then the sort-by columns it brought. A grouped column
 *  is in each cell's filter context, so anything that recomputes a cell (Explain this number) must group by all of them. */
export function queryGroupRefs(c: PivotConfig): string[] {
  const { group, extra } = sortPlan(c);
  return [...group.map((f) => f.ref), ...extra.map((x) => x.ref)];
}

/** The ORDER BY of a query: every sort-by column in field order. A model-ordered field is shown in the model's order, as
 *  Power BI does it, so a fiscal "Jul to Jun" month field never runs January first. */
function orderBy(c: PivotConfig): string {
  const { group, source } = sortPlan(c);
  const refs: string[] = [];
  group.forEach((f, i) => { if (source[i] !== null && f.sortBy && !refs.includes(f.sortBy.ref)) refs.push(f.sortBy.ref); });
  return refs.length ? `\nORDER BY ${refs.join(', ')}` : '';
}

// One DAX boolean predicate for a column filter — the bridge that turns a dropped filter into filter context.
export function filterToDax(pf: PivotFilter): string | null {
  const ref = pf.ref;
  const f = pf.filter;
  switch (f.kind) {
    case 'boolean': return `${ref} = ${f.value ? 'TRUE()' : 'FALSE()'}`;
    case 'number': {
      const { op, a, b } = f;
      if (op === 'blank') return `ISBLANK(${ref})`;
      if (op === 'nblank') return `NOT ISBLANK(${ref})`;
      if (op === 'between') {
        const parts: string[] = [];
        if (a != null) parts.push(`${ref} >= ${a}`);
        if (b != null) parts.push(`${ref} <= ${b}`);
        return parts.length ? parts.join(' && ') : null;
      }
      if (a == null) return null;
      const OPS: Record<string, string> = { eq: '=', neq: '<>', gt: '>', gte: '>=', lt: '<', lte: '<=' };
      return `${ref} ${OPS[op]} ${a}`;
    }
    case 'text': {
      const v = f.value.trim();
      const conds: string[] = [];
      if (f.picked.length) conds.push(`${ref} IN {${f.picked.map((p) => `"${esc(p)}"`).join(', ')}}`);
      if (f.op === 'blank') return `ISBLANK(${ref})`;
      if (f.op === 'nblank') return `NOT ISBLANK(${ref})`;
      if (v) {
        if (f.op === 'contains') conds.push(`CONTAINSSTRING(${ref}, "${esc(v)}")`);
        else if (f.op === 'ncontains') conds.push(`NOT CONTAINSSTRING(${ref}, "${esc(v)}")`);
        else if (f.op === 'eq') conds.push(`${ref} = "${esc(v)}"`);
        else if (f.op === 'starts') conds.push(`LEFT(${ref}, ${v.length}) = "${esc(v)}"`);
        else if (f.op === 'ends') conds.push(`RIGHT(${ref}, ${v.length}) = "${esc(v)}"`);
      }
      return conds.length ? conds.join(' && ') : null;
    }
    case 'date': {
      if (f.mode === 'range') {
        const parts: string[] = [];
        if (f.from) parts.push(`${ref} >= ${daxDate(f.from)}`);
        if (f.to) parts.push(`${ref} <= ${daxDate(f.to)}`);
        return parts.length ? parts.join(' && ') : null;
      }
      if (f.mode === 'rel') {
        const n = Math.max(1, f.n | 0);
        let lo: string;
        if (f.unit === 'day') lo = `TODAY() - ${n - 1}`;
        else if (f.unit === 'week') lo = `TODAY() - ${n * 7 - 1}`;
        else if (f.unit === 'month') lo = `EDATE(TODAY(), -${n})`;
        else if (f.unit === 'quarter') lo = `EDATE(TODAY(), -${n * 3})`;
        else lo = `EDATE(TODAY(), -${n * 12})`;
        return `${ref} >= ${lo} && ${ref} <= TODAY()`;
      }
      // calendar periods
      switch (f.period) {
        case 'today': return `${ref} >= TODAY() && ${ref} < TODAY() + 1`;
        case 'thisWeek': return `${ref} >= TODAY() - WEEKDAY(TODAY(), 2) + 1 && ${ref} < TODAY() - WEEKDAY(TODAY(), 2) + 8`;
        case 'thisMonth': return `${ref} >= DATE(YEAR(TODAY()), MONTH(TODAY()), 1) && ${ref} < EDATE(DATE(YEAR(TODAY()), MONTH(TODAY()), 1), 1)`;
        case 'thisQuarter': return `${ref} >= DATE(YEAR(TODAY()), (QUARTER(TODAY()) - 1) * 3 + 1, 1) && ${ref} < EDATE(DATE(YEAR(TODAY()), (QUARTER(TODAY()) - 1) * 3 + 1, 1), 3)`;
        case 'thisYear': return `${ref} >= DATE(YEAR(TODAY()), 1, 1) && ${ref} < DATE(YEAR(TODAY()) + 1, 1, 1)`;
        case 'ytd': return `${ref} >= DATE(YEAR(TODAY()), 1, 1) && ${ref} <= TODAY()`;
      }
      return null;
    }
  }
}

function daxDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `DATE(${+m[1]}, ${+m[2]}, ${+m[3]})` : `TODAY()`;
}

// Build the runnable EVALUATE query from the wells.
export function buildQuery(c: PivotConfig): string {
  const group = queryGroupRefs(c);
  const valExt = c.values.map((v) => `"${v.name}", ${v.ref}`);
  const inner = [...group, ...valExt];
  // Nothing chosen is no query: a placeholder row stood in for it and ran as a real one, so the Answer read Blank (pro UAT 18 Oct, N1).
  if (!inner.length) return '';
  const sc = `SUMMARIZECOLUMNS(\n    ${inner.join(',\n    ')}\n)`;
  const filters = c.filters.map(filterToDax).filter((x): x is string => !!x);
  if (!filters.length) return `EVALUATE\n${sc}${orderBy(c)}`;
  const indented = sc.split('\n').map((l, i) => (i === 0 ? l : '    ' + l)).join('\n');
  return `EVALUATE\nCALCULATETABLE(\n    ${indented},\n    ${filters.join(',\n    ')}\n)${orderBy(c)}`;
}

// Like buildQuery, but wraps each Values measure in EVALUATEANDLOG so the Debug tab captures a labelled
// snapshot per measure — lets "Log each measure" produce trace output without the user hand-editing DAX.
export function buildInstrumentedQuery(c: PivotConfig): string {
  if (!c.values.length) return buildQuery(c);
  const group = queryGroupRefs(c);
  const valExt = c.values.map((v) => `"${v.name}", EVALUATEANDLOG(${v.ref}, "${v.name}")`);
  const sc = `SUMMARIZECOLUMNS(\n    ${[...group, ...valExt].join(',\n    ')}\n)`;
  const filters = c.filters.map(filterToDax).filter((x): x is string => !!x);
  if (!filters.length) return `EVALUATE\n${sc}${orderBy(c)}`;
  const indented = sc.split('\n').map((l, i) => (i === 0 ? l : '    ' + l)).join('\n');
  return `EVALUATE\nCALCULATETABLE(\n    ${indented},\n    ${filters.join(',\n    ')}\n)${orderBy(c)}`;
}

// Sensible, RUNNABLE defaults from the live model — never dead Contoso examples. First measure → Values,
// a date-ish column → Columns, another dimension column → Rows.
export function seedConfig(model: DaxModel): PivotConfig {
  const col = (c: { table: string; name: string }): WellField => ({ kind: 'column', ref: `'${c.table}'[${c.name}]`, name: c.name, table: c.table });
  const meas = model.measures[0];
  const dateCol = model.columns.find((c) => DATE_RE.test(c.name));
  // a low-cardinality-ish dimension: prefer a non-key text column not on the measure's table
  const dim = model.columns.find((c) => !/key$|id$/i.test(c.name) && c !== dateCol && (!meas || c.table !== meas.table))
    ?? model.columns.find((c) => !/key$|id$/i.test(c.name) && c !== dateCol)
    ?? model.columns.find((c) => c !== dateCol);
  return {
    rows: dim ? [col(dim)] : [],
    cols: dateCol ? [col(dateCol)] : [],
    values: meas ? [{ kind: 'measure', ref: `[${meas.name}]`, name: meas.name, table: meas.table }] : [],
    filters: [],
  };
}
