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

assert.match(bpa, /setWaivedOpen\(true\)/, 'D-079: the BPA "N accepted" count must open the accepted list');
assert.match(bpa, /<button type="button"[\s\S]*?\{card\.waivedCount\} accepted<\/button>/, 'D-079: "N accepted" is a real control, not a coloured span');

assert.match(app, /c\.waived \? ` · \$\{c\.waived\} accepted`/, 'D-130: a category row must count waived findings, not drop them');
assert.match(app, /card\.waivedCount \? ` · \$\{card\.waivedCount\} accepted`/, 'D-130: the readiness header counts waived findings');
assert.match(app, /setWaivedOpen\(true\)/, 'D-079: the readiness "N accepted" count must open the accepted list too');

assert.match(rules, /This id is already used/, 'D-076: saving a new custom rule with an existing id must refuse out loud');
assert.match(rules, /!form\.editingId && rules\.some/, 'D-076: editing the same rule is still allowed; only a new duplicate is refused');

console.log('learning and waivers UI contract tests passed');
