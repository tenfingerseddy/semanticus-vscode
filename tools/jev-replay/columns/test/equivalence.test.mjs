// The question that ships is the question the replay scored (verifier D4, 26 Sep). For a fixture model in the replay's
// facts format, the replay's own builder makes its calls, product-payload.mjs maps each to the product's payload, and
// that must equal the committed expected file. The engine test (Semanticus.Tests/ColumnFamiliesFixTests.cs) builds the
// same model and must produce the same file, so product == mapping(replay), with only the mapping's named differences.
//   node --test tools/jev-replay/columns/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { buildFairCalls } from '../build-calls-fair.mjs';
import { toProductPayload, canonical, roundRows, POINT_QUESTION, BASE_MEASURE_INSTRUCTIONS } from '../product-payload.mjs';
import { productRuleSummarize, fairRuleSummarize, flagOrOrdinal } from '../rules.mjs';

const here = new URL('./fixtures/', import.meta.url);
const fixture = JSON.parse(readFileSync(new URL('fair-equivalence.json', here), 'utf8'));
const expected = JSON.parse(readFileSync(new URL('fair-equivalence-expected.json', here), 'utf8'));

export function payloads() {
  const { main } = buildFairCalls(fixture.models);
  const out = {};
  for (const call of main) {
    const [, table, column] = call.id.split('|');
    for (const [point, q] of Object.entries(POINT_QUESTION)) {
      if (call.questions[q]) out[`${point}|${table}|${column}`] = canonical(toProductPayload(call, point));
    }
  }
  return out;
}

test('the replay payload, mapped, is exactly the committed product payload for every column and point', () => {
  const got = payloads();
  assert.deepEqual(Object.keys(got).sort(), Object.keys(expected).sort());
  for (const k of Object.keys(expected)) assert.deepEqual(got[k], expected[k], k);
});

test('the mapping changes only what it names: a key rename, the type spelling, the rounded rows, one question', () => {
  const { main } = buildFairCalls(fixture.models);
  const call = main.find((c) => c.id === 'fx0|Sales|Amount');
  const p = toProductPayload(call, 'column.summarize');
  const fair = call.arms.fair;
  assert.equal(p.state.column_facts.table_rows, 81000);
  assert.equal(fair.derived_facts.table_rows, 81234);
  assert.equal(p.state.column_facts.data_type, 'Double');
  assert.deepEqual(p.state.column_facts.measured, fair.derived_facts.measured);
  assert.deepEqual(p.state.column_facts.model_tables, fair.model_tables);
  assert.deepEqual(p.state.table_columns, fair.other_column_names_in_the_same_table);
  // The base-measure question keeps the replay's criteria and asks for the column's explicit measure (Kane, 26 Sep).
  assert.deepEqual(p.questions.summarize_by.criteria, call.questions.summarize_by.criteria);
  assert.equal(p.questions.summarize_by.instructions, BASE_MEASURE_INSTRUCTIONS);
  assert.deepEqual(Object.keys(p.questions), ['summarize_by']);
  const city = main.find((c) => c.id === 'fx0|Customer|City');
  assert.deepEqual(toProductPayload(city, 'column.category').questions.data_category, city.questions.data_category);
  for (const [n, r] of [[12, 12], [99, 99], [250, 250], [4096, 4100], [81234, 81000], [99950, 100000]]) assert.equal(roundRows(n), r);
});

test('the product fallback is the fair rule plus the flag guard, exactly as ColumnSummarizeFamily.PrincipledRule has it', () => {
  const col = (name, over = {}) => ({ name, dataType: 'int64', relationshipRole: 'none', isKey: false, measured: {}, ...over });
  for (const n of ['IsWeekend', 'IsReturn', 'OrderLine', 'Weekend Flag', 'Seq', 'Has Discount Rate']) assert.equal(productRuleSummarize(col(n)), 'none', n);
  for (const n of ['Line Total', 'Island Sales', 'Quantity']) assert.equal(productRuleSummarize(col(n)), 'sum', n);
  assert.equal(productRuleSummarize(col('Unit Price', { dataType: 'double' })), 'average');
  assert.equal(fairRuleSummarize(col('IsWeekend')), 'sum', 'the replay rule itself sums flags');
  assert.equal(flagOrOrdinal('Isolation Time'), false);
});
