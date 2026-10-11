// Retiring the Overview and the Edit History tab (Plan-heart slice 11, lanes R1 and R2; contract 11 and 15).
//
// A retired page must never become a dead link. Saved deep links, the host's commands, the model tree's model row,
// the engine's plan jumps and ask-box routes, and the keyboard all still name `modelhome`, `overview` or `history`
// somewhere, today or in a build a person has not updated yet. Every one of those words has to land on a real page:
//   modelhome, overview  -> the Plan
//   history              -> the Plan, on its History view
// This file EXECUTES route.ts's landing table rather than reading it as text, then walks every place that names a
// Studio tool or area and checks each one lands.
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');

function load(file) {
  const js = ts.transpileModule(readFileSync(file, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }, fileName: file,
  }).outputText;
  const module = { exports: {} };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(req, module, module.exports);
  return module.exports;
}
const R = load(join(webview, 'src', 'route.ts'));
assert.equal(typeof R.landingOf, 'function', 'route.ts exports landingOf, the one table every navigation goes through');
assert.ok(R.AREA_ALIASES, 'route.ts exports AREA_ALIASES, so an old area word is executed here and not read as text');

const app = src('App.tsx');
const tools = new Set(Object.keys(R.DESTINATION_OF_TOOL));
const areas = new Set(Object.keys(R.AREA_HOME));

// ---- 1. The retired pages are gone, as pages ---------------------------------------------------------------------
assert.ok(!existsSync(join(webview, 'src', 'modelhome.tsx')), 'modelhome.tsx is deleted (R1)');
assert.ok(!existsSync(join(webview, 'src', 'history.tsx')), 'history.tsx is deleted (R2)');
for (const f of readdirSync(join(webview, 'src'))) {
  if (!/\.(tsx?|mjs|css)$/.test(f)) continue;
  assert.doesNotMatch(src(f), /ModelHome/, `${f} names ModelHome; a search for it in webview/src must find nothing (R1 finish line)`);
  assert.doesNotMatch(src(f), /\bHistoryView\b[^\n]*from '\.\/history'|from '\.\/history'/, `${f} still imports the Edit History tab`);
}
assert.ok(!tools.has('modelhome') && !tools.has('history'), 'neither retired id is a tool of its own any more');
assert.ok(!areas.has('overview'), 'Overview is not an area any more: six areas, the Plan first');
assert.deepEqual([...areas], ['plan', 'explore', 'edit', 'review', 'publish', 'workflows']);

// ---- 2. Every tool id Studio has ever answered to still lands --------------------------------------------------------
// The current tools, the legacy ids kept as routes (compare, dataagent, evidence, knowledge, optimize, spec), the two
// utility pages, and the two retired here.
const everId = [...tools, 'compare', 'dataagent', 'modelhome', 'overview', 'history'];
for (const id of everId) {
  const land = R.landingOf(id);
  assert.ok(land, `the tool id "${id}" must still land somewhere`);
  assert.ok(tools.has(land.tool), `"${id}" lands on "${land.tool}", which is not a tool Studio renders`);
}
assert.deepEqual(R.landingOf('modelhome'), { tool: 'plan' }, 'the Overview lands on the Plan');
assert.deepEqual(R.landingOf('overview'), { tool: 'plan' }, 'so does its area word used as a tool');
assert.deepEqual(R.landingOf('history'), { tool: 'plan', view: 'history' }, 'Edit History lands on the Plan, on its History view');
assert.deepEqual(R.landingOf('compare'), { tool: 'deploy' }, 'Compare stays an alias for Published');
assert.equal(R.landingOf('nonsense'), null, 'an id nobody ever used is still refused, not guessed at');
assert.equal(R.landingOf(''), null);

// ---- 3. Every area word ever used still lands on one of the six --------------------------------------------------
for (const word of ['overview', 'plan', 'explore', 'edit', 'review', 'publish', 'workflows',
  'model', 'calc', 'checks', 'changes', 'understand', 'change', 'improve', 'prove', 'ship']) {
  assert.ok(areas.has(R.AREA_ALIASES[word]), `the area word "${word}" must land on one of the six, got "${R.AREA_ALIASES[word]}"`);
}
assert.equal(R.AREA_ALIASES.overview, 'plan', 'the Overview area button of an old keybinding opens the Plan');
assert.equal(R.AREA_ALIASES.model, 'plan', 'and so does "model", which opened the Overview before 22 September');

// ---- 4. The shell goes through the table, and renders what it lands on ------------------------------------------
assert.match(app, /const land = landingOf\(raw\);/, 'goTab resolves every id through landingOf');
assert.doesNotMatch(app, /VALID_TABS/, 'and keeps no second list of valid ids beside it');
assert.match(app, /AREA_ALIASES\[group\]/, 'goGroup resolves area words through the same exported table');
for (const tool of tools) {
  if (tool === 'tests' || tool === 'evidence') continue;   // rendered by the always-mounted TestsView
  assert.match(app, new RegExp(`tab === '${tool}'`), `App renders the "${tool}" tool`);
}
assert.doesNotMatch(app, /tab === 'modelhome'|tab === 'history'/, 'no branch renders a retired page');

// ---- 5. Everything outside the webview that names a tool or an area still lands -----------------------------------
const extension = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.doesNotMatch(extension, /'modelhome'/, 'the host names the Plan, not the Overview (contract 11.1)');
const hostTabs = [
  ...[...extension.matchAll(/navigateStudio\(context, '([a-z]+)'/g)].map((m) => m[1]),
  ...[...extension.matchAll(/command: 'semanticus\.studioGoTab'[^\n]*arguments: \['([a-z]+)'\]/g)].map((m) => m[1]),
  ...[...(extension.match(/const tabs: Array<vscode\.QuickPickItem & \{ key: string \}> = \[[\s\S]*?\];/) || [''])[0]
    .matchAll(/key: '([a-z]+)'/g)].map((m) => m[1]),
].filter((t) => !['ask', 'shortcuts', 'pro'].includes(t) || tools.has(t));
assert.ok(hostTabs.length > 15, `the host's tool names were found (${hostTabs.length})`);
for (const t of hostTabs) assert.ok(R.landingOf(t), `the host sends "${t}", which lands nowhere`);
const hostAreas = [...(extension.match(/const groups: Array<vscode\.QuickPickItem & \{ key: string \}> = \[[\s\S]*?\];/) || [''])[0]
  .matchAll(/key: '([a-z]+)'/g)].map((m) => m[1]);
assert.deepEqual(hostAreas, [...areas], 'Go to Studio Area offers the six areas');
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
for (const b of pkg.contributes.keybindings.filter((x) => x.command === 'semanticus.studioGoGroup')) {
  assert.ok(areas.has(R.AREA_ALIASES[b.args]), `the key ${b.key} sends "${b.args}", which lands on no area`);
}

// The engine names pages in plan jumps and ask-box routes. An engine one release behind still says `modelhome`,
// which section 2 covers; the engine in this tree must say only what lands.
const engineFiles = ['Semanticus.Engine/LocalEngine.Plan.cs', 'Semanticus.Engine/Plan/AskRouteCatalog.cs',
  'Semanticus.Engine/Plan/LocalEngine.PlanVerbs.cs', 'Semanticus.Engine/Plan/JevFamilies/JevFamily.cs',
  'Semanticus.Engine/Plan/AskRoutePoint.cs'];
for (const f of engineFiles) {
  const text = readFileSync(join(repo, f), 'utf8');
  const named = [...text.matchAll(/Tool = "([a-z]+)"/g), ...text.matchAll(/new AskRouteTool\("([a-z]+)"/g), ...text.matchAll(/Answered\("([a-z]+)"/g)]
    .map((m) => m[1]).filter((t) => t !== 'none');
  for (const t of named) assert.ok(R.landingOf(t), `${f} names the page "${t}", which lands nowhere`);
  assert.doesNotMatch(text, /Tool = "modelhome"|new AskRouteTool\("modelhome"/, `${f} still sends people to the Overview`);
}

// The keyboard.
const shortcuts = src('shortcuts.tsx');
for (const m of shortcuts.matchAll(/\{ key: '([a-z])', tab: '([a-z]+)'/g)) assert.ok(R.landingOf(m[2]), `Ctrl+Alt+${m[1]} sends "${m[2]}", which lands nowhere`);

// The ask box offers every catalogue place by a label, and a jump from it goes through goTab.
const askLabels = [...(src('askbox.tsx').match(/export const ASK_TOOL_LABEL: Record<string, string> = \{[\s\S]*?\n\};/) || [''])[0]
  .matchAll(/^\s+(\w+): '/gm)].map((m) => m[1]);
assert.ok(askLabels.length > 15, `the ask box's places were found (${askLabels.length})`);
for (const id of askLabels) assert.ok(R.landingOf(id), `the ask box offers "${id}", which lands nowhere`);
assert.ok(!askLabels.includes('modelhome'), 'the ask box no longer offers the Overview');

console.log('retired routes: every old tab id, area word and route lands');
