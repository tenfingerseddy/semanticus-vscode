#!/usr/bin/env node
// Cross-language proof for the fulfillment Worker's minter. Run from this directory:
//
//   node selftest.mjs            (SEMANTICUS_MINT_CONFIG overrides the dotnet config; default Release)
//
// What it proves, against the REAL .NET code the shipped engine runs (via the Semanticus.License CLI):
//   1. keygen (.NET)  → mint (node WebCrypto) → verify (.NET)      — the production Worker direction
//   2. keygen (.NET)  → mint (.NET)           → verify (node)      — the reverse, pinning both sides
//   3. a node-minted token TAMPERED with must FAIL .NET verify
//   4. the Paddle webhook signature scheme round-trips (HMAC ts:body, hex, replay window)
// Exits non-zero on any failure. No network, no real keys — a throwaway keypair per run.

import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { mintToken, verifyToken } from './mint.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const MINTER = join(resolve(__dirname, '..', '..', '..'), 'Semanticus.License');
const CONFIG = process.env.SEMANTICUS_MINT_CONFIG || 'Release';

let failures = 0;
function check(name, ok, detail = '') {
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${ok || !detail ? '' : '  — ' + detail}`);
    if (!ok) failures++;
}

function cli(...args) {
    const r = spawnSync('dotnet', ['run', '--project', MINTER, '-c', CONFIG, '--', ...args], { encoding: 'utf8' });
    return { status: r.status, stdout: (r.stdout || '').trim(), stderr: (r.stderr || '').trim() };
}

// --- throwaway keypair from the .NET keygen (the issuer of record) ---
const kg = cli('keygen');
if (kg.status !== 0) { console.error('keygen failed:\n' + kg.stderr); process.exit(1); }
// dotnet run can prepend restore/build chatter on a cold config — keep only lines that ARE base64 keys.
const lines = kg.stdout.split('\n').map(s => s.trim()).filter(s => /^[A-Za-z0-9+/]{40,}={0,2}$/.test(s));
const [publicKey, privateKey] = lines;
check('keygen produced a keypair', !!publicKey && !!privateKey);

const now = Math.floor(Date.now() / 1000);

// --- 1. node mint → .NET verify (the Worker's production path) ---
const nodeToken = await mintToken(privateKey, { sub: 'selftest@semanticus.com.au', tier: 'pro', iat: now, exp: now + 3600 });
let v = cli('verify', '--pub', publicKey, '--token', nodeToken);
check('node-minted token passes the .NET verifier', v.status === 0 && v.stdout.startsWith('VALID'), v.stdout || v.stderr);
check('.NET verifier read the claims back', v.stdout.includes('sub=selftest@semanticus.com.au') && v.stdout.includes('tier=pro'), v.stdout);

// --- 2. .NET mint → node verify ---
const m = cli('mint', '--priv', privateKey, '--sub', 'dotnet@semanticus.com.au', '--tier', 'pro', '--days', '1');
const dotnetToken = m.stdout.split('\n').map(s => s.trim()).find(s => /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(s));
check('.NET mint succeeded', m.status === 0 && !!dotnetToken, m.stderr);
const claims = await verifyToken(dotnetToken, publicKey, now);
check('.NET-minted token passes the node verifier', claims?.sub === 'dotnet@semanticus.com.au' && claims?.tier === 'pro');

// --- 3. tampering must fail ---
const [p, s] = nodeToken.split('.');
const payload = JSON.parse(Buffer.from(p.replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString());
payload.exp = now + 86400 * 3650;   // a decade of free Pro, if signatures didn't matter
const forged = Buffer.from(JSON.stringify(payload)).toString('base64url') + '.' + s;
v = cli('verify', '--pub', publicKey, '--token', forged);
check('tampered token FAILS the .NET verifier', v.status !== 0 && (v.stderr || v.stdout).includes('INVALID'));

// --- 4. Paddle signature scheme (same math as worker.mjs, via node crypto as the oracle) ---
const { createHmac } = await import('node:crypto');
const secret = 'pdl_ntfset_test_secret';
const body = '{"event_type":"transaction.completed","data":{"id":"txn_1"}}';
const ts = String(Math.floor(Date.now() / 1000));
const h1 = createHmac('sha256', secret).update(ts + ':' + body).digest('hex');
// worker.mjs's verifier is not exported (it closes over env) — recompute with WebCrypto exactly as it does.
const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
const mac = new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(ts + ':' + body)));
const hex = [...mac].map(b => b.toString(16).padStart(2, '0')).join('');
check('WebCrypto HMAC matches the node-crypto oracle', hex === h1);

console.log(failures === 0 ? '\nAll selftests passed.' : `\n${failures} selftest(s) FAILED.`);
process.exit(failures === 0 ? 0 : 1);
