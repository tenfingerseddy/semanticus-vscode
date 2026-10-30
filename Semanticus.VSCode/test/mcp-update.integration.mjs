// Real MCP processes and a real extension-folder deletion. Run after building the engine and extension:
// node test/mcp-update.integration.mjs ../Semanticus.Engine/bin/Debug/net8.0
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import * as fs from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { createInterface } from 'node:readline';
import { writePosixLauncher } from '../out/mcpLauncher.js';
import { engineBuildIdentity } from '../out/engineResolution.js';

assert.notEqual(process.platform, 'win32', 'This integration test exercises the POSIX launcher.');
const engineDir = resolve(process.argv[2] ?? '../Semanticus.Engine/bin/Release/net8.0');
assert.ok(fs.existsSync(join(engineDir, 'Semanticus.Engine')), `Build the engine first: ${engineDir}`);
const root = fs.mkdtempSync(join(tmpdir(), 'update-'));
const home = join(root, 'Home (test)');
const extensionsDir = join(home, '.vscode', 'extensions');
const workspace = join(root, 'Workspace');
fs.mkdirSync(home, { recursive: true });
fs.mkdirSync(workspace);
fs.mkdirSync(join(workspace, '.semanticus', 'workflows'), { recursive: true });
fs.copyFileSync(join(engineDir, 'workflows', 'check-blast-radius.md'), join(workspace, '.semanticus', 'workflows', 'check-blast-radius.md'));
const children = [];
const owners = new Set();
const install = (version) => {
  const dir = join(extensionsDir, `semanticus-vscode.semanticus-vscode-${version}`, 'engine');
  fs.mkdirSync(dirname(dir), { recursive: true });
  fs.cpSync(engineDir, dir, { recursive: true });
  return join(dir, 'Semanticus.Engine');
};
const owner = (folder = workspace) => {
  const record = JSON.parse(fs.readFileSync(join(folder, '.semanticus', 'engine.json'), 'utf8'));
  owners.add(record.Pid);
  return record;
};
async function connect(launcher, folder = workspace) {
  const child = spawn(launcher, ['mcp', '--workspace', folder], {
    env: { ...process.env, HOME: home, SEMANTICUS_HOME: join(home, '.semanticus'),
      DOTNET_CLI_HOME: home, DOTNET_PROCESSOR_COUNT: '2', SEMANTICUS_DEV_PRO: '1', ANTHROPIC_API_KEY: '' },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  children.push(child);
  let stderr = '';
  child.stderr.on('data', (data) => {
    stderr += data;
    for (const match of stderr.matchAll(/started Semanticus for this folder \(pid ([0-9]+)\)/g)) owners.add(Number(match[1]));
  });
  const pending = new Map();
  createInterface({ input: child.stdout }).on('line', (line) => {
    const message = JSON.parse(line);
    const receive = pending.get(message.id);
    if (receive) { pending.delete(message.id); receive(message); }
  });
  let id = 0;
  const request = async (method, params) => {
    const current = ++id;
    let timer;
    const response = new Promise((done, fail) => {
      pending.set(current, done);
      timer = setTimeout(() => fail(new Error(`${method} timed out: ${stderr}`)), 30_000);
    });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method, params }) + '\n');
    try {
      const message = await response;
      assert.equal(message.error, undefined, JSON.stringify(message));
      return message.result;
    } finally { clearTimeout(timer); pending.delete(current); }
  };
  await request('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'update-test', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  owner(folder);
  return { child, call: (name, args = {}) => request('tools/call', { name, arguments: args }) };
}
async function disconnect(connection) {
  const exited = once(connection.child, 'exit');
  connection.child.stdin.end();
  await exited;
}
const cachedCopy = (source) => fs.readdirSync(join(home, '.semanticus', 'engine'))
  .map((name) => join(home, '.semanticus', 'engine', name))
  .find((dir) => fs.existsSync(join(dir, '.semanticus-source')) && fs.readFileSync(join(dir, '.semanticus-source'), 'utf8') === source);
function workflows(result) {
  assert.equal(result.isError ?? false, false, JSON.stringify(result));
  const entries = JSON.parse(result.content[0].text);
  assert.ok(entries.length > 1, 'The installed stock workflows must survive the update too.');
  const entry = entries.find((item) => item.name === 'check-blast-radius');
  assert.ok(entry, JSON.stringify(result));
  assert.equal(entry.error ?? null, null, `A late-loaded parser must still run: ${JSON.stringify(result)}`);
  assert.ok(entry.stepCount > 0, JSON.stringify(entry));
}
try {
  const oldEngine = install('1.7.0');
  const launcher = writePosixLauncher(fs, home, { extensionsDir, fallbackEngine: oldEngine });
  const connection = await connect(launcher);
  const first = await connection.call('list_connections');
  assert.equal(first.isError ?? false, false, JSON.stringify(first));
  assert.doesNotMatch(JSON.stringify(first), /Semanticus was updated after this assistant connected/);
  const oldOwner = owner();
  if (process.platform === 'linux')
    assert.doesNotMatch(fs.readFileSync(`/proc/${oldOwner.Pid}/maps`, 'utf8'), /YamlDotNet\.dll/,
      'The regression must exercise an assembly that has not loaded before the deletion.');
  fs.rmSync(dirname(dirname(oldEngine)), { recursive: true });
  const after = await connection.call('list_workflows');
  assert.equal(after.isError ?? false, false, `A connected engine must survive deletion of its install: ${JSON.stringify(after)}`);
  assert.doesNotMatch(JSON.stringify(after), /Could not load file or assembly|YamlDotNet|FileNotFoundException/,
    `A late-loaded parser must still run: ${JSON.stringify(after)}`);
  workflows(after);
  assert.match(JSON.stringify(after), /Semanticus was updated after this assistant connected/);
  const oldCopy = cachedCopy(oldEngine);
  assert.ok(oldCopy, 'The engine must run from a retained copy.');
  if (process.platform === 'linux') {
    assert.equal(fs.readlinkSync(`/proc/${oldOwner.Pid}/exe`), join(oldCopy, 'Semanticus.Engine'));
    assert.equal(fs.readlinkSync(`/proc/${connection.child.pid}/exe`), join(oldCopy, 'Semanticus.Engine'));
  }
  assert.equal(owner().ExePath, oldEngine, 'The original install remains the owner provenance.');
  assert.equal(owner().BuildId, oldOwner.BuildId, 'The connected owner keeps its original build.');

  const newEngine = install('1.7.1');
  // A PE overlay changes the build identity without changing executable IL. Both real builds can use one test output.
  fs.appendFileSync(join(dirname(newEngine), 'Semanticus.Analysis.dll'), '\nupdate-test-build\n');
  const newBuild = engineBuildIdentity(dirname(newEngine));
  assert.notEqual(newBuild, oldOwner.BuildId);
  const probeWorkspace = join(root, 'Probe');
  fs.mkdirSync(probeWorkspace);
  const probe = await connect(launcher, probeWorkspace);
  assert.ok(fs.existsSync(oldCopy), 'Cleanup must retain a copy with running MCP and owner processes.');
  assert.equal(owner(probeWorkspace).BuildId, newBuild);
  assert.match(JSON.stringify(await connection.call('list_connections')), /Semanticus was updated after this assistant connected/);

  // Pause only our owner so its eight-second shutdown grace cannot race a slow test machine.
  process.kill(oldOwner.Pid, 'SIGSTOP');
  try {
    await disconnect(connection);
    const ownerOnlyProbe = await connect(launcher, probeWorkspace);
    process.kill(oldOwner.Pid, 0);
    assert.ok(fs.existsSync(oldCopy), 'Cleanup must retain the copy while only its owner remains.');
    await disconnect(ownerOnlyProbe);
  } finally { process.kill(oldOwner.Pid, 'SIGCONT'); }
  const reconnected = await connect(launcher);
  const newOwner = owner();
  assert.notEqual(newOwner.Pid, oldOwner.Pid);
  assert.equal(newOwner.BuildId, newBuild, 'Reconnection must select the newly installed build.');
  const current = await reconnected.call('list_workflows');
  workflows(current);
  assert.doesNotMatch(JSON.stringify(current), /Semanticus was updated after this assistant connected/);
  const sweep = await connect(launcher, probeWorkspace);
  assert.equal(fs.existsSync(oldCopy), false, 'The next launch removes the old copy once both processes have exited.');
  const newCopy = cachedCopy(newEngine);
  assert.ok(fs.existsSync(newCopy));
  assert.equal(owner(probeWorkspace).BuildId, newBuild);
  await disconnect(sweep);
  await disconnect(probe);
  await disconnect(reconnected);
  console.log('mcp-update: late load, notice, reconnect, owner-only retention and inactive-copy cleanup passed');
} finally {
  for (const child of children) {
    if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await once(child, 'exit'); }
  }
  for (const pid of owners) { try { process.kill(pid, 'SIGKILL'); } catch { /* already exited */ } }
  fs.rmSync(root, { recursive: true, force: true });
}
