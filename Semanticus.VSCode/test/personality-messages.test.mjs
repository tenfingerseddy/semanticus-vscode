import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../webview/src/personality/messages.ts', import.meta.url), 'utf8');
const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.ESNext } }).outputText;
const { VoiceSession, MESSAGES, resultEvent, updateVoiceFacts, voicePublishStage, JOKE_INTERVAL_MS, LONG_WAIT_MS } = await import(`data:text/javascript;base64,${Buffer.from(js).toString('base64')}`);
const success = { id: 'grade-a' };

test('On shows the approved line; Quiet shows the plain sentence', () => {
  assert.equal(new VoiceSession().choose(success, 'on', 0).text, MESSAGES['grade-a'].line);
  const quiet = new VoiceSession().choose(success, 'quiet', 0);
  assert.equal(quiet.text, 'Grade A.');
  assert.equal(quiet.joke, false);
});
test('errors, warnings and unsuccessful outcomes never have a joke', () => {
  for (const bad of [{ error: 'Failed' }, { ok: false }, { warning: 'Incomplete' }]) {
    assert.equal(new VoiceSession().choose({ ...success, ...bad, plain: 'Could not check the model.' }, 'on', 0).joke, false);
  }
});
test('delete and production publishes never have a joke', () => {
  for (const context of [{ action: 'delete' }, { action: 'publish', stage: 'prod' }, { action: 'publish', stage: 'Production' }, { action: 'publish' }]) {
    assert.equal(new VoiceSession().choose({ ...success, ...context }, 'on', 0).joke, false);
  }
});
test('the first-publish and waiting lines require an explicit Test destination', () => {
  for (const id of ['first-publish', 'test-wait']) {
    for (const stage of [undefined, 'dev', 'uat', 'prod', 'Production', 'Contest']) {
      assert.equal(new VoiceSession().choose({ id, stage, elapsedMs: 9000 }, 'on', 0).joke, false);
    }
    assert.equal(new VoiceSession().choose({ id, action: 'publish', stage: 'Test', elapsedMs: 9000 }, 'on', 0).joke, true);
  }
});
test('the real registry UAT label maps to Test, while other labels stay plain', () => {
  assert.equal(voicePublishStage('uat'), 'test');
  for (const label of ['prod', 'dev', 'local', undefined, null, 'Contest']) assert.equal(voicePublishStage(label), undefined);
});
test('a joke never repeats in a session, even after the cooldown', () => {
  const s = new VoiceSession();
  assert.equal(s.choose(success, 'on', 0).joke, true);
  assert.equal(s.choose(success, 'on', JOKE_INTERVAL_MS * 2).joke, false);
  assert.equal(s.choose(success, 'on', JOKE_INTERVAL_MS * 20).text, 'Grade A.');
});
test('reloads keep the used jokes and their cooldown', () => {
  const original = new VoiceSession();
  original.choose(success, 'on', 1000);
  const restored = new VoiceSession(original.memory());
  assert.equal(restored.choose(success, 'on', JOKE_INTERVAL_MS * 2).joke, false);
  assert.equal(restored.choose({ id: 'undo' }, 'on', 1001).joke, false);
});
test('different jokes stay at least three minutes apart', () => {
  const s = new VoiceSession();
  s.choose(success, 'on', 0);
  assert.equal(s.choose({ id: 'undo' }, 'on', JOKE_INTERVAL_MS - 1).joke, false);
  assert.equal(s.choose({ id: 'undo' }, 'on', JOKE_INTERVAL_MS).joke, true);
});
test('Quiet and unsafe attempts do not spend the joke', () => {
  const s = new VoiceSession();
  s.choose(success, 'quiet', 0);
  s.choose({ ...success, error: 'Failed' }, 'on', 1);
  assert.equal(s.choose(success, 'on', 2).joke, true);
});
test('waiting must pass eight seconds', () => {
  const event = { id: 'refresh-wait', facts: { seconds: 8 } };
  for (const elapsedMs of [undefined, 0, LONG_WAIT_MS]) {
    assert.equal(new VoiceSession().choose({ ...event, elapsedMs }, 'on', 0).joke, false);
  }
  assert.equal(new VoiceSession().choose({ ...event, elapsedMs: LONG_WAIT_MS + 1 }, 'on', 0).joke, true);
});
test('counts are facts, never the sample values', () => {
  const s = new VoiceSession();
  assert.equal(s.choose({ id: 'huge-model' }, 'on', 0), null);
  assert.equal(s.choose({ id: 'huge-model', facts: { tables: 731 } }, 'on', 0).text, '731 tables. I have read them all, so you do not have to.');
  assert.equal(new VoiceSession().choose({ id: 'synonym-fields', facts: { count: 885 } }, 'quiet', 0).text, 'Synonyms added to 885 fields.');
  assert.equal(new VoiceSession().choose({ id: 'synonyms', facts: { count: 312 } }, 'on', 0).text, 'Added 312 synonyms.');
  assert.equal(new VoiceSession().choose({ id: 'synonyms', facts: { count: 312, equivalence: 'revenue,sales,turnover' } }, 'on', 0).joke, true);
});
test('the first Test publish is remembered without counting other destinations', () => {
  const s = new VoiceSession();
  assert.equal(s.firstTestPublish('prod'), false);
  assert.equal(s.firstTestPublish('Test'), true);
  assert.equal(s.firstTestPublish('test'), false);
});
test('a growing receipt keeps its current count without spending another joke', () => {
  const s = new VoiceSession();
  const start = s.choose({ id: 'synonym-fields', facts: { count: 10 } }, 'on', 0);
  const final = updateVoiceFacts(start, { count: 885 }, 'Your assistant added synonyms to 885 fields.');
  assert.equal(final.text, 'Synonyms added to 885 fields. Copilot has more words to work with.');
  assert.equal(final.plain, 'Your assistant added synonyms to 885 fields.');
  assert.equal(s.choose({ id: 'synonym-fields', facts: { count: 900 } }, 'on', JOKE_INTERVAL_MS * 2).joke, false);
});
test('plan completion requires an observed transition, with every task done', () => {
  const s = new VoiceSession();
  assert.equal(s.observePlan('one', [{ status: 'done' }]), null);
  assert.equal(s.observePlan('one', [{ status: 'ready' }]), null);
  assert.deepEqual(s.observePlan('one', [{ status: 'done' }]), { id: 'plan-finished' });
  assert.equal(s.observePlan('one', [{ status: 'done' }]), null);
  s.observePlan('two', [{ status: 'ready' }]);
  assert.equal(s.observePlan('two', []), null);
  s.observePlan('two', [{ status: 'ready' }]);
  assert.equal(s.observePlan('two', [{ status: 'skipped' }]), null);
});
test('flagged jokes are absent, including Friday, while the empty plan stays plain', () => {
  const lines = Object.values(MESSAGES).map((m) => m.line).join(' ');
  for (const phrase of ['or it is Monday', 'big is my favourite', 'called Column1', 'Five undos in a row', 'after midnight', 'Brave, and it worked']) assert.equal(lines.includes(phrase), false);
  assert.equal(new VoiceSession().choose({ id: 'empty-plan' }, 'on', 0).text, 'Nothing on the plan.');
  assert.equal(lines.includes(String.fromCharCode(0x2014)), false);
});
test('complete scan and bulk results produce the right real moment', () => {
  assert.deepEqual(resultEvent('aiReadinessScan', { grade: 'A', gatedBy: [] }), { id: 'grade-a' });
  assert.deepEqual(resultEvent('bpaFixAll', { applied: 9, scorecard: { violationCount: 0, ruleCount: 30, ruleErrors: [] } }), { id: 'zero-findings' });
  assert.deepEqual(resultEvent('writeWords', { applied: 885, rejected: [], words: { verb: 'added synonyms to', noun: 'fields', count: 885 } }), { id: 'synonym-fields', facts: { count: 885 } });
  for (const bad of [{ error: 'Failure' }, { warning: 'Some refused' }, { rejected: [{ index: 2 }] }, { caveat: 'Incomplete' }, { ruleErrors: ['Bad rule'] }]) {
    assert.equal(resultEvent('aiReadinessScan', { grade: 'A', ...bad }), null);
  }
  assert.equal(resultEvent('bpaScan', { violationCount: 0, ruleCount: 0 }), null);
  assert.equal(resultEvent('bpaFixAll', { scorecard: { violationCount: 0, ruleCount: 30 }, unfixed: [{}] }), null);
  assert.equal(resultEvent('aiReadinessScan', { grade: 'A', gatedBy: ['Missing evidence'] }), null);
  assert.equal(resultEvent('deleteMeasure', { applied: true }), null);
});
