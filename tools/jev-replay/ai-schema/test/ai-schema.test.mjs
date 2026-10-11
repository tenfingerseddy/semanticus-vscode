// The ai.schema replay's self-test: no network, no key. The labels' shape, the committed bar, and the scorer's arithmetic.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LABELS, BAR, score, mcnemar, confusion } from '../score.mjs';

test('the labels cover three models, use only three words, and carry enough exclusions to score', () => {
  assert.equal(LABELS.models.length, 3);
  let exclude = 0, scored = 0;
  for (const m of LABELS.models) {
    assert.match(m.sha256, /^[0-9a-f]{64}$/);
    for (const v of Object.values(m.labels)) {
      assert.ok(['include', 'exclude', 'either'].includes(v), v);
      if (v === 'exclude') exclude++;
      if (v !== 'either') scored++;
    }
  }
  assert.ok(exclude >= BAR.minExcludeLabels, `${exclude} exclude labels`);
  assert.ok(scored >= BAR.minScoredRows, `${scored} scored rows`);
});

test("the bar is Kane's switch-on bar of 26 September, recorded as set after run 2, and the scorer reads it", () => {
  assert.equal(BAR.point, 'ai.schema');
  assert.equal(BAR.version, 3);
  assert.equal(BAR.committedBeforeFirstRun, false);
  assert.equal(BAR.setAfterRun, 2);
  assert.equal(BAR.maxMcNemarP, 0.05);
  assert.equal(BAR.minAccuracyGain, undefined, 'the size of the win does not matter under Kane\'s bar');
  assert.equal(BAR.maxHarmfulVersusRule, 0, 'no more wrong exclusions than the rule');
});

test("run 2's numbers read SHIP under Kane's bar, and so does any reliable win without extra harm", () => {
  // Run 2: Pro path right and the rule wrong on 8 rows, the reverse on 0; 0 wrong exclusions each (docs/redesign).
  const rows = (n, want, pro, rule) => Array.from({ length: n }, (_, i) => ({ ref: `${want}-${pro}-${rule}-${i}`, want, pro, rule }));
  const set = [...rows(8, 'exclude', 'exclude', 'include'), ...rows(6, 'exclude', 'exclude', 'exclude'),
    ...rows(120, 'include', 'include', 'include'), ...rows(35, 'exclude', 'include', 'include')];
  const labels = { models: [{ id: 'm', labels: Object.fromEntries(set.map((r) => [r.ref, r.want])) }] };
  const results = { confidenceBar: 0.85, models: [{ id: 'm', rows: set.map((r) => ({ ref: r.ref, jevChoice: r.pro, jevConfidence: 0.95,
    pro: r.pro, jev: r.pro, jevAllLines: r.pro, rule: r.rule })) }] };
  assert.equal(score(results, labels).verdict, 'SHIP');
  // One more wrong exclusion than the rule is harm, whatever the win: RULE.
  const harm = structuredClone(results);
  harm.models[0].rows.push({ ref: 'x', jevChoice: 'exclude', jevConfidence: 0.95, pro: 'exclude', jev: 'exclude', jevAllLines: 'exclude', rule: 'include' });
  const harmLabels = structuredClone(labels); harmLabels.models[0].labels.x = 'include';
  assert.equal(score(harm, harmLabels).verdict, 'RULE');
  // A win that could be luck is MORE DATA.
  const luck = structuredClone(results); luck.models[0].rows = luck.models[0].rows.filter((r, i) => i >= 6);
  assert.equal(score(luck, labels).verdict, 'MORE DATA');
});

test('McNemar and the confusion arithmetic', () => {
  assert.equal(mcnemar(0, 0), 1);
  assert.equal(mcnemar(10, 0), 2 / 1024);
  const c = confusion([['exclude', 'exclude'], ['include', 'exclude'], ['exclude', 'include'], ['include', 'include']]);
  assert.deepEqual([c.tp, c.fp, c.fn, c.tn], [1, 1, 1, 1]);
  assert.equal(c.balancedAccuracy, 0.5);
});

function fixture(jevOf) {
  const models = LABELS.models.map((m) => ({
    id: m.id,
    rows: Object.entries(m.labels).map(([ref, want]) => {
      const jev = jevOf(want);
      return { ref, jevChoice: jev, jevConfidence: 0.95, pro: jev, jev, jevAllLines: jev, rule: 'include' };
    }),
  }));
  return { confidenceBar: 0.85, models };
}

test('a Pro path that matches every label ships; one that excludes everything, or nothing, stays on the rule', () => {
  assert.equal(score(fixture((w) => (w === 'exclude' ? 'exclude' : 'include'))).verdict, 'SHIP');
  assert.equal(score(fixture(() => 'exclude')).verdict, 'RULE');
  assert.equal(score(fixture(() => 'include')).verdict, 'RULE');
});
