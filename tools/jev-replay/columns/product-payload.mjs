// The product's question for one column, written as a function of the fair replay's call for that column
// (build-calls-fair.mjs, the main arm). This IS the stated mapping between the two, and the only place it is written:
// the node test pins its output for a fixture model, and the engine's own payload for the same model must equal it
// byte for byte (Semanticus.Tests/ColumnFamiliesFixTests.cs), so the question that ships is the question the replay scored,
// with exactly the differences named here and no others.
//
//   the replay's state                               the product's snapshot (four declared fields)
//   column.table, column.name                        table_name, column_name
//   other_column_names_in_the_same_table             table_columns
//   column.data_type, column.is_a_calculated_column,
//   this_column_is_used_in_a_relationship,
//   measure_count_in_the_same_table, model_tables,
//   derived_facts (every key)                        column_facts, under the same key names
//
// Named differences, all forced by rules the product keeps:
//   1. Data types are spelled as the model spells them ("Int64", "DateTime"), not the replay's lower case.
//   2. table_rows goes to two significant figures (81,234 becomes 81,000): Semanticus never sends a table's exact row
//      count. Under a hundred rows the count goes as it is.
//   3. One question per call: each point is asked, answered and paid for on its own, where the replay asked four
//      settings in one call. data_category is the replay's question word for word. summarize_by keeps the replay's
//      criteria but asks for the column's explicit base measure (Kane's redesign, 26 Sep), which is what its answer now
//      decides; the 26 Sep re-test (retest.mjs) scored exactly this question.

import { SUMMARIZE_FAIR } from './build-calls-fair.mjs';

/** The summarize_by instructions the product sends (ColumnSummarizeFamily.Instructions, word for word). */
export const BASE_MEASURE_INSTRUCTIONS =
  "Following good modelling practice, how should this column's values be combined in its base measure, the explicit measure a report author uses instead of the column itself?";

/** The question the product sends for one point: the replay's own for data_category; the base-measure wording, with
 *  the replay's criteria, for summarize_by. */
export function productQuestion(call, point) {
  const q = POINT_QUESTION[point];
  if (q === 'summarize_by') return { type: 'choice', instructions: BASE_MEASURE_INSTRUCTIONS, criteria: SUMMARIZE_FAIR };
  return call.questions[q];
}

const TOM_TYPE = { int64: 'Int64', double: 'Double', decimal: 'Decimal', string: 'String', dateTime: 'DateTime', boolean: 'Boolean', binary: 'Binary', variant: 'Variant', unknown: 'Unknown' };
const tomType = (t) => TOM_TYPE[t] ?? t;

/** Two significant figures, rounding half up, as ColumnAskFacts.RoundRows does. */
export function roundRows(rows) {
  if (rows === null || rows === undefined) return null;
  if (rows < 100) return rows;
  const scale = 10 ** (String(Math.trunc(rows)).length - 2);
  return Math.floor((rows + scale / 2) / scale) * scale;
}

export const POINT_QUESTION = { 'column.category': 'data_category', 'column.summarize': 'summarize_by' };

/** { state, questions } exactly as the engine sends them for `point`, from the replay's call. */
export function toProductPayload(call, point) {
  const q = POINT_QUESTION[point];
  const fair = call.arms.fair;
  const d = fair.derived_facts;
  return {
    state: {
      table_name: fair.column.table,
      column_name: fair.column.name,
      column_facts: {
        data_type: tomType(fair.column.data_type),
        is_a_calculated_column: fair.column.is_a_calculated_column,
        this_column_is_used_in_a_relationship: fair.this_column_is_used_in_a_relationship,
        measure_count_in_the_same_table: fair.measure_count_in_the_same_table,
        model_tables: fair.model_tables,
        table_rows: roundRows(d.table_rows),
        is_key: d.is_key,
        relationship_role: d.relationship_role,
        used_by_measures: d.used_by_measures,
        measured: d.measured,
        one_to_one_partners: d.one_to_one_partners.map((p) => ({ name: p.name, data_type: tomType(p.data_type) })),
        used_as_sort_target: d.used_as_sort_target,
      },
      table_columns: fair.other_column_names_in_the_same_table,
    },
    questions: { [q]: productQuestion(call, point) },
  };
}

/** Sorted keys at every level, lists in order: the engine's canonical JSON (DecisionDigest) reads the same way. */
export function canonical(v) {
  if (Array.isArray(v)) return v.map(canonical);
  if (v && typeof v === 'object') return Object.fromEntries(Object.keys(v).sort().map((k) => [k, canonical(v[k])]));
  return v;
}
