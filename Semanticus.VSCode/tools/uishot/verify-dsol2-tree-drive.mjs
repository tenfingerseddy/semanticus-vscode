// Verifier re-check of m11/drafts-sol round 2 (5 Oct), the builder's join item 2, against a REAL engine: the Model tree's own commands send a write that names no
// model and no session, so a Rename, a Delete or a row filter the person was still typing or confirming for A lands on B's object of the same name once B opens.
// Same host and the same compiled extension modules as dsol2-fence-drive.mjs; the requests below are byte for byte what renameCmd, authorDelete and roleAddRlsCmd send
// (extension.ts) over the extension's own connection (FencedMessageWriter, no withModelIdentity around them). test/verify-drafts-sol-2.test.mjs runs the commands' source.
//
//   TMPDIR=/tmp/vdtre node tools/uishot/verify-dsol2-tree-drive.mjs          (build Semanticus.Tests Debug first; npm run compile in Semanticus.VSCode)
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

const BASE = join(process.env.HOME, '.cache', `vdsol2-tree-${process.pid}`);
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

  // two models with the same objects
  const A = join(BASE, 'A.bim'), B = join(BASE, 'B.bim');
  copyFileSync(info.bim, A); copyFileSync(info.bim, B);
  // the tree's connection IS the extension's conn: a request it sends outside withModelIdentity names nothing
  const tree = (method, ...params) => conn.sendRequest(method, ...params);
  await tree('open', A, true);
  const idA = (await tree('sessionInfo')).modelIdentity;
  const m = (await tree('listMeasures'))[0];
  // the row filter case needs a role in both models (the test model may have none): the same role is made in each, so B holds one of the same name
  if (!(await tree('listRoles')).length) await tree('createRole', 'Readers', null, 'human');
  const roles = await tree('listRoles');
  // B opens while the person is in the input box or the modal (the assistant over MCP; here the same engine call)
  await tree('open', B, true);
  const idB = (await tree('sessionInfo')).modelIdentity;
  if (!(await tree('listRoles')).some((r) => r.name === roles[0]?.name)) await tree('createRole', roles[0].name, null, 'human');
  check('two models with the same objects, B open now', !!idA && !!idB && idA !== idB && (await tree('listMeasures')).some((x) => x.ref === m.ref), `${idA} / ${idB}`);

  // what Studio sends for the same write (named for A) is refused: the fence works where it is used
  const named = await refused(withModelIdentity(idA, () => conn.sendRequest('renameObject', m.ref, 'Renamed on A', 'human')));
  check('control: the same rename named for A (as a Studio page sends it) is refused while B is open', !!named && /another model/.test(named), String(named));

  // renameCmd: conn.sendRequest('renameObject', n.ref, newName, 'human')
  const renamed = await refused(tree('renameObject', m.ref, 'Renamed on A', 'human'));
  const afterRename = await tree('listMeasures');
  check('tree Rename typed for A is NOT applied to B', !afterRename.some((x) => x.name === 'Renamed on A'), `rename answered ${renamed ?? 'success'}; B now has "Renamed on A"`);

  // roleAddRlsCmd: conn.sendRequest('setTablePermission', n.name, table, filter, 'human', existing)
  if (roles.length) {
    const role = roles[0];
    const table = 'table:' + m.ref.slice('measure:'.length).split('/')[0];
    const existing = role.tableFilters?.find((f) => 'table:' + f.table === table)?.filterExpression ?? '';
    const rls = await refused(tree('setTablePermission', role.name, table, 'FALSE()', 'human', existing));
    const after = (await tree('listRoles')).find((r) => r.name === role.name);
    check('tree Add Row-Level Filter typed for A is NOT written into B', !(after?.tableFilters ?? []).some((f) => f.filterExpression === 'FALSE()'), `setTablePermission answered ${rls ?? 'success'}; B's role ${role.name} now filters ${table} with FALSE()`);
  } else check('the model has a role for the row filter case', false, 'no role in the test model');

  // authorDelete: conn.sendRequest('deleteObject', refs[0], 'human')
  const victim = (await tree('listMeasures')).find((x) => x.ref !== m.ref) ?? m;
  const del = await refused(tree('deleteObject', victim.ref, 'human'));
  const afterDelete = await tree('listMeasures');
  check('tree Delete confirmed for A does NOT delete B\'s object of the same name', afterDelete.some((x) => x.ref === victim.ref), `delete answered ${del ?? 'success'}; ${victim.ref} is gone from B`);
  await finish();
} catch (e) {
  console.error('DRIVE ERROR', e?.stack ?? e);
  results.push({ name: 'drive ran', ok: false });
  await finish(1);
}
