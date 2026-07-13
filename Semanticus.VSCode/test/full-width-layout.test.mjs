import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const styles = read('webview/src/styles.css');
const app = read('webview/src/App.tsx');
const tests = read('webview/src/tests.tsx');
const bpa = read('webview/src/bpa.tsx');
const permissions = read('webview/src/permissions.tsx');
const history = read('webview/src/history.tsx');
const search = read('webview/src/search.tsx');
const optimize = read('webview/src/optimize.tsx');
const docs = read('webview/src/documentation.tsx');
const mcode = read('webview/src/mcode.tsx');

assert.match(styles, /--sem-page-max:\s*1420px/, 'the shared report/review measure must remain 1420px');
assert.match(styles, /\.sem-centered-page\s*\{[\s\S]*?max-width:\s*var\(--sem-page-max\)[\s\S]*?margin-inline:\s*auto/, 'the centered report modifier must own max width + centering');
assert.equal((app.match(/sem-centered-page/g) ?? []).length, 2, 'AI Readiness and Statistics must be centered');
assert.equal((tests.match(/sem-centered-page/g) ?? []).length, 2, 'Tests and Evidence must be centered');
for (const [name, source] of Object.entries({ BPA: bpa, Permissions: permissions, 'Edit History': history, Search: search, 'Change Plan': optimize, Docs: docs }))
  assert.match(source, /sem-centered-page/, `${name} must use the centered report/review measure`);

for (const [name, file] of Object.entries({
  'M Code': 'webview/src/mcode.tsx', Diagram: 'webview/src/diagram.tsx', Lineage: 'webview/src/lineage.tsx',
  Data: 'webview/src/datapreview.tsx', 'DAX Lab': 'webview/src/daxlab.tsx', 'Model Spec': 'webview/src/spec.tsx',
  Workflows: 'webview/src/workflows.tsx', 'Data Agent': 'webview/src/dataagent.tsx',
})) assert.doesNotMatch(read(file), /sem-centered-page/, `${name} must remain a full-width workbench/canvas`);

assert.doesNotMatch(mcode, /pqLane|setLane\(|>Incremental Refresh<|>M query</, 'M Code must not split query and refresh into separate lanes');
assert.match(mcode, /<MLane[\s\S]*aria-label="Incremental refresh settings"/, 'the query and refresh inspector must render in one workspace');

console.log('job-shaped Studio layout tests passed');
