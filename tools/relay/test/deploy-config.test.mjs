// The deploy configuration of the two workers, checked against what the shipped engine and app expect.
//
// The relay's address, the billing address, the licence public key and the shared status namespace are
// each written in two places: once in the product and once in a wrangler.toml. Every one of them fails
// SILENTLY when the two copies disagree: the app talks to an address nothing answers, a real licence is
// refused, or the relay reads an empty namespace and serves a cancelled subscriber. So each pair is
// compared here, offline, and tools/golive/golive.mjs (the go-live helper) is driven on a scratch copy.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { PATHS, SECRETS, kvId, main, problems, routePatterns, varValue } from '../../golive/golive.mjs';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const read = (rel) => readFileSync(resolve(REPO, rel), 'utf8');
const relay = read(PATHS.relay);
const billing = read(PATHS.billing);
const engineKey = /EmbeddedPublicKey\s*=\s*"([^"]+)"/.exec(read(PATHS.engineKey))[1];

test('the relay is routed at the address the engine calls by default', () => {
    const base = /DefaultRelayBaseUrl\s*=\s*"([^"]+)"/.exec(read(PATHS.engineRelay))[1];
    assert.deepEqual(routePatterns(relay), [new URL(base).hostname]);
});

test('billing is routed at the address the app ships as its default', () => {
    const app = JSON.parse(read(PATHS.extensionPackage));
    assert.deepEqual(routePatterns(billing), [new URL(app.semanticusBilling.productionUrl).hostname]);
    assert.equal(app.contributes.configuration.properties['semanticus.billingUrl'].default, app.semanticusBilling.productionUrl);
});

test('both workers verify licences with the engine public key', () => {
    assert.equal(varValue(relay, 'RELAY_PUBLIC_KEY'), engineKey);
    assert.equal(varValue(billing, 'LICENSE_PUBLIC_KEY'), engineKey);
});

test('the relay reads subscription status from the namespace billing writes', () => {
    assert.equal(kvId(relay, 'STATUS_KV'), kvId(billing, 'BILLING_KV'));
});

test('every secret each worker reads is named in its wrangler.toml and never given a value there', () => {
    for (const [worker, toml] of [['relay', relay], ['billing', billing]]) {
        for (const { name } of SECRETS[worker]) {
            assert.match(toml, new RegExp(`^#\\s+${name}\\b`, 'm'), `${name} is listed by name in the ${worker} file`);
            assert.doesNotMatch(toml, new RegExp(`^${name}\\s*=`, 'm'), `${name} must never be a var`);
        }
    }
    // And the relay never holds a billing or signing credential: that separation is its whole security case.
    for (const forbidden of ['STRIPE_SECRET_KEY', 'SEMANTICUS_SIGNING_KEY']) {
        assert.ok(!SECRETS.relay.some((s) => s.name === forbidden));
    }
});

test('the relay carries the caps Kane chose on 1 Oct: USD 2 a user a month, 47.6M input tokens at USD 0.042 per million', () => {
    assert.equal(varValue(relay, 'RELAY_LICENCE_COUNT'), '80');
    assert.equal(varValue(relay, 'RELAY_LICENCE_TOKENS'), '47600000');
    const usd = Number(varValue(relay, 'RELAY_LICENCE_TOKENS')) * 0.042 / 1_000_000;
    assert.ok(Math.abs(usd - 2) < 0.01, 'USD ' + usd);
});

test('set-kv writes the billing id into both files and check then passes', () => {
    const scratch = mkdtempSync(join(tmpdir(), 'golive-'));
    try {
        for (const rel of Object.values(PATHS)) {
            mkdirSync(dirname(join(scratch, rel)), { recursive: true });
            cpSync(resolve(REPO, rel), join(scratch, rel));
            // The repo now carries the live ids (go-live, 9 Oct); put placeholders back so the copy is a fresh checkout.
            const file = join(scratch, rel);
            writeFileSync(file, readFileSync(file, 'utf8')
                .replace('id = "9d873b50cd0e4e17aa656b966fc5eda7"', 'id = "REPLACE_AT_DEPLOY"')
                .replace('id = "c36e4692c7394434ba7aa0b687e3d3b1"', 'id = "REPLACE_WITH_BILLING_KV_NAMESPACE_ID"'));
        }
        const lines = [];
        const log = (line) => lines.push(line);
        assert.equal(main(['check'], scratch, log), 1, 'placeholders are still in, so check refuses');

        const billingId = 'a'.repeat(32);
        const relayId = 'b'.repeat(32);
        assert.equal(main(['set-kv', '--billing', 'not-an-id', '--relay', relayId], scratch, log), 2);
        assert.equal(main(['set-kv', '--billing', billingId, '--relay', billingId], scratch, log), 2, 'one namespace cannot be both');
        assert.equal(main(['set-kv', '--billing', billingId, '--relay', relayId], scratch, log), 0);

        const r = readFileSync(join(scratch, PATHS.relay), 'utf8');
        const b = readFileSync(join(scratch, PATHS.billing), 'utf8');
        assert.equal(kvId(r, 'RELAY_KV'), relayId);
        assert.equal(kvId(r, 'STATUS_KV'), billingId);
        assert.equal(kvId(b, 'BILLING_KV'), billingId);
        assert.deepEqual(problems(scratch), []);
        lines.length = 0;
        assert.equal(main(['check'], scratch, log), 0);
        assert.ok(lines.some((l) => l.includes('JEV_HOUSE_KEY')), 'a ready check lists the secrets by name');
        assert.ok(!lines.join('\n').match(/sk_(test|live)_|whsec_/), 'no secret value is ever printed');
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
