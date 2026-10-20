// UAT P38/F55 (Kane, 18 Oct): a Validation check that differs can be waived. The page says "Accepted", never a pass, and one press
// takes it back. The pure reading rules are run here; the page's wiring is held by reading its source.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
async function load(file) {
  const out = ts.transpileModule(read(file), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(out, 'utf8').toString('base64')}`);
}
const model = await load('webview/src/tests-model.ts');
const vm = await load('webview/src/validation-model.ts');

// A check that differs and was waived reads Accepted. Only a difference can be accepted: a pass stays a pass.
assert.equal(model.resultChip(true, { verdict: 'Fail', waiver: { by: 'human' } }), 'Accepted');
assert.equal(model.resultChip(true, { verdict: 'Suspect', waiver: { by: 'human' } }), 'Accepted');
assert.equal(model.resultChip(true, { verdict: 'Fail', waiver: null }), 'Differs', 'taken back: it differs again');
assert.equal(model.resultChip(true, { verdict: 'Fail', waiverLapsed: true }), 'Differs', 'a different difference came back');
assert.equal(model.resultChip(true, { verdict: 'Pass', waiver: { by: 'human' } }), 'Pass');

// The headline: "4 of 5 pass, 1 accepted". An accepted check is in the total and never in the passes.
const chips = ['Pass', 'Pass', 'Pass', 'Pass', 'Accepted'];
const t = vm.boardTally(chips);
assert.deepEqual([t.total, t.passed, t.differs, t.accepted], [5, 4, 0, 1]);
assert.deepEqual(vm.boardHeadline(t), { big: '4', rest: ' of 5 pass' });
assert.deepEqual(vm.boardParts(t).map((p) => p.text), ['1 accepted']);
const mixed = vm.boardTally(['Pass', 'Differs', 'Accepted', 'Accepted', 'Not run']);
assert.deepEqual(vm.boardParts(mixed).map((p) => p.text), ['1 differs', '2 accepted', '1 not run yet']);
assert.equal(vm.boardParts(mixed).reduce((n, p) => n + p.n, mixed.passed), mixed.total, 'the parts still add up to the checks');
assert.deepEqual(vm.boardHeadline(vm.boardTally(['Accepted'])), { big: '0', rest: ' of 1 pass' }, 'an accepted check alone is a run with no passes');

// Who accepted it, when, and the note.
assert.equal(model.acceptedByLine({ by: 'human', when: '2026-10-18T05:00:00Z', note: 'Known rounding in the warehouse.' }), 'By you on 18 Oct. Known rounding in the warehouse.');
assert.equal(model.acceptedByLine({ by: 'agent', when: '2026-10-18T05:00:00Z' }), 'By your assistant on 18 Oct.');
assert.equal(model.acceptedByLine(null), '');

// The page: one press, the note is never required, Undo waive is one press, and an accepted check stays in view.
const page = read('webview/src/tests.tsx');
assert.match(page, /action\('Waive', \(\) => \{ props\.onWaive\(waiveNote\)/, 'Waive is one press');
assert.doesNotMatch(page, /disabled=\{[^}]*waiveNote/, 'the note never gates the button');
assert.match(page, /placeholder="Note \(optional\)"/);
assert.match(page, /action\('Undo waive', props\.onUnwaive/);
assert.match(page, /rpc\('waiveFinding', CHECK_WAIVER_SYSTEM, def\.id, null, note\.trim\(\) \|\| null, 'human'\)/, 'the same door the assistant uses');
assert.match(page, /rpc\('unwaiveFinding', CHECK_WAIVER_SYSTEM, def\.id, null, 'human'\)/);
assert.match(page, /Accepted difference\./, 'the opened check names it');
assert.match(page, /data-testid="v-accepted"/, 'accepted checks stay on the page, not folded into the passes');
assert.doesNotMatch(page, /CHECK_WAIVER_LABELS/, 'accepting a difference is no model change: the page no longer reads model labels for it');
assert.match(page, /event\.kind === 'check_waiver'\) void syncWaivers\(\)/, 'the page reads the waivers again when the engine says one changed (the assistant too)');
assert.match(page, /It stays accepted until you press Undo waive\./, 'the page says Undo waive is the one way back');
// The person's own accept is not listed as the assistant's live activity; the assistant's own still is.
const feed = read('webview/src/activity.tsx');
assert.match(feed, /if \(!\(e\.kind === 'check_waiver' && e\.origin !== 'agent'\)\) setFeed/, 'a waive by the person stays out of Live activity, your assistant');
// Copy: no em dash, no en dash.
for (const text of [page, read('webview/src/tests-model.ts')]) assert.doesNotMatch(text.split('\n').filter((l) => /Accepted|Waive|waive/.test(l)).join('\n'), /[–—]/);
console.log('validation-waive: ok');
