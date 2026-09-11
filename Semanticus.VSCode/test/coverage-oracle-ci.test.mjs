import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const repoRoot = new URL('../../', import.meta.url);
const workflow = readFileSync(new URL('.github/workflows/ci.yml', repoRoot), 'utf8')
  .replaceAll('\r\n', '\n');
const oracleSource = readFileSync(new URL('tools/coverage-oracle.ps1', repoRoot), 'utf8');
const inventory = JSON.parse(readFileSync(new URL('docs/mcp-surface-inventory.json', repoRoot), 'utf8'));

const EVIDENCE_PATH = /^(?:Semanticus\.(?:Tests|Smoke|RpcSmoke|McpSmoke|AirSmoke|CicdSmoke|LearnSmoke|LearnBench)\/|Semanticus\.VSCode\/(?:src\/test|test)\/).+\.(?:cs|mjs)$/u;

function assertTrackedEvidenceIsSortedPathSet(value) {
  assert.ok(Array.isArray(value),
    'trackedEvidenceFiles must be the sorted path set, not a count');
  assert.ok(value.length > 0, 'the recorded evidence set must not be empty');
  assert.equal(value.length, new Set(value).size, 'the recorded evidence set must be unique');
  assert.deepEqual(value, [...value].sort(),
    'the recorded evidence set must be in ordinal path order');
  for (const path of value) {
    assert.equal(typeof path, 'string');
    assert.ok(EVIDENCE_PATH.test(path), `recorded evidence path is outside the oracle glob: ${path}`);
  }
}

const daxDebugCommand = /^        run: dotnet test Semanticus\.Dax\.Tests\/Semanticus\.Dax\.Tests\.csproj -c Debug\r?$/m;
const daxReleaseCommand = /^        run: dotnet test Semanticus\.Dax\.Tests\/Semanticus\.Dax\.Tests\.csproj -c Release\r?$/m;

function assertDaxTestsRunInDebugAndRelease(workflowText) {
  assert.match(workflowText, daxDebugCommand,
    'CI must explicitly run the dependency-free Semanticus.Dax test project in Debug');
  assert.match(workflowText, daxReleaseCommand,
    'CI must explicitly run the dependency-free Semanticus.Dax test project in Release');
}

function jobBlock(workflowText, name) {
  const marker = `\n  ${name}:\n`;
  const start = workflowText.indexOf(marker);
  assert.notEqual(start, -1, `CI must keep the ${name} job`);
  const bodyStart = start + marker.length;
  const rest = workflowText.slice(bodyStart);
  const nextJob = rest.search(/\n  [A-Za-z0-9_-]+:\n/u);
  return nextJob === -1 ? rest : rest.slice(0, nextJob);
}

function assertDaxTestsRunOnBothOperatingSystems(workflowText) {
  const buildAndSmoke = jobBlock(workflowText, 'build-and-smoke');
  assert.match(buildAndSmoke, /^        os: \[ubuntu-latest, windows-latest\]$/m,
    'the DAX test job must keep both Linux and Windows matrix legs');
  assertDaxTestsRunInDebugAndRelease(buildAndSmoke);
}

assertDaxTestsRunOnBothOperatingSystems(workflow);

function stripGuardedCommand(workflowText, command, label) {
  const stripped = workflowText.replace(
    command,
    `        run: echo "${label} DAX test step deliberately stripped by positive control"`,
  );
  assert.notEqual(stripped, workflowText,
    `the DAX CI positive control must strip the real guarded ${label} command`);
  return stripped;
}

const workflowWithoutDebug = stripGuardedCommand(workflow, daxDebugCommand, 'Debug');
assert.throws(() => assertDaxTestsRunOnBothOperatingSystems(workflowWithoutDebug),
  /CI must explicitly run the dependency-free Semanticus\.Dax test project in Debug/u,
  'the DAX CI guard must reject a workflow copy whose explicit Debug test command was stripped');

const workflowWithoutRelease = stripGuardedCommand(workflow, daxReleaseCommand, 'Release');
assert.throws(() => assertDaxTestsRunOnBothOperatingSystems(workflowWithoutRelease),
  /CI must explicitly run the dependency-free Semanticus\.Dax test project in Release/u,
  'the DAX CI guard must reject a workflow copy whose explicit Release test command was stripped');

const workflowWithoutWindows = workflow.replace(
  '        os: [ubuntu-latest, windows-latest]',
  '        os: [ubuntu-latest]',
);
assert.notEqual(workflowWithoutWindows, workflow,
  'the DAX CI positive control must strip the Windows matrix leg');
assert.throws(() => assertDaxTestsRunOnBothOperatingSystems(workflowWithoutWindows),
  /the DAX test job must keep both Linux and Windows matrix legs/u,
  'the DAX CI guard must reject a workflow copy whose Windows matrix leg was stripped');

assert.match(workflow, /^  coverage-oracle:\r?$/m,
  'CI must keep the generated coverage inventory as a dedicated visible job');
assert.match(workflow, /^    name: coverage oracle\r?$/m,
  'the coverage-oracle job must keep its stable required-check name');
assert.match(workflow, /^        run: \.\/tools\/coverage-oracle\.ps1 -Check\r?$/m,
  'CI must fail when the committed coverage inventory does not match tracked source');

assert.doesNotMatch(oracleSource, /trackedEvidenceFiles\s*=\s*\$evidenceText\.Count/u,
  'the coverage oracle must not record a count of evidence files');
assert.match(oracleSource, /trackedEvidenceFiles\s*=\s*@\(\$trackedEvidencePaths\)/u,
  'the coverage oracle must assign the sorted evidence path set');

assertTrackedEvidenceIsSortedPathSet(inventory.summary.trackedEvidenceFiles);

// Count-drift control for F-002. This extension test proves that a count-shaped value is refused.
// Two branches that each add a different file keep the same COUNT and disagree on the SET; that
// collision shape is a fixture here. Exact path-set drift is proved by coverage-oracle.ps1 -Check,
// which byte-compares the whole inventory, not by this file.
const branchA = ['Semanticus.Tests/AlphaTests.cs', 'Semanticus.Tests/SharedTests.cs'];
const branchB = ['Semanticus.Tests/BetaTests.cs', 'Semanticus.Tests/SharedTests.cs'];
assert.equal(branchA.length, branchB.length,
  'the F-002 collision fixture must keep equal counts');
assert.notDeepEqual(branchA, branchB,
  'the F-002 collision fixture must disagree on membership');
assert.throws(() => assertTrackedEvidenceIsSortedPathSet(branchA.length),
  /sorted path set, not a count/u,
  'recording a count must fail the evidence-set assertion');

const swapped = inventory.summary.trackedEvidenceFiles.slice();
const last = swapped.length - 1;
assert.ok(last > 0, 'the live set must have more than one path so a swap is possible');
swapped[last] = swapped[last].endsWith('AlphaControl.cs')
  ? swapped[last].replace(/AlphaControl\.cs$/u, 'BetaControl.cs')
  : swapped[last].replace(/(\.cs|\.mjs)$/u, 'AlphaControl$1');
assert.equal(swapped.length, inventory.summary.trackedEvidenceFiles.length,
  'a one-path swap must keep the count');
assert.notDeepEqual(swapped, inventory.summary.trackedEvidenceFiles,
  'a one-path swap must disagree with the recorded set');

console.log('coverage oracle and DAX CI contracts passed');
