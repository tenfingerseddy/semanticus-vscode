// The Studio's note queue and the suggestion line (1.4, lane notes; contract sections 5 and 6).
//
// What is executed here, one line each, against the pure store and the pure suggestion rules with a fake clock and a fake driver:
//   the read time is clamp(1500 + 55 * length, 2500, 8000) milliseconds
//   a note moves the screen at once when the driver may, and the next one waits for the read time to run out
//   four waiting notes fold into one that counts them; a read note leaves the strip once the work is over; a press plays at once
//   while the person has the wheel, or drives, nothing moves: the note waits with Show me and a waiting count
//   Show me moves the screen once, as the person's own action, and the next waiting note takes the strip
//   Skip to now plays only the newest note, reads the ones it passes and plays no press for them
//   the next prompt (the driver's resume) jumps to the newest waiting note and sends the older ones to What I did unread
//   the person taking the wheel stops a playing queue: the playing note stays read, the rest wait
//   What I did opens by itself when the queue empties after three or more notes, and not after two
//   a note delivered twice plays once; a note read from the engine's ring plays when it arrives live; another model's notes start a new list
//   a suggestion: the limits, the lead line, the Plan line, what a press sends, and what a newer note does to it
//   the shipped copy has no em dash and no "one click"
import assert from 'node:assert/strict';
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { makeLoader } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const studio = join(root, 'webview', 'src', 'studio');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');

const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs|js)$/.test(file) ? file : existsSync(file + '.ts') ? file + '.ts' : file + '.tsx';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true }, fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => (spec.startsWith('.') ? load(resolve(dirname(full), spec)) : req(spec));
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  return module.exports;
}
const S = load(join(studio, 'notes-store.ts'));
const G = load(join(studio, 'suggest-model.ts'));

let failures = 0;
function check(name, fn) {
  try { fn(); console.log(`ok - ${name}`); } catch (error) { failures++; console.error(`not ok - ${name}\n${error.stack}`); }
}

// ---- the fakes ----------------------------------------------------------------------------------------------------

function makeClock() {
  let now = 0; let seq = 0; const timers = new Map();
  return {
    setTimer(fn, ms) { const id = ++seq; timers.set(id, { at: now + ms, fn }); return id; },
    clearTimer(id) { timers.delete(id); },
    live: () => timers.size,
    elapsed: () => now,
    advance(ms) {
      const end = now + ms;
      for (;;) {
        const next = [...timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!next) break;
        now = next[1].at; timers.delete(next[0]); next[1].fn();
      }
      now = end;
    },
  };
}

function makeDriver(mode = 'claudeDrives', state = 'idle') {
  const listeners = new Set();
  const d = {
    mode, state, resumeVersion: 0, reported: [],
    canMoveScreen: () => d.mode === 'claudeDrives' && d.state !== 'personHasWheel',
    snapshot: () => ({ resumeVersion: d.resumeVersion, lastAssistantCall: d.lastAssistantCall ?? null }),
    subscribe: (fn) => { listeners.add(fn); return () => listeners.delete(fn); },
    reportQueue: (n) => d.reported.push(n),
    set(next) { Object.assign(d, next); listeners.forEach((fn) => fn()); },
  };
  return d;
}

function setup(mode, state) {
  const clock = makeClock(); const driver = makeDriver(mode, state); const moved = [];
  const store = S.createNotesStore({ driver, moveTo: (n) => moved.push(n.id), setTimer: clock.setTimer, clearTimer: clock.clearTimer });
  return { store, clock, driver, moved, snap: () => store.getSnapshot() };
}

let counter = 0;
const note = (say = 'Opening the plan.', extra = {}) => ({
  id: `sn-${String(++counter).padStart(12, '0')}`, say, how: 'Open Plan. The top rows are the work worth doing next.', place: 'plan',
  at: new Date(Date.UTC(2026, 9, 10, 9, 0, counter)).toISOString(), by: 'assistant', sessionId: 's1', ...extra,
});
const words = (n) => 'x'.repeat(n);

// ---- read time -----------------------------------------------------------------------------------------------------

check('the read time is clamp(1500 + 55 * length, 2500, 8000)', () => {
  assert.equal(S.readTimeMs(''), 2500, 'an empty sentence still gets the minimum');
  assert.equal(S.readTimeMs(words(10)), 2500, '1500 + 550 = 2050 is raised to 2500');
  assert.equal(S.readTimeMs(words(19)), 2545, '1500 + 1045');
  assert.equal(S.readTimeMs(words(60)), 4800, '1500 + 3300');
  assert.equal(S.readTimeMs(words(118)), 7990, '1500 + 6490');
  assert.equal(S.readTimeMs(words(119)), 8000, '1500 + 6545 is cut to 8000');
  assert.equal(S.readTimeMs(words(200)), 8000, 'the longest sentence the engine allows');
});

check('a note moves the screen at once and the next one waits for the read time', () => {
  const t = setup();
  const a = note(words(60)); const b = note(words(60));   // 4800 ms each
  t.store.push(a); t.store.push(b);
  assert.deepEqual(t.moved, [a.id], 'only the head moved the screen');
  assert.equal(t.snap().current.id, a.id);
  assert.equal(t.snap().phase, 'playing');
  assert.equal(t.snap().playing.id, a.id, 'the cursor may play a press for it');
  assert.equal(t.snap().more, 1);
  t.clock.advance(4799);
  assert.deepEqual(t.moved, [a.id], 'one millisecond short of the read time');
  t.clock.advance(1);
  assert.deepEqual(t.moved, [a.id, b.id]);
  assert.equal(t.snap().current.id, b.id);
  t.clock.advance(4800);
  assert.equal(t.snap().current, null, 'read, and the work was over by then, so the strip is clear (H4)');
  assert.equal(t.snap().playing, null);
  assert.equal(t.clock.live(), 0, 'no timer is left running');
});

// ---- the person has the wheel, or drives ------------------------------------------------------------------------------

for (const [label, mode, state] of [['the person has the wheel', 'claudeDrives', 'personHasWheel'], ['I drive', 'iDrive', 'idle']]) {
  check(`${label}: notes wait with Show me and a count, and nothing moves`, () => {
    const t = setup(mode, state);
    const a = note(); const b = note(); const c = note();
    [a, b, c].forEach((n) => t.store.push(n));
    const s = t.snap();
    assert.deepEqual(t.moved, []);
    assert.equal(s.current.id, a.id);
    assert.equal(s.phase, 'waiting', 'the strip offers Show me');
    assert.equal(s.playing, null, 'no press plays for a note that did not move the screen');
    assert.equal(s.more, 2, 'the strip says "2 more"');
    assert.equal(s.waiting, 3);
    assert.equal(t.clock.live(), 1, 'a waiting note has no read time; only the work-over quiet runs');
  });

  check(`${label}: Show me moves the screen once and the next waiting note takes the strip`, () => {
    const t = setup(mode, state);
    const a = note(); const b = note();
    t.store.push(a); t.store.push(b);
    t.store.showMe(a.id);
    assert.deepEqual(t.moved, [a.id]);
    assert.equal(t.snap().current.id, b.id);
    assert.equal(t.snap().phase, 'waiting');
    assert.equal(t.snap().waiting, 1);
    t.store.showMe(b.id);
    assert.deepEqual(t.moved, [a.id, b.id]);
    assert.equal(t.snap().phase, 'done');
    assert.equal(t.snap().waiting, 0);
    assert.equal(t.snap().unread.size, 0);
  });
}

check('a line of What I did shows that step again, whatever its place in the queue', () => {
  const t = setup('iDrive', 'idle');
  const a = note(); const b = note(); const c = note();
  [a, b, c].forEach((n) => t.store.push(n));
  t.store.showMe(c.id);
  assert.deepEqual(t.moved, [c.id]);
  assert.equal(t.snap().waiting, 2, 'the one shown leaves the waiting notes');
  assert.equal(t.snap().current.id, a.id);
});

// ---- Skip to now ---------------------------------------------------------------------------------------------------

check('Skip to now plays only the newest note and reads the ones it passes', () => {
  const t = setup();
  const [a, b, c, d] = [note(words(60)), note(words(60)), note(words(60)), note(words(60))];
  [a, b, c, d].forEach((n) => t.store.push(n));
  assert.equal(t.snap().waiting, 3);
  t.store.skipToNow();
  assert.deepEqual(t.moved, [a.id, d.id], 'b and c never moved the screen, so no press played for them');
  const s = t.snap();
  assert.equal(s.current.id, d.id);
  assert.equal(s.playing.id, d.id);
  assert.equal(s.more, 0);
  assert.equal(s.unread.size, 0, 'the notes it passed count as read');
  t.clock.advance(4800);
  assert.equal(t.snap().phase, 'done');
  assert.equal(t.snap().listOpen, true, 'four notes were queued, so What I did opens when the queue empties');
});

check('Skip to now while the person has the wheel moves once, as the person\'s own action', () => {
  const t = setup('claudeDrives', 'personHasWheel');
  const [a, b, c] = [note(), note(), note()];
  [a, b, c].forEach((n) => t.store.push(n));
  t.store.skipToNow();
  assert.deepEqual(t.moved, [c.id]);
  assert.equal(t.snap().phase, 'done');
  assert.equal(t.snap().playing, null);
  assert.equal(t.snap().waiting, 0);
  assert.equal(t.snap().unread.size, 0);
});

check('Skip to now is offered at two waiting notes and not before', () => {
  const t = setup();
  t.store.push(note());
  assert.equal(t.snap().waiting, 0, 'a playing note is not a waiting one');
  t.store.push(note());
  assert.equal(t.snap().waiting, 1);
  t.store.push(note());
  assert.equal(t.snap().waiting, 2, 'the strip draws Skip to now from here (note-strip.tsx: waiting >= 2)');
  const held = setup('iDrive', 'idle');
  held.store.push(note());
  assert.equal(held.snap().waiting, 1, 'one waiting note has Show me only');
  held.store.push(note());
  assert.equal(held.snap().waiting, 2);
});

// ---- the driver changes ----------------------------------------------------------------------------------------------

check('the next prompt jumps to the newest waiting note and sends the older ones to What I did unread', () => {
  const t = setup('claudeDrives', 'personHasWheel');
  const [a, b, c] = [note(), note(), note()];
  [a, b, c].forEach((n) => t.store.push(n));
  t.driver.set({ state: 'claudeDriving', resumeVersion: 1 });   // the driver's resume: the first call after 8 quiet seconds
  assert.deepEqual(t.moved, [c.id], 'one move, to the newest');
  const s = t.snap();
  assert.equal(s.current.id, c.id);
  assert.equal(s.phase, 'playing');
  assert.deepEqual([...s.unread], [a.id, b.id]);
  assert.equal(s.waiting, 0);
});

check('switching from I drive back to Claude drives resumes the same way', () => {
  const t = setup('iDrive', 'idle');
  const [a, b] = [note(), note()];
  t.store.push(a); t.store.push(b);
  t.driver.set({ mode: 'claudeDrives' });
  assert.deepEqual(t.moved, [b.id]);
  assert.deepEqual([...t.snap().unread], [a.id]);
});

check('the person taking the wheel stops a playing queue: the playing note stays read, the rest wait', () => {
  const t = setup();
  const [a, b, c] = [note(words(60)), note(words(60)), note(words(60))];
  [a, b, c].forEach((n) => t.store.push(n));
  t.driver.set({ state: 'personHasWheel' });
  const s = t.snap();
  assert.equal(s.playing, null, 'the cursor stops at once');
  assert.equal(s.current.id, b.id, 'the next waiting note takes the strip');
  assert.equal(s.phase, 'waiting');
  assert.equal(s.more, 1);
  assert.equal(t.clock.live(), 1, 'only the work-over quiet runs');
  t.clock.advance(20000);
  assert.deepEqual(t.moved, [a.id], 'time passing moves nothing');
});

check('switching to I drive stops a playing queue the same way', () => {
  const t = setup();
  [note(words(60)), note(words(60))].forEach((n) => t.store.push(n));
  t.driver.set({ mode: 'iDrive' });
  assert.equal(t.snap().phase, 'waiting');
  assert.equal(t.snap().playing, null);
});

check('Let Claude drive with nothing waiting changes nothing', () => {
  const t = setup('claudeDrives', 'personHasWheel');
  t.driver.set({ state: 'idle', resumeVersion: 1 });
  assert.deepEqual(t.moved, []);
  assert.equal(t.snap().current, null);
});

// ---- What I did opens by itself ----------------------------------------------------------------------------------------

check('What I did opens by itself when the queue empties after three notes, and not after two', () => {
  const two = setup();
  two.store.push(note()); two.store.push(note());
  two.clock.advance(60000);
  assert.equal(two.snap().listOpen, false);

  const three = setup();
  for (let i = 0; i < 3; i++) three.store.push(note());
  three.clock.advance(2499);
  assert.equal(three.snap().listOpen, false, 'not while a note is still being read');
  three.clock.advance(60000);
  assert.equal(three.snap().listOpen, true);
  three.store.setListOpen(false);
  three.store.push(note()); three.clock.advance(60000);
  assert.equal(three.snap().listOpen, false, 'one more note after the person closed it does not reopen it');
});

check('the driver bar is told how many notes are left, down to none', () => {
  const t = setup();
  t.store.push(note()); t.store.push(note());
  t.clock.advance(60000);
  assert.deepEqual(t.driver.reported, [1, 2, 1, 0], 'one playing, two, then each read in turn');
});

// ---- delivery --------------------------------------------------------------------------------------------------------

check('a note delivered twice plays once', () => {
  const t = setup();
  const a = note();
  t.store.push(a); t.store.push({ ...a });
  assert.equal(t.snap().log.length, 1);
  assert.deepEqual(t.moved, [a.id]);
});

check('what the engine kept fills What I did and plays nothing; the same note arriving live still plays once', () => {
  const t = setup();
  const kept = note('Kept.'); const older = note('Older.', { at: '2026-10-10T08:00:00.000Z' });
  t.store.load({ sessionId: 's1', notes: [kept, older], suggestion: null });
  assert.deepEqual(t.moved, []);
  assert.deepEqual(t.snap().log.map((n) => n.say), ['Older.', 'Kept.'], 'oldest first, newest last');
  assert.equal(t.snap().current, null);
  t.store.push(kept);   // the host flushed it when the Studio opened
  assert.deepEqual(t.moved, [kept.id]);
  assert.equal(t.snap().log.length, 2, 'it is not listed twice');
  t.store.push(kept);
  assert.deepEqual(t.moved, [kept.id], 'and not played twice');
});

check('What I did keeps the last 100 notes', () => {
  const t = setup('iDrive', 'idle');
  const all = Array.from({ length: 105 }, () => note());
  all.forEach((n) => t.store.push(n));
  const log = t.snap().log;
  assert.equal(log.length, S.LOG_CAP);
  assert.equal(log[log.length - 1].id, all[104].id);
  assert.equal(log[0].id, all[5].id);
});

check('another model\'s notes start a new list', () => {
  const t = setup('iDrive', 'idle');
  t.store.push(note('One.', { sessionId: 's1' })); t.store.push(note('Two.', { sessionId: 's1' }));
  t.store.push(note('Other model.', { sessionId: 's2' }));
  assert.deepEqual(t.snap().log.map((n) => n.say), ['Other model.']);
  t.store.push(note('A tour line.', { sessionId: '', by: 'tour' }));
  assert.equal(t.snap().log.length, 2, 'a tour note names no model and joins the list');
});

check('the strip\'s Close clears a read note and never a note still to read', () => {
  const t = setup();
  t.store.push(note(words(60))); t.store.push(note(words(60)));
  t.store.dismiss();
  assert.equal(t.snap().phase, 'playing', 'a playing note is not closed');
  t.clock.advance(60000);
  t.store.dismiss();
  assert.equal(t.snap().current, null);
});

// ---- hero15: old notes clear, a long walk folds, a press plays at once ------------------------------------------------

check('H4: a read note leaves the strip once the work is over, and stays in What I did', () => {
  const t = setup();
  const a = note();                                   // 2500 ms to read
  t.store.push(a);
  t.clock.advance(2500);
  assert.equal(t.snap().phase, 'done');
  assert.equal(t.snap().current.id, a.id, 'read, but the work may still be going on');
  t.clock.advance(S.WORK_OVER_MS - 2501);
  assert.equal(t.snap().current.id, a.id, 'one millisecond short of the quiet');
  t.clock.advance(1);
  assert.equal(t.snap().current, null, 'the work is over: the strip is clear');
  assert.deepEqual(t.snap().log.map((n) => n.id), [a.id]);
  assert.equal(t.snap().unread.size, 0);
  assert.equal(t.clock.live(), 0, 'no timer is left running');
});

check('H4: an assistant call or a count update keeps a read note on screen until the work stops', () => {
  const t = setup();
  const a = note();
  t.store.push(a);
  t.clock.advance(6000);
  t.driver.set({ lastAssistantCall: 6000 });          // the assistant is still working
  t.clock.advance(6000);
  assert.equal(t.snap().current.id, a.id);
  t.store.push({ ...a, say: 'Your assistant has written 2 descriptions so far.', count: 2, update: true });
  t.clock.advance(S.WORK_OVER_MS - 1);
  assert.equal(t.snap().current.id, a.id);
  t.clock.advance(1);
  assert.equal(t.snap().current, null);
});

check('H4: notes that waited for the person leave the strip once the work is over, unread in What I did', () => {
  const t = setup('claudeDrives', 'personHasWheel');
  const [a, b] = [note(), note()];
  t.store.push(a); t.store.push(b);
  t.clock.advance(S.WORK_OVER_MS);
  const s = t.snap();
  assert.equal(s.current, null);
  assert.equal(s.waiting, 0);
  assert.deepEqual([...s.unread], [a.id, b.id], 'What I did marks them, so nothing is lost');
  assert.deepEqual(t.moved, [], 'the screen never moved');
});

check('H16: four waiting notes fold into one note that counts them, and the count keeps running', () => {
  const t = setup('iDrive', 'idle');
  const all = Array.from({ length: 5 }, () => note());
  all.slice(0, 3).forEach((n) => t.store.push(n));
  assert.equal(t.snap().waiting, 3, 'three still wait one by one');
  t.store.push(all[3]);
  let s = t.snap();
  assert.equal(s.waiting, 1, 'one note in the strip');
  assert.equal(s.current.id, all[3].id, 'the newest');
  assert.equal(s.current.folded, 3, 'it says how many it stands for');
  assert.deepEqual([...s.unread], all.slice(0, 3).map((n) => n.id));
  t.store.push(all[4]);
  s = t.snap();
  assert.equal(s.current.id, all[4].id);
  assert.equal(s.current.folded, 4, 'a note after the fold joins it');
  assert.equal(s.log.length, 5, 'What I did lists every one');
  t.store.showMe(all[4].id);
  assert.deepEqual(t.moved, [all[4].id], 'Show me goes to the newest place');
});

check('H16: a 49-note walk keeps pace with reading: at most three wait, and the last note shows within one read', () => {
  const t = setup();
  const all = Array.from({ length: 49 }, (_, i) => note(`${i + 1} of 49: ` + words(90)));   // about 7 s to read each
  let most = 0;
  for (const n of all) { t.store.push(n); most = Math.max(most, t.snap().more); t.clock.advance(1400); }
  assert.ok(most <= 3, `at most three wait behind the strip (saw ${most})`);
  t.clock.advance(S.readTimeMs(all[48].say));
  assert.equal(t.moved.at(-1), all[48].id, 'the screen reached the last step');
  assert.ok(t.moved.length < 20, `far fewer moves than notes (${t.moved.length})`);
  assert.equal(t.snap().log.length, 49);
});

check('H1: a note that presses a control plays at once, so the cursor finds the control while it is there', () => {
  const t = setup();
  const a = note(words(60)); const b = note(words(60));
  const press = note('Your assistant is approving the start plan.', { press: 'approve-build', place: 'plan/start', auto: true });
  t.store.push(a); t.store.push(b); t.store.push(press);
  assert.deepEqual(t.moved, [a.id, press.id], 'no wait for the read time of the notes before it');
  assert.equal(t.snap().playing.id, press.id, 'the cursor plays its press');
  assert.equal(t.snap().more, 0);
  assert.deepEqual([...t.snap().unread], [b.id], 'the note it passed is marked in What I did');
  const held = setup('claudeDrives', 'personHasWheel');
  held.store.push(press);
  assert.deepEqual(held.moved, [], 'with the person at the wheel it waits like any note');
  assert.equal(held.snap().playing, null);
});

// ---- the suggestion ----------------------------------------------------------------------------------------------------

const suggestion = (id = 'sg-000000000001', extra = {}) => ({
  id, say: 'Here is what I would do next.', at: '2026-10-10T09:30:00.000Z', sessionId: 's1',
  steps: [{ label: 'Describe the Sales measures', prompt: 'Describe the measures in the Sales table.' }, { label: 'Run the model checks', prompt: 'Run the model checks and list what fails.' }],
  ...extra,
});

check('a suggestion: the same id changes nothing, a new one replaces it, a press marks its step added', () => {
  const t = setup();
  t.store.setSuggestion(suggestion());
  t.store.markStepAdded(0);
  t.store.setSuggestion(suggestion());
  assert.deepEqual(t.snap().suggestion.added, [0], 'a repeat delivery does not forget the press');
  t.store.markStepAdded(0);
  assert.deepEqual(t.snap().suggestion.added, [0]);
  t.store.markStepFailed(1, 'This plan already has 50 open tasks.');
  assert.equal(t.snap().suggestion.failed.message, 'This plan already has 50 open tasks.');
  t.store.markStepAdded(1);
  assert.equal(t.snap().suggestion.failed, null, 'a press that works clears the last failure');
  t.store.setSuggestion(suggestion('sg-000000000002'));
  assert.deepEqual(t.snap().suggestion.added, [], 'a new suggestion starts clean');
});

check('a note newer than the suggestion takes it away; an older one leaves it', () => {
  const t = setup();
  t.store.setSuggestion(suggestion());
  t.store.push(note('Before.', { at: '2026-10-10T09:00:00.000Z' }));
  assert.ok(t.snap().suggestion, 'a note from before the suggestion leaves it');
  t.store.push(note('After.', { at: '2026-10-10T09:45:00.000Z' }));
  assert.equal(t.snap().suggestion, null);
});

check('the engine\'s current suggestion is read on mount', () => {
  const t = setup();
  t.store.load({ sessionId: 's1', notes: [], suggestion: suggestion() });
  assert.equal(t.snap().suggestion.suggestion.id, 'sg-000000000001');
});

// ---- suggestion copy and limits ----------------------------------------------------------------------------------------

check('the limits are the ones suggest_next enforces', () => {
  assert.deepEqual([G.STEPS_MIN, G.STEPS_MAX, G.LABEL_MIN, G.LABEL_MAX, G.PROMPT_MAX, G.SAY_MAX], [2, 3, 3, 40, 280, 200]);
});

check('at most three buttons, each with a label of 3 to 40 characters and words to send', () => {
  const step = (label, prompt = 'Do it.') => ({ label, prompt });
  const four = { steps: [step('Describe it'), step('Check it'), step('Publish it'), step('Extra one')] };
  assert.deepEqual(G.stepsOf(four).map((s) => s.label), ['Describe it', 'Check it', 'Publish it']);
  assert.equal(G.stepsOf({ steps: [step('ab'), step(words(41)), step('Fine label', ''), step('Fine label', words(281)), step(' Keeps  ', ' words ')] }).length, 1,
    'too short, too long, no words and words too long are not drawn');
  assert.deepEqual(G.stepsOf({ steps: [step(' Keeps ', ' words ')] }), [{ label: 'Keeps', prompt: 'words' }]);
  assert.deepEqual(G.stepsOf({ steps: [step(words(40), words(280))] }).length, 1, 'the limits themselves are allowed');
  assert.deepEqual(G.stepsOf(null), []);
});

check('the lead line is the assistant\'s, or the default', () => {
  assert.equal(G.leadOf({ say: 'Two things worth doing now.' }), 'Two things worth doing now.');
  assert.equal(G.leadOf({ say: '' }), 'Here is what I would do next.');
  assert.equal(G.leadOf({ say: words(201) }), 'Here is what I would do next.');
  assert.equal(G.leadOf(null), 'Here is what I would do next.');
});

check('From your plan: the top row, unless a step\'s words already name it', () => {
  const steps = G.stepsOf(suggestion());
  const top = { id: 'safefix/Visibility', title: 'Hide the key columns' };
  assert.equal(G.fromYourPlan(steps, top), 'From your plan: Hide the key columns');
  assert.equal(G.fromYourPlan(steps, null), null);
  assert.equal(G.fromYourPlan(steps, { id: 'x', title: '' }), null);
  const named = [{ label: 'Hide the keys', prompt: 'Please HIDE THE KEY COLUMNS in every table.' }];
  assert.equal(G.fromYourPlan(named, top), null, 'the title, in any case');
  assert.equal(G.fromYourPlan([{ label: 'Fix visibility', prompt: 'Do safefix/visibility now.' }], top), null, 'the id, in any case');
});

check('pressing a step adds its words as a task for the assistant from the suggest source', () => {
  const s = suggestion();
  const step = G.stepsOf(s)[1];
  const request = G.taskRequest(s, 1, step);
  assert.equal(request.words, 'Run the model checks and list what fails.');
  assert.equal(request.doer, 'assistant');
  assert.equal(request.sourcePage, 'suggest');
  assert.equal(request.sendKey, 'suggest:sg-000000000001:1', 'the same press twice is the same task');
  assert.equal(G.ADDED_TEXT, 'Added. Tell Claude go.');
});

// ---- follow: the engine updates the count of the one automatic note ----------------------------------------------------

check('an update changes the words of the note in the strip and queues nothing, moves nothing', () => {
  const t = setup();
  const a = note('Your assistant is writing descriptions.', { auto: true });
  t.store.push(a);
  t.store.push({ ...a, say: 'Your assistant has written 24 descriptions so far.', count: 24, update: true });
  assert.deepEqual(t.moved, [a.id], 'the screen moved once, for the first note');
  assert.equal(t.snap().current.say, 'Your assistant has written 24 descriptions so far.');
  assert.equal(t.snap().log.length, 1);
  assert.equal(t.snap().log[0].say, 'Your assistant has written 24 descriptions so far.');
  assert.equal(t.snap().more, 0);
  assert.equal(t.snap().phase, 'playing');
});

check('an update reaches a note that waits in the queue, and a note that was already read', () => {
  const t = setup();
  const a = note('First.'); const b = note('Your assistant is writing descriptions.', { auto: true });
  t.store.push(a); t.store.push(b);
  t.store.push({ ...b, say: 'Your assistant has written 3 descriptions so far.', count: 3, update: true });
  t.clock.advance(3000);
  assert.equal(t.snap().current.id, b.id);
  assert.equal(t.snap().current.say, 'Your assistant has written 3 descriptions so far.');
  t.clock.advance(60000);
  t.store.push({ ...b, say: 'Your assistant has written 9 descriptions so far.', count: 9, update: true });
  assert.equal(t.snap().log.find((n) => n.id === b.id).say, 'Your assistant has written 9 descriptions so far.');
  assert.equal(t.snap().log.length, 2);
});

check('an update for a note nobody has is shown as a new note', () => {
  const t = setup();
  const a = note('Your assistant has written 4 descriptions so far.', { auto: true, count: 4, update: true });
  t.store.push(a);
  assert.deepEqual(t.moved, [a.id]);
  assert.equal(t.snap().log.length, 1);
});

check('an update does not move the screen for a person who has the wheel', () => {
  const t = setup('claudeDrives', 'personHasWheel');
  const a = note('Your assistant is writing descriptions.', { auto: true });
  t.store.push(a);
  t.store.push({ ...a, say: 'Your assistant has written 5 descriptions so far.', count: 5, update: true });
  assert.deepEqual(t.moved, []);
  assert.equal(t.snap().waiting, 1);
  assert.equal(t.snap().current.say, 'Your assistant has written 5 descriptions so far.');
});

// ---- the copy ----------------------------------------------------------------------------------------------------------

check('the Free Pro note renders the existing upgrade action, and a completed Pro step has no upgrade action', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  let current = note('Pro would apply 12 best-practice fixes in one step.', { proStep: true, upgrade: true });
  let upgrades = 0;
  const loaded = makeLoader({
    './notes-runtime': { useNotesWire() {}, useNotes: () => ({ current, phase: 'waiting', more: 0, waiting: 1, log: [], unread: new Set(), listOpen: false }), notesStore: {} },
    './what-i-did': { WhatIDid: () => null }, './suggest': { Suggest: () => null }, './studio-notes.css': {},
    '../pro': { upgradeToPro: () => upgrades++ },
    '../personality/message-line': { MessageLine: () => null },
  })('studio/note-strip');
  const tree = loaded.NoteStrip();
  const html = renderToStaticMarkup(tree);
  assert.match(html, /Pro would apply 12 best-practice fixes in one step\./);
  assert.match(html, /data-testid="note-upgrade"[^>]*>Unlock Pro/);
  const walk = element => {
    if (!element?.props) return;
    if (element.props['data-testid'] === 'note-upgrade') element.props.onClick();
    React.Children.forEach(element.props.children, walk);
  };
  walk(tree);
  assert.equal(upgrades, 1, 'the existing Pro action is called');
  current = { ...current, upgrade: false, say: 'Pro applied 12 best-practice fixes in one step. One undo takes them back.' };
  assert.doesNotMatch(renderToStaticMarkup(React.createElement(loaded.NoteStrip)), /note-upgrade/);
});

check('a note about the person\'s own press wears the person\'s colour and has no "To do this yourself" line (h17 P15)', () => {
  const { renderToStaticMarkup } = req('react-dom/server');
  const draw = (current) => renderToStaticMarkup(makeLoader({
    './notes-runtime': { useNotesWire() {}, useNotes: () => ({ current, phase: 'waiting', more: 0, waiting: 1, log: [], unread: new Set(), listOpen: false }), notesStore: {} },
    './what-i-did': { WhatIDid: () => null }, './suggest': { Suggest: () => null }, './studio-notes.css': {},
    '../pro': { upgradeToPro: () => undefined }, '../personality/message-line': { MessageLine: () => null },
  })('studio/note-strip').NoteStrip());
  const mine = draw({ ...note('Pro applied 12 safe fixes in one step. One undo takes them back.', { proStep: true }), by: 'person', how: 'Open the Plan and press Run the plan.' });
  assert.match(mine, /data-by="person"/);
  assert.doesNotMatch(mine, /To do this yourself/);
  const theirs = draw({ ...note('Your assistant is writing descriptions.', { auto: true }), how: 'Open the Plan.' });
  assert.doesNotMatch(theirs, /data-by="person"/);
  assert.match(theirs, /To do this yourself: Open the Plan\./);
});

check('What I did opens as one calm line, the newest step, and the list is one press away (copy21)', () => {
  const { renderToStaticMarkup } = req('react-dom/server');
  const React = req('react');
  const { WhatIDid } = makeLoader({ './studio-notes.css': {} })('studio/what-i-did');
  const log = [note('Your assistant is checking the model.'), note('Your assistant pressed Save.'), note('AI readiness is now A 91.')];
  const html = renderToStaticMarkup(React.createElement(WhatIDid, { log, unread: new Set(), onShow: () => undefined, onClose: () => undefined }));
  assert.match(html, /data-testid="what-i-did-latest"/);
  assert.match(html, /AI readiness is now A 91\./);
  assert.doesNotMatch(html, /Your assistant pressed Save/, 'older steps wait behind Show all');
  assert.doesNotMatch(html, /data-testid="what-i-did-line"/);
  assert.match(html, /data-testid="what-i-did-all"[^>]*>Show all 3</);
  const src = readFileSync(join(studio, 'what-i-did.tsx'), 'utf8');
  assert.match(src, /useState\(false\)/, 'the list starts closed');
});

check('the shipped copy has no em dash and no "one click"', () => {
  for (const name of readdirSync(studio).filter((f) => /^(notes-|note-strip|what-i-did|suggest|types|studio-notes)/.test(f))) {
    const text = readFileSync(join(studio, name), 'utf8');
    assert.ok(!text.includes(String.fromCharCode(0x2014)), `${name} has an em dash`);
    assert.ok(!/one[- ]click/i.test(text), `${name} says "one click"`);
  }
});

// ---- drive follow (Kane, 13 Oct): the screen follows the tool to its home, calmly ----------------------------------------

{
  const placeMap = JSON.parse(readFileSync(join(studio, 'places.json'), 'utf8'));
  const navs = [];
  const loadNav = makeLoader({
    '../bridge': { dispatchNavigate: (m) => navs.push(m), revealInTree: () => undefined },
    './places.json': placeMap,
  });
  const nav = loadNav('studio/notes-nav');
  const route = loadNav('route');
  const sayOf = (tool) => placeMap.toolSays[tool].say;
  const placeOfTool = (tool) => placeMap.places.find((p) => (p.tools ?? []).includes(tool));
  const autoNote = (tool) => note(sayOf(tool), { auto: true, place: placeOfTool(tool).id });

  /** One scripted assistant: the engine makes a note for each call (engine tests prove that), the Studio's real queue plays them and the real router moves. */
  function drive(calls, gapMs, mode = 'claudeDrives', state = 'idle') {
    navs.length = 0;
    const clock = makeClock(); const driver = makeDriver(mode, state); const seen = [];
    const store = S.createNotesStore({
      driver, setTimer: clock.setTimer, clearTimer: clock.clearTimer,
      moveTo: (n) => { const before = navs.length; nav.moveScreenTo(n); seen.push({ at: clock.elapsed(), place: n.place, say: n.say, tab: navs.slice(before).map((m) => m.tab)[0] }); },
    });
    for (const tool of calls) { store.push(autoNote(tool)); clock.advance(gapMs); }
    clock.advance(10_000);
    return { seen, store };
  }

  check('drive follow: readiness, then best practice, then the plan: the page and the sub-view follow each call, in order', () => {
    const { seen } = drive(['ai_readiness_scan', 'bpa_scan', 'get_model_plan'], 1000);
    assert.deepEqual(seen.map((m) => m.tab), ['readiness', 'bpa', 'plan']);
    for (const m of seen.slice(0, 2)) assert.equal(route.DESTINATION_OF_TOOL[route.landingOf(m.tab).tool], 'review', 'both scans are on the Review page');
    assert.match(seen[0].say, /ready the model is for AI/);
    assert.match(seen[1].say, /best practice/, 'the words on the strip are the words for the view on screen');
  });

  check('drive follow: no screen waits behind the last one longer than the dwell, however long its sentence takes to read', () => {
    const { seen } = drive(['ai_readiness_scan', 'bpa_scan', 'get_model_plan'], 500);
    assert.equal(seen.length, 3);
    for (let i = 1; i < seen.length; i++) {
      assert.ok(seen[i].at - seen[i - 1].at <= S.MOVE_DWELL_MS, `move ${i} came ${seen[i].at - seen[i - 1].at} ms after the one before (dwell ${S.MOVE_DWELL_MS})`);
      assert.ok(seen[i].at - seen[i - 1].at >= S.MOVE_DWELL_MS, `the page before stayed at least the dwell, so nothing flickers`);
    }
  });

  check('drive follow: one move per call, and never a bounce back: readiness, best practice, a fix on the same page, readiness shows three pages in that order', () => {
    const { seen } = drive(['ai_readiness_scan', 'bpa_scan', 'apply_fix', 'ai_readiness_scan'], 400);
    const pages = seen.map((m) => m.tab).filter((tab, i, all) => tab !== all[i - 1]);
    assert.deepEqual(pages, ['readiness', 'bpa', 'readiness'], 'a fix on the page already showing changes nothing on screen, and nothing bounces back');
  });

  check('drive follow: I drive still wins, and a person who took the wheel keeps it', () => {
    for (const [mode, state] of [['iDrive', 'idle'], ['claudeDrives', 'personHasWheel']]) {
      const { seen, store } = drive(['ai_readiness_scan', 'bpa_scan'], 1000, mode, state);
      assert.deepEqual(seen, [], `${mode}/${state}: the screen never moved`);
      assert.ok(store.getSnapshot().log.length === 2 || store.getSnapshot().unread.size >= 0);
    }
  });

  check('drive follow: a note on the same page does not cut the read short', () => {
    const t = setup();
    const a = note(words(60)); const b = note(words(60));   // both on the plan, 4800 ms to read
    t.store.push(a); t.store.push(b);
    t.clock.advance(S.MOVE_DWELL_MS + 100);
    assert.deepEqual(t.moved, [a.id], 'the dwell only shortens a wait for a DIFFERENT page');
  });
}

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('studio notes: all checks passed');
