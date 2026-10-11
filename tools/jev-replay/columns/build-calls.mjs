// Build the Jev call list from gathered facts. Runs on the laptop, reads and writes only the private cache dir.
//
//   node build-calls.mjs [--in <dir>] [--out <file>]
//
// One call per column, asking every setting that applies, in two arms over the SAME questions and options:
//   names: the pilot's column state (structure only), docs/redesign/jev-pilot/prep.py, minus description and source
//          column (neither is a name, type, setting or derived fact under the contract)
//   facts: the same state plus the contract's derived facts for this column
// The state never carries the five labelled settings of any column (the pilot's vocabulary rule), and options are
// the ones the engine would enumerate: census enums, and for sort-by the table's one-to-one columns.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import {
  NUMERIC, formatFamily, summarizeTruth, baselineFormat, baselineFormatTyped, baselineDatacat, baselineHidden,
  baselineSummarize, baselineSortby, baselineSummarizeTyped, baselineDatacatTyped, baselineSortbyTyped, DATACAT_CRITERIA, SUMMARIZE_CRITERIA, FORMAT_CRITERIA, HIDDEN_CRITERIA,
} from './rules.mjs';

export function mulberry32(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const DATACAT_TYPES = new Set(['string', 'double', 'decimal', 'int64']);

export function buildCalls(models, seed = 20260923) {
  const rand = mulberry32(seed);
  const calls = [];
  const diag = {};
  for (const model of models) {
    const tables = model.tables;
    const all = tables.flatMap((t) => t.columns.map((c) => ({ t, c })));
    const explicitSb = all.filter(({ c }) => c.summarizeBy && c.summarizeBy !== 'default').length;
    // The pilot's rule: score summarize-by only where the author set it on at least 80% of columns.
    const askSummarize = all.length > 0 && explicitSb / all.length >= 0.8;
    diag[model.model] = {
      tables: tables.length, columns: all.length, measuredColumns: all.filter(({ c }) => c.measured).length,
      explicitSummarizeShare: all.length ? Math.round((explicitSb / all.length) * 1000) / 1000 : 0, askSummarize,
    };
    const tableShape = tables.map((t) => ({ table: t.name, columns: t.columns.length, measures: t.measures ?? 0 }));
    // Balanced data-category subset, as the pilot drew it: every labelled column plus as many unlabelled ones.
    const dcPool = all.filter(({ c }) => DATACAT_TYPES.has(c.dataType));
    const dcWith = dcPool.filter(({ c }) => c.dataCategory);
    const dcWithout = shuffle(dcPool.filter(({ c }) => !c.dataCategory), rand).slice(0, dcWith.length);
    const dcBalanced = new Set([...dcWith, ...dcWithout].map(({ t, c }) => `${t.name}\u0001${c.name}`));

    for (const { t, c } of all) {
      // A column the gather could not measure has no facts arm to compare, so it is left out and counted in diag.
      if (!c.measured) continue;
      const siblings = t.columns.filter((x) => x !== c).map((x) => x.name);
      const base = {
        model_tables: tableShape,
        column: { table: t.name, name: c.name, data_type: c.dataType, is_a_calculated_column: !!c.isCalculated },
        other_column_names_in_the_same_table: siblings,
        this_column_is_used_in_a_relationship: c.relationshipRole !== 'none',
        measure_count_in_the_same_table: t.measures ?? 0,
      };
      const facts = {
        ...base,
        derived_facts: {
          table_rows: t.rows ?? null,
          is_key: !!c.isKey, relationship_role: c.relationshipRole, used_by_measures: c.usedByMeasures,
          used_in_reports: c.usedInReports,
          measured: c.measured ?? null,
        },
      };
      const qs = {}, truth = {}, baseline = {}, extra = { typed: {} };
      qs.hidden = { type: 'noul', instructions: 'This column should be hidden from people building reports.', criteria: HIDDEN_CRITERIA };
      truth.hidden = !!c.isHidden; baseline.hidden = baselineHidden(c.name);

      if (askSummarize && NUMERIC.has(c.dataType)) {
        qs.summarize_by = { type: 'choice', instructions: 'When a report author drops this column onto a chart, how are its values combined by default?', criteria: SUMMARIZE_CRITERIA };
        truth.summarize_by = summarizeTruth(c.summarizeBy, c.dataType); baseline.summarize_by = baselineSummarize(c.name, c.dataType);
        extra.typed.summarize_by = baselineSummarizeTyped(c);
      }
      if (DATACAT_TYPES.has(c.dataType)) {
        qs.data_category = { type: 'choice', instructions: 'What kind of real-world thing do the values in this column hold?', criteria: DATACAT_CRITERIA };
        // TOM's Uncategorized is the explicit form of no category, so it reads as none.
        truth.data_category = c.dataCategory && c.dataCategory !== 'Uncategorized' ? c.dataCategory : 'none';
        baseline.data_category = baselineDatacat(c.name);
        extra.typed.data_category = baselineDatacatTyped(c);
        extra.dcBalanced = dcBalanced.has(`${t.name}\u0001${c.name}`);
        extra.dcOutsideOptions = !(truth.data_category in DATACAT_CRITERIA);
      }
      const cands = c.measured?.oneToOneWith ?? [];
      if (c.sortByColumn || cands.length) {
        const crit = Object.fromEntries(cands.map((s) => [s, `the column named "${s}" in the same table`]));
        crit.none = "the column's own values decide the order";
        qs.sort_by_column = { type: 'choice', instructions: 'When this column is placed on a chart axis, which column decides the order its values appear in?', criteria: crit };
        truth.sort_by_column = c.sortByColumn || 'none'; baseline.sort_by_column = baselineSortby(c.name, siblings);
        extra.sortLabelOutsideOptions = !!c.sortByColumn && !cands.includes(c.sortByColumn);
        extra.hadSortBy = !!c.sortByColumn;
        extra.typed.sort_by_column = baselineSortbyTyped(c, t.columns);
        // The second run's sort-by population: text columns with a one-to-one partner. No labelled sort-by in the
        // first run's model or the public samples sits on a number or date column.
        extra.sortTextPop = c.dataType === 'string' && cands.length > 0;
      }
      if (c.formatString && (NUMERIC.has(c.dataType) || c.dataType === 'dateTime')) {
        qs.format_family = { type: 'choice', instructions: "How are this column's values displayed to a report reader?", criteria: FORMAT_CRITERIA };
        truth.format_family = formatFamily(c.formatString); baseline.format_family = baselineFormat(c.name);
        extra.formatTyped = baselineFormatTyped(c.name, c.dataType);
      }
      calls.push({
        id: `${model.model}|${t.name}|${c.name}`, model: model.model, questions: qs, truth, baseline, extra,
        adjudicate: adjudicationFlags(c, truth),
        arms: { names: base, facts },
      });
    }
  }
  return { calls, diag };
}

/**
 * Rows whose stored label is plausibly wrong, decided mechanically from facts (never by reading names by hand).
 * The adjudicated score drops these rows, as the pilot dropped its plainly wrong rows.
 */
export function adjudicationFlags(c, truth) {
  const m = c.measured ?? {};
  const out = {};
  const keyish = /(key|id)$/i.test(c.name.trim());
  // A visible foreign key: the many side of a relationship, named like a key.
  if (truth.hidden === false && c.relationshipRole === 'many' && keyish) out.hidden = 'visible foreign key';
  // Adding up or averaging a key, a year, a month number or a quarter number.
  if (['sum', 'average'].includes(truth.summarize_by)
    && (c.relationshipRole !== 'none' || m.looksLikeYear || m.looksLikeMonthNumber || m.looksLikeQuarter)) out.summarize_by = 'aggregates a key or a calendar number';
  if (truth.data_category !== undefined && m.distinct >= 3) {
    const p = m.placeShare ?? {};
    const dc = truth.data_category;
    if (dc === 'none' && (p.country >= 0.9 || m.urlShare >= 0.9)) out.data_category = 'unlabelled but the values are countries or web addresses';
    else if (dc === 'Country' && c.dataType === 'string' && p.country === 0 && m.distinct >= 5) out.data_category = 'labelled country, no value is a country';
    else if ((dc === 'WebUrl' || dc === 'ImageUrl') && m.urlShare === 0) out.data_category = 'labelled web address, no value is one';
    else if (dc === 'PostalCode' && (m.looksLikeQuarter || m.looksLikeYear || m.looksLikeMonthNumber || (c.dataType === 'string' && p.postcode === 0)))
      out.data_category = 'labelled postal code, values are calendar numbers or not code shaped';
  }
  return out;
}

function shuffle(a, rand) {
  const x = a.slice();
  for (let i = x.length - 1; i > 0; i--) { const j = Math.floor(rand() * (i + 1)); [x[i], x[j]] = [x[j], x[i]]; }
  return x;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const arg = (k, d) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : d; };
  const IN = arg('--in', join(homedir(), '.cache', 'jev-replay-columns', 'jev-columns-facts.json'));
  const OUT = arg('--out', join(homedir(), '.cache', 'jev-replay-columns', 'calls.json'));
  const models = JSON.parse(readFileSync(IN, 'utf8')).models;
  const { calls, diag } = buildCalls(models);
  writeFileSync(OUT, JSON.stringify(calls));
  writeFileSync(OUT.replace(/\.json$/, '-diag.json'), JSON.stringify(diag, null, 1));
  const byQ = {};
  for (const c of calls) for (const q of Object.keys(c.questions)) byQ[q] = (byQ[q] ?? 0) + 1;
  console.log(JSON.stringify({ models: models.length, calls: calls.length, questions: byQ }));
}
