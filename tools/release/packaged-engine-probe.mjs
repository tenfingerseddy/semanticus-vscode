#!/usr/bin/env node
/*
Gate T163 (kane-gate-pack block 8), the SHIPPED-ARTIFACT half.

WHY THIS EXISTS. The rest of block 8 drives an in-process LocalEngine compiled from the checkout.
That certified a build nobody runs. This probe drives the engine binary extracted from a packaged
.vsix -- the Release, self-contained artifact that actually ships -- out of process, over the real
MCP stdio door, and requires it to refuse interactive authentication at agent origin.

The .vsix is PACKAGED LOCALLY, by the same Semanticus.VSCode/scripts/package.mjs that CI runs. It is
deliberately NOT downloaded from a CI run: a release gate that depends on a green CI run depends on
the very thing it exists to certify, which is circular. Packaging costs minutes; this gate runs
rarely.

HOW IT REACHES A REFUSAL WITHOUT TOUCHING THE REAL AUTH STORE, and why no production code changed to
allow it. In-process, the suite isolates the auth store with two internal test seams
(EntraToken.PersistDirOverride and EntraToken.TokenCacheNameOverride). Neither exists out of process,
and adding one would mean shaping production code for a test's convenience. It is not needed:
SEMANTICUS_ENTRA_CLIENT_ID is already a production override (EntraToken.cs:45-49) and its value is
BOTH the first component of the record-file key (RecordPath, EntraToken.cs:197) AND the credential's
ClientId (:476, :497). Pointed at an unregistered GUID:
  - no saved record file can match, so LoadRecord returns null and nothing is pinned;
  - the MSAL cache is partitioned by client id, so it holds nothing for that id;
  - so silent acquisition is impossible, and with DisableAutomaticAuthentication set for an agent
    (:478) the credential throws instead of prompting.
That is asserted, not assumed: this probe hashes every file in the real auth store before and after
and FAILS if any byte moved.

WHAT THIS PROBE CERTIFIES, exactly, and nothing wider:

  the shipped engine, run out of process from a packaged .vsix, refuses interactive
  authentication at agent origin when no saved auth record exists, and does not
  mutate the auth store

WHAT IT DOES NOT COVER. Printed on every run and copied into the evidence, because a narrow claim
that names its own limits is worth more than a broad one that hides them:
  - the RECORD-PRESENT branch: an agent call when a saved record DOES exist and the cache can serve
    silently. Reaching it needs the two internal seams, so it stays in-process only (deliberately:
    widening it by adding production seams is the wrong direction, and is its own scheduled item).
  - the STALE-CACHE branch: a record present but its refresh token aged out.
  - the packaged EXTENSION. Only the engine binary is exercised. The TypeScript extension host, the
    webview and the activation path are not run at all.
  - every limit the in-process suite already carries (background tabs, poll gaps, other desktops).

Usage:
  node tools/release/packaged-engine-probe.mjs --out <dir> [--vsix <path>] [--target <t>]
  --vsix    skip packaging and use an existing .vsix (used by the tamper self-tests)
  --no-package  fail rather than package, when no --vsix is given

Self-tests, which must each make the probe REFUSE. All exit non-zero on success:
  --prove-can-fail        demand the refusal NOT appear, which the shipped engine always produces
  --simulate-blind-store  pretend the auth-store snapshot saw nothing, which must not read as "unchanged"

  exit 0  the probe certified. NOTHING ELSE EXITS 0.
  exit 1  the probe refused to certify. In a self-test this is the CORRECT result.
  exit 2  a self-test certified anyway, so this probe can no longer be trusted to fail. P1.
*/
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractPackagedEngine } from '../../Semanticus.VSCode/scripts/verify-vsix.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');
const EXT = path.join(REPO, 'Semanticus.VSCode');

// The refusal Azure.Identity produces when a credential that MAY NOT prompt is asked for a token it
// cannot get silently. Pinning this string is what proves the SHIPPED build carried
// DisableAutomaticAuthentication; without it the same call reaches a prompt instead.
const REFUSAL = 'Interactive authentication is needed';

// Not expected to be a registered application, so no flow can complete even in principle. The
// in-process positive control uses the same id (AgentSigninObservedTests.cs:79).
const DEAD_CLIENT_ID = '00000000-dead-4bee-8000-000000000163';

const NOT_COVERED = [
  'the RECORD-PRESENT branch: an agent call when a saved auth record exists and the cache can serve silently. It needs the two internal in-process seams and is NOT exercised here.',
  'the STALE-CACHE branch: a saved record whose refresh token has aged out.',
  'the packaged EXTENSION: only the engine binary from extension/engine/ is run. The TypeScript extension host, the webview and the activation path are not exercised at all.',
  'a real network round trip: the refusal happens at credential build, so nothing reaches Entra or Fabric.',
  'any door other than MCP stdio. The RPC door shares this code but is not run here, and sharing code is not exercising it.',
];

function arg(name, fallback = undefined) {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : fallback;
}
const has = (name) => process.argv.includes(name);

const outDir = arg('--out');
if (!outDir) { console.error('--out <dir> is required'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });

const proveCanFail = has('--prove-can-fail');
const blindStore = has('--simulate-blind-store');
const selfTest = proveCanFail || blindStore;

function hostTarget() {
  const key = `${process.platform}-${process.arch}`;
  const known = ['win32-x64', 'win32-arm64', 'linux-x64', 'darwin-x64', 'darwin-arm64'];
  if (!known.includes(key)) throw new Error(`no packaging target for this host (${key})`);
  return key;
}

// Every file in the real auth store, hashed. Both halves matter: the record JSON under
// LocalApplicationData/Semanticus/auth AND the encrypted MSAL cache, which lives under a NAME in
// .IdentityService rather than under our directory.
function snapshotAuthStore() {
  if (blindStore) return {};                       // self-test: the snapshot sees nothing
  const files = {};
  const localAppData = process.env.LOCALAPPDATA
    || (process.platform === 'darwin'
      ? path.join(os.homedir(), 'Library', 'Application Support')
      : path.join(os.homedir(), '.local', 'share'));
  const roots = [path.join(localAppData, 'Semanticus', 'auth'), path.join(localAppData, '.IdentityService')];
  for (const root of roots) {
    if (!fs.existsSync(root)) continue;
    for (const name of fs.readdirSync(root)) {
      const full = path.join(root, name);
      try {
        const st = fs.statSync(full);
        if (!st.isFile()) continue;
        files[full] = createHash('sha256').update(fs.readFileSync(full)).digest('hex') + ':' + st.size;
      } catch { files[full] = 'UNREADABLE'; }
    }
  }
  return files;
}

function diffAuthStore(before, after) {
  const changed = [];
  for (const k of new Set([...Object.keys(before), ...Object.keys(after)])) {
    if (before[k] !== after[k]) {
      changed.push(`${path.basename(k)}: ${before[k] === undefined ? 'CREATED' : after[k] === undefined ? 'DELETED' : 'MODIFIED'}`);
    }
  }
  return changed;
}

// ---- drive the extracted engine over the real MCP stdio door -----------------------------------
function callEngineOverMcp(exe, engineRoot, timeoutMs = 120_000) {
  return new Promise((resolve) => {
    // Same isolation package.mjs's own execution check uses: the extracted engine must run on its
    // own payload, never on a global dotnet that happens to be installed.
    const env = { ...process.env };
    for (const k of Object.keys(env)) {
      if (k.toLowerCase() === 'path' || k.toUpperCase().startsWith('DOTNET_ROOT') || k.toUpperCase() === 'DOTNET_HOST_PATH') delete env[k];
    }
    env.PATH = engineRoot;
    env.DOTNET_ROOT = path.join(engineRoot, '__no_global_dotnet__');
    env.DOTNET_MULTILEVEL_LOOKUP = '0';
    env.SEMANTICUS_ENTRA_CLIENT_ID = DEAD_CLIENT_ID;      // the whole isolation mechanism

    const child = spawn(exe, ['--mcp'], { cwd: engineRoot, env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    let stdout = '';
    let stderr = '';
    let done = false;
    const pending = new Map();
    let nextId = 1;
    const finish = (o) => { if (!done) { done = true; try { child.kill(); } catch {} resolve({ stdout, stderr, ...o }); } };
    const timer = setTimeout(() => finish({ error: `the engine did not answer within ${timeoutMs}ms` }), timeoutMs);

    child.on('error', (e) => { clearTimeout(timer); finish({ error: `the engine could not start: ${e.message}` }); });
    child.on('exit', (code) => { if (!done) { clearTimeout(timer); finish({ error: `the engine exited (${code}) before answering` }); } });
    child.stderr.on('data', (d) => { stderr += d.toString(); });
    child.stdout.on('data', (d) => {
      stdout += d.toString();
      let i;
      while ((i = stdout.indexOf('\n')) >= 0) {
        const line = stdout.slice(0, i).trim();
        stdout = stdout.slice(i + 1);
        if (!line) continue;
        let m; try { m = JSON.parse(line); } catch { continue; }
        if (m.id && pending.has(m.id)) { const r = pending.get(m.id); pending.delete(m.id); r(m); }
      }
    });
    const call = (method, params) => new Promise((res) => {
      const id = nextId++;
      pending.set(id, res);
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });

    (async () => {
      const init = await call('initialize', {
        protocolVersion: '2024-11-05', capabilities: {},
        clientInfo: { name: 't163-packaged-engine-probe', version: '1' },
      });
      if (init.error) return finish({ error: `initialize failed: ${JSON.stringify(init.error)}` });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
      // A browser-lane op at agent origin. The MCP door declares itself an agent with no argument
      // saying so, which is the property prepare_working_copy once got wrong.
      const r = await call('tools/call', {
        name: 'fabric_git_status',
        arguments: { workspaceId: 'ws-t163-probe', authMode: 'interactive' },
      });
      const text = (r.result?.content ?? []).map((c) => c.text ?? '').join('\n') || JSON.stringify(r.error ?? r.result ?? {});
      clearTimeout(timer);
      finish({ answered: true, text });
    })().catch((e) => { clearTimeout(timer); finish({ error: `probe threw: ${e.message}` }); });
  });
}

// ---- package (or accept) the artifact ----------------------------------------------------------
const failures = [];
const target = arg('--target', hostTarget());
let vsix = arg('--vsix');

// --package-only exists so a CALLER can do the packaging outside a desktop watch. Packaging spawns
// npm, dotnet and a self-contained publish, which would flood the sentinel's watched interval with
// hundreds of unrelated processes and windows. run-block8.ps1 packages first, then probes with
// --vsix inside the watch.
if (!vsix && has('--package-only')) {
  console.log(`== packaging the .vsix locally for ${target} (the same script CI runs) ==`);
  const r = spawnSync('npm', ['run', 'package', '--', target], { cwd: EXT, stdio: 'inherit', shell: process.platform === 'win32' });
  if (r.status !== 0) { console.error(`packaging failed (exit ${r.status})`); process.exit(1); }
  const dist = path.join(EXT, 'dist');
  const built = fs.readdirSync(dist)
    .filter((f) => f.startsWith(`semanticus-${target}-`) && f.endsWith('.vsix'))
    .map((f) => ({ f, m: fs.statSync(path.join(dist, f)).mtimeMs }))
    .sort((a, b) => b.m - a.m);
  if (!built.length) { console.error(`packaging reported success but produced no .vsix for ${target}`); process.exit(1); }
  const produced = path.join(dist, built[0].f);
  fs.writeFileSync(path.join(outDir, 'packaged-vsix-path.txt'), produced);
  console.log(`PACKAGED: ${produced}`);
  process.exit(0);
}

if (!vsix) {
  if (has('--no-package')) { console.error('no --vsix given and --no-package set'); process.exit(2); }
  console.log(`== packaging the .vsix locally for ${target} (the same script CI runs) ==`);
  const r = spawnSync('npm', ['run', 'package', '--', target], { cwd: EXT, stdio: 'inherit', shell: process.platform === 'win32' });
  if (r.status !== 0) { console.error(`packaging failed (exit ${r.status})`); process.exit(1); }
  const dist = path.join(EXT, 'dist');
  const built = fs.readdirSync(dist)
    .filter((f) => f.startsWith(`semanticus-${target}-`) && f.endsWith('.vsix'))
    .map((f) => ({ f, m: fs.statSync(path.join(dist, f)).mtimeMs }))
    .sort((a, b) => b.m - a.m);
  if (!built.length) { console.error(`packaging reported success but produced no .vsix for ${target}`); process.exit(1); }
  vsix = path.join(dist, built[0].f);
}

const engineRoot = path.join(outDir, 'engine');
fs.rmSync(engineRoot, { recursive: true, force: true });
const artifact = await extractPackagedEngine(vsix, target, engineRoot);

console.log(`== the artifact under probe ==`);
console.log(`   vsix    : ${path.basename(artifact.vsixPath)}`);
console.log(`   version : ${artifact.version}`);
console.log(`   sha256  : ${artifact.vsixSha256}`);
console.log(`   engine  : ${path.basename(artifact.exe)} ${artifact.engineBytes} bytes, sha256 ${artifact.engineSha256.slice(0, 16)}...`);

if (!artifact.hostRunnable) {
  failures.push(`the ${target} engine cannot execute on this host (${process.platform}-${process.arch}), so no refusal could be read from it. A gate must not report a refusal it never observed.`);
}

let answered = false;
let answerText = '';
let storeChanged = [];
const before = snapshotAuthStore();
console.log(`== auth-store snapshot: ${Object.keys(before).length} file(s) hashed ==`);

if (artifact.hostRunnable) {
  console.log('== driving the PACKAGED engine over MCP stdio at agent origin ==');
  const r = await callEngineOverMcp(artifact.exe, engineRoot);
  const after = snapshotAuthStore();
  storeChanged = diffAuthStore(before, after);
  if (r.error) failures.push(`the packaged engine did not answer: ${r.error}`);
  else { answered = true; answerText = r.text; }
  fs.writeFileSync(path.join(outDir, 'engine-answer.txt'), `${answerText}\n---stderr---\n${r.stderr ?? ''}`);
}

// ---- adjudicate --------------------------------------------------------------------------------
const refused = answered && answerText.includes(REFUSAL);

if (answered && !refused) {
  failures.push(`the packaged engine ANSWERED WITHOUT REFUSING. Expected "${REFUSAL}"; got: ${answerText.slice(0, 300)}`);
}
// The auth store must be untouched. A gate that reached its refusal by consuming the user's real
// credentials would be worse than the human it replaces.
if (storeChanged.length) {
  failures.push(`the probe MUTATED the real auth store, which it must never do: ${storeChanged.join('; ')}`);
}
// LIVENESS OF THE SNAPSHOT ITSELF. An empty snapshot cannot prove "unchanged": zero files compared
// to zero files is trivially equal. Without this, blinding the snapshot yields a clean pass.
if (Object.keys(before).length === 0) {
  failures.push(`the auth-store snapshot saw ZERO files, so "unchanged" compares nothing and proves nothing (blindStore=${blindStore}).`);
}

if (proveCanFail && refused) {
  failures.push('--prove-can-fail: demanded that the shipped engine NOT produce its refusal. It always does, so the probe said no, which is the point of this mode.');
}

const certified = failures.length === 0;
const claim = certified
  ? 'the shipped engine, run out of process from a packaged .vsix, refuses interactive authentication at agent origin when no saved auth record exists, and does not mutate the auth store'
  : 'NOTHING IS CERTIFIED BY THIS RUN';

const result = {
  probe: 'T163 packaged-engine out-of-process refusal',
  certified,
  claim,
  mode: proveCanFail ? 'prove-can-fail' : blindStore ? 'simulate-blind-store' : 'normal',
  artifact: {
    vsix: path.basename(artifact.vsixPath), vsixSha256: artifact.vsixSha256, vsixBytes: artifact.vsixBytes,
    version: artifact.version, target: artifact.target,
    engine: path.basename(artifact.exe), engineSha256: artifact.engineSha256, engineBytes: artifact.engineBytes,
    // What THIS invocation observed, and nothing more. When run-block8.ps1 drives the probe it does
    // the packaging itself in a --package-only phase and then passes --vsix, so this is false even
    // though the artifact WAS packaged locally. The caller records that fact; the probe must not
    // claim to know how an artifact it was handed came to exist.
    packagedByThisProbe: !arg('--vsix'),
    hostRunnable: artifact.hostRunnable,
  },
  observed: {
    answered, refused, refusalString: REFUSAL,
    authStoreFilesHashed: Object.keys(before).length,
    authStoreChanged: storeChanged,
  },
  notCovered: NOT_COVERED,
  failures,
  recordedUtc: new Date().toISOString(),
};
fs.writeFileSync(path.join(outDir, 'packaged-engine-probe.json'), JSON.stringify(result, null, 2));

console.log('');
console.log('======== PACKAGED-ENGINE PROBE ========');
console.log(`certified : ${certified}`);
console.log(`claim     : ${claim}`);
console.log(`observed  : answered=${answered} refused=${refused} authStoreChanged=${storeChanged.length ? storeChanged.join('; ') : 'none'} (${Object.keys(before).length} files hashed)`);
console.log('NOT COVERED BY THIS PROBE:');
for (const n of NOT_COVERED) console.log(`  - ${n}`);
if (failures.length) { console.log('FAILURES:'); for (const f of failures) console.log(`  - ${f}`); }
console.log('=======================================');

if (selfTest) {
  if (certified) {
    console.log('');
    console.log('SELF-TEST BROKEN: the probe certified while rigged to fail. Do not trust it.');
    process.exit(2);
  }
  console.log('');
  console.log(`self-test OK: the probe refused to certify, which is the expected result.`);
  process.exit(1);
}
process.exit(certified ? 0 : 1);
