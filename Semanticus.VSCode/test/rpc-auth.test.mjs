import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { PassThrough } from 'node:stream';

const require = createRequire(import.meta.url);
const {
  getUiChallenge,
  RpcHandshakeRejectedError,
  rpcChallengeKey,
  rpcRolePreamble,
  waitForRpcHandshake,
} = require('../out/rpcAuth.js');

class FakeSecrets {
  values = new Map();
  writes = [];
  async get(key) { return this.values.get(key); }
  async store(key, value) { this.values.set(key, value); this.writes.push({ key, value }); }
}

const secrets = new FakeSecrets();
const first = await getUiChallenge(secrets, 'C:\\Models\\Sales');
const second = await getUiChallenge(secrets, 'C:\\Models\\Sales');
assert.equal(first, second, 'one workspace must reuse the SecretStorage challenge across windows');
assert.match(first, /^[A-Za-z0-9_-]{43}$/, 'a new challenge must contain 256 bits encoded without line breaks');
assert.equal(secrets.writes.length, 1, 'a valid stored challenge must not be rotated on every attach');

secrets.values.set(rpcChallengeKey('C:\\Models\\Sales'), 'corrupt');
const repaired = await getUiChallenge(secrets, 'C:\\Models\\Sales');
assert.notEqual(repaired, 'corrupt', 'an invalid stored challenge must be replaced before it reaches the pipe');

assert.equal(
  rpcChallengeKey('C:\\Models\\Sales', 'win32'),
  rpcChallengeKey('c:\\models\\sales', 'win32'),
  'Windows workspace identity must be case-insensitive');
assert.notEqual(
  rpcChallengeKey('/Models/Sales', 'linux'),
  rpcChallengeKey('/models/sales', 'linux'),
  'Unix workspace identity must preserve case');

assert.equal(rpcRolePreamble('agent'), 'SEMANTICUS-RPC/1 agent\n');
assert.equal(rpcRolePreamble('human', first), `SEMANTICUS-RPC/1 human ${first}\n`);
assert.throws(() => rpcRolePreamble('human', 'short'), /invalid/i);

const accepted = new PassThrough();
const acceptedResult = waitForRpcHandshake(accepted, 1000);
accepted.write('SEMANTICUS-RPC/1 accepted\nContent-Length: 2\r\n\r\n{}');
await acceptedResult;
assert.equal(
  accepted.read()?.toString(),
  'Content-Length: 2\r\n\r\n{}',
  'bytes after the acknowledgement must remain available to JSON-RPC');

const rejected = new PassThrough();
const rejectedResult = waitForRpcHandshake(rejected, 1000);
rejected.write('SEMANTICUS-RPC/1 rejected\n');
await assert.rejects(rejectedResult, RpcHandshakeRejectedError);

const silent = new PassThrough();
const silentResult = waitForRpcHandshake(silent, 1000);
silent.end();
await assert.rejects(silentResult, /disconnected before confirming/i);

console.log('RPC role authentication tests passed');
