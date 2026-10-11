import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const hooks = read('webview/src/hooks.ts');
const findings = read('webview/src/findings.tsx');
const bpa = read('webview/src/bpa.tsx');
const app = read('webview/src/App.tsx');
const rules = read('webview/src/rulesauthor.tsx');

assert.match(hooks, /export function dropDoneFixState/, 'D-078: fixed ticks must be droppable as a pure step');
assert.match(hooks, /onDidChange\(\(\) => setState\(\(s\) => dropDoneFixState\(s\)\)\)/, 'D-078: a model change (undo, rescan) must clear the fixed tick');
assert.match(hooks, /if \(v === 'done'\)/, 'D-078: only the fixed tick is dropped, not an in-flight fix');

assert.match(findings, /id="waived-findings"/, 'D-079: the accepted list must have a target the header can open');
assert.match(findings, /open\?: boolean; onOpenChange\?: \(open: boolean\) => void/, 'D-079: the accepted list is driven by the header count');

// Minimal pass (8 Oct): the "N accepted" count left the Model quality header. The accepted list is the last fold row of the findings card (its
// own row is the control, and carries the count), so it is still one press away and the score is never silently inflated.
assert.match(bpa, /<WaivedList embedded rows=\{waivedRows\}/, 'D-079: the BPA accepted list is the last fold row of the findings card');
assert.doesNotMatch(bpa, /\{card\.waivedCount\} accepted<\/button>/, 'the header no longer repeats the count');
assert.match(findings, /<Row onClick=\{\(\) => setOpen\(!isOpen\)\}[\s\S]{0,260}\{rows\.length\}<\/span>/, 'D-079: the accepted row opens the list and says how many');

// 8 Oct 2026 (R4): "N accepted" shows ONCE on the readiness page, as the Accepted row's own count, instead of four
// times (header, category row, Findings title, Accepted row). The accepted findings are still listed, not dropped.
assert.doesNotMatch(app, /c\.waived \? ` · \$\{c\.waived\} accepted`/, 'R4: a category row does not repeat the accepted count');
assert.doesNotMatch(app, /card\.waivedCount \? ` · \$\{card\.waivedCount\} accepted`/, 'R4: neither does the readiness header');
assert.match(app, /<WaivedList rows=\{waivedRows\}/, 'D-130: the accepted findings are still listed on the page, with their count');
assert.doesNotMatch(app, /setWaivedOpen/, 'R9: the Findings title no longer carries an accepted link; the Accepted row opens itself');

assert.match(rules, /This id is already used/, 'D-076: saving a new custom rule with an existing id must refuse out loud');
assert.match(rules, /!form\.editingId && rules\.some/, 'D-076: editing the same rule is still allowed; only a new duplicate is refused');

console.log('learning and waivers UI contract tests passed');
