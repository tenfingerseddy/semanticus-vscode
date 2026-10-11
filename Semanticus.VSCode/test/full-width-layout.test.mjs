import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const styles = read('webview/src/styles.css');
const app = read('webview/src/App.tsx');
const tests = read('webview/src/tests.tsx');
const permissions = read('webview/src/permissions.tsx');
const search = read('webview/src/search.tsx');
const optimize = read('webview/src/optimize.tsx');
const docs = read('webview/src/documentation.tsx');
const mcode = read('webview/src/mcode.tsx');

assert.match(styles, /--sem-page-max:\s*1420px/, 'the shared report/review measure must remain 1420px');
// The frame is border-box and its padding is the page margin, so the measure is the content plus both margins
// (at 1440 the old max-width put the content 34 in and every card 10px off column 1).
assert.match(styles, /\.sem-centered-page\s*\{[\s\S]*?max-width:\s*calc\(var\(--sem-page-max\) \+ 2 \* var\(--sem-page-margin\)\)[\s\S]*?margin-inline:\s*auto/, 'the centered report modifier must own max width + centering');
// AI readiness left the centred measure for the full-width 12-column page (One Visual Language, 23 September 2026).
// The space rule (Kane, 27 September 2026): dashboards and lists fill the window; only a reading list keeps the measure.
assert.equal((app.match(/sem-centered-page/g) ?? []).length, 0, 'Size fills the window');
// ONE now, not two. Reports was its own page with its own centred shell until 22 September 2026; it is a
// tab inside Validation now and renders inside Validation's shell, so a second one would double the padding.
// One Visual Language (23 September 2026): Validation and Edit History sit on the page's 12-column grid with the
// 24px page margin, full width, so their cards line up with every other page's columns. A 1420px centred measure
// put their edges 10px off the grid at 1440.
assert.equal((tests.match(/sem-centered-page/g) ?? []).length, 0, 'Validation is on the 12-column page grid, and Reports rides inside it');
assert.match(tests, /<main className="sem-page">/, 'Validation is one sem-page grid');
// Edit History retired in slice 11 (lane R2); History is a view inside the Plan page and brings no page shell of its own.
assert.ok(!/className="sem-page"/.test(read('webview/src/plan-history.tsx')), 'the Plan History rides inside the Plan page');
assert.doesNotMatch(tests, /export function EvidenceBody\(\)[\s\S]{0,900}sem-centered-page/, 'the Reports tab brings no page shell of its own');
assert.match(search, /sem-centered-page" data-readable/, 'Find and replace is a reading list: it keeps the measure and says so');
for (const [name, source] of Object.entries({ Permissions: permissions, 'Change Plan': optimize }))
  assert.doesNotMatch(source, /sem-centered-page/, `${name} fills the window under the space rule`);

for (const [name, file] of Object.entries({
  'M Code': 'webview/src/mcode.tsx', Diagram: 'webview/src/diagram.tsx', Lineage: 'webview/src/lineage.tsx',
  Data: 'webview/src/datapreview.tsx', 'DAX Lab': 'webview/src/daxlab.tsx', 'Model Spec': 'webview/src/spec.tsx',
  Workflows: 'webview/src/workflows.tsx', 'Data Agent': 'webview/src/dataagent.tsx', Docs: 'webview/src/documentation.tsx',
  'Model quality': 'webview/src/bpa.tsx',
})) assert.doesNotMatch(read(file), /sem-centered-page/, `${name} must remain a full-width workbench/canvas`);

assert.doesNotMatch(mcode, /pqLane|setLane\(|>Incremental Refresh<|>M query</, 'M Code must not split query and refresh into separate lanes');
assert.match(mcode, /<MLane[\s\S]*aria-label="Incremental refresh settings"/, 'the query and refresh inspector must render in one workspace');

console.log('job-shaped Studio layout tests passed');
