using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>The ONE place effective feature grants are computed. Both <see cref="EntitlementGuard.RequirePro(IEntitlement, ProFeature, string)"/>
    /// and <c>get_entitlement</c> read it, so the engine and a page can never disagree about what this installation
    /// may do. Today the answer is all-or-nothing by design: a valid Pro licence, a licence inside the 14-day grace
    /// window and SEMANTICUS_DEV_PRO=1 all carry all four features; free carries none.
    ///
    /// The token format already has an optional <c>features</c> claim (<see cref="LicenseClaims.Features"/>), and
    /// tokens minted today omit it. An omitted claim must keep all four, and an unused optional claim must not
    /// quietly become a new set of product tiers, so this function deliberately ignores the claim until a narrower
    /// tier is actually sold. Change it here and both doors change together.</summary>
    public static class FeatureGrants
    {
        /// <summary>The features this entitlement actually grants.</summary>
        public static IReadOnlyList<ProFeature> For(IEntitlement entitlement) =>
            entitlement != null && entitlement.IsPro ? ProFeatures.All : Array.Empty<ProFeature>();

        /// <summary>Same answer from the serialized info, for callers that only hold the POCO.</summary>
        public static IReadOnlyList<ProFeature> For(EntitlementInfo info) =>
            info != null && string.Equals(info.Tier, "pro", StringComparison.OrdinalIgnoreCase)
                ? ProFeatures.All : Array.Empty<ProFeature>();

        /// <summary>True when this entitlement grants that one feature.</summary>
        public static bool Grants(IEntitlement entitlement, ProFeature feature) => For(entitlement).Contains(feature);

        /// <summary>The wire ids for <c>get_entitlement.features</c>. Free is an empty array, never null.</summary>
        public static string[] WireIds(IEntitlement entitlement) =>
            For(entitlement).Select(ProFeatures.WireId).ToArray();

        /// <summary>Stamp the effective grants onto the info the doors return, from the same single source.</summary>
        public static EntitlementInfo Stamp(EntitlementInfo info, IEntitlement entitlement)
        {
            if (info == null) return null;
            info.Features = WireIds(entitlement);
            return info;
        }
    }
}
