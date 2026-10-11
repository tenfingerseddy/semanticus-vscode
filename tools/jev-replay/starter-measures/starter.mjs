// Pure helpers for the starter measures replay (T268). No I/O, no network: gather.mjs, build-calls.mjs and
// score.mjs all call in here, and the self-test checks this file against the engine's own C#.
//
// WHAT THIS MIRRORS, AND WHERE. Each piece below copies one engine rule so the replay asks exactly what the product
// asks and scores exactly what the product would propose:
//   factTables, candidates   Semanticus.Engine/Plan/JevFamilies/StarterMeasureTemplates.cs (FactTables, Candidates)
//   ruleChoice               the same file, RuleChoice
//   snapshot, QUESTION       Semanticus.Engine/Plan/JevFamilies/StarterMeasuresFamily.cs (Snapshot, Questions)
// test/starter.test.mjs reads those C# files and fails on any difference in the criteria, the instructions or the
// option list, so this copy cannot drift from the engine in silence.
//
// THE LABEL. For each candidate column, the measures that already exist say what its author chose:
//   a plain aggregation measure over it (SUM, AVERAGE, MIN, MAX, COUNT or COUNTA, DISTINCTCOUNT or
//   DISTINCTCOUNTNOBLANK of Table[Column], or the X form over its own table)   the matching option(s)
//   no measure names it at all                                                 none
//   only measures that do something else with it                               other (not scored: no plain answer)
// Measure expressions are read ONLY on the machine that holds the model. Nothing here writes one out: the label is
// an option id.

// TOM enums as the TMSCHEMA DMVs report them, spelled the way the engine's snapshot spells them (TOM's own names).
export const DATA_TYPE = { 2: 'String', 6: 'Int64', 8: 'Double', 9: 'DateTime', 10: 'Decimal', 11: 'Boolean', 17: 'Binary', 19: 'Unknown', 20: 'Variant' };
export const SUMMARIZE_BY = { 1: 'Default', 2: 'None', 3: 'Sum', 4: 'Min', 5: 'Max', 6: 'Count', 7: 'Average', 8: 'DistinctCount' };
export const COLUMN_TYPE = { 1: 'data', 2: 'calculated', 3: 'rowNumber', 4: 'calculatedTableColumn' };
export const NUMBER_TYPES = new Set(['Int64', 'Double', 'Decimal']);

export const OPTIONS = ['none', 'sum', 'average', 'min', 'max', 'count', 'distinctcount'];

// The one question, byte for byte the engine's (StarterMeasuresFamily.Questions and Criteria).
export const QUESTION_NAME = 'aggregation';
export const INSTRUCTIONS = 'Does this number column need a starter measure, and which one?';
export const CRITERIA = {
  none: 'the column is an identifier, a code, a key, a date part or a setting, so nobody adds it up, averages it or counts it on its own',
  sum: 'the values add up to a meaningful total across rows, such as an amount sold, a quantity or a cost',
  average: 'the values are rates, unit prices, scores or shares, where a total means nothing but a typical value does',
  min: 'the smallest value is what people ask about, such as the lowest price or the earliest reading',
  max: 'the largest value is what people ask about, such as a peak or the highest reading',
  count: 'people ask how many rows have a value in this column',
  distinctcount: 'people ask how many different values appear, such as how many customers or products',
};
export const QUESTIONS = { [QUESTION_NAME]: { type: 'choice', instructions: INSTRUCTIONS, criteria: CRITERIA } };

const MAX_TABLE_COLUMNS = 40;

export const isSystemTable = (name) => /^(LocalDateTable_|DateTableTemplate_)/.test(name ?? '');
const b = (v) => v === true || v === 'true' || v === 1 || v === '1' || v === 'True';
const byName = (a, b2) => a.localeCompare(b2, 'en', { sensitivity: 'base' });

/** The engine's rule: a key or relationship column gets none; otherwise summarize-by picks, Default means sum. */
export function ruleChoice(isKey, relationshipRole, summarizeBy) {
  if (isKey) return 'none';
  if (relationshipRole && relationshipRole !== 'none') return 'none';
  switch (String(summarizeBy ?? '').trim().toLowerCase()) {
    case '': case 'default': case 'sum': return 'sum';
    case 'average': return 'average';
    case 'min': return 'min';
    case 'max': return 'max';
    case 'count': return 'count';
    case 'distinctcount': return 'distinctcount';
    default: return 'none';
  }
}

/** A second rule, scored beside the engine's and never shipped unless it wins: it ignores summarize-by, because
 *  on a mature model authors switch it off AFTER writing explicit measures. A number column that is not a key and
 *  not on the one side of any relationship (a fact-side column) gets sum; everything else gets none. */
export function factSideRule(isKey, relationshipRole) {
  return !isKey && relationshipRole !== 'one' ? 'sum' : 'none';
}

/** Shape TMSCHEMA rows into the model the engine would see: tables, columns, relationship roles, fact tables. */
export function shapeModel({ tables, columns, relationships }) {
  const tById = new Map(tables.map((t) => [String(t.ID), t]));
  const colName = (c) => c.ExplicitName || c.InferredName;
  const role = new Map();
  const many = new Set(), one = new Set();
  for (const r of relationships) {
    // Cardinality: 1 One, 2 Many. "one" wins when a column is on both ends somewhere (the facts' own reading).
    const set = (id, card) => {
      const k = String(id), v = Number(card) === 1 ? 'one' : 'many';
      if (role.get(k) !== 'one') role.set(k, v);
    };
    set(r.FromColumnID, r.FromCardinality);
    set(r.ToColumnID, r.ToCardinality);
    // The engine's fact reading: the many end is the fact side; with neither end many, From is.
    let manyT, oneT;
    if (Number(r.FromCardinality) === 2) { manyT = r.FromTableID; oneT = r.ToTableID; }
    else if (Number(r.ToCardinality) === 2) { manyT = r.ToTableID; oneT = r.FromTableID; }
    else { manyT = r.FromTableID; oneT = r.ToTableID; }
    many.add(String(manyT)); one.add(String(oneT));
  }
  const out = [];
  for (const t of tables) {
    if (isSystemTable(t.Name)) continue;
    const id = String(t.ID);
    const isFact = many.has(id) && !one.has(id) && String(t.DataCategory ?? '') !== 'Time';
    const cols = columns
      .filter((c) => String(c.TableID) === id && COLUMN_TYPE[Number(c.Type)] !== 'rowNumber')
      .map((c) => {
        const dt = DATA_TYPE[Number(c.ExplicitDataType)] ?? DATA_TYPE[Number(c.InferredDataType)] ?? 'Unknown';
        const type = COLUMN_TYPE[Number(c.Type)];
        return {
          id: String(c.ID), name: colName(c), dataType: dt,
          isKey: b(c.IsKey), relationshipRole: role.get(String(c.ID)) ?? 'none',
          summarizeBy: SUMMARIZE_BY[Number(c.SummarizeBy)] ?? 'Default',
          formatString: c.FormatString || null, isHidden: b(c.IsHidden),
          isCalculated: type === 'calculated' || type === 'calculatedTableColumn',
        };
      })
      .sort((a, c) => byName(a.name, c.name));
    if (!cols.length) continue;
    out.push({ id, name: t.Name, isFact, columns: cols });
  }
  void tById;
  return out;
}

/** The candidate columns for the replay: number columns on fact tables. Unlike the product, a column a measure
 *  already uses IS a candidate here, because those measures are the label. */
export function candidates(shaped) {
  const out = [];
  for (const t of [...shaped].sort((a, c) => byName(a.name, c.name))) {
    if (!t.isFact) continue;
    for (const c of t.columns) if (NUMBER_TYPES.has(c.dataType)) out.push({ t, c });
  }
  return out;
}

// ---- labels, from measure expressions, on the machine that holds them ------------------------------------------

const AGG = { SUM: 'sum', AVERAGE: 'average', MIN: 'min', MAX: 'max', COUNT: 'count', COUNTA: 'count', DISTINCTCOUNT: 'distinctcount', DISTINCTCOUNTNOBLANK: 'distinctcount' };
const AGGX = { SUMX: 'sum', AVERAGEX: 'average', MINX: 'min', MAXX: 'max' };
const TABLE = String.raw`(?:'((?:[^']|'')+)'|([A-Za-z_][A-Za-z0-9_]*))`;
const COLUMN = String.raw`\[((?:[^\]]|\]\])+)\]`;
const PLAIN = new RegExp(String.raw`^([A-Z]+)\s*\(\s*${TABLE}\s*${COLUMN}\s*\)$`, 'i');
const PLAINX = new RegExp(String.raw`^([A-Z]+)\s*\(\s*${TABLE}\s*,\s*${TABLE}\s*${COLUMN}\s*\)$`, 'i');

/** Strip comments and collapse white space, so a formatted formula and a one-line one read the same. */
export function normalizeDax(expr) {
  return String(expr ?? '')
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/(^|[^:])\/\/[^\n]*/g, '$1 ')
    .replace(/--[^\n]*/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

const unq = (quoted, bare) => (quoted != null ? quoted.replace(/''/g, "'") : bare);
const unb = (c) => c.replace(/\]\]/g, ']');
const same = (a, c) => String(a).toLowerCase() === String(c).toLowerCase();

/** The option a measure's formula is, when it is a plain aggregation of exactly one column, else null. */
export function plainAggregation(expr) {
  let e = normalizeDax(expr);
  // A bare CALCULATE around a plain aggregation with no filter is the same aggregation.
  const calc = /^CALCULATE\s*\((.*)\)$/i.exec(e);
  if (calc && !/,/.test(calc[1].replace(/'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]/g, ''))) e = calc[1].trim();
  let m = PLAIN.exec(e);
  if (m && AGG[m[1].toUpperCase()]) return { option: AGG[m[1].toUpperCase()], table: unq(m[2], m[3]), column: unb(m[4]) };
  m = PLAINX.exec(e);
  if (m && AGGX[m[1].toUpperCase()]) {
    const table = unq(m[2], m[3]), inner = unq(m[4], m[5]);
    if (same(table, inner)) return { option: AGGX[m[1].toUpperCase()], table, column: unb(m[6]) };
  }
  return null;
}

/** Whether a formula names Table[Column] at all, quoted or bare. */
export function references(expr, table, column) {
  const e = normalizeDax(expr).toLowerCase().replace(/\s+\[/g, '[');
  const col = `[${String(column).replace(/]/g, ']]')}]`.toLowerCase();
  const quoted = `'${String(table).replace(/'/g, "''")}'${col}`.toLowerCase();
  const bare = /^[A-Za-z_][A-Za-z0-9_]*$/.test(table) ? `${table}${col}`.toLowerCase() : null;
  return e.includes(quoted) || (bare != null && e.includes(bare));
}

/** The label for one column: { kind: 'plain', options: [...] } | { kind: 'none' } | { kind: 'other' }. */
export function labelFor(table, column, measures) {
  const options = new Set();
  let used = false;
  for (const m of measures) {
    const plain = plainAggregation(m.Expression);
    if (plain && same(plain.table, table) && same(plain.column, column)) { options.add(plain.option); used = true; continue; }
    if (references(m.Expression, table, column)) used = true;
  }
  if (options.size) return { kind: 'plain', options: [...options].sort() };
  return used ? { kind: 'other' } : { kind: 'none' };
}

// ---- the request the product sends -----------------------------------------------------------------------------

/** The engine's snapshot for one column (StarterMeasuresFamily.Snapshot): names, types, settings, derived shares. */
export function snapshot(t, c, measured, rows) {
  const measuredFacts = measured ? {
    distinct_share: rows > 0 ? Math.round(Math.min(1, measured.distinct / rows) * 1000) / 1000 : null,
    blank_share: Math.round(measured.blankShare * 1000) / 1000,
    whole_numbers: !!measured.wholeNumbers,
    unique_on_every_row: !!measured.uniqueOnEveryRow,
    sampled: false,
  } : null;
  return {
    table_name: t.name,
    column_name: c.name,
    column_facts: {
      data_type: c.dataType,
      is_key: c.isKey,
      relationship_role: c.relationshipRole,
      summarize_by: c.summarizeBy,
      format_string: c.formatString,
      is_hidden: c.isHidden,
      is_calculated: c.isCalculated,
      measured: measuredFacts,
    },
    table_columns: t.columns.filter((x) => x !== c).slice(0, MAX_TABLE_COLUMNS).map((x) => ({ name: x.name, data_type: x.dataType })),
  };
}

// ---- scoring -------------------------------------------------------------------------------------------------

export const band = (confidence) => (confidence >= 0.85 ? 'above 0.85' : confidence >= 0.6 ? '0.6 to 0.85' : 'below 0.6');

/** Right when the choice is one of the options the author wrote (or none, for a column nobody measured). */
export function isRight(choice, label) {
  if (label.kind === 'none') return choice === 'none';
  if (label.kind === 'plain') return label.options.includes(choice);
  return null;
}

/** Exact two-sided McNemar p on the disagreements (b: Jev right, rule wrong; c: the other way). */
export function mcnemar(b1, c1) {
  const n = b1 + c1;
  if (n === 0) return 1;
  const k = Math.min(b1, c1);
  let tail = 0;
  // log-space binomial, so a large n does not overflow
  const lf = (x) => { let s = 0; for (let i = 2; i <= x; i++) s += Math.log(i); return s; };
  for (let i = 0; i <= k; i++) tail += Math.exp(lf(n) - lf(i) - lf(n - i) - n * Math.log(2));
  return Math.min(1, 2 * tail);
}
