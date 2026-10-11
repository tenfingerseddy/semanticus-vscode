// m11/completeness round 4 (verifier round 3, L1): the Free guard's one gap. A quoted data method name at the top level
// of a file that does not send was read as data, so a map or a list of method names in a data file, indexed by a Free
// page (`rpc(DATA_RPC.ask)`), passed the guard, and a harmless refactor of askdata-model.ts
// (`ASK_METHOD = METHODS.ask`) took the page's own uses out of its sight. The guard now counts such a name at the top
// level of every file but features.ts (whose lists name the Pro feature askData as data). This file plants each shape
// in a scratch copy of this branch's webview and asks: the leaks fail, naming the Free page, and a data file that holds
// the names but that no Free page indexes for them passes, even when a Free page imports something else from it.
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, writeFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const ext = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(ext, '..');

function guard(change) {
  const root = mkdtempSync(join(tmpdir(), 'free-guard-r4-'));
  try {
    mkdirSync(join(root, 'Semanticus.VSCode', 'test'), { recursive: true });
    mkdirSync(join(root, 'Semanticus.Tests'), { recursive: true });
    cpSync(join(ext, 'webview', 'src'), join(root, 'Semanticus.VSCode', 'webview', 'src'), { recursive: true });
    cpSync(join(ext, 'test', 'free-sends-no-jev.test.mjs'), join(root, 'Semanticus.VSCode', 'test', 'free-sends-no-jev.test.mjs'));
    for (const f of ['JevDraftBuilderTests.cs', 'AskRouteTests.cs', 'ModelPlanCacheTests.cs'])
      cpSync(join(repo, 'Semanticus.Tests', f), join(root, 'Semanticus.Tests', f));
    change(join(root, 'Semanticus.VSCode', 'webview', 'src'));
    const r = spawnSync(process.execPath, [join(root, 'Semanticus.VSCode', 'test', 'free-sends-no-jev.test.mjs')], { encoding: 'utf8' });
    return { status: r.status, out: `${r.stdout}${r.stderr}` };
  } finally { rmSync(root, { recursive: true, force: true }); }
}
const prepend = (src, rel, text) => writeFileSync(join(src, rel), text + readFileSync(join(src, rel), 'utf8'));
const append = (src, rel, text) => writeFileSync(join(src, rel), readFileSync(join(src, rel), 'utf8') + text);
const firstError = (out) => (out.split('\n').find((l) => l.startsWith('AssertionError')) ?? '').slice(0, 260);
const failures = [];

const leaks = {
  'a map of method names in a data file, indexed by a Free page': (src) => {
    writeFileSync(join(src, 'leakmap.ts'), "export const DATA_RPC = { ask: 'askData', history: 'listDataQuestions' } as const;\n");
    prepend(src, 'lineage.tsx', "import { DATA_RPC } from './leakmap';\n");
    append(src, 'lineage.tsx', "\nvoid rpc(DATA_RPC.ask, { question: 'a Free page asks' });\n");
  },
  'a list of method names in a data file, indexed by a Free page': (src) => {
    writeFileSync(join(src, 'leaklist.ts'), "export const DATA_RPCS = ['askData', 'listDataQuestions'];\n");
    prepend(src, 'lineage.tsx', "import { DATA_RPCS } from './leaklist';\n");
    append(src, 'lineage.tsx', "\nvoid rpc(DATA_RPCS[0], { question: 'a Free page asks' });\n");
  },
  'a method const read from a map in a data file, sent by a Free page': (src) => {
    writeFileSync(join(src, 'leakconst.ts'), "export const METHODS = { ask: 'askData' } as const;\nexport const ASK_METHOD = METHODS.ask;\n");
    prepend(src, 'lineage.tsx', "import { ASK_METHOD } from './leakconst';\n");
    append(src, 'lineage.tsx', "\nvoid rpc(ASK_METHOD, { question: 'a Free page asks' });\n");
  },
  // Sol on round 4: features.ts is exempt only as data. A send from it is a send.
  'a top-level send from features.ts': (src) => {
    prepend(src, 'features.ts', "import { rpc } from './bridge';\n");
    append(src, 'features.ts', "\nvoid rpc('askData', { question: 'x' });\n");
  },
  // Sol's second pass: an aliased rpc is still a send.
  'a top-level send from features.ts through an aliased rpc': (src) => {
    prepend(src, 'features.ts', "import { rpc as send } from './bridge';\n");
    append(src, 'features.ts', "\nvoid send('askData', {});\n");
  },
};
for (const [name, change] of Object.entries(leaks)) {
  const r = guard(change);
  if (r.status === 0 || !/lineage\.tsx|features\.ts/.test(r.out))
    failures.push(`${name} is not caught: ${r.status === 0 ? 'the guard passes' : firstError(r.out)}`);
}

const harmless = {
  'a data file that holds the names and that no page imports': (src) => {
    writeFileSync(join(src, 'datanames.ts'), "export const DATA_RPC = { ask: 'askData', history: 'listDataQuestions' } as const;\n");
  },
  'a Free page that imports another export of that data file': (src) => {
    writeFileSync(join(src, 'datanames.ts'), "export const DATA_RPC = { ask: 'askData' } as const;\nexport const TITLE = 'Lineage';\n");
    prepend(src, 'lineage.tsx', "import { TITLE } from './datanames';\n");
    append(src, 'lineage.tsx', '\nconsole.log(TITLE);\n');
  },
  // Sol on round 4: an object that names the feature in one member, read by a Free page for another member only.
  'a Free page that reads another member of an object that names the method': (src) => {
    writeFileSync(join(src, 'datameta.ts'), "export const META = { feature: 'askData', title: 'Lineage' } as const;\n");
    prepend(src, 'lineage.tsx', "import { META } from './datameta';\n");
    append(src, 'lineage.tsx', '\nconsole.log(META.title);\n');
  },
};
for (const [name, change] of Object.entries(harmless)) {
  const r = guard(change);
  if (r.status !== 0) failures.push(`${name} fails the guard, which it should pass: ${firstError(r.out)}`);
}

for (const f of failures) console.log('FREE GUARD R4: ' + f);
assert.equal(failures.length, 0, `${failures.length} way(s) the Free guard misreads a data file of method names (listed above)`);
console.log('completeness-free-guard-r4: a map or a list of data method names indexed by a Free page is caught, and one no Free page indexes passes');
