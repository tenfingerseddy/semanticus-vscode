// The Query page's pure half (1.6): which visuals can draw a result, and a result as the shared visual set's data.
//
// THE RULES are lifted from Graphicus at 84812e04 (docs/query-visuals.md): the shape each visual needs and its limits. They
// narrow the visuals to the ones that CAN draw a result (`fits`); Jev may pick one of them (queries.chart_kind, JevChartPick.cs)
// and the person may press any other that fits. A visual that cannot be drawn honestly from the rows is never offered: a total
// card never adds a column of rows into a total (a ratio added up is a wrong number shown as right), and a heat colour is never
// put on a plain amount.
import type { ResultSet } from './wire';
import type { PivotConfig } from './daxwells';
import { tabulate } from './tabulate';
import { columnFormatter, friendlyHeader, plainNumber } from './resultformat.mjs';
import { formatNumber } from './gridformat';
import type { Data, Fmt, Kind, Row } from './visuals';

/** The visuals the Query page can offer for a result. Waterfall and heat are the Explain card's and Validation's. */
export const QUERY_KINDS: Kind[] = ['total', 'bars', 'groups', 'line', 'variance', 'dumbbell', 'bullet', 'slope', 'multiples', 'sparktable', 'table'];

export interface QueryDim { name: string; isDate: boolean; ordered?: boolean; distinct: number }
export interface QueryShape {
  dims: QueryDim[]; measures: number; rows: number;
  /** Every number is in one unit (same format string), so two of them can be set against each other. */
  sameUnit?: boolean;
  /** A negative number is in the result (a bullet draws from zero, so it cannot). */
  negatives?: boolean;
  /** The model's name for each number, so Jev's question names them (one call for each answer, as before). */
  measureNames?: string[];
}
export interface QueryFits { pick: Kind; fits: Kind[]; reason: string }

const MEMBERS = {
  bars: { min: 1, max: 50 }, groups: { min: 2, max: 30 }, variance: { min: 2, max: 30 }, dumbbell: { min: 3, max: 30 }, bullet: { min: 1, max: 15 },
  slope: { min: 3, max: 20 }, multiples: { min: 2, max: 12 }, sparktable: { min: 5, max: 50 },
} as const;
const MIN_PERIODS = 6;
/** Bars tell at most this many series apart. */
const SERIES_MAX = 8;
const inRange = (n: number, r: { min: number; max: number }) => n >= r.min && n <= r.max;
const lower = (s: string) => (s.length > 1 && s === s.toUpperCase() ? s : s.toLowerCase());

/** Which visuals can draw this shape, in the order they are offered, the rules' pick first, and the one sentence why. */
export function queryVisualFits(shape: QueryShape): QueryFits {
  const dims = shape.dims, m = Math.max(0, shape.measures), rows = Math.max(0, shape.rows);
  const dates = dims.filter((d) => d.isDate && d.ordered !== false);
  const cats = dims.filter((d) => !dates.includes(d));
  const only = (reason: string): QueryFits => ({ pick: 'table', fits: ['table'], reason });
  if (m === 0) return only('The result has no number in it, so it shows as rows.');
  if (rows === 0) return only('The result has no rows.');
  const pair = m === 2 && shape.sameUnit === true;

  if (dims.length === 0) {
    if (rows === 1 && (m === 1 || pair)) return { pick: 'total', fits: ['total', 'table'], reason: m === 1 ? 'One number, so a total card.' : 'One number set against a second, so a total card with the two side by side.' };
    return only(m > 2 ? `${m} numbers with no split, so they show side by side as rows.` : 'Two numbers in different units, so they show as rows.');
  }
  if (dims.length === 1 && dates.length === 1) {
    if (rows >= 2 && (m === 1 || pair)) return { pick: 'line', fits: m === 1 ? ['line', 'bars', 'table'] : ['line', 'table'], reason: m === 1 ? 'Dates run left to right, so a line.' : 'Dates run left to right, with the second number as a dashed line.' };
    return only('Too few dates to draw a line, so the rows are shown.');
  }
  if (dims.length === 1 && cats.length === 1) {
    const k = rows;
    if (m === 1) {
      if (inRange(k, MEMBERS.bars)) return { pick: 'bars', fits: ['bars', 'table'], reason: `One number for each ${lower(cats[0].name)}, so ranked bars.` };
      return only(`${k} values of ${lower(cats[0].name)} are too many for bars, so they show as rows.`);
    }
    if (pair) {
      const fits: Kind[] = [];
      if (inRange(k, MEMBERS.dumbbell)) fits.push('dumbbell');
      if (inRange(k, MEMBERS.variance)) fits.push('variance');
      if (inRange(k, MEMBERS.bullet) && !shape.negatives) fits.push('bullet');
      if (inRange(k, MEMBERS.slope)) fits.push('slope');
      if (fits.length) return { pick: fits[0], fits: [...fits, 'table'], reason: `Two values for each ${lower(cats[0].name)}, so each gap is drawn.` };
    }
    return only(m === 2 ? 'Two numbers in different units, so they show as rows.' : `${m} numbers for each ${lower(cats[0].name)}, so they show as rows.`);
  }
  if (dims.length === 2 && dates.length === 1 && cats.length === 1 && m === 1) {
    const members = cats[0].distinct, periods = dates[0].distinct;
    if (periods >= MIN_PERIODS) {
      const fits: Kind[] = [];
      if (inRange(members, MEMBERS.multiples)) fits.push('multiples');
      if (inRange(members, MEMBERS.sparktable)) fits.push('sparktable');
      if (fits.length) return { pick: fits[0], fits: [...fits, 'table'], reason: `${members} values of ${lower(cats[0].name)}, each over ${periods} ${lower(dates[0].name)} values, so one small trend each.` };
    }
  }
  // Two time splits (a year, then its months) are one run of time: one line along it, not a list (UAT 17 Oct, P20a: 24 months showed as rows).
  if (dims.length === 2 && dates.length === 2 && m === 1 && rows >= 2) return { pick: 'line', fits: ['line', 'bars', 'table'], reason: 'Dates run left to right, so a line.' };
  // One number split by two fields: a cluster of bars for each member of the one with more values, a bar in it for each of the other.
  // Few enough of each to tell apart, else the rows (UAT 18 Oct, worst 4: "Net Sales by Brand and Year" had no number and no chart).
  if (dims.length === 2 && m === 1 && rows >= 2) {
    const [more, fewer] = dims[0].distinct >= dims[1].distinct ? [dims[0], dims[1]] : [dims[1], dims[0]];
    if (fewer.distinct >= 2 && fewer.distinct <= SERIES_MAX && inRange(more.distinct, MEMBERS.groups))
      return { pick: 'groups', fits: ['groups', 'table'], reason: `One number for each ${lower(more.name)}, with a bar for each ${lower(fewer.name)}.` };
  }
  return only('Two ways to split the number, so a grid shows them side by side.');
}

export const KIND_SEES: Record<Kind, string> = {
  total: 'The number large, with what it is set against.',
  bars: 'The members ranked, biggest first.',
  groups: 'Each member with one bar for each value of the second field.',
  line: 'The number along the dates.',
  variance: 'How far one number is from the other, per member.',
  dumbbell: 'The two values and the gap, per member.',
  bullet: 'A value against its mark.',
  slope: 'Before and after, member by member.',
  multiples: 'One small trend for each member.',
  waterfall: 'The steps from a start to an end.',
  sparktable: 'A trend beside each member\'s figures.',
  heat: 'Where the counts sit.',
  table: 'The rows themselves.',
};

// ---- a result as the visual set's data

const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
/** The number format of one measure column: how a value reads, and which kind of unit it is (a percent draws differences as points). */
export function formatOf(format?: string | null): { fmt: Fmt; format: (v: number) => string } {
  return { fmt: format && /%/.test(format) ? 'pct' : 'int', format: (v) => formatNumber(v, format) ?? plainNumber(v) };
}

/** What kind of number a measure is, from its name, for the case where the model gives it no format string. A count is never an
 *  amount; a name that says neither is unknown, and an unknown number is not assumed to share a unit with anything. */
export type MeasureKind = 'count' | 'ratio' | 'amount' | 'unknown';
const COUNT_NAME = /\b(count|counts|number of|no\.? of|qty|quantity|units?|orders?|customers?|visits?|items?|rows|headcount)\b|^#|#$/i;
const RATIO_NAME = /%|\b(percent|pct|rate|ratio|share)\b/i;
const AMOUNT_NAME = /\b(sales|revenue|amount|cost|costs|profit|price|spend|budget|target|forecast|plan|goal|expected|prior|previous|last year|ly|py|total|income|margin|value)\b/i;
export function measureKind(name: string): MeasureKind {
  if (RATIO_NAME.test(name)) return 'ratio';
  if (COUNT_NAME.test(name)) return 'count';
  if (AMOUNT_NAME.test(name)) return 'amount';
  return 'unknown';
}

/** Same format string means same unit, except a count and an amount that share a plain format. With no format string at all the
 *  measure's kind decides, and an unknown kind is treated as a different unit (a count drawn against an amount is a wrong picture). */
export function unitsAgree(res: ResultSet, config: PivotConfig): boolean {
  const nm = config.values.length;
  if (nm < 2) return true;
  const valStart = config.rows.length + config.cols.length;
  const formats = Array.from({ length: nm }, (_, i) => res.columns[valStart + i]?.format ?? '');
  const kinds = Array.from({ length: nm }, (_, i) => measureKind(config.values[i]?.name ?? res.columns[valStart + i]?.name ?? ''));
  const sameFormat = formats.every((f) => f === formats[0]);
  const blankFormats = formats.every((f) => !f);
  const kindsAgree = kinds.every((k) => k === kinds[0]) && (!blankFormats || kinds[0] !== 'unknown');
  return sameFormat && kindsAgree;
}

/** The shape of a result for the rules: the splits and their counts, how many numbers, how many rows, and whether the numbers share a unit. */
export function queryShape(res: ResultSet, config: PivotConfig, dims: QueryDim[]): QueryShape {
  const m = tabulate(res, config);
  const nm = config.values.length;
  const valStart = config.rows.length + config.cols.length;
  const sameUnit = unitsAgree(res, config);
  const nums = res.rows.flatMap((r) => r.slice(valStart, valStart + nm)).filter((v): v is number => typeof v === 'number');
  return { dims, measures: nm, measureNames: config.values.map((v) => v.name).filter(Boolean), rows: m ? (config.cols.length ? m.rows.length : res.rows.length) : 0, sameUnit, negatives: nums.some((v) => v < 0) };
}

/** The result drawn as one of the visual set's kinds, or null when its numbers cannot be drawn that way honestly (a member with a
 *  missing period, a pivot the kind cannot read). `table` and the rest of the single-split kinds share one builder. */
export function visualData(kind: Kind, res: ResultSet, config: PivotConfig): Data | null {
  const m = tabulate(res, config);
  if (!m) return null;
  const nRow = config.rows.length, nCol = config.cols.length, nVal = config.values.length;
  const valStart = nRow + nCol;
  // A number is named as the model names it ("Net Sales"), not sentence-cased like a grid header.
  const measureName = (i: number) => config.values[i]?.name ?? friendlyHeader(res.columns[valStart + i]?.name ?? 'Value');
  const fmtOf = (i: number) => formatOf(res.columns[valStart + i]?.format);
  const label = (keys: unknown[]) => keys.map((k, i) => columnFormatter(res.rows, i, config.rows[i]?.name)(k)).join(' / ');
  const first = fmtOf(0);
  const base = { fmt: first.fmt, format: first.format, dimLabel: config.rows.map((f) => friendlyHeader(f.name)).join(' / ') };

  if (kind === 'multiples' || kind === 'sparktable') {
    const grid = seriesGrid(m, res, config);
    if (!grid) return null;
    const rows: Row[] = grid.members.map((name, i) => ({ label: name, v: grid.cells[i][grid.cells[i].length - 1], series: grid.cells[i] as number[] }));
    return { ...base, rows, vLabel: grid.vLabel ?? measureName(0), periods: grid.periods, dimLabel: grid.dimLabel };
  }
  if (kind === 'groups') {
    const g = groupGrid(m, res, config);
    if (!g) return null;
    const rows: Row[] = g.members.map((name, i) => ({ label: name, v: g.cells[i].find((v) => v !== null) ?? null, series: g.cells[i] as number[] }));
    return { ...base, rows, vLabel: measureName(0), periods: g.series, dimLabel: g.dimLabel };
  }
  if (!m.colIsValues) {
    // A pivot has no single column of numbers, so its table is the grid itself: a row for each member, a column for each value across the top.
    if (kind !== 'table') return null;
    const cells = m.rows.map((r) => r.cells.map(num));
    return { ...base, rows: m.rows.map((r) => ({ label: label(r.keys), v: null })), cols: m.colKeys, cells, vLabel: measureName(0), dimLabel: base.dimLabel };
  }
  if (kind === 'total') {
    if (m.rows.length !== 1) return null;
    const cells = m.rows[0].cells.map(num);
    const prior = nVal > 1 ? cells[1] : null;
    if (cells[0] === null) return null;                    // a blank number has no total card (the table says "blank")
    return { ...base, rows: [{ label: measureName(0), v: cells[0] }], vLabel: measureName(0), v2Label: nVal > 1 ? measureName(1) : undefined,
      total: { value: cells[0], label: measureName(0), prior: prior ?? undefined, priorLabel: nVal > 1 ? measureName(1) : undefined, mark: prior === null ? 'none' : 'pair' } };
  }
  const rows: Row[] = m.rows.map((r) => ({ label: label(r.keys), v: num(r.cells[0]), v2: nVal > 1 ? num(r.cells[1]) : undefined }));
  if (kind !== 'table' && kind !== 'bars' && kind !== 'line' && rows.some((r) => r.v === null || r.v2 === null || r.v2 === undefined)) return null;
  // An amount beside a quantity has no gap to state: both numbers show, the Difference and % do not.
  return { ...base, rows, vLabel: measureName(0), v2Label: nVal > 1 ? measureName(1) : undefined, noGap: nVal > 1 && !unitsAgree(res, config) || undefined };
}

/** Members by series for the grouped bars: the field with more values is the members, the one with fewer is the series, wherever the person
 *  put them (two row fields, or one down and one across). A missing number stays a gap. Null with other shapes or too many to tell apart. */
function groupGrid(m: NonNullable<ReturnType<typeof tabulate>>, res: ResultSet, config: PivotConfig): { members: string[]; series: string[]; cells: (number | null)[][]; dimLabel: string } | null {
  const fieldFmt = (ri: number) => columnFormatter(res.rows, ri, config.rows[ri]?.name);
  if (config.values.length !== 1) return null;
  let members: string[], series: string[], cells: (number | null)[][], dimLabel: string;
  if (config.cols.length === 1 && config.rows.length === 1) {
    members = m.rows.map((r) => fieldFmt(0)(r.keys[0])); series = m.colKeys;
    cells = m.rows.map((r) => r.cells.map(num)); dimLabel = friendlyHeader(config.rows[0].name);
    if (series.length > members.length) { cells = series.map((_, ci) => cells.map((row) => row[ci])); [members, series] = [series, members]; dimLabel = friendlyHeader(config.cols[0].name); }
  } else if (config.cols.length === 0 && config.rows.length === 2 && m.colIsValues) {
    const keys = (i: number) => [...new Set(m.rows.map((r) => fieldFmt(i)(r.keys[i])))];
    const a = keys(0), b = keys(1);
    const [mi, si] = a.length >= b.length ? [0, 1] : [1, 0];
    members = mi === 0 ? a : b; series = mi === 0 ? b : a; dimLabel = friendlyHeader(config.rows[mi].name);
    const at = new Map<string, number | null>();
    for (const r of m.rows) at.set(fieldFmt(mi)(r.keys[mi]) + '\u241f' + fieldFmt(si)(r.keys[si]), num(r.cells[0]));
    cells = members.map((mem) => series.map((s) => at.get(mem + '\u241f' + s) ?? null));
  } else return null;
  if (members.length < MEMBERS.groups.min || members.length > MEMBERS.groups.max || series.length < 2 || series.length > SERIES_MAX) return null;
  return { members, series, cells, dimLabel };
}

/** Members by periods: a date across the columns and a category down the rows, or the reverse, or both down the rows. Null when a
 *  member lacks a period, because a trend with a hole in it would be drawn as if the number were zero. */
function seriesGrid(m: NonNullable<ReturnType<typeof tabulate>>, res: ResultSet, config: PivotConfig): { members: string[]; periods: string[]; cells: (number | null)[][]; vLabel?: string; dimLabel?: string } | null {
  const fieldFmt = (ri: number) => columnFormatter(res.rows, ri, config.rows[ri]?.name);
  const isDateField = (ri: number) => config.rows[ri] && /date|month|year|quarter|week|day|period/i.test(config.rows[ri].name);
  if (config.cols.length === 1 && config.rows.length === 1) {
    const dateInCols = /date|month|year|quarter|week|day|period/i.test(config.cols[0].name);
    if (dateInCols) {
      const cells = m.rows.map((r) => r.cells.map(num));
      if (cells.some((row) => row.some((v) => v === null))) return null;
      return { members: m.rows.map((r) => fieldFmt(0)(r.keys[0])), periods: m.colKeys, cells, dimLabel: friendlyHeader(config.rows[0].name) };
    }
    const cells = m.colKeys.map((_, ci) => m.rows.map((r) => num(r.cells[ci])));
    if (cells.some((row) => row.some((v) => v === null))) return null;
    return { members: m.colKeys, periods: m.rows.map((r) => fieldFmt(0)(r.keys[0])), cells, dimLabel: friendlyHeader(config.cols[0].name) };
  }
  if (config.cols.length === 0 && config.rows.length === 2 && m.colIsValues) {
    const di = isDateField(0) && !isDateField(1) ? 0 : isDateField(1) && !isDateField(0) ? 1 : -1;
    if (di < 0) return null;
    const ci = 1 - di;
    const periods: string[] = [], members: string[] = [];
    const at = new Map<string, Map<string, number | null>>();
    for (const r of m.rows) {
      const p = fieldFmt(di)(r.keys[di]), mem = fieldFmt(ci)(r.keys[ci]);
      if (!periods.includes(p)) periods.push(p);
      if (!members.includes(mem)) { members.push(mem); at.set(mem, new Map()); }
      at.get(mem)!.set(p, num(r.cells[0]));
    }
    const cells = members.map((mem) => periods.map((p) => at.get(mem)!.get(p) ?? null));
    if (cells.some((row) => row.some((v) => v === null))) return null;
    return { members, periods, cells, dimLabel: friendlyHeader(config.rows[ci].name) };
  }
  return null;
}

/** The Answer's headline for a result that no sentence came with (Build and DAX): what was asked, in plain words, and what came back. */
export function headlineOf(res: ResultSet | null, config: PivotConfig | null, busy: boolean): string {
  if (busy) return 'Building, checking and running the query.';
  if (!res) return 'Nothing has run yet. Ask a question, choose fields, or write a query, then run it.';
  const rows = res.rowCount ?? res.rows.length;
  if (!rows) return 'No rows came back. Nothing was added up, so no total is shown in its place.';
  if (config && config.values.length) {
    const names = config.values.map((v) => v.name);
    const by = [...config.rows, ...config.cols].map((f) => f.name);
    const what = names.length > 2 ? `${names.slice(0, 2).join(', ')} and ${names.length - 2} more` : names.join(' and ');
    if (rows === 1 && !by.length) {
      const v = res.rows[0]?.[config.rows.length + config.cols.length];
      if (typeof v === 'number') return `${what} is ${formatOf(res.columns[config.rows.length + config.cols.length]?.format).format(v)}.`;
      if (v === null && names.length === 1) return `${what} is blank.`;
    }
    return `${what}${by.length ? ` by ${by.join(' and ')}` : ''}: ${rows.toLocaleString('en-US')} ${rows === 1 ? 'row' : 'rows'}${res.truncated ? ', stopped at the row limit' : ''}.`;
  }
  return `Your query returned ${rows.toLocaleString('en-US')} ${rows === 1 ? 'row' : 'rows'}${res.truncated ? ', stopped at the row limit' : ''}.`;
}

export interface ReadPart { part: string; value: string }
/** Read as, for the Build view: the fields chosen, in the words the Ask chips use. Always shown (the K21 harm is a filter dropped without a word). */
export function readAsOfWells(config: PivotConfig): ReadPart[] {
  const out: ReadPart[] = [];
  for (const v of config.values) out.push({ part: 'Number', value: v.name });
  for (const f of config.rows) out.push({ part: 'Split by', value: f.name });
  for (const f of config.cols) out.push({ part: 'Across', value: f.name });
  for (const f of config.filters) out.push({ part: 'Filter', value: f.name });
  return out;
}

/** Hero 25 H11: the fields of a result nobody built from fields (a hand-written query), read from the result itself: the leading text or date
 *  columns are the split, the numbers after them are the measures. Used only to DRAW the rows like any other answer; nothing here names a
 *  measure of the model, so it is never what Explain or a check is built from. Null when the shape is not "a split, then numbers". */
export function configOfResult(res: ResultSet | null | undefined): PivotConfig | null {
  if (!res || res.error || res.rows.length < 2) return null;
  const isNum = (c: number) => { const v = res.rows.find((r) => r[c] !== null && r[c] !== undefined)?.[c]; return typeof v === 'number'; };
  let split = 0;
  while (split < res.columns.length && !isNum(split)) split++;
  if (split < 1 || split > 2 || split === res.columns.length) return null;
  for (let c = split; c < res.columns.length; c++) if (!isNum(c)) return null;
  const plain = (name: string) => { const m = /\[((?:[^\]]|\]\])+)\]\s*$/.exec(name); return m ? m[1].replace(/\]\]/g, ']') : name; };
  const field = (c: number, kind: 'column' | 'measure') => ({ kind, ref: res.columns[c].name, name: plain(res.columns[c].name) });
  return {
    rows: res.columns.slice(0, split).map((_, c) => field(c, 'column')), cols: [],
    values: res.columns.slice(split).map((_, i) => field(split + i, 'measure')), filters: [],
  };
}

/** What Matrix draws: the last split moves across the top, so Year by Month is one row per year with the months in a row (UAT 17 Oct, P23 + F28:
 *  Matrix looked the same as Grid). Only with one number and two or more splits; any other shape is the grid's already, so Matrix is not offered. */
export function matrixConfigOf(c: PivotConfig | null): PivotConfig | null {
  if (!c || c.cols.length > 0 || c.rows.length < 2 || c.values.length !== 1) return null;
  return { ...c, rows: c.rows.slice(0, -1), cols: [c.rows[c.rows.length - 1]] };
}

/** True when the text typed in the question box is a DAX query, not a question: it starts (after any comment lines) with EVALUATE or DEFINE.
 *  It is run as written, as its own row, and never sent to be read in words (v-batch1: Jev was asked to read "EVALUATE ROW(...)"). */
export const looksLikeDax = (text: string | null | undefined): boolean => /^\s*(?:(?:\/\/|--)[^\n]*\n\s*)*(?:EVALUATE|DEFINE)\b/i.test(text ?? '');

/** How well a finder's words match a field, best first: 5 the exact name (case, spaces and underscores folded), 4 a name that starts
 *  with the words, 3 a name that holds them, 2 a name one letter off (names and words of 4 or more letters only: "brnd" finds Brand),
 *  1 the description, 0 nothing. UAT 18 Oct, N6: "year" + Enter took fiscal_year because the exact "year" was fourth in the list. */
export function fieldRank(find: string, name: string, description?: string | null): 0 | 1 | 2 | 3 | 4 | 5 {
  const fold = (t: string) => t.toLowerCase().replace(/[\s_]+/g, '');
  const f = fold(find);
  if (!f) return 3;
  const n = fold(name);
  if (n === f) return 5;
  if (n.startsWith(f)) return 4;
  if (n.includes(f)) return 3;
  if (f.length >= 4 && [n, ...name.toLowerCase().split(/[\s_]+/).filter(Boolean)].some((w) => w.length >= 4 && oneOff(w, f))) return 2;
  return description && description.toLowerCase().includes(find.trim().toLowerCase()) ? 1 : 0;
}

/** True when the two words differ by one letter added, dropped or changed. */
function oneOff(a: string, b: string): boolean {
  if (Math.abs(a.length - b.length) > 1) return false;
  let i = 0;
  while (i < a.length && i < b.length && a[i] === b[i]) i++;
  if (i === a.length && i === b.length) return false;
  if (a.length === b.length) return a.slice(i + 1) === b.slice(i + 1);
  return a.length > b.length ? a.slice(i + 1) === b.slice(i) : a.slice(i) === b.slice(i + 1);
}

/** The fields a finder matches, best match first and, within a match, in the model's order. Enter takes the first. */
export function rankFields<T extends { name: string; description?: string | null }>(find: string, items: readonly T[]): T[] {
  return items.map((x, i) => ({ x, i, k: fieldRank(find, x.name, x.description) })).filter((e) => e.k > 0)
    .sort((a, b) => b.k - a.k || a.i - b.i).map((e) => e.x);
}

/** How well a finder's words match a field: 2 by its name, 1 by its description, 0 not at all (UAT 17 Oct, F41: "sales" did not find "Total net amount",
 *  which the model describes as sales). The name always ranks first. */
export function fieldMatch(find: string, name: string, description?: string | null): 0 | 1 | 2 {
  const k = fieldRank(find, name, description);
  return k >= 2 ? 2 : (k as 0 | 1);
}

/** A calculation-steps run replaces the result on screen only when it logged steps. A failed or unsupported one leaves the result, and
 *  the plan recorded beside it, exactly as they were (UAT 18 Oct, N3: the plan was wiped by a button the server could not honour). */
export function logRunUsable(r: { error?: string | null; entries?: readonly unknown[] | null }): boolean {
  return !r.error && !!r.entries && r.entries.length > 0;
}
