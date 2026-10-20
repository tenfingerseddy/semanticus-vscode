// Hero 24 D8: engine words that reached people on the Query page. "needs confirm=true" (say what to tick), "[trace: sid=set spid=...]" (say that no
// query plan came back) and a run strip that read "First First First First First 6 7 8 9 10" (number the runs, mark the cold ones).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader } from './helpers/webview-load.mjs';

const load = makeLoader({});
const { plainEngineNote, splitEngineNote, plainClearRefusal } = load('enginewords.ts');
const parts = load('query-parts.tsx');

test('a plan that did not come back says so in plain words, with no trace line', () => {
  const engine = 'No query plan captured (the engine may have answered from cache / metadata only). [trace: sid=set spid=678939 warmup=2185ms raw=1]';
  assert.equal(plainEngineNote(engine), 'No query plan came back.');
  assert.doesNotMatch(plainEngineNote(engine), /trace|sid=|spid|raw=/);
});

test('cold runs skipped on a shared endpoint say what to tick, not the parameter name', () => {
  const engine = 'Cold runs skipped: clearing the cache on a shared/cloud endpoint needs confirm=true (it affects all users). Showing warm-only. ';
  const said = plainEngineNote(engine);
  assert.doesNotMatch(said, /confirm=true|warm-only/);
  assert.match(said, /tick "Shared endpoint: clearing the cache affects all users"/);
  assert.match(said, /measure again/);
});

test('every other note is left as the engine wrote it', () => {
  assert.equal(plainEngineNote('Storage timing data was unavailable.'), 'Storage timing data was unavailable.');
  assert.equal(plainEngineNote(null), '');
  assert.equal(plainEngineNote('  '), '');
});

test('the run strip numbers every run and marks the cold ones, so five cold runs read 1 to 5', () => {
  const labels = Array.from({ length: 10 }, (_, i) => parts.runLabel(i, i < 5));
  assert.deepEqual(labels, ['1·', '2·', '3·', '4·', '5·', '6', '7', '8', '9', '10']);
  assert.ok(!labels.some((l) => /first/i.test(l)));
});

// Real-user UAT F30: the raw "[trace: sid=set ... log=0]" line was on the Steps part, inside a "Technical details" fold. A person never sees
// trace diagnostics in the page; they go to the log.
test('a note with a trace line gives the page plain words and hands the trace line to the log', () => {
  const engine = 'The query ran. [trace: sid=set spid=678939 warmup=2185ms raw=1 log=0]';
  const { text, technical } = splitEngineNote(engine);
  assert.equal(text, 'The query ran.');
  assert.match(technical, /^\[trace: sid=set/);
  assert.equal(splitEngineNote('Storage timing data was unavailable.').technical, '');
});

test('the Query page never renders trace diagnostics', () => {
  const src = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src', 'daxlab.tsx'), 'utf8');
  assert.doesNotMatch(src, /Technical details/);
  assert.doesNotMatch(src, /\{technical\}/);
});

// Fix 2 of the query-plan lane: a refused clear is plain words (never "confirm=true"); the clear-then-record button waits for the clear; and a server that
// does not send calculation steps says so instead of blaming the query.
test('a refused clear on a shared server says what to tick, not the parameter name', () => {
  const said = plainClearRefusal('Storage-Engine cache clear on a shared/cloud endpoint affects all users. Pass confirm=true to proceed.');
  assert.doesNotMatch(said, /confirm=true|Storage-Engine/);
  assert.match(said, /Tick "Shared endpoint: clearing the cache affects all users"/);
});

test('clear then record records only after the clear went through, and a refusal is not a green line', () => {
  const lab = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src', 'daxlab.tsx'), 'utf8');
  const view = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src', 'query.tsx'), 'utf8');
  assert.match(lab, /if \(!\(await clearCache\('steps'\)\)\) return;\s*\n\s*speedFor\(q\)[^\n]*\n\s*await call<QueryPlanResult>\('captureQueryPlan'/);
  assert.match(view, /clearRefusal\?\.where === 'steps' && <div className="sem-text-meta xe-dot xe-dot-warn"/);
  assert.doesNotMatch(lab, /setClearMsg\(r\.cleared \? [^:]*: \(r\.error/);
});

test('on a server that does not send calculation steps the Steps card says so and shows no hint that cannot work', () => {
  const view = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src', 'query.tsx'), 'utf8');
  assert.match(view, /This server does not send calculation steps \(EVALUATEANDLOG works in Power BI Desktop only\)\./);
  assert.match(view, /\{!q\.shared && !q\.logs && !q\.logNote && q\.busy !== 'debug' && <div className="sem-text-meta">No calculation steps have been logged/);
  assert.match(view, /\{q\.logNote && !q\.shared && <TraceNote/);
});
