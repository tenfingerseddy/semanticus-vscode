// The 1.5 copy pass (hero19 D12, D14, D15, D17 to D21): small words and layout fixes, each held by a check.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, webview } from './helpers/webview-load.mjs';

const load = makeLoader();
const C = load('plan-calm-model.ts');
const P = load('plan-model.ts');
const B = load('bpa-detail.ts');
const A = load('askdata-model.ts');
const src = (name) => readFileSync(join(webview, 'src', name), 'utf8');

let failures = 0;
function check(name, fn) {
  try { fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}

check('D12: the first AI ready read is tried again, so a reopened model shows its number and not a letter alone', () => {
  const head = src('plan-head.tsx');
  assert.match(head, /export const GRADE_READS = 4;/u);
  assert.match(head, /for \(let tries = 0; tries < GRADE_READS; tries\+\+\)/u);
  assert.match(head, /if \(g\) \{ setGrade\(g\); return; \}/u, 'a read that holds a grade stops the retries');
});

check('D14: Ask puts the answer before "Asked before" in the page, so a narrow window shows the answer first', () => {
  const ask = src('askdata.tsx');
  assert.ok(ask.indexOf('aria-label="Your question"') < ask.indexOf('aria-label="Asked before"'), 'the answer card comes first in the document');
  assert.match(ask, /sem-main-right/u);
  assert.match(ask, /sem-rail-left/u);
  const css = src('styles.css');
  assert.match(css, /\.sem-page > \.sem-rail-left \{ grid-column: 1 \/ span 3; grid-row: 1; \}/u, 'two columns keep the rail on the left');
  assert.match(css, /@media \(max-width: 900px\) \{\s*\.sem-page > \.sem-rail-left, \.sem-page > \.sem-main-right \{ grid-row: auto; \}/u, 'one column stacks in document order');
});

check('D15: a bar chart over many rows draws the top few and says so on one line', () => {
  const rows = Array.from({ length: 500 }, (_, i) => [`P${i}`, 'Jan', i]);
  const c = A.crowdedChart(rows, 2, 'Net Sales');
  assert.equal(c.rows.length, A.CHART_TOP_ROWS);
  assert.deepEqual(c.rows[0], ['P499', 'Jan', 499], 'largest first');
  assert.equal(c.line, '500 rows are too many to read as bars, so the chart shows the top 20 by Net Sales. The table has every row.');
  assert.equal(A.crowdedChart(rows.slice(0, 40), 2, 'Net Sales'), null, 'a readable chart is drawn whole');
  assert.match(src('askdata.tsx'), /data-testid="askdata-chart-top"/u);
  assert.doesNotMatch(src('askdata.tsx') + src('askdata-model.ts'), /—/u);
});

check('D17: the slot says "your assistant" in lower case mid-sentence and a real name as it is', () => {
  const slot = (name) => C.slotOf({ press: null, work: null, receipt: null, hasNext: false, driver: { connected: true, mode: 'claudeDrives', wheel: true, assistantWorking: false, name } });
  assert.equal(slot('Your assistant').text, 'You have the wheel. Your next message to your assistant gives it back.');
  assert.equal(slot('Claude Code').text, 'You have the wheel. Your next message to Claude Code gives it back.');
});

check('D17: the slot text is in the DOM once and has the full line at a middle width', () => {
  const head = src('plan-head.tsx');
  assert.doesNotMatch(head, /slot\.kind === 'done' \|\| slot\.kind === 'said' \? slot\.text : ''/u, 'the hidden status no longer repeats the visible words');
  assert.match(head, /<span className="pc-done" role="status"/u);
  assert.match(head, /<span className="pc-said" role="status"/u);
  const css = src('plan-calm.css');
  const mid = css.slice(css.indexOf('@media (max-width: 1280px) and (min-width: 721px)'), css.indexOf('/* ---- a narrow Studio'));
  assert.match(mid, /\.pc-slot-host \{ order: 10; flex: 1 0 100%;/u, 'the slot takes a whole line at the middle width');
});

check('D18: a Model quality category says Info findings do not count', () => {
  assert.equal(B.categoryDetail([{ severity: 2 }, { severity: 1 }], 82), '1 of 82, 1 Info not counted');
  assert.equal(B.categoryDetail(Array.from({ length: 44 }, () => ({ severity: 1 })), 82), '44 Info, not counted');
  assert.equal(B.categoryDetail([{ severity: 3 }, { severity: 2 }], 82), '2 of 82');
  assert.equal(B.categoryDetail([], 82), '0 of 82');
  assert.match(src('bpa.tsx'), /categoryDetail\(openIn\(c\.category\), c\.objectsChecked\)/u);
});

check('D19: a change set that ran is "applied here before", never "approved" by nobody; Jev chose counts settings', () => {
  assert.equal(P.receiptLine({ lastAppliedCount: 1, lastAppliedAt: null }), '1 change was applied here before.');
  assert.equal(P.receiptLine({ lastAppliedCount: 3 }), '3 changes were applied here before.');
  assert.equal(C.doneWords(1, 3000, 10), 'Done. 1 task in 3 s. Jev chose 10 settings.');
  assert.equal(C.doneWords(1, 3000, 1), 'Done. 1 task in 3 s. Jev chose 1 setting.');
  assert.doesNotMatch(src('plan-model.ts'), /approved here before/u);
});

check('D20: the Publish header names where the assistant is publishing and does not say there is no destination', () => {
  const d = src('deploy.tsx');
  assert.match(d, /assistantPublish\(e\)/u);
  assert.match(d, /assistantTo \? <span data-testid="deploy-assistant-to">To <b>\{assistantTo\}<\/b><\/span>\s*: <span className="sem-text-meta">No publish destination yet\.<\/span>/u);
});

check('D21: a Move up that cannot move says why', () => {
  const mk = (id, order) => ({ id, section: 'Structure', title: id, sub: '', who: 'auto', status: 'proposed', confidence: 1, why: [], doer: 'assistant', stateWord: 'ready', order });
  const items = [mk('pt-a', 1), mk('pt-b', 2)];
  const up = (r) => P.rowMenuActions(r, { items }).find((a) => a.id === 'up');
  assert.deepEqual([up(items[0]).disabled, up(items[0]).why], [true, 'Already first']);
  assert.deepEqual([up(items[1]).disabled, up(items[1]).why], [false, undefined]);
});

check('no em dash in what these fixes say', () => {
  for (const f of ['bpa-detail.ts']) assert.doesNotMatch(src(f), /—/u);
  assert.doesNotMatch(C.doneWords(1, 3000, 4) + P.receiptLine({ lastAppliedCount: 2 }) + P.UP_FIRST_WHY, /—/u);
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
