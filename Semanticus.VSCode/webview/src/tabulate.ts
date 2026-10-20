// A flat SUMMARIZECOLUMNS result as a row by column matrix, in the order the model sets for time (UAT N1). Kept apart from the drawing so the
// Query page's visuals (query-model.ts) and the grid draw from the same ordered rows without loading the chart library.
import type { ResultSet } from './wire';
import type { PivotConfig } from './daxwells';
import { timeKey, type TimeKey } from './axisorder';

const fmtKey = (v: unknown) => (v === null || v === undefined ? '' : String(v));
const MAX_ROWS = 400, MAX_COLS = 60;

export interface Matrix {
  rowFieldNames: string[]; colFieldNames: string[]; colKeys: string[]; colIsValues: boolean;
  rows: { keys: unknown[]; cells: unknown[] }[]; totalRows: number; truncatedCols: boolean; measureName: string;
  /** The model's format of each value measure, by measure position (one entry when the columns are a pivot of the first). */
  formats: (string | undefined)[];
  /** The result's sort-by answers (see foldSortColumns), so a cell can name the sort-by values it was drawn in. */
  sortRank?: ResultSet['sortRank'];
}
/** The format of the number in one matrix cell column: each measure's own when the columns are the measures, else the first's. */
const formatAt = (m: Matrix, ci: number) => (m.colIsValues ? m.formats[ci] : m.formats[0]);

// Tabulate the flat SUMMARIZECOLUMNS result into a row × column matrix using the wells' shape.
export function tabulate(res: ResultSet, config: PivotConfig): Matrix | null {
  const nRow = config.rows.length, nCol = config.cols.length, nVal = config.values.length;
  if (!res.rows.length || (nRow === 0 && nVal === 0)) return null;
  const rowFieldNames = config.rows.map((f) => f.name);
  const colFieldNames = config.cols.map((f) => f.name);
  const valStart = nRow + nCol;
  const colIsValues = nCol === 0;
  const measureName = config.values[0]?.name ?? 'Value';

  if (colIsValues) {
    // columns ARE the value measures; one result row per row-group.
    const colKeys = nVal ? config.values.map((v) => v.name) : ['Value'];
    const all = res.rows.map((r, i) => ({ id: String(i), keys: r.slice(0, nRow), cells: r.slice(valStart, valStart + Math.max(1, nVal)) }));
    const order = all.map((x) => x.id);
    timeOrder(order, (id) => all[Number(id)].keys, config.rows.map((f, i) => timeKey(res, f, i)));
    const rows = order.slice(0, MAX_ROWS).map((id) => all[Number(id)]);
    const formats = (nVal ? config.values : [null]).map((_, i) => res.columns[valStart + i]?.format);
    return { rowFieldNames, colFieldNames, colKeys, colIsValues, rows, totalRows: res.rows.length, truncatedCols: false, measureName, formats, sortRank: res.sortRank };
  }

  // pivot: distinct column keys (composite of col fields), first value measure as the cell.
  const colKeySet: string[] = []; const seenCol = new Set<string>(); const colRaw = new Map<string, unknown[]>();
  const rowMap = new Map<string, { keys: unknown[]; byCol: Map<string, unknown> }>(); const rowOrder: string[] = [];
  // Every column key is gathered before any is dropped at MAX_COLS, so the columns kept are the earliest in time,
  // not the first the engine happened to return.
  for (const r of res.rows) {
    const ck = r.slice(nRow, nRow + nCol).map(fmtKey).join(' / ');
    if (!seenCol.has(ck)) { seenCol.add(ck); colKeySet.push(ck); colRaw.set(ck, r.slice(nRow, nRow + nCol)); }
    const rk = r.slice(0, nRow).map(fmtKey).join('␟');
    let e = rowMap.get(rk);
    if (!e) { e = { keys: r.slice(0, nRow), byCol: new Map() }; rowMap.set(rk, e); rowOrder.push(rk); }
    e.byCol.set(ck, r[valStart]);
  }
  timeOrder(colKeySet, (ck) => colRaw.get(ck) ?? [], config.cols.map((f, i) => timeKey(res, f, nRow + i)));
  timeOrder(rowOrder, (rk) => rowMap.get(rk)?.keys ?? [], config.rows.map((f, i) => timeKey(res, f, i)));
  const truncatedCols = colKeySet.length > MAX_COLS;
  colKeySet.length = Math.min(colKeySet.length, MAX_COLS);
  const rows = rowOrder.slice(0, MAX_ROWS).map((rk) => { const e = rowMap.get(rk)!; return { keys: e.keys, cells: colKeySet.map((ck) => (e.byCol.has(ck) ? e.byCol.get(ck) : null)) }; });
  return { rowFieldNames, colFieldNames, colKeys: colKeySet, colIsValues, rows, totalRows: rowOrder.length, truncatedCols, measureName, formats: [res.columns[valStart]?.format], sortRank: res.sortRank };
}

// Two sort values: numbers by size, anything else as text, a value the model gave no sort number last.
const byRank = (x: number | string | null | undefined, y: number | string | null | undefined) => {
  if (x == null || y == null) return x == null ? (y == null ? 0 : 1) : -1;
  return typeof x === 'number' && typeof y === 'number' ? x - y : String(x) < String(y) ? -1 : String(x) > String(y) ? 1 : 0;
};
function timeOrder(keys: string[], partsOf: (k: string) => unknown[], kinds: TimeKey[]) {
  if (!kinds.some(Boolean)) return;
  const firstSeen = kinds.map(() => new Map<string, number>());
  keys.forEach((k) => partsOf(k).forEach((v, i) => { const m = firstSeen[i]; if (m && !m.has(fmtKey(v))) m.set(fmtKey(v), m.size); }));
  const at = new Map(keys.map((k, i) => [k, i]));
  keys.sort((a, b) => {
    const pa = partsOf(a), pb = partsOf(b);
    for (let i = 0; i < kinds.length; i++) {
      const va = pa[i], vb = pb[i];
      if (va === vb) continue;
      if (va == null || vb == null) return va == null ? 1 : -1;   // a blank date sorts last
      const ki = kinds[i];
      const lt = (x: string, y: string) => (x < y ? -1 : x > y ? 1 : 0);
      const d = ki instanceof Map ? byRank(ki.get(fmtKey(va)), ki.get(fmtKey(vb)))
        : ki === 'number' ? Number(va) - Number(vb)
        : ki === 'text' ? lt(String(va), String(vb))
          : (firstSeen[i].get(fmtKey(va)) ?? 0) - (firstSeen[i].get(fmtKey(vb)) ?? 0);
      if (d) return d;
    }
    return (at.get(a) ?? 0) - (at.get(b) ?? 0);
  });
}


/** The result's rows in the order the model sets for time, so the Table tab reads January to December like the Answer and the Matrix do
 *  (pro UAT 18 Oct, N9: the grid showed February, June, August, November). The rows themselves are untouched; only their order changes. */
export function gridRowsInTimeOrder(res: ResultSet, config: PivotConfig | null): unknown[][] {
  if (!config || config.cols.length || !config.rows.length) return res.rows;
  const nRow = config.rows.length;
  const kinds = config.rows.map((f, i) => timeKey(res, f, i));
  if (!kinds.some(Boolean)) return res.rows;
  const ids = res.rows.map((_, i) => String(i));
  timeOrder(ids, (id) => res.rows[Number(id)].slice(0, nRow), kinds);
  return ids.map((id) => res.rows[Number(id)]);
}
