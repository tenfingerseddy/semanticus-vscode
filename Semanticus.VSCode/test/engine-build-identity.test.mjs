// F-241 (Kane, 23 September, the twelfth drop). Kane installed a new build over the old one: same version,
// 1.2.1, same install path. Every new VS Code window then ATTACHED to the engine the previous build had
// started. engine.json showed the same ExePath, the log said "Attaching to running engine ... Engine adopted by
// this window", and he tested new screens on old engine code. The only identity a window compared was the
// executable PATH, and an in-place reinstall does not change the path.
//
// So every engine now records a BUILD identity (a hash of the Semanticus assemblies beside it) in engine.json
// and answers it over its RPC door, and a window adopts a running engine only when that identity is the one it
// would launch itself. A different build is replaced, but never while it holds unsaved work: that engine is
// kept, adopted so its grace cannot run out under the person, and the F-239 Save offer is shown.
//
// Two fake engines of the same version and different builds carry the whole rule below.
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  engineBuildIdentity, settleRunningBuild, OLDER_BUILD_UNSAVED, OLDER_BUILD_UNCHECKED, OLDER_OWNER_ACTIONS,
} from '../out/engineResolution.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

// ---- (1) the identity, pinned against the engine's own recipe ------------------------------------------
// Semanticus.Tests/EngineBuildIdentityTests.cs pins the SAME vector. If the two ever disagree, every window
// would read every engine as a different build and replace it, so drift must fail on both sides.
const scratch = mkdtempSync(join(tmpdir(), 'semanticus-build-id-'));
try {
  const same = join(scratch, 'semanticus-vscode-1.2.1', 'engine');
  mkdirSync(same, { recursive: true });
  writeFileSync(join(same, 'Semanticus.Engine.dll'), 'engine-v1');
  writeFileSync(join(same, 'Semanticus.Core.dll'), 'core');
  writeFileSync(join(same, 'Newtonsoft.Json.dll'), 'not ours');
  writeFileSync(join(same, 'Semanticus.Engine.pdb'), 'symbols are not the build');
  const first = engineBuildIdentity(same);
  assert.equal(first, '15872dc41088e854ce60d35e09d280fd97b60d0107a5cdb0e026829106efa1ce',
    'the extension computes the same identity the engine records (pinned in EngineBuildIdentityTests.cs)');

  // The in-place reinstall: same version folder, same path, same file names, new bytes.
  writeFileSync(join(same, 'Semanticus.Engine.dll'), 'engine-v2');
  const second = engineBuildIdentity(same);
  assert.equal(second, 'f9a787070b482ff622d4de6ad7241dfc083131ea710c43f4d1c8930dfc199793');
  assert.notEqual(first, second, 'same version and path with different bytes is a different build');

  const empty = join(scratch, 'empty');
  mkdirSync(empty);
  writeFileSync(join(empty, 'Newtonsoft.Json.dll'), 'not ours');
  assert.equal(engineBuildIdentity(empty), undefined, 'a folder with no Semanticus assembly has no identity');
  assert.equal(engineBuildIdentity(join(scratch, 'missing')), undefined, 'a missing folder has no identity');
} finally {
  rmSync(scratch, { recursive: true, force: true });
}

// ---- (2) two fake engines, same version, different builds -----------------------------------------------
const OLD = 'build-old';
const NEW = 'build-new';
function fakeEngine(build, unsaved) {
  const asked = { build: 0, unsaved: 0 };
  return {
    asked,
    build: async () => { asked.build++; return build; },
    hasUnsavedChanges: async () => { asked.unsaved++; return unsaved; },
  };
}

{
  const running = fakeEngine(NEW, true);
  const s = await settleRunningBuild(NEW, running, false);
  assert.equal(s.verdict, 'adopt', 'the same build is adopted, as before');
  assert.equal(running.asked.unsaved, 0, 'the same build is never asked about unsaved work: nothing will stop it');
}
{
  const running = fakeEngine(OLD, false);
  const s = await settleRunningBuild(NEW, running, false);
  assert.equal(s.verdict, 'replace', 'a different build with no unsaved work is replaced, not adopted (F-241)');
  assert.equal(running.asked.unsaved, 1, 'replacing is only decided after asking about unsaved work');
}
{
  const s = await settleRunningBuild(NEW, fakeEngine(OLD, true), false);
  assert.equal(s.verdict, 'keep-for-unsaved', 'a different build holding unsaved work is never stopped');
  assert.equal(s.unsaved, true);
}
{
  const s = await settleRunningBuild(NEW, fakeEngine(OLD, undefined), false);
  assert.equal(s.verdict, 'keep-for-unsaved', 'an engine that could not be asked keeps running: stopping it could lose work');
  assert.equal(s.unsaved, undefined);
}
{
  // An engine from before this fix answers no build at all. It is certainly not this build, since this build
  // always reports one, so it is judged exactly like a different build.
  assert.equal((await settleRunningBuild(NEW, fakeEngine(undefined, false), false)).verdict, 'replace');
  assert.equal((await settleRunningBuild(NEW, fakeEngine('', false), false)).verdict, 'replace');
}
{
  // A window that cannot compute its own build cannot judge anyone else's: attach as before.
  const running = fakeEngine(OLD, false);
  assert.equal((await settleRunningBuild(undefined, running, false)).verdict, 'adopt');
  assert.equal(running.asked.unsaved, 0);
}
{
  // The engine this window just started is never replaced, whatever it reports, or a recipe drift between the
  // two languages would turn into a loop of spawn, replace, spawn.
  assert.equal((await settleRunningBuild(NEW, fakeEngine(OLD, false), true)).verdict, 'adopt');
}

// ---- (3) the copy -------------------------------------------------------------------------------------
for (const line of [OLDER_BUILD_UNSAVED, OLDER_BUILD_UNCHECKED]) {
  assert.ok(!/\u2014|\u2013/.test(line), 'no em or en dashes in user copy');
  assert.ok(!/engine\.json|build identity|pid|adopt/i.test(line), 'the notice never speaks engine jargon');
  assert.match(line, /Save/, 'the notice names the Save it offers');
}
assert.deepEqual([...OLDER_OWNER_ACTIONS], ['Save'], 'the older-build notice reuses the F-239 Save button');

// ---- (4) the wiring in extension.ts ------------------------------------------------------------------
function slice(text, startPattern, endPattern, what) {
  const start = text.search(startPattern);
  assert.notEqual(start, -1, `could not find the start of ${what}`);
  const rest = text.slice(start);
  const end = rest.slice(1).search(endPattern);
  return end === -1 ? rest : rest.slice(0, end + 1);
}
const attempt = slice(source, /async function connectEngineAttempt\(/, /\n}\n/, 'connectEngineAttempt');
const settleAt = attempt.indexOf('settleRunningBuild(');
const adoptAt = attempt.indexOf('await adoptEngine(connected)');
assert.ok(settleAt > 0, 'the connect path settles the running build');
assert.ok(adoptAt > settleAt, 'the build is settled BEFORE anything is adopted');
assert.match(attempt, /verdict === 'replace'[\s\S]*?replaceCheckedOwner\([\s\S]*?waitForReplacedOwner\([\s\S]*?ensureEngine\([\s\S]*?connectPipe\(/,
  'a replaced build is stopped, and this window starts and joins its own engine');
assert.match(attempt, /verdict === 'keep-for-unsaved'[\s\S]*?offerSaveForOlderBuild\(/,
  'a kept build holding unsaved work gets the Save offer');
assert.match(source, /spawned\?\.pid[\s\S]{0,120}readEngineInfo\(ws\)\?\.pid/,
  'the window knows when the running engine is the one it just started');

const expected = slice(source, /function expectedEngineBuild\(/, /\n}\n/, 'expectedEngineBuild');
assert.match(expected, /engineBuildIdentity\(path\.dirname\(/,
  'the expected build is read from the folder this window would launch from');
assert.doesNotMatch(source, /let\s+\w*[Ee]xpectedBuild\w*\s*[:=]/,
  'the expected build is recomputed per connect, never cached: an in-place reinstall changes it under a running window');

const ask = slice(source, /async function runningEngineBuild\(/, /\n}\n/, 'runningEngineBuild');
assert.match(ask, /sendRequest<string>\('buildIdentity'\)/, 'the running engine is asked for its build over its RPC door');
assert.match(ask, /MethodNotFound/, 'an engine without the method is an older build, not a transport failure');
assert.match(ask, /readEngineInfo\(ws\)\?\.buildId/, 'a transport failure falls back to the build engine.json records');

const readInfo = slice(source, /function readEngineInfo\(/, /\n}\n/, 'readEngineInfo');
assert.match(readInfo, /buildId: o\.buildId \?\? o\.BuildId/, 'engine.json BuildId is read in either key case');

// ---- (5) F-244 (Sol 6, slice 5 review): replace the CHECKED owner, never a pid from a fresh record ----------
// The window checked one engine for unsaved work, then stopDifferentBuild reread engine.json and killed whatever
// pid it named. Another window can replace the checked engine in between, with unsaved work of its own, and that
// engine is the one that died. Now the window asks the engine it checked, over the connection it checked it on,
// to stop itself only while clean; an engine too old to be asked is stopped only if the record still names the
// very owner that was checked.
{
  const replace = slice(attempt, /verdict === 'replace'/, /\n        } else if \(build\.verdict === 'keep-for-unsaved'\)/, 'the replace branch');
  assert.doesNotMatch(source, /async function stopDifferentBuild\(/,
    'the stop that killed whatever pid a fresh engine.json named is gone');
  assert.match(replace, /replaceCheckedOwner\(/, 'the replace branch goes through the one checked-owner rule');
  assert.match(replace, /stopIfClean: \(\) => askStopIfClean\(joined\)/, 'the stop is asked of the connection that was checked');
  assert.doesNotMatch(replace, /process\.kill\(\s*(existing|readEngineInfo)/, 'no pid read from a fresh record is killed');
}

const { replaceCheckedOwner } = await import('../out/engineResolution.js');
assert.equal(typeof replaceCheckedOwner, 'function', 'engineResolution exports replaceCheckedOwner');

function ownerDoor({ answer, unsaved = false, record }) {
  const calls = { stop: 0, killed: [] };
  return {
    calls,
    stopIfClean: async () => { calls.stop++; return answer; },
    hasUnsavedChanges: async () => unsaved,
    currentRecord: () => record,
    kill: (pid) => { calls.killed.push(pid); },
  };
}
const checked = { pid: 4100, startedUtc: '2026-09-24T01:00:00.0000000Z' };
const otherOwner = { pid: 4200, startedUtc: '2026-09-24T01:00:05.0000000Z' };
{
  // The race Sol named, on a build that can be asked: another window's engine now holds the record. The checked
  // engine is asked to stop itself; the other engine is never touched.
  const door = ownerDoor({ answer: { stopping: true, pid: checked.pid }, record: otherOwner });
  const outcome = await replaceCheckedOwner(checked, door);
  assert.deepEqual(outcome, { kind: 'stopping', pid: checked.pid }, 'the checked engine stops itself');
  assert.deepEqual(door.calls.killed, [], 'nothing is killed from outside, least of all the new owner');
}
{
  // The same race on an engine too old to be asked: the record changed between the check and the stop, and the
  // new owner survives.
  const door = ownerDoor({ answer: 'unsupported', record: otherOwner });
  const outcome = await replaceCheckedOwner(checked, door);
  assert.deepEqual(outcome, { kind: 'changed-hands' }, 'a record naming another owner stops nothing');
  assert.deepEqual(door.calls.killed, [], 'the new owner, and its unsaved work, survives');
}
{
  // An older engine that is still the checked owner and still clean is stopped, by the pid that was checked.
  const door = ownerDoor({ answer: 'unsupported', record: { ...checked } });
  assert.deepEqual(await replaceCheckedOwner(checked, door), { kind: 'stopping', pid: checked.pid });
  assert.deepEqual(door.calls.killed, [checked.pid]);
}
{
  // Same pid, different start: the pid was reused by a new engine. Not the checked owner.
  const door = ownerDoor({ answer: 'unsupported', record: { pid: checked.pid, startedUtc: otherOwner.startedUtc } });
  assert.deepEqual(await replaceCheckedOwner(checked, door), { kind: 'changed-hands' });
  assert.deepEqual(door.calls.killed, []);
}
{
  // The checked engine picked up unsaved work after the first check: it refuses, and it is kept with the Save offer.
  const door = ownerDoor({ answer: { stopping: false, pid: checked.pid, reason: 'unsaved' }, record: { ...checked } });
  assert.deepEqual(await replaceCheckedOwner(checked, door), { kind: 'kept', unsaved: true });
  assert.deepEqual(door.calls.killed, []);
  const older = ownerDoor({ answer: 'unsupported', unsaved: true, record: { ...checked } });
  assert.deepEqual(await replaceCheckedOwner(checked, older), { kind: 'kept', unsaved: true });
  assert.deepEqual(older.calls.killed, []);
}
{
  // No answer in time is not a clean answer: nothing stops, and the engine is kept.
  const silent = ownerDoor({ answer: undefined, record: { ...checked } });
  assert.deepEqual(await replaceCheckedOwner(checked, silent), { kind: 'kept', unsaved: undefined });
  assert.deepEqual(silent.calls.killed, []);
  // An older engine with no record of who was checked cannot be matched, so it is not killed.
  const unknown = ownerDoor({ answer: 'unsupported', record: { ...checked } });
  assert.deepEqual(await replaceCheckedOwner(undefined, unknown), { kind: 'changed-hands' });
  assert.deepEqual(unknown.calls.killed, []);
}

console.log('engine-build-identity: ok');
