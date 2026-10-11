using System;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine : Entitlement.ILicenseHolder
    {
        // How a token becomes an entitlement. The default is the SAME construction a start uses (stdin token, else
        // the environment, else the user licence file), so a swap and a restart can never disagree about a machine.
        private Func<string, Entitlement.IEntitlement> _licenseEvaluator = Entitlement.LicenseEntitlement.FromEnvironmentOrToken;
        private readonly object _licenseGate = new object();

        /// <summary>Test seam: a verifier with a test key and a fixed clock. No production path reaches it.</summary>
        internal void UseLicenseEvaluatorForTest(Func<string, Entitlement.IEntitlement> evaluator) =>
            _licenseEvaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

        Entitlement.LicenseSwapResult Entitlement.ILicenseHolder.ApplyLicense(string token, bool verifyOnly) =>
            ApplyLicense(token, verifyOnly);

        /// <summary>Swap the running entitlement for the one <paramref name="token"/> gives (F-242), so a licence
        /// change takes effect without a restart. Before this, the entitlement was read once at start, and a window
        /// that had adopted an engine it did not start had no way to make Remove license land.
        ///
        /// A non-empty token must verify as Pro or it is REFUSED and nothing changes: a typo must not turn a paying
        /// person's session free. An empty token is a removal and always applies; what it lands on is whatever a
        /// restart would read (an environment licence or the licence file can still make it Pro, and the answer says
        /// so rather than claiming free). <paramref name="verifyOnly"/> answers the same question and swaps nothing,
        /// so the page can refuse a bad key before anything is stored.
        ///
        /// The agent door is not told: it reads the entitlement on its next call, as it learns of every change.</summary>
        internal Entitlement.LicenseSwapResult ApplyLicense(string token, bool verifyOnly)
        {
            var trimmed = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            lock (_licenseGate)
            {
                var candidate = _licenseEvaluator(trimmed);
                var current = _entitlement;
                if (trimmed != null && (candidate == null || !candidate.IsPro))
                    return new Entitlement.LicenseSwapResult
                    {
                        Accepted = false,
                        Reason = candidate?.Info?.Reason ?? "license missing or invalid",
                        Entitlement = Entitlement.FeatureGrants.Stamp(current.Info, current),
                    };
                if (verifyOnly)
                    return new Entitlement.LicenseSwapResult { Accepted = true, Entitlement = Entitlement.FeatureGrants.Stamp(current.Info, current) };

                _entitlement = candidate;
                // The hosted decision service was built with the old licence's credential. Retire it and drop it, so
                // the next use builds one with the new credential, or none: a removed licence must not keep a route
                // that spends money. Dropping alone was not enough (F-243): a request that started under the old
                // licence held the old service and its Pro answer, and could still send. Retiring cancels anything
                // in flight and refuses anything still waiting to send.
                DecisionService retired;
                lock (_decisionGate) { retired = _decisions; _decisions = null; }
                retired?.Retire();
                return new Entitlement.LicenseSwapResult
                {
                    Accepted = true,
                    Applied = true,
                    Entitlement = Entitlement.FeatureGrants.Stamp(candidate.Info, candidate),
                };
            }
        }
    }
}
