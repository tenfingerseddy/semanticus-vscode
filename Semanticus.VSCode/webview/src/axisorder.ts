// What orders a result's date axis, and whether anything really did (UAT N1, Sol H8). Kept apart from the drawing so the
// Queries page and the Ask-data page read the SAME order the chart is drawn in: the caption "Dates run left to right"
// comes from here, never from a field's name.

import type { ResultSet } from './wire';
import { PivotConfig, WellField, familyByName, sortPlan } from './daxwells';
import { friendlyHeader, isDateColumn } from './resultformat.mjs';
import { periodKeys } from './periodorder.mjs';

const fmtKey = (v: unknown) => (v === null || v === undefined ? '' : String(v));

// Time runs left to right, and down, but only where something SET that order. The engine returns SUMMARIZECOLUMNS rows
// in no set order (seen live on FrameworkTesting: 2024-03, 2024-06, 2025-03, 2025-08, 2024-09), so first-seen order made a
// date split zig-zag through time while the chart rules' reason said "Dates run left to right" (verify-queries-redesign D2).
// An order is set by, in this order of trust:
//   1. the model's sort-by column for the field, brought by the query and folded in (foldSortColumns): the order Power BI
//      draws, whatever the labels say (a fiscal month runs Jul to Jun);
//   2. dates the engine sent as text (ISO, so they order as text), or whole numbers in a date-named field (a year, a date
//      key), or calendar labels read by what they say (2024-03, Mar-24, 03/2024, FY25 P3, a month name in a date-named
//      field: periodorder.mjs), for a field the model gives no sort-by.
// Anything else keeps the order it was first seen in, is never re-sorted, and makes no time claim (axisDims says so).
// A field with a sort-by the result does NOT carry (a typed query, an assistant's run) is not read by its labels either:
// they would put "Jul..Jun" in calendar order against the model's own.
export type TimeKey = 'text' | 'number' | Map<string, number | string | null> | Map<string, string> | null;
export function timeKey(res: ResultSet, field: Pick<WellField, 'name' | 'sortBy'>, ci: number): TimeKey {
  const rank = res.sortRank?.[ci];
  if (rank) return rank;
  if (isDateColumn(res.rows, ci)) return 'text';
  if (field.sortBy) return null;
  const dated = familyByName(field.name) === 'date';
  if (dated && res.rows.every((r) => r?.[ci] == null || Number.isInteger(r[ci]))) return 'number';
  return periodKeys(res.rows.map((r) => r?.[ci]), dated, { calendarMonths: !/fiscal|\bfy\b/i.test(field.name) });
}

/** The row and column fields of a result as the chart rules read them. `isDate` is whether the field is a date by its name
 *  or by what the engine sent; `ordered` (a date only) is whether the order it is DRAWN in was really set, so the rules
 *  say "Dates run left to right" only over an axis that does (UAT N1: the caption came from the name alone). */
export function axisDims(res: ResultSet, config: PivotConfig): { name: string; isDate: boolean; ordered?: boolean; distinct: number }[] {
  return [...config.rows, ...config.cols].map((f, i) => {
    const isDate = familyByName(f.name) === 'date' || isDateColumn(res.rows, i);
    return {
      name: friendlyHeader(res.columns[i]?.name ?? f.name), isDate,
      ...(isDate ? { ordered: timeKey(res, f, i) !== null } : {}),
      distinct: new Set(res.rows.map((r) => String(r?.[i] ?? ''))).size,
    };
  });
}

/** A result run from the wells' own query carries the sort-by columns after the fields (buildQuery brings them so the
 *  query can ORDER BY them). The rest of the page counts columns by the wells' layout, so they leave here, and the order
 *  they gave stays: for each field, its labels' sort-by values. A result without them is returned as it is. */
export function foldSortColumns(res: ResultSet, config: PivotConfig): ResultSet {
  const plan = sortPlan(config);
  if (!plan.extra.length || res.columns.length !== plan.group.length + plan.extra.length + config.values.length) return res;
  const at = (rank: Map<string, number | string | null>, label: string, v: unknown) => { if (!rank.has(label)) rank.set(label, v === undefined ? null : (v as number | string | null)); };
  const sortRank = plan.group.map((_, i) => {
    const src = plan.source[i];
    if (src === null) return null;
    const rank = new Map<string, number | string | null>();
    for (const r of res.rows) at(rank, fmtKey(r?.[i]), r?.[src]);
    return rank;
  });
  const keep = (_: unknown, i: number) => i < plan.group.length || i >= plan.group.length + plan.extra.length;
  return { ...res, columns: res.columns.filter(keep), rows: res.rows.map((r) => r.filter(keep)), sortRank };
}
