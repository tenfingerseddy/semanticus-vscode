// Review > Copilot test (1.5.1, Kane's pick A, 15 Oct 2026): the page's pure half (copilot-test-model.ts), its wiring into the Studio (the route, the Review row, the
// places list, the activity follow), and the first thing a person sees before any round has run. The page against a mocked engine, with its grid and every
// control pressed, is tools/uishot/copilot-test-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, webview, req } from './helpers/webview-load.mjs';

let failures = 0;
async function check(name, fn) {
  try { await fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const M = makeLoader({})('copilot-test-model.ts');
const load = makeLoader({
  './bridge': { rpc: async () => ({ rounds: [], bankSize: 0, bankByKind: [], heldBack: 0, questions: [] }), navigateInStudio: () => {}, onActivity: () => () => {}, onDidChange: () => () => {}, onProgress: () => () => {} },
  './sendtask': { GiveToAssistant: (p) => ({ type: 'button', props: { 'data-send-key': p.sendKey, children: p.label ?? 'Give' }, key: null, ref: null, $$typeof: Symbol.for('react.transitional.element') }), clampWords: (t) => t, giveToAssistant: async () => ({ taskId: 'pt-1' }) },
  './copilot-test-publish': { PublishThenTest: () => null },
  './studio/driver-store': { useDriver: () => ({ assistantConnected: false }) },
});

const score = (right, total, couldNotCheck = 0) => ({ right, total, couldNotCheck, share: total ? right / total : null });
const round = (n, all, held, extra = {}) => ({
  id: `rd-${n}`, number: n, when: '2026-10-15T00:00:00Z', student: 'Haiku 5.5', total: 30, answered: 30, complete: true,
  practice: score(all - held, 24), heldBack: score(held, 6), all: score(all, 30), byKind: [{ kind: 'totals', right: 8, total: 10 }, { kind: 'cannot', right: 3, total: 4 }],
  missCounts: {}, newMisses: 0, changes: [], groups: [], misses: [], ...extra,
});

await check('the seven kinds and six kinds of miss have plain labels and hints, and no em dash, no "Q&A", no "Map"', () => {
  for (const k of M.KINDS) { assert.ok(M.KIND_LABEL[k]); assert.ok(M.KIND_HINT[k]); }
  for (const m of M.MISSES) { assert.ok(M.MISS_LABEL[m]); assert.ok(M.MISS_HINT[m]); assert.ok(M.MISS_SHORT[m]); }
  for (const f of ['copilot-test-model.ts', 'copilot-test.tsx', 'copilot-test.css']) {
    const s = src(f);
    assert.ok(!s.includes('—'), `${f} has an em dash`);
    assert.ok(!/Q&A|Q\s*&amp;\s*A/.test(s), `${f} says Q&A`);
    assert.ok(!/\bMap\b/.test(s.replace(/\.map\(|new Map|Map</g, '')), `${f} says Map`);
  }
});

await check('the score line has the practice and held-back share of each round, and nothing where nothing was checked', () => {
  const s = M.scoreSeries([round(1, 18, 4), round(2, 24, 6, { all: score(0, 0), heldBack: score(0, 0) })]);
  assert.deepEqual(s.map((p) => p.number), [1, 2]);
  assert.equal(s[0].all, 0.6);
  assert.equal(s[1].all, null);
  assert.equal(s[1].held, null);
});

await check('the headline and its note say how far the score moved, and whether the aim is reached', () => {
  const rounds = [round(1, 18, 4), round(2, 28, 6)];
  assert.equal(M.headline(rounds).value, 28 / 30);
  assert.match(M.headlineNote(rounds, 0.9), /Round 2, up 33 points on round 1\. Above your aim of 90%\./);
  assert.match(M.headlineNote([round(1, 18, 4)], 0.9), /Round 1\. Your aim is 90%\./);
  assert.equal(M.headlineNote([], 0.9), 'No round has run yet.');
  assert.match(M.headlineNote([round(1, 18, 4), round(2, 12, 2, { complete: false, answered: 12 })], 0.9), /Round 2 so far, 12 of 30 graded\. Round 1 ended at 60%\./);
});

await check('a round is running until its last answer is graded, and a running one shows its progress', () => {
  assert.equal(M.roundState([], null), 'none');
  assert.equal(M.roundState([round(1, 18, 4)], null), 'done');
  assert.equal(M.roundState([round(1, 18, 4, { complete: false, answered: 10 })], null), 'running');
  assert.equal(M.roundState([round(1, 18, 4)], { done: 5, total: 30 }), 'running');
  assert.equal(M.progressShare(round(1, 18, 4, { answered: 15 }), null), 0.5);
  assert.equal(M.progressShare(null, null), 0);
});

await check('a change says what it fixed once the round is done, and how many misses it is for while it runs', () => {
  const c = { fixed: 3, forMisses: [1, 2, 3, 4] };
  assert.equal(M.changeOutcome(c, true), 'Fixed 3 of 4');
  assert.equal(M.changeOutcome(c, false), 'For 4 misses');
  assert.equal(M.changeOutcome({ fixed: 0, forMisses: [1] }, false), 'For 1 miss');
  assert.equal(M.changeOutcome({ fixed: 0, forMisses: [] }, true), 'No miss named');
});

await check('the misses are grouped by kind of miss in the page order, and the heat table counts kind by kind of miss', () => {
  const miss = (n, kind, m) => ({ n, question: 'q' + n, kind, miss: m, missWords: m });
  const misses = [miss(1, 'time', 'wrong_filter'), miss(2, 'time', 'wrong_filter'), miss(3, 'words', 'wrong_field'), miss(4, 'cannot', 'should_say_no')];
  assert.deepEqual(M.missesByKind(misses).map((g) => [g.miss, g.items.length]), [['wrong_field', 1], ['wrong_filter', 2], ['should_say_no', 1]]);
  const h = M.heatCells(misses);
  assert.equal(h.cells[h.rows.indexOf('time')][M.MISSES.indexOf('wrong_filter')], 2);
  assert.equal(h.cells[h.rows.indexOf('cannot')][M.MISSES.indexOf('should_say_no')], 1);
  assert.ok(!M.heatCells([miss(1, 'time', 'wrong_filter')]).rows.includes('cannot'), 'a kind with no miss is left out when it is the cannot row');
});

await check('each kind over the rounds is a line per kind with points only where a question could be checked', () => {
  const k = M.kindSeries([round(1, 18, 4), round(2, 28, 6)]);
  assert.deepEqual(k.map((x) => x.kind), ['totals', 'cannot']);
  assert.equal(k[0].now, 0.8);
  assert.equal(k[0].series.length, 2);
});

await check('the bank rows show each kind with its size and, once a round ran, how many were right', () => {
  const rows = M.bankRows({ bankByKind: [{ kind: 'totals', count: 10 }, { kind: 'filters', count: 0 }, { kind: 'cannot', count: 4 }] }, round(1, 18, 4));
  assert.deepEqual(rows.map((r) => [r.kind, r.n, r.right]), [['totals', 10, 8], ['cannot', 4, 3]]);
  assert.deepEqual(M.bankRows(null, null), []);
});

await check('no red and no green before a result: a bank no round has run, and a question not graded yet, are neither right nor missed (UAT 17 Oct)', () => {
  const view = { bankByKind: [{ kind: 'totals', count: 10 }, { kind: 'cannot', count: 4 }] };
  const before = M.bankRows(view, null);
  assert.deepEqual(before.map((r) => [r.right, r.missed]), [[null, null], [null, null]]);
  const half = M.bankRows(view, round(1, 8, 2, { complete: false, byKind: [{ kind: 'totals', right: 4, total: 5 }] }));
  assert.deepEqual(half.map((r) => [r.kind, r.right, r.missed]), [['totals', 4, 1], ['cannot', null, null]], 'only the graded part is coloured');
  const css = src('copilot-test.css');
  const bar = /\.ct-kind-bar \{[^}]*\}/.exec(css)[0];
  assert.ok(!/--sem-bad/.test(bar), 'the bar itself is neutral; red is a graded miss only');
  assert.match(css, /\.ct-kind-miss \{[^}]*--sem-bad/);
});

await check('the page says when the assistant learns of a task, and the unpublished line says what the test would do to them', () => {
  assert.match(M.assistantNote(true), /next call/);
  assert.match(M.assistantNote(false), /No assistant is connected/);
  assert.match(M.unpublishedLine(3, 0), /^3 changes are not published yet\. The Copilot test grades the published model/);
  assert.match(M.unpublishedLine(1, 2), /^1 change is not published yet\..*2 removals stay for you to choose on Publish\.$/);
  assert.equal(M.PUBLISH_THEN_TEST, 'Publish, then test');
  for (const s of [M.assistantNote(true), M.assistantNote(false), M.unpublishedLine(2, 1)]) assert.ok(!s.includes('—'), 'no em dash');
});

await check('the Publish, then test press never publishes a removal and never publishes before it is pressed', () => {
  const s = src('copilot-test-publish.tsx');
  assert.match(s, /filter\(\(r\) => !isRemoval\(r\)\)/, 'removals are left out of the press');
  assert.match(s, /onClick=\{\(\) => void publish\(\)\}/, 'the publish is the press itself');
  assert.match(s, /Undo publish/, 'the publish carries Undo publish');
  assert.ok(s.indexOf("'applyDiff'") < s.indexOf('await onPublished()'), 'the test is asked for after the publish landed');
});

await check('after the publish lands the unpublished box is gone at once, and only what could not go stays (UAT 18 Oct N24)', () => {
  const gap = { refs: ['a', 'b', 'c'], removals: 1, destination: 'Northstar' };
  assert.equal(M.gapAfterPublish(gap, { applied: true, count: 3, failedRefs: [] }), null, 'everything went: no box');
  assert.equal(M.gapAfterPublish(gap, { applied: true, count: 2, failedRefs: ['c'] }).refs.join(), 'c', 'one could not go: the box counts that one');
  assert.equal(M.gapAfterPublish(gap, { applied: false, count: 0, failedRefs: null }), gap, 'nothing went: the box stays as it was');
  const s = src('copilot-test-publish.tsx');
  assert.ok(s.indexOf('gapAfterPublish') > -1 && s.indexOf('gapAfterPublish') < s.indexOf('await onPublished()'), 'the box is cleared before the test is awaited, not after it ends');
});

await check('the two tasks name the interview-model skill and the right calls, so an assistant that has not read it still knows the loop', () => {
  const bank = M.bankTask(), run = M.roundTask(3);
  assert.match(bank.brief, /interview-model skill/); assert.match(bank.brief, /add_interview_questions/);
  assert.equal(run.words, 'Run Copilot test round 3');
  for (const call of ['get_copilot_view', 'get_student_questions', 'run_interview_batch']) assert.match(run.brief, new RegExp(call));
});

await check('the student is named as the round says, and Haiku 5.5 when it does not', () => {
  assert.equal(M.studentLabel({ student: 'Haiku 4.5' }), 'Haiku 4.5');
  assert.equal(M.studentLabel({ student: ' ' }), 'Haiku 5.5');
  assert.equal(M.studentLabel(null), 'Haiku 5.5');
});

await check('the page is a tool of the Review row, found by the ask box, the places list and the assistant follow', () => {
  assert.match(src('route.ts'), /'copilottest'/);
  assert.match(src('route.ts'), /copilottest: 'review'/);
  assert.match(src('App.tsx'), /\{ id: 'copilottest', label: 'Copilot test' \}/);
  assert.match(src('App.tsx'), /tab === 'copilottest' \? \(\s*<CopilotTestView \/>/);
  assert.match(src('activity.tsx'), /run_interview_batch: 'copilottest'/);
  const places = JSON.parse(src('studio/places.json'));
  const place = places.places.find((p) => p.id === 'review/copilot-test');
  assert.equal(place.tool, 'copilottest');
  for (const t of ['add_interview_questions', 'get_copilot_view', 'get_student_questions', 'run_interview_batch', 'interview_rounds']) {
    assert.ok(place.tools.includes(t), `${t} is a tool of the Copilot test place`);
    assert.ok(places.toolSays[t]?.say, `${t} has a line the Studio says while the assistant uses it`);
  }
  assert.match(readFileSync(join(webview, '..', 'src', 'extension.ts'), 'utf8'), /label: 'Copilot test', key: 'copilottest'/);
});

await check('before any round has run the page says so, and offers the assistant the first job', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { CopilotTestView } = load('copilot-test.tsx');
  const html = renderToStaticMarkup(React.createElement(CopilotTestView));
  assert.match(html, /Copilot test/);
  assert.match(html, /No round has run yet/);
  assert.match(html, /There is no question bank yet/);
  assert.match(html, /Write the questions/);
  assert.ok(!/Run round 1/.test(html), 'with no bank there is nothing to run');
  assert.match(html, /A round needs questions first/);
  assert.ok(!html.includes('—'));
});

// Verifier (v-copilot-test, 15 Oct 2026). Claude Code gives a subagent every tool of the session, MCP tools included, unless its file names an allowlist
// (`tools:`) or denies them (docs: sub-agents, "mcp__<server>" or "mcp__*" in disallowedTools). The student must not reach the Semanticus tools: with them it
// can read formulas and data (so its score is not what Copilot would get) and write the model. Both copies of the file are checked.
await check('the student agent cannot reach any MCP tool, so it cannot read or write the model', () => {
  // The .claude copy is internal and left out of the public source mirror; the private repo (marked by its mirror manifest) always checks it.
  const privateRepo = existsSync(join(webview, '..', '..', 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
  for (const f of [...(privateRepo ? ['../.claude/agents/copilot-student.md'] : []), 'assistant-pack/plugins/semanticus/agents/copilot-student.md']) {
    const text = readFileSync(join(webview, '..', f), 'utf8').replace(/\r\n/g, '\n');
    const head = /^---\n([\s\S]*?)\n---\n/.exec(text);
    assert.ok(head, f + ' has no frontmatter');
    assert.match(head[1], /^model: claude-haiku-5-5$/m, f + ' is not on Haiku 5.5');
    const allow = /^tools:\s*(.*)$/m.exec(head[1]);
    const deny = (/^disallowedTools:\s*(.*)$/m.exec(head[1])?.[1] ?? '').split(',').map((s) => s.trim());
    const mcpAllowed = allow ? /mcp__/.test(allow[1]) : !deny.some((d) => d === 'mcp__*' || d === 'mcp__semanticus' || d === 'mcp__semanticus__*');
    assert.ok(!mcpAllowed, f + ': the student inherits the MCP tools (mcp__semanticus__run_dax, set_description, ...). Add mcp__* to disallowedTools, or give it a tools: allowlist.');
  }
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('copilot-test: all passed');
