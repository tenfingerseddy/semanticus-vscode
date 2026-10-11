#!/usr/bin/env node
// Go-live helper for the two Cloudflare workers: the Jev relay (tools/relay) and billing
// (tools/fulfillment/worker). Plain Node, no dependencies, touches no account and holds no secret.
//
//   node tools/golive/golive.mjs check
//       Read-only. Says what still stands between the two wrangler.toml files and a deploy: placeholders
//       left in, the relay reading a different status namespace from the one billing writes, a public key
//       that is not the engine's, or a route that is not the address the app and the engine ship with.
//       Exits 1 while anything is left.
//
//   node tools/golive/golive.mjs set-kv --billing <BILLING_KV id> --relay <RELAY_KV id>
//       Writes the two namespace ids that `npx wrangler kv namespace create` printed into both files. The
//       billing id goes in TWICE on purpose: billing writes subscription status to BILLING_KV and the relay
//       reads the same namespace as STATUS_KV. Pasting it by hand into only one file is the silent failure
//       this command exists to prevent: a wrong namespace reads as empty, and empty reads as "not cancelled".
//
// Secrets are never read, printed or written here. They go in with `npx wrangler secret put <NAME>`.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

export const PATHS = {
    relay: 'tools/relay/wrangler.toml',
    billing: 'tools/fulfillment/worker/wrangler.toml',
    engineKey: 'Semanticus.Engine/Entitlement/LicenseEntitlement.cs',
    engineRelay: 'Semanticus.Engine/Decisions/JevDecisionOracle.cs',
    extensionPackage: 'Semanticus.VSCode/package.json',
};

/** Secrets each worker needs, by NAME. `required` ones must be set before real traffic. */
export const SECRETS = {
    relay: [{ name: 'JEV_HOUSE_KEY', required: true, what: 'the Jev (Typesafe) vendor key' }],
    billing: [
        { name: 'STRIPE_SECRET_KEY', required: true, what: 'Stripe key for the mode in STRIPE_MODE' },
        { name: 'STRIPE_WEBHOOK_SECRET', required: true, what: 'the /stripe/webhook endpoint signing secret' },
        { name: 'SEMANTICUS_SIGNING_KEY', required: true, what: 'licence signing private key, pair of the engine public key' },
        { name: 'PADDLE_WEBHOOK_SECRET', required: false, what: 'only while the old Paddle checkout still sells' },
        { name: 'PADDLE_API_KEY', required: false, what: 'only while the old Paddle checkout still sells' },
        { name: 'ADMIN_KEY', required: false, what: 'bearer for GET /pending (Paddle records)' },
        { name: 'RESEND_API_KEY', required: false, what: 'licence email for Paddle sales; optional' },
    ],
};

const read = (repo, rel) => readFileSync(resolve(repo, rel), 'utf8');

/** The id of one [[kv_namespaces]] entry, by binding. */
export function kvId(toml, binding) {
    const re = new RegExp(`\\[\\[kv_namespaces\\]\\]\\s*binding\\s*=\\s*"${binding}"\\s*id\\s*=\\s*"([^"]*)"`);
    const m = re.exec(toml);
    return m ? m[1] : null;
}

export function setKvId(toml, binding, id) {
    const re = new RegExp(`(\\[\\[kv_namespaces\\]\\]\\s*binding\\s*=\\s*"${binding}"\\s*id\\s*=\\s*")[^"]*(")`);
    if (!re.test(toml)) throw new Error(`no [[kv_namespaces]] entry with binding ${binding}`);
    return toml.replace(re, `$1${id}$2`);
}

export const varValue = (toml, name) => (new RegExp(`^${name}\\s*=\\s*"([^"]*)"`, 'm').exec(toml) || [])[1] ?? null;
export const routePatterns = (toml) => [...toml.matchAll(/pattern\s*=\s*"([^"]+)"\s*,\s*custom_domain\s*=\s*true/g)].map((m) => m[1]);

/** Everything still standing between the files and a deploy, as plain sentences. Empty means ready. */
export function problems(repo = REPO) {
    const relay = read(repo, PATHS.relay);
    const billing = read(repo, PATHS.billing);
    const engineKey = /EmbeddedPublicKey\s*=\s*"([^"]+)"/.exec(read(repo, PATHS.engineKey))[1];
    const engineRelay = /DefaultRelayBaseUrl\s*=\s*"([^"]+)"/.exec(read(repo, PATHS.engineRelay))[1];
    const appBilling = JSON.parse(read(repo, PATHS.extensionPackage)).semanticusBilling.productionUrl;
    const out = [];
    const idLooksReal = (id) => /^[0-9a-f]{32}$/.test(id ?? '');

    for (const [file, toml, binding] of [
        [PATHS.relay, relay, 'RELAY_KV'], [PATHS.relay, relay, 'STATUS_KV'],
        [PATHS.billing, billing, 'BILLING_KV'], [PATHS.billing, billing, 'FULFILL_KV'],
    ]) {
        if (!idLooksReal(kvId(toml, binding))) out.push(`${file}: ${binding} has no real namespace id yet (run set-kv).`);
    }
    if (kvId(relay, 'STATUS_KV') !== kvId(billing, 'BILLING_KV')) {
        out.push('The relay\'s STATUS_KV is not the same namespace as billing\'s BILLING_KV, so the relay would never see a cancellation.');
    }
    if (varValue(relay, 'RELAY_PUBLIC_KEY') !== engineKey) out.push(`${PATHS.relay}: RELAY_PUBLIC_KEY is not the engine's public key.`);
    if (varValue(billing, 'LICENSE_PUBLIC_KEY') !== engineKey) out.push(`${PATHS.billing}: LICENSE_PUBLIC_KEY is not the engine's public key.`);
    if (!routePatterns(relay).includes(new URL(engineRelay).hostname)) out.push(`${PATHS.relay}: no custom domain for ${new URL(engineRelay).hostname}.`);
    if (!routePatterns(billing).includes(new URL(appBilling).hostname)) out.push(`${PATHS.billing}: no custom domain for ${new URL(appBilling).hostname}.`);
    if (Number(varValue(relay, 'RELAY_LICENCE_COUNT') ?? 0) < 1) out.push(`${PATHS.relay}: RELAY_LICENCE_COUNT is 0, so the relay would refuse every request.`);
    for (const [file, toml] of [[PATHS.relay, relay], [PATHS.billing, billing]]) {
        if (/REPLACE_/.test(toml.replace(/^\s*#.*$/gm, ''))) out.push(`${file}: a REPLACE_ placeholder is still in a live line.`);
    }
    return out;
}

function arg(argv, name) {
    const i = argv.indexOf(name);
    return i >= 0 ? argv[i + 1] : undefined;
}

export function main(argv, repo = REPO, log = console.log) {
    const [command] = argv;
    if (command === 'check') {
        const list = problems(repo);
        if (list.length === 0) {
            log('Ready to deploy. Secrets still go in by name:');
            for (const [worker, names] of Object.entries(SECRETS)) {
                for (const s of names) log(`  ${worker}: ${s.name}${s.required ? '' : ' (optional)'}  ${s.what}`);
            }
            return 0;
        }
        for (const p of list) log('NOT READY: ' + p);
        return 1;
    }
    if (command === 'set-kv') {
        const billingId = arg(argv, '--billing');
        const relayId = arg(argv, '--relay');
        for (const [flag, id] of [['--billing', billingId], ['--relay', relayId]]) {
            if (!/^[0-9a-f]{32}$/.test(id ?? '')) { log(`${flag} needs the 32-character id wrangler printed.`); return 2; }
        }
        if (billingId === relayId) { log('The two ids must be two different namespaces.'); return 2; }
        const relayPath = resolve(repo, PATHS.relay);
        const billingPath = resolve(repo, PATHS.billing);
        writeFileSync(relayPath, setKvId(setKvId(readFileSync(relayPath, 'utf8'), 'RELAY_KV', relayId), 'STATUS_KV', billingId));
        writeFileSync(billingPath, setKvId(readFileSync(billingPath, 'utf8'), 'BILLING_KV', billingId));
        log('Written. BILLING_KV is also the relay\'s STATUS_KV. Run: node tools/golive/golive.mjs check');
        return 0;
    }
    log('Usage: node tools/golive/golive.mjs check | set-kv --billing <id> --relay <id>');
    return 2;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    process.exitCode = main(process.argv.slice(2));
}
