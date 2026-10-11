// Self-test for the queries.chart_kind replay: the labels, the bar and the scorer. No network, no key, no Jev.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadJson, chartRulePick, mcnemarExact, scoreArm, rulesPicks, verdict } from '../score.mjs';

const KINDS = ['matrix', 'line', 'bar', 'area', 'table', 'card', 'scatter'];
const labels = loadJson('labels.json');
const bar = loadJson('bar.json');

test('the labels are valid, held out, and at least 150', () => {
  assert.equal(labels.point, 'queries.chart_kind');
  assert.equal(labels.writtenBeforeFirstRun, true);
  assert.ok(labels.items.length >= bar.minItems, `${labels.items.length} items`);
  assert.ok(labels.items.length >= 150);
  assert.match(labels.howWritten, /not run to write any label/);
  const ids = new Set();
  for (const it of labels.items) {
    assert.ok(!ids.has(it.id), 'duplicate id ' + it.id);
    ids.add(it.id);
    assert.ok(bar.heldOutModels.includes(it.model), it.id + ' is not on a held-out model');
    assert.ok(typeof it.words === 'string' && it.words.trim().length > 0, it.id);
    assert.ok(Array.isArray(it.shape.dims) && Number.isInteger(it.shape.measures) && Number.isInteger(it.shape.rows), it.id);
    assert.ok(it.acceptable.length > 0, it.id + ' has no acceptable chart');
    for (const k of [...it.acceptable, ...it.harmful]) assert.ok(KINDS.includes(k), it.id + ' names ' + k);
    for (const k of it.harmful) assert.ok(!it.acceptable.includes(k), it.id + ' calls ' + k + ' both acceptable and harmful');
  }
  for (const m of bar.heldOutModels) assert.ok(labels.items.some((it) => it.model === m), 'no item on ' + m);
});

test('every acceptable chart is one the rules can draw for that shape', () => {
  for (const it of labels.items) {
    const { fits } = chartRulePick(it.shape);
    for (const k of it.acceptable) assert.ok(fits.includes(k) || it.whyNotInFits, `${it.id}: ${k} is not in fits ${fits.join(',')}`);
  }
});

test('the set leans on the intent words that change the chart', () => {
  const intent = /\b(share|trend|trending|mix|compare|compared|best|worst|against|versus|relationship|exact|build up|burn up|cumulative|move together|over time|most|drive|predict)\b/;
  const n = labels.items.filter((it) => intent.test(it.words)).length;
  assert.ok(n / labels.items.length >= 0.5, `${n} of ${labels.items.length} carry an intent word`);
});

test('the bar is the three conditions of the bar doc, committed before any run', () => {
  assert.equal(bar.point, 'queries.chart_kind');
  assert.equal(bar.committedBeforeFirstRun, true);
  assert.deepEqual(bar.conditions, { beatsRules: true, mcnemarAlpha: 0.05, harmfulNotAboveRules: true });
  assert.deepEqual(bar.heldOutModels, ['northstar', 'mssec', 'smartsheets']);
});

test('McNemar exact on known cases', () => {
  assert.equal(mcnemarExact(0, 0), 1);
  // b = 10, c = 0: 2 * 0.5^10
  assert.ok(Math.abs(mcnemarExact(10, 0) - 2 / 1024) < 1e-12);
  // b = 39, c = 20: the query.summarize gate's own case, p = 0.018 as recorded in JevQueryReader.cs
  assert.equal(mcnemarExact(39, 20).toFixed(3), '0.018');
  assert.equal(mcnemarExact(5, 5), 1);
  assert.equal(mcnemarExact(3, 7), mcnemarExact(7, 3));
});

test('the scorer counts acceptable and harmful on a tiny fixture', () => {
  const tiny = { items: [
    { id: 'a', shape: { dims: [{ name: 'Month', isDate: true, distinct: 12 }], measures: 1, rows: 12 }, acceptable: ['bar'], harmful: [] },
    { id: 'b', shape: { dims: [{ name: 'Region', isDate: false, distinct: 4 }], measures: 1, rows: 4 }, acceptable: ['bar'], harmful: [] },
    { id: 'c', shape: { dims: [{ name: 'Brand', isDate: false, distinct: 40 }, { name: 'Month', isDate: true, distinct: 24 }], measures: 1, rows: 500 }, acceptable: ['matrix'], harmful: ['line'] },
  ] };
  const rules = rulesPicks(tiny);
  assert.deepEqual(rules, { a: 'line', b: 'bar', c: 'matrix' });
  const r = scoreArm(tiny, rules);
  assert.equal(r.acceptable, 2);
  assert.equal(r.harmful, 0);
  const v = verdict(tiny, bar, { arm: 'jev', picks: { a: { kind: 'bar', by: 'jev' }, c: { kind: 'line', by: 'jev' } } });
  assert.equal(v.jev.acceptable, 2);
  assert.equal(v.jev.harmful, 1);
  assert.deepEqual(v.discordant, { jevOnly: 1, rulesOnly: 1 });
  assert.equal(v.passes, false);
  // A pick by "rules" is scored as the rules' pick, whatever kind it names.
  const same = verdict(tiny, bar, { picks: { a: { kind: 'bar', by: 'rules' } } });
  assert.deepEqual(same.discordant, { jevOnly: 0, rulesOnly: 0 });
});

test('the rules baseline recorded in the README is the one the scorer computes', async () => {
  const { readFileSync } = await import('node:fs');
  const { fileURLToPath } = await import('node:url');
  const readme = readFileSync(fileURLToPath(new URL('../README.md', import.meta.url)), 'utf8');
  const r = scoreArm(labels, rulesPicks(labels));
  assert.ok(readme.includes(`${r.acceptable} of ${labels.items.length}`), 'README does not record ' + r.acceptable + ' of ' + labels.items.length);
  assert.ok(readme.includes(`harmful ${r.harmful}`), 'README does not record harmful ' + r.harmful);
});
