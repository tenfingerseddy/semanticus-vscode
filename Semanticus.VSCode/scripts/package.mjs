#!/usr/bin/env node
// Per-platform .vsix packager for the Semanticus extension.
//
// Kane's decision: platform-SPECIFIC vsixes (vsce --target), NOT one fat cross-platform vsix. Each vsix bundles a
// self-contained Semanticus.Engine publish for exactly one RID under <ext>/engine, so the installed extension can
// spawn the engine directly with NO .NET runtime on the user's machine (resolveEngine() → kind 'exe').
//
// Usage:
//   node scripts/package.mjs <target>     # one target, e.g. win32-x64
//   node scripts/package.mjs all          # every target below
//
// Targets map to .NET RIDs. The webview is built ONCE up front (it is platform-agnostic); the engine is
// re-published per target into <ext>/engine (cleaned before AND after each target so no RID leaks into another).

import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';
import { verifyVsix } from './verify-vsix.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const extRoot = path.resolve(scriptDir, '..');
const repoRoot = path.resolve(extRoot, '..');
const engineCsproj = path.join(repoRoot, 'Semanticus.Engine', 'Semanticus.Engine.csproj');
const engineDir = path.join(extRoot, 'engine');
const distDir = path.join(extRoot, 'dist');
const NON_PRODUCT_RUNTIME_BINARIES = ['createdump', 'createdump.exe'];

// vsce --target  ->  .NET runtime identifier
const TARGETS = {
  'win32-x64':   'win-x64',
  'win32-arm64': 'win-arm64',
  'linux-x64':   'linux-x64',
  'darwin-x64':  'osx-x64',
  'darwin-arm64':'osx-arm64',
};

const version = JSON.parse(fs.readFileSync(path.join(extRoot, 'package.json'), 'utf8')).version;

function run(cmd, args, opts = {}) {
  console.log(`\n> ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, { stdio: 'inherit', cwd: extRoot, ...opts });
  if (r.error) throw r.error;
  if (r.status !== 0) throw new Error(`${cmd} exited with code ${r.status}`);
}

function cleanEngine() {
  fs.rmSync(engineDir, { recursive: true, force: true });
}

function pruneNonProductRuntimeBinaries() {
  for (const name of NON_PRODUCT_RUNTIME_BINARIES) {
    fs.rmSync(path.join(engineDir, name), { force: true });
  }
}

function resolveVsce() {
  // Run vsce's node entry directly (its bin is a #!/usr/bin/env node script) — avoids npx/.cmd shell quirks
  // across platforms. Requires `npm install` to have run in the extension root.
  const vsce = path.join(extRoot, 'node_modules', '@vscode', 'vsce', 'vsce');
  if (!fs.existsSync(vsce)) {
    throw new Error(`@vscode/vsce not found at ${vsce} — run \`npm install\` in ${extRoot} first.`);
  }
  return vsce;
}

function resolveNpmCli() {
  const candidates = [
    process.env.npm_execpath,
    path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js'),
    path.resolve(path.dirname(process.execPath), '..', 'lib', 'node_modules', 'npm', 'bin', 'npm-cli.js'),
  ].filter(Boolean);
  const npmCli = candidates.find((candidate) => fs.existsSync(candidate));
  if (!npmCli) throw new Error('npm-cli.js was not found beside Node. Run this packager through `npm run package:win`.');
  return npmCli;
}

async function packageTarget(target, vsce) {
  const rid = TARGETS[target];
  if (!rid) throw new Error(`Unknown target "${target}". Known: ${Object.keys(TARGETS).join(', ')}, all`);

  console.log(`\n=== Packaging ${target} (RID ${rid}) ===`);
  cleanEngine();
  // Self-contained publish: bundles the .NET runtime so the user needs no dotnet installed.
  run('dotnet', ['publish', engineCsproj, '-c', 'Release', '-r', rid, '--self-contained', '-o', engineDir]);
  pruneNonProductRuntimeBinaries();

  const outFile = path.join(distDir, `semanticus-${target}-${version}.vsix`);
  fs.mkdirSync(distDir, { recursive: true });
  run(process.execPath, [vsce, 'package', '--target', target, '-o', outFile]);

  const verified = await verifyVsix(outFile, target);
  console.log(
    `  payload verified: ${verified.entries} entries, ${verified.engineFiles} engine files; ` +
    `extracted engine executed: ${verified.hostExecuted}`);

  const sizeMb = (fs.statSync(outFile).size / (1024 * 1024)).toFixed(1);
  console.log(`\n  -> ${outFile}  (${sizeMb} MB)`);
  return outFile;
}

const arg = process.argv[2];
if (!arg) {
  console.error(`Usage: node scripts/package.mjs <target|all>\n  targets: ${Object.keys(TARGETS).join(', ')}`);
  process.exit(2);
}

// Build the (platform-agnostic) webview ONCE, then publish + package the engine per target.
const npmCli = resolveNpmCli();
run(process.execPath, [npmCli, 'run', 'build:webview']);
run(process.execPath, [npmCli, 'run', 'compile']);

const vsce = resolveVsce();
const targets = arg === 'all' ? Object.keys(TARGETS) : [arg];
const built = [];
try {
  for (const t of targets) built.push(await packageTarget(t, vsce));
} finally {
  cleanEngine();  // never leave a RID-specific engine/ lying around (would ship into the next hand-built vsix)
}

console.log('\nDone. Packaged:');
for (const f of built) console.log('  ' + f);
