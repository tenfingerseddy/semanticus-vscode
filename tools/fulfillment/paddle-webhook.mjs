#!/usr/bin/env node
// Semanticus — REFERENCE Paddle Billing webhook receiver (node stdlib only, zero dependencies).
//
// Listens for Paddle Billing webhooks, verifies the signature, and on a paid/renewed subscription mints a Pro
// license token via the same offline Semanticus.License minter fulfill.mjs uses. At v1 it LOGS the token + a
// delivery email to stdout; the marked TODO is where real delivery (SMTP, or the Paddle Notification/customer API)
// plugs in. Deploy on any tiny node host (a $5 VM, a container, a serverless function adapted to this handler).
//
// Env:
//   PADDLE_WEBHOOK_SECRET  — the destination's secret key from Paddle > Notifications (e.g. "pdl_ntfset_...").
//   SEMANTICUS_SIGNING_KEY — path to (or base64 of) the PKCS8 private signing key (never logged; staged @file).
//   PORT                   — listen port (default 8787).
//
// SIGNATURE SCHEME (Paddle Billing — https://developer.paddle.com/webhooks/signature-verification):
//   The `Paddle-Signature` header is "ts=<unix>;h1=<hex>". Build the signed payload as `<ts>:<rawBody>`, compute
//   HMAC-SHA256 over it keyed by the RAW secret string, and constant-time compare the hex digest to h1. The RAW
//   request body must be hashed byte-for-byte (never the re-serialized JSON), so we buffer it before parsing.

import { createServer } from 'node:http';
import { createHmac, timingSafeEqual } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { existsSync, writeFileSync, rmSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');
const MINTER = join(REPO, 'Semanticus.License');

const SECRET = process.env.PADDLE_WEBHOOK_SECRET;
const PORT = parseInt(process.env.PORT || '8787', 10);
// Paddle recommends rejecting webhooks whose timestamp is older than 5s, but clock skew + delivery latency make a
// tight bound brittle for a self-hosted receiver — use a generous 5-minute replay window.
const MAX_SKEW_SECONDS = 300;

if (!SECRET) { console.error('paddle-webhook: PADDLE_WEBHOOK_SECRET is not set — refusing to start (every request would fail verification).'); process.exit(1); }
if (!process.env.SEMANTICUS_SIGNING_KEY) { console.error('paddle-webhook: SEMANTICUS_SIGNING_KEY is not set — cannot mint.'); process.exit(1); }

// --- Paddle signature verification ---------------------------------------------------------------------------
function verifyPaddleSignature(header, rawBody) {
    if (!header) return false;
    // "ts=...;h1=..." — split on ';' then '='. Tolerate ordering and extra parts.
    const parts = Object.fromEntries(header.split(';').map(kv => {
        const j = kv.indexOf('=');
        return j < 0 ? [kv.trim(), ''] : [kv.slice(0, j).trim(), kv.slice(j + 1).trim()];
    }));
    const ts = parts.ts, h1 = parts.h1;
    if (!ts || !h1) return false;

    // Replay guard: reject stale timestamps.
    const age = Math.abs(Math.floor(Date.now() / 1000) - parseInt(ts, 10));
    if (!Number.isFinite(age) || age > MAX_SKEW_SECONDS) { console.error(`paddle-webhook: signature timestamp out of window (age ${age}s).`); return false; }

    const expected = createHmac('sha256', SECRET).update(ts + ':' + rawBody).digest('hex');
    // Constant-time compare (equal-length hex strings).
    if (expected.length !== h1.length) return false;
    try { return timingSafeEqual(Buffer.from(expected, 'hex'), Buffer.from(h1, 'hex')); }
    catch { return false; }
}

// --- Extract the fulfillment inputs from a Paddle event -------------------------------------------------------
// Paddle webhooks don't always inline the customer email (it may require a customers API lookup by data.customer_id).
// Pull it from the likeliest places; return null when it's absent so the caller can log the TODO instead of guessing.
function extractEmail(data) {
    return data?.customer?.email
        || data?.billing_details?.email
        || data?.custom_data?.email
        || null;
}

// Map the subscription billing cycle to the minter's --expires spec (e.g. yearly ⇒ "+1y"). Falls back to +1y.
function extractPeriod(data) {
    const cycle = data?.billing_cycle;                       // { interval: "year"|"month"|..., frequency: N }
    if (cycle?.interval && cycle?.frequency) {
        const unit = { year: 'y', month: 'm', week: 'w', day: 'd' }[cycle.interval];
        if (unit) return `+${cycle.frequency}${unit}`;
    }
    // Or honor the exact period end when Paddle provides it (absolute date the token should expire on).
    const endsAt = data?.current_billing_period?.ends_at;    // ISO 8601
    if (endsAt) return endsAt.slice(0, 10);
    return '+1y';
}

// --- Mint (same offline key path as fulfill.mjs; key staged as @file, never a process arg, never logged) ------
function mint(sub, tier, expires) {
    const keyEnv = process.env.SEMANTICUS_SIGNING_KEY;
    let keyRef, tempDir;
    try {
        if (existsSync(keyEnv)) { keyRef = '@' + keyEnv; }
        else {
            tempDir = mkdtempSync(join(tmpdir(), 'semanticus-key-'));
            const kf = join(tempDir, 'signing.key');
            writeFileSync(kf, keyEnv.trim(), { mode: 0o600 });
            keyRef = '@' + kf;
        }
        const config = process.env.SEMANTICUS_MINT_CONFIG || 'Release';   // dev override when Release bin is locked
        const r = spawnSync('dotnet', ['run', '--project', MINTER, '-c', config, '--',
            'mint', '--priv', keyRef, '--sub', sub, '--tier', tier, '--expires', expires], { encoding: 'utf8' });
        if (r.status !== 0) throw new Error('minter exit ' + r.status + ': ' + (r.stderr || r.stdout));
        const token = (r.stdout || '').trim();
        if (!token.includes('.')) throw new Error('minter produced no token');
        return token;
    } finally {
        if (tempDir) { try { rmSync(tempDir, { recursive: true, force: true }); } catch { /* best effort */ } }
    }
}

// Events that should (re)issue a Pro license.
const FULFILL_EVENTS = new Set(['transaction.completed', 'subscription.activated', 'subscription.renewed', 'subscription.updated']);

const server = createServer((req, res) => {
    if (req.method !== 'POST') { res.writeHead(405); res.end('POST only'); return; }
    const chunks = [];
    req.on('data', c => chunks.push(c));
    req.on('end', () => {
        const rawBody = Buffer.concat(chunks).toString('utf8');   // hash the RAW bytes, not re-serialized JSON
        if (!verifyPaddleSignature(req.headers['paddle-signature'], rawBody)) {
            res.writeHead(401); res.end('bad signature'); return;
        }

        let evt;
        try { evt = JSON.parse(rawBody); }
        catch { res.writeHead(400); res.end('bad json'); return; }

        // ACK fast (200) so Paddle doesn't retry; do the work after responding.
        res.writeHead(200); res.end('ok');

        if (!FULFILL_EVENTS.has(evt.event_type)) { console.log(`paddle-webhook: ignoring ${evt.event_type}`); return; }

        const data = evt.data || {};
        const email = extractEmail(data);
        const period = extractPeriod(data);
        const sub = email || (`paddle:${data.customer_id || data.id || 'unknown'}`);   // license "licensed to" (display)

        let token;
        try { token = mint(sub, 'pro', period); }
        catch (e) { console.error(`paddle-webhook: MINT FAILED for ${sub} (${evt.event_type}): ${e.message}`); return; }

        // ---- v1 DELIVERY: log the token + email template. -------------------------------------------------------
        // TODO(delivery): replace this block with real delivery — send an email (SMTP / a transactional-email API)
        // OR write the token back to the customer via the Paddle API. Look up the email here if extractEmail()
        // returned null: GET https://api.paddle.com/customers/{data.customer_id} with your Paddle API key.
        console.log('================= FULFILL =================');
        console.log(`event=${evt.event_type} email=${email || '(unknown — look up customer_id ' + data.customer_id + ')'} expires=${period}`);
        console.log('token=' + token);
        if (email) {
            console.log('--- delivery email ---');
            console.log(`To: ${email}\nSubject: Your Semanticus Pro license\n\nActivate in VS Code via "Semanticus: Activate License" and paste:\n\n  ${token}\n\nVerification is fully offline. — The Semanticus team`);
        }
        console.log('==========================================');
    });
});

server.listen(PORT, () => console.log(`paddle-webhook: listening on :${PORT} (POST your Paddle destination here).`));
