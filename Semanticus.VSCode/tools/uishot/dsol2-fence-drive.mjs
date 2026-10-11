// End-to-end proof of C1 (drafts-sol round 2): the person's door refuses a write whose page was showing another model.
// The REAL pieces on both ends: the extension's own compiled modules (out/modelFence.js, out/rpcAuth.js) over real vscode-jsonrpc, and a real LocalEngine + RpcServer on a pipe
// (the host of Semanticus.Tests/VerifyDraftIdentityRound3Tests.cs, VerifyDrafts3StudioHost). Nothing between them is mocked or re-implemented: the request leaves the extension's writer
// with `modelIdentity` as a top-level member and the engine's PersonFence reads it.
//
// ROUND 3 (the deliberate contract change): the extension's writer now says `namesModel: true` on every request, and the engine refuses a write from it that names NO model while a model is
// open (the check's T1). So the setup writes below name the model they are for, and the control that used to assert "a request that names no model is written as before" asserts the
// opposite for the extension's writer, and keeps the old behaviour for a client that never says it names a model (a second connection with a plain writer: an older extension).
//
//   TMPDIR=/tmp/dsfz node tools/uishot/dsol2-fence-drive.mjs          (build Semanticus.Tests Debug first; npm run compile in Semanticus.VSCode)
import { spawn } from 'node:child_process';
import net from 'node:net';
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const repoRoot = resolve(webRoot, '..');
const require = createRequire(join(webRoot, 'package.json'));
const { createMessageConnection, StreamMessageReader, StreamMessageWriter, ParameterStructures } = require('vscode-jsonrpc/node');
const { FencedMessageWriter, withModelIdentity } = require(join(webRoot, 'out', 'modelFence.js'));
const { rpcRolePreamble, waitForRpcHandshake } = require(join(webRoot, 'out', 'rpcAuth.js'));

const BASE = join(process.env.HOME, '.cache', `dsol2-fence-${process.pid}`);
rmSync(BASE, { recursive: true, force: true }); mkdirSync(BASE, { recursive: true });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const check = (name, ok, detail = '') => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  ' + detail}`); };

const dir = join(BASE, 'host'); mkdirSync(dir, { recursive: true });
const env = { ...process.env, VD3_HOST_DIR: dir, VD3_SNAP_ROOT: join(BASE, 'snapshots'), BROWSER: 'true', AZURE_CONFIG_DIR: '/tmp/no-az-login', MSBUILDDISABLENODEREUSE: '1', DOTNET_CLI_USE_MSBUILD_SERVER: '0' };
delete env.OUTDIR;
const child = spawn('dotnet', ['test', join(repoRoot, 'Semanticus.Tests', 'Semanticus.Tests.csproj'), '--no-build', '-c', 'Debug', '--filter', 'FullyQualifiedName~VerifyDrafts3StudioHost'], { env, cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
const log = []; child.stdout.on('data', (d) => log.push(String(d))); child.stderr.on('data', (d) => log.push(String(d)));
let conn, socket;
async function finish(code) {
  try { conn?.dispose(); socket?.destroy(); } catch { /* gone */ }
  writeFileSync(join(dir, 'stop'), '');
  for (let i = 0; i < 100 && child.exitCode === null && child.signalCode === null; i++) await wait(200);
  if (child.exitCode === null && child.signalCode === null) try { process.kill(-child.pid, 'SIGTERM'); } catch { /* gone */ }
  rmSync(BASE, { recursive: true, force: true });
  const failed = results.filter((r) => !r.ok).length;
  console.log(`\n${results.length - failed} PASS, ${failed} FAIL`);
  process.exit(code ?? (failed ? 1 : 0));
}
try {
  const pj = join(dir, 'pipe.json');
  for (let i = 0; !existsSync(pj); i++) { if (child.exitCode !== null) throw new Error('host exited early: ' + log.join('')); if (i > 1500) throw new Error('no pipe.json'); await wait(200); }
  const info = JSON.parse(readFileSync(pj, 'utf8'));

  // the extension's own connection: the compiled handshake and the compiled fenced writer over real vscode-jsonrpc
  socket = net.connect(info.socket);
  await new Promise((res, rej) => { socket.once('connect', res); socket.once('error', rej); });
  const accepted = waitForRpcHandshake(socket);
  socket.write(rpcRolePreamble('human', info.challenge), 'utf8');
  await accepted;
  conn = createMessageConnection(new StreamMessageReader(socket), new FencedMessageWriter(new StreamMessageWriter(socket)));
  conn.listen(); socket.resume();
  // what studioRelayHandler does for a page's request: byPosition params, the page's model named around the one send
  const asPage = (modelIdentity, method, ...params) => withModelIdentity(modelIdentity, () => (params.length ? conn.sendRequest(method, ParameterStructures.byPosition, ...params) : conn.sendRequest(method)));
  const refused = async (p) => { try { await p; return null; } catch (e) { return String(e?.message ?? e); } };

  // two models, the same file twice, so they start alike (Sol's F1 scenario)
  const A = join(BASE, 'A.bim'), B = join(BASE, 'B.bim');
  copyFileSync(info.bim, A); copyFileSync(info.bim, B);
  await asPage(undefined, 'open', A, true);
  const idA = (await asPage(undefined, 'sessionInfo')).modelIdentity;
  await asPage(idA, 'setCompatibilityLevel', 1600, 'human');   // round 3: a setup write names the model it is for, like every write of the extension's writer
  await asPage(undefined, 'open', B, true);
  const idB = (await asPage(undefined, 'sessionInfo')).modelIdentity;
  await asPage(idB, 'setCompatibilityLevel', 1600, 'human');
  check('two models open one after the other have two identities, and the engine names B now', !!idA && !!idB && idA !== idB && idA.startsWith('file:'), `${idA} / ${idB}`);
  const instr = async () => (await asPage(undefined, 'getAiInstructions')).instructions ?? '';
  const bBefore = await instr();

  // D1: the Save typed for A lands while B is open
  let err = await refused(asPage(idA, 'setAiInstructions', 'Words typed for A', null, 'human', bBefore));
  check('D1: AI instructions typed for A and saved while B is open are refused', !!err && /another model/.test(err), String(err));
  check('D1: B keeps its own instructions', (await instr()) === bBefore);

  // the rest of the class, by the ops the editors use: each refused, B unchanged
  const measures = await asPage(undefined, 'listMeasures');
  const m = measures[0];
  const tries = [
    ['setDax', [m.ref, '1', 'human']],
    ['setDescription', [m.ref, 'Words typed for A', 'human']],
    ['setObjectProperty', [m.ref, 'Description', 'Words typed for A', 'human']],
    ['setObjectProperties', [[m.ref], 'Description', 'Words typed for A', 'human']],
    ['setMeasureFormatExpression', [m.ref, '"0"', 'human']],
    ['renameObject', [m.ref, 'Renamed for A', 'human']],
    ['deleteObject', [m.ref, 'human']],
    ['setDocSection', ['model', 'overview', 'Words typed for A', 'human', '']],
    ['setPrimer', ['Words typed for A', 'human']],
    ['addInsight', ['Words typed for A', ['k'], 'insight', 'project', false, 'human']],
    ['setSpec', ['{"name":"A"}', 'human']],
  ];
  for (const [method, params] of tries) {
    const e = await refused(asPage(idA, method, ...params));
    check(`${method} typed for A is refused while B is open`, !!e && /another model/.test(e), String(e));
  }
  const after = await asPage(undefined, 'listMeasures');
  check('nothing of A reached B: B still has its measure under its own name and description', after.length === measures.length && after[0].name === m.name);

  // controls: the matching model writes, a request that names none writes as before, a read for another model is never refused
  const ok = await refused(asPage(idB, 'setAiInstructions', 'Words typed for B', null, 'human', bBefore));
  check('control: the same write named for the model that IS open is written', ok === null && (await instr()) === 'Words typed for B', String(ok));
  // CONTRACT CHANGE (round 3): the extension's writer names a model on every request, so one that names none while a model is open could not say which model it meant. Refused.
  const none = await refused(asPage(undefined, 'setAiInstructions', 'Plain words', null, 'human', 'Words typed for B'));
  check('a request from the extension\'s writer that names no model is refused while a model is open (round 3, was: written as before)', !!none && /another model/.test(none) && (await instr()) === 'Words typed for B', String(none));
  // ...and an older client, one that never says it names a model, keeps today's behaviour: a second connection with a plain writer.
  const oldSocket = net.connect(info.socket);
  await new Promise((res, rej) => { oldSocket.once('connect', res); oldSocket.once('error', rej); });
  const oldAccepted = waitForRpcHandshake(oldSocket);
  oldSocket.write(rpcRolePreamble('human', info.challenge), 'utf8');
  await oldAccepted;
  const older = createMessageConnection(new StreamMessageReader(oldSocket), new StreamMessageWriter(oldSocket));
  older.listen(); oldSocket.resume();
  const olderWrite = await refused(older.sendRequest('setAiInstructions', ParameterStructures.byPosition, 'Plain words', null, 'human', 'Words typed for B'));
  check('control: a client that never says it names a model (an older extension) keeps today\'s behaviour', olderWrite === null && (await instr()) === 'Plain words', String(olderWrite));
  try { older.dispose(); oldSocket.destroy(); } catch { /* gone */ }
  const read = await refused(asPage(idA, 'getAiInstructions'));
  check('control: a read for another model is never refused', read === null, String(read));
  const opened = await refused(asPage(idA, 'open', A, true));
  check('control: opening a model is not fenced (it replaces the session)', opened === null, String(opened));
  const nowA = (await asPage(undefined, 'sessionInfo')).modelIdentity;
  check('and A is the open model after it', nowA === idA);
  await asPage(idA, 'setCompatibilityLevel', 1600, 'human');   // the file was never saved, so A reopens as it was on disk
  // the same request that was refused a moment ago is now for the model that is open
  const sameNow = await refused(asPage(idA, 'setAiInstructions', 'Words typed for A', null, 'human', await instr()));
  check('the write that was refused for A is written once A is the open model', sameNow === null && (await instr()) === 'Words typed for A', String(sameNow));
  await finish();
} catch (e) {
  console.error('DRIVE ERROR', e?.stack ?? e);
  results.push({ name: 'drive ran', ok: false });
  await finish(1);
}
