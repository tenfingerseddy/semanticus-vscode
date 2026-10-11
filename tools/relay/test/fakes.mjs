// Test doubles for the relay: an in-memory KV, an in-memory Durable Object namespace that keeps the real
// single-threaded-per-object behaviour, a fake upstream, and a test minter for licence tokens.
//
// The DO fake matters more than it looks. A Durable Object serialises every request to one object, and the
// reservation rule only holds because of that. A fake that let two reserves interleave would make the
// concurrency test pass for the wrong reason, so this one chains each object's calls on a promise.

const te = new TextEncoder();

export function b64urlEncode(bytes) {
    let bin = '';
    for (const b of bytes) bin += String.fromCharCode(b);
    return btoa(bin).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

export function b64Encode(bytes) {
    let bin = '';
    for (const b of bytes) bin += String.fromCharCode(b);
    return btoa(bin);
}

/** A P-256 keypair as the shipped product holds it: SPKI base64 public, PKCS8 base64 private. */
export async function generateKeyPair() {
    const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
    const spki = new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey));
    const pkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', pair.privateKey));
    return { publicKey: b64Encode(spki), privateKey: b64Encode(pkcs8), signingKey: pair.privateKey };
}

/** Mint exactly the wire format LicenseVerifier.Verify reads: base64url(payload) "." base64url(P1363 sig),
 *  the signature taken over the ASCII bytes of the base64url payload string. Independent of mint.mjs on
 *  purpose: the relay must stand on its own reading of the C# verifier, not on another lane's file. */
export async function mintToken(signingKey, claims) {
    const payload = b64urlEncode(te.encode(JSON.stringify(claims)));
    const sig = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, signingKey, te.encode(payload));
    return payload + '.' + b64urlEncode(new Uint8Array(sig));
}

// --- KV -------------------------------------------------------------------------------------------------

export class FakeKV {
    constructor(seed = {}) { this.map = new Map(Object.entries(seed)); this.reads = 0; }
    async get(key) { this.reads++; return this.map.has(key) ? this.map.get(key) : null; }
    async put(key, value) { this.map.set(key, String(value)); }
    async delete(key) { this.map.delete(key); }
    async list({ prefix = '', limit = 1000, cursor } = {}) {
        const keys = [...this.map.keys()].filter((k) => k.startsWith(prefix)).sort();
        const start = cursor ? Number(cursor) : 0;
        const page = keys.slice(start, start + limit);
        const end = start + page.length;
        return { keys: page.map((name) => ({ name })), list_complete: end >= keys.length, cursor: String(end) };
    }
}

// --- Durable Objects ------------------------------------------------------------------------------------

class FakeStorage {
    constructor() { this.map = new Map(); }
    async get(key) { return this.map.has(key) ? structuredClone(this.map.get(key)) : undefined; }
    async put(key, value) { this.map.set(key, structuredClone(value)); }
    async delete(key) { return this.map.delete(key); }
    async list({ prefix = '' } = {}) {
        const out = new Map();
        for (const k of [...this.map.keys()].sort()) if (k.startsWith(prefix)) out.set(k, structuredClone(this.map.get(k)));
        return out;
    }
}

/** A namespace whose stubs route to real instances of the ledger class, one per name, with every call to
 *  one object serialised the way the platform serialises them. */
export function makeLedgerNamespace(Ctor) {
    const objects = new Map();
    const queues = new Map();
    const ns = {
        idFromName(name) { return { name, toString() { return name; } }; },
        get(id) {
            const name = id.name;
            if (!objects.has(name)) objects.set(name, new Ctor({ storage: new FakeStorage() }, {}));
            const instance = objects.get(name);
            return {
                fetch(url, init) {
                    const prior = queues.get(name) ?? Promise.resolve();
                    const next = prior.then(() => instance.fetch(new Request(url, init)));
                    queues.set(name, next.then(() => undefined, () => undefined));
                    return next;
                },
            };
        },
        _objects: objects,
    };
    return ns;
}

// --- upstream -------------------------------------------------------------------------------------------

/** A fake vendor. `handler(requestInfo)` returns { status, body, headers } or throws to simulate a failure. */
export function makeUpstream(handler) {
    const calls = [];
    const fn = async (url, init = {}) => {
        const info = {
            url: String(url),
            method: init.method ?? 'GET',
            headers: new Headers(init.headers ?? {}),
            body: init.body,
            json: init.body ? JSON.parse(init.body) : undefined,
        };
        calls.push(info);
        const out = await handler(info, calls.length);
        return new Response(out.body ?? '', {
            status: out.status ?? 200,
            headers: out.headers ?? { 'content-type': 'application/json' },
        });
    };
    fn.calls = calls;
    return fn;
}

export function timeoutError() {
    const e = new Error('The operation was aborted due to timeout');
    e.name = 'TimeoutError';
    return e;
}

/** Capture console.log/console.warn lines for the logging assertions. */
export function captureLogs() {
    const lines = [];
    const log = console.log;
    const warn = console.warn;
    console.log = (...a) => lines.push(a.join(' '));
    console.warn = (...a) => lines.push(a.join(' '));
    return { lines, restore() { console.log = log; console.warn = warn; } };
}

/** White-box look inside one ledger object, for the state-machine assertions. */
export async function dumpLedger(ns, name) {
    const instance = ns._objects.get(name);
    if (!instance) return null;
    const storage = instance.state.storage;
    const meta = await storage.get('meta');
    const open = [...(await storage.list({ prefix: 'e:' })).values()];
    const done = [...(await storage.list({ prefix: 'd:' })).values()];
    return { meta, open, done };
}
