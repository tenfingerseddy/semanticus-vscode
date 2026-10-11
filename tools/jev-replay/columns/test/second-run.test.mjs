// Self-test for the second column replay (summarize by, data category, sort by on public sample models). The bars,
// populations and stronger rules it checks were fixed before any Jev answer existed; see the README's "Second run".
//   node --test tools/jev-replay/columns/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildCalls } from '../build-calls.mjs';
import { scoreSecond, verdictOf, BAR2 } from '../score.mjs';
import { baselineSummarizeTyped, baselineDatacatTyped, baselineSortbyTyped } from '../rules.mjs';

function measured(over = {}) {
  return {
    distinct: 10, blankShare: 0, wholeNumbers: false, uniqueOnEveryRow: false, looksLikeYear: false,
    looksLikeMonthName: false, looksLikeMonthNumber: false, looksLikeDayName: false, looksLikeQuarter: false,
    placeShare: { country: 0, region: 0, city: 0, postcode: 0 }, urlShare: 0, imageUrlShare: 0, emailShare: 0,
    oneToOneWith: [], dictionaryBytes: null, dataBytes: null, cardinality: 10, sampled: false, ...over,
  };
}
function col(name, dataType, over = {}, m = {}) {
  return {
    name, dataType, isCalculated: false, isHidden: false, summarizeBy: 'sum', dataCategory: null, sortByColumn: null,
    formatString: null, isAvailableInMdx: true, isKey: false, relationshipRole: 'none', usedByMeasures: 0,
    usedInReports: null, measured: measured(m), ...over,
  };
}

test('the stronger summarize-by rule says none for keys, calendar numbers and row ids, sum otherwise', () => {
  assert.equal(baselineSummarizeTyped(col('Units', 'int64')), 'sum');
  assert.equal(baselineSummarizeTyped(col('Units', 'int64', { relationshipRole: 'many' })), 'none');
  assert.equal(baselineSummarizeTyped(col('Fiscal', 'int64', {}, { looksLikeYear: true })), 'none');
  assert.equal(baselineSummarizeTyped(col('Period', 'int64', {}, { looksLikeMonthNumber: true })), 'none');
  assert.equal(baselineSummarizeTyped(col('Line', 'int64', {}, { uniqueOnEveryRow: true })), 'none');
  assert.equal(baselineSummarizeTyped(col('Order Number', 'int64')), 'none', 'the pilot name rule still applies');
  assert.equal(baselineSummarizeTyped(col('Label', 'string')), 'none');
});

test('the stronger data-category rule reads the value shares first, then the name rule', () => {
  assert.equal(baselineDatacatTyped(col('Nation', 'string', {}, { placeShare: { country: 0.9, region: 0, city: 0, postcode: 0 } })), 'Country');
  assert.equal(baselineDatacatTyped(col('Town', 'string', {}, { placeShare: { country: 0, region: 0, city: 0.85, postcode: 0 } })), 'City');
  assert.equal(baselineDatacatTyped(col('Area', 'string', {}, { placeShare: { country: 0, region: 0.8, city: 0, postcode: 0 } })), 'StateOrProvince');
  assert.equal(baselineDatacatTyped(col('Picture', 'string', {}, { urlShare: 1, imageUrlShare: 1 })), 'ImageUrl');
  assert.equal(baselineDatacatTyped(col('Link', 'string', {}, { urlShare: 1 })), 'WebUrl');
  // A code-shaped share alone is not a postal code: plenty of codes are four or five digits.
  assert.equal(baselineDatacatTyped(col('Code', 'string', {}, { placeShare: { country: 0, region: 0, city: 0, postcode: 1 } })), 'none');
  assert.equal(baselineDatacatTyped(col('Zip', 'string', {}, { placeShare: { country: 0, region: 0, city: 0, postcode: 1 } })), 'PostalCode');
  assert.equal(baselineDatacatTyped(col('Latitude', 'double')), 'Latitude');
  assert.equal(baselineDatacatTyped(col('Amount', 'double')), 'none');
});

test('the stronger sort-by rule sorts month and day names by a numeric one-to-one partner, then the name rule', () => {
  const month = col('Month', 'string', {}, { looksLikeMonthName: true, oneToOneWith: ['Month Key', 'Month Label'] });
  const table = [month, col('Month Key', 'int64'), col('Month Label', 'string')];
  assert.equal(baselineSortbyTyped(month, table), 'Month Key');
  const day = col('Weekday', 'string', {}, { looksLikeDayName: true, oneToOneWith: ['Weekday Label'] });
  assert.equal(baselineSortbyTyped(day, [day, col('Weekday Label', 'string')]), 'none', 'no numeric partner, no sort');
  const band = col('Band', 'string', {}, { oneToOneWith: ['Band Order'] });
  assert.equal(baselineSortbyTyped(band, [band, col('Band Order', 'int64')]), 'Band Order');
});

test('build-calls reads Uncategorized as no category, and marks the stronger rules and the sort-by population', () => {
  const tables = [{ name: 'Geo', rows: 10, measures: 0, columns: [
    col('Region', 'string', { dataCategory: 'Uncategorized' }),
    col('Month', 'string', { sortByColumn: 'Month Key' }, { looksLikeMonthName: true, oneToOneWith: ['Month Key'] }),
    col('Month Key', 'int64', { summarizeBy: 'none' }, { oneToOneWith: ['Month'] }),
  ] }];
  const { calls } = buildCalls([{ model: 'p0', data: 'local', tables }]);
  const by = Object.fromEntries(calls.map((c) => [c.id.split('|')[2], c]));
  assert.equal(by.Region.truth.data_category, 'none');
  assert.equal(by.Region.extra.dcOutsideOptions, false);
  assert.equal(by.Month.extra.typed.sort_by_column, 'Month Key');
  assert.equal(by.Month.extra.sortTextPop, true);
  assert.equal(by['Month Key'].extra.sortTextPop, false, 'a number column is outside the sort-by population');
  assert.equal(by['Month Key'].extra.typed.summarize_by, 'none');
});

// Rows shaped as flatten() makes them, for the second-run sections.
function rows(q, spec) {
  const out = [];
  for (const [truth, n, factsOk, namesOk, ruleOk, typedOk, extra = {}] of spec) {
    for (let i = 0; i < n; i++) out.push({
      q, truth, flagged: false, extra,
      facts: { ok: factsOk, pred: factsOk ? truth : 'x', conf: 0.9, band: 'above 0.85' },
      names: { ok: namesOk, pred: namesOk ? truth : 'x', conf: 0.9, band: 'above 0.85' },
      rule: { ok: ruleOk }, typed: { ok: typedOk },
    });
  }
  return out;
}

test('the verdict bar judges either arm, and the setting ships with the arm that clears', () => {
  const rs = rows('summarize_by', [['sum', 40, true, true, true, true], ['none', 40, true, false, false, false], ['count', 20, false, false, false, false]]);
  assert.equal(verdictOf(rs, 'typed', 'facts').verdict, 'SHIP');
  assert.equal(verdictOf(rs, 'typed', 'names').verdict, 'RULE', 'names only ties the rule');
  const s = scoreSecond(rs);
  const sec = s.sections.find((x) => x.q === 'summarize_by');
  assert.equal(sec.verdict, 'SHIP');
  assert.equal(sec.shipArm, 'facts');
});

test('data category and sort by need fifty labelled columns, and data category must not flood the full population', () => {
  assert.equal(BAR2.minLabelled, 50);
  // 30 labelled only: MORE DATA however well Jev does.
  const few = rows('data_category', [['City', 30, true, true, false, false], ['none', 300, true, true, true, true]]);
  const secFew = scoreSecond(few).sections.find((x) => x.q === 'data_category');
  assert.equal(secFew.verdict, 'MORE DATA');
  assert.match(secFew.why, /30 labelled/);
  // 60 labelled, Jev right on them, but it calls 100 plain columns a place that the rule leaves alone.
  const flood = rows('data_category', [['City', 60, true, true, false, false], ['none', 100, false, false, true, true], ['none', 900, true, true, true, true]]);
  const secFlood = scoreSecond(flood).sections.find((x) => x.q === 'data_category');
  assert.equal(secFlood.balanced.verdictFacts.verdict, 'SHIP', 'on the balanced draw Jev wins');
  assert.equal(secFlood.verdict, 'RULE', 'but on every column it is worse than the rule, so it stays the rule');
  // Labels outside the offered options are counted and left out, never scored.
  const outside = rows('data_category', [['Year', 5, false, false, false, false, { dcOutsideOptions: true }], ['City', 60, true, true, false, false], ['none', 900, true, true, true, true]]);
  const secOut = scoreSecond(outside).sections.find((x) => x.q === 'data_category');
  assert.equal(secOut.outsideOptions, 5);
  assert.equal(secOut.full.n, 960);
});

test('sort by is scored on text columns that have a one-to-one partner', () => {
  const rs = rows('sort_by_column', [
    ['Month Key', 55, true, true, false, false, { sortTextPop: true }],
    ['none', 200, true, true, true, true, { sortTextPop: true }],
    ['none', 500, false, false, false, false, { sortTextPop: false }],
  ]);
  const sec = scoreSecond(rs).sections.find((x) => x.q === 'sort_by_column');
  assert.equal(sec.full.n, 255);
  assert.equal(sec.labelled, 55);
  assert.equal(sec.verdict, 'SHIP');
});
