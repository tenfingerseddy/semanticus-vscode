// Pure helpers for gather.mjs: metadata shaping and the scalar DAX that turns column values into DERIVED facts.
//
// Privacy shape. Every data query built here is a scalar probe (EVALUATE ROW(...)) that returns counts only. No query
// returns a row, a raw value, a minimum, a maximum or a sample, so nothing but counts ever reaches this script, and
// only booleans, shares and counts are written out. The engine's agent policy treats a scalar probe as a
// calculation, so this also needs no data-read approval.
import { COUNTRIES, REGIONS, CITIES, list } from './gazetteer.mjs';

// TOM enums as the TMSCHEMA DMVs report them.
export const DATA_TYPE = { 1: 'automatic', 2: 'string', 6: 'int64', 8: 'double', 9: 'dateTime', 10: 'decimal', 11: 'boolean', 17: 'binary', 19: 'unknown', 20: 'variant' };
export const SUMMARIZE_BY = { 1: 'default', 2: 'none', 3: 'sum', 4: 'min', 5: 'max', 6: 'count', 7: 'average', 8: 'distinctcount' };
export const COLUMN_TYPE = { 1: 'data', 2: 'calculated', 3: 'rowNumber', 4: 'calculatedTableColumn' };
export const NUMERIC = new Set(['int64', 'double', 'decimal']);

// Power BI's automatic date tables are generated boilerplate, not an author's decisions.
export const isSystemTable = (name) => /^(LocalDateTable_|DateTableTemplate_)/.test(name ?? '');

// Distinct non-blank values above this are measured on a bounded, deterministic SAMPLE and flagged sampled.
export const SAMPLE_ABOVE = 20_000;
export const SAMPLE_SIZE = 5_000;

export const qTable = (t) => `'${String(t).replace(/'/g, "''")}'`;
export const qCol = (t, c) => `${qTable(t)}[${String(c).replace(/]/g, ']]')}]`;
const qStr = (s) => `"${String(s).replace(/"/g, '""')}"`;
const inList = (items) => `{${items.map(qStr).join(',')}}`;

/** Rows of a DMV ResultSet as objects keyed by column name. */
export function rowsOf(rs) {
  if (!rs || rs.error) throw new Error(rs?.error ? 'query failed' : 'no result');
  const names = (rs.columns ?? []).map((c) => c.name);
  return (rs.rows ?? []).map((r) => Object.fromEntries(names.map((n, i) => [n, r[i]])));
}

const b = (v) => v === true || v === 'true' || v === 1 || v === '1' || v === 'True';

/** Shape TMSCHEMA rows into tables and columns carrying names, types and the five labelled settings. */
export function shapeModel({ tables, columns, relationships, measures }) {
  const cById = new Map(columns.map((c) => [String(c.ID), c]));
  const colName = (c) => c.ExplicitName || c.InferredName;
  const role = new Map();   // columnId -> 'one' | 'many'
  for (const r of relationships) {
    // Cardinality enum: 1 One, 2 Many. The default relationship is many (from) to one (to).
    const fromOne = Number(r.FromCardinality) === 1, toOne = Number(r.ToCardinality) !== 2;
    const set = (id, v) => { const k = String(id); if (role.get(k) !== 'one') role.set(k, v); };
    set(r.FromColumnID, fromOne ? 'one' : 'many');
    set(r.ToColumnID, toOne ? 'one' : 'many');
  }
  const out = [];
  for (const t of tables) {
    if (isSystemTable(t.Name)) continue;
    const cols = columns.filter((c) => String(c.TableID) === String(t.ID) && COLUMN_TYPE[Number(c.Type)] !== 'rowNumber');
    if (!cols.length) continue;
    out.push({
      name: t.Name,
      measures: measures.filter((x) => String(x.TableID) === String(t.ID)).length,
      columns: cols.map((c) => {
        const dt = DATA_TYPE[Number(c.ExplicitDataType)] ?? DATA_TYPE[Number(c.InferredDataType)] ?? 'unknown';
        const sb = c.SortByColumnID != null && c.SortByColumnID !== '' ? cById.get(String(c.SortByColumnID)) : null;
        return {
          id: String(c.ID), name: colName(c), dataType: dt === 'automatic' ? (DATA_TYPE[Number(c.InferredDataType)] ?? 'unknown') : dt,
          isCalculated: COLUMN_TYPE[Number(c.Type)] === 'calculated' || COLUMN_TYPE[Number(c.Type)] === 'calculatedTableColumn',
          isHidden: b(c.IsHidden), summarizeBy: SUMMARIZE_BY[Number(c.SummarizeBy)] ?? 'default',
          dataCategory: c.DataCategory || null, sortByColumn: sb ? colName(sb) : null,
          formatString: c.FormatString || null, isAvailableInMdx: c.IsAvailableInMDX == null ? null : b(c.IsAvailableInMDX),
          isKey: b(c.IsKey), relationshipRole: role.get(String(c.ID)) ?? 'none',
          usedByMeasures: countMeasureRefs(t.Name, colName(c), measures),
          usedInReports: null,   // no report was checked in the replay
        };
      }),
    });
  }
  return out;
}

/** How many measures reference Table[Column] (quoted or bare table name). Counted here; only the count leaves. */
export function countMeasureRefs(table, column, measures) {
  const col = `[${column.replace(/]/g, ']]')}]`.toLowerCase();
  const bare = /^[A-Za-z_][A-Za-z0-9_]*$/.test(table) ? `${table}${col}`.toLowerCase() : null;
  const quoted = `'${table.replace(/'/g, "''")}'${col}`.toLowerCase();
  let n = 0;
  for (const m of measures) {
    const e = String(m.Expression ?? '').toLowerCase().replace(/\s+\[/g, '[');
    if (e.includes(quoted) || (bare && e.includes(bare))) n++;
  }
  return n;
}

const DIGITS = (x) => { let s = x; for (let d = 0; d <= 9; d++) s = `SUBSTITUTE(${s},"${d}","")`; return s; };
const MONTHS = ['JANUARY', 'FEBRUARY', 'MARCH', 'APRIL', 'MAY', 'JUNE', 'JULY', 'AUGUST', 'SEPTEMBER', 'OCTOBER', 'NOVEMBER', 'DECEMBER'];
const MONTH_WORDS = [...MONTHS, ...MONTHS.map((m) => m.slice(0, 3)), 'SEPT'];
const DAYS = ['MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY', 'SUNDAY'];
const DAY_WORDS = [...DAYS, ...DAYS.map((d) => d.slice(0, 3)), 'TUES', 'THUR', 'THURS'];
const QUARTER_WORDS = [1, 2, 3, 4].flatMap((q) => [`Q${q}`, `QTR ${q}`, `QTR${q}`, `QUARTER ${q}`]);

/**
 * One scalar query per column. Fields come back in the order of the returned `fields`. V is the non-blank distinct
 * value set, or a deterministic SAMPLE of it when the column is large.
 */
export function columnFactsQuery(table, column, dataType, sample) {
  const c = qCol(table, column);
  const V = sample
    ? `SAMPLE(${SAMPLE_SIZE}, FILTER(ALLNOBLANKROW(${c}), NOT ISBLANK(${c})), ${c}, ASC)`
    : `FILTER(ALLNOBLANKROW(${c}), NOT ISBLANK(${c}))`;
  const cnt = (pred) => `COUNTROWS(FILTER(__v, ${pred}))`;
  const numeric = NUMERIC.has(dataType), text = dataType === 'string';
  const u = `UPPER(TRIM(${c}))`;
  const zero = '0';
  const f = {
    rows: `COUNTROWS(${qTable(table)})`,
    distinct: `DISTINCTCOUNT(${c})`,
    blanks: `COUNTBLANK(${c})`,
    nb: `COUNTROWS(__v)`,
    nonWhole: numeric && dataType !== 'int64' ? cnt(`${c} <> INT(${c})`) : zero,
    yearN: numeric ? cnt(`${c} >= 1900 && ${c} <= 2100 && ${c} = INT(${c})`)
      : text ? cnt(`LEN(${u}) = 4 && LEN(${DIGITS(u)}) = 0 && LEFT(${u}, 2) IN {"19","20"}`) : zero,
    month12N: numeric ? cnt(`${c} >= 1 && ${c} <= 12 && ${c} = INT(${c})`) : zero,
    quarter4N: numeric ? cnt(`${c} >= 1 && ${c} <= 4 && ${c} = INT(${c})`)
      : text ? cnt(`${u} IN ${inList(QUARTER_WORDS)}`) : zero,
    monthNameN: text ? cnt(`${u} IN ${inList(MONTH_WORDS)}`) : zero,
    dayNameN: text ? cnt(`${u} IN ${inList(DAY_WORDS)}`) : zero,
    countryN: text ? cnt(`${u} IN ${inList(list(COUNTRIES))}`) : zero,
    regionN: text ? cnt(`${u} IN ${inList(list(REGIONS))}`) : zero,
    cityN: text ? cnt(`${u} IN ${inList(list(CITIES))}`) : zero,
    // AU / NZ 4 digits, US 5 digits or ZIP+4, UK and Canadian shapes by length and a leading letter.
    postcodeN: text ? cnt(`(LEN(${DIGITS(u)}) = 0 && LEN(${u}) IN {4,5}) || (LEN(${u}) = 10 && MID(${u},6,1) = "-" && LEN(${DIGITS(`SUBSTITUTE(${u},"-","")`)}) = 0) || (LEN(${u}) IN {6,7,8} && CONTAINSSTRING(${u}, " ") && LEN(${DIGITS(u)}) <= LEN(${u}) - 3 && LEN(${DIGITS(u)}) >= LEN(${u}) - 6 && NOT CONTAINSSTRING(TRIM(MID(${u}, 1, 1)), " "))`) : zero,
    urlN: text ? cnt(`LEFT(LOWER(${c}), 7) = "http://" || LEFT(LOWER(${c}), 8) = "https://" || LEFT(LOWER(${c}), 4) = "www."`) : zero,
    imageUrlN: text ? cnt(`(LEFT(LOWER(${c}), 4) = "http" || LEFT(LOWER(${c}), 4) = "www.") && (RIGHT(LOWER(${c}), 4) IN {".png",".jpg",".gif",".svg",".bmp"} || RIGHT(LOWER(${c}), 5) IN {".jpeg",".webp"})`) : zero,
    emailN: text ? cnt(`CONTAINSSTRING(${c}, "@") && CONTAINSSTRING(${c}, ".") && NOT CONTAINSSTRING(${c}, " ")`) : zero,
  };
  const body = Object.entries(f).map(([k, v]) => `"${k}", ${v}`).join(',\n  ');
  return { fields: Object.keys(f), query: `DEFINE VAR __v = ${V}\nEVALUATE ROW(\n  ${body}\n)` };
}

/** Turns the counts from columnFactsQuery into the contract's measured block. No count below is a raw value. */
export function deriveMeasured(counts, dataType, sampled, storage) {
  const n = (k) => Number(counts[k] ?? 0);
  const rows = n('rows'), nb = n('nb');
  const share = (k) => (nb > 0 ? round(n(k) / nb) : 0);
  const all = (k) => nb > 0 && n(k) === nb;
  const distinct = n('distinct');
  return {
    distinct,
    blankShare: rows > 0 ? round(n('blanks') / rows) : 0,
    wholeNumbers: NUMERIC.has(dataType) ? n('nonWhole') === 0 : false,
    uniqueOnEveryRow: rows > 0 && n('blanks') === 0 && distinct === rows,
    looksLikeYear: all('yearN') && nb >= 1,
    looksLikeMonthName: all('monthNameN') && nb >= 2,
    looksLikeMonthNumber: all('month12N') && nb >= 6 && nb <= 12,
    looksLikeDayName: all('dayNameN') && nb >= 2,
    looksLikeQuarter: all('quarter4N') && nb >= 2 && nb <= 4,
    placeShare: { country: share('countryN'), region: share('regionN'), city: share('cityN'), postcode: share('postcodeN') },
    urlShare: share('urlN'), imageUrlShare: share('imageUrlN'), emailShare: share('emailN'),
    oneToOneWith: [],
    dictionaryBytes: storage?.dictionaryBytes ?? null, dataBytes: storage?.dataBytes ?? null,
    cardinality: distinct,
    sampled: !!sampled,
  };
}

const round = (x) => Math.round(x * 1000) / 1000;

/**
 * Candidate pairs for oneToOneWith inside one table: same distinct count, more than one value. Pairs where both
 * columns are unique on every row are one to one without a query. Returns { known, toTest, capped }.
 */
export function oneToOnePlan(cols, cap = 400) {
  const byD = new Map();
  for (const c of cols) {
    const d = c.measured?.distinct ?? 0;
    if (d > 1) (byD.get(d) ?? byD.set(d, []).get(d)).push(c);
  }
  const known = [], toTest = [];
  for (const group of byD.values()) {
    for (let i = 0; i < group.length; i++) for (let j = i + 1; j < group.length; j++) {
      const a = group[i], b2 = group[j];
      if (a.measured.uniqueOnEveryRow && b2.measured.uniqueOnEveryRow) known.push([a, b2]);
      else toTest.push([a, b2]);
    }
  }
  return { known, toTest: toTest.slice(0, cap), capped: toTest.length > cap };
}

/** One scalar query testing up to `pairs.length` pairs: distinct(pair) per pair. */
export function pairQuery(table, pairs) {
  const body = pairs.map(([a, b2], i) =>
    `"p${i}", COUNTROWS(SUMMARIZE(${qTable(table)}, ${qCol(table, a.name)}, ${qCol(table, b2.name)}))`).join(',\n  ');
  return `EVALUATE ROW(\n  ${body}\n)`;
}
