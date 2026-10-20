// The Semanticus decision relay. One Cloudflare worker, no SDK, two routes.
//
//   POST /v1/decide   Authorization: Bearer <licence token>
//                     { point, snapshot, questions, idempotencyKey, schemaVersion? }
//                     -> the vendor's answer body, byte for byte, plus x-relay-tokens-used and
//                        x-relay-remaining; or a refusal { error, reason } with a plain status.
//   GET  /v1/budget   Authorization: Bearer <licence token>  -> { used, cap, resetsAt }
//
// What this worker holds and what it must never hold. It holds the licence VERIFICATION key (public) and
// the Jev house key (secret). It does NOT hold the licence SIGNING key, the Stripe or Paddle credentials,
// or any issuance route: those belong to tools/fulfillment and stay there, so a hole here cannot mint a
// licence or move money. The only thing it reads from the billing side is a subscription status, read-only.
//
// The order of the gates is the design. Everything that can refuse for free refuses before anything is
// reserved, and everything that reserves does so before anything is forwarded:
//
//   size -> licence signature -> tier -> expiry (with the engine's 14-day grace) -> global kill switch
//   -> subscription status -> pinned template, schema and fields -> point and model kill switches
//   -> reserve on the licence ledger -> reserve on the house ledger -> forward -> settle
//
// Astra named the one failure that costs real money: forwarding before an atomic reservation, so parallel
// requests all see the same balance. That is why the two reservations are Durable Object calls and why the
// forward sits after both. The two ledgers cannot be reserved in one atomic step, so the pair is
// compensating: if the house refuses, the licence hold is released; if the worker dies between them, the
// licence hold is left in `reserved` and the sweep releases it an hour later, because nothing was sent.
//
// The second half of the same rule, which the first cut of this file got wrong: a reservation only protects
// the call it covers. The caller's idempotency key is hashed together with the licence identity and bound
// to a digest of the forwarded body, so one reference cannot cover two different questions, cannot be
// shared between two licences on the house ledger, and cannot let a second forward out while the first is
// still running. Past this point exactly one of three things is true of every request: it is covered by
// its own live hold, it is served from an answer a settled hold already paid for, or it was refused.
//
// Logging discipline: one line per request, and it carries the point, a hashed subscription id, the tokens,
// the latency and the outcome. It never carries the token, the snapshot, the questions or the reply. There
// is no debug mode that turns that off.

import { LIMITS, checkTemplate, TEMPLATES } from './templates.mjs';
import { LicenceLedger, resetsAt } from './ledger.mjs';
import { budgetIdentity, sha256Hex, verifyLicence } from './verify.mjs';

export { LicenceLedger };

const DEFAULT_UPSTREAM = 'https://api.typesafe.ai/v1/systemone';
const INACTIVE_STATUSES = new Set(['cancelled', 'canceled', 'refunded', 'chargeback', 'unpaid', 'revoked']);

/**
 * Build the worker. `deps.fetch` and `deps.now` exist so the suite can run a fake vendor and a fixed clock;
 * production passes neither and gets the real ones.
 */
export function createRelay(deps = {}) {
    const upstreamFetch = deps.fetch ?? ((...args) => globalThis.fetch(...args));
    const now = deps.now ?? (() => Date.now());

    return {
        async fetch(request, env, ctx) {
            const url = new URL(request.url);
            const path = url.pathname.replace(/\/+$/, '') || '/';
            if (path === '/v1/decide') {
                if (request.method !== 'POST') return methodNotAllowed('POST');
                return handleDecide(request, env, { upstreamFetch, now, ctx });
            }
            if (path === '/v1/budget') {
                if (request.method !== 'GET') return methodNotAllowed('GET');
                return handleBudget(request, env, { now });
            }
            if (path === '/' && request.method === 'GET') return new Response('semanticus relay', { status: 200 });
            return refusal(404, 'not_found', 'There is nothing at that address.');
        },

        /** The reconcile job. Cloudflare calls this on the cron in wrangler.toml. */
        async scheduled(event, env) {
            const at = now();
            const swept = { ledgers: 0, settled: 0, released: 0, stuck: 0, pruned: 0 };
            for (const name of await ledgerNames(env)) {
                const out = await ledgerCall(env, name, 'sweep', { now: at });
                swept.ledgers++;
                swept.settled += out.settled ?? 0;
                swept.released += out.released ?? 0;
                swept.stuck += out.stuck ?? 0;
                swept.pruned += out.pruned ?? 0;
            }
            console.log(JSON.stringify({ at: new Date(at).toISOString(), outcome: 'sweep', ...swept }));
            return swept;
        },
    };
}

export default createRelay();

// --- POST /v1/decide ------------------------------------------------------------------------------------

async function handleDecide(request, env, { upstreamFetch, now }) {
    const startedAt = now();
    const log = { point: null, sid: null, legacy: false, tokens: 0, ms: 0, outcome: null, status: 200 };
    const finish = (response, outcome) => {
        log.outcome = outcome;
        log.status = response.status;
        log.ms = now() - startedAt;
        console.log(JSON.stringify({ at: new Date(startedAt).toISOString(), ...log }));
        return response;
    };

    const raw = await request.text();
    if (byteLength(raw) > LIMITS.maxRequestBytes) {
        return finish(refusal(413, 'too_large', 'That request is too big to send.'), 'too_large');
    }

    const licence = await verifyLicence(bearer(request), env.RELAY_PUBLIC_KEY, startedAt);
    if (!licence.ok) {
        const status = licence.code === 'no_licence' || licence.code === 'bad_signature' ? 401
            : licence.code === 'not_configured' ? 503 : 403;
        return finish(refusal(status, licence.code, licence.reason), licence.code);
    }
    const identity = budgetIdentity(licence.claims);
    log.sid = (await sha256Hex(identity.id)).slice(0, 16);
    log.legacy = identity.legacy;
    log.grace = licence.inGrace;

    if (await killed(env, 'kill:global')) {
        return finish(refusal(503, 'paused', 'Cloud advice is paused right now. Nothing else has changed.'), 'paused');
    }

    // Revocation. A signed, in-date token is not proof the subscription is still good: a cancellation or a
    // refund lands here, not in the token. A MISSING status is allowed through on purpose: the billing
    // worker writes the status when it mints, and a KV read that has not caught up must not lock a paying
    // customer out of what they bought.
    const inactive = await subscriptionInactive(env, identity.sid);
    if (inactive) {
        return finish(refusal(403, 'subscription_inactive',
            'This subscription is not active. Check your billing details to use cloud advice again.'), 'subscription_inactive');
    }

    let body;
    try { body = JSON.parse(raw); } catch { body = null; }
    if (!body || typeof body !== 'object') {
        return finish(refusal(400, 'bad_request', 'The request was not in the expected shape.'), 'bad_request');
    }
    log.point = typeof body.point === 'string' ? body.point : null;

    const idempotencyKey = typeof body.idempotencyKey === 'string' ? body.idempotencyKey.trim() : '';
    if (!idempotencyKey || idempotencyKey.length > 200) {
        return finish(refusal(400, 'bad_request', 'The request was missing its own reference.'), 'bad_request');
    }

    const bad = checkTemplate(body.point, body.schemaVersion, body.snapshot, body.questions);
    if (bad) return finish(refusal(bad.status, bad.error, bad.reason), bad.error);
    log.schema = body.schemaVersion === undefined || body.schemaVersion === null
        ? 'assumed' : Number(body.schemaVersion);

    if (await killed(env, 'kill:point:' + body.point)) {
        return finish(refusal(503, 'point_paused', 'This kind of advice is paused right now.'), 'point_paused');
    }
    const model = env.RELAY_MODEL || 'jev-latest';
    if (await killed(env, 'kill:model:' + model)) {
        return finish(refusal(503, 'model_paused', 'Cloud advice is paused right now. Nothing else has changed.'), 'model_paused');
    }

    // The forwarded body. The relay builds it: the caller never chooses the model, and nothing it did not
    // send survives into the request.
    const upstreamBody = JSON.stringify({ model, state: body.snapshot, questions: body.questions });
    const estimate = estimateInputTokens(upstreamBody);

    const licenceCap = capFromEnv(env.RELAY_LICENCE_TOKENS, LIMITS.licenceMonthlyTokens);
    // The house cap is the CONFIGURED per-licence cap times the paying count, so the two never drift apart (Sol, 1 Oct: it used to
    // multiply the 30,000,000 default, whatever RELAY_LICENCE_TOKENS said).
    const houseCap = licenceCap * capFromEnv(env.RELAY_LICENCE_COUNT, 0);

    // The ledger key is the caller's reference BOUND TO THIS LICENCE, and the hold it takes is bound to the
    // exact words being asked. Astra's finding 1: a bare caller key let one reference forward five calls
    // under a single 500-token charge, let a second licence ride on the first one's house entry, and let a
    // released hold be retried into a forward that recorded nothing at all. Both bindings close that.
    const entryKey = await sha256Hex(identity.id + '|' + idempotencyKey);
    const payloadDigest = await sha256Hex(upstreamBody);

    const mine = await ledgerCall(env, identity.id, 'reserve', {
        key: entryKey, digest: payloadDigest, estimate,
        cap: licenceCap, concurrencyLimit: LIMITS.perSidConcurrency, now: startedAt,
    });
    if (!mine.ok) {
        if (mine.error === 'concurrency') {
            return finish(refusal(429, 'too_many_in_flight',
                'Too many advice requests are already running for this licence. Try again in a moment.'), 'too_many_in_flight');
        }
        if (mine.error === 'key_conflict') {
            return finish(refusal(409, 'idempotency_reused',
                'That reference was already used for a different request. Send this one with its own reference.'),
                'idempotency_reused');
        }
        if (mine.error === 'in_flight') {
            return finish(refusal(409, 'already_in_flight',
                'That request is already running. Wait for the answer rather than asking again.'), 'already_in_flight');
        }
        return finish(refusal(402, 'licence_budget_spent',
            'This licence has used its cloud advice for the month. It resets on ' + shortDate(mine.resetsAt) + '.',
            { used: mine.used, cap: mine.cap, resetsAt: mine.resetsAt }), 'licence_budget_spent');
    }

    // The same question, already answered and already paid for. It is served without going near the vendor,
    // which is the only way a second call under one reference is allowed to cost nothing.
    if (mine.cached) {
        const cachedHeaders = new Headers();
        cachedHeaders.set('content-type', mine.cached.contentType || 'application/json');
        cachedHeaders.set('x-relay-tokens-used', '0');
        cachedHeaders.set('x-relay-remaining', String(Math.max(0, licenceCap - (mine.used ?? 0))));
        cachedHeaders.set('x-relay-cached', '1');
        return finish(new Response(mine.cached.body, { status: 200, headers: cachedHeaders }), 'served_cached');
    }

    // Every forward from here down is covered by THIS hold and no other.
    const attemptId = mine.id;
    await rememberLedger(env, identity.id);

    const house = await ledgerCall(env, 'house', 'reserve', {
        id: attemptId, estimate, cap: houseCap, concurrencyLimit: 0, now: startedAt,
    });
    if (!house.ok) {
        // Nothing has been sent, so the licence hold is given straight back.
        await ledgerCall(env, identity.id, 'release', { id: attemptId, now: startedAt });
        return finish(refusal(503, 'house_budget_spent',
            'Cloud advice is at its limit right now. Try again later.'), 'house_budget_spent');
    }

    await ledgerCall(env, identity.id, 'forward', { id: attemptId, now: startedAt });
    await ledgerCall(env, 'house', 'forward', { id: attemptId, now: startedAt });

    let response;
    try {
        response = await upstreamFetch(env.RELAY_UPSTREAM || DEFAULT_UPSTREAM, {
            method: 'POST',
            headers: {
                authorization: 'Bearer ' + (env.JEV_HOUSE_KEY ?? ''),
                'content-type': 'application/json',
                // The caller's key, unchanged, so a retry of the same ask is the same ask to the vendor too.
                // Their deduplication is UNCONFIRMED, which is why a retry is still accounted as possible
                // spend rather than assumed free.
                'idempotency-key': idempotencyKey,
            },
            body: upstreamBody,
            signal: AbortSignal.timeout(LIMITS.upstreamTimeoutMs),
        });
    } catch (error) {
        // The vendor has the request and we do not know the outcome. It stays possibly charged.
        await stick(env, identity.id, attemptId, now());
        const timedOut = error?.name === 'TimeoutError' || error?.name === 'AbortError';
        return finish(refusal(timedOut ? 504 : 502,
            timedOut ? 'upstream_timeout' : 'upstream_failed',
            'The advice service did not answer in time.'), timedOut ? 'upstream_timeout' : 'upstream_failed');
    }

    // The body can die after the headers arrive (a reset, the deadline firing mid-read). The vendor has the
    // request, so it stays possibly charged: stuck, never forwarded-forever and never thrown past the ledger.
    let text;
    try {
        text = await response.text();
    } catch (error) {
        await stick(env, identity.id, attemptId, now());
        const timedOut = error?.name === 'TimeoutError' || error?.name === 'AbortError';
        return finish(refusal(timedOut ? 504 : 502,
            timedOut ? 'upstream_timeout' : 'upstream_failed',
            'The advice service did not answer in time.'), timedOut ? 'upstream_timeout' : 'upstream_failed');
    }

    if (!response.ok) {
        if (response.status >= 500 || response.status === 429) {
            // Possibly charged, possibly not. Pessimism is the rule: it stays held until the sweep.
            await stick(env, identity.id, attemptId, now());
        } else {
            // A 4xx means the vendor read the request and rejected it. It reached them, so the estimate is
            // spent. JevDecisionOracle's direct route counts a vendor 4xx the same way; only the relay's own
            // refusals above, given before anything is forwarded, are free there.
            await settleBoth(env, identity.id, attemptId, estimate, now(), 'rejected');
        }
        return finish(refusal(502, 'upstream_failed', 'The advice service could not answer.'), 'upstream_failed');
    }

    const usedTokens = readInputTokens(text) ?? estimate;
    // Keep the answer only when it is small enough to be worth keeping. An answer that is not cached is not
    // a problem: the next identical ask simply takes a new hold and is paid for again, honestly.
    const cache = byteLength(text) <= LIMITS.maxCachedReplyBytes
        ? { body: text, contentType: response.headers.get('content-type') ?? 'application/json' }
        : null;
    const settled = await settleBoth(env, identity.id, attemptId, usedTokens, now(), 'reply', cache);
    log.tokens = usedTokens;

    const headers = new Headers();
    headers.set('content-type', response.headers.get('content-type') ?? 'application/json');
    headers.set('x-relay-tokens-used', String(usedTokens));
    headers.set('x-relay-remaining', String(Math.max(0, licenceCap - (settled.used ?? 0))));
    return finish(new Response(text, { status: 200, headers }), 'served');
}

// --- GET /v1/budget -------------------------------------------------------------------------------------

async function handleBudget(request, env, { now }) {
    const at = now();
    const licence = await verifyLicence(bearer(request), env.RELAY_PUBLIC_KEY, at);
    if (!licence.ok) {
        const status = licence.code === 'no_licence' || licence.code === 'bad_signature' ? 401
            : licence.code === 'not_configured' ? 503 : 403;
        return refusal(status, licence.code, licence.reason);
    }
    const identity = budgetIdentity(licence.claims);
    const cap = capFromEnv(env.RELAY_LICENCE_TOKENS, LIMITS.licenceMonthlyTokens);
    const out = await ledgerCall(env, identity.id, 'budget', { cap, now: at });
    console.log(JSON.stringify({
        at: new Date(at).toISOString(), outcome: 'budget', status: 200,
        sid: (await sha256Hex(identity.id)).slice(0, 16), legacy: identity.legacy, ms: now() - at,
    }));
    return new Response(JSON.stringify({ used: out.used, cap: out.cap ?? cap, resetsAt: out.resetsAt ?? resetsAt(at) }),
        { status: 200, headers: { 'content-type': 'application/json' } });
}

// --- plumbing -------------------------------------------------------------------------------------------

function bearer(request) {
    const header = request.headers.get('authorization') ?? '';
    const match = /^Bearer\s+(.+)$/i.exec(header.trim());
    return match ? match[1].trim() : '';
}

function byteLength(text) {
    return new TextEncoder().encode(text).length;
}

/** The same pessimistic rule the engine uses (JevDecisionOracle.EstimateInputTokens): the body IS the
 *  input, and dividing its length by three over-counts on purpose. */
function estimateInputTokens(body) {
    return Math.max(1, Math.floor((body?.length ?? 0) / 3));
}

function capFromEnv(value, fallback) {
    const n = Number(value);
    return Number.isFinite(n) && n >= 0 && value !== undefined && value !== null && String(value).trim() !== ''
        ? Math.floor(n) : fallback;
}

function readInputTokens(text) {
    try {
        const parsed = JSON.parse(text);
        const n = Number(parsed?.usage?.input_tokens);
        return Number.isFinite(n) && n > 0 ? Math.floor(n) : null;
    } catch { return null; }
}

async function killed(env, key) {
    if (!env.RELAY_KV) return false;
    const value = await env.RELAY_KV.get(key);
    return value !== null && value !== undefined && String(value).trim() !== '' && String(value) !== '0';
}

async function subscriptionInactive(env, sid) {
    if (!sid || !env.STATUS_KV) return false;
    const raw = await env.STATUS_KV.get('sid:' + sid);
    if (raw === null || raw === undefined) return false;
    let status = String(raw).trim();
    if (status.startsWith('{')) {
        try { status = String(JSON.parse(status).status ?? ''); } catch { /* fall through to the raw string */ }
    }
    return INACTIVE_STATUSES.has(status.toLowerCase());
}

async function ledgerCall(env, name, op, payload) {
    const stub = env.LEDGER.get(env.LEDGER.idFromName(name));
    const response = await stub.fetch('https://ledger/' + op, {
        method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload),
    });
    return response.json();
}

async function stick(env, identityId, id, at) {
    await ledgerCall(env, identityId, 'stick', { id, now: at });
    await ledgerCall(env, 'house', 'stick', { id, now: at });
}

/** The cached answer is held on the licence ledger only. The house ledger counts money, not answers. */
async function settleBoth(env, identityId, id, actual, at, by, cache = null) {
    const mine = await ledgerCall(env, identityId, 'settle', { id, actual, now: at, by, cache });
    await ledgerCall(env, 'house', 'settle', { id, actual, now: at, by });
    return mine;
}

/** Durable Objects cannot be listed, so the sweep needs an index of the ones that exist. It lives in KV
 *  and is eventually consistent, which is fine: a ledger that is missed by one sweep is swept by the next,
 *  and nothing is released early because every release waits an hour anyway. */
async function rememberLedger(env, identityId) {
    if (!env.RELAY_KV) return;
    // Write once per licence, and never let the index fail a decide: on 15 Oct 2026 a put on every decide used up
    // the free tier's 1,000 writes a day and every request failed. A missed write is retried on the next decide.
    const key = 'led:' + identityId;
    try {
        if (await env.RELAY_KV.get(key) !== null) return;
        await env.RELAY_KV.put(key, '1');
    } catch {
        // The sweep finds this ledger after a later decide writes the key.
    }
}

async function ledgerNames(env) {
    const names = ['house'];
    if (!env.RELAY_KV) return names;
    let cursor;
    for (;;) {
        const page = await env.RELAY_KV.list({ prefix: 'led:', limit: 1000, cursor });
        for (const key of page.keys) names.push(key.name.slice('led:'.length));
        if (page.list_complete) break;
        cursor = page.cursor;
    }
    return names;
}

function shortDate(iso) {
    try { return new Date(iso).toISOString().slice(0, 10); } catch { return 'the first of next month'; }
}

function refusal(status, error, reason, extra) {
    return new Response(JSON.stringify({ error, reason, ...(extra ?? {}) }),
        { status, headers: { 'content-type': 'application/json' } });
}

function methodNotAllowed(allow) {
    return new Response(JSON.stringify({ error: 'method_not_allowed', reason: 'That address does not take this kind of request.' }),
        { status: 405, headers: { 'content-type': 'application/json', allow } });
}

// Named so a reader looking for the registry finds it: the relay serves exactly these points.
export const SERVED_POINTS = Object.keys(TEMPLATES);
