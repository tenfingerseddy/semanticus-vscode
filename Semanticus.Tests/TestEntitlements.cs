using Semanticus.Engine.Entitlement;

namespace Semanticus.Tests
{
    /// <summary>The entitlement a test hands the engine when the tier is not what it is testing.
    ///
    /// Before 2026-09-15 a test could leave the entitlement out and the ambient free default was fine, because
    /// almost everything was free. Pro is four whole features now, so a test that exercises Workflows, Tests,
    /// Create in Model or Advanced publishing has to say it holds that feature; leaving it out means testing the
    /// refusal instead of the behaviour. Use <see cref="Pro"/> for those, and keep an explicit free entitlement
    /// only where the refusal IS the subject (see ProFeatureGateTests and ProGateRemovalTests).</summary>
    internal static class TestEntitlements
    {
        /// <summary>All four features. Same object the dev escape builds, so it cannot drift from the product.</summary>
        public static IEntitlement Pro => LicenseEntitlement.DevPro();

        /// <summary>No features. The refusal path.</summary>
        public static IEntitlement Free => new FreeTier();

        private sealed class FreeTier : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }
    }
}
