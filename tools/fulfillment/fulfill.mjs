#!/usr/bin/env node
// Semanticus — MANUAL fulfillment CLI (the launch-volume path).
//
//   node fulfill.mjs --email buyer@example.com --period 1y [--tier pro] [--sub "Display Name"]
//
// Mints a Pro license token by invoking the offline Semanticus.License minter, then prints the token AND a
// ready-to-paste delivery email. This is the human path for a handful of early subscribers; the same minting is
// automated by paddle-webhook.mjs once volume warrants it.
//
// The signing key comes from env SEMANTICUS_SIGNING_KEY — a PATH to the PKCS8 base64 private key file (preferred),
// or the base64 key material itself. Either way the key is handed to the minter via a `@file` reference so it is
// NEVER passed as a process argument and NEVER logged. It is not stored beyond a 0600 temp file (deleted on exit)
// when the env holds inline material rather than a path.
//
// Golden rule #1 holds: this tool lives OUTSIDE the shipped engine (the engine only ever verifies with the public
// key). Run it on a trusted issuing machine.

import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { existsSync, writeFileSync, rmSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');                 // <repo>/tools/fulfillment -> <repo>
const MINTER = join(REPO, 'Semanticus.License');             // the offline minter project

function arg(name, fallback = undefined) {
    const i = process.argv.indexOf(name);
    return (i >= 0 && i + 1 < process.argv.length) ? process.argv[i + 1] : fallback;
}

function fail(msg) { console.error('fulfill: ' + msg); process.exit(1); }

const email = arg('--email');
const period = arg('--period', '1y');
const tier = arg('--tier', 'pro');
const sub = arg('--sub', email);   // the license "licensed to" — defaults to the email (display only)

if (!email || process.argv.includes('-h') || process.argv.includes('--help')) {
    console.log('Usage: node fulfill.mjs --email <addr> --period <1y|6m|30d|2w|YYYY-MM-DD> [--tier pro] [--sub "Name"]');
    console.log('Env:   SEMANTICUS_SIGNING_KEY = path to (or base64 of) the PKCS8 private key.');
    process.exit(email ? 0 : 1);
}

// --period → the minter's --expires spec. A bare "Ny/Nm/Nw/Nd" becomes a relative "+Ny"; anything else (e.g. an ISO
// date) is passed through verbatim so an absolute expiry works too.
const expires = /^\d+[dwmy]$/i.test(period) ? '+' + period.toLowerCase() : period;

const keyEnv = process.env.SEMANTICUS_SIGNING_KEY;
if (!keyEnv) fail('SEMANTICUS_SIGNING_KEY is not set (path to, or base64 of, the PKCS8 private key).');

// Resolve the key to a `@file` reference the minter reads directly — so the key is never a process arg. If the env
// is a real path, use it as-is; otherwise treat it as inline base64 and stage it in a 0600 temp file we delete after.
let keyRef, tempDir;
try {
    if (existsSync(keyEnv)) {
        keyRef = '@' + keyEnv;
    } else {
        tempDir = mkdtempSync(join(tmpdir(), 'semanticus-key-'));
        const keyFile = join(tempDir, 'signing.key');
        writeFileSync(keyFile, keyEnv.trim(), { mode: 0o600 });
        keyRef = '@' + keyFile;
    }

    // Invoke the minter. `dotnet run --project <minter> -- mint ...` works from a clean checkout with no prior build.
    // (A built exe — `<minter>/bin/Release/net8.0/semanticus-license mint ...` — is faster if you `dotnet build` it
    //  first; documented in docs/subscription-fulfillment.md. The token flows over stdout either way.)
    // Release by default (the shipped issuing config); SEMANTICUS_MINT_CONFIG overrides (e.g. on a dev box where the
    // Release engine bin is locked by a running host, use Verify/Debug).
    const config = process.env.SEMANTICUS_MINT_CONFIG || 'Release';
    const mintArgs = ['run', '--project', MINTER, '-c', config, '--',
        'mint', '--priv', keyRef, '--sub', sub, '--tier', tier, '--expires', expires];
    const r = spawnSync('dotnet', mintArgs, { encoding: 'utf8' });
    if (r.status !== 0) fail('minter failed (exit ' + r.status + '):\n' + (r.stderr || r.stdout || '(no output)'));

    const token = (r.stdout || '').trim();
    if (!token || !token.includes('.')) fail('minter produced no token:\n' + (r.stdout || r.stderr));

    // --- Output: the token, then a ready-to-paste delivery email. Never the key. ---
    console.log('=== LICENSE TOKEN (deliver to ' + email + ', expiry ' + expires + ') ===');
    console.log(token);
    console.log();
    console.log('=== DELIVERY EMAIL (copy below the line) ===');
    console.log(deliveryEmail(email, token, period));
} finally {
    if (tempDir) { try { rmSync(tempDir, { recursive: true, force: true }); } catch { /* best effort */ } }
}

function deliveryEmail(to, token, period) {
    return [
        `To: ${to}`,
        `Subject: Your Semanticus Pro license`,
        ``,
        `Thanks for subscribing to Semanticus Pro!`,
        ``,
        `Your license token (valid for ${period}) is below. To activate:`,
        ``,
        `  1. In VS Code, open the Command Palette (Ctrl/Cmd+Shift+P).`,
        `  2. Run "Semanticus: Activate License".`,
        `  3. Paste the token below and press Enter.`,
        ``,
        `Semanticus will restart its engine and confirm "Semanticus Pro is active".`,
        `Using Claude Code? Run "Semanticus: Connect Claude Code" to write the token into .mcp.json too.`,
        ``,
        `Your token:`,
        ``,
        `  ${token}`,
        ``,
        `Verification is fully offline — Semanticus never phones home. Keep this token; re-paste it if you`,
        `reinstall. We'll email a fresh token before this one expires so Pro never lapses.`,
        ``,
        `— The Semanticus team`,
    ].join('\n');
}
