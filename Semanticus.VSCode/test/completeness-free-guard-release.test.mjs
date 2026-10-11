// verify: m11/completeness round 2 (28 Sep), failing on purpose at 03684796.
//
// This lane merges last, so its Free guard (test/free-sends-no-jev.test.mjs) is judged here against the release as it
// will ship, not against this branch alone. m11/queries-redesign (df69ac19) gives Queries a Pro question box built on
// Ask your data: daxlab.tsx reads the feature grant with useFeature('askData') and mounts <AskField> only when it is
// 'granted' (daxlab.tsx:243, :320, :560), and queriesask.tsx sends the page model's ASK_METHOD through rpc
// (queriesask.tsx:1-40; the hook asks nothing on its own). Run over that lane's own webview, the guard fails on the
// feature id ("daxlab.tsx calls an Ask your data method; only the page (askdata.tsx and its model) may"), and with that
// set aside it fails next on queriesask.tsx, a Pro-gated caller outside the page. npm test is a required gate, so it
// goes red the day this lane merges onto that code. This file lays the same shapes over this branch's webview (the
// page and its model as m11/askdata 726b0666 writes them, the Queries box as df69ac19 writes it) and asks for two
// things: the guard passes on them, and a planted askData call from a Free page still fails it.
//
// At the M11 join (30 Sep) those lanes are in the tree, so both asks run on the real webview (the overlays are gone).
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, appendFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const ext = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(ext, '..');
const failures = [];

function guard(change) {
  const root = mkdtempSync(join(tmpdir(), 'free-guard-release-'));
  mkdirSync(join(root, 'Semanticus.VSCode', 'test'), { recursive: true });
  mkdirSync(join(root, 'Semanticus.Tests'), { recursive: true });
  cpSync(join(ext, 'webview', 'src'), join(root, 'Semanticus.VSCode', 'webview', 'src'), { recursive: true });
  cpSync(join(ext, 'test', 'free-sends-no-jev.test.mjs'), join(root, 'Semanticus.VSCode', 'test', 'free-sends-no-jev.test.mjs'));
  for (const f of ['JevDraftBuilderTests.cs', 'AskRouteTests.cs', 'ModelPlanCacheTests.cs'])
    cpSync(join(repo, 'Semanticus.Tests', f), join(root, 'Semanticus.Tests', f));
  const src = join(root, 'Semanticus.VSCode', 'webview', 'src');
  // The M11 join: m11/askdata's page (askdata.tsx, askdata-model.ts) and m11/queries-redesign's Pro question box (queriesask.tsx,
  // daxlab.tsx's useFeature('askData') gate) are in this tree now, so the guard is judged on the release's own webview as copied
  // above. The overlays this file used to write (a stand-in page and a stand-in box, spliced into App.tsx 200 characters after
  // its Free gate) are gone: on the joined App.tsx that splice landed inside the gate's ternary chain, broke it, and made the real
  // gated mount of <AskDataView> read as ungated, which is the stand-in's fault and not the release's.
  if (change) change(src);
  const r = spawnSync(process.execPath, [join(root, 'Semanticus.VSCode', 'test', 'free-sends-no-jev.test.mjs')], { encoding: 'utf8' });
  rmSync(root, { recursive: true, force: true });
  return { status: r.status, out: `${r.stdout}${r.stderr}` };
}
const firstError = (out) => (out.split('\n').find((l) => l.startsWith('AssertionError')) ?? '').slice(0, 220);

const queries = guard();
if (queries.status !== 0)
  failures.push(`the Free guard fails on the release's Pro-gated Queries question box: ${firstError(queries.out)}`);
else {
  // Only once the release's own shapes pass can a planted call show whether the guard still catches a Free caller.
  const planted = guard((src) => appendFileSync(join(src, 'lineage.tsx'), "\nvoid rpc('askData', { question: 'a Free page asks' });\n"));
  if (planted.status === 0 || !/lineage\.tsx/.test(planted.out))
    failures.push(`a planted askData call from lineage.tsx, a Free page, is not caught: ${planted.status === 0 ? 'the guard passes' : firstError(planted.out)}`);
}

for (const f of failures) console.log('FREE GUARD ON THE RELEASE: ' + f);
assert.equal(failures.length, 0, `${failures.length} way(s) the Free guard misreads the release's webview (listed above)`);
console.log('completeness-free-guard-release: the guard passes the Pro-gated Queries box and still catches a Free caller');
