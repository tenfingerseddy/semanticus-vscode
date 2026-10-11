// Self-test for the compose.draft replay: no network, no key. The labels and the bar are sane, every labelled op is a
// step template's op in the engine, and the scorer does the arithmetic the verdict rests on.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { LABELS, BAR, score, stepRight, draftRight, unmatched, mcnemar } from '../score.mjs';
const PLAN2 = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'labels-plan2.json'), 'utf8'));
const BAR_PLAN2 = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'bar-plan2.json'), 'utf8'));
const FRESH = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'labels-fresh.json'), 'utf8'));
const BAR_FRESH = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'bar-fresh.json'), 'utf8'));

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..');
const read = (p) => readFileSync(join(root, p), 'utf8');

/** Every template op the engine offers: DraftMenu.All, the steps after the change, and the two reads. */
function templateOps() {
  const src = read('Semanticus.Engine/Composer/DraftDoors.cs') + read('Semanticus.Engine/Composer/DescribedSteps.cs') + read('Semanticus.Engine/Composer/JevDraftBuilder.cs');
  return new Set([...src.matchAll(/\bT\("[^"]+",\s*"[^"]+",\s*"([a-z_]+)"/g)].map((m) => m[1]));
}

test('the bar was committed with the labels and names both halves', () => {
  assert.equal(BAR.point, 'compose.draft');
  assert.equal(BAR.committedBeforeFirstRun, true);
  assert.ok(BAR.describeMin > 0 && BAR.describeMin <= 1);
  assert.ok(BAR.composeMin > 0 && BAR.composeMin <= 1);
});

test('every labelled op is a step template the engine offers', () => {
  const ops = templateOps();
  assert.ok(ops.size >= 20, `parsed ${ops.size} template ops`);
  for (const item of [...LABELS.items, ...FRESH.items, ...PLAN2.items])
    for (const op of [...item.clauses.flat(), ...(item.alsoOk ?? []).flat()])
      assert.ok(ops.has(op), `${item.id} labels ${op}, which is not a template op`);
});

test('the labels carry no model or person names beyond the Contoso set', () => {
  const ids = new Set();
  for (const item of [...LABELS.items, ...FRESH.items, ...PLAN2.items]) {
    assert.ok(!ids.has(item.id), `${item.id} twice`);
    ids.add(item.id);
    assert.ok(['uat', 'composer', 'starter', 'fresh'].includes(item.source));
    if (item.alsoOk) assert.equal(item.alsoOk.length, item.clauses.length, `${item.id} alsoOk does not line up`);
  }
});

test('a step is right with every labelled op and nothing outside the label and alsoOk', () => {
  assert.equal(stepRight(['create_measure'], ['create_measure']), true);
  assert.equal(stepRight(['benchmark_dax', 'profile_dax'], ['benchmark_dax'], ['profile_dax']), true);
  assert.equal(stepRight(['benchmark_dax', 'run_tests'], ['benchmark_dax'], ['profile_dax']), false);
  assert.equal(stepRight([], []), true);
  assert.equal(stepRight([], ['create_measure']), false);
});

test('the scorer counts, compares with the word match, and gives the verdict', () => {
  const labels = { items: [
    { id: 'a', words: 'x', clauses: [['create_measure'], ['benchmark_dax']], alsoOk: [[], ['profile_dax']] },
    { id: 'b', words: 'y', clauses: [[]] },
  ] };
  const results = { requests: 3, inputTokens: 1000, templates: [{ id: 'add-measure', op: 'create_measure' }], rows: [
    { id: 'a', jevAsked: true, describeMs: 10, composeMs: 20, clauses: [
      { jev: ['create_measure'], byWords: [] }, { jev: ['benchmark_dax', 'profile_dax'], byWords: ['run_tests'] } ],
      compose: { jevDrafted: true, kept: ['add-measure'], dropped: [] } },
    { id: 'b', jevAsked: true, describeMs: 10, composeMs: 20, clauses: [{ jev: [], byWords: [] }], compose: { jevDrafted: false, kept: [], dropped: [] } },
  ] };
  const s = score(results, labels, { describeMin: 0.8, composeMin: 0.8, notBelowWordMatch: true });
  assert.equal(s.steps, 3);
  assert.equal(s.jevRight, 3);
  assert.equal(s.wordsRight, 1);
  assert.equal(s.compose, 2);
  assert.equal(s.composeJevRight, 1);        // b was the menu, so Jev alone drafted nothing there
  assert.equal(s.composeHybridRight, 2);     // the menu's word match is right for b
  assert.equal(s.hybridRight, 3);
  assert.equal(s.verdict, 'BELOW THE BAR');   // Jev only on the Plan is 50%
  const hybrid = score(results, labels, { arm: 'hybrid', describeMin: 0.8, composeMin: 0.8 });
  assert.equal(hybrid.verdict, 'CLEARS THE BAR');
  const none = score({ ...results, rows: results.rows.map((r) => ({ ...r, jevAsked: false })) }, labels, { describeMin: 0.8, composeMin: 0.8 });
  assert.equal(none.verdict, 'NOT RUN');
});

test('the fresh set is fresh: no description of the tuning set, the size Kane asked for, and a hybrid bar', () => {
  const old = new Set(LABELS.items.map((i) => i.words.toLowerCase()));
  assert.ok(FRESH.items.every((i) => !old.has(i.words.toLowerCase())));
  assert.ok(FRESH.items.length >= 38, `${FRESH.items.length} descriptions`);
  assert.ok(FRESH.items.reduce((n, i) => n + i.clauses.length, 0) >= 60);
  assert.equal(BAR_FRESH.arm, 'hybrid');
  assert.equal(BAR_FRESH.describeMin, 0.8);
  assert.equal(BAR_FRESH.composeMin, 0.8);
});

test('the second fresh set is distinct from both, about 80 descriptions, and carries Kane\'s bar', () => {
  const seen = new Set([...LABELS.items, ...FRESH.items].map((i) => i.words.toLowerCase()));
  assert.ok(PLAN2.items.every((i) => !seen.has(i.words.toLowerCase())));
  assert.ok(PLAN2.items.length >= 75, `${PLAN2.items.length} descriptions`);
  assert.equal(BAR_PLAN2.arm, 'kane-2026-09-26');
  assert.equal(BAR_PLAN2.alpha, 0.05);
});

test('McNemar matches the exact binomial', () => {
  assert.ok(Math.abs(mcnemar(11, 4) - 0.1185) < 0.001);
  assert.ok(Math.abs(mcnemar(22, 8) - 0.0161) < 0.001);
  assert.equal(mcnemar(0, 0), 1);
});

test('a Plan draft is compared as a list: a step asked for twice needs two, and a step kept twice is one too many (D7)', () => {
  assert.equal(draftRight(['set_measure_format', 'set_measure_format'], ['set_measure_format', 'set_measure_format']), true);
  assert.equal(draftRight(['set_measure_format'], ['set_measure_format', 'set_measure_format']), false);
  assert.equal(draftRight(['create_measure', 'create_measure'], ['create_measure']), false);
  assert.equal(draftRight(['create_measure', 'save_model'], ['create_measure'], ['save_model']), true);
  assert.equal(unmatched(['a', 'a', 'b'], ['a']), 2);
  const labels = { items: [{ id: 'two', words: 'x', clauses: [['set_measure_format'], ['set_measure_format']] }] };
  const row = (kept, byWords) => ({ id: 'two', jevAsked: true, clauses: [{ jev: [], byWords: [byWords[0]] }, { jev: [], byWords: [byWords[1]] }],
    compose: { jevDrafted: true, kept, dropped: [] } });
  const templates = [{ id: 'format', op: 'set_measure_format' }];
  // The engine's second step of one kind is "format-2"; a draft that folded the two clauses into one step is wrong.
  const merged = score({ templates, rows: [row(['format'], ['set_measure_format', 'set_measure_format'])] }, labels, BAR_PLAN2);
  assert.equal(merged.composeHybridRight, 0);
  assert.equal(merged.harm.pro.dropped, 1);
  assert.equal(merged.composeWordsRight, 1);
  const perClause = score({ templates, rows: [row(['format', 'format-2'], ['set_measure_format', 'set_measure_format'])] }, labels, BAR_PLAN2);
  assert.equal(perClause.composeHybridRight, 1);
  assert.equal(perClause.rebuiltRight, 1);
});
