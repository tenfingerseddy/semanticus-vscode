// The relay's suite. Everything runs against a fake upstream, a fake KV and real ledger objects held in
// memory, so no test can reach the vendor, the house key or Kane's account.
//
// Run: node --test tools/relay/test/
//
// What each block is here to prove is named in its own test title. The ones that matter most to money are
// the reservation tests: the loser of a near-limit race is refused, a retry does not reserve twice, and a
// call that may have been charged is never quietly forgiven.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { createRelay } from '../worker.mjs';
import { LicenceLedger } from '../ledger.mjs';
import { LIMITS } from '../templates.mjs';
import {
    FakeKV, captureLogs, dumpLedger, generateKeyPair, makeLedgerNamespace, makeUpstream, mintToken, timeoutError,
} from './fakes.mjs';

const POINT = 'measure.folder_and_format';
const UPSTREAM = 'https://api.typesafe.ai/v1/systemone';
const MODEL = 'jev-2026-09-01';

// 15 September 2026, 10:00 UTC. Fixed so the month window and the grace maths are not clock-dependent.
const T0 = Date.UTC(2026, 8, 15, 10, 0, 0);
const DAY = 86_400_000;

let keys;
async function keypair() {
    if (!keys) keys = await generateKeyPair();
    return keys;
}

function snapshot() {
    return {
        measure_name: 'Total Sales',
        measure_formula_facts: { functions: ['SUM'], returns_ratio: false, returns_count: false,
            returns_currency_like_sum: true, references_date_column: false, time_intelligence: false },
        references_measures: [],
        table_name: 'Sales',
        table_columns: [{ name: 'Amount', type: 'decimal' }],
        sibling_measures: [{ name: 'Total Cost', folder: 'Finance', format: '#,0.00' }],
    };
}

function questions() {
    return {
        display_folder: { type: 'choice', instructions: 'pick a folder', criteria: { Finance: 'money', none: 'leave it' } },
        format_family: { type: 'choice', instructions: 'pick a format', criteria: { currency: 'money', percent: 'a share' } },
    };
}

function decideBody(extra = {}) {
    return JSON.stringify({
        point: POINT,
        snapshot: snapshot(),
        questions: questions(),
        idempotencyKey: 'idem-1',
        schemaVersion: 2,
        ...extra,
    });
}

const VENDOR_REPLY = JSON.stringify({
    answers: { display_folder: { value: 'Finance', confidence: 0.91 }, format_family: { value: 'currency', confidence: 0.94 } },
    usage: { input_tokens: 1200, output_tokens: 40 },
    model: MODEL,
});

// The reply Astra's accounting probes use: a round 500 input tokens, so a recorded total either matches the
// number of forwarded calls exactly or it does not.
const REPLY_500 = JSON.stringify({
    answers: { display_folder: { value: 'Finance', confidence: 0.91 }, format_family: { value: 'currency', confidence: 0.94 } },
    usage: { input_tokens: 500, output_tokens: 20 },
    model: MODEL,
});

/** A whole relay world: keys, env, clock and a fake vendor. */
async function world(options = {}) {
    const { publicKey, signingKey } = await keypair();
    const clock = { now: options.now ?? T0 };
    const upstream = makeUpstream(options.upstream ?? (() => ({ status: 200, body: VENDOR_REPLY })));
    const statusKv = new FakeKV(options.status ?? {});
    const relayKv = new FakeKV(options.control ?? {});
    const ledgers = makeLedgerNamespace(LicenceLedger);
    const env = {
        RELAY_PUBLIC_KEY: publicKey,
        JEV_HOUSE_KEY: 'house-key-not-real',
        RELAY_UPSTREAM: UPSTREAM,
        RELAY_MODEL: MODEL,
        RELAY_LICENCE_COUNT: String(options.licenceCount ?? 10),
        STATUS_KV: statusKv,
        RELAY_KV: relayKv,
        LEDGER: ledgers,
        ...(options.licenceTokens === undefined ? {} : { RELAY_LICENCE_TOKENS: String(options.licenceTokens) }),
        ...(options.env ?? {}),
    };
    const relay = createRelay({ fetch: upstream, now: () => clock.now });

    async function token(claims = {}) {
        return mintToken(signingKey, {
            sub: 'buyer@example.com', tier: 'pro', iat: Math.floor(T0 / 1000) - DAY,
            exp: Math.floor((T0 + 30 * DAY) / 1000), sid: 'sub_test_1', ...claims,
        });
    }

    async function decide(tok, body = decideBody(), headers = {}) {
        const request = new Request('https://relay.semanticus.com.au/v1/decide', {
            method: 'POST',
            headers: { 'content-type': 'application/json', ...(tok ? { authorization: 'Bearer ' + tok } : {}), ...headers },
            body,
        });
        return relay.fetch(request, env, { waitUntil() {} });
    }

    async function budget(tok) {
        const request = new Request('https://relay.semanticus.com.au/v1/budget', {
            method: 'GET', headers: { authorization: 'Bearer ' + tok },
        });
        return relay.fetch(request, env, { waitUntil() {} });
    }

    return { relay, env, clock, upstream, statusKv, relayKv, ledgers, token, decide, budget, signingKey, publicKey };
}

// --- the happy path -------------------------------------------------------------------------------------

test('a valid Pro token is accepted and the vendor answer comes back unchanged', async () => {
    const w = await world();
    const res = await w.decide(await w.token());
    assert.equal(res.status, 200);
    assert.equal(await res.text(), VENDOR_REPLY, 'the relay must not rewrite the vendor body');
    assert.equal(res.headers.get('x-relay-tokens-used'), '1200');
    assert.equal(Number(res.headers.get('x-relay-remaining')), LIMITS.licenceMonthlyTokens - 1200);
    assert.equal(w.upstream.calls.length, 1);
});

test('the forwarded request carries the pinned model, the house key and the caller idempotency key', async () => {
    const w = await world();
    await w.decide(await w.token());
    const call = w.upstream.calls[0];
    assert.equal(call.url, UPSTREAM);
    assert.equal(call.method, 'POST');
    assert.equal(call.headers.get('authorization'), 'Bearer house-key-not-real');
    assert.equal(call.headers.get('idempotency-key'), 'idem-1');
    assert.deepEqual(call.json.state, snapshot());
    assert.deepEqual(Object.keys(call.json.questions).sort(), ['display_folder', 'format_family']);
    assert.equal(call.json.model, MODEL, 'the served model is pinned by the relay, never chosen by the caller');
});

test('the licence token is never passed upstream', async () => {
    const w = await world();
    const tok = await w.token();
    await w.decide(tok);
    const call = w.upstream.calls[0];
    assert.ok(!String(call.body).includes(tok));
    assert.ok(!(call.headers.get('authorization') ?? '').includes(tok));
});

// --- the token gate -------------------------------------------------------------------------------------

test('a request with no Authorization header is refused with 401', async () => {
    const w = await world();
    const res = await w.decide(null);
    assert.equal(res.status, 401);
    assert.equal((await res.json()).error, 'no_licence');
    assert.equal(w.upstream.calls.length, 0);
});

test('a token signed by another key is refused with 401', async () => {
    const other = await generateKeyPair();
    const w = await world();
    const bad = await mintToken(other.signingKey, { sub: 'x@example.com', tier: 'pro', iat: 1, exp: Math.floor((T0 + DAY) / 1000), sid: 's' });
    const res = await w.decide(bad);
    assert.equal(res.status, 401);
    assert.equal((await res.json()).error, 'bad_signature');
    assert.equal(w.upstream.calls.length, 0);
});

test('a free-tier token is refused with 403', async () => {
    const w = await world();
    const res = await w.decide(await w.token({ tier: 'free' }));
    assert.equal(res.status, 403);
    assert.equal((await res.json()).error, 'not_pro');
    assert.equal(w.upstream.calls.length, 0);
});

test('a token expired past the 14-day grace is refused with 403', async () => {
    const w = await world();
    const res = await w.decide(await w.token({ exp: Math.floor((T0 - 15 * DAY) / 1000) }));
    assert.equal(res.status, 403);
    assert.equal((await res.json()).error, 'expired');
    assert.equal(w.upstream.calls.length, 0);
});

test('a token inside the 14-day grace is still served, the way the engine treats it', async () => {
    const w = await world();
    const res = await w.decide(await w.token({ exp: Math.floor((T0 - 3 * DAY) / 1000) }));
    assert.equal(res.status, 200);
    assert.equal(w.upstream.calls.length, 1);
});

test('a perpetual token (exp 0) is served', async () => {
    const w = await world();
    const res = await w.decide(await w.token({ exp: 0 }));
    assert.equal(res.status, 200);
});

test('a token whose payload is not JSON is refused with 401', async () => {
    const w = await world();
    const res = await w.decide('bm90LWpzb24.AAAA');
    assert.equal(res.status, 401);
    assert.equal(w.upstream.calls.length, 0);
});

// --- subscription status --------------------------------------------------------------------------------

test('a cancelled subscription id is refused with 403 and never forwarded', async () => {
    const w = await world({ status: { 'sid:sub_test_1': 'cancelled' } });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 403);
    assert.equal((await res.json()).error, 'subscription_inactive');
    assert.equal(w.upstream.calls.length, 0);
});

test('a refunded subscription id is refused, including when the status is stored as JSON', async () => {
    const w = await world({ status: { 'sid:sub_test_1': JSON.stringify({ status: 'refunded', at: 1 }) } });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 403);
    assert.equal((await res.json()).error, 'subscription_inactive');
});

test('an active subscription id is served', async () => {
    const w = await world({ status: { 'sid:sub_test_1': 'active' } });
    assert.equal((await w.decide(await w.token())).status, 200);
});

test('a legacy token with no sid is served and budgeted under sub', async () => {
    const w = await world();
    const res = await w.decide(await w.token({ sid: undefined }));
    assert.equal(res.status, 200);
    const bySub = await dumpLedger(w.ledgers, 'sub:buyer@example.com');
    assert.ok(bySub, 'a token without sid must budget under its sub');
    assert.equal(bySub.meta.settled, 1200);
    assert.equal(await dumpLedger(w.ledgers, 'sid:sub_test_1'), null);
});

// --- the pinned templates -------------------------------------------------------------------------------

test('a point the relay does not serve is refused with 404', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), decideBody({ point: 'measure.rewrite_dax' }));
    assert.equal(res.status, 404);
    assert.equal((await res.json()).error, 'unknown_point');
    assert.equal(w.upstream.calls.length, 0);
});

test('a schema version the relay does not serve is refused with 409', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), decideBody({ schemaVersion: 3 }));
    assert.equal(res.status, 409);
    assert.equal((await res.json()).error, 'schema_mismatch');
});

test('a snapshot field the template does not declare is refused with 400', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), decideBody({ snapshot: { ...snapshot(), source_query: 'select * from Sales' } }));
    assert.equal(res.status, 400);
    assert.equal((await res.json()).error, 'undeclared_field');
    assert.equal(w.upstream.calls.length, 0);
});

test('a question the template does not declare is refused with 400', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), decideBody({ questions: { ...questions(), rename_to: { type: 'text' } } }));
    assert.equal(res.status, 400);
    assert.equal((await res.json()).error, 'undeclared_question');
});

// --- the other two points the product asks -----------------------------------------------------------
//
// Astra's finding 13. Both of these are shipped decision points in DecisionRegistry.cs, and the relay
// answered 404 unknown_point to each of them. The bounds here are the engine's own: plan.rank asks one
// score question per plan item and the engine takes at most PlanRankPoint.MaxScoredItems of them in a
// single call; ask.route asks exactly one. drift.test.mjs is what keeps these numbers honest.

function planSnapshot(itemCount) {
    const items = [];
    for (let i = 0; i < itemCount; i++) {
        items.push({
            id: 'safefix/Item' + i,
            goal: 'Make the model easier to read',
            heading: 'Hide ' + (i + 1) + ' key columns',
            line: 'Key columns are for joins, not for reading.',
            who_would_do_it: 'anyone',
            changes: 2,
            touches: 2,
            reasons: ['a key column is shown in the field list'],
        });
    }
    return {
        model_shape: { model_name: 'Contoso Sales', tables: 12, columns: 140, measures: 30, connected_to_a_live_model: false },
        plan_items: items,
    };
}

function planQuestions(itemCount) {
    const out = {};
    for (let i = 0; i < itemCount; i++) {
        out['value:safefix/Item' + i] = {
            type: 'score',
            instructions: 'How much is it worth doing this to the model?',
            // A score's criteria are a list in ladder order, as the engine sends them (the vendor refuses an object).
            criteria: ['nobody would notice', 'it is producing wrong answers now'],
        };
    }
    return out;
}

// Verify, 28 Sep, L9: a SCORE's criteria go to the service as an ordered LIST, lowest rung first. A keyed object is
// refused with 422 "Input should be a valid list" (JevDecisionOracle.cs, the score branch of the request writer). The
// relay forwards questions unchanged, so this fixture is the one place in the suite that shows the shape, and it must
// show the one the service takes.
test('the plan.rank fixture sends score criteria as the ordered list the service accepts', () => {
    const questions = planQuestions(3);
    for (const [name, q] of Object.entries(questions)) {
        assert.equal(q.type, 'score', name + ' is not a score question');
        assert.ok(Array.isArray(q.criteria), name + ' sends its score criteria as a keyed object, which the service refuses');
        assert.ok(q.criteria.length >= 2 && q.criteria.every((c) => typeof c === 'string' && c.length > 0),
            name + ' has no ladder of plain rungs');
    }
});

function planBody(itemCount, extra = {}) {
    return JSON.stringify({
        point: 'plan.rank',
        snapshot: planSnapshot(itemCount),
        questions: planQuestions(itemCount),
        idempotencyKey: 'plan-' + itemCount,
        schemaVersion: 1,
        ...extra,
    });
}

function askBody(extra = {}) {
    return JSON.stringify({
        point: 'ask.route',
        snapshot: { ask_text: 'where do I add a measure' },
        questions: {
            tool: {
                type: 'choice',
                instructions: 'Which of these places answers what the person typed?',
                criteria: { 'edit.measures': 'writing and changing measures', none: 'not about a model at all' },
            },
        },
        idempotencyKey: 'ask-1',
        schemaVersion: 1,
        ...extra,
    });
}

test('a plan.rank request is served and reaches the vendor', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), planBody(3));
    assert.equal(res.status, 200);
    assert.equal(w.upstream.calls.length, 1);
    assert.equal(w.upstream.calls[0].json.state.plan_items.length, 3);
});

test('plan.rank takes the forty questions the engine can send, and refuses the forty-first', async () => {
    const w = await world();
    const tok = await w.token();
    assert.equal((await w.decide(tok, planBody(40))).status, 200);

    const over = await w.decide(tok, planBody(41));
    assert.equal(over.status, 413);
    assert.equal((await over.json()).error, 'too_many_questions');
    assert.equal(w.upstream.calls.length, 1, 'the forty-first question is refused before anything is forwarded');
});

test('a plan.rank question that is not one of the engine value scores is refused', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), planBody(1, { questions: { rename_item: { type: 'text' } } }));
    assert.equal(res.status, 400);
    assert.equal((await res.json()).error, 'undeclared_question');
    assert.equal(w.upstream.calls.length, 0);
});

test('a plan.rank snapshot field the engine does not declare is refused', async () => {
    const w = await world();
    const snap = { ...planSnapshot(1), plan_history: ['skipped safefix/Visibility'] };
    const res = await w.decide(await w.token(), planBody(1, { snapshot: snap }));
    assert.equal(res.status, 400);
    assert.equal((await res.json()).error, 'undeclared_field');
    assert.equal(w.upstream.calls.length, 0);
});

test('an ask.route request is served and sends only the words typed', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), askBody());
    assert.equal(res.status, 200);
    assert.equal(w.upstream.calls.length, 1);
    assert.deepEqual(Object.keys(w.upstream.calls[0].json.state), ['ask_text']);
});

test('an ask.route request carrying anything about the model is refused', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), askBody({ snapshot: { ask_text: 'how big is this', table_name: 'Sales' } }));
    assert.equal(res.status, 400);
    assert.equal((await res.json()).error, 'undeclared_field');
    assert.equal(w.upstream.calls.length, 0);
});

test('an ask.route question the engine does not ask is refused', async () => {
    const w = await world();
    const res = await w.decide(await w.token(), askBody({ questions: { answer: { type: 'text' } } }));
    assert.equal(res.status, 400);
    assert.equal((await res.json()).error, 'undeclared_question');
});

// --- size caps ------------------------------------------------------------------------------------------

test('a body over the byte cap is refused with 413', async () => {
    const w = await world();
    const big = decideBody({ snapshot: { ...snapshot(), measure_name: 'x'.repeat(LIMITS.maxRequestBytes) } });
    const res = await w.decide(await w.token(), big);
    assert.equal(res.status, 413);
    assert.equal((await res.json()).error, 'too_large');
    assert.equal(w.upstream.calls.length, 0);
});

test('more questions than the cap is refused with 413', async () => {
    const w = await world();
    const many = {};
    for (let i = 0; i < LIMITS.maxQuestions + 1; i++) many['q' + i] = { type: 'choice' };
    // A point that kept the general bound of eight: measure.folder_and_format now takes a whole chunk of a table, so the
    // general bound is proved on one that did not ask for more (m11/jev-batch).
    const res = await w.decide(await w.token(), decideBody({ point: 'relationship.join', questions: many, schemaVersion: 1 }));
    assert.equal(res.status, 413);
    assert.equal((await res.json()).error, 'too_many_questions');
});

// --- the budget -----------------------------------------------------------------------------------------

test('GET /v1/budget answers used, cap and resetsAt', async () => {
    const w = await world();
    await w.decide(await w.token());
    const res = await w.budget(await w.token());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.used, 1200);
    assert.equal(body.cap, LIMITS.licenceMonthlyTokens);
    assert.equal(body.resetsAt, '2026-10-01T00:00:00.000Z');
});

test('the shipped licence cap is 47.6 million input tokens a month: USD 2 a user at USD 0.042 per million (Kane, 1 Oct)', () => {
    assert.equal(LIMITS.licenceMonthlyTokens, 47_600_000);
    assert.ok(Math.abs(LIMITS.licenceMonthlyTokens * 0.042 / 1_000_000 - 2) < 0.01);
});

test('the house cap is the CONFIGURED licence cap times the count, not the shipped default times the count', async () => {
    // Two licences, a cap of 1300 each and a count of 1: the house holds 1 x 1300. The first call settles 1200 of it, so the second
    // licence (whose own ledger is empty) is refused by the HOUSE. Multiplying the shipped default would have let it through.
    const w = await world({ licenceTokens: 1300, licenceCount: 1 });
    assert.equal((await w.decide(await w.token())).status, 200);
    const other = await w.token({ sid: 'sub_test_2' });
    const res = await w.decide(other, decideBody({ idempotencyKey: 'idem-house-2' }));
    assert.equal(res.status, 503, 'the house had 1300 (1 x 1300); with the default it would have had 47.6 million and let this through');
    assert.equal((await res.json()).error, 'house_budget_spent');
});

test('the used figure resets when the server clock rolls into a new month', async () => {
    const w = await world();
    await w.decide(await w.token());
    w.clock.now = Date.UTC(2026, 9, 2, 1, 0, 0);
    const body = await (await w.budget(await w.token({ exp: 0 }))).json();
    assert.equal(body.used, 0);
    assert.equal(body.resetsAt, '2026-11-01T00:00:00.000Z');
});

test('a licence past its cap is refused with 402 and nothing is forwarded', async () => {
    // 1300 is chosen against the real estimate for this body (about 170 input tokens) and the 1200 the fake
    // vendor reports: the first call fits, the second cannot be held on what is left.
    const w = await world({ licenceTokens: 1300 });
    assert.equal((await w.decide(await w.token())).status, 200);
    const res = await w.decide(await w.token(), decideBody({ idempotencyKey: 'idem-2' }));
    assert.equal(res.status, 402);
    assert.equal((await res.json()).error, 'licence_budget_spent');
    assert.equal(w.upstream.calls.length, 1);
});

test('the house cap refuses with 503 even when the licence still has room', async () => {
    const w = await world({ licenceTokens: 10_000_000, licenceCount: 0 });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 503);
    assert.equal((await res.json()).error, 'house_budget_spent');
    assert.equal(w.upstream.calls.length, 0);
    const mine = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(mine.open.length, 0, 'a reservation refused by the house cap must be released, not left held');
    assert.equal(mine.done[0].state, 'released');
});

test('two near-limit requests race and exactly one wins; the loser is refused', async () => {
    let release;
    const gate = new Promise((r) => { release = r; });
    // One request fits in what is left, two do not. The upstream holds both calls open so the race is real.
    // One request estimates at about 215 tokens (the body length over three, worker.mjs), so 300 holds
    // one and not two. It was 200 while the snapshot carried a short formula; the formula facts that
    // replaced it (F-229) are longer on the wire.
    const w = await world({
        licenceTokens: 300,
        upstream: async () => { await gate; return { status: 200, body: VENDOR_REPLY }; },
    });
    const tok = await w.token();
    const a = w.decide(tok, decideBody({ idempotencyKey: 'race-a' }));
    const b = w.decide(tok, decideBody({ idempotencyKey: 'race-b' }));
    release();
    const [ra, rb] = await Promise.all([a, b]);
    const statuses = [ra.status, rb.status].sort();
    assert.deepEqual(statuses, [200, 402], 'exactly one of the two may be served');
    assert.equal(w.upstream.calls.length, 1, 'the loser must be refused before anything is forwarded');
});

test('a retry with the same idempotency key does not reserve twice', async () => {
    const w = await world();
    const tok = await w.token();
    assert.equal((await w.decide(tok)).status, 200);
    assert.equal((await w.decide(tok)).status, 200);
    const mine = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(mine.open.length + mine.done.length, 1, 'one idempotency key is one ledger entry');
    assert.equal(mine.meta.settled, 1200, 'a retry of the same call must not double the spend');
    assert.equal((await (await w.budget(tok)).json()).used, 1200);
});

test('a different idempotency key is a different charge', async () => {
    const w = await world();
    const tok = await w.token();
    await w.decide(tok);
    await w.decide(tok, decideBody({ idempotencyKey: 'idem-2' }));
    assert.equal((await (await w.budget(tok)).json()).used, 2400);
});

// --- the key is bound to the licence and to the payload ---------------------------------------------------
//
// Astra's finding 1. The rule these six prove, in one sentence: every call that reaches the vendor is
// covered by exactly one live reservation or by a cached answer that was already paid for, and a recorded
// total is the SUM of what was forwarded, never the largest single piece of it.

test('five calls with one idempotency key forward once, and every forwarded call is paid for', async () => {
    const w = await world({ upstream: () => ({ status: 200, body: REPLY_500 }) });
    const tok = await w.token();

    const bodies = [];
    for (let i = 0; i < 5; i++) {
        const res = await w.decide(tok);
        assert.equal(res.status, 200);
        bodies.push(await res.text());
    }

    assert.deepEqual([...new Set(bodies)], [REPLY_500], 'every caller gets the same answer byte for byte');
    assert.equal(w.upstream.calls.length, 1, 'an answer already paid for is served from the ledger, not bought again');
    const used = (await (await w.budget(tok)).json()).used;
    assert.equal(used, 500 * w.upstream.calls.length, 'recorded spend must cover every forwarded call');
    assert.equal((await dumpLedger(w.ledgers, 'house')).meta.settled, 500, 'the house records the same one call');
});

test('the same idempotency key with a different payload is refused and nothing more is forwarded', async () => {
    const w = await world({ upstream: () => ({ status: 200, body: REPLY_500 }) });
    const tok = await w.token();
    assert.equal((await w.decide(tok)).status, 200);

    const different = decideBody({ snapshot: { ...snapshot(), measure_name: 'Total Cost' } });
    const res = await w.decide(tok, different);
    assert.equal(res.status, 409);
    assert.equal((await res.json()).error, 'idempotency_reused');
    assert.equal(w.upstream.calls.length, 1, 'a reused reference must never buy a second, different answer');
});

test('a second call under a key that is still in flight is refused, not forwarded', async () => {
    let release;
    const gate = new Promise((r) => { release = r; });
    const w = await world({ upstream: async () => { await gate; return { status: 200, body: REPLY_500 }; } });
    const tok = await w.token();

    const first = w.decide(tok);
    await new Promise((r) => setTimeout(r, 20));
    // Started, not awaited: a relay that wrongly forwards it would block on the same gate and the test
    // would hang instead of failing, which is not a result anyone can read.
    const second = w.decide(tok);
    await new Promise((r) => setTimeout(r, 20));
    release();

    const res = await second;
    assert.equal(res.status, 409);
    assert.equal((await res.json()).error, 'already_in_flight');
    assert.equal(w.upstream.calls.length, 1);
    assert.equal((await first).status, 200);
});

test('a released reservation is never retried into a forward with nothing recorded', async () => {
    // The house cap refuses the first call after the licence hold is taken, so that hold is released.
    const w = await world({ licenceCount: 0, upstream: () => ({ status: 200, body: REPLY_500 }) });
    const tok = await w.token();
    const refused = await w.decide(tok);
    assert.equal(refused.status, 503);
    assert.equal((await refused.json()).error, 'house_budget_spent');
    assert.equal(w.upstream.calls.length, 0);

    w.env.RELAY_LICENCE_COUNT = '10';
    const served = await w.decide(tok);
    assert.equal(served.status, 200);
    assert.equal(w.upstream.calls.length, 1);
    assert.equal((await (await w.budget(tok)).json()).used, 500,
        'the forward is covered by a fresh reservation, never by a released one');
});

test('a retry after a stuck call is a second charge, and the ledger adds them', async () => {
    let failing = true;
    const w = await world({
        upstream: () => { if (failing) throw timeoutError(); return { status: 200, body: REPLY_500 }; },
    });
    const tok = await w.token();
    assert.equal((await w.decide(tok)).status, 504);

    failing = false;
    assert.equal((await w.decide(tok)).status, 200);
    assert.equal(w.upstream.calls.length, 2);

    const mine = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(mine.open.length, 1, 'the stuck attempt still holds its estimate');
    assert.equal(mine.done.length, 1, 'the served attempt is its own settled entry');
    assert.equal((await (await w.budget(tok)).json()).used, mine.open[0].estimate + 500,
        'two forwards are two charges added together, not the larger of the two');
});

test('two licences using the same idempotency key are billed separately on the house ledger', async () => {
    const w = await world({ upstream: () => ({ status: 200, body: REPLY_500 }) });
    const a = await w.token({ sid: 'sub_a' });
    const b = await w.token({ sid: 'sub_b' });
    assert.equal((await w.decide(a)).status, 200);
    assert.equal((await w.decide(b)).status, 200);
    assert.equal(w.upstream.calls.length, 2);

    assert.equal((await dumpLedger(w.ledgers, 'house')).meta.settled, 1000,
        'one caller key must not let a second licence ride on the first licence house entry');
    assert.equal((await (await w.budget(a)).json()).used, 500);
    assert.equal((await (await w.budget(b)).json()).used, 500);
});

test('the per-licence concurrency cap of six refuses the seventh call', async () => {
    let release;
    const gate = new Promise((r) => { release = r; });
    const w = await world({ upstream: async () => { await gate; return { status: 200, body: VENDOR_REPLY }; } });
    const tok = await w.token();
    const inflight = [];
    for (let i = 0; i < LIMITS.perSidConcurrency; i++) inflight.push(w.decide(tok, decideBody({ idempotencyKey: 'c' + i })));
    // Let the six reservations land before the seventh asks.
    await new Promise((r) => setTimeout(r, 20));
    const seventh = await w.decide(tok, decideBody({ idempotencyKey: 'c-extra' }));
    assert.equal(seventh.status, 429);
    assert.equal((await seventh.json()).error, 'too_many_in_flight');
    release();
    await Promise.all(inflight);
});

// --- upstream failures ----------------------------------------------------------------------------------

test('an upstream timeout leaves a stuck reservation that the scheduled handler settles as spent', async () => {
    const w = await world({ upstream: () => { throw timeoutError(); } });
    const tok = await w.token();
    const res = await w.decide(tok);
    assert.equal(res.status, 504);
    assert.equal((await res.json()).error, 'upstream_timeout');

    const afterCall = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(afterCall.open.length, 1);
    assert.equal(afterCall.open[0].state, 'stuck', 'a call that may have been charged is never released');
    assert.equal(afterCall.meta.settled, 0);

    // An hour has not passed yet, so the sweep leaves it alone.
    w.clock.now = w.clock.now + 30 * 60_000;
    await w.relay.scheduled({ scheduledTime: w.clock.now }, w.env, { waitUntil() {} });
    assert.equal((await dumpLedger(w.ledgers, 'sid:sub_test_1')).open[0].state, 'stuck');

    w.clock.now = w.clock.now + 61 * 60_000;
    await w.relay.scheduled({ scheduledTime: w.clock.now }, w.env, { waitUntil() {} });
    const swept = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(swept.open.length, 0);
    assert.equal(swept.done[0].state, 'settled');
    assert.ok(swept.meta.settled > 0, 'a stuck reservation settles as spent, it is never forgiven');
    assert.equal(swept.done[0].settledBy, 'sweep');
});

test('an upstream 5xx also leaves the reservation stuck', async () => {
    const w = await world({ upstream: () => ({ status: 503, body: 'upstream down' }) });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 502);
    assert.equal((await res.json()).error, 'upstream_failed');
    const mine = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(mine.open[0].state, 'stuck');
});

test('an upstream 4xx settles the reservation as spent, because the vendor read the request', async () => {
    const w = await world({ upstream: () => ({ status: 400, body: 'bad request' }) });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 502);
    const mine = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    assert.equal(mine.done[0].state, 'settled', 'a rejected call still cost the estimate: the vendor saw it');
});

test('a reservation left in reserved by a crash is released by the sweep, not counted as spent', async () => {
    const w = await world();
    const ledger = w.env.LEDGER.get(w.env.LEDGER.idFromName('sid:orphan'));
    await ledger.fetch('https://ledger/reserve', {
        method: 'POST',
        body: JSON.stringify({ id: 'orphan-1', estimate: 500, cap: 1e9, concurrencyLimit: 6, now: T0 }),
    });
    await w.env.RELAY_KV.put('led:sid:orphan', '1');
    w.clock.now = T0 + 2 * 3600_000;
    await w.relay.scheduled({ scheduledTime: w.clock.now }, w.env, { waitUntil() {} });
    const swept = await dumpLedger(w.ledgers, 'sid:orphan');
    assert.equal(swept.done[0].state, 'released');
    assert.equal(swept.meta.settled, 0);
});

// --- the kill switch ------------------------------------------------------------------------------------

test('the global kill switch refuses every decide with 503', async () => {
    const w = await world({ control: { 'kill:global': '1' } });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 503);
    assert.equal((await res.json()).error, 'paused');
    assert.equal(w.upstream.calls.length, 0);
});

test('the per-point kill switch refuses only that point', async () => {
    const w = await world({ control: { ['kill:point:' + POINT]: '1' } });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 503);
    assert.equal((await res.json()).error, 'point_paused');
    assert.equal(w.upstream.calls.length, 0);
});

test('the served-model kill switch refuses before anything is forwarded', async () => {
    const w = await world({ control: { ['kill:model:' + MODEL]: '1' } });
    const res = await w.decide(await w.token());
    assert.equal(res.status, 503);
    assert.equal((await res.json()).error, 'model_paused');
    assert.equal(w.upstream.calls.length, 0);
});

test('the kill switch does not block a budget read', async () => {
    const w = await world({ control: { 'kill:global': '1' } });
    assert.equal((await w.budget(await w.token())).status, 200);
});

// --- routing --------------------------------------------------------------------------------------------

test('an unknown route is 404 and a wrong method is 405', async () => {
    const w = await world();
    const tok = await w.token();
    const bad = await w.relay.fetch(new Request('https://relay/v1/nope', { headers: { authorization: 'Bearer ' + tok } }), w.env, {});
    assert.equal(bad.status, 404);
    const wrong = await w.relay.fetch(new Request('https://relay/v1/decide', { method: 'GET', headers: { authorization: 'Bearer ' + tok } }), w.env, {});
    assert.equal(wrong.status, 405);
});

// --- logging --------------------------------------------------------------------------------------------

test('the log line carries the point, a hashed sid, tokens, latency and outcome, and no content', async () => {
    const w = await world();
    const cap = captureLogs();
    try {
        await w.decide(await w.token());
    } finally { cap.restore(); }
    assert.equal(cap.lines.length, 1, 'one request, one line');
    const line = cap.lines[0];
    const entry = JSON.parse(line);
    assert.equal(entry.point, POINT);
    assert.equal(entry.outcome, 'served');
    assert.equal(entry.tokens, 1200);
    assert.equal(typeof entry.ms, 'number');
    assert.match(entry.sid, /^[0-9a-f]{16}$/, 'the subscription id is hashed, never logged in the clear');
    assert.ok(!line.includes('sub_test_1'));
    assert.ok(!line.includes('buyer@example.com'));
    assert.ok(!line.includes('Total Sales'), 'the snapshot never reaches the log');
    assert.ok(!line.includes('SUM(Sales'), 'the expression never reaches the log');
    assert.ok(!line.includes('display_folder'), 'the questions never reach the log');
    assert.ok(!line.includes('Finance'), 'the reply never reaches the log');
});

test('a refusal is logged with its outcome and still no content', async () => {
    const w = await world({ status: { 'sid:sub_test_1': 'cancelled' } });
    const cap = captureLogs();
    try {
        await w.decide(await w.token());
    } finally { cap.restore(); }
    const entry = JSON.parse(cap.lines[0]);
    assert.equal(entry.outcome, 'subscription_inactive');
    assert.equal(entry.status, 403);
    assert.ok(!cap.lines[0].includes('Total Sales'));
});

// --- one request per table (m11/jev-batch) -------------------------------------------------------------------
//
// The Plan's four per-object families (measure.folder_and_format, column.format, column.summarize, ai.schema) now ask a table's
// objects in ONE request: the table's shared facts once, a list of objects, and one question per object named
// "<question>:<object ref>". Only those four points may do it, only up to what a chunk needs, and everything else keeps its bound.

const BATCHED = {
    'measure.folder_and_format': { cap: 24, questions: ['display_folder', 'format_family'], field: 'measures', schemaVersion: 2 },
    'column.format': { cap: 30, questions: ['format_family'], field: 'columns', schemaVersion: 1 },
    'column.summarize': { cap: 20, questions: ['summarize_by'], field: 'columns', schemaVersion: 1 },
    'ai.schema': { cap: 30, questions: ['ai_schema'], field: 'objects', schemaVersion: 1 },
};

/** One question per object, in the engine's own naming: "<question>:<object ref>". */
function batchedQuestions(point, objects) {
    const out = {};
    for (let i = 0; i < objects; i++) {
        for (const q of BATCHED[point].questions) {
            out[q + ':column:Sales/Col' + i] = { type: 'choice', instructions: 'For the column `columns[' + i + ']`: pick one', criteria: { a: 'the first', b: 'the second' } };
        }
    }
    return out;
}

function batchedBody(point, objects, extra = {}) {
    const entries = [];
    for (let i = 0; i < objects; i++) entries.push({ name: 'Col' + i, data_type: 'Double' });
    return JSON.stringify({
        point,
        snapshot: { table_name: 'Sales', [BATCHED[point].field]: entries },
        questions: batchedQuestions(point, objects),
        idempotencyKey: 'batch-' + point + '-' + objects,
        schemaVersion: BATCHED[point].schemaVersion,
        ...extra,
    });
}

for (const [point, spec] of Object.entries(BATCHED)) {
    const perObject = spec.questions.length;
    const most = Math.floor(spec.cap / perObject);

    test(point + ' takes a whole chunk of a table in one request, one question per object', async () => {
        const w = await world();
        const res = await w.decide(await w.token(), batchedBody(point, most));
        assert.equal(res.status, 200);
        assert.equal(w.upstream.calls.length, 1);
        assert.equal(Object.keys(w.upstream.calls[0].json.questions).length, most * perObject);
    });

    test(point + ' refuses one question more than a chunk needs', async () => {
        const w = await world();
        const res = await w.decide(await w.token(), batchedBody(point, most + 1));
        assert.equal(res.status, 413);
        assert.equal((await res.json()).error, 'too_many_questions');
        assert.equal(w.upstream.calls.length, 0);
    });

    test(point + ' refuses a question whose name is not one of its own followed by an object', async () => {
        const w = await world();
        const bad = JSON.parse(batchedBody(point, 1));
        bad.questions = { ['rename_to:column:Sales/Col0']: { type: 'choice' } };
        const res = await w.decide(await w.token(), JSON.stringify(bad));
        assert.equal(res.status, 400);
        assert.equal((await res.json()).error, 'undeclared_question');
    });

    test(point + ' refuses a batched name with nothing after the colon', async () => {
        const w = await world();
        const bad = JSON.parse(batchedBody(point, 1));
        bad.questions = { [spec.questions[0] + ':']: { type: 'choice' } };
        const res = await w.decide(await w.token(), JSON.stringify(bad));
        assert.equal(res.status, 400);
        assert.equal((await res.json()).error, 'undeclared_question');
    });

    test(point + ' still refuses a snapshot field it does not declare, batched or not', async () => {
        const w = await world();
        const bad = JSON.parse(batchedBody(point, 1));
        bad.snapshot.source_query = 'select * from Sales';
        const res = await w.decide(await w.token(), JSON.stringify(bad));
        assert.equal(res.status, 400);
        assert.equal((await res.json()).error, 'undeclared_field');
    });

    test(point + ' still takes its own fixed question names, one object at a time', async () => {
        const w = await world();
        const fixed = {};
        for (const q of spec.questions) fixed[q] = { type: 'choice', instructions: 'pick', criteria: { a: 'x', b: 'y' } };
        const body = JSON.parse(batchedBody(point, 1));
        body.questions = fixed;
        body.idempotencyKey = 'fixed-' + point;
        const res = await w.decide(await w.token(), JSON.stringify(body));
        assert.equal(res.status, 200);
    });
}

test('a point that is not one of the four batched ones does not take a batched name, and keeps the general bound', async () => {
    const w = await world();
    for (const [point, question, version] of [['column.category', 'data_category', 1], ['ask.route', 'tool', 1], ['date.table', 'date_table', 1], ['relationship.join', 'join_to', 1]]) {
        const res = await w.decide(await w.token(), JSON.stringify({
            point, snapshot: {}, questions: { [question + ':column:Sales/Amount']: { type: 'choice' } },
            idempotencyKey: 'not-batched-' + point, schemaVersion: version,
        }));
        assert.equal(res.status, 400, point);
        assert.equal((await res.json()).error, 'undeclared_question', point);
    }
    const many = {};
    for (let i = 0; i < LIMITS.maxQuestions + 1; i++) many['data_category'] = { type: 'choice' };
    for (const point of ['column.category', 'relationship.join']) {
        const over = {};
        for (let i = 0; i < LIMITS.maxQuestions + 1; i++) over['q' + i] = { type: 'choice' };
        const res = await w.decide(await w.token(), JSON.stringify({
            point, snapshot: {}, questions: over, idempotencyKey: 'over-' + point, schemaVersion: 1,
        }));
        assert.equal(res.status, 413, point);
    }
});

test('a bigger request is held for what it is: the estimate is the body over three, and it settles to what the vendor reports', async () => {
    // The hold must grow with the request, or a chunk of thirty questions would be reserved like a single one and the licence's month
    // would overspend before anyone saw it. It is the same pessimistic rule as before (worker.mjs estimateInputTokens): the size of
    // the body the relay forwards over three, settled against the vendor's own figure when it reports one.
    const w = await world({
        upstream: () => ({ status: 200, body: JSON.stringify({ answers: {}, usage: { input_tokens: 4000, output_tokens: 300 }, model: MODEL }) }),
    });
    const tok = await w.token();
    const small = await w.decide(tok, batchedBody('column.format', 1, { idempotencyKey: 'small' }));
    assert.equal(small.status, 200);
    const big = await w.decide(tok, batchedBody('column.format', 30, { idempotencyKey: 'big' }));
    assert.equal(big.status, 200);

    const sizeOf = (call) => Math.floor(JSON.stringify({ model: MODEL, state: call.json.state, questions: call.json.questions }).length / 3);
    const [a, b] = w.upstream.calls;
    assert.ok(sizeOf(b) > 10 * sizeOf(a), 'thirty questions hold far more than one');
    const mine = await dumpLedger(w.ledgers, 'sid:sub_test_1');
    const held = Object.fromEntries(mine.done.map((e) => [e.id, e]));
    const estimates = mine.done.map((e) => e.estimate).sort((x, y) => x - y);
    assert.equal(estimates[0], sizeOf(a));
    assert.equal(estimates[1], sizeOf(b));
    assert.equal(mine.done.every((e) => e.state === 'settled'), true, JSON.stringify(Object.keys(held)));
    assert.equal(Number(big.headers.get('x-relay-tokens-used')), 4000, 'the licence is charged what the vendor reported, not the estimate');
});
