// The ledger: one Durable Object per budget identity, plus one named "house" for the whole deployment.
//
// Why a Durable Object and not KV. Astra named the one failure that costs real money: forwarding before an
// atomic reservation, so two parallel requests both read the same balance and both spend it. KV is
// eventually consistent and has no compare-and-set, so it cannot hold that line. A Durable Object handles
// one request at a time for one object, which is exactly the lock this needs and the reason the reservation
// lives in here rather than in the worker.
//
// The money rule, in one sentence: every call that reaches the vendor is covered by exactly one live
// reservation, or it is a cached answer that was already paid for, and a recorded total is the SUM of the
// calls that were made and never the largest single one of them.
//
// Attempts, not keys. A caller's idempotency key is an IDEMPOTENCY RECORD (k:), not a reservation. Each
// forward gets its own reservation entry under its own attempt id (<key>#1, <key>#2). That separation is
// what Astra's five-calls-one-key probe broke: the old ledger handed a retry the existing entry, the worker
// forwarded anyway, and five calls of 500 tokens were recorded as 500. It also means a released or stuck
// reservation is never resurrected. It is terminal; a retry that is allowed out gets a NEW hold.
//
// What one key gets you, and what it does not:
//
//   same key, same payload, an answer already bought    the cached answer, no forward, no new charge
//   same key, same payload, an attempt still in flight  refused; a second forward would be uncovered
//   same key, same payload, nothing to serve            a NEW attempt, held and charged in its own right
//   same key, a DIFFERENT payload                       refused outright; one reference, one question
//
// The state machine, and what each state means about money:
//
//   reserved    capacity is held; NOTHING has left the relay. Safe to release.
//   forwarded   the vendor has the request. It may already have been charged. Never release from here.
//   settled     terminal. The charge is known (from the reply) or assumed (the reserved estimate).
//   released    terminal. Nothing was sent, so the hold is given back.
//   stuck       the vendor has it and we do not know the outcome: a timeout, a 5xx, or a crash between
//               forwarding and settling. Terminal-adjacent: it holds its estimate until the sweep settles
//               it as spent an hour later. A client timeout NEVER refunds a possibly charged call.
//
//   reserved ──forward──► forwarded ──settle──► settled
//      │                     │
//      └──release──► released└──stick/sweep──► stuck ──sweep──► settled
//
// Accounting. used = settled tokens this window + the estimates still held by open entries. A reservation
// is admitted only when used + estimate <= cap, so the cap is a ceiling rather than a line the last call is
// allowed to cross. The estimate deliberately over-counts (the body length over three, the same rule
// JevDecisionOracle.EstimateInputTokens uses): a budget that guesses low is a budget that overspends.
//
// Storage layout, one key per entry so the object never grows a single value it cannot write:
//   meta        { window, settled }
//   k:<key>     an idempotency record: the payload digest, how many attempts it has had, which attempt is
//               live, and the answer if one was bought. Kept for entryRetentionMs.
//   e:<attempt> an OPEN entry (reserved, forwarded, stuck). Scanned on every reservation, so it stays small.
//   d:<attempt> a FINISHED entry (settled, released), kept for entryRetentionMs so the record it belongs to
//               and the entries behind it are pruned together.

import { LIMITS } from './templates.mjs';

export const LEDGER_STATES = ['reserved', 'forwarded', 'settled', 'released', 'stuck'];
const OPEN_STATES = new Set(['reserved', 'forwarded', 'stuck']);
const IN_FLIGHT_STATES = new Set(['reserved', 'forwarded']);

/** Whether an entry can still be a call that is really running. The worker cuts its vendor call at
 *  upstreamTimeoutMs, so an entry older than that plus a short grace belongs to a worker that crashed or
 *  went away without settling it. Counting it as running locked a licence for the hour until the sweep. It
 *  is not "running" for concurrency, but it is still OPEN, so it keeps holding its estimate for the budget. */
function isRunning(entry, nowMs) {
    if (!IN_FLIGHT_STATES.has(entry.state)) return false;
    const since = entry.forwardedAt || entry.createdAt || 0;
    return nowMs - since < LIMITS.upstreamTimeoutMs + LIMITS.inFlightGraceMs;
}

/** The month window on the SERVER clock, never the client's. */
export function windowKey(nowMs) {
    const d = new Date(nowMs);
    return d.getUTCFullYear() + '-' + String(d.getUTCMonth() + 1).padStart(2, '0');
}

/** The first instant of the next month, UTC, as ISO. */
export function resetsAt(nowMs) {
    const d = new Date(nowMs);
    return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 1)).toISOString();
}

export class LicenceLedger {
    constructor(state, env) {
        this.state = state;
        this.env = env;
    }

    async fetch(request) {
        const op = new URL(request.url).pathname.replace(/^\/+/, '');
        let body = {};
        try { body = await request.json(); } catch { body = {}; }
        switch (op) {
            case 'reserve': return json(await this.reserve(body));
            case 'forward': return json(await this.mark(body.id, 'forwarded', body.now));
            case 'stick': return json(await this.mark(body.id, 'stuck', body.now));
            case 'settle': return json(await this.settle(body));
            case 'release': return json(await this.release(body));
            case 'budget': return json(await this.budget(body));
            case 'sweep': return json(await this.sweep(body));
            default: return new Response('unknown ledger op', { status: 404 });
        }
    }

    // --- reads ------------------------------------------------------------------------------------------

    /** Roll the window on the server clock. Settled spend resets; OPEN entries carry over still holding
     *  their estimates, because a call in flight across midnight on the first is still a call in flight. */
    async ensureWindow(nowMs) {
        const wanted = windowKey(nowMs);
        let meta = await this.state.storage.get('meta');
        if (!meta || meta.window !== wanted) {
            meta = { window: wanted, settled: 0 };
            await this.state.storage.put('meta', meta);
        }
        return meta;
    }

    async openEntries() {
        return [...(await this.state.storage.list({ prefix: 'e:' })).values()];
    }

    async find(id) {
        return (await this.state.storage.get('e:' + id)) ?? (await this.state.storage.get('d:' + id));
    }

    async totals(nowMs) {
        const meta = await this.ensureWindow(nowMs);
        const open = await this.openEntries();
        let held = 0;
        let inFlight = 0;
        for (const entry of open) {
            if (OPEN_STATES.has(entry.state)) held += entry.estimate;
            if (isRunning(entry, nowMs)) inFlight++;
        }
        return { meta, held, inFlight, used: meta.settled + held };
    }

    // --- the state machine ------------------------------------------------------------------------------

    /**
     * Take a hold.
     *
     * Two callers, two shapes, and they are not interchangeable:
     *   { key, digest, ... }  the LICENCE ledger. It owns the idempotency record and allocates the attempt
     *                         id, so it is the one place that can say "already bought" or "already running".
     *   { id, ... }           the HOUSE ledger, booking the attempt id the licence ledger just allocated.
     *                         That id already carries the licence inside it, which is why one caller's key
     *                         can no longer collide with another licence's on the shared house object.
     */
    async reserve({ id, key, digest, estimate, cap, concurrencyLimit = 0, now }) {
        const { meta, used, inFlight } = await this.totals(now);
        const shared = { used, cap, resetsAt: resetsAt(now) };

        if (!key) {
            // Booking one already-allocated attempt. An attempt id is used once, so an existing entry here
            // can only be this same attempt being booked twice by a retried worker step: hold it once.
            const already = await this.find(id);
            if (already) {
                return { ok: true, id, duplicate: true, state: already.state, estimate: already.estimate, ...shared };
            }
            if (used + estimate > cap) return { ok: false, error: 'cap', ...shared };
            await this.putEntry({ id, key: null, estimate, window: meta.window, now });
            return { ok: true, id, duplicate: false, state: 'reserved', estimate,
                     used: used + estimate, cap, resetsAt: shared.resetsAt };
        }

        const record = await this.state.storage.get('k:' + key);
        if (record) {
            // One reference, one question. A reused key carrying different words is a caller bug or an
            // attack, and either way it must not buy a second answer under the first one's cover.
            if (record.digest !== digest) return { ok: false, error: 'key_conflict', ...shared };

            // Already bought and still held. This is the only path that serves an answer without a hold,
            // and it is safe precisely because the hold that paid for it was settled.
            if (record.cached) return { ok: true, cached: record.cached, ...shared };

            if (record.live) {
                const live = await this.state.storage.get('e:' + record.live);
                if (live && isRunning(live, now)) return { ok: false, error: 'in_flight', ...shared };
            }
        }

        if (concurrencyLimit > 0 && inFlight >= concurrencyLimit) {
            return { ok: false, error: 'concurrency', ...shared, inFlight };
        }
        if (used + estimate > cap) return { ok: false, error: 'cap', ...shared };

        const attempts = (record?.attempts ?? 0) + 1;
        const attemptId = key + '#' + attempts;
        await this.putEntry({ id: attemptId, key, estimate, window: meta.window, now });
        await this.state.storage.put('k:' + key, {
            key,
            digest,
            attempts,
            live: attemptId,
            cached: record?.cached ?? null,
            createdAt: record?.createdAt ?? now,
            lastAt: now,
        });
        return { ok: true, id: attemptId, duplicate: false, state: 'reserved', estimate,
                 used: used + estimate, cap, resetsAt: shared.resetsAt };
    }

    async putEntry({ id, key, estimate, window, now }) {
        await this.state.storage.put('e:' + id, {
            id, key, state: 'reserved', estimate, actual: 0, window,
            createdAt: now, forwardedAt: 0, stuckAt: 0, settledAt: 0, settledBy: null,
        });
    }

    /** Update the idempotency record an attempt belongs to. A house entry has no key and no record. */
    async updateRecord(key, id, now, mutate) {
        if (!key) return;
        const record = await this.state.storage.get('k:' + key);
        if (!record) return;
        if (record.live === id) record.live = null;
        record.lastAt = now;
        if (mutate) mutate(record);
        await this.state.storage.put('k:' + key, record);
    }

    /** reserved -> forwarded, or forwarded -> stuck. Both are one-way: nothing walks back toward release. */
    async mark(id, state, now) {
        const entry = await this.state.storage.get('e:' + id);
        if (!entry) return { ok: false, error: 'unknown_entry' };
        if (state === 'forwarded' && entry.state !== 'reserved') return { ok: true, state: entry.state };
        if (state === 'stuck' && entry.state !== 'forwarded') return { ok: true, state: entry.state };
        entry.state = state;
        if (state === 'forwarded') entry.forwardedAt = now;
        if (state === 'stuck') entry.stuckAt = now;
        await this.state.storage.put('e:' + id, entry);
        // A stuck attempt is over as far as this key is concerned: its outcome will never be known, so the
        // key is free to be retried into a new hold rather than riding on this one.
        if (state === 'stuck') await this.updateRecord(entry.key, id, now);
        return { ok: true, state };
    }

    /** Settle ONE attempt with its real cost. Totals add because attempts are separate entries; re-settling
     *  the same attempt can only ever RAISE it, because a double settle of one call is still one call. */
    async settle({ id, actual, now, by = 'reply', cache = null }) {
        const meta = await this.ensureWindow(now);
        const open = await this.state.storage.get('e:' + id);
        const done = open ? null : await this.state.storage.get('d:' + id);
        const entry = open ?? done;
        if (!entry) return { ok: false, error: 'unknown_entry' };

        const amount = Math.max(1, Math.floor(Number(actual) || 0) || entry.estimate);
        if (entry.state === 'settled') {
            if (amount > entry.actual) {
                meta.settled += amount - entry.actual;
                entry.actual = amount;
                await this.state.storage.put('meta', meta);
                await this.state.storage.put('d:' + id, entry);
            }
        } else if (entry.state === 'released') {
            return { ok: false, error: 'already_released' };
        } else {
            entry.state = 'settled';
            entry.actual = amount;
            entry.settledAt = now;
            entry.settledBy = by;
            meta.settled += amount;
            await this.state.storage.put('meta', meta);
            await this.state.storage.put('d:' + id, entry);
            await this.state.storage.delete('e:' + id);
        }

        // An answer becomes cacheable only once the call that bought it is settled, so a retry served from
        // the cache is never an unpaid one.
        await this.updateRecord(entry.key, id, now, (record) => {
            if (cache) record.cached = cache;
        });

        const after = await this.totals(now);
        return { ok: true, state: 'settled', actual: entry.actual, used: after.used };
    }

    /** Give the hold back. Only legal from reserved: once the vendor has it, money may already be gone. */
    async release({ id, now }) {
        const entry = await this.state.storage.get('e:' + id);
        if (!entry) return { ok: false, error: 'unknown_entry' };
        if (entry.state !== 'reserved') return { ok: false, error: 'not_releasable', state: entry.state };
        entry.state = 'released';
        entry.settledAt = now;
        await this.state.storage.put('d:' + id, entry);
        await this.state.storage.delete('e:' + id);
        await this.updateRecord(entry.key, id, now);
        const after = await this.totals(now);
        return { ok: true, state: 'released', used: after.used };
    }

    async budget({ cap, now }) {
        const { used } = await this.totals(now);
        return { used, cap, resetsAt: resetsAt(now) };
    }

    /**
     * The reconcile job.
     *   forwarded, older than staleMs                             -> stuck (a crash before settling)
     *   stuck, forwarded older than staleMs                       -> settled at the estimate, marked swept
     *   reserved, older than staleMs                              -> released (nothing ever left)
     *   finished, older than retainMs                             -> pruned
     *   an idempotency record with nothing live, older than retainMs -> pruned, cached answer and all
     * The hour is measured from forwardedAt in both stuck paths, so a timeout that already marked itself
     * stuck and a crash that did not are treated the same way.
     */
    async sweep({ now, staleMs = LIMITS.stuckAfterMs, retainMs = LIMITS.entryRetentionMs }) {
        const out = { settled: 0, released: 0, stuck: 0, pruned: 0 };

        for (const entry of await this.openEntries()) {
            if (entry.state === 'reserved') {
                if (now - entry.createdAt >= staleMs) { await this.release({ id: entry.id, now }); out.released++; }
                continue;
            }
            const since = entry.forwardedAt || entry.createdAt;
            if (now - since < staleMs) continue;
            if (entry.state === 'forwarded') { await this.mark(entry.id, 'stuck', now); out.stuck++; }
            await this.settle({ id: entry.id, actual: entry.estimate, now, by: 'sweep' });
            out.settled++;
        }

        for (const [key, entry] of await this.state.storage.list({ prefix: 'd:' })) {
            if (now - (entry.settledAt || entry.createdAt) >= retainMs) {
                await this.state.storage.delete(key);
                out.pruned++;
            }
        }

        // A record does not outlive its entries. Once the last attempt is pruned the cached answer goes with
        // it, so the relay never serves an answer it can no longer point at a settled charge for.
        for (const [key, record] of await this.state.storage.list({ prefix: 'k:' })) {
            if (record.live) continue;
            if (now - (record.lastAt || record.createdAt || 0) < retainMs) continue;
            await this.state.storage.delete(key);
            out.pruned++;
        }
        return out;
    }
}

function json(value) {
    return new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } });
}
