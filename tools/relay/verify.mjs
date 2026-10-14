// Server-side licence verification, with server-owned keys. Nothing the client says about itself is
// believed: not IsPro, not the dev flag, not a public-key override. Those three exist in the engine
// (LicenseEntitlement.cs:53 and :56) because a local feature gate is allowed to be generous. Spending money
// is not.
//
// This mirrors Semanticus.Engine.Entitlement.LicenseVerifier exactly, in WebCrypto:
//   wire format   base64url(payloadJson) "." base64url(signature)
//   algorithm     ECDSA P-256 over SHA-256
//   signed bytes  the ASCII bytes of the base64url payload STRING, not the decoded JSON
//   signature     IEEE P1363 (r||s), which is WebCrypto's native form and .NET's VerifyData default
// and it mirrors LicenseEntitlement.Evaluate's expiry POLICY: exp 0 is perpetual, and a lapsed token stays
// Pro for GraceDays past exp. The engine keeps a paying subscriber working through a failed renewal charge;
// refusing their cloud advice for the same two weeks would be the relay quietly disagreeing with the
// product. A token with trial: true gets no grace at all, again matching the engine. Revocation is the subscription status check in the worker, not the grace window.

export const GRACE_DAYS = 14;
const GRACE_MS = GRACE_DAYS * 86_400_000;

const te = new TextEncoder();

export function b64Decode(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

export function b64urlDecode(s) {
    const t = s.replace(/-/g, '+').replace(/_/g, '/');
    return b64Decode(t + '='.repeat((4 - (t.length % 4)) % 4));
}

export async function sha256Hex(text) {
    const digest = await crypto.subtle.digest('SHA-256', te.encode(text));
    return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

// One import per key string per isolate. Key import is the expensive part of a verify, and the public key
// never changes inside a deployment.
const keyCache = new Map();

async function importPublicKey(spkiBase64) {
    const cached = keyCache.get(spkiBase64);
    if (cached) return cached;
    const key = await crypto.subtle.importKey(
        'spki', b64Decode(spkiBase64.trim()), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
    keyCache.set(spkiBase64, key);
    return key;
}

/** Verify a licence token and apply the tier and expiry policy at nowMs.
 *  Returns { ok: true, claims, inGrace } or { ok: false, code, reason }. Never throws. */
export async function verifyLicence(token, spkiBase64, nowMs) {
    if (!token || !String(token).trim()) {
        return refuse('no_licence', 'This needs a Semanticus Pro licence. Sign in and try again.');
    }
    if (!spkiBase64 || !String(spkiBase64).trim()) {
        // A relay with no verification key refuses everything rather than waving traffic through.
        return refuse('not_configured', 'The service cannot check licences right now.');
    }

    let claims;
    try {
        const raw = String(token).trim();
        const dot = raw.indexOf('.');
        if (dot <= 0 || dot === raw.length - 1) return refuse('bad_signature', 'That licence could not be read.');
        const payload = raw.slice(0, dot);
        const signature = b64urlDecode(raw.slice(dot + 1));
        const key = await importPublicKey(spkiBase64);
        const ok = await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, signature, te.encode(payload));
        if (!ok) return refuse('bad_signature', 'That licence did not come from Semanticus.');
        claims = JSON.parse(new TextDecoder().decode(b64urlDecode(payload)));
    } catch {
        return refuse('bad_signature', 'That licence could not be read.');
    }

    if (!claims || typeof claims !== 'object') return refuse('bad_signature', 'That licence could not be read.');
    if (String(claims.tier ?? '').toLowerCase() !== 'pro') {
        return refuse('not_pro', 'Cloud advice is part of Semanticus Pro.');
    }

    let inGrace = false;
    const exp = Number(claims.exp ?? 0);
    if (exp !== 0) {
        const expMs = exp * 1000;
        if (nowMs > expMs) {
            // A Pro trial ends exactly at exp, the same instant the engine drops it to Free. Grace exists to
            // carry a paying subscriber through a failed renewal charge; a trial has no charge to fail.
            if (claims.trial === true || nowMs > expMs + GRACE_MS) {
                return refuse('expired', 'This licence has run out. Renew it to use cloud advice again.');
            }
            inGrace = true;
        }
    }

    return { ok: true, claims, inGrace };
}

/** The budget identity. The subscription id is the stable key: renewals and reissued tokens share it, and a
 *  display email does not. A token minted before the sid claim existed falls back to sub and is marked
 *  legacy, so the fallback shows up in the logs instead of looking like ordinary traffic. */
export function budgetIdentity(claims) {
    const sid = typeof claims?.sid === 'string' ? claims.sid.trim() : '';
    if (sid) return { id: 'sid:' + sid, sid, legacy: false };
    const sub = typeof claims?.sub === 'string' ? claims.sub.trim() : '';
    return { id: 'sub:' + (sub || 'unknown'), sid: null, legacy: true };
}

function refuse(code, reason) {
    return { ok: false, code, reason };
}
