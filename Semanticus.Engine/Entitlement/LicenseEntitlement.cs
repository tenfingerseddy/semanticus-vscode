using System;
using System.Globalization;
using System.IO;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>The production <see cref="IEntitlement"/>: reads a signed license token (env SEMANTICUS_LICENSE,
    /// else ~/.semanticus/license) and verifies it OFFLINE against the embedded public key (rotate at ship-time via
    /// SEMANTICUS_LICENSE_PUBKEY). Absent / invalid / expired / wrong-tier ⇒ FREE (the safe default — a missing or
    /// corrupt license never blocks the free product). A dev escape (SEMANTICUS_DEV_PRO=1) unlocks Pro locally so
    /// building Semanticus is never self-gated. Evaluated at construction, for one instant: a licence with an end carries
    /// <see cref="ChangesAt"/>, and the engine evaluates the same token again past that instant (<see cref="At"/>), so a
    /// window left open drops to Free on time without a restart.</summary>
    public sealed class LicenseEntitlement : IEntitlement
    {
        // The issuance keypair's PUBLIC half (SPKI base64). The matching PRIVATE key is the issuer's, kept OFFLINE
        // and never in this repo. The literal "REPLACE_AT_ISSUANCE" placeholder means "no production key embedded
        // yet" ⇒ only the dev escape or a SEMANTICUS_LICENSE_PUBKEY override can unlock Pro. Replace this constant
        // with the real public key once issuance is set up (keygen via the Semanticus.License CLI).
        // Production keypair minted 2026-10-08 (go-live, Kane's decision): the private half lives outside the repo and is
        // set as the billing Worker's SEMANTICUS_SIGNING_KEY secret. It replaced the 2026-07-04 pair, so a licence
        // signed by the old private key no longer verifies.
        public const string EmbeddedPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4UzdpLYvBWYfMnPiArcoCmJ7H1V6pV4nOf9jJmZRu31j1gf5B9LXHKCXmBFQL7lJxINjweMi8inaqLFr6du1aQ==";

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

        /// <summary>The verified token itself, kept ONLY when a real signed Pro token was the thing that unlocked
        /// this entitlement. It is the credential the hosted decision relay authenticates with: the relay holds the
        /// public key and re-verifies server side, so nothing else this process could say about itself is worth
        /// anything to it (docs/redesign/contracts/relay.md, "Eligibility").
        ///
        /// Null on every other path, and each null is deliberate rather than an oversight:
        ///   free, expired past grace, wrong tier: there is nothing a relay would accept;
        ///   SEMANTICUS_DEV_PRO: a local feature gate is allowed to be generous, spending money is not, and the
        ///   relay refuses the dev flag by construction, so handing it a credential it does not have would only
        ///   turn a clean "no service" into a 401 round trip.
        /// A token inside the grace window IS kept: the relay mirrors the same 14-day grace.
        ///
        /// Never logged, never returned on any door. The one place it is written to disk is LicenseMirror, the user licence file, when the person
        /// connects another assistant (it says so in its answer). It is internal so a
        /// new caller has to be inside this assembly, and there are two: LicenseMirror, and the decision oracle's construction
        /// in LocalEngine.Decisions.cs.</summary>
        internal string CloudCredential { get; }

        /// <summary>The next instant at which the SAME token evaluates differently, or null when it never will (free, the dev
        /// escape, a perpetual licence, a licence already past its last change). Evaluation is <c>now &gt; instant</c>, so
        /// the instant itself still reads as before. A running trial changes at its end; a paid licence at its expiry (Pro
        /// into grace) and then at the end of grace (Free).</summary>
        internal DateTimeOffset? ChangesAt { get; }

        /// <summary>The claims of a trial token, running or ended, so the engine can key and date the trial's tally. Null for
        /// every other licence.</summary>
        internal LicenseClaims TrialClaims { get; }

        private readonly Func<DateTimeOffset, LicenseEntitlement> _again;

        private LicenseEntitlement(bool isPro, EntitlementInfo info, string cloudCredential = null, DateTimeOffset? changesAt = null,
                                   LicenseClaims trialClaims = null, Func<DateTimeOffset, LicenseEntitlement> again = null)
        {
            IsPro = isPro;
            Info = info;
            CloudCredential = cloudCredential;
            ChangesAt = changesAt;
            TrialClaims = trialClaims;
            _again = again;
        }

        /// <summary>The claims of the trial token behind <paramref name="entitlement"/>, or null when it is not one. With
        /// <paramref name="runningOnly"/> only a trial that is still running (still Pro) answers; without it an ended trial
        /// answers too, so its tally stays readable for the end card. Like <see cref="CloudCredentialOf"/>, this is not a
        /// tier gate: it decides what the tally counts and reads, never what a person may do.</summary>
        internal static LicenseClaims TrialOf(IEntitlement entitlement, bool runningOnly) =>
            entitlement is LicenseEntitlement e && e.TrialClaims != null && (!runningOnly || e.IsPro) ? e.TrialClaims : null;

        /// <summary>The same token evaluated at <paramref name="now"/>, with the same public key. This entitlement itself
        /// when nothing can change.</summary>
        internal LicenseEntitlement At(DateTimeOffset now) => ChangesAt == null || _again == null ? this : _again(now);

        /// <summary>Whole days left before <paramref name="end"/>, rounded UP, and never below 0.</summary>
        internal static int DaysLeft(long end, DateTimeOffset now)
        {
            var seconds = end - now.ToUnixTimeSeconds();
            return seconds <= 0 ? 0 : (int)((seconds + 86399) / 86400);
        }

        /// <summary>The credential an entitlement can lend a hosted service, or null when it has none.
        ///
        /// It lives here rather than at the call site so the cast is where credential policy is written, and
        /// so there is ONE sanctioned way to ask. Anything that is not a real verified license reads as null,
        /// which is the right default for something that can spend money: a test double, a free tier and the
        /// dev escape all get no credential and therefore no cloud route.
        ///
        /// This is NOT a tier read and it is not a gate. It answers "is there a token to present", never "may
        /// this person do X". The tier gate stays FeatureGrants and nothing here reaches it.</summary>
        internal static string CloudCredentialOf(IEntitlement entitlement) =>
            (entitlement as LicenseEntitlement)?.CloudCredential;

        /// <summary>The default construction: read the ambient license + public-key override + dev flag, at now.</summary>
        public static LicenseEntitlement FromEnvironment() => FromEnvironmentOrToken(null);

        /// <summary>Construction preferring an EXPLICIT license token (from stdin via <c>--license-stdin</c>),
        /// falling back to env (SEMANTICUS_LICENSE) then the user license file. An attaching MCP process inherits
        /// the owner's entitlement over the pipe; the token must not be placed on argv.</summary>
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

            LicenseEntitlement Again(DateTimeOffset at) => Evaluate(token, pubKeyOverride, devPro: false, at);
            if (claims.Trial) return Trial(claims, token, now, Again);

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
                            Reason = $"license expired {exp:yyyy-MM-dd}; in grace until {graceEnd:yyyy-MM-dd}. Renew at {RenewUrl} to stay on Pro" },
                            cloudCredential: token.Trim(), changesAt: graceEnd, again: Again);
                    // Past grace ⇒ drop to Free, but TEACH: what lapsed, where to renew, and that nothing broke.
                    return Free($"license expired {exp:yyyy-MM-dd}. Renew at {RenewUrl}; the engine keeps working on the free tier");
                }
            }
            return new LicenseEntitlement(true, new EntitlementInfo { Tier = "pro", LicensedTo = claims.Sub, Expiry = claims.Exp },
                cloudCredential: token.Trim(),
                changesAt: claims.Exp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(claims.Exp), again: Again);
        }

        /// <summary>A free Pro month (Kane, 11 Oct 2026). Pro until its end and Free the second after, with NO grace: grace
        /// exists so a paying subscriber's late renewal never drops them mid-work, and a trial has no renewal to wait for.
        /// The relay mirrors this (tools/relay: a trial token is expired at exp). A trial with no end is not a trial anyone
        /// sells, so it unlocks nothing rather than becoming a perpetual licence.</summary>
        private static LicenseEntitlement Trial(LicenseClaims claims, string token, DateTimeOffset now, Func<DateTimeOffset, LicenseEntitlement> again)
        {
            if (claims.Exp == 0) return Free("this Pro trial license has no end date, so it was not used");
            var end = DateTimeOffset.FromUnixTimeSeconds(claims.Exp);
            if (now > end)
                return new LicenseEntitlement(false, new EntitlementInfo
                {
                    Tier = "free",
                    TrialEnded = true,
                    TrialEndsAt = claims.Exp,
                    Reason = $"Your Pro trial ended on {end.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}. Your work is untouched.",
                }, trialClaims: claims);
            return new LicenseEntitlement(true, new EntitlementInfo
            {
                Tier = "pro",
                LicensedTo = claims.Sub,
                Expiry = claims.Exp,
                IsTrial = true,
                TrialEndsAt = claims.Exp,
                TrialDaysLeft = DaysLeft(claims.Exp, now),
            }, cloudCredential: token.Trim(), changesAt: end, trialClaims: claims, again: again);
        }

        /// <summary>A Pro entitlement for the dev/test harnesses (the smoke runners verify FUNCTIONALITY; the gate
        /// itself is covered by the xUnit EntitlementGateTests). NEVER used by the shipped engine path.</summary>
        public static LicenseEntitlement DevPro() => Evaluate(null, null, devPro: true, DateTimeOffset.UtcNow);

        private static LicenseEntitlement Free(string reason) =>
            new LicenseEntitlement(false, new EntitlementInfo { Tier = "free", Reason = reason });

        /// <summary>The user licence file, in the same per-user home as every other store (SEMANTICUS_HOME or
        /// --state-dir, else ~/.semanticus), so a moved home is moved whole.</summary>
        public static string LicenseFilePath() => Path.Combine(SemanticusHome.Root(), "license");

        private static string ReadLicenseFile()
        {
            try
            {
                var p = LicenseFilePath();
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch { return null; }   // an unreadable file ⇒ no license (free); never throw on the read path
        }
    }
}
