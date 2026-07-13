using System;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The offline license evaluator — the Pro gate's source of truth. Tests the PURE <see cref="LicenseEntitlement.Evaluate"/>
    /// (env-independent, deterministic) so entitlement can't silently drift. Delivery is via the engine's --license arg /
    /// env / ~/.semanticus/license file; the reliable path is --license (an attaching MCP proxy's env is ignored — the
    /// gate follows the OWNER engine — and Claude Code's env passthrough is unreliable).</summary>
    public sealed class LicenseEntitlementTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void No_token_is_free()
        {
            var e = LicenseEntitlement.Evaluate(null, null, devPro: false, Now);
            Assert.False(e.IsPro);
            Assert.Equal("free", e.Info.Tier);
        }

        [Fact]
        public void Bogus_token_fails_verification_and_is_free()
        {
            // A non-signed/garbage token must NOT unlock Pro — proves the token actually flows through offline
            // verification against the embedded public key (not merely a presence check).
            var e = LicenseEntitlement.Evaluate("not-a-real-signed-license", null, devPro: false, Now);
            Assert.False(e.IsPro);
            Assert.Equal("free", e.Info.Tier);
            Assert.DoesNotContain("—", e.Info.Reason);
        }

        [Fact]
        public void Dev_flag_unlocks_pro()
        {
            var e = LicenseEntitlement.Evaluate(null, null, devPro: true, Now);
            Assert.True(e.IsPro);
            Assert.Equal("pro", e.Info.Tier);
        }

        [Fact]
        public void Every_tier_exposes_one_secure_web_account_path()
        {
            var free = LicenseEntitlement.Evaluate(null, null, devPro: false, Now);
            var pro = LicenseEntitlement.Evaluate(null, null, devPro: true, Now);

            Assert.Equal(LicenseEntitlement.ManageUrl, free.Info.ManageUrl);
            Assert.Equal(LicenseEntitlement.ManageUrl, pro.Info.ManageUrl);
            Assert.StartsWith("https://", free.Info.ManageUrl, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(LicenseEntitlement.ManageUrl, LicenseEntitlement.RenewUrl);
        }

        [Fact]
        public void Unconfigured_public_key_never_unlocks_on_a_token()
        {
            // With no production key (placeholder) and no dev flag, even a token stays free — the safe default.
            var e = LicenseEntitlement.Evaluate("some-token", "REPLACE_AT_ISSUANCE", devPro: false, Now);
            Assert.False(e.IsPro);
        }
    }
}
