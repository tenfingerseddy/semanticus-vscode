import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

function discoverPackageRoots(root) {
  const found = [];
  const pending = [root];
  while (pending.length > 0) {
    const current = pending.pop();
    if (existsSync(new URL('package-lock.json', current))) found.push(current);
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (!entry.isDirectory() || entry.name === 'node_modules' || entry.name === '.git') continue;
      pending.push(new URL(`${entry.name}/`, current));
    }
  }
  return found.sort((left, right) => fileURLToPath(left).localeCompare(fileURLToPath(right)));
}

const packageRoots = discoverPackageRoots(new URL('../', import.meta.url));
assert.ok(packageRoots.length > 0, 'dependency audit found no npm lockfiles');

const npmCli = process.env.npm_execpath;
const command = npmCli ? process.execPath : (process.platform === 'win32' ? (process.env.ComSpec || 'cmd.exe') : 'npm');
const severityFields = ['info', 'low', 'moderate', 'high', 'critical'];

function vulnerabilityTotal(metadata, label) {
  const vulnerabilities = metadata?.vulnerabilities;
  assert.ok(vulnerabilities && typeof vulnerabilities === 'object' && !Array.isArray(vulnerabilities),
    `npm audit returned no vulnerability metadata in ${label}`);
  for (const field of [...severityFields, 'total']) {
    assert.ok(Object.hasOwn(vulnerabilities, field), `npm audit vulnerability metadata is missing ${field} in ${label}`);
    assert.ok(Number.isInteger(vulnerabilities[field]) && vulnerabilities[field] >= 0,
      `npm audit vulnerability metadata has an invalid ${field} count in ${label}`);
  }
  const sum = severityFields.reduce((total, field) => total + vulnerabilities[field], 0);
  assert.equal(vulnerabilities.total, sum, `npm audit vulnerability totals are inconsistent in ${label}`);
  return vulnerabilities.total;
}

assert.throws(() => vulnerabilityTotal({ vulnerabilities: {} }, 'parser self-test'), /missing info/);
assert.throws(() => vulnerabilityTotal({ vulnerabilities: {
  info: 0, low: 0, moderate: 0, high: 0, critical: 0, total: '0',
} }, 'parser self-test'), /invalid total/);

for (const root of packageRoots) {
  const auditArgs = ['audit', '--audit-level=low', '--json'];
  const args = npmCli
    ? [npmCli, ...auditArgs]
    : (process.platform === 'win32' ? ['/d', '/s', '/c', 'npm.cmd', ...auditArgs] : auditArgs);
  const result = spawnSync(command, args, {
    cwd: fileURLToPath(root),
    encoding: 'utf8',
    maxBuffer: 10 * 1024 * 1024,
  });

  if (result.error) throw result.error;
  if (result.status !== 0) {
    if (result.stdout) process.stderr.write(result.stdout);
    if (result.stderr) process.stderr.write(result.stderr);
    assert.fail(`npm audit failed in ${fileURLToPath(root)} with exit code ${result.status}`);
  }

  let report;
  try {
    report = JSON.parse(result.stdout);
  } catch (error) {
    assert.fail(`npm audit returned invalid JSON in ${fileURLToPath(root)}: ${error.message}`);
  }
  const total = vulnerabilityTotal(report?.metadata, fileURLToPath(root));
  assert.equal(total, 0, `npm audit reported ${total} known vulnerabilities in ${fileURLToPath(root)}`);
}

console.log('dependency advisory tests passed');
