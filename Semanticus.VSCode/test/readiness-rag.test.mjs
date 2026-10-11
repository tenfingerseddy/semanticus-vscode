// AI readiness colour (Kane, 8 Oct): one band function names the colour of the grade letter, the score and every
// category bar, so a B model cannot show an amber bar. The cut points are ReadinessAnalyzer.GradeFor's.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { band, gradeBand } from '../webview/src/readinessband.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

for (const [score, want] of [[100, 'good'], [80, 'good'], [79, 'warn'], [60, 'warn'], [59, 'bad'], [0, 'bad']]) {
  assert.equal(band(score), want, `band(${score})`);
}
for (const [letter, want] of [['A', 'good'], ['B', 'good'], ['C', 'warn'], ['D', 'warn'], ['F', 'bad'], ['E', null], ['b', null], ['', null], [null, null], [undefined, null]]) {
  assert.equal(gradeBand(letter), want, `gradeBand(${String(letter)})`);
}
// The letter and the score cannot disagree: every score lands in the band of the letter the engine gives it.
const gradeFor = (s) => (s >= 90 ? 'A' : s >= 80 ? 'B' : s >= 70 ? 'C' : s >= 60 ? 'D' : 'F');
for (let s = 0; s <= 100; s++) assert.equal(band(s), gradeBand(gradeFor(s)), `score ${s} and grade ${gradeFor(s)} share a band`);

// Hero and Categories call the band function, and no inline cut point is left in them.
const app = read('webview/src/App.tsx');
const hero = app.slice(app.indexOf('function Hero('), app.indexOf('function Categories('));
const cats = app.slice(app.indexOf('function Categories('), app.indexOf('const sevNum'));
assert.match(hero, /band\(card\.overall\)/, 'Hero takes its colour from the band function');
const rag = read('webview/src/ragbars.tsx');
assert.match(cats, /<RagBars/, 'Categories draws its bars with the shared component');
assert.match(rag, /band\(c\.score\)/, 'the shared bar takes each colour from the band function');
for (const [name, src] of [['Hero', hero], ['Categories', cats], ['RagBars', rag]]) {
  assert.doesNotMatch(src, />=\s*(60|70|80|90)\b/, `${name} has no inline cut point`);
  assert.doesNotMatch(src, /GRADE_COLOR/, `${name} has no second colour table`);
}
assert.match(rag, /left: '60%'[\s\S]*left: '80%'/, 'the bar shows its 60 and 80 marks');

// Model quality (K20, 8 Oct): the same big grade and one bar per category as AI readiness, from the same band function and
// the same sem-rag-* styles. The engine sends each category's score; the page cuts no point of its own.
const bpa = read('webview/src/bpa.tsx');
assert.match(bpa, /from '\.\/readinessband\.mjs'/, 'Model quality imports the band function');
assert.match(bpa, /gradeBand\(card\.bestPractice\)/, 'the grade takes its colour from the grade band');
assert.match(bpa, /sem-rag-grade/, 'the grade is the AI readiness grade style');
assert.match(bpa, /<RagBars/, 'each category bar is drawn by the component AI readiness uses');
assert.match(bpa, /score: c\.score/, 'the bar takes the engine score as it is');
assert.doesNotMatch(bpa, />=\s*(60|70|80|90)\b/, 'Model quality has no inline cut point');
assert.doesNotMatch(bpa, /bpa-grade-/, 'the old letter colours are gone');
assert.doesNotMatch(read('webview/src/styles.css'), /\.bpa-grade-/, 'no style is left for the old letter colours');
