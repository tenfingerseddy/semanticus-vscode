// The Studio wire, extension and webview half (1.4 Claude drives; contract sections 1, 2 and 4).
//
// The assistant's notes cross four hops: the engine's studio/show, the host's buffer (studioNotes.ts), the host's postMessage,
// and the webview's bridge listeners. This file EXECUTES each hop it owns:
//   1. places.json is the place map the engine embeds: every tool resolves through route.ts's landingOf, every "how" sentence
//      keeps the copy rules, and the ids are the contract's.
//   2. the host buffer posts at once when the Studio is ready, else holds the last 20 and flushes in order; it opens a closed
//      Studio only in Claude drives mode and only while the person has not taken the wheel.
//   3. bridge.ts hands each host message to its listener, and ignores a malformed one.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');

function load(file, stubs = {}) {
  const js = ts.transpileModule(readFileSync(file, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }, fileName: file,
  }).outputText;
  const module = { exports: {} };
  const localRequire = (name) => (name in stubs ? stubs[name] : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  return module.exports;
}

// ---- 1. the place map ---------------------------------------------------------------------------------------------------
const R = load(join(webview, 'src', 'route.ts'));
const map = JSON.parse(readFileSync(join(webview, 'src', 'studio', 'places.json'), 'utf8'));
assert.equal(map.version, 1);
const places = map.places;

const CONTRACT_IDS = [
  'plan', 'plan/start', 'plan/history', 'plan/changes',
  'explore/data', 'explore/ask', 'explore/diagram', 'explore/lineage', 'explore/size',
  'edit/queries', 'edit/properties', 'edit/instructions', 'edit/advanced', 'edit/power-query',
  'review/model-quality', 'review/ai-readiness', 'review/copilot-test', 'review/validation', 'review/docs',
  'publish/changes', 'publish/new-model', 'publish/data-agent', 'workflows', 'connections', 'help',
];
assert.deepEqual(places.map((p) => p.id), CONTRACT_IDS, 'the place ids are the contract\'s (adding or renaming one needs a line in the lane report)');
assert.equal(new Set(places.map((p) => p.id)).size, places.length, 'place ids are unique');

// Two places are not Studio tools: the host opens them (the Connections hub, the Help menu).
const HOST_WORDS = new Set(['connections', 'help']);
for (const p of places) {
  assert.ok(p.label && typeof p.label === 'string', `${p.id} has a label`);
  if (HOST_WORDS.has(p.tool)) continue;
  const landing = R.landingOf(p.tool);
  assert.ok(landing, `${p.id}: the tool "${p.tool}" resolves through landingOf`);
  assert.ok(R.DESTINATION_OF_TOOL[landing.tool], `${p.id}: it lands on a real Studio tool`);
}
assert.deepEqual(R.landingOf('history'), { tool: 'plan', view: 'history' }, 'plan/history lands on the Plan\'s History view');

// D4 (hero 14): plan/start opens the Plan's start view (the start form, or the start plan), not the old Model Spec draft page.
{
  const start = places.find((p) => p.id === 'plan/start');
  assert.equal(start.tool, 'plan', 'plan/start is the Plan, not the retired spec page');
  assert.equal(start.target, 'start');
}

// Follow (1.4): each place lists the MCP tools that work on it, and the engine narrates a call to one of them by itself.
{
  const says = map.toolSays;
  assert.ok(says && typeof says === 'object', 'places.json carries toolSays');
  const seen = new Set();
  for (const p of places) {
    for (const tool of p.tools ?? []) {
      assert.match(tool, /^[a-z][a-z0-9_]*$/, `${p.id}: ${tool} is a tool name`);
      assert.ok(!seen.has(tool), `${tool} is listed in one place only`);
      seen.add(tool);
      const say = says[tool];
      assert.ok(say && say.say, `${tool} has a sentence`);
      assert.ok(say.say.startsWith('Your assistant'), `${tool}: ${say.say}`);
      assert.ok(say.say.length <= 200);
      assert.ok(!(say.say + (say.count ?? '')).match(/—|–|one[- ]click/i), `${tool}: copy rules`);
      assert.doesNotMatch(say.say + (say.count ?? ''), /\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b/, `${tool}: no raw tool name in the sentence`);
      if (say.count) assert.ok(say.count.includes('{n}'), `${tool}: the count sentence holds {n}`);
    }
  }
  for (const tool of Object.keys(says)) assert.ok(seen.has(tool), `${tool} has a sentence but no place`);
  const where = (tool) => places.find((p) => (p.tools ?? []).includes(tool))?.id;
  assert.equal(where('ask_data'), 'explore/ask');
  assert.equal(where('set_description'), 'edit/properties');
  assert.equal(where('ai_readiness_scan'), 'review/ai-readiness');
  assert.equal(where('build_start_plan'), 'plan/start');
  assert.equal(where('publish_new_model'), 'publish/new-model');
  assert.equal(where('deploy_live'), 'publish/changes');
}

// The copy rules (CLAUDE.md, docs/product-copy-style.md): the Studio shows a "how" as "To do this yourself: <how>".
for (const p of places) {
  const how = p.how;
  assert.equal(typeof how, 'string', `${p.id} has a how`);
  assert.ok(how.length > 0 && how.length <= 120, `${p.id}: how is 120 characters or fewer (${how.length})`);
  assert.match(how, /^Open /, `${p.id}: how starts with Open`);
  assert.match(how, /\.$/, `${p.id}: how ends with a full stop`);
  assert.ok(!how.includes('—') && !how.includes('–'), `${p.id}: no dash used as a pause`);
  assert.doesNotMatch(how, /one[- ]click/i, `${p.id}: never "one click"`);
  assert.doesNotMatch(how, /\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b/, `${p.id}: no raw tool or enum name`);
  assert.doesNotMatch(how, /[\u{1F300}-\u{1FAFF}️]/u, `${p.id}: no emoji`);
  assert.doesNotMatch(how, /\bclaude\b/i, `${p.id}: the UI says your assistant, not Claude, outside the driver bar`);
  assert.doesNotMatch(how, /\b(MCP|RPC|JSON|engine|session|payload)\b/i, `${p.id}: no engine words`);
  assert.ok(how.split(/(?<=\.)\s+/).every((sentence) => sentence.split(/\s+/).length <= 25), `${p.id}: sentences are 25 words or fewer`);
}

// D4 (hero 14) and follow: what a note for plan/start does, and the answer the Ask page takes.
{
  assert.equal(R.PLAN_START_TARGET, 'start');
  const calls = [];
  const nav = load(join(webview, 'src', 'studio', 'notes-nav.ts'), {
    '../bridge': { dispatchNavigate: (m) => calls.push(m), revealInTree: () => {} },
    './places.json': { default: map },
  });
  nav.moveScreenTo({ id: 'sn-1', say: 'Reading your source.', how: '', place: 'plan/start', at: 't', by: 'assistant', sessionId: 's1' });
  assert.deepEqual(calls, [{ tab: 'plan', target: 'start', sessionId: 's1' }], 'plan/start asks the Plan for its start view, not the retired spec page');

  const app = readFileSync(join(webview, 'src', 'App.tsx'), 'utf8');
  assert.match(app, /tool === 'plan' && target === PLAN_START_TARGET\) return \{ kind: 'planStart'/, 'the shell turns the target into a one-use seed');
  assert.match(app, /showStart=\{showPlanStart\}/, 'the shell hands the seed to the Plan');
  const plan = readFileSync(join(webview, 'src', 'plan.tsx'), 'utf8');
  const effect = plan.slice(plan.indexOf('if (!showStart) return;'), plan.indexOf('if (!showStart) return;') + 420);
  assert.match(effect, /setView\('work'\)/, 'it leaves History for the work view (the second hero 14 note stayed on Plan History)');
  assert.match(plan, /if \(sp\.view\) \{ setPanel\('none'\); setShowBuilt\(true\); \} else setPanel\('start'\);/, 'the start plan when there is one, else the start form');
  assert.match(plan, /!startAsk \|\| !sp\.loaded/, 'it waits for the first read of the start plan');
}
{
  const A = load(join(webview, 'src', 'studio', 'answer-store.ts'), { react: { useSyncExternalStore: () => null } });
  assert.equal(A.getAssistantAnswer(), null);
  let heard = 0;
  const off = A.subscribeAssistantAnswer(() => { heard += 1; });
  const a1 = { id: 'sa-1', sessionId: 's1', at: new Date(Date.now() - 5000).toISOString(), question: 'sales by store', answer: { outcome: 'answered' } };
  A.setAssistantAnswer(a1); A.setAssistantAnswer({ ...a1 });
  assert.equal(heard, 1, 'the same answer twice is one change');
  assert.equal(A.getAssistantAnswer().id, 'sa-1');
  assert.equal(A.answerIsFor(a1, 's1'), true);
  assert.equal(A.answerIsFor(a1, 's2'), false, 'another model\'s answer is never shown');
  assert.equal(A.answerIsFor(a1, undefined), true);
  assert.equal(A.answerIsFor({ ...a1, at: new Date(Date.now() - 61000).toISOString() }, 's1'), false, 'the rows are the model\'s data: an old answer is not offered to a page that opens later');
  assert.equal(A.answerIsFor(null, 's1'), false);
  off();
  const ask = readFileSync(join(webview, 'src', 'query.tsx'), 'utf8') + readFileSync(join(webview, 'src', 'daxlab.tsx'), 'utf8');
  assert.match(ask, /Asked by your assistant/);
  assert.match(ask, /data-testid="query-by-assistant"/);
  assert.match(ask, /useAssistantAnswer\(\)/);
}

// ---- 2. the host buffer -------------------------------------------------------------------------------------------------
const { StudioNotes, STUDIO_BUFFER_LIMIT } = load(join(root, 'src', 'studioNotes.ts'));
assert.equal(STUDIO_BUFFER_LIMIT, 20);

function harness({ ready = false, open = false } = {}) {
  const h = { ready, open, posted: [], opened: 0 };
  h.notes = new StudioNotes({
    isReady: () => h.ready,
    isOpen: () => h.open,
    post: (m) => h.posted.push(m),
    open: () => { h.opened += 1; h.open = true; },
  });
  return h;
}
const show = (n) => ({ type: 'studioShow', payload: { id: `sn-${n}`, say: `Note ${n}` } });

{ // a ready Studio gets it at once, in order
  const h = harness({ ready: true, open: true });
  h.notes.push(show(1)); h.notes.push({ type: 'assistantCall', payload: { tool: 'x', at: 't' } }); h.notes.push(show(2));
  assert.deepEqual(h.posted.map((m) => m.type), ['studioShow', 'assistantCall', 'studioShow']);
  assert.equal(h.notes.waiting, 0);
  assert.equal(h.opened, 0);
}
{ // a Studio that is not ready holds them and flushes in order on studioReady
  const h = harness({ open: true });
  h.notes.push(show(1)); h.notes.push(show(2)); h.notes.push(show(3));
  assert.equal(h.posted.length, 0);
  h.notes.flush();
  assert.equal(h.posted.length, 0, 'flush before the Studio is ready posts nothing and loses nothing');
  assert.equal(h.notes.waiting, 3);
  h.ready = true; h.notes.flush();
  assert.deepEqual(h.posted.map((m) => m.payload.id), ['sn-1', 'sn-2', 'sn-3']);
  assert.equal(h.notes.waiting, 0);
  h.notes.flush();
  assert.equal(h.posted.length, 3, 'a second flush posts nothing twice');
}
{ // the last 20 are kept
  const h = harness({ open: true });
  for (let i = 1; i <= 25; i++) h.notes.push(show(i));
  h.ready = true; h.notes.flush();
  assert.equal(h.posted.length, 20);
  assert.equal(h.posted[0].payload.id, 'sn-6');
  assert.equal(h.posted[19].payload.id, 'sn-25');
}
{ // a call signal is live only: held it would read as a new prompt later
  const h = harness({ open: true });
  h.notes.push({ type: 'assistantCall', payload: { tool: 'x', at: 't' } });
  assert.equal(h.notes.waiting, 0);
}
{ // one current suggestion: a newer one replaces a waiting one
  const h = harness({ open: true });
  h.notes.push({ type: 'studioSuggest', payload: { id: 'sg-1', steps: [] } });
  h.notes.push(show(1));
  h.notes.push({ type: 'studioSuggest', payload: { id: 'sg-2', steps: [] } });
  h.ready = true; h.notes.flush();
  assert.deepEqual(h.posted.map((m) => m.payload.id), ['sn-1', 'sg-2']);
}
{ // the Next line (1.5): held for a Studio that is not ready, and a newer view replaces a waiting one
  const h = harness({ open: true });
  h.notes.push({ type: 'studioNextStep', payload: { id: 'ns-1' } });
  h.notes.push(show(1));
  h.notes.push({ type: 'studioNextStep', payload: { id: 'ns-2' } });
  assert.equal(h.opened, 0);
  h.ready = true; h.notes.flush();
  assert.deepEqual(h.posted.map((m) => m.payload.id), ['sn-1', 'ns-2']);
}
{ // an answer the assistant asked for: held for a Studio that is not ready, and a newer one replaces a waiting one
  const h = harness({ open: true });
  h.notes.push({ type: 'studioAnswer', payload: { id: 'sa-1' } });
  h.notes.push(show(1));
  h.notes.push({ type: 'studioAnswer', payload: { id: 'sa-2' } });
  h.ready = true; h.notes.flush();
  assert.deepEqual(h.posted.map((m) => m.payload.id), ['sn-1', 'sa-2']);
  const closed = harness();
  closed.notes.push({ type: 'studioAnswer', payload: { id: 'sa-1' } });
  assert.equal(closed.opened, 0, 'an answer alone does not open a closed Studio');
}
{ // a note opens a closed Studio in Claude drives mode, once
  const h = harness();
  h.notes.push(show(1)); h.notes.push(show(2));
  assert.equal(h.opened, 1, 'opened once, not once per note');
  h.ready = true; h.notes.flush();
  assert.equal(h.posted.length, 2, 'the notes that opened the Studio reach it');
}
{ // never for I drive, never while the person has the wheel, never for a suggestion or a call
  const iDrive = harness();
  iDrive.notes.setDriver({ mode: 'iDrive', state: 'idle' });
  iDrive.notes.push(show(1));
  assert.equal(iDrive.opened, 0);
  assert.equal(iDrive.notes.waiting, 1, 'the note waits for the next time the Studio opens');

  const wheel = harness();
  wheel.notes.setDriver({ mode: 'claudeDrives', state: 'personHasWheel' });
  wheel.notes.push(show(1));
  assert.equal(wheel.opened, 0);

  const quiet = harness();
  quiet.notes.push({ type: 'studioSuggest', payload: { id: 'sg-1', steps: [] } });
  quiet.notes.push({ type: 'assistantCall', payload: { tool: 'x', at: 't' } });
  assert.equal(quiet.opened, 0, 'only a note is worth opening a closed Studio for');

  const claude = harness();
  claude.notes.setDriver({ mode: 'claudeDrives', state: 'claudeDriving' });
  claude.notes.push(show(1));
  assert.equal(claude.opened, 1);
}

// ---- 3. the webview's bridge listeners -------------------------------------------------------------------------------------
{
  let onMessage;
  globalThis.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return {}; }, setState() {} });
  globalThis.window = { addEventListener: (type, fn) => { if (type === 'message') onMessage = fn; }, setTimeout, clearTimeout };
  const bridge = load(join(webview, 'src', 'bridge.ts'), {
    './engineupdated.mjs': { studioErrorText: (t) => t },
    './pagemodel': { pageModelIdentity: () => undefined },
  });
  for (const name of ['onStudioShow', 'onStudioSuggest', 'onAssistantCall', 'onDriveMode', 'onStudioAnswer']) {
    assert.equal(typeof bridge[name], 'function', `bridge.ts exports ${name}`);
  }
  assert.equal(typeof onMessage, 'function', 'bridge.ts listens for host messages');

  const heard = { show: [], suggest: [], call: [], mode: [] };
  const answers = [];
  const stopAnswer = bridge.onStudioAnswer((a) => answers.push(a));
  onMessage({ data: { type: 'studioAnswer', payload: { id: 'sa-1', answer: { outcome: 'answered' } } } });
  onMessage({ data: { type: 'studioAnswer', payload: { id: 'sa-2' } } });   // no answer inside: reaches nobody
  onMessage({ data: { type: 'studioAnswer', payload: null } });
  assert.deepEqual(answers.map((a) => a.id), ['sa-1']);
  stopAnswer();
  const stops = [
    bridge.onStudioShow((n) => heard.show.push(n)),
    bridge.onStudioSuggest((s) => heard.suggest.push(s)),
    bridge.onAssistantCall((c) => heard.call.push(c)),
    bridge.onDriveMode((m) => heard.mode.push(m)),
  ];
  const note = { id: 'sn-0123456789ab', say: 'Here is your Plan.', how: 'Open Plan.', place: 'plan', at: '2026-10-10T00:00:00Z', by: 'assistant' };
  const suggestion = { id: 'sg-0123456789ab', say: 'Next:', steps: [{ label: 'Run the plan', prompt: 'Run the plan.' }, { label: 'Save', prompt: 'Save it.' }], at: 't' };
  const call = { call: 'abc', tool: 'run_dax', at: '2026-10-10T00:00:01Z' };
  onMessage({ data: { type: 'studioShow', payload: note } });
  onMessage({ data: { type: 'studioSuggest', payload: suggestion } });
  onMessage({ data: { type: 'assistantCall', payload: call } });
  onMessage({ data: { type: 'driveMode', mode: 'iDrive' } });
  assert.deepEqual(heard, { show: [note], suggest: [suggestion], call: [call], mode: ['iDrive'] });

  // A malformed message reaches nobody.
  onMessage({ data: { type: 'studioShow', payload: null } });
  onMessage({ data: { type: 'studioShow', payload: { id: 'x' } } });
  onMessage({ data: { type: 'studioSuggest', payload: { id: 'x' } } });
  onMessage({ data: { type: 'assistantCall', payload: {} } });
  onMessage({ data: { type: 'driveMode', mode: 'sideways' } });
  assert.equal(heard.show.length, 1);
  assert.equal(heard.suggest.length, 1);
  assert.equal(heard.call.length, 1);
  assert.equal(heard.mode.length, 1);

  // A listener that throws starves nobody, and a stopped listener hears nothing more.
  const second = [];
  stops.push(bridge.onStudioShow(() => { throw new Error('a view broke'); }));
  stops.push(bridge.onStudioShow((n) => second.push(n)));
  onMessage({ data: { type: 'studioShow', payload: note } });
  assert.equal(second.length, 1);
  stops.forEach((stop) => stop());
  onMessage({ data: { type: 'studioShow', payload: note } });
  assert.equal(heard.show.length, 2, 'the first listener heard the second show, then was stopped');
  assert.equal(second.length, 1);
}

// ---- 4. the assistant's run of checks on the Validation page (show-checks, 12 Oct) ------------------------------------------------
// Kane: "when the assistant runs checks in validation the screen doesn't appear to change". The note moved the page, but the page
// only ever showed a run its own Run button made. The page now follows the assistant: its call shows "running", its result is the
// run on screen, and a failed or refused run says why. One pure model holds that, so this file can drive every step.
{
  const C = load(join(webview, 'src', 'studio', 'checks-run.ts'));
  const T0 = Date.parse('2026-10-12T09:00:00Z');
  const call = (tool, at = T0) => ({ tool, at: new Date(at).toISOString() });
  const finished = (extra = {}) => ({ seq: 7, kind: 'run_tests', origin: 'agent', ok: true, label: 'Ran your checks: grade B, 12 passed, 1 failed.', result: { runId: 'r1', health: { grade: 'B' } }, ...extra });

  let v = C.NO_ASSISTANT_RUN;
  assert.equal(v.phase, 'idle');
  assert.equal(C.runLine(v), null, 'nothing to say before the assistant runs anything');

  assert.equal(C.runStarted(v, call('get_model_plan'), T0).phase, 'idle', 'another tool does not start a run');
  v = C.runStarted(v, call('run_tests'), T0);
  assert.equal(v.phase, 'running');
  assert.equal(C.runLine(v), 'Your assistant is running your checks.');

  const done = C.runFinished(v, finished());
  assert.equal(done.phase, 'done');
  assert.equal(done.run.runId, 'r1', 'the run on screen is the one the assistant measured');
  assert.equal(C.runLine(done), 'Ran your checks: grade B, 12 passed, 1 failed.');

  // The result can arrive without the call (the call event is lost, or the page mounted late): it still shows.
  assert.equal(C.runFinished(C.NO_ASSISTANT_RUN, finished()).phase, 'done');
  // A run that did not happen says why and leaves no run to show.
  const refused = C.runFinished(v, finished({ ok: false, error: 'Held for your approval.', result: null, label: 'Tried to run your checks' }));
  assert.equal(refused.phase, 'failed');
  assert.equal(refused.run, null);
  assert.equal(C.runLine(refused), 'Held for your approval.');
  const bad = C.runFinished(v, finished({ result: { error: 'No open model.' } }));
  assert.equal(bad.phase, 'failed');
  assert.equal(C.runLine(bad), 'No open model.');
  // Another kind of activity is not a run.
  assert.equal(C.runFinished(v, finished({ kind: 'run_dax' })), v);
  // The same result twice changes nothing.
  assert.equal(C.runFinished(done, finished()), done);

  // A run that never reports back (the assistant stopped, the engine restarted) stops saying "running" after the wait.
  assert.equal(C.runStale(v, T0 + C.RUN_WAIT_MS - 1), v);
  assert.equal(C.runStale(v, T0 + C.RUN_WAIT_MS).phase, 'idle');
  assert.equal(C.runStale(done, T0 + C.RUN_WAIT_MS * 9), done, 'a finished run stays');
  // The person pressing Run takes over the page.
  assert.equal(C.personRan(done).phase, 'idle');
  assert.equal(C.personRan(done).run, null);
  // A scan the assistant asks for is a scan the page that shows it makes again, and both pages listen for it.
  assert.deepEqual(C.SCAN_PAGE_OF_TOOL, { bpa_scan: 'bpa', ai_readiness_scan: 'readiness' });
  const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
  assert.match(src('bpa.tsx'), /onAssistantCall\(\(call\) => \{ if \(SCAN_PAGE_OF_TOOL\[call\.tool\] === 'bpa'\) void scan\(\)/, 'Model quality scans again for the assistant\'s scan');
  assert.match(src('App.tsx'), /onAssistantCall\(\(call\) => \{ if \(SCAN_PAGE_OF_TOOL\[call\.tool\] === 'readiness'\) void scan\(\)/, 'AI readiness scans again for the assistant\'s scan');
  const page = src('tests.tsx');
  assert.match(page, /useClaudeReflection\('run_tests'/, 'Validation takes the assistant\'s run');
  assert.match(page, /onAssistantCall\(\(call\) => setAssistantRun\(/, 'Validation shows the assistant\'s run as running');

  // The other Validation tools (testspeed2, 13 Oct): one sentence on the page, and what the tool changed is read again.
  const t = C.toolNote({ seq: 9, kind: 'try_test', ok: true, label: 'Tried a check: Total Sales. The measure matched the number you trust.' });
  assert.equal(t.note.text, 'Tried a check: Total Sales. The measure matched the number you trust.');
  assert.equal(t.note.ok, true);
  assert.deepEqual(t.reload, [], 'a single try writes nothing, so nothing is read again');
  const failed = C.toolNote({ seq: 10, kind: 'record_test_run', ok: false, label: 'The run was not saved.', error: 'Only a full run can be recorded.' });
  assert.equal(failed.note.ok, false);
  assert.equal(failed.note.text, 'Only a full run can be recorded.', 'a failure says why');
  assert.deepEqual(failed.reload, ['history']);
  assert.deepEqual(C.toolNote({ kind: 'save_test', ok: true, label: 'Saved a check: A.' }).reload, ['suite', 'mappings']);
  assert.deepEqual(C.toolNote({ kind: 'delete_test', ok: true, label: 'Deleted a check.' }).reload, ['suite', 'mappings']);
  assert.deepEqual(C.toolNote({ kind: 'test_sql_source', ok: true, label: 'x' }).reload, ['sources']);
  assert.equal(C.toolNote({ kind: 'run_dax', ok: true, label: 'Ran DAX query' }), null, 'only Validation tools');
  assert.equal(C.toolNote({ kind: 'try_test', ok: true }), null, 'no sentence, no note');
  for (const kind of ['try_test', 'run_interview', 'reconcile_measure', 'get_unmatched_rows', 'record_test_run', 'test_sql_source', 'review_reconcile_mapping', 'export_test_report', 'save_test', 'delete_test'])
    assert.ok(C.TOOL_NOTE_KINDS.includes(kind), kind + ' is shown on the page');
  assert.match(page, /for \(const kind of TOOL_NOTE_KINDS\) useClaudeReflection\(kind, takeToolEvent\)/, 'Validation listens for every one of them');
  assert.match(page, /data-assistant-note=/, 'and shows the sentence');
  // Validation C (1.6): Jev's visual pick is a quick look for the assistant, not a Validation tool with a sentence of its own.
  assert.ok(!C.TOOL_NOTE_KINDS.includes('pick_check_visuals'), 'pick_check_visuals shows no sentence on the page');
  // Validation A (1.6): the page no longer prints how many visuals Jev chose; each visual's own line still says who chose it.
  assert.match(page, /by=\{chosen\.by\}/, 'the line under a visual says whether Jev or the rules chose it');
}

console.log('studio wire tests passed');
