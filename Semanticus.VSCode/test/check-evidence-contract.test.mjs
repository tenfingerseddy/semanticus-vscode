// T206: controls live in this suite, not in the checks under test. Collection probes
// keep cardinality fixed and change membership. Do not replace these with count assertions.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { main } from '../../tools/tasks-register-check.mjs';

const repo = fileURLToPath(new URL('../../', import.meta.url));
const psQuote = value => "'" + value.replaceAll("'", "''") + "'";
const sha256 = value => createHash('sha256').update(value).digest('hex');
function inHome(action) {
  const home = mkdtempSync(join(tmpdir(), 'semanticus-evidence-'));
  try { return action(home); } finally { rmSync(home, { recursive: true, force: true }); }
}
function put(home, path, contents) {
  const full = join(home, path);
  mkdirSync(dirname(full), { recursive: true });
  writeFileSync(full, contents);
  return full;
}
function register(home, text) {
  const output = [];
  const code = main(put(home, 'TASKS.md', text), line => output.push(line), line => output.push(line));
  return { code, output: output.join('\n') };
}

test('coverage oracle rejects an equal-count evidence-file replacement', () => inHome(home => {
  const files = ['tools/coverage-oracle.ps1', 'Semanticus.Engine/McpTools.cs',
    'Semanticus.Engine/EngineRpcTarget.cs', 'Semanticus.Engine/HumanGovernanceRpcTarget.cs',
    'Semanticus.Engine/IEngine.cs', 'Semanticus.VSCode/webview/src/App.tsx',
    'Semanticus.VSCode/webview/src/route.ts'];
  for (const file of files) {
    const dest = put(home, file, '');
    copyFileSync(join(repo, file), dest);
  }
  const alpha = 'Semanticus.Tests/AlphaEvidence.cs';
  const beta = 'Semanticus.Tests/BetaEvidence.cs';
  put(home, alpha, '// invented evidence fixture');
  mkdirSync(join(home, 'docs'));
  const git = spawnSync('git', ['init', '--quiet'], { cwd: home, encoding: 'utf8' });
  assert.ifError(git.error);
  assert.equal(git.status, 0, git.stderr);
  const run = (...args) => {
    const result = spawnSync('pwsh', ['-NoProfile', '-File', join(home, files[0]), ...args],
      { cwd: home, encoding: 'utf8', timeout: 30_000 });
    assert.ifError(result.error);
    return result;
  };
  const generated = run();
  assert.equal(generated.status, 0, generated.stdout + generated.stderr);
  const inventory = () => JSON.parse(readFileSync(join(home, 'docs/mcp-surface-inventory.json'), 'utf8'));
  assert.deepEqual(inventory().summary.trackedEvidenceFiles, [alpha]);
  const baseline = run('-Check');
  assert.equal(baseline.status, 0, baseline.stdout + baseline.stderr);
  renameSync(join(home, alpha), join(home, beta));
  const replaced = run('-Check');
  assert.notEqual(replaced.status, 0, 'same-cardinality replacement must make the real check stale');
  assert.match(replaced.stdout + replaced.stderr, /Coverage inventory is stale/);
  const regenerated = run();
  assert.equal(regenerated.status, 0, regenerated.stdout + regenerated.stderr);
  assert.deepEqual(inventory().summary.trackedEvidenceFiles, [beta]);
  put(home, 'Semanticus.VSCode/webview/src/route.ts', "export type ToolId = CoreToolId | 'permissions';");
  const composed = run();
  assert.notEqual(composed.status, 0, 'a composed union must not silently record only its literal member');
  assert.match(composed.stdout + composed.stderr, /Unsupported ToolId declaration/);
}));

test('task register records the work collection, not just its cardinality', () => inHome(home => {
  const a = register(home, '- [T801] Repair the alpha reader.\n- [T802] Repair the beta writer.');
  const b = register(home, '- [T801] Repair the alpha reader.\n- [T803] Repair the gamma writer.');
  assert.equal(a.code, 0);
  assert.equal(b.code, 0);
  assert.notEqual(a.output, b.output, 'equal counts must not conceal a different task collection');
  assert.match(a.output, /Repair the beta writer/);
  assert.match(b.output, /Repair the gamma writer/);
}));

test('two ids cannot allocate identical work, including wrapped continuation text', () => inHome(home => {
  const same = register(home, '- [T801] Repair the reader\n        and preserve its receipts.\n- **[T802]** Repair the reader and preserve its receipts.');
  assert.equal(same.code, 1, 'unique ids are not proof that work is unique');
  assert.match(same.output, /same work/i);
  assert.match(same.output, /T801/);
  assert.match(same.output, /T802/);
  const distinct = register(home, '- [T801] Repair the reader\n        for alpha.\n- [T802] Repair the reader\n        for beta.');
  assert.equal(distinct.code, 0, 'a shared opening is not proof of duplicate work');
}));

// Execute the real block8 script. Only its external processes and desktop waits are
// replaced. No desktop, engine, or actual VSIX release claim is earned by this fixture.
function block8(home, mode) {
  const source = join(repo, 'tools/release/run-block8.ps1');
  const script = put(home, 'tools/release/run-block8.ps1', readFileSync(source));
  put(home, 'Semanticus.VSCode/package.json', JSON.stringify({ version: '0.0.0-fixture' }));
  const artifact = put(home, `Semanticus.VSCode/dist/semanticus-${process.platform}-${process.arch}-0.0.0-fixture.vsix`, 'stale fixture');
  const evidence = join(home, 'evidence');
  const fresh = 'fresh fixture archive';
  const reported = mode === 'substituted' ? 'other fixture archive' : fresh;
  assert.equal(reported.length, fresh.length, 'artifact replacement must keep byte count fixed');
  const driver = put(home, 'drive.ps1', `
$ErrorActionPreference = 'Stop'
$artifact = ${psQuote(artifact)}
$mode = ${psQuote(mode)}
function dotnet { $global:LASTEXITCODE = 0 }
function Start-Sleep { param($Seconds, $Milliseconds) }
function Start-Process {
    param($FilePath, [switch]$PassThru, $WindowStyle, $ArgumentList)
    $out = $ArgumentList[4]
    New-Item -ItemType Directory -Force $out | Out-Null
    Set-Content (Join-Path $out 'armed.json') '{}'
    $positive = $out.EndsWith('positive-control-human-door')
    @{
        verdict = if ($positive) { 'SOMETHING_APPEARED' } else { 'NOTHING_APPEARED' }
        browserLaneObservable = $true; baselineWindowCount = 1; polls = 10
        signinDetections = if ($positive) { 1 } else { 0 }; items = @()
    } | ConvertTo-Json | Set-Content (Join-Path $out 'summary.json')
    $process = [pscustomobject]@{ HasExited = $false }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { return $true }
    return $process
}
function node {
    $global:LASTEXITCODE = 0
    if ($args -contains '-p') { return '${process.platform}-${process.arch}' }
    if ($args -contains '--package-only' -or "$($args[0])".EndsWith('package.mjs')) {
        if ($mode -ne 'stale') { [IO.File]::WriteAllText($artifact, ${psQuote(fresh)}) }
        if ($mode -eq 'packaging-failed') { $global:LASTEXITCODE = 1 }
        if ($args -contains '--out') {
            $out = $args[[Array]::IndexOf($args, '--out') + 1]
            Set-Content (Join-Path $out 'packaged-vsix-path.txt') $artifact
        }
        return
    }
    $out = $args[[Array]::IndexOf($args, '--out') + 1]
    $probeVsix = $args[[Array]::IndexOf($args, '--vsix') + 1]
    if ($mode -eq 'post-probe-tamper') { [IO.File]::WriteAllText($probeVsix, 'other fixture archive') }
    @{
        certified = $true; claim = 'fixture only'
        artifact = @{
            vsix = (Split-Path $probeVsix -Leaf); vsixSha256 = '${sha256(reported)}'; vsixBytes = ${Buffer.byteLength(reported)}
            engineSha256 = '${sha256('fixture engine')}'; engineBytes = 14
            version = '0.0.0-fixture'; target = '${process.platform}-${process.arch}'; packagedByThisProbe = $false
        }
        observed = @{ refused = $true; authStoreFilesHashed = 1; authStoreChanged = @() }
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'packaged-engine-probe.json')
}
& ${psQuote(script)} -EvidenceDir ${psQuote(evidence)}
exit $LASTEXITCODE
`);
  const run = spawnSync('pwsh', ['-NoProfile', '-File', driver], { encoding: 'utf8', timeout: 30_000 });
  assert.ifError(run.error);
  return { ...run, result: () => JSON.parse(readFileSync(join(evidence, 'block8.json'), 'utf8')) };
}

test('block8 refuses a stale artifact despite a successful packaging command', () => inHome(home => {
  const run = block8(home, 'stale');
  assert.notEqual(run.status, 0, `a stale file was certified as locally packaged:\n${run.stdout}\n${run.stderr}`);
  assert.match(run.stdout + run.stderr, /fresh|produced no|not produced/i);
}));

test('block8 binds the probed archive to independent packaging evidence', () => inHome(home => {
  const run = block8(home, 'substituted');
  assert.notEqual(run.status, 0, 'a different archive was certified using the packaging claim');
  assert.match(run.stdout + run.stderr, /artifact|digest|packag/i);
}));

for (const mode of ['packaging-failed', 'post-probe-tamper']) {
  test(`block8 refuses ${mode} even with a fresh archive and a positive probe`, () => inHome(home => {
    const run = block8(home, mode);
    assert.notEqual(run.status, 0, run.stdout + run.stderr);
    assert.match(run.stdout + run.stderr, /packaging|archive/i);
  }));
}

test('block8 records the observed artifact when packaging and probe agree', () => inHome(home => {
  const run = block8(home, 'fresh');
  assert.equal(run.status, 0, run.stdout + run.stderr);
  const result = run.result();
  assert.equal(result.verdict, 'PASS');
  assert.equal(result.shippedArtifact.packagedLocally, true);
  assert.equal(result.shippedArtifact.packagingEvidence.sha256, sha256('fresh fixture archive'));
  assert.equal(result.shippedArtifact.packagingEvidence.bytes, Buffer.byteLength('fresh fixture archive'));
}));
