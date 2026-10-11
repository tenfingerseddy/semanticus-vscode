// verify: m11/completeness round 1 (27 Sep), failing on purpose at d0452195.
//
// test/free-sends-no-jev.test.mjs (the lane's H1 guard) is run here against copies of the webview source with one
// change each, the way it will meet real code:
//   A. A quoted 'askData' that is not a call. m11/askdata names the Pro feature by that wire id (features.ts:16-21, at
//      fa4fb162 and at f2f9996a), and the guard reads any quoted method name as a caller, so it fails the day that lane
//      merges. Its proof was two files overlaid (App.tsx and the page), never the lane's whole webview.
//   B. A file that imports a pure helper from the page's model. m11/askdata's tests-authoring.tsx imports checkTitle
//      from askdata-model (f2f9996a), and the guard refuses any import of that module.
//   C. A new caller of an engine-gated method. The guard's header says "A new caller fails here until someone says what
//      holds Free out of it"; for composeTask, editDraft, describeWorkflowSteps, routeAsk and getModelPlan it only checks
//      that the named C# test exists, so a new caller passes unseen.
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, writeFileSync, appendFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const ext = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(ext, '..');

function mirror(change) {
  const root = mkdtempSync(join(tmpdir(), 'free-guard-'));
  mkdirSync(join(root, 'Semanticus.VSCode', 'test'), { recursive: true });
  mkdirSync(join(root, 'Semanticus.Tests'), { recursive: true });
  cpSync(join(ext, 'webview', 'src'), join(root, 'Semanticus.VSCode', 'webview', 'src'), { recursive: true });
  cpSync(join(ext, 'test', 'free-sends-no-jev.test.mjs'), join(root, 'Semanticus.VSCode', 'test', 'free-sends-no-jev.test.mjs'));
  for (const f of ['JevDraftBuilderTests.cs', 'AskRouteTests.cs', 'ModelPlanCacheTests.cs'])
    cpSync(join(repo, 'Semanticus.Tests', f), join(root, 'Semanticus.Tests', f));
  change(join(root, 'Semanticus.VSCode', 'webview', 'src'));
  const r = spawnSync(process.execPath, [join(root, 'Semanticus.VSCode', 'test', 'free-sends-no-jev.test.mjs')], { encoding: 'utf8' });
  rmSync(root, { recursive: true, force: true });
  return { status: r.status, out: `${r.stdout}${r.stderr}` };
}
const firstError = (out) => (out.split('\n').find((l) => l.startsWith('AssertionError')) ?? '').slice(0, 200);

const failures = [];

// A. A feature id is not a call.
const a = mirror((src) => appendFileSync(join(src, 'features.ts'),
  "\n// as m11/askdata writes it: the Pro feature's wire id, never an RPC\nexport const ASK_FEATURE_ID = 'askData';\n"));
if (a.status !== 0) failures.push(`A. a Pro feature id reads as a caller: ${firstError(a.out)}`);

// B. A pure helper from the page's model sends nothing.
const b = mirror((src) => {
  writeFileSync(join(src, 'askdata-model.ts'), "export function checkTitle(): string { return ''; }\n");
  const p = join(src, 'tests-authoring.tsx');
  writeFileSync(p, "import { checkTitle } from './askdata-model';\n" + readFileSync(p, 'utf8'));
});
if (b.status !== 0) failures.push(`B. importing a pure helper from askdata-model fails the guard: ${firstError(b.out)}`);

// C. A new caller of an engine-gated method must fail, as the guard's header says it does.
const c = mirror((src) => appendFileSync(join(src, 'lineage.tsx'), "\nvoid rpc('composeTask', 'a caller nobody listed');\n"));
if (c.status === 0) failures.push('C. a new composeTask caller in lineage.tsx passes the guard, whose header says a new caller fails');

for (const f of failures) console.log('FREE GUARD: ' + f);
assert.equal(failures.length, 0, `${failures.length} way(s) the Free guard misreads real code (listed above)`);
console.log('completeness-free-guard: the guard ignores a feature id and a pure helper, and catches a new caller');
