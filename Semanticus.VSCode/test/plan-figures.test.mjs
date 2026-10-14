// The Plan header's three small figures (1.5, Kane's pick A "Three small figures", 12 Oct 2026): the pure half.
//   AI ready is the grade of the AI readiness read; Best practice is the strip's letter and score; Plan done is the strip's tasksDone of tasksAll
//   a figure with nothing behind it is not drawn (no guessed number); the percent never rounds a long plan into 0 or 100
//   each tooltip says what it measures and where it comes from, with the engine's numbers in its first line
//   the page hands the engine's strip to the header, the header draws the three, and the Done line no longer repeats the grade
//   the numbers equal their sources on a real model file in the engine suite (Semanticus.Tests/StoryFiguresTests.cs)
//   no string has an em dash or "one click"
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, webview } from './helpers/webview-load.mjs';

const load = makeLoader();
const F = load('plan-figures-model.ts');
const src = (name) => readFileSync(join(webview, 'src', name), 'utf8');

let failures = 0;
async function check(name, fn) {
  try { await fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}
const strip = { bestPractice: 'D', bestPracticeScore: 64, tasksDone: 8, tasksAll: 20, tracks: [] };

await check('three figures, in order, each from its own source', () => {
  const f = F.figuresOf({ letter: 'A', score: 91 }, strip);
  assert.deepEqual(f.map((x) => [x.key, x.label]), [['ai', 'AI ready'], ['bp', 'Best practice'], ['plan', 'Plan done']]);
  assert.deepEqual([f[0].letter, f[0].score], ['A', 91]);
  assert.deepEqual([f[1].letter, f[1].score], ['D', 64]);
  assert.deepEqual([f[2].done, f[2].all, f[2].percent], [8, 20, 40]);
});

await check('a figure with nothing behind it is not drawn', () => {
  assert.deepEqual(F.figuresOf(null, null), []);
  assert.deepEqual(F.figuresOf(null, strip).map((x) => x.key), ['bp', 'plan']);
  assert.deepEqual(F.figuresOf({ letter: 'B', score: null }, { tracks: [] }).map((x) => x.key), ['ai'], 'no scan, no counts: only the grade');
  assert.deepEqual(F.figuresOf(null, { ...strip, tasksAll: 0, tasksDone: 0 }).map((x) => x.key), ['bp'], 'a plan with no task has no percent');
  assert.equal(F.bpFigure({ bestPractice: null, bestPracticeScore: 12 }), null, 'a score without the letter is not shown');
  assert.equal(F.bpFigure({ bestPractice: 'B' }).score, null, 'the letter alone still shows');
});

await check('the percent never claims 0 or 100 early', () => {
  assert.equal(F.percentOf(0, 20), 0);
  assert.equal(F.percentOf(20, 20), 100);
  assert.equal(F.percentOf(199, 200), 99);
  assert.equal(F.percentOf(1, 1000), 1);
  assert.equal(F.percentOf(13, 19), 68);
  assert.equal(F.percentOf(3, 0), 0);
});

await check('the tooltips say what each figure measures and where it comes from, with the engine\'s numbers', () => {
  const [ai, bp, plan] = F.figuresOf({ letter: 'A', score: 91 }, strip).map(F.tipOf);
  assert.equal(ai.head, 'AI readiness, 91 of 100');
  assert.match(ai.src, /AI readiness check/u);
  assert.equal(bp.head, 'Best practice, 64 of 100');
  assert.match(bp.src, /Best practice rules/u);
  assert.equal(plan.head, 'Plan, 8 of 20 tasks done');
  assert.match(plan.src, /History/u);
  assert.equal(F.tipOf(F.planFigure({ tasksDone: 0, tasksAll: 1 })).head, 'Plan, 0 of 1 task done');
  assert.equal(F.tipOf(F.aiFigure({ letter: 'B', score: null })).head, 'AI readiness', 'no score, no "of 100"');
});

await check('a screen reader hears the label, the grade and the number', () => {
  const [ai, bp, plan] = F.figuresOf({ letter: 'A', score: 91 }, strip);
  assert.equal(F.spokenOf(ai), 'AI ready, grade A, 91 of 100');
  assert.equal(F.spokenOf(bp), 'Best practice, grade D, 64 of 100');
  assert.equal(F.spokenOf(plan), 'Plan done, 40 percent');
});

await check('the component: each figure is one tab stop, the tip shows on hover and on focus, Escape hides it, a change flips and bumps', () => {
  const c = src('plan-figures.tsx'), css = src('plan-calm.css');
  assert.match(c, /tabIndex=\{0\}/u);
  assert.match(c, /role="tooltip"/u);
  assert.match(c, /aria-describedby/u);
  assert.match(c, /e\.key === 'Escape'/u);
  assert.match(c, /<GradeFlip/u, 'a letter change flips');
  assert.match(c, /pc-bump/u, 'a score change bumps');
  assert.match(css, /\.pc-fig:hover:not\(\[data-hidden\]\) \.pc-tip/u);
  assert.match(css, /\.pc-fig:focus:not\(\[data-hidden\]\) \.pc-tip/u);
});

await check('the page gives the header the engine\'s strip, counts up as rows turn Done, and the Done line has no grade in it', () => {
  const plan = src('plan.tsx'), head = src('plan-head.tsx');
  assert.match(plan, /<PlanHead[^>]*strip=\{plan\?\.strip\}/u);
  assert.match(head, /<PlanFigures grade=\{grade\} strip=\{strip\}/u);
  assert.match(plan, /phase === 'done'[\s\S]{0,900}getModelPlan/u, 'a row turning Done reads the plan again, so the numbers are the engine\'s');
  assert.doesNotMatch(plan, /doneWords\([^)]*(before|after)/u);
  assert.match(plan, /await refreshGrade\(\)/u, 'the AI ready figure reads the model again after a run');
});

await check('at 400 the three figures take their own line under Plan; up to 1280 they leave the slot its room', () => {
  const css = src('plan-calm.css');
  assert.match(css, /@media \(max-width: 720px\)[\s\S]*?\.pc-figs \{ order: 3; flex: 1 0 100%/u);
  assert.match(css, /@media \(max-width: 1280px\) and \(min-width: 721px\)[\s\S]*?\.pc-figs \{[^}]*flex: 1 0 100%/u);
});

await check('no string has an em dash or "one click"', () => {
  for (const f of ['plan-figures.tsx', 'plan-figures-model.ts']) {
    const text = src(f);
    assert.ok(!text.includes('—'), `${f} has an em dash`);
    assert.ok(!/one click/iu.test(text), `${f} says one click`);
  }
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('plan-figures: all passed');
