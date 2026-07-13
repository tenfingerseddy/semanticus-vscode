using System;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>Whether this installation is entitled to the Pro tier. The engine consults this ONLY at the
    /// bulk-apply chokepoints — the one-click "fix/apply everything" primitives (apply_change_plan with &gt;1 item,
    /// bpa_fix_all, apply_safe_fixes). The free tier stays fully usable one-edit-at-a-time; Pro unlocks the
    /// bulk/atomic apply. Read-only, offline, secret-free: it verifies a signed token with an EMBEDDED PUBLIC key
    /// only — no private key, no Anthropic/Fabric creds, no network, no inference (golden rule #1 holds).</summary>
    public interface IEntitlement
    {
        bool IsPro { get; }
        EntitlementInfo Info { get; }
    }

    /// <summary>The entitlement surfaced to both doors (get_entitlement) so the UI can show the tier and pre-gate
    /// the bulk buttons, and the agent knows what it may do. Plain POCO so it serializes over RPC/MCP.</summary>
    public sealed class EntitlementInfo
    {
        public string Tier { get; set; } = "free";   // "free" | "pro"
        public string LicensedTo { get; set; }         // claims.sub — display only; null when free
        public long Expiry { get; set; }               // unix seconds; 0 = perpetual / none
        public string Reason { get; set; }             // teaching line: why free (no license / invalid / expired), OR a
                                                        // grace advisory while still Pro ("expired X; in grace until Y")
        public string ManageUrl { get; set; } = LicenseEntitlement.ManageUrl; // one product-owned Pro plans/support page
    }

    /// <summary>Thrown when a Pro-only bulk op is attempted on the free tier. Carries a user-facing, actionable
    /// message; both doors surface it as a clean error (the UI also pre-gates the button via get_entitlement). It is
    /// thrown at the TOP of the gated op, before any mutation, so a refusal can never leave a half-applied model.</summary>
    public sealed class EntitlementException : InvalidOperationException
    {
        public EntitlementException(string message) : base(message) { }
    }

    /// <summary>The single gate helper used at every bulk chokepoint, so the upsell message stays consistent.</summary>
    public static class EntitlementGuard
    {
        public static void RequirePro(IEntitlement e, string feature, string freeAlternative)
            => RequirePro(e != null && e.IsPro, feature, freeAlternative);

        // Overload for callers that have already resolved the tier to a bool (e.g. the static AgentPolicyStore,
        // which is handed isPro rather than the IEntitlement). Same single message source, so every Pro refusal
        // reads identically — the "Semanticus Pro feature" phrase the UI matches on always comes from here.
        public static void RequirePro(bool isPro, string feature, string freeAlternative)
        {
            if (isPro) return;
            throw new EntitlementException(
                $"{feature} is a Semanticus Pro feature. {freeAlternative} " +
                "Unlock Pro with a license (set SEMANTICUS_LICENSE, or ~/.semanticus/license).");
        }
    }
}
