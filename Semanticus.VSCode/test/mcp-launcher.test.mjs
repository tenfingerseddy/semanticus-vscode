// The assistant's connection must survive an extension update. Before this, .mcp.json named the engine inside
// the VERSIONED extension folder; VS Code deletes that folder after an update, so the entry written by 1.2.0 could
// not start anything under 1.2.1. These pin the stable launcher (POSIX) and the activation heal (every platform).
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import * as fs from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ASSISTANT_UNSAVED_OLDER,
  ASSISTANT_UNSAVED_SAME,
  OLDER_OWNER_ACTIONS,
  OLDER_OWNER_SAVE,
  decideOwnerStop,
  saveOlderOwnerThenProceed,
  decideStaleEngine,
  shouldAutoHealMcpEntry,
} from '../out/engineResolution.js';
import { isLauncherCommand, launcherEntry, launcherPath, posixLauncherScript, writePosixLauncher } from '../out/mcpLauncher.js';

// ---- the activation heal: an entry written by 1.2.0 heals under 1.2.1 ------------------------------------------

const ws = '/home/contoso/Models/Sales (Main)';
const old120 = {
  command: '/home/contoso/.vscode/extensions/semanticus-vscode.semanticus-vscode-1.2.0/engine/Semanticus.Engine',
  args: ['mcp', '--workspace', ws],
};
const launcher = launcherEntry('/home/contoso/.semanticus/bin/semanticus-mcp', ws);
assert.equal(shouldAutoHealMcpEntry(old120, launcher, 'linux'), true,
  'activation must move an entry written by 1.2.0 onto the stable launcher');
assert.equal(shouldAutoHealMcpEntry(old120, launcher, 'darwin'), true,
  'the same heal applies on macOS');
assert.equal(shouldAutoHealMcpEntry(launcher, launcher, 'linux'), false,
  'a launcher entry that is already current is left alone');
assert.equal(shouldAutoHealMcpEntry({ ...launcher, args: ['mcp', '--workspace', '/home/contoso/Other'] }, launcher, 'linux'), false,
  'a launcher entry for another folder is not rewritten');
assert.equal(shouldAutoHealMcpEntry({ ...old120, env: { CUSTOM: '1' } }, launcher, 'linux'), false,
  'a hand-augmented entry still needs the explicit Connect confirmation');
const win120 = {
  command: 'C:\\Users\\Kane (Work)\\.vscode\\extensions\\semanticus-vscode.semanticus-vscode-1.2.0\\engine\\Semanticus.Engine.exe',
  args: ['mcp', '--workspace', 'C:\\Users\\Kane (Work)\\Models\\Sales'],
};
const win121 = { ...win120, command: win120.command.replace('1.2.0', '1.2.1') };
assert.equal(shouldAutoHealMcpEntry(win120, win121, 'win32'), true,
  'on Windows an entry written by 1.2.0 heals to the 1.2.1 engine, parentheses and spaces included');
assert.equal(isLauncherCommand(launcher.command), true);
assert.equal(isLauncherCommand(old120.command), false);

// ---- an owner the assistant started never blocks the updated VS Code --------------------------------------------
// The assistant starts its own owner when VS Code is closed. That owner's recorded parent is the assistant's
// connection, which stays alive, so without a marker a window on the NEW version would read it as another open
// window and refuse the folder. An assistant-started owner from another version is stopped instead; the old
// connection then tells the assistant to reconnect.
const staleInput = {
  recordedExecutable: '/home/contoso/.vscode/extensions/semanticus-vscode.semanticus-vscode-1.2.0/engine/Semanticus.Engine',
  extensionPath: '/home/contoso/.vscode/extensions/semanticus-vscode.semanticus-vscode-1.2.1',
  recordedParentPid: 4242,
  recordedParentAlive: true,
  enginePidAlive: true,
  platform: 'linux',
};
assert.equal(decideStaleEngine(staleInput), 'leave-foreign', 'a window-owned engine from another version is still left alone');
assert.equal(decideStaleEngine({ ...staleInput, recordedStartedBy: 'assistant' }), 'stop-stale',
  'an assistant-started owner from another version must not lock the updated window out');
assert.equal(decideStaleEngine({ ...staleInput, recordedStartedBy: 'assistant',
  recordedExecutable: '/home/contoso/.vscode/extensions/semanticus-vscode.semanticus-vscode-1.2.1/engine/Semanticus.Engine' }), 'keep',
  'the same version is kept whoever started it (the handshake reclaim handles that case)');

// ---- Studio never throws away work the assistant holds ----------------------------------------------------------
// Before VS Code stops an owner it cannot join (the reclaim) or an assistant-started owner from another version
// (the sweep), it asks that owner, over the open agent door, whether it holds unsaved changes. Only a clean answer
// allows the stop. Unsaved, or no answer at all, leaves it running with one plain line.
assert.equal(decideOwnerStop(false), 'stop', 'a clean owner may be stopped');
assert.equal(decideOwnerStop(true), 'leave-running', 'an owner with unsaved changes is never stopped');
assert.equal(decideOwnerStop(undefined), 'leave-running', 'an owner that could not be asked is never stopped');
assert.equal(ASSISTANT_UNSAVED_OLDER, 'Your assistant has unsaved changes in an older engine. Save them and this window will carry on.');
for (const line of [ASSISTANT_UNSAVED_OLDER, ASSISTANT_UNSAVED_SAME]) {
  assert.ok(!/\u2014/.test(line), 'no em dashes');
  assert.equal(line.split('\n').length, 1, 'one plain line');
}

// ---- the launcher itself (POSIX) -------------------------------------------------------------------------------

if (process.platform === 'win32') {
  console.log('mcp-launcher: POSIX launcher checks skipped on Windows (the entry there stays the engine path).');
} else {
  // A home and a workspace with spaces and parentheses, as on Kane's machines.
  const root = mkdtempSync(join(tmpdir(), 'smx launcher ('));
  try {
    const home = join(root, 'Contoso User (Work)');
    const extDir = join(home, '.vscode', 'extensions');
    const workspace = join(root, 'Sales Model (v2)');
    mkdirSync(workspace, { recursive: true });

    // A stand-in engine per version: it prints which version ran and the arguments it got, one per line.
    const install = (version) => {
      const exe = join(extDir, `semanticus-vscode.semanticus-vscode-${version}`, 'engine', 'Semanticus.Engine');
      mkdirSync(dirname(exe), { recursive: true });
      writeFileSync(exe, `#!/bin/sh\necho "ran ${version}"\nfor a in "$@"; do echo "arg=$a"; done\n`);
      chmodSync(exe, 0o755);
      return exe;
    };
    const e120 = install('1.2.0');
    install('1.9.3');
    const e1100 = install('1.10.0-linux-x64');   // numeric compare, and a platform-specific folder name

    const written = writePosixLauncher(fs, home, { extensionsDir: extDir, fallbackEngine: e120 });
    assert.equal(written, launcherPath(home));
    const body = readFileSync(written);
    assert.ok(!body.some((b) => b < 0x20 && b !== 0x0a), 'the launcher must contain no raw control bytes');
    assert.ok((fs.statSync(written).mode & 0o111) !== 0, 'the launcher must be executable');

    const run = (args, opts = {}) => spawnSync(written, args, { encoding: 'utf8', timeout: 10000, ...opts });

    let r = run(['mcp', '--workspace', workspace]);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stdout, /^ran 1\.10\.0-linux-x64$/m, 'the launcher must pick the newest installed engine');
    assert.deepEqual(r.stdout.trim().split('\n').slice(1), ['arg=mcp', 'arg=--workspace', `arg=${workspace}`],
      'every argument must reach the engine intact');

    // A live Semanticus for this folder on an older installed version is matched, not the newest.
    const rec = join(workspace, '.semanticus');
    mkdirSync(rec, { recursive: true });
    writeFileSync(join(rec, 'engine.json'), JSON.stringify({ PipeName: 'p', Pid: process.pid, ExePath: e120 }, null, 2));
    r = run(['mcp', '--workspace', workspace]);
    assert.match(r.stdout, /^ran 1\.2\.0$/m, 'a running older version for this folder must be matched');

    // A dead owner, or a record naming anything outside the installed extensions, is ignored.
    writeFileSync(join(rec, 'engine.json'), JSON.stringify({ Pid: 2147483646, ExePath: e120 }, null, 2));
    r = run(['mcp', '--workspace', workspace]);
    assert.match(r.stdout, /^ran 1\.10\.0-linux-x64$/m, 'a record whose process is gone must not pin an old version');
    const planted = join(workspace, 'Semanticus.Engine');
    writeFileSync(planted, '#!/bin/sh\necho planted\n');
    chmodSync(planted, 0o755);
    for (const exePath of [planted, `${extDir}/semanticus-vscode.semanticus-vscode-9/../../../../${planted}`]) {
      writeFileSync(join(rec, 'engine.json'), JSON.stringify({ Pid: process.pid, ExePath: exePath }, null, 2));
      r = run(['mcp', '--workspace', workspace]);
      assert.doesNotMatch(r.stdout, /planted/, `a workspace file must never choose what runs (${exePath})`);
      assert.match(r.stdout, /^ran 1\.10\.0-linux-x64$/m);
    }

    // The newest folder was removed by an update: the next newest runs.
    rmSync(dirname(dirname(e1100)), { recursive: true, force: true });
    rmSync(join(rec, 'engine.json'));
    r = run(['mcp', '--workspace', workspace]);
    assert.match(r.stdout, /^ran 1\.9\.3$/m);

    // Nothing installed at all: one plain line on stderr and a quick exit, not a hang.
    rmSync(extDir, { recursive: true, force: true });
    const started = Date.now();
    r = run(['mcp', '--workspace', workspace]);
    assert.equal(r.status, 1);
    assert.ok(Date.now() - started < 5000, 'a missing engine must fail fast');
    assert.equal(r.stdout, '');
    assert.equal(r.stderr.trim().split('\n').length, 1, 'exactly one line on stderr');
    assert.match(r.stderr, /Run Semanticus: Connect AI Assistant in VS Code, then reconnect with \/mcp\./);
    assert.ok(!/\u2014/.test(r.stderr) && !/\u2014/.test(posixLauncherScript({ extensionsDir: 'a', fallbackEngine: 'b' })),
      'no em dashes in anything the user can read');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

// ---- F-239: the older owner's notice offers Save, and Save goes through that owner's own agent door ---------------
// After an update the new launcher cannot attach to the older owner, so "ask your assistant to save" was a dead end.
// The notice now carries a Save button. A fake owner stands in for the engine: it answers the agent door's save,
// and the sweep that runs next stops it only when it answers clean, exactly as the real sweep does.

assert.deepEqual(OLDER_OWNER_ACTIONS, [OLDER_OWNER_SAVE], 'the notice offers exactly one action');
assert.equal(OLDER_OWNER_SAVE, 'Save');

function fakeOwner({ saveFails } = {}) {
  const owner = {
    alive: true, unsaved: true, saves: 0,
    async save() {
      owner.saves++;
      if (saveFails !== undefined) throw new Error(saveFails);
      owner.unsaved = false;
      return { revision: 7, path: '/home/contoso/Models/Sales.bim', format: 'bim', fileCount: 1 };
    },
  };
  // The usual path: ask the owner, stop it only on a clean answer, then this window starts its own engine.
  const proceed = async () => {
    if (decideOwnerStop(owner.alive ? owner.unsaved : undefined) === 'stop') owner.alive = false;
    owner.proceeded = true;
  };
  return { owner, proceed };
}

{
  const { owner, proceed } = fakeOwner();
  const outcome = await saveOlderOwnerThenProceed(owner, proceed);
  assert.equal(outcome.saved, true);
  assert.equal(owner.saves, 1, 'saved once');
  assert.equal(owner.proceeded, true, 'after the save the window carries on');
  assert.equal(owner.alive, false, 'the saved owner is clean, so the usual reclaim stops it');
  assert.equal(outcome.line, "Saved your assistant's changes. This window is taking over the folder.");
}
{
  const { owner, proceed } = fakeOwner({ saveFails: 'The file is read-only.\n   at Semanticus.Engine.Save' });
  const outcome = await saveOlderOwnerThenProceed(owner, proceed);
  assert.equal(outcome.saved, false);
  assert.equal(owner.proceeded, undefined, 'a failed save does not carry on');
  assert.equal(owner.alive, true, 'a failed save keeps the owner and its work');
  assert.equal(outcome.line, "Could not save your assistant's changes: The file is read-only. The older engine keeps running.");
}
{
  const { owner, proceed } = fakeOwner({ saveFails: '' });
  const outcome = await saveOlderOwnerThenProceed(owner, proceed);
  assert.equal(outcome.saved, false);
  assert.equal(owner.alive, true);
  assert.equal(outcome.line, "Could not save your assistant's changes: the older engine did not say why. The older engine keeps running.");
}
{
  // A save that never answers is a failure, not a hang: the owner keeps running.
  const owner = { alive: true, save: () => new Promise(() => {}) };
  const outcome = await saveOlderOwnerThenProceed(owner, async () => { owner.alive = false; }, 50);
  assert.equal(outcome.saved, false);
  assert.equal(owner.alive, true);
  assert.match(outcome.line, /did not answer in time/);
}
for (const line of [ASSISTANT_UNSAVED_OLDER, "Saved your assistant's changes. This window is taking over the folder."]) {
  assert.ok(![...line].some((ch) => ch.charCodeAt(0) === 0x2014), 'no em dashes');
}

// ---- the extension wires it: launcher written on activation, before the heal, and the entry uses it --------------

const here = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(here, 'src', 'extension.ts'), 'utf8');
assert.match(source, /ensureMcpLauncher\(context\);[\s\S]*?await autoHealSemanticusMcpEntry\(context\);/,
  'activation must rewrite the launcher before it heals the entry');
assert.match(source, /function semanticusMcpEntry[\s\S]*?launcherEntry\(/,
  'Connect AI Assistant must write the launcher entry when the launcher exists');

assert.match(source, /ownerHoldsUnsavedWork\([^)]*\)[\s\S]{0,600}?await reclaimUiOwnership\(/,
  'the reclaim must ask the owner about unsaved changes before stopping it');
assert.match(source, /function sweepEngineRecord[\s\S]*?ownerHoldsUnsavedWork\(/,
  'the version sweep must ask an assistant-started owner about unsaved changes before stopping it');

assert.match(source, /function sweepEngineRecord[\s\S]*?throw new OlderOwnerUnsavedError\(info\.pipeName\)/,
  'the sweep hands the older owner\'s pipe to the notice');
assert.match(source, /instanceof OlderOwnerUnsavedError[\s\S]{0,300}?offerSaveForOlderOwner\(/,
  'the connect failure for an older owner with unsaved work offers Save instead of the generic error');
assert.match(source, /function offerSaveForOlderOwner[\s\S]*?OLDER_OWNER_ACTIONS[\s\S]*?saveOlderOwnerThenProceed\([\s\S]*?'agent'/,
  'Save goes through the older owner\'s agent door');

console.log('mcp-launcher: ok');
