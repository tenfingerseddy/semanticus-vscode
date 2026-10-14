// The calm Plan page (1.5, Kane's pick C "Header does it all"): the pure half, run against the real model file.
//   the list is grouped by who (Your assistant, Semanticus, You) with counts; a group shows 10 rows then Show all
//   "not checked" is a row state; the Filter appears only above 15 rows, matches words and objects, and names itself as one chip
//   the live slot says one thing at a time, in the order: your press, other work, the last press, the wheel, the next step
//   a run ends with "Done. 10 tasks in 2 min." (the three figures beside Plan show the grades); a row's own press is Run, Ask your assistant or Open
//   one row runs through propose then apply with that one row's digest (the assistant's apply_plan_items path)
//   I drive also pauses the assistant: the switch shows I drive while the queue is paused
//   no string here has an em dash or "one click"
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, webview } from './helpers/webview-load.mjs';

const load = makeLoader();
const C = load('plan-calm-model.ts');
const P = load('plan-model.ts');
const src = (name) => readFileSync(join(webview, 'src', name), 'utf8');

let failures = 0;
async function check(name, fn) {
  try { await fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}
const item = (id, title, doer, stateWord, extra = {}) => ({ id, section: 'Structure', title, sub: '', who: 'auto', status: 'proposed', confidence: 1, why: [], doer, stateWord, ...extra });

await check('groups: assistant, then Semanticus, then you, each with its rows; an empty group is not drawn', () => {
  const rows = [item('a', 'A', 'you', 'needs_you'), item('b', 'B', 'semanticus', 'ready'), item('c', 'C', 'assistant', 'ready'), item('d', 'D', 'semanticus', 'waiting')];
  const g = C.groupsOf(rows);
  assert.deepEqual(g.map((x) => [x.who, x.label, x.rows.map((r) => r.id)]), [['assistant', 'Your assistant', ['c']], ['semanticus', 'Semanticus', ['b', 'd']], ['you', 'You', ['a']]]);
  assert.deepEqual(C.groupsOf([item('b', 'B', 'semanticus', 'ready')]).map((x) => x.who), ['semanticus']);
});

await check('rows drawn: open rows, rows done while the page was open, and the open row wherever it went', () => {
  const rows = [item('o', 'Open', 'semanticus', 'ready'), item('d', 'Done now', 'semanticus', 'done'), item('e', 'Done before', 'semanticus', 'done'), item('x', 'Held open', 'semanticus', 'done')];
  assert.deepEqual(C.calmRows(rows, new Set(['d']), 'x').map((r) => r.id), ['o', 'd', 'x']);
});

await check('a group shows 10 rows, then Show all', () => { assert.equal(C.GROUP_CAP, 10); });

await check('not checked is a row state, read from the engine\'s own sub line', () => {
  assert.equal(C.isNotChecked(item('n', 'N', 'you', 'needs_you', { sub: 'could not be checked this time' })), true);
  assert.equal(C.isNotChecked(item('n', 'N', 'you', 'needs_you', { sub: 'Custom calendars need it' })), false);
  assert.equal(C.NOT_CHECKED_WORD, 'Not checked');
  const row = src('plan-row.tsx');
  assert.match(row, /isNotChecked\(item\) && word !== 'done'/, 'the state word draws Not checked');
});

await check('the Filter: only above 15 rows; words match the task and the objects; the goal choice narrows; one chip names it', () => {
  assert.equal(C.FILTER_MIN_ROWS, 15);
  const rows = [item('1', 'Hide the key in Sales', 'semanticus', 'ready', { goal: 'ai', objects: ['column:Sales/Key'] }),
    item('2', 'Rename Store', 'semanticus', 'ready', { goal: 'quality', objects: ['table:Store'] }),
    item('3', 'Describe the margin', 'assistant', 'ready', { goal: 'ai', objects: ['measure:Sales/Margin'] })];
  assert.deepEqual(C.filterRows(rows, 'sales', 'all').map((r) => r.id), ['1', '3'], 'object names count');
  assert.deepEqual(C.filterRows(rows, 'sales', 'ai').map((r) => r.id), ['1', '3']);
  assert.deepEqual(C.filterRows(rows, '', 'quality').map((r) => r.id), ['2']);
  assert.deepEqual(C.filterRows(rows, 'hide sales', 'all').map((r) => r.id), ['1'], 'every word must match');
  assert.equal(C.filterChip('', 'all', 3, 3), null, 'no filter, no chip');
  assert.equal(C.filterChip('', 'ai', 34, 120), 'AI ready, 34 of 120');
  assert.equal(C.filterChip('sales', 'ai', 4, 120), 'AI ready, sales, 4 of 120');
  assert.equal(C.filterChip(' sales ', 'all', 9, 120), 'sales, 9 of 120');
});

await check('the verb names the kind of work of the row that runs, not the page it points at (a formats row is Modelling, not descriptions)', () => {
  const V = load('personality/verbs.ts');
  const g = (title, extra = {}) => C.verbGroupOf({ title, section: 'Meaning', jump: { tool: 'readiness', label: 'AI readiness' }, ...extra });
  assert.equal(g('Add folders and formats to 2 measures in Sales'), 'Modelling');
  assert.equal(g('Add 3 measures for the numbers in Customer'), 'DAX');
  assert.equal(g('Rewrite 3 descriptions'), 'Readiness');
  assert.equal(g('Describe 2 measures'), 'Readiness');
  assert.equal(g('Join Sales to Date on Order Date'), 'Relationships');
  assert.equal(g('Hide the key column in Sales'), 'Modelling');
  assert.equal(g('Do the thing', { section: 'Numbers' }), 'DAX');
  assert.equal(g('Do the thing', { section: 'Structure' }), 'Modelling');
  assert.equal(g('Do the thing', { section: 'Ship' }), 'Publishing');
  assert.equal(g('Do the thing', { section: undefined, jump: { tool: 'relationships', label: 'Relationships' } }), 'Relationships');
  const rows = [item('f', 'Add folders and formats to 2 measures in Sales', 'semanticus', 'ready', { section: 'Meaning', jump: { tool: 'readiness', label: 'AI readiness' } })];
  const w = C.pressWords(['f'], { f: { step: 'work' } }, rows, false);
  assert.ok(V.VERBS.Modelling.includes(w.verb), `a Modelling verb, got ${w.verb}`);
});

await check('the end of a run says one line: tasks and time; the grades are the figures\' job, not the line\'s', () => {
  assert.equal(C.durationWords(2400), '2 s'); assert.equal(C.durationWords(120000), '2 min'); assert.equal(C.durationWords(100), '1 s');
  assert.equal(C.doneWords(10, 120000), 'Done. 10 tasks in 2 min.');
  assert.equal(C.doneWords(1, 3000), 'Done. 1 task in 3 s.');
  assert.equal(C.doneWords(5, 4000), 'Done. 5 tasks in 4 s.');
});

await check('while it runs: the verb of the row being worked on, and "3 of 10"', () => {
  const items = [item('r1', 'Add 3 measures for the numbers in Customer', 'semanticus', 'ready'), item('r2', 'Join Sales to Date on Order Date', 'semanticus', 'ready', { jump: { tool: 'relationships', label: 'Relationships' } }),
    item('r3', 'Write 3 descriptions', 'semanticus', 'ready', { jump: { tool: 'readiness', label: 'AI readiness' } })];
  const held = ['r1', 'r2', 'r3'];
  const w = C.pressWords(held, { r1: { step: 'done' }, r2: { step: 'work' } }, items, false);
  assert.equal(w.count, '2 of 3');
  assert.ok(load('personality/verbs.ts').VERBS.Relationships.includes(w.verb), `a Relationships verb, got ${w.verb}`);
  const first = C.pressWords(held, {}, items, false);
  assert.equal(first.count, '1 of 3'); assert.ok(load('personality/verbs.ts').VERBS.DAX.includes(first.verb), `a DAX verb, got ${first.verb}`);
  assert.equal(C.pressWords(held, {}, items, true).verb, 'Writing DAX', 'Quiet says the plain words');
  assert.equal(C.pressWords(held, { r1: { step: 'done' }, r2: { step: 'done' }, r3: { step: 'done' } }, items, false).count, '3 of 3');
});

await check('the slot says one thing, in a fixed order', () => {
  const base = { press: null, receipt: null, work: null, driver: { connected: true, mode: 'claudeDrives', wheel: false, assistantWorking: false, name: 'Your assistant' }, hasNext: true };
  assert.equal(C.slotOf(base).kind, 'next', 'at rest: the assistant\'s next step');
  // Never empty (Kane, 13 Oct): with no step, whoever drives and whether or not an assistant is connected, one quiet line holds the place.
  const quietLine = { kind: 'quiet', text: 'Nothing waiting for your assistant' };
  assert.deepEqual(C.slotOf({ ...base, hasNext: false }), quietLine);
  assert.deepEqual(C.slotOf({ ...base, hasNext: false, driver: { ...base.driver, connected: false } }), quietLine);
  assert.deepEqual(C.slotOf({ ...base, hasNext: false, driver: { ...base.driver, mode: 'iDrive' } }), quietLine);
  assert.equal(C.slotOf({ ...base, hasNext: false, driver: { ...base.driver, assistantWorking: true } }).kind, 'work', 'the assistant is working: its work, not the quiet line');
  assert.equal(C.slotOf({ ...base, driver: { ...base.driver, wheel: true } }).kind, 'wheel');
  assert.equal(C.slotOf({ ...base, receipt: { kind: 'ran', text: 'Done. 1 task in 1 s.' } }).kind, 'done');
  assert.equal(C.slotOf({ ...base, receipt: { kind: 'said', text: 'Undone.' } }).kind, 'said');
  const work = { who: 'assistant', verb: 'Writing descriptions', progress: null };
  assert.deepEqual(C.slotOf({ ...base, work, receipt: { kind: 'ran', text: 'x' } }), { kind: 'work', who: 'assistant', verb: 'Writing descriptions', count: null });
  assert.deepEqual(C.slotOf({ ...base, work: { ...work, who: 'person' } }), { kind: 'work', who: 'semanticus', verb: 'Writing descriptions', count: null });
  assert.deepEqual(C.slotOf({ ...base, work, press: { verb: 'CALCULATE-ing', count: '3 of 10' } }), { kind: 'press', who: 'semanticus', verb: 'CALCULATE-ing', count: '3 of 10' });
});

await check('the next step is quiet: muted words, no bold, a small press, one cross-fade, none under reduced motion', () => {
  const css = src('plan-calm.css');
  const rule = (sel) => css.match(new RegExp(`${sel.replace(/[.]/g, '\\.')} \\{[^}]*\\}`))?.[0] ?? '';
  assert.ok(/color: var\(--sem-muted\)/.test(rule('.pc-next')) && !/weight-strong/.test(rule('.pc-next')), 'muted, not bold');
  assert.ok(/color: var\(--sem-muted\)/.test(rule('.pc-next-assistant')), 'not in the assistant purple');
  assert.ok(/font-size: var\(--sem-text-meta\)/.test(rule('.pc-next-acts .pc-ask')), 'a small press');
  assert.ok(/prefers-reduced-motion: reduce\)[^\n]*\.pc-next-in[^\n]*\.pc-next-out/.test(css), 'reduced motion: no fade');
  const head = src('plan-head.tsx');
  assert.ok(head.includes('pc-next-out') && head.includes('STEP_FADE_MS') && head.includes('reducedMotion'), 'the cross-fade is wired, and off under the app\'s own reduced-motion setting');
  assert.ok(!head.includes("slot.kind === 'waiting'"), 'no second no-step state');
});

await check('the end-of-run line with Undo stays until the next action, and goes only when the model moved on without it (hero19 D8)', () => {
  const plan = src('plan.tsx');
  assert.ok(!plan.includes('useExpiring'), 'no timer takes the line away');
  assert.equal(C.RECEIPT_SHOWN_MS, undefined, 'the 8 second expiry is gone');
  const ran = { kind: 'ran', entryId: 'e1', revision: 10 };
  assert.equal(C.receiptStays(ran, 10), true, 'right after the press');
  assert.equal(C.receiptStays(ran, 10), true, 'any time later, the model unchanged');
  assert.equal(C.receiptStays({ kind: 'said' }, 99), true, 'a plain sentence (Undone.) stays too');
  assert.equal(C.receiptStays(ran, 11), false, 'another writer changed the model: Undo is gone, so the line is');
  assert.equal(C.receiptStays({ kind: 'ran', revision: 10 }, 11), true, 'a line with no Undo has nothing to lose');
  assert.ok(/receiptStays/.test(plan), 'the page asks the model');
  assert.ok(/\.pc-head \{[^}]*position: sticky/.test(src('plan-calm.css')), 'the header stays in view, so the Undo is reachable from any row');
});

await check('while the assistant drives and works, the slot shows its work, never "Save your changes / Do it" (hero19 D3)', () => {
  const base = { press: null, receipt: null, work: null, driver: { connected: true, mode: 'claudeDrives', wheel: false, assistantWorking: true, name: 'Your assistant' }, hasNext: true };
  assert.deepEqual(C.slotOf(base), { kind: 'work', who: 'assistant', verb: C.ASSISTANT_WORKING, count: null });
  assert.equal(C.slotOf({ ...base, hasNext: false }).kind, 'work', 'with no step to suggest too');
  assert.equal(C.slotOf({ ...base, driver: { ...base.driver, assistantWorking: false } }).kind, 'next', 'idle: the next step is back');
  assert.equal(C.slotOf({ ...base, driver: { ...base.driver, mode: 'iDrive' } }).kind, 'next', 'the person drives: the step is theirs to press');
  assert.equal(C.slotOf({ ...base, driver: { ...base.driver, wheel: true } }).kind, 'wheel', 'the person has the wheel');
  assert.equal(C.slotOf({ ...base, receipt: { kind: 'ran', text: 'Done.' } }).kind, 'done', 'the person\'s last result still wins');
  assert.ok(!/\u2014/.test(C.ASSISTANT_WORKING));
  assert.ok(/ASSISTANT_ACTIVE_MS/.test(src('plan-head.tsx')), 'the slot draws again when the assistant\'s quiet gap ends, so it never sticks on Working');
});

await check('a read of the plan that lands late is used when it is newer than the one on screen, so a long run of edits cannot starve the page (hero19 D3)', () => {
  const gate = C.createRereadGate();
  const a = gate.start(); const b = gate.start(); const c = gate.start();
  assert.equal(gate.accept(a), true, 'the oldest read lands first: it is shown, the page moves');
  assert.equal(gate.accept(c), true, 'a newer read replaces it');
  assert.equal(gate.accept(b), false, 'an older read that lands after a newer one is dropped');
  gate.reset();
  assert.equal(gate.accept(a), false, 'a read from before a reset (another model) is never shown');
  assert.equal(gate.accept(gate.start()), true);
  const plan = src('plan.tsx');
  assert.ok(plan.includes('createRereadGate'), 'the page reads through the gate');
});

await check('a row\'s own press: Run for Semanticus, Ask your assistant for the assistant, Open for you', () => {
  assert.equal(C.rowPressOf(item('a', 'A', 'semanticus', 'ready')), 'run');
  assert.equal(C.rowPressOf(item('a', 'A', 'semanticus', 'waiting')), null, 'a waiting row has no press');
  assert.equal(C.rowPressOf(item('a', 'A', 'semanticus', 'ready', { held: true })), null);
  assert.equal(C.rowPressOf(item('draft/x', 'A', 'semanticus', 'ready')), null, 'a draft runs on its own');
  assert.equal(C.rowPressOf(item('a', 'A', 'assistant', 'ready')), 'ask');
  assert.equal(C.rowPressOf(item('a', 'A', 'assistant', 'on_it')), null);
  assert.equal(C.rowPressOf(item('a', 'A', 'you', 'needs_you', { jump: { tool: 'readiness', label: 'AI readiness' } })), 'open');
  assert.equal(C.rowPressOf(item('a', 'A', 'you', 'needs_you')), null, 'nothing to open');
  assert.equal(C.rowPressOf(item('a', 'A', 'semanticus', 'done')), null);
  assert.deepEqual(C.ROW_PRESS_LABEL, { run: 'Run', ask: 'Ask your assistant', open: 'Open' });
});

await check('one row runs the way the assistant does it: propose, then apply_plan_items with that one row and its digest', () => {
  const none = item('a', 'A', 'semanticus', 'ready');
  assert.equal(C.proposedDigest(none), null);
  const worked = { ...none, changeSet: { state: 'proposed', digest: 'dg1', lines: [] } };
  assert.equal(C.proposedDigest(worked), 'dg1');
  assert.equal(C.proposedDigest({ ...none, changeSet: { state: 'applied', digest: 'dg1' } }), null);
  assert.deepEqual(C.applyOneCall('a', 'dg1'), ['applyPlanItems', [{ id: 'a', digest: 'dg1' }], 'human']);
  assert.deepEqual(C.askRowRequest({ id: 'a', title: 'Rewrite 3 descriptions' }), { words: 'Rewrite 3 descriptions', rowId: 'a', sendKey: 'row:a' });
});

await check('I drive also pauses the assistant: the switch shows I drive while the queue is paused', () => {
  assert.equal(C.driveValue('claudeDrives', 'on'), 'claudeDrives');
  assert.equal(C.driveValue('claudeDrives', 'paused'), 'iDrive');
  assert.equal(C.driveValue('iDrive', 'on'), 'iDrive');
  assert.equal(C.driveValue('claudeDrives', 'on', true), 'iDrive', 'the person has the wheel: "You have the wheel" never sits beside a pressed "Claude drives"');
  const head = src('plan-head.tsx');
  assert.match(head, /const want = mode === 'iDrive' \? 'paused' : 'on';\s*if \(queue !== want\) onQueue\(want\);/, 'choosing a side sets the engine queue');
});

await check('I drive pauses the assistant\'s tasks everywhere, and the Plan header sets the queue itself (one call, the whole plan back)', async () => {
  const calls = [], posts = [];
  const wire = makeLoader({ '../bridge': { postToHost: (m) => posts.push(m), rpc: async (...a) => { calls.push(a); return null; } } })('studio/driver-wire.ts');
  wire.setDriveMode('iDrive');
  wire.setDriveMode('claudeDrives');
  wire.setDriveMode('iDrive', false);
  await new Promise((r) => setTimeout(r, 0));
  assert.deepEqual(calls, [['setAssistantQueue', 'paused'], ['setAssistantQueue', 'on']], 'the bar sets the queue; the header (queue false) does not call twice');
  assert.deepEqual(posts.map((m) => m.mode), ['iDrive', 'claudeDrives', 'iDrive'], 'the host hears every choice');
});

await check('the page: one Ask your assistant handler, the header carries the switch, the old pieces are gone', () => {
  const plan = src('plan.tsx'), head = src('plan-head.tsx'), ask = src('studio/ask-assistant.ts'), next = src('studio/next-line.tsx');
  assert.match(ask, /export async function askAssistant\(/);
  assert.match(next, /askAssistant\(askRequest\(view, step\)\)/, 'the slot presses through the one handler');
  assert.match(plan, /askAssistant\(C\.askRowRequest\(item\)\)/, 'a row presses through it too');
  assert.match(head, /useNextActions\(view \?\? null\)/, 'the slot presses through the shared next-step actions');
  assert.doesNotMatch(plan, /ReadingCard|PlanStrip|PlanHelpMark|RaiseLevelAction|plan-queue-on|plan-queue-paused/, 'the card, the strip, the ? button, the old switch are gone');
  assert.doesNotMatch(plan, /Waits for|plan-latest|plan-facts/);
  assert.match(head, /rpc<ReadingCard \| null>\('readingCard', false\)/, 'the fast first grade still comes from the engine\'s readingCard');
});

await check('the Plan hands the shell its chrome: the driver bar stands aside, the notes go to the bottom bar; no page has a Next bar any more', () => {
  const layer = src('studio/layer.tsx'), app = src('App.tsx'), bar = src('contextbar.tsx');
  assert.match(layer, /\{!calm && <DriverBar /);
  assert.doesNotMatch(app, /NextLine|studio-next-line/, 'the Next bar is gone from every page (Kane, 13 Oct)');
  assert.match(bar, /<PlanNotes \/>/);
});

const jevLine = (id, word, kept = true, extra = {}) => ({ id, line: '~ x', op: 'set_property', target: 'column:Sales/' + id, kept, provenance: { kind: 'decision', point: 'ai.schema', confidence: 0.9, confidenceWord: word }, ...extra });
const jevRow = (id, extra = {}) => item(id, 'Choose which 12 fields Copilot can see in Customer', 'semanticus', 'ready',
  { source: 'jev', value: '+4 AI ready', why: ['5 keys and ids Copilot cannot use are still visible to it.', 'Jev chose 7 of these.'],
    changeSet: { lines: [jevLine('a', 'sure'), jevLine('b', 'likely'), jevLine('c', 'unsure', false)] }, ...extra });

await check('Jev on a row (1.5, pick A): the mark, the worth and the one-line reason are read off the engine row', () => {
  const row = jevRow('jev.ai.table:Customer');
  assert.equal(P.jevChoseRow(row), true);
  assert.equal(P.aiWorthOf(row), '+4 AI ready');
  assert.equal(P.jevReasonOf(row), '5 keys and ids Copilot cannot use are still visible to it.');
  // a row Jev chose nothing for (the progress row, a rule's row) gets no mark and no reason
  const progress = item('jev.progress', 'Jev is still looking', 'semanticus', 'waiting', { who: 'jev', why: ['Jev is working.'] });
  assert.equal(P.jevChoseRow(progress), false); assert.equal(P.jevReasonOf(progress), null);
  assert.equal(P.jevChoseRow(item('safefix/x', 'Fix', 'semanticus', 'ready', { source: 'found' })), false);
});

await check('the worth is only the engine\'s own "+N AI ready", never a made-up number or another phrase', () => {
  assert.equal(P.aiWorthOf(jevRow('a', { value: '' })), null);
  assert.equal(P.aiWorthOf(jevRow('a', { value: undefined })), null);
  assert.equal(P.aiWorthOf(jevRow('a', { value: 'fewer best-practice issues' })), null);
  assert.equal(P.aiWorthOf(jevRow('a', { value: '+0 AI ready ' })), '+0 AI ready');
});

await check('"Jev chose N." joins the Done line only when the run wrote lines Jev chose (kept lines with a confidence word)', () => {
  assert.equal(P.jevChoseCount([jevRow('r1'), item('r2', 'R2', 'semanticus', 'ready')]), 2);   // sure + likely; the dropped unsure line is not Jev's applied choice
  assert.equal(P.jevChoseCount([item('r2', 'R2', 'semanticus', 'ready')]), 0);
  assert.equal(C.doneWords(5, 4000, 37), 'Done. 5 tasks in 4 s. Jev chose 37 settings.');
  assert.equal(C.doneWords(5, 4000, 0), 'Done. 5 tasks in 4 s.');
  assert.equal(C.doneWords(1, 3000, 1), 'Done. 1 task in 3 s. Jev chose 1 setting.');
});

await check('"Jev chose N." counts the rows the waves really ran: a press that stops after one wave, and a row it skipped', async () => {
  const held = [jevRow('r1'), jevRow('r2'), jevRow('r3')];
  const plan = (rev) => ({ revision: rev, readyDigest: 'd' + rev, items: held, strip: { tracks: [] } });
  // wave 1 runs r1 only, wave 2 throws: the press held 6 Jev lines and wrote 2
  let k = 0;
  const stopped = await P.runAllWaves(plan(0), async () => { if (++k === 1) return { performed: true, applied: 2, itemIds: ['r1'], revision: 1, entryId: 'e1', plan: plan(1) }; throw new Error('boom'); }, async () => plan(1));
  assert.deepEqual(stopped.receipt.ranIds, ['r1']);
  assert.equal(P.jevChoseRan(held, stopped.receipt.ranIds), 2);
  assert.equal(P.jevChoseCount(held), 6, 'counting the held rows is the old, wrong figure');
  // one wave that skipped r2: r1 and r3 ran
  const skipped = await P.runAllWaves(plan(0), async () => ({ performed: true, applied: 4, itemIds: ['r1', 'r3'], revision: 1, entryId: 'e1', plan: { ...plan(1), items: [] } }), async () => plan(1));
  assert.equal(P.jevChoseRan(held, skipped.receipt.ranIds), 4);
  assert.equal(P.jevChoseRan(held, undefined), 0);
  assert.doesNotMatch(src('plan.tsx'), /const jevChose = P\.jevChoseCount\(holding\)/u, 'the page no longer counts before the run');
});

await check('"Jev chose N." on one row counts the row after it was worked out, not the row before (a row Jev fills during the run)', () => {
  const before = jevRow('r1', { changeSet: { lines: [] } });   // no lines yet: Jev fills them when Run on this row works it out
  const after = jevRow('r1');                                   // the same row in the fresh plan: sure + likely kept, one dropped
  assert.equal(P.jevChoseCount([before]), 0, 'the row as it was written has nothing of Jev' + String.fromCharCode(39) + 's');
  assert.equal(P.jevChoseCount([after]), 2);
  assert.equal(C.doneWords(1, 3000, P.jevChoseCount([after])), 'Done. 1 task in 3 s. Jev chose 2 settings.');
  assert.match(src('plan.tsx'), /P\.jevChoseCount\(\[ran\]\)/u, 'Run on one row counts the worked-out row');
  assert.doesNotMatch(src('plan.tsx'), /P\.jevChoseCount\(\[item\]\)/u, 'and not the row before it was worked out');
});

await check('an open Jev row says its reason once: the detail leaves that line out of Why', () => {
  const row = src('plan-row.tsx');
  assert.match(row, /const jevReason = P\.jevReasonOf\(item\);\s*const why = \(item\.why \?\? \[\]\)\.filter\(\(w\) => Boolean\(w\) && !\(jevReason && w\.trim\(\) === jevReason\)\)/u);
  assert.match(row, /\(why\.length > 0 \|\| !jevReason\) && <><dt>Why<\/dt>/u);
});

await check('the row draws the mark after the title, the worth before the state word, and the reason when open', () => {
  const row = src('plan-row.tsx'), css = src('plan-calm.css'), tokens = src('tokens.css'), page = src('plan.tsx');
  assert.match(row, /pw-title-jev[\s\S]*\{jev && <JevMark \/>\}/u, 'the mark follows the title');
  assert.ok(row.indexOf('<RowWorth') < row.indexOf('<StateWord', row.indexOf('<RowWorth')), 'the worth comes before the state word');
  assert.match(page, /openId === item\.id && P\.jevReasonOf\(item\) && <JevReason item=\{item\} \/>/u);
  assert.match(tokens, /--sem-jev: var\(--sem-series-4\)/u, 'Jev is Semanticus jade one step lighter, the chart palette\'s fourth step');
  assert.match(css, /\.pw-jev \{[^}]*color: var\(--sem-jev\)/u); assert.match(css, /\.pw-worth \{[^}]*color: var\(--sem-jev\)/u);
  assert.match(page, /doneWords\(out\.receipt\.rows, Date\.now\(\) - startedAt, P\.jevChoseRan\(holding, out\.receipt\.ranIds\)\)/u);
});

await check('no string on the calm page has an em dash or "one click"', () => {
  for (const f of ['plan-head.tsx', 'plan-calm-model.ts', 'plan-notes.tsx', 'plan-calm.css', 'plan-row.tsx', 'studio/ask-assistant.ts']) {
    const text = src(f);
    assert.ok(!text.includes('—'), `${f} has an em dash`);
    assert.ok(!/one click/i.test(text), `${f} says one click`);
  }
});

await check('a row the assistant is working on reads On it, not Waiting (hero20 D3)', () => {
  const waiting = item('a', 'Write words for Copilot', 'assistant', 'waiting');
  assert.equal(P.stateWordOf(waiting), 'waiting');
  assert.equal(P.stateWordOf({ ...waiting, assistantAtWork: true }), 'on_it');
  assert.equal(P.stateWordOf({ ...item('b', 'Done row', 'assistant', 'done'), assistantAtWork: true }), 'done', 'a finished row keeps its word');
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('plan-calm: all checks passed');
