// Semanticus — WebCrypto license minter (Cloudflare Workers + node ≥20, zero dependencies).
//
// Emits EXACTLY the wire format of Semanticus.Engine.Entitlement.LicenseVerifier:
//   base64url(payloadJson) "." base64url(signature)
// where the signature is ECDSA P-256 / SHA-256 over the ASCII bytes of the base64url payload STRING,
// in IEEE P1363 (r||s) form — which is WebCrypto's native ECDSA output, and .NET's VerifyData default.
// The verifier deserializes the payload as-transmitted, so JSON key order/whitespace need not match
// .NET's serializer — only the claim names do. Compatibility is PROVEN by selftest.mjs, which
// round-trips tokens through the real .NET verifier (Semanticus.License verify) in both directions.

const te = new TextEncoder();

export function b64urlEncode(bytes) {
    let bin = '';
    for (const b of bytes) bin += String.fromCharCode(b);
    return btoa(bin).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

export function b64Decode(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

export function b64urlDecode(s) {
    s = s.replace(/-/g, '+').replace(/_/g, '/');
    return b64Decode(s + '='.repeat((4 - (s.length % 4)) % 4));
}

/** Mint a license token. claims = { sub, tier, iat, exp, features? } (unix-seconds iat/exp; exp 0 = perpetual).
 *  privateKeyPkcs8Base64 is the offline signing key exactly as `Semanticus.License keygen` printed it. */
export async function mintToken(privateKeyPkcs8Base64, claims) {
    const key = await crypto.subtle.importKey(
        'pkcs8', b64Decode(privateKeyPkcs8Base64.trim()),
        { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign']);
    // Same claim shape as LicenseClaims; omit features when absent (mirrors WhenWritingNull).
    const payloadObj = { sub: claims.sub, tier: claims.tier, iat: claims.iat, exp: claims.exp };
    if (claims.features?.length) payloadObj.features = claims.features;
    const payload = b64urlEncode(te.encode(JSON.stringify(payloadObj)));
    const sig = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key, te.encode(payload));
    return payload + '.' + b64urlEncode(new Uint8Array(sig));
}

/** Verify with the SPKI public key; returns claims or null. Mirror of LicenseVerifier.Verify (hard expiry),
 *  used by the selftest to check .NET-minted tokens from this side. */
export async function verifyToken(token, publicKeySpkiBase64, nowUnixSeconds) {
    try {
        const dot = token.indexOf('.');
        if (dot <= 0 || dot === token.length - 1) return null;
        const payload = token.slice(0, dot);
        const sig = b64urlDecode(token.slice(dot + 1));
        const key = await crypto.subtle.importKey(
            'spki', b64Decode(publicKeySpkiBase64.trim()),
            { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
        const ok = await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, sig, te.encode(payload));
        if (!ok) return null;
        const claims = JSON.parse(new TextDecoder().decode(b64urlDecode(payload)));
        if (claims.exp !== 0 && claims.exp < nowUnixSeconds) return null;
        return claims;
    } catch { return null; }
}
