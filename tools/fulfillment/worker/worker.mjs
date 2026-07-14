// Semanticus — Cloudflare Worker fulfillment (the production path; see README.md for deploy).
//
// Paddle Billing webhook → verify signature → mint a Pro license token (WebCrypto, same wire format the
// shipped engine verifies offline) → email it to the buyer via Resend → record everything in KV.
//
// Resilience over cleverness: the mint + KV record ALWAYS happen; delivery is best-effort with the record
// keeping status, and GET /pending (admin) lists anything a human still needs to send. Duplicate webhook
// deliveries are idempotent by transaction id — but a duplicate of an UNDELIVERED sale re-runs completion
// (fill the email, mint if missing, deliver if possible), so Paddle's "replay" button doubles as the
// recovery tool. The signing key exists only as a Worker secret — this code never logs or returns it, and
// the engine itself still holds only the public key (golden rule #1).
//
// Observability: every terminal branch logs one line (ids only — never tokens, keys, or email addresses),
// so `wrangler tail` always shows WHY a request ended where it did.
//
// Bindings/config (wrangler.toml + `wrangler secret put`):
//   KV       FULFILL_KV             — sale records: txn:<paddle transaction id> → JSON
//   var      PRICE_ANNUAL/MONTHLY   — the two Paddle price ids (public; used to derive token expiry)
//   var      FROM_EMAIL, OWNER_EMAIL
//   secret   PADDLE_WEBHOOK_SECRET  — the notification destination's secret ("pdl_ntfset_…")
//   secret   PADDLE_API_KEY         — server-side API key, used ONLY to look up the buyer's email
//   secret   SEMANTICUS_SIGNING_KEY — the PKCS8 base64 private key (from `Semanticus.License keygen`)
//   secret   RESEND_API_KEY         — optional; without it, mints queue as pending-delivery
//   secret   ADMIN_KEY              — bearer for GET /pending

import { mintToken, b64urlDecode } from './mint.mjs';

const REPLAY_WINDOW_SECONDS = 300;   // generous: Paddle suggests ~5s, but retries + clock skew make that brittle
const FULFILL_EVENTS = new Set(['transaction.completed']);   // the money event — fires on first purchase AND renewals

export default {
    async fetch(request, env, ctx) {
        const url = new URL(request.url);
        const path = url.pathname.replace(/\/+$/, '') || '/';   // '/paddle/' must not fall through to a 200 ACK
        if (path === '/paddle' && request.method === 'POST') return handlePaddle(request, env, ctx);
        if (path === '/pending' && request.method === 'GET') return handlePending(request, env);
        if (path === '/' && request.method === 'GET') return new Response('semanticus fulfillment', { status: 200 });
        // Anything else is a misconfiguration — fail LOUD (a webhook pointed at a wrong path must not get a
        // 2xx, or Paddle marks it delivered and the sale silently vanishes).
        console.warn('unmatched route', request.method, url.pathname);
        return new Response('not found', { status: 404 });
    },
};

// --- Paddle webhook -------------------------------------------------------------------------------------

async function handlePaddle(request, env, ctx) {
    const rawBody = await request.text();   // signature is over the RAW bytes — never re-serialize
    const sigError = await verifyPaddleSignature(request.headers.get('Paddle-Signature'), rawBody, env.PADDLE_WEBHOOK_SECRET);
    if (sigError) {
        console.warn('paddle: rejected —', sigError);
        return new Response('bad signature', { status: 401 });
    }

    let evt;
    try { evt = JSON.parse(rawBody); } catch { return new Response('bad json', { status: 400 }); }
    if (!FULFILL_EVENTS.has(evt.event_type)) {
        console.log('paddle: ignored event', evt.event_type, evt.event_id);
        return new Response('ignored', { status: 200 });
    }

    const data = evt.data || {};
    const txnId = data.id || evt.event_id;
    const kvKey = 'txn:' + txnId;

    // Idempotency: Paddle retries until it sees a 2xx, and can deliver twice regardless. A fully delivered
    // sale is ACKed and skipped; an UNDELIVERED one re-runs completion so a replay heals it.
    let existing = null;
    const stored = await env.FULFILL_KV.get(kvKey);
    if (stored) {
        try { existing = JSON.parse(stored); } catch { /* corrupt record — refulfill from scratch */ }
        if (existing && (existing.status === 'delivered' || existing.status === 'resolved')) {
            console.log('paddle: duplicate (already ' + existing.status + ')', txnId);
            return new Response('duplicate', { status: 200 });
        }
        console.log('paddle: re-fulfilling undelivered txn', txnId, 'status', existing?.status);
    } else {
        console.log('paddle: fulfilling', txnId, 'customer', data.customer_id);
    }

    // ACK fast; fulfill after responding so Paddle's 10s timeout never triggers a retry storm.
    ctx.waitUntil(fulfill(env, kvKey, txnId, data, existing));
    return new Response('ok', { status: 200 });
}

async function fulfill(env, kvKey, txnId, data, existing = null) {
    const record = existing || {
        txnId,
        at: new Date().toISOString(),
        customerId: data.customer_id || null,
        email: null, period: null, token: null,
        status: 'minting',
        error: null,
    };
    record.error = null;
    try {
        if (!record.email) {
            // custom_data wins when the checkout supplies it; the Customers API is the fallback. A failed
            // lookup is RECORDED, not swallowed — a null email with no explanation is undebuggable.
            const looked = data.custom_data?.email
                ? { email: data.custom_data.email, note: null }
                : await lookupCustomerEmail(env, data.customer_id);
            record.email = looked.email;
            if (!looked.email && looked.note) {
                record.error = looked.note;
                console.warn('fulfill: no buyer email —', looked.note, txnId);
            }
        }
        record.period = record.period || derivePeriod(env, data);

        // Mint when there is no token yet, or when a better identity arrived (e.g. the first pass fell back
        // to paddle:<customer_id> and a replay has since resolved the real email). Old tokens stay valid —
        // re-minting an undelivered sale never strands a buyer.
        const wantSub = record.email || `paddle:${data.customer_id || txnId}`;
        if (!record.token || tokenSub(record.token) !== wantSub) {
            const now = Math.floor(Date.now() / 1000);
            record.token = await mintToken(env.SEMANTICUS_SIGNING_KEY, {
                sub: wantSub,
                tier: 'pro',
                iat: now,
                exp: record.period.expUnix,
            });
        }
        record.status = 'minted';

        if (env.RESEND_API_KEY && record.email) {
            await deliver(env, record);
            record.status = 'delivered';
        } else {
            record.status = 'pending-delivery';   // shows up on GET /pending until a human (or Resend) exists
        }
    } catch (e) {
        record.error = String(e?.message || e);
        record.status = record.token ? 'pending-delivery' : 'mint-failed';
        console.error('fulfill: error', txnId, record.error);
    }
    // The record is the safety net — write it whatever happened. (KV put last so a crash before this point
    // leaves the txn unrecorded and Paddle's retry re-runs the whole fulfillment.)
    await env.FULFILL_KV.put(kvKey, JSON.stringify(record));
    console.log('fulfill: recorded', kvKey, record.status, 'email', record.email ? 'yes' : 'no', 'token', record.token ? 'yes' : 'no');
}

// The sub claim of a minted token (payload is base64url JSON before the dot); null if unreadable.
function tokenSub(token) {
    try { return JSON.parse(new TextDecoder().decode(b64urlDecode(token.split('.')[0]))).sub ?? null; }
    catch { return null; }
}

// --- Signature verification (Paddle Billing: "ts=<unix>;h1=<hex>" over `<ts>:<rawBody>`) -----------------

// Returns null when valid, else a short reason string (logged, never sent to the caller).
async function verifyPaddleSignature(header, rawBody, secret) {
    if (!secret) return 'PADDLE_WEBHOOK_SECRET not set';
    if (!header) return 'missing Paddle-Signature header';
    const parts = Object.fromEntries(header.split(';').map(kv => {
        const j = kv.indexOf('=');
        return j < 0 ? [kv.trim(), ''] : [kv.slice(0, j).trim(), kv.slice(j + 1).trim()];
    }));
    if (!parts.ts || !parts.h1) return 'malformed Paddle-Signature header';

    const age = Math.abs(Math.floor(Date.now() / 1000) - parseInt(parts.ts, 10));
    if (!Number.isFinite(age) || age > REPLAY_WINDOW_SECONDS) return `stale timestamp (age ${age}s)`;

    // trim(): an interactively pasted secret can pick up stray whitespace; the correct value is unaffected.
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret.trim()),
        { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    const mac = new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(parts.ts + ':' + rawBody)));
    const expected = [...mac].map(b => b.toString(16).padStart(2, '0')).join('');
    return timingSafeEqualHex(expected, parts.h1.toLowerCase()) ? null : 'HMAC mismatch';
}

function timingSafeEqualHex(a, b) {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    return diff === 0;
}

// --- Fulfillment inputs ----------------------------------------------------------------------------------

async function lookupCustomerEmail(env, customerId) {
    if (!customerId) return { email: null, note: 'transaction carries no customer_id' };
    if (!env.PADDLE_API_KEY) return { email: null, note: 'PADDLE_API_KEY not set' };
    const res = await fetch(`https://api.paddle.com/customers/${customerId}`, {
        headers: { Authorization: `Bearer ${env.PADDLE_API_KEY.trim()}` },
    });
    if (!res.ok) return { email: null, note: `customer lookup HTTP ${res.status}: ${(await res.text()).slice(0, 200)}` };
    const email = (await res.json())?.data?.email || null;
    return { email, note: email ? null : 'customer record has no email' };
}

// Token expiry, most-authoritative first: the transaction's own billing period end; else the known price id
// (annual/monthly); else a conservative +1 month (never gift a year on an unrecognized product — the record
// lands on /pending where a human can re-mint properly).
function derivePeriod(env, data) {
    const endsAt = data.billing_period?.ends_at;
    if (endsAt) return { label: 'until ' + endsAt.slice(0, 10), expUnix: Math.floor(Date.parse(endsAt) / 1000) };

    const priceIds = (data.items || []).map(i => i.price?.id).filter(Boolean);
    const d = new Date();
    if (priceIds.includes(env.PRICE_ANNUAL)) {
        d.setUTCFullYear(d.getUTCFullYear() + 1);
        return { label: '1 year', expUnix: Math.floor(d.getTime() / 1000) };
    }
    if (priceIds.includes(env.PRICE_MONTHLY)) {
        d.setUTCMonth(d.getUTCMonth() + 1);
        return { label: '1 month', expUnix: Math.floor(d.getTime() / 1000) };
    }
    d.setUTCMonth(d.getUTCMonth() + 1);
    return { label: '1 month (unrecognized price — review)', expUnix: Math.floor(d.getTime() / 1000) };
}

// --- Delivery (Resend) -----------------------------------------------------------------------------------

async function deliver(env, record) {
    const body = {
        from: env.FROM_EMAIL,
        to: [record.email],
        bcc: env.OWNER_EMAIL ? [env.OWNER_EMAIL] : undefined,
        subject: 'Your Semanticus Pro license',
        text: deliveryText(record.token, record.period.label),
    };
    const res = await fetch('https://api.resend.com/emails', {
        method: 'POST',
        headers: { Authorization: `Bearer ${env.RESEND_API_KEY.trim()}`, 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`resend ${res.status}: ${(await res.text()).slice(0, 300)}`);
}

function deliveryText(token, periodLabel) {
    return [
        'Thanks for subscribing to Semanticus Pro!',
        '',
        `Your license token (valid for ${periodLabel}) is below. To activate:`,
        '',
        '  1. In VS Code, open the Command Palette (Ctrl/Cmd+Shift+P).',
        '  2. Run "Semanticus: Activate License".',
        '  3. Paste the token below and press Enter.',
        '',
        'Semanticus will restart its engine and confirm "Semanticus Pro is active".',
        'Using Claude Code? Run "Semanticus: Connect Claude Code" to write the token into .mcp.json too.',
        '',
        'Your token:',
        '',
        `  ${token}`,
        '',
        'Verification is fully offline — Semanticus never phones home. Keep this token; re-paste it if you',
        "reinstall. We'll email a fresh token on each renewal so Pro never lapses.",
        '',
        'Questions, wrong email, anything at all: hello@semanticus.com.au',
        '',
        '— The Semanticus team',
    ].join('\n');
}

// --- Admin: anything not yet delivered -------------------------------------------------------------------

async function handlePending(request, env) {
    const auth = request.headers.get('Authorization') || '';
    if (!env.ADMIN_KEY || !timingSafeEqualHex(auth, 'Bearer ' + env.ADMIN_KEY.trim())) {
        return new Response('unauthorized', { status: 401 });
    }
    const out = [];
    let cursor;
    do {
        const page = await env.FULFILL_KV.list({ prefix: 'txn:', cursor });
        for (const k of page.keys) {
            const rec = JSON.parse(await env.FULFILL_KV.get(k.name));
            if (rec && rec.status !== 'delivered' && rec.status !== 'resolved') out.push(rec);
        }
        cursor = page.list_complete ? null : page.cursor;
    } while (cursor);
    return new Response(JSON.stringify(out, null, 2), { headers: { 'Content-Type': 'application/json' } });
}
