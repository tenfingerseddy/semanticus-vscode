// The Link lane (1.5): the working verbs, the job state machine behind The Link, the Link's motion frames, the sound rules and
// the personality settings. Runs the real webview modules through the transpiling loader; the bridge is stubbed.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader } from './helpers/webview-load.mjs';

const posted = [];
const load = makeLoader({});
const verbs = load('personality/verbs.ts');
const state = load('personality/link-state.ts');
const settings = load('personality/settings.ts');
const sound = load('personality/sound.ts');
const link = load('personality/link.tsx');
const heard = {};
link.wirePersonality({ postToHost: (m) => posted.push(m),
  onAssistantCall: (fn) => { heard.call = fn; return () => {}; }, onActivity: (fn) => { heard.activity = fn; return () => {}; },
  onProgress: (fn) => { heard.progress = fn; return () => {}; }, onPlanRunProgress: (fn) => { heard.plan = fn; return () => {}; } });

// A clock and a one-timer scheduler the test moves by hand.
function clock() {
  let t = 1_000_000; const timers = [];
  const schedule = (fn, ms) => { const x = { at: t + ms, fn, live: true }; timers.push(x); return () => { x.live = false; }; };
  const advance = (ms) => {
    const end = t + ms;
    for (;;) {
      const next = timers.filter((x) => x.live && x.at <= end).sort((a, b) => a.at - b.at)[0];
      if (!next) break;
      t = next.at; next.live = false; next.fn();
    }
    t = end;
  };
  return { now: () => t, schedule, advance, pending: () => timers.filter((x) => x.live).length };
}

test('verbs: the group follows the tool, reads keep the job group, Quiet says the plain words', () => {
  const g = verbs.groupForTool;
  assert.equal(g('run_dax'), 'DAX');
  assert.equal(g('create_measure'), 'DAX');
  assert.equal(g('evaluate_and_log'), 'DAX');
  assert.equal(g('create_relationship'), 'Relationships');
  assert.equal(g('set_relationship_crossfilter'), 'Relationships');
  assert.equal(g('write_words'), 'Readiness');
  assert.equal(g('ai_readiness_scan'), 'Readiness');
  assert.equal(g('deploy_live'), 'Publishing');
  assert.equal(g('git_commit'), 'Publishing');
  assert.equal(g('propose_measure_defaults'), 'Jev');
  assert.equal(g('edit_objects'), 'Modelling');
  assert.equal(g('get_model_summary'), null);
  assert.equal(g('list_measures'), null);
  assert.equal(verbs.verbFor('DAX', 3, true), 'Writing DAX');
  assert.equal(verbs.verbFor(null, 0, false), 'Working');
  const shown = new Set(Array.from({ length: 30 }, (_, i) => verbs.verbFor('Jev', i, false, 5)));
  assert.deepEqual([...shown].sort(), [...verbs.VERBS.Jev].sort(), 'a Jev job cycles through every Jev verb and only those');
});

test('verbs: every word is plain (no em dash, no emoji), and the counts match the design page', () => {
  const counts = Object.fromEntries(Object.entries(verbs.VERBS).map(([k, v]) => [k, v.length]));
  assert.deepEqual(counts, { Modelling: 10, DAX: 12, Relationships: 10, Readiness: 10, Publishing: 8, Jev: 6 });
  for (const w of [...Object.values(verbs.VERBS).flat(), ...Object.values(verbs.PLAIN)]) {
    assert.ok(!/[–—]/.test(w), w);
    assert.ok(!/\p{Extended_Pictographic}/u.test(w), w);
  }
});

test('link state: a DAX query works, thinks past 8 s, ends on its result, links up, then rests with no timer left', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  assert.equal(w.getSnapshot().state, 'idle');
  assert.equal(c.pending(), 0, 'idle sets no timer');
  w.call('run_dax');
  assert.equal(w.getSnapshot().state, 'working');
  assert.equal(w.getSnapshot().group, 'DAX');
  assert.equal(w.getSnapshot().who, 'assistant');
  c.advance(5000);
  assert.equal(w.getSnapshot().state, 'working', 'a slow call stays open past the quiet gap');
  c.advance(3500);
  assert.equal(w.getSnapshot().state, 'thinking', 'past 8 seconds it thinks hard');
  w.activity({ origin: 'agent', ok: true, kind: 'run_dax' });
  assert.equal(w.getSnapshot().state, 'thinking');
  c.advance(state.QUIET_END_MS);
  assert.equal(w.getSnapshot().state, 'success');
  c.advance(state.SUCCESS_MS);
  assert.equal(w.getSnapshot().state, 'idle');
  assert.equal(c.pending(), 0);
});

test('link state: a burst of edits is one job; the verb follows the latest kind of work; the engine words ride the success', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.call('get_model_summary');
  assert.equal(w.getSnapshot().group, 'Readiness', 'a read alone reads the model');
  const job = w.getSnapshot().job;
  c.advance(800); w.call('create_relationship');
  assert.equal(w.getSnapshot().group, 'Relationships');
  c.advance(800); w.call('list_columns');
  assert.equal(w.getSnapshot().group, 'Relationships', 'a read keeps the group');
  c.advance(800); w.call('write_words');
  w.activity({ origin: 'agent', ok: true, kind: 'write_words', words: { sentence: 'Added synonyms to 50 fields.' } });
  assert.equal(w.getSnapshot().job, job, 'still one job');
  c.advance(state.QUIET_END_MS);
  assert.equal(w.getSnapshot().state, 'success');
  assert.equal(link.statusWords(w.getSnapshot(), 0, false), 'Done. Added synonyms to 50 fields.');
});

// h17 P3 (verifier proof): hero17 lane d sent six deploy_live calls about a minute apart and the Link stayed "thinking" for six minutes. The cause was
// the engine, which never reported deploy_live as model/activity, so each call held the Link open for OPEN_CALL_CAP_MS. The engine now ends every
// deploy_live and publish_new_model with an activity (McpTools, pinned in DeployGateScopeTests); this pins the Link's side of that pair.
test('link state: deploy_live calls a minute apart each end with the engine activity, so the Link rests between them (h17 P3)', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  for (let i = 0; i < 6; i++) {
    w.call('deploy_live');
    w.activity({ origin: 'agent', ok: true, kind: 'deploy_live' });
    c.advance(state.QUIET_END_MS + state.SUCCESS_MS + 500);
    assert.equal(w.getSnapshot().state, 'idle', 'the Link rests once the engine says the publish ended');
    c.advance(60_000);
  }
  assert.equal(c.pending(), 0);
});

test('link state: a deploy_live that fails ends the Link in error, not in thinking (h17 P3)', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.call('deploy_live');
  w.activity({ origin: 'agent', ok: false, kind: 'deploy_live', error: 'The live model refused the write.' });
  assert.equal(w.getSnapshot().state, 'error');
  c.advance(state.ERROR_MS + 100);
  assert.equal(w.getSnapshot().state, 'idle');
});

test('link state: a failure turns grey with the engine sentence and no joke, then rests', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.call('run_dax');
  w.activity({ origin: 'agent', ok: false, error: 'The query names a column that does not exist.' });
  assert.equal(w.getSnapshot().state, 'error');
  assert.equal(link.statusWords(w.getSnapshot(), 4, false), 'Stopped. The query names a column that does not exist.');
  c.advance(state.ERROR_MS);
  assert.equal(w.getSnapshot().state, 'idle');
  assert.equal(c.pending(), 0);
});

test('link state: Jev deciding thinks hard with Jev verbs; Jev landing links up; a lost landing still ends', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.setJevPending(true);
  assert.equal(w.getSnapshot().state, 'thinking');
  assert.equal(w.getSnapshot().group, 'Jev');
  assert.ok(verbs.VERBS.Jev.includes(link.statusWords(w.getSnapshot(), 1, false)));
  assert.equal(link.statusWords(w.getSnapshot(), 1, true), 'Jev is choosing');
  c.advance(30_000);
  assert.equal(w.getSnapshot().state, 'thinking', 'Jev keeps the job open');
  w.setJevPending(false);
  c.advance(400);
  assert.equal(w.getSnapshot().state, 'success');
  const w2 = state.createLinkWork(c.now, c.schedule);
  w2.setJevPending(true);
  c.advance(state.JEV_CAP_MS + state.QUIET_END_MS + state.SUCCESS_MS);
  assert.equal(w2.getSnapshot().state, 'idle', 'a plan page that closed before Jev landed does not leave the Link thinking');
});

test('link state: the person\'s own job (DAX Lab busy) works with DAX verbs and links up soon after it stops', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.setBusySurfaces(['daxlab']);
  assert.equal(w.getSnapshot().state, 'working');
  assert.equal(w.getSnapshot().who, 'person');
  assert.equal(w.getSnapshot().group, 'DAX');
  c.advance(20_000);
  assert.equal(w.getSnapshot().state, 'thinking');
  w.setBusySurfaces([]);
  c.advance(400);
  assert.equal(w.getSnapshot().state, 'success');
  w.progress({ opKey: 'bpa', done: 12, total: 40 });
  assert.deepEqual(w.getSnapshot().progress, { done: 12, total: 40 });
});

test('the S: the dots start and end on the logo\'s end dots and meet at the middle of the bar', () => {
  const near = (a, b) => assert.ok(Math.abs(a[0] - b[0]) < 1e-6 && Math.abs(a[1] - b[1]) < 1e-6, `${a} vs ${b}`);
  near(link.pointOnS(0), [19, 5.5]);
  near(link.pointOnS(1), [5, 18.5]);
  near(link.pointOnS(0.5), [12, 12]);
  // The far left of the top bend and the far right of the bottom bend.
  const xs = Array.from({ length: 201 }, (_, i) => link.pointOnS(i / 200)[0]);
  assert.ok(Math.abs(Math.min(...xs) - 5) < 0.01 && Math.abs(Math.max(...xs) - 19) < 0.01);
});

test('motion: dots move by transform only; reduced motion holds them still; a 16 px idle Link does not animate at all', () => {
  const working = link.keyframesFor('working', false, true, null);
  assert.equal(working.duration, 2400);
  const dot = working.tracks.find((t) => t.el === 'dotA').frames;
  assert.ok(dot.every((f) => Object.keys(f).join() === 'transform'));
  assert.ok(new Set(dot.map((f) => f.transform)).size > 20, 'the dot travels');
  const reduced = link.keyframesFor('working', true, true, null);
  assert.equal(new Set(reduced.tracks.find((t) => t.el === 'dotA').frames.map((f) => f.transform)).size, 1, 'reduced: still dots');
  assert.ok(new Set(reduced.tracks.find((t) => t.el === 'bar').frames.map((f) => f.opacity)).size > 2, 'reduced: the bar still fades');
  assert.equal(link.periodOf('idle', false, true), 0);
  assert.equal(link.periodOf('idle', false, false), 3.2, 'the 200 px Link breathes');
  assert.equal(link.periodOf('error', false, false), 0, 'error is still');
  const ok = link.keyframesFor('success', false, false, null);
  const a = ok.tracks.find((t) => t.el === 'dotA').frames, b = ok.tracks.find((t) => t.el === 'dotB').frames;
  const mid = Math.round(a.length * 0.5);
  assert.equal(a[mid].transform.split(' scale')[0], b[mid].transform.split(' scale')[0], 'at the peak the dots meet');
  assert.equal(a.at(-1).transform, link.keyframesFor('idle', false, false, null).tracks[0].frames[0].transform.replace(/scale\([^)]*\)/, 'scale(1.0000)'), 'and go home');
});

test('sound: off by default; Quiet mutes; nothing before a gesture or out of focus; 300 ms apart; ticks at most 25 a second', () => {
  const on = { sound: true, personality: 'on' };
  const last = () => ({ any: -Infinity, tick: -Infinity });
  assert.equal(sound.mayPlay('pop', 0, settings.DEFAULT_PERSONALITY_SETTINGS, true, true, last()), false, 'sounds start off');
  assert.equal(sound.mayPlay('pop', 0, { sound: true, personality: 'quiet' }, true, true, last()), false);
  assert.equal(sound.mayPlay('pop', 0, on, false, true, last()), false, 'no gesture yet');
  assert.equal(sound.mayPlay('pop', 0, on, true, false, last()), false, 'window not in focus');
  const l = last();
  assert.equal(sound.mayPlay('snap', 1000, on, true, true, l), true);
  assert.equal(sound.mayPlay('chime', 1200, on, true, true, l), false);
  assert.equal(sound.mayPlay('chime', 1300, on, true, true, l), true);
  assert.equal(sound.mayPlay('tick', 1301, on, true, true, l), true, 'ticks are exempt from the 300 ms rule');
  assert.equal(sound.mayPlay('tick', 1320, on, true, true, l), false);
  assert.equal(sound.mayPlay('tick', 1341, on, true, true, l), true);
  assert.deepEqual(Object.keys(sound.VOICES).sort(), ['chime', 'pop', 'snap', 'tick']);
});

test('settings: the host message sets them, Motion follows VS Code and then the OS, the page writes back through the host', () => {
  let os = false;
  const s = settings.createPersonalityStore(() => os);
  const seen = [];
  s.subscribe((v) => seen.push(v));
  assert.deepEqual(s.get(), { personality: 'on', sound: false, volume: 0.4, reducedMotion: false });
  s.receive({ type: 'personalitySettings', personality: 'quiet', sounds: true, soundVolume: 70, reduceMotion: 'auto' });
  assert.deepEqual(s.get(), { personality: 'quiet', sound: true, volume: 0.7, reducedMotion: false });
  os = true; s.osChanged();
  assert.equal(s.get().reducedMotion, true, 'auto follows the OS');
  s.receive({ type: 'personalitySettings', personality: 'quiet', sounds: true, soundVolume: 70, reduceMotion: 'off' });
  assert.equal(s.get().reducedMotion, false, 'VS Code says off');
  s.receive({ type: 'personalitySettings', personality: 'bogus', sounds: 'yes', soundVolume: 'loud' });
  assert.deepEqual(s.get(), { personality: 'on', sound: false, volume: 0.4, reducedMotion: true });
  assert.equal(seen.length, 4);
  posted.length = 0;
  settings.setSound(true);
  assert.equal(settings.getPersonalitySettings().sound, true, 'the switch shows at once');
  settings.setPersonality('quiet');
  settings.setSoundVolume(0.55);
  assert.deepEqual(posted.map((m) => [m.type, m.key, m.value]), [
    ['setPersonalitySetting', 'sounds', true], ['setPersonalitySetting', 'personality', 'quiet'], ['setPersonalitySetting', 'soundVolume', 55]]);
  assert.equal(typeof heard.call, 'function', 'App hands The Link the bridge: it hears assistant calls');
  heard.call({ tool: 'bpa_scan' });
  assert.equal(state.linkWork.getSnapshot().state, 'working', 'a real assistant call reaches the shared Link');
  assert.equal(state.linkWork.getSnapshot().group, 'Readiness');
  const off = settings.onPersonalitySettingsChange(() => {});
  assert.equal(typeof off, 'function');
});

// plan-status (hero20 D4): a scan the engine never reports on must not hold the Link open for two minutes after Claude's last call.
test('link state: a bpa_scan with no result event ends within seconds of the last call; run_dax still waits for its result (hero20 D4)', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.call('set_description');
  w.call('bpa_scan');
  c.advance(state.SILENT_CALL_CAP_MS + 100);
  assert.equal(w.getSnapshot().state, 'success', 'the Link stopped about 15 seconds after the last call, not 120');
  c.advance(state.SUCCESS_MS + 100);
  assert.equal(w.getSnapshot().state, 'idle');
  assert.equal(c.pending(), 0);
  w.call('run_dax');
  c.advance(30_000);
  assert.ok(['working', 'thinking'].includes(w.getSnapshot().state), 'a query the engine reports on stays open until its result');
  w.activity({ origin: 'agent', ok: true, kind: 'run_dax' });
  c.advance(state.QUIET_END_MS + 100);
  assert.equal(w.getSnapshot().state, 'success');
});

// Hero 24 D8: "RETURNing the favour" and "Removing filters, ALL of them" stayed on the status bar for minutes over an Explain that never finished.
// Humour is for work that moves. Work with no sign of progress for a while says the plain fact, no joke.
test('status words: work that has gone quiet says the plain fact, work that moves keeps its verb (hero 24 D8)', () => {
  const c = clock();
  const w = state.createLinkWork(c.now, c.schedule);
  w.call('run_dax');
  const snap = w.getSnapshot();
  assert.ok(verbs.VERBS.DAX.includes(link.statusWords(snap, 1, false, 5_000)), 'a young job keeps its verb');
  assert.equal(link.statusWords(snap, 1, false, verbs.STUCK_MS + 1), verbs.WAITING.DAX, 'a job with no progress for a few seconds says what it waits on');
  assert.equal(link.statusWords(snap, 1, true, verbs.STUCK_MS - 1), verbs.PLAIN.DAX, 'Quiet is plain from the start');
  assert.ok(!verbs.VERBS.DAX.includes(link.statusWords(snap, 7, false, 10 * 60_000)));
  const moving = { ...snap, progress: { done: 3, total: 9 } };
  assert.ok(verbs.VERBS.DAX.includes(link.statusWords(moving, 1, false, verbs.STUCK_MS + 1)), 'a job that reports progress is not stuck');
});

test('the wait line names a step the person can see: Jev working in the background on the plan keeps its verb (UAT 17 Oct, "Waiting for Jev to answer 0:15" with nothing on screen)', () => {
  const base = { state: 'thinking', group: 'Jev', job: 1, startedAt: 0, endedAt: null, progress: null, words: null, error: null };
  const background = { ...base, who: 'assistant', jev: true };
  assert.ok(verbs.VERBS.Jev.includes(link.statusWords(background, 1, false, verbs.STUCK_MS + 1)), 'nothing on screen waits, so no wait line');
  assert.equal(link.statusWords({ ...background, who: 'person' }, 1, false, verbs.STUCK_MS + 1), verbs.WAITING.Jev, 'a press the person made does wait');
  assert.equal(verbs.WAITING.Jev, 'Waiting for Jev to finish this step');
  assert.equal(link.statusWords({ ...base, group: 'DAX', who: 'assistant', jev: false }, 1, false, verbs.STUCK_MS + 1), verbs.WAITING.DAX, 'a query that shows as a row still says what it waits on');
});
