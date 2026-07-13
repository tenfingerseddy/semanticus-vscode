using System;
using System.IO;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>The production <see cref="IEntitlement"/>: reads a signed license token (env SEMANTICUS_LICENSE,
    /// else ~/.semanticus/license) and verifies it OFFLINE against the embedded public key (rotate at ship-time via
    /// SEMANTICUS_LICENSE_PUBKEY). Absent / invalid / expired / wrong-tier ⇒ FREE (the safe default — a missing or
    /// corrupt license never blocks the free product). A dev escape (SEMANTICUS_DEV_PRO=1) unlocks Pro locally so
    /// building Semanticus is never self-gated. Evaluated ONCE at construction (a license doesn't change mid-session).</summary>
    public sealed class LicenseEntitlement : IEntitlement
    {
        // The issuance keypair's PUBLIC half (SPKI base64). The matching PRIVATE key is the issuer's, kept OFFLINE
        // and never in this repo. The literal "REPLACE_AT_ISSUANCE" placeholder means "no production key embedded
        // yet" ⇒ only the dev escape or a SEMANTICUS_LICENSE_PUBKEY override can unlock Pro. Replace this constant
        // with the real public key once issuance is set up (keygen via the Semanticus.License CLI).
        // Production keypair minted 2026-07-04 (launch): private half in Kane's password manager + the
        // fulfillment Worker secret. The pre-launch key it replaced was never used to issue a license.
        public const string EmbeddedPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvfPm61yXQOeq3I3Vtk+S5pI5b0y+HesTVymzTBQTtQKweRJME0ZqZFkDTBKLvjPKy0nFlwmOLA1SFzYcHGh4/g==";

        // Annual subscriptions expire, but a renewal payment + fresh-token delivery can lag the exp instant (a failed
        // charge retry, a weekend, a manual fulfillment). GRACE keeps a just-lapsed license on Pro for this many days
        // past exp so a paying subscriber is never abruptly downgraded mid-work; the Reason teaches the grace deadline.
        public const int GraceDays = 14;

        // One live, web-owned Pro door. The extension only opens it; checkout and subscription support stay on the
        // Semanticus website and with Paddle. It is an upgrade/help page, not an in-extension billing portal.
        public const string ManageUrl = "https://semanticus.com.au/pro";
        public const string RenewUrl = ManageUrl; // compatibility name for expiry/grace copy and existing callers

        public bool IsPro { get; }
        public EntitlementInfo Info { get; }

        private LicenseEntitlement(bool isPro, EntitlementInfo info) { IsPro = isPro; Info = info; }

        /// <summary>The default construction: read the ambient license + public-key override + dev flag, at now.</summary>
        public static LicenseEntitlement FromEnvironment() => FromEnvironmentOrToken(null);

        /// <summary>Construction preferring an EXPLICIT license token (e.g. from the engine's <c>--license</c> CLI arg),
        /// falling back to env (SEMANTICUS_LICENSE) then the ~/.semanticus/license file. This is the RELIABLE delivery
        /// path: an attaching MCP process's env is ignored (the gate follows the OWNER engine), and Claude Code's
        /// <c>env:</c> passthrough is unreliable — so the extension / .mcp.json should pass the license as an arg, not env.</summary>
        public static LicenseEntitlement FromEnvironmentOrToken(string explicitToken) => Evaluate(
            !string.IsNullOrWhiteSpace(explicitToken) ? explicitToken.Trim()
                : (Environment.GetEnvironmentVariable("SEMANTICUS_LICENSE") ?? ReadLicenseFile()),
            Environment.GetEnvironmentVariable("SEMANTICUS_LICENSE_PUBKEY"),
            Environment.GetEnvironmentVariable("SEMANTICUS_DEV_PRO") == "1",
            DateTimeOffset.UtcNow);

        /// <summary>Pure evaluator — (token, public-key override, dev flag, now) ⇒ entitlement. Public so the tests
        /// (and a future UI preview) can exercise every branch deterministically with an injected key + clock.</summary>
        public static LicenseEntitlement Evaluate(string token, string pubKeyOverride, bool devPro, DateTimeOffset now)
        {
            if (devPro)
                return new LicenseEntitlement(true, new EntitlementInfo { Tier = "pro", LicensedTo = "dev (SEMANTICUS_DEV_PRO)" });

            var pub = string.IsNullOrWhiteSpace(pubKeyOverride) ? EmbeddedPublicKey : pubKeyOverride;
            if (string.IsNullOrWhiteSpace(pub) || pub == "REPLACE_AT_ISSUANCE")
                return Free(string.IsNullOrWhiteSpace(token) ? "no license" : "license verification not configured");
            if (string.IsNullOrWhiteSpace(token))
                return Free("no license");

            // Verify the SIGNATURE only, then apply the expiry/grace POLICY here (Verify's hard-expiry would collapse
            // "expired-but-in-grace" and "truly-lapsed" into one null, losing the exp we need to teach the deadline).
            var claims = LicenseVerifier.VerifySignature(token, pub);
            if (claims == null) return Free("license missing or invalid: the signature did not verify against the Semanticus public key");
            if (!string.Equals(claims.Tier, "pro", StringComparison.OrdinalIgnoreCase))
                return Free("license is not a Pro tier");

            // Expiry policy: exp==0 ⇒ PERPETUAL. This is also the BACKWARD-COMPAT path — a token minted before the exp
            // claim existed (or with --days<=0) carries no "exp" field, so it deserializes to 0 and stays valid forever.
            // Old dev/founder tokens must never brick, and exp==0 has always been the wire contract for "no expiry".
            if (claims.Exp != 0)
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(claims.Exp);
                if (now > exp)
                {
                    var graceEnd = exp.AddDays(GraceDays);
                    if (now <= graceEnd)
                        // Lapsed but within grace: STILL Pro, but the Reason teaches the (soft) deadline so the user renews.
                        return new LicenseEntitlement(true, new EntitlementInfo { Tier = "pro", LicensedTo = claims.Sub, Expiry = claims.Exp,
                            Reason = $"license expired {exp:yyyy-MM-dd}; in grace until {graceEnd:yyyy-MM-dd}. Renew at {RenewUrl} to stay on Pro" });
                    // Past grace ⇒ drop to Free, but TEACH: what lapsed, where to renew, and that nothing broke.
                    return Free($"license expired {exp:yyyy-MM-dd}. Renew at {RenewUrl}; the engine keeps working on the free tier");
                }
            }
            return new LicenseEntitlement(true, new EntitlementInfo { Tier = "pro", LicensedTo = claims.Sub, Expiry = claims.Exp });
        }

        /// <summary>A Pro entitlement for the dev/test harnesses (the smoke runners verify FUNCTIONALITY; the gate
        /// itself is covered by the xUnit EntitlementGateTests). NEVER used by the shipped engine path.</summary>
        public static LicenseEntitlement DevPro() => Evaluate(null, null, devPro: true, DateTimeOffset.UtcNow);

        private static LicenseEntitlement Free(string reason) =>
            new LicenseEntitlement(false, new EntitlementInfo { Tier = "free", Reason = reason });

        private static string ReadLicenseFile()
        {
            try
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus", "license");
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch { return null; }   // an unreadable file ⇒ no license (free); never throw on the read path
        }
    }
}
