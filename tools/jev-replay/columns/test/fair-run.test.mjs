// Self-test for the third, fair column replay (README, "Third run"). Everything it checks was fixed before any Jev
// answer existed: the principled rules, the Pro path, the calls' shape and the bar.
//   node --test tools/jev-replay/columns/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildFairCalls, formatPopulation } from '../build-calls-fair.mjs';
import { fairRuleHidden, fairRuleSummarize, fairRuleNeedsSort, fairRulePartner } from '../rules.mjs';
import { proPath, sure, scoreFair, FAIR_BAR, SURE, harm } from '../score-fair.mjs';

function measured(over = {}) {
  return {
    distinct: 10, blankShare: 0, wholeNumbers: false, uniqueOnEveryRow: false, looksLikeYear: false,
    looksLikeMonthName: false, looksLikeMonthNumber: false, looksLikeDayName: false, looksLikeQuarter: false,
    looksLikePeriodNumber: false, placeShare: { country: 0, region: 0, city: 0, postcode: 0 }, urlShare: 0,
    imageUrlShare: 0, emailShare: 0, oneToOneWith: [], dictionaryBytes: null, dataBytes: null, cardinality: 10,
    sampled: false, ...over,
  };
}
function col(name, dataType, over = {}, m = {}) {
  return {
    name, dataType, isCalculated: false, isHidden: false, summarizeBy: 'default', dataCategory: null, sortByColumn: null,
    formatString: null, isAvailableInMdx: true, isKey: false, relationshipRole: 'none', usedByMeasures: 0,
    usedInReports: null, usedAsSortTarget: false, measured: measured(m), key: {}, ...over,
  };
}

test('the principled hidden rule reads structure and names, and leaves ambiguous rows visible', () => {
  assert.equal(fairRuleHidden(col('Customer Ref', 'int64', { relationshipRole: 'many' })), true);
  assert.equal(fairRuleHidden(col('Date', 'dateTime', { relationshipRole: 'one' })), false);
  assert.equal(fairRuleHidden(col('ProductKey', 'int64', { relationshipRole: 'one' })), true);
  assert.equal(fairRuleHidden(col('customer_id', 'string')), true);
  assert.equal(fairRuleHidden(col('ModifiedDate', 'dateTime')), true);
  assert.equal(fairRuleHidden(col('Month Sort', 'int64', { usedAsSortTarget: true })), true);
  assert.equal(fairRuleHidden(col('Month Number', 'int64', { usedAsSortTarget: true })), false);
  assert.equal(fairRuleHidden(col('Colour', 'string')), false);
  for (const n of ['Column12', 'Folder Path', 'stateid', 'IdsCategoria']) assert.equal(fairRuleHidden(col(n, 'string')), true, n);
  assert.equal(fairRuleHidden(col('Sort Order', 'int64')), true);
  assert.equal(fairRuleHidden(col('Paid', 'string')), false);
});

test('the principled summarize rule never sums keys, years, periods or row ids, averages rates, sums the rest', () => {
  assert.equal(fairRuleSummarize(col('Units', 'int64', { relationshipRole: 'many' })), 'none');
  assert.equal(fairRuleSummarize(col('Fiscal', 'int64', {}, { looksLikeYear: true })), 'none');
  assert.equal(fairRuleSummarize(col('Week', 'int64', {}, { looksLikePeriodNumber: true })), 'none');
  assert.equal(fairRuleSummarize(col('Line', 'int64', {}, { uniqueOnEveryRow: true, wholeNumbers: true })), 'none');
  assert.equal(fairRuleSummarize(col('Sales', 'double', {}, { uniqueOnEveryRow: true })), 'sum');
  assert.equal(fairRuleSummarize(col('Unit Price', 'double')), 'average');
  assert.equal(fairRuleSummarize(col('Ratings Count', 'int64')), 'sum');
  assert.equal(fairRuleSummarize(col('IdsCliente', 'int64')), 'none');
  assert.equal(fairRuleSummarize(col('Order Quantity', 'int64')), 'sum');
  // The key's word rules, ported: words, not substrings.
  assert.equal(fairRuleSummarize(col('Operating Profit', 'double')), 'sum');
  assert.equal(fairRuleSummarize(col('Trip Duration', 'double')), 'sum');
  assert.equal(fairRuleSummarize(col('TaxRate', 'double')), 'average');
  assert.equal(fairRuleSummarize(col('SalesLastYear', 'double')), 'sum');
  assert.equal(fairRuleSummarize(col('YearsSinceLastPromotion', 'int64', {}, { looksLikePeriodNumber: true })), 'average');
  for (const n of ['fav_cuisine_coded', 'Area Code (M49)', 'X', 'MaxQty', 'MonthOffset', 'Order Date']) assert.equal(fairRuleSummarize(col(n, 'int64')), 'none', n);
});

test('needs a sort is the month and day rule; the partner rule falls back to the first numeric partner', () => {
  const month = col('Month', 'string', {}, { looksLikeMonthName: true, oneToOneWith: ['Month Label', 'Month No'] });
  const t = [month, col('Month No', 'int64'), col('Month Label', 'string')];
  assert.equal(fairRuleNeedsSort(month, t), true);
  assert.equal(fairRulePartner(month, t), 'Month No');
  const band = col('Band', 'string', {}, { oneToOneWith: ['Band Code', 'Band Seq'] });
  const t2 = [band, col('Band Code', 'string'), col('Band Seq', 'int64')];
  assert.equal(fairRuleNeedsSort(band, t2), false);
  assert.equal(fairRulePartner(band, t2), 'Band Seq');
});

test('sure is the product reading: a choice at or above 0.85, a noul whose max(p, 1 - p) is at or above 0.85', () => {
  assert.equal(SURE, 0.85);
  assert.equal(sure({ type: 'choice', choice: 'sum', confidence: 0.85 }), true);
  assert.equal(sure({ type: 'choice', choice: 'sum', confidence: 0.84 }), false);
  assert.equal(sure({ type: 'noul', noul: 0.9 }), true);
  assert.equal(sure({ type: 'noul', noul: 0.1 }), true);
  assert.equal(sure({ type: 'noul', noul: 0.8 }), false);
});

test('the Pro path takes Jev when sure and the rule otherwise; needs-a-sort is the rule OR a sure yes', () => {
  assert.equal(proPath('summarize_by', { type: 'choice', choice: 'average', confidence: 0.9 }, 'sum'), 'average');
  assert.equal(proPath('summarize_by', { type: 'choice', choice: 'average', confidence: 0.5 }, 'sum'), 'sum');
  assert.equal(proPath('summarize_by', undefined, 'sum'), 'sum', 'no answer is the rule');
  assert.equal(proPath('hidden', { type: 'noul', noul: 0.95 }, false), true);
  assert.equal(proPath('hidden', { type: 'noul', noul: 0.05 }, true), false);
  assert.equal(proPath('needs_sort', { type: 'noul', noul: 0.05 }, true), true, 'a sure no never removes the month rule');
  assert.equal(proPath('needs_sort', { type: 'noul', noul: 0.95 }, false), true);
  assert.equal(proPath('needs_sort', { type: 'noul', noul: 0.7 }, false), false);
});

test('the calls carry names, types and derived facts only, and the sort call carries no sort setting at all', () => {
  const month = col('Month', 'string', { sortByColumn: 'Month No', key: { hidden: { label: false }, data_category: { label: 'none' }, sort: { needs: true, partners: ['Month No'] } } },
    { looksLikeMonthName: true, oneToOneWith: ['Month No'] });
  const num = col('Month No', 'int64', { usedAsSortTarget: true, isHidden: true, summarizeBy: 'none', formatString: '0',
    key: { hidden: { label: false }, summarize_by: { label: 'none' }, data_category: { label: 'none' }, format_family: { label: 'whole_number' } } },
  { oneToOneWith: ['Month'], looksLikeMonthNumber: true, wholeNumbers: true });
  const models = [{ model: 'f0', tables: [{ name: 'Calendar', rows: 12, measures: 0, columns: [month, num] }] }];
  const { main, sort, format } = buildFairCalls(models);
  assert.equal(main.length, 2);
  assert.equal(sort.length, 1);
  const text = JSON.stringify([main, sort, format].map((l) => l.map((c) => c.arms)));
  for (const leak of ['isHidden', 'summarizeBy', 'dataCategory', 'sortByColumn', 'formatString', '"key"', 'label', 'author']) {
    assert.ok(!text.includes(leak), `no ${leak} in any state`);
  }
  assert.ok(!JSON.stringify(sort[0].arms).includes('used_as_sort_target'), 'the sort call never says who sorts by whom');
  const numMain = main.find((c) => c.id.endsWith('|Month No'));
  assert.equal(numMain.arms.fair.derived_facts.used_as_sort_target, true, 'the hidden call gets the column\'s own sort-target fact');
  assert.deepEqual(Object.keys(sort[0].questions).sort(), ['needs_sort', 'sort_partner']);
  assert.deepEqual(Object.keys(sort[0].questions.sort_partner.criteria), ['Month No']);
  assert.deepEqual(sort[0].arms.fair.derived_facts.one_to_one_partners, [{ name: 'Month No', data_type: 'int64' }]);
  assert.equal(sort[0].truth.needs_sort, true);
  assert.deepEqual(sort[0].truth.sort_partner, ['Month No']);
  assert.equal(sort[0].rule.needs_sort, true);
  assert.equal(sort[0].rule.sort_partner, 'Month No');
});

test('the format population is FMT-COLUMN\'s: a visible number or date that is not a key or named like one', () => {
  assert.equal(formatPopulation(col('Sales', 'double')), true);
  assert.equal(formatPopulation(col('Order Date', 'dateTime')), true);
  assert.equal(formatPopulation(col('Sales', 'double', { isHidden: true })), false);
  assert.equal(formatPopulation(col('ProductKey', 'int64')), false);
  assert.equal(formatPopulation(col('Fiscal Year', 'int64')), false);
  assert.equal(formatPopulation(col('Amount', 'double', { relationshipRole: 'many' })), false);
  assert.equal(formatPopulation(col('Name', 'string')), false);
});

test('the bar: SHIP needs 5 points over the rule, McNemar p under 0.05, and 50 scored columns', () => {
  assert.deepEqual(FAIR_BAR, { minN: 50, margin: 0.05, alpha: 0.05 });
  const row = (proOk, ruleOk) => ({ setting: 'summarize_by', pro: { ok: proOk }, rule: { ok: ruleOk }, jev: { ok: proOk, sure: true }, author: { ok: true } });
  const ship = [...Array(30).fill(row(true, false)), ...Array(5).fill(row(false, true)), ...Array(65).fill(row(true, true))];
  assert.equal(scoreFair(ship).settings.summarize_by.verdict, 'SHIP');
  const tie = [...Array(10).fill(row(true, false)), ...Array(10).fill(row(false, true)), ...Array(80).fill(row(true, true))];
  assert.equal(scoreFair(tie).settings.summarize_by.verdict, 'RULE');
  const small = [...Array(20).fill(row(true, false)), ...Array(20).fill(row(true, true))];
  assert.equal(scoreFair(small).settings.summarize_by.verdict, 'RULE', 'under 50 columns the rule stays');
});

test("Kane's bar (26 September): on when the Pro path wins, p under 0.05, and no more harmful mistakes than the rule", () => {
  const r = (setting, truth, pro, rule) => ({ setting, truth, pro: { pred: pro, ok: pro === truth }, rule: { pred: rule, ok: rule === truth }, jev: { ok: true, sure: true }, author: null });
  // Small wins count: 12 to 2 on 1,000 columns is 1 point and still on.
  const small = [...Array(12).fill(r('summarize_by', 'average', 'average', 'sum')), ...Array(2).fill(r('summarize_by', 'sum', 'none', 'sum')), ...Array(986).fill(r('summarize_by', 'sum', 'sum', 'sum'))];
  const v = scoreFair(small).settings.summarize_by;
  assert.equal(v.kane.verdict, 'ON');
  assert.equal(v.kane.harmPro, 0);
  assert.equal(v.kane.harmRule, 12, 'the rule summed 12 averages');
  // A win that adds sums of things that must not be summed is off.
  const harmful = [...Array(20).fill(r('summarize_by', 'none', 'none', 'average')), ...Array(3).fill(r('summarize_by', 'none', 'sum', 'none')), ...Array(77).fill(r('summarize_by', 'sum', 'sum', 'sum'))];
  assert.equal(scoreFair(harmful).settings.summarize_by.kane.verdict, 'OFF');
  // A wrong hide is harm; a wrong show is not.
  assert.equal(harm('hidden', false, true), true);
  assert.equal(harm('hidden', true, false), false);
  assert.equal(harm('sort_by', ['Month No'], 'Month Key'), true);
  assert.equal(harm('sort_by', ['none'], 'Month No'), true, 'sorting a column that needs none is a wrong partner');
  assert.equal(harm('sort_by', ['Month No'], 'none'), false, 'leaving it alphabetical is a miss, not harm');
  assert.equal(harm('data_category', 'none', 'City'), false);
});
