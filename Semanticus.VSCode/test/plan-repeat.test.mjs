// The repeat editor on a Plan row (lane RP; contract section 1.7).
//
// Two kinds of check:
//   1. plan-repeat-model.ts is pure, so which rows can repeat, what kind of repeat they make, the cadence
//      sentence and the due line are executed here. The cadence sentences are the engine's own words
//      (PlanRepeats.CadenceText, asserted in Semanticus.Tests/PlanRepeatTests.cs), so the two cannot drift apart
//      unnoticed.
//   2. The editor is rendered for real with react-dom/server in each of its faces, and what a person can read is
//      checked: the offer, the Pro line on free, a saved repeat with Change, Pause and Stop repeating, and nothing
//      at all on a row that cannot repeat. The clicks are driven in a real browser by
//      tools/uishot/rp-repeat-drive.mjs, which records the call each button makes.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs|js)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}

const R = load(join(webview, 'src', 'plan-repeat-model.ts'));
const { RepeatEditor } = load(join(webview, 'src', 'plan-repeat.tsx'));

const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

let passed = 0;
const test = (name, fn) => { fn(); passed++; };

// ---- the pure half -----------------------------------------------------------------------------------------

test('the cadence sentence is the engine sentence', () => {
  assert.equal(R.cadenceText({ kind: 'daily', every: 1 }), 'Every day');
  assert.equal(R.cadenceText({ kind: 'daily', every: 3 }), 'Every 3 days');
  assert.equal(R.cadenceText({ kind: 'weekly', every: 1, day: 'monday' }), 'Every week, on Monday');
  assert.equal(R.cadenceText({ kind: 'weekly', every: 2, day: 'monday' }), 'Every 2 weeks, on Monday');
  assert.equal(R.cadenceText({ kind: 'monthly', every: 1, date: 1 }), 'Every month, on the 1st');
  assert.equal(R.cadenceText({ kind: 'monthly', every: 2, date: 22 }), 'Every 2 months, on the 22nd');
  assert.equal(R.cadenceText({ kind: 'monthly', every: 1, date: 31 }), 'Every month, on the 31st or the last day');
  assert.equal(R.cadenceText({ kind: 'monthly', every: 1, date: 11 }), 'Every month, on the 11th');
});

const found = { id: 'safefix/Visibility', title: 'Hide the key columns', source: 'found', doer: 'semanticus', stateWord: 'ready' };
const typed = { id: 'pt-1a2b3c4d', title: 'Check the Sales descriptions', source: 'words', doer: 'assistant', stateWord: 'ready' };
const jev = { id: 'jev/measures/format', title: 'Format the measures', source: 'jev', doer: 'semanticus', stateWord: 'ready' };
const aimYou = { id: 'safefix/x', title: 'x', source: 'found', doer: 'you', stateWord: 'needs_you' };
const card = { id: 'pt-99990000', title: 'Update model notes', source: 'repeat', doer: 'assistant', stateWord: 'ready' };
const saved = {
  id: 'rp-0a0b0c0d', words: 'Update model notes', doer: 'assistant', stock: 'model-notes',
  cadence: { kind: 'weekly', every: 2, day: 'friday' }, cadenceText: 'Every 2 weeks, on Friday',
  nextDueUtc: '2026-10-09T00:00:00Z', held: false, openItemId: card.id,
};

test('a found row Semanticus does repeats as a fix; a typed task repeats as words', () => {
  assert.deepEqual(R.repeatRequestFor(found, []), { fix: 'safefix/Visibility' });
  assert.deepEqual(R.repeatRequestFor(typed, []), { words: 'Check the Sales descriptions', doer: 'assistant', fromTask: typed.id });
});


test('a Jev row, a row a person does, and a repeat card cannot be set up as a repeat', () => {
  assert.equal(R.repeatRequestFor(jev, []), null);
  assert.equal(R.repeatRequestFor(aimYou, []), null);
  assert.equal(R.repeatRequestFor(card, [saved]), null);
  // A fix that already repeats is not offered twice: the row shows its saved repeat instead, due or not.
  const fixRepeat = { ...saved, fix: found.id, openItemId: null };
  assert.equal(R.repeatRequestFor(found, [fixRepeat]), null);
  assert.equal(R.repeatForRow(found.id, [fixRepeat]), fixRepeat);
  assert.equal(R.repeatFace(found, [fixRepeat], true), 'saved');
});

test('the face follows the tier and whether the row is a card', () => {
  assert.equal(R.repeatFace(found, [], true), 'offer');
  assert.equal(R.repeatFace(found, [], false), 'pro');
  assert.equal(R.repeatFace(card, [saved], true), 'saved');
  assert.equal(R.repeatFace(card, [saved], false), 'saved');
  assert.equal(R.repeatFace(jev, [], true), 'none');
});

test('the due line says paused, then open, then the date, and never promises a run', () => {
  assert.match(R.dueLine({ ...saved, held: true }, true), /^Paused\./);
  assert.match(R.dueLine(saved, true), /still open/);
  const idle = { ...saved, openItemId: null };
  assert.match(R.dueLine(idle, true), /^Next on 9 October\. Nothing runs by itself\.$/);
  assert.match(R.dueLine({ ...idle, fix: 'safefix/Visibility' }, true), /Run the plan does it/);
  assert.equal(R.dueLine(idle, false), 'Not coming due on free.');
});

test('a cadence is checked the way the engine checks it', () => {
  assert.equal(R.cadenceProblem({ kind: 'weekly', every: 1, day: 'monday' }), null);
  assert.ok(R.cadenceProblem({ kind: 'weekly', every: 0, day: 'monday' }));
  assert.ok(R.cadenceProblem({ kind: 'weekly', every: 13, day: 'monday' }));
  assert.ok(R.cadenceProblem({ kind: 'weekly', every: 1, day: 'someday' }));
  assert.ok(R.cadenceProblem({ kind: 'monthly', every: 1, date: 32 }));
  assert.ok(R.cadenceProblem({ kind: 'hourly', every: 1 }));
  const friday = new Date(2026, 8, 25, 10);
  assert.deepEqual(R.defaultCadence(friday), { kind: 'weekly', every: 1, day: 'friday', date: null });
  assert.deepEqual(R.withKind(R.defaultCadence(friday), 'monthly', friday), { kind: 'monthly', every: 1, day: null, date: 25 });
});

// ---- the editor, rendered -------------------------------------------------------------------------------------

const handlers = { onAdd: () => {}, onChange: () => {}, onDelete: () => {} };
const render = (props) => renderToStaticMarkup(React.createElement(RepeatEditor, { busy: null, ...handlers, ...props }));

test('a row that cannot repeat draws nothing', () => {
  assert.equal(render({ row: jev, repeats: [], isPro: true }), '');
});

test('Pro offers Repeat this, and says nothing runs by itself', () => {
  const html = render({ row: found, repeats: [], isPro: true });
  assert.match(html, /data-face="offer"/);
  assert.match(readable(html), /Repeats Repeat this It comes back on a date you choose\. Nothing runs by itself\./);
});

test('free shows the Pro line in place of the offer, with no button', () => {
  const html = render({ row: found, repeats: [], isPro: false });
  assert.match(readable(html), /Repeating this is part of Semanticus Pro\./);
  assert.doesNotMatch(html, /<button/);
});

test('a saved repeat shows its cadence, when it comes next, and Change, Pause and Stop repeating', () => {
  const html = render({ row: card, repeats: [saved], isPro: true });
  const text = readable(html);
  assert.match(text, /Every 2 weeks, on Friday/);
  assert.match(text, /This one is still open\. The next comes after it is done and 9 October has passed\./);
  for (const id of ['plan-repeat-change', 'plan-repeat-hold', 'plan-repeat-delete']) assert.match(html, new RegExp(`data-testid="${id}"`));
  assert.match(text, /Pause/);
  assert.match(readable(render({ row: card, repeats: [{ ...saved, held: true }], isPro: true })), /Resume/);
});

test('a saved repeat on free shows the Pro line and only Stop repeating', () => {
  const html = render({ row: card, repeats: [saved], isPro: false });
  assert.match(readable(html), new RegExp(R.PRO_LINE.replace(/\./g, '\\.')));
  assert.doesNotMatch(html, /plan-repeat-change|plan-repeat-hold/);
  assert.match(html, /plan-repeat-delete/);
});

test('the copy has no em dash and no engine words', () => {
  for (const f of ['plan-repeat.tsx', 'plan-repeat-model.ts']) {
    const src = readFileSync(join(webview, 'src', f), 'utf8');
    assert.ok(!src.includes('\u2014'), `${f} has an em dash`);
  }
  const shown = [render({ row: found, repeats: [], isPro: true }), render({ row: card, repeats: [saved], isPro: true }),
    render({ row: card, repeats: [saved], isPro: false }), render({ row: found, repeats: [], isPro: false })].map(readable).join(' ');
  for (const word of ['RPC', 'rpc', 'engine', 'occurrence', 'NextDue', 'cadence', 'compose']) assert.ok(!shown.includes(word), `the editor says "${word}"`);
});

// UAT S12-6-003: Save put the task back on "Repeat this", because nothing tied the saved repeat to the row it was made
// from. The repeat names its task now, so that row shows the schedule, Stop repeating, and why it rests.
test('a repeat made from a task shows on that task, with Stop repeating, and says when it rests', () => {
  const fromTyped = { ...saved, id: 'rp-11112222', stock: null, openItemId: null, fromItemId: typed.id };
  assert.equal(R.repeatForRow(typed.id, [fromTyped]), fromTyped);
  assert.equal(R.repeatFace(typed, [fromTyped], true), 'saved');
  const html = render({ row: typed, repeats: [fromTyped], isPro: true });
  assert.match(readable(html), /Stop repeating/);
  const resting = { ...fromTyped, resting: 'Its task is set aside, so it does not come due. Put the task back, or stop repeating.' };
  assert.equal(R.dueLine(resting, true), resting.resting);
});

console.log(`plan-repeat: ${passed} passed`);
