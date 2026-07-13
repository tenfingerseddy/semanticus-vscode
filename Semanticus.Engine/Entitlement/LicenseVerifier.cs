using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>A license token's claims: who it's for, the tier, and validity window. This is the signed payload.</summary>
    public sealed class LicenseClaims
    {
        [JsonPropertyName("sub")] public string Sub { get; set; }         // licensed to (email/name) — display only
        [JsonPropertyName("tier")] public string Tier { get; set; }       // "pro" (anything else is treated as free)
        [JsonPropertyName("iat")] public long Iat { get; set; }           // issued-at (unix seconds)
        [JsonPropertyName("exp")] public long Exp { get; set; }           // expiry (unix seconds; 0 = perpetual)
        [JsonPropertyName("features")] public string[] Features { get; set; }
    }

    /// <summary>Offline license tokens. ECDSA P-256 over a compact JSON payload (.NET 8 has no native Ed25519; P-256
    /// is the equivalent offline-verify primitive with ZERO added dependencies and is native to the BCL). The wire
    /// format is <c>base64url(payloadJson) "." base64url(signature)</c>. ONE class mints (with the PRIVATE key — the
    /// issuer's offline secret, never shipped) and verifies (with the PUBLIC key — embedded in the shipped engine),
    /// so signer and verifier can never drift. The engine only ever calls <see cref="Verify"/>: it holds the public
    /// key only, touches no network, runs no inference (golden rule #1).</summary>
    public static class LicenseVerifier
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Generate a fresh P-256 keypair as (publicSpkiBase64, privatePkcs8Base64). Run ONCE: embed the
        /// public string in the engine (<see cref="LicenseEntitlement.EmbeddedPublicKey"/>), keep the private string
        /// OFFLINE for issuance. Used by the Semanticus.License CLI and the tests.</summary>
        public static (string publicKey, string privateKey) GenerateKeyPair()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(ec.ExportPkcs8PrivateKey()));
        }

        /// <summary>Sign claims into a token with the PKCS8 private key (the issuance side). The engine NEVER calls
        /// this — only the offline minter (Semanticus.License) and the tests do.</summary>
        public static string Mint(string privateKeyBase64, LicenseClaims claims)
        {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims, Json));
            var sig = ec.SignData(Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256);   // IEEE P1363 r||s
            return payload + "." + Base64Url(sig);
        }

        /// <summary>Verify the SIGNATURE only — returns the claims whenever the token is authentic (well-formed +
        /// signed by the matching private key), REGARDLESS of expiry; returns null on any signature/format failure.
        /// The caller owns the expiry policy (e.g. the annual-subscription grace window in <see cref="LicenseEntitlement"/>),
        /// which needs the exp claim even from a lapsed token to teach "expired on X / in grace until Y". NEVER throws —
        /// a corrupt token simply yields null ("not licensed"), so a bad SEMANTICUS_LICENSE can't break the engine.</summary>
        public static LicenseClaims VerifySignature(string token, string publicKeyBase64)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(publicKeyBase64)) return null;
            try
            {
                var dot = token.IndexOf('.');
                if (dot <= 0 || dot == token.Length - 1) return null;
                var payloadPart = token.Substring(0, dot);
                var sig = FromBase64Url(token.Substring(dot + 1));
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
                if (!ec.VerifyData(Encoding.ASCII.GetBytes(payloadPart), sig, HashAlgorithmName.SHA256)) return null;
                return JsonSerializer.Deserialize<LicenseClaims>(FromBase64Url(payloadPart), Json);
            }
            catch { return null; }   // any malformed input ⇒ not licensed (the verify path must never throw)
        }

        /// <summary>Verify a token against the SPKI public key at <paramref name="now"/> with HARD expiry (no grace).
        /// Returns the claims when the signature is valid AND unexpired; null on any failure (malformed, bad signature,
        /// wrong key, expired). This is the strict low-level primitive; the grace-window policy lives one level up in
        /// <see cref="LicenseEntitlement.Evaluate"/> (which uses <see cref="VerifySignature"/> directly). NEVER throws.</summary>
        public static LicenseClaims Verify(string token, string publicKeyBase64, DateTimeOffset now)
        {
            var claims = VerifySignature(token, publicKeyBase64);
            if (claims == null) return null;
            if (claims.Exp != 0 && DateTimeOffset.FromUnixTimeSeconds(claims.Exp) < now) return null;   // expired
            return claims;
        }

        private static string Base64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }
    }
}
