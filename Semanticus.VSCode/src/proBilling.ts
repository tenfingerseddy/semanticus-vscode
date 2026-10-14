// ===================================================================================================
// Semanticus Pro billing — the app half of the Stripe slice (docs/redesign/stripe-billing.md).
//
// NOTHING IN THIS FILE IMPORTS vscode, on purpose. The parts that are easy to get wrong — the collect
// poll, its 425/404/410 arms, the cancel, the fifteen-minute give-up, the unverified payload read — are
// all here so a plain node test can drive them against a real stub HTTP server. extension.ts owns the
// quick pick, the progress notice, the browser hand-off and the licence storage; it owns no protocol.
//
// The worker contract this speaks to (tools/fulfillment/worker):
//   POST /stripe/checkout  { price, app, nonce }    -> { url }
//   GET  /stripe/collect?nonce=...                 -> { token } once, 410 after, 404 before the mint, 425 pending
//   GET  /stripe/portal    Bearer <licence token>  -> { url }
//   GET  /stripe/renew     Bearer <licence token>  -> { token }, or 204 when we hold the newest already
//
// The last two carry PROOF. A subscription id on its own is a bearer capability: whoever holds one could
// open that customer's Stripe portal and cancel the subscription. So the worker takes the licence token,
// checks its signature, and reads the subscription id out of the signed claims; a sid parameter is
// refused outright. Nothing on this side decides whether a licence is real, and nothing here needs to.
// ===================================================================================================

import { randomBytes } from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as http from 'http';
import * as https from 'https';
import { URL } from 'url';

/// The billing addresses, read from ONE place: the "semanticusBilling" block in package.json, which ships
/// inside the extension. It names the production address, the worker's name (the `name` in
/// tools/fulfillment/worker/wrangler.toml, and pro-billing.test.mjs fails if the two disagree) and, once
/// the Cloudflare account exists, that account's workers.dev subdomain. The subdomain only exists after
/// the first `npx wrangler deploy` prints it, so until someone fills it in there is simply no sandbox
/// address; there is never a half-finished one with a placeholder in it.
export interface BillingAddressConfig {
    productionUrl: string;
    workerName: string;
    workersDevSubdomain?: string;
}

const LABEL_RE = /^[a-z0-9][a-z0-9-]*$/;

export function readBillingConfig(packageJsonPath = path.join(__dirname, '..', 'package.json')): BillingAddressConfig {
    const block = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8')).semanticusBilling;
    if (!block || typeof block.productionUrl !== 'string' || typeof block.workerName !== 'string') {
        throw new Error('package.json has no usable "semanticusBilling" block.');
    }
    return block;
}

/// The sandbox worker's address, for test-mode cards, or undefined when no subdomain is configured.
export function sandboxBillingUrlFor(config: BillingAddressConfig): string | undefined {
    const subdomain = (config.workersDevSubdomain || '').trim().toLowerCase();
    if (!subdomain) return undefined;
    if (!LABEL_RE.test(subdomain)) throw new Error(`The workers.dev subdomain "${subdomain}" in semanticusBilling is not a valid host label.`);
    return `https://${config.workerName}.${subdomain}.workers.dev`;
}

const SHIPPED_BILLING = readBillingConfig();

/// Where the fulfilment worker answers. A setting overrides it (semanticus.billingUrl) so a sandbox
/// worker can be pointed at without a rebuild; this is also that setting's default in package.json.
export const PRODUCTION_BILLING_URL = SHIPPED_BILLING.productionUrl;

/// The sandbox worker, for test-mode cards, when the config names the account's subdomain. Not a default:
/// whoever tests with a Stripe test card pastes it into the setting deliberately.
export const SANDBOX_BILLING_URL: string | undefined = sandboxBillingUrlFor(SHIPPED_BILLING);

/// How often a running window asks whether its licence has been renewed. Once a day: a subscription term
/// is a month at the shortest, so this is far more often than it needs to be and still nearly free.
export const RENEWAL_CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000;

/// Poll cadence and give-up point, both from the contract. Three seconds is slow enough to be polite to
/// the worker and fast enough that a person who has just paid does not think the app has hung.
export const COLLECT_INTERVAL_MS = 3000;
export const COLLECT_TIMEOUT_MS = 15 * 60 * 1000;

/// How long an issued nonce stays collectable. A returning link older than this is refused: the checkout
/// it belonged to is long dead, so the only thing such a link can be is stale or hostile.
export const NONCE_LIFETIME_MS = COLLECT_TIMEOUT_MS;

export type BillingPlan = 'monthly' | 'yearly';

export interface BillingResponse { status: number; json: any; }
export interface BillingRequest { method?: 'GET' | 'POST'; body?: string; timeoutMs?: number; headers?: Record<string, string>; }
export type BillingFetch = (url: string, init?: BillingRequest) => Promise<BillingResponse>;

/// A worker call that failed in a way worth showing a person. `status` is 0 for a transport failure.
export class BillingError extends Error {
    constructor(message: string, readonly status = 0) { super(message); }
}

/// 32 bytes of randomness, hex, as the contract asks. The worker stores it against the Checkout Session,
/// so only the app that started the checkout can collect the token it mints.
export function newCheckoutNonce(byteLength = 32): string {
    return randomBytes(byteLength).toString('hex');
}

/// The hosts a Semanticus licence token is allowed to reach, and the ONLY ones.
///
/// HTTPS proves that nobody is listening in on the way; it proves nothing about who is at the other end.
/// The billing address is a setting, and a setting with no scope can be written by a repository's own
/// .vscode/settings.json, so opening somebody's repository could have pointed the daily renewal check at
/// their server with the saved licence riding along as a bearer token. The setting is machine scoped in
/// package.json now, which is the real fix, and this list is the second lock on the same door: even a
/// value that somehow arrives from a workspace cannot name an address this file will talk to.
///
/// Loopback stays allowed. It cannot leave the machine, and it is what lets the unit tests drive the
/// shipped request path against a stub server on 127.0.0.1.
///
/// The sandbox is pinned to the one configured address once the config names the account's subdomain, so
/// a worker of the same name on somebody else's Cloudflare account is refused. Until then the documented
/// pattern (the worker's name on any workers.dev subdomain) is what a tester can use.
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]', '::1']);

export function billingHostCheck(config: BillingAddressConfig): (hostname: string) => boolean {
    const production = new URL(config.productionUrl).hostname.toLowerCase();
    const sandboxUrl = sandboxBillingUrlFor(config);
    const worker = config.workerName.toLowerCase();
    if (!LABEL_RE.test(worker)) throw new Error(`The worker name "${worker}" in semanticusBilling is not a valid host label.`);
    const sandbox = sandboxUrl
        ? (host: string) => host === new URL(sandboxUrl).hostname
        : (host: string) => host.startsWith(worker + '.') && host.endsWith('.workers.dev')
            && LABEL_RE.test(host.slice(worker.length + 1, -'.workers.dev'.length));
    return (hostname: string) => {
        const host = (hostname || '').trim().toLowerCase().replace(/\.$/, '');
        if (!host) return false;
        if (LOOPBACK_HOSTS.has(host)) return true;
        if (host === production) return true;
        return sandbox(host);
    };
}

export const isAllowedBillingHost = billingHostCheck(SHIPPED_BILLING);

/// Build one worker URL from the configured base, and refuse a base that is not safely addressable.
/// HTTPS only, with ONE exception: plain http on loopback. That exception is what lets the unit tests
/// drive the real request path against a stub server on 127.0.0.1, and it can never reach the network.
/// The host must also be one of ours (see isAllowedBillingHost): every billing call is built here, so
/// refusing here is what makes "the token goes nowhere else" true for all four routes at once.
export function billingEndpoint(base: string, path: string, query?: Record<string, string>): string {
    const trimmed = (base || '').trim().replace(/\/+$/, '');
    if (!trimmed) throw new BillingError('No Semanticus billing address is set.');
    let parsed: URL;
    try { parsed = new URL(trimmed); } catch { throw new BillingError('The Semanticus billing address is not a valid web address.'); }
    const loopback = LOOPBACK_HOSTS.has(parsed.hostname);
    if (parsed.protocol !== 'https:' && !(parsed.protocol === 'http:' && loopback)) {
        throw new BillingError('The Semanticus billing address must start with https.');
    }
    if (!isAllowedBillingHost(parsed.hostname)) {
        throw new BillingError('Semanticus will not send your license to that address. Only the Semanticus billing service and its test sandbox are accepted.');
    }
    const url = new URL(trimmed.replace(/\/+$/, '') + path);
    for (const [key, value] of Object.entries(query ?? {})) url.searchParams.set(key, value);
    return url.toString();
}

/// The real transport. node's own http/https rather than a global fetch: the extension host's node
/// version is not ours to assume, and this keeps the stub-server tests on exactly the shipped code path.
export const nodeBillingFetch: BillingFetch = (url, init) => new Promise<BillingResponse>((resolve, reject) => {
    const target = new URL(url);
    const agent = target.protocol === 'http:' ? http : https;
    const body = init?.body;
    const request = agent.request(target, {
        method: init?.method ?? 'GET',
        headers: {
            ...(body ? { 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) } : {}),
            ...(init?.headers ?? {}),
        },
        timeout: init?.timeoutMs ?? 20000,
    }, (response) => {
        const chunks: Buffer[] = [];
        response.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
        response.on('end', () => {
            const text = Buffer.concat(chunks).toString('utf8');
            let json: any = null;
            try { json = text ? JSON.parse(text) : null; } catch { json = null; }
            resolve({ status: response.statusCode ?? 0, json });
        });
    });
    request.on('timeout', () => request.destroy(new BillingError('The Semanticus billing service did not answer in time.')));
    request.on('error', (e) => reject(new BillingError(e?.message || 'The Semanticus billing service could not be reached.')));
    if (body) request.write(body);
    request.end();
});

/// Ask the worker for a Checkout Session and get back the page to open in the browser. The returned
/// address must be plain HTTPS: we are about to hand a person to it to type card details.
export async function createCheckout(base: string, plan: BillingPlan, nonce: string, fetchJson: BillingFetch = nodeBillingFetch): Promise<string> {
    const url = billingEndpoint(base, '/stripe/checkout');
    const answer = await fetchJson(url, { method: 'POST', body: JSON.stringify({ price: plan, app: 'vscode', nonce }) });
    if (answer.status !== 200) throw new BillingError(`The Semanticus billing service could not start the checkout (${answer.status}).`, answer.status);
    const checkout = String(answer.json?.url ?? '').trim();
    if (!/^https:\/\//i.test(checkout)) throw new BillingError('The Semanticus billing service did not return a secure checkout address.', answer.status);
    return checkout;
}

/// Ask the worker for Stripe's hosted customer portal. The LICENCE is what is sent: the worker reads the
/// subscription out of the signed claims and refuses a sid parameter, so a stolen subscription id cannot
/// open somebody else's portal. Nothing is sent in the query string at all.
export async function portalLink(base: string, token: string, fetchJson: BillingFetch = nodeBillingFetch): Promise<string> {
    const trimmed = (token || '').trim();
    if (!trimmed) throw new BillingError('No Semanticus Pro licence was found to open your subscription page with.');
    const url = billingEndpoint(base, '/stripe/portal');
    const answer = await fetchJson(url, { headers: { authorization: `Bearer ${trimmed}` } });
    if (answer.status !== 200) throw new BillingError(`The Semanticus billing service could not open your subscription page (${answer.status}).`, answer.status);
    const portal = String(answer.json?.url ?? '').trim();
    if (!/^https:\/\//i.test(portal)) throw new BillingError('The Semanticus billing service did not return a secure subscription address.', answer.status);
    return portal;
}

/// The waiting half of the collect poll, as one object, so three things can end a wait: the three-second
/// timer, a cancel, and the return link arriving back in the editor. Without the third, a person who
/// clicked "return to VS Code" would still sit out the rest of the current tick for no reason.
export class CollectGate {
    private cancelled = false;
    private waiters: (() => void)[] = [];

    get isCancelled(): boolean { return this.cancelled; }

    /// The person pressed cancel. Stops the poll at its next check and wakes it now so it stops at once.
    cancel(): void { this.cancelled = true; this.release(); }

    /// The browser came back (the URI handler). Look again immediately instead of waiting out the tick.
    nudge(): void { this.release(); }

    /// Wait up to `ms`, or until cancel/nudge, whichever is first.
    wait(ms: number): Promise<void> {
        if (this.cancelled) return Promise.resolve();
        return new Promise<void>((resolve) => {
            let settled = false;
            const finish = () => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                const at = this.waiters.indexOf(finish);
                if (at >= 0) this.waiters.splice(at, 1);
                resolve();
            };
            const timer = setTimeout(finish, ms);
            // An extension host must not be held open by a licence poll that nobody is watching.
            (timer as any).unref?.();
            this.waiters.push(finish);
        });
    }

    private release(): void {
        const woken = this.waiters.slice();
        this.waiters.length = 0;
        for (const wake of woken) wake();
    }
}

/// What the poll ended as. `timeout` carries whether the worker ever said "pending", because those are
/// two different stories to tell: Stripe had the payment and the mint was slow, versus nothing arrived
/// at all (the checkout was probably abandoned).
export type CollectOutcome =
    | { kind: 'token'; token: string }
    | { kind: 'gone' }
    | { kind: 'cancelled' }
    | { kind: 'timeout'; sawPending: boolean }
    | { kind: 'failed'; reason: string };

export interface CollectOptions {
    base: string;
    nonce: string;
    gate: CollectGate;
    fetchJson?: BillingFetch;
    intervalMs?: number;
    timeoutMs?: number;
    now?: () => number;
    /// The route polled. /stripe/collect for a purchase; the free month's /trial/claim answers with the same arms.
    path?: string;
}

/// Poll /stripe/collect until the token lands, the link is spent, the person cancels, or we give up.
///
/// 404 and 425 are BOTH "not yet". The contract distinguishes them (404 before the webhook has minted,
/// 425 while the session is pending) and the distinction is worth keeping, but neither is a reason to
/// stop: a real checkout spends its first seconds in exactly those states. Only 410 is terminal, and it
/// means the token was already collected by an earlier run. A run of transport failures gives up rather
/// than hammering a worker that is down.
export async function collectProToken(options: CollectOptions): Promise<CollectOutcome> {
    const fetchJson = options.fetchJson ?? nodeBillingFetch;
    const now = options.now ?? Date.now;
    const intervalMs = options.intervalMs ?? COLLECT_INTERVAL_MS;
    const deadline = now() + (options.timeoutMs ?? COLLECT_TIMEOUT_MS);
    const url = billingEndpoint(options.base, options.path ?? '/stripe/collect', { nonce: options.nonce });
    let sawPending = false;
    let consecutiveFailures = 0;

    for (;;) {
        if (options.gate.isCancelled) return { kind: 'cancelled' };
        try {
            const answer = await fetchJson(url);
            if (answer.status === 200) {
                const token = String(answer.json?.token ?? '').trim();
                if (token) return { kind: 'token', token };
                return { kind: 'failed', reason: 'The Semanticus billing service answered without a licence.' };
            }
            if (answer.status === 410) return { kind: 'gone' };
            if (answer.status === 425) { sawPending = true; consecutiveFailures = 0; }
            else if (answer.status === 404) { consecutiveFailures = 0; }
            else {
                consecutiveFailures++;
                if (consecutiveFailures >= 5) return { kind: 'failed', reason: `The Semanticus billing service answered ${answer.status}.` };
            }
        } catch (e: any) {
            consecutiveFailures++;
            if (consecutiveFailures >= 5) return { kind: 'failed', reason: e?.message || 'The Semanticus billing service could not be reached.' };
        }
        if (now() >= deadline) return { kind: 'timeout', sawPending };
        await options.gate.wait(intervalMs);
    }
}

/// What the daily renewal check found.
///
/// `refused` is the one that is NOT silence. It means the billing service would not accept this licence
/// at all, which is not something the app can fix and not something it should hide either: a subscriber
/// whose saved token had gone stale used to be told nothing, keep paying, and simply drop off Pro.
export type RenewalOutcome =
    | { kind: 'renewed'; token: string }
    | { kind: 'current' }
    | { kind: 'refused' }
    | { kind: 'silent' };

/// Ask whether a newer licence exists for the subscription this one names, and hand it back if so.
///
/// This is how a renewal reaches a window that is ALREADY OPEN. Collection by nonce cannot do it: that
/// ticket is one-shot and belongs to the first purchase, so a year later it is long spent. The app asks
/// with the licence it holds; the worker answers 204 when that is already the newest one.
///
/// It never throws. Almost everything it can run into is silence, because it runs once a day in the
/// background and a worker that is down or a malformed answer are nothing a person could act on. The one
/// exception is a refusal, which the caller shows.
export async function checkForRenewedToken(base: string, token: string, fetchJson: BillingFetch = nodeBillingFetch): Promise<RenewalOutcome> {
    const trimmed = (token || '').trim();
    if (!trimmed) return { kind: 'silent' };
    try {
        const url = billingEndpoint(base, '/stripe/renew');
        const answer = await fetchJson(url, { headers: { authorization: `Bearer ${trimmed}` } });
        if (answer.status === 401 || answer.status === 403) return { kind: 'refused' };
        if (answer.status === 204) return { kind: 'current' };
        if (answer.status !== 200) return { kind: 'silent' };
        const renewed = String(answer.json?.token ?? '').trim();
        if (!renewed) return { kind: 'silent' };
        return renewed === trimmed ? { kind: 'current' } : { kind: 'renewed', token: renewed };
    } catch { return { kind: 'silent' }; }
}

/// Whether enough time has passed to ask again. A `lastCheckedMs` in the future (a clock that moved
/// backwards, a settings blob synced from another machine) reads as "ask now" rather than parking the
/// check until the future catches up.
export function shouldCheckForRenewal(lastCheckedMs: number | undefined, now = Date.now()): boolean {
    if (typeof lastCheckedMs !== 'number' || !Number.isFinite(lastCheckedMs) || lastCheckedMs <= 0) return true;
    if (lastCheckedMs > now) return true;
    return now - lastCheckedMs >= RENEWAL_CHECK_INTERVAL_MS;
}

/// Read the subscription id out of a licence token WITHOUT verifying its signature.
///
/// This is a READ, never a trust decision: the engine remains the only thing that decides whether a
/// token is real, and the worst a forged payload can do here is send someone to a Stripe portal page
/// for a subscription that is not theirs, which Stripe itself then refuses. The token's shape is the
/// engine's own (tools/fulfillment/worker/mint.mjs): base64url(payloadJson) "." base64url(signature),
/// payload FIRST — it is NOT a three-part JWT, and reading it as one returns the signature.
export function subscriptionIdFromToken(token: string): string {
    const trimmed = (token || '').trim();
    const dot = trimmed.indexOf('.');
    if (dot <= 0) return '';
    try {
        const payload = JSON.parse(Buffer.from(trimmed.slice(0, dot), 'base64url').toString('utf8'));
        const sid = payload?.sid;
        return typeof sid === 'string' ? sid.trim() : '';
    } catch { return ''; }
}

/// The nonces this editor actually issued, so a vscode://…/pro?nonce=… link from anywhere else cannot
/// make the extension fetch and store a licence. Kept in globalState (survives a window reload, which a
/// browser round trip can cause) and swept by age on every read.
export interface NonceStore { get(): Record<string, number>; set(value: Record<string, number>): Promise<void>; }

export async function rememberCheckoutNonce(store: NonceStore, nonce: string, at = Date.now()): Promise<void> {
    await store.set({ ...sweepNonces(store.get(), at), [nonce]: at });
}

export function isKnownCheckoutNonce(store: NonceStore, nonce: string, at = Date.now()): boolean {
    if (!nonce) return false;
    const issued = sweepNonces(store.get(), at)[nonce];
    return typeof issued === 'number';
}

export async function forgetCheckoutNonce(store: NonceStore, nonce: string, at = Date.now()): Promise<void> {
    const kept = sweepNonces(store.get(), at);
    delete kept[nonce];
    await store.set(kept);
}

function sweepNonces(value: Record<string, number> | undefined, at: number): Record<string, number> {
    const kept: Record<string, number> = {};
    for (const [nonce, issued] of Object.entries(value ?? {})) {
        if (typeof issued === 'number' && at - issued < NONCE_LIFETIME_MS) kept[nonce] = issued;
    }
    return kept;
}
