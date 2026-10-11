using System;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>Whether this installation is entitled to the Pro tier. Since 22 September 2026 Pro is the whole
    /// Workflows tab and the ask box (see <see cref="ProFeature"/>), plus the ranked and run plan, which is gated
    /// on the tier rather than on a named feature. Everything a person can do by hand or with their own assistant
    /// is free, every read and every bulk apply included. Read-only, offline, secret-free: it verifies a signed
    /// token with an EMBEDDED PUBLIC key only, so no private key, no Anthropic/Fabric creds, no network, no
    /// inference (golden rule #1 holds).</summary>
    public interface IEntitlement
    {
        bool IsPro { get; }
        EntitlementInfo Info { get; }
    }

    /// <summary>The entitlement surfaced to both doors (get_entitlement) so the UI can decide what to render and the
    /// agent knows what it may call. Plain POCO so it serializes over RPC/MCP.</summary>
    public sealed class EntitlementInfo
    {
        public string Tier { get; set; } = "free";   // "free" | "pro"
        public string LicensedTo { get; set; }         // claims.sub, display only; null when free
        public long Expiry { get; set; }               // unix seconds; 0 = perpetual / none
        public string Reason { get; set; }             // teaching line: why free (no license / invalid / expired), OR a
                                                        // grace advisory while still Pro ("expired X; in grace until Y")
        public string ManageUrl { get; set; } = LicenseEntitlement.ManageUrl; // one product-owned Pro plans/support page

        /// <summary>The effective feature grants, as wire ids. Today there is exactly one, "workflows", so a Pro
        /// session reports ["workflows"] and a free session reports []. Never null. Only a feature that IS a tab
        /// has a wire id, because this array exists for a page to lock a tab on; the ask box and the ranked plan
        /// are paid and carry none, and a page keys those on the tier. Computed in ONE place
        /// (<see cref="FeatureGrants"/>) and used by both this response and the gate, so a page and the engine
        /// can never disagree.</summary>
        public string[] Features { get; set; } = Array.Empty<string>();
    }

    /// <summary>Thrown when a Pro feature is reached on the free tier. Carries a user-facing, actionable message;
    /// both doors surface it as a clean error (the UI also renders the preview from get_entitlement). It is thrown
    /// at the TOP of the operation, before any protected computation, network call or side effect, so a refusal can
    /// never leave half-done work behind and never costs the free user a second of waiting.</summary>
    public sealed class EntitlementException : InvalidOperationException
    {
        public EntitlementException(string message) : base(message) { }
    }

    /// <summary>The single gate helper, so every Pro refusal reads identically.</summary>
    public static class EntitlementGuard
    {
        /// <summary>The feature gate. The tab is the one a person would open (Workflows, Ask about this model),
        /// never the enum name, and the free alternative comes from <see cref="ProFeatures"/>, so one feature
        /// always refuses with one sentence. Effective grants come from <see cref="FeatureGrants"/>, the same
        /// function get_entitlement reports from.</summary>
        public static void RequirePro(IEntitlement e, ProFeature feature, string tab = null)
            => RequirePro(FeatureGrants.Grants(e, feature),
                          string.IsNullOrWhiteSpace(tab) ? ProFeatures.DefaultTab(feature) : tab,
                          ProFeatures.FreeAlternative(feature));

        public static void RequirePro(IEntitlement e, string feature, string freeAlternative)
            => RequirePro(e != null && e.IsPro, feature, freeAlternative);

        // Overload for callers that have already resolved the tier to a bool. Same single message source, so every
        // Pro refusal reads identically: the "Semanticus Pro feature" phrase the UI matches on always comes from here.
        public static void RequirePro(bool isPro, string feature, string freeAlternative)
        {
            if (isPro) return;
            throw new EntitlementException(
                $"{feature} is a Semanticus Pro feature. {freeAlternative} " +
                "Unlock Pro from Pro Plans and Support.");
        }
    }
}
