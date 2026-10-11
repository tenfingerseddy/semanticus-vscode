// The progress page is a view of TASKS.md (CLAUDE.md: "TASKS.md stays the canonical status home"). Its summary
// is hand-written, so it can run ahead of the rows: on 2026-09-15 the summary said 1.2.0 was merged and tagged
// while five listed rows still read IN PROGRESS and the rendered page said 52 percent (Codex, PR 377, F-160).
// This pins the two together: a summary that declares a release needs every listed row closed.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const STATES = ['DONE', 'IN PROGRESS', 'NOT STARTED', 'BLOCKED', 'ON HOLD'];
const RELEASED = /\b(is released|merged to main and tagged|is merged, tagged)\b/i;

export function stateOf(tasksText, id) {
  const lines = tasksText.split(/\r?\n/);
  const index = lines.findIndex((line) => line.startsWith('- [' + id + '] '));
  if (index < 0) return 'Missing';
  const detail = (lines[index + 1] || '').trim();
  return STATES.find((key) => detail.startsWith(key + '.') || detail.startsWith(key + ',')) || 'Unknown';
}

export function releasedSummaryNeedsClosedRows(view, tasksText) {
  if (!RELEASED.test(view.summary || '')) return [];
  return view.tasks.map((t) => [t.id, stateOf(tasksText, t.id)]).filter(([, state]) => state !== 'DONE');
}

// Positive control: the rule must catch the 15 Sep shape (a released summary over an open row).
const control = releasedSummaryNeedsClosedRows(
  { summary: 'Semanticus 1.2.0 is released.', tasks: [{ id: 'T9' }] },
  '- [T9] A row.\n        IN PROGRESS. Owner: someone.\n        detail\n');
assert.deepEqual(control, [['T9', 'IN PROGRESS']], 'the control must fail the rule, or the rule is inert');

// The public mirror (tools/release/mirror-public.mjs) drops TASKS.md and docs/redesign/, and it excludes
// tools/release/, so that manifest's absence means the inputs are absent too: the check is private-only and
// says so instead of throwing ENOENT (Codex, PR 379).
const viewPath = resolve(root, 'docs/redesign/progress-view.json');
const tasksPath = resolve(root, 'TASKS.md');
if (!existsSync(resolve(root, 'tools/release/mirror-manifest.json')) || !existsSync(viewPath) || !existsSync(tasksPath)) {
  console.log('progress view: private inputs are not in this tree (the public mirror), so only the control ran');
  process.exit(0);
}
const view = JSON.parse(readFileSync(viewPath, 'utf8'));
const tasks = readFileSync(tasksPath, 'utf8');
const open = releasedSummaryNeedsClosedRows(view, tasks);
assert.deepEqual(open, [], 'a released summary needs every listed row closed; open: ' + JSON.stringify(open));
for (const t of view.tasks) assert.notEqual(stateOf(tasks, t.id), 'Missing', t.id + ' is listed on the progress page but has no row');
console.log('progress view: the summary and the rows agree');
