// Kane, 2026-09-21 (Yoga): closing and reopening VS Code left old engines running. Measured on that machine
// the same day: nine `serve` processes, all reparented to systemd --user, six from extension 1.1.1 spawned
// ten days earlier and two from 1.2.0 spawned seven days earlier, alongside the live one.
//
// Three rules keep that from happening again, and this file pins the extension's two:
//
//   1. The spawn hands the engine --parent-pid, so the engine dies with this extension host even when
//      nothing below runs (a crashed host never reaches deactivate at all). The engine side of that rule
//      lives in Semanticus.Tests/EngineLifetimeTests.cs.
//   2. deactivate() stops the engine THIS window started, and activation sweeps the folder first: a record
//      whose pid is gone is cleared, and a live engine from a different extension version folder is stopped
//      rather than joined.
//
// The narrowness is the point. An engine from the SAME version folder is a session another window may own,
// so it is still attached to, never stopped.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { decideStaleEngine, engineExtensionFolder } from '../out/engineResolution.js';
import { mcpLaunchArgs, ownerServeArgs, ownerSignInArgs } from '../out/licenseDelivery.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

function slice(text, startPattern, endPattern, what) {
  const start = text.search(startPattern);
  assert.notEqual(start, -1, `could not find the start of ${what}`);
  const rest = text.slice(start);
  const end = rest.slice(1).search(endPattern);
  return end === -1 ? rest : rest.slice(0, end + 1);
}

// ---- (1) the stale-folder rule ------------------------------------------------------------------------

const ext120 = '/home/k/.vscode/extensions/semanticus-vscode.semanticus-vscode-1.2.0';
const ext121 = '/home/k/.vscode/extensions/semanticus-vscode.semanticus-vscode-1.2.1';

assert.equal(
  engineExtensionFolder(`${ext120}/engine/Semanticus.Engine`, 'linux'),
  ext120,
  'a bundled engine belongs to the version folder two levels above it',
);
assert.equal(
  engineExtensionFolder('C:\\Users\\k\\.vscode\\extensions\\pub.semanticus-vscode-1.2.0\\engine\\Semanticus.Engine.exe', 'win32'),
  'C:\\Users\\k\\.vscode\\extensions\\pub.semanticus-vscode-1.2.0',
  'the Windows apphost carries .exe and still resolves to its version folder',
);
assert.equal(
  engineExtensionFolder('/opt/dotnet8/dotnet', 'linux'),
  undefined,
  'a development owner is dotnet, which belongs to no extension folder',
);
assert.equal(
  engineExtensionFolder(undefined, 'linux'),
  undefined,
  'an owner with no recorded executable has no folder to judge',
);

// Codex P1 on PR 386: VS Code can install an update while an older window is still open. That window's
// engine then reads as "different install folder" to the newly activated window, and stopping it discards
// the older window's unsaved model work. A different folder is evidence of a version MISMATCH, not of an
// orphan. The recorded parent is what says whether anyone still owns it.

const bundled = (folder) => `${folder}/engine/Semanticus.Engine`;
const verdict = (over) => decideStaleEngine({
  recordedExecutable: bundled(ext120),
  extensionPath: ext121,
  enginePidAlive: true,
  platform: 'linux',
  ...over,
});

assert.equal(
  decideStaleEngine({ recordedExecutable: bundled(ext120), extensionPath: ext120, enginePidAlive: true, platform: 'linux' }),
  'keep',
  'an engine from the running version folder is a live session, not an orphan',
);
assert.equal(
  verdict({ recordedParentPid: 4242, recordedParentAlive: true }),
  'leave-foreign',
  'THE P1: an older version whose window is still open must be left running, not stopped',
);
assert.equal(
  verdict({ recordedParentPid: 4242, recordedParentAlive: false }),
  'stop-stale',
  'an older version whose window is gone is an orphan and is stopped',
);
assert.equal(
  verdict({ recordedParentPid: undefined }),
  'leave-foreign',
  'an old-format record has no recorded parent and no grace to consult, so a LIVE engine pid is left alone: we cannot prove it is unowned',
);
assert.equal(
  verdict({ recordedParentPid: undefined, enginePidAlive: false }),
  'stop-stale',
  'an old-format record is treated as dead only once the engine pid itself fails the liveness probe',
);
assert.equal(
  verdict({ recordedParentPid: 4242, recordedParentAlive: true, enginePidAlive: false }),
  'stop-stale',
  'a dead engine is swept whatever its record claims about a parent',
);
assert.equal(
  decideStaleEngine({ recordedExecutable: '/opt/dotnet8/dotnet', extensionPath: ext121, enginePidAlive: true, platform: 'linux' }),
  'keep',
  'a development DLL owner must not be judged by the version-folder rule at all',
);
assert.equal(
  decideStaleEngine({ recordedExecutable: undefined, extensionPath: ext121, enginePidAlive: true, platform: 'linux' }),
  'keep',
  'unknown provenance is not evidence of staleness; the owner-mismatch decision already handles it',
);
assert.equal(
  decideStaleEngine({ recordedExecutable: bundled(ext120), extensionPath: undefined, enginePidAlive: true, platform: 'linux' }),
  'keep',
  'with no running extension folder to compare against there is nothing to judge',
);
assert.equal(
  decideStaleEngine({
    recordedExecutable: 'C:\\X\\pub.semanticus-vscode-1.2.0\\engine\\Semanticus.Engine.exe',
    extensionPath: 'c:\\x\\PUB.semanticus-vscode-1.2.0',
    enginePidAlive: true,
    platform: 'win32',
  }),
  'keep',
  'Windows paths compare case insensitively, so one install must not read as two',
);

// ---- (2) the spawn ties the engine to this extension host ----------------------------------------------

const ensure = slice(source, /async function ensureEngine\(/, /\n(?:async )?function |\n\/\/\/ /, 'ensureEngine');
assert.match(ensure, /'--parent-pid', String\(process\.pid\)/,
  'the engine spawn must name this extension host, so the engine exits when VS Code does');

// ---- (3) the sweep runs before the attach decision ------------------------------------------------------

assert.match(ensure, /const existing = await sweepEngineRecord\(ws, readEngineInfo\(ws\)\);/,
  'activation must sweep the folder before it decides whether to attach');

const verdictFn = slice(source, /function staleEngineVerdict\(/, /\n(?:async )?function |\n\/\/\/ /, 'staleEngineVerdict');
assert.match(verdictFn, /recordedParentAlive: info\.parentPid === undefined \? undefined : pidAlive\(info\.parentPid\)/,
  'the sweep must probe the RECORDED PARENT, and must pass undefined rather than false when no parent was recorded');
assert.match(verdictFn, /enginePidAlive: !!info\.pid && pidAlive\(info\.pid\)/,
  'the engine pid is the other probe the rule needs');

const sweep = slice(source, /function sweepEngineRecord\(/, /\n(?:async )?function |\n\/\/\/ /, 'sweepEngineRecord');
assert.match(sweep, /const verdict = staleEngineVerdict\(info\);/,
  'one rule, shared with the home-folder sweep, so the two cannot drift apart');
assert.match(sweep, /verdict === 'leave-foreign'[\s\S]*?return info;/,
  'a foreign engine whose window is still open is left running and handed back, never stopped');
assert.match(sweep, /process\.kill\(info\.pid\)/,
  'an unowned engine is stopped by pid');
assert.match(sweep, /removeEngineFiles\(ws, info\.pipeName\)/,
  'a stopped engine leaves a socket file behind; the next connect fails on it unless it is removed');

const homeSweep = slice(source, /function sweepStaleEngineInHomeFolder\(/, /\n(?:async )?function |\n\/\/\/ /, 'sweepStaleEngineInHomeFolder');
assert.match(homeSweep, /staleEngineVerdict\(info\) !== 'stop-stale'/,
  'the home folder gets the same rule: an engine whose recorded parent is alive belongs to a window somebody is using');

// ---- (4) deactivate stops what this window started ------------------------------------------------------

const deactivateFn = slice(source, /export function deactivate\(\)/, /\n(?:export )?(?:async )?function |\n\/\/ ---- /, 'deactivate');
assert.doesNotMatch(deactivateFn, /process\.kill|spawned\.kill|killEngine/,
  'deactivate must not kill the engine: it cannot tell a reload from a quit, and killing here makes the grace unreachable');
assert.match(deactivateFn, /grace/i,
  'deactivate must say WHY it leaves the engine alone, or the next reader will call this the orphan bug again');
assert.doesNotMatch(deactivateFn, /Leave the engine running\./,
  'the old open-ended "leave it running" contract is retired; the engine is now bounded by its own grace');

// ---- (5) adoption is what makes leaving safe ------------------------------------------------------------

const adopt = slice(source, /\/\/\/ Claim the engine on the other end/, /\nasync function connectEngineAttempt/, 'adoptEngine');
assert.match(adopt, /sendRequest<boolean>\('adoptParent', process\.pid\)/,
  'a reconnecting window must claim the engine by pid so the engine switches its watch to this host');
assert.match(adopt, /catch/,
  'an engine without adoptParent (an older build) must not break activation');
const connectFn = slice(source, /async function connectEngineAttempt\(/, /\n(?:async )?function |\n\/\/\/ /, 'connectEngineAttempt');
assert.match(connectFn, /await adoptEngine\(connected\);/,
  'every connect adopts: a reload lands here inside the engine grace and this call is what saves the session');

// ---- (6) the sweep away from our own folder -------------------------------------------------------------

const home = slice(source, /function sweepStaleEngineInHomeFolder\(/, /\n(?:async )?function |\n\/\/\/ /, 'sweepStaleEngineInHomeFolder');
assert.match(home, /os\.homedir\(\)/,
  'the home folder is the one engine.json outside our workspace we can find without a registry');
assert.match(ensure, /sweepStaleEngineInHomeFolder\(ws\);/,
  'the home-folder sweep runs on activation, before we decide what to do about our own folder');

// ---- (7) door two is untouched --------------------------------------------------------------------------

// The MCP process is spawned by the assistant and dies with its stdio. Giving it a lifetime rule of its own
// could only end a healthy agent session early, so the launch line must stay clean. The engine enforces the
// same thing from its side: Semanticus.Tests/EngineLifetimeTests.Mcp_door_ignores_parent_pid passes
// --parent-pid to a real `mcp` process, kills that parent, and requires the agent door to keep serving.
assert.deepEqual(
  mcpLaunchArgs('/work/model'),
  ['mcp', '--workspace', '/work/model'],
  'the agent door launch line must carry no parent-pid: that process dies with its stdio',
);
assert.ok(
  !ownerServeArgs('/work/model').includes('--parent-pid'),
  'the shared serve args stay the pinned no-model contract; the spawn site appends the parent pid',
);

// A person's browser sign-in needs the owner told it may open the system browser (28 Sep: a test suite's sign-in
// control opened a real Microsoft sign-in page on the person's screen). A real window tells it; the extension's own
// test runs never do, and the agent door never can.
assert.deepEqual(ownerSignInArgs(false), ['--interactive-signin'], 'a real window lets its owner open the browser for a sign-in');
assert.deepEqual(ownerSignInArgs(true), [], 'an extension test run never lets the engine open a browser');
assert.ok(!ownerServeArgs('/work/model').includes('--interactive-signin') && !mcpLaunchArgs('/work/model').includes('--interactive-signin'),
  'only the spawn site adds the sign-in option, and the agent door launch line never carries it');
assert.match(source, /\.\.\.ownerSignInArgs\(context\.extensionMode === vscode\.ExtensionMode\.Test\)/,
  'the owner spawn must pass the sign-in option except in the extension\'s own test runs');

// ---- (8) the refusal a second window sees ---------------------------------------------------------------

// Ratified 2026-09-22: no two-owner model. The workspace stays single-writer, so when an older window still
// owns the engine the newer window refuses the folder. That refusal used to say "Run Semanticus: Restart
// Engine", which is the one thing the reader must not do: restarting kills the other window's engine and
// its unsaved work. It now names what is actually happening and the way through.
const mismatch = /decision === 'mismatch'\)[\s\S]{0,400}?throw new Error\('([^']*)'\)/.exec(source);
assert.ok(mismatch, 'the version-mismatch refusal must still exist');
const mismatchMessage = mismatch[1];

assert.doesNotMatch(mismatchMessage, /restart/i,
  'the refusal must never suggest restarting: that kills the other window\'s engine and its unsaved work');
assert.match(mismatchMessage, /Another VS Code window is using this model with an older version of Semanticus\./,
  'say plainly what is happening: another window, an older version, the same model');
assert.match(mismatchMessage, /Close that window, then open this folder again\./,
  'and say the way through, in everyday words');
assert.doesNotMatch(mismatchMessage, /\u2014/,
  'no em dashes in user-facing copy (docs/product-copy-style.md rule 3)');
assert.doesNotMatch(mismatchMessage, /AI Assistant|engine owner|workspace|pipe/,
  'no engine vocabulary in primary copy (rule 2), and "AI Assistant" was retired (rule 7)');

// The engine says the same thing from its own side, for the same event.
const program = readFileSync(resolve(root, '..', 'Semanticus.Engine', 'Program.cs'), 'utf8');
const engineRefusal = /FATAL: ([^"]*already serving this folder[^"]*)"/.exec(program);
assert.ok(engineRefusal, 'the engine keeps its own refusal for a different build owning the folder');
assert.doesNotMatch(engineRefusal[1], /restart/i,
  'the engine-side refusal must not suggest restarting either');
assert.doesNotMatch(engineRefusal[1], /AI Assistant/,
  '"AI Assistant" was retired; it is "your assistant"');

console.log('engine-lifetime: ok');
