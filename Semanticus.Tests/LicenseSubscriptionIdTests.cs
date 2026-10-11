using System;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The optional stable subscription id on a licence token (the Stripe billing slice, see
    /// /tmp/semanticus-joint-review/billing-contract.md). The worker mints it as the `sid` claim so the relay can
    /// map replaced and renewed tokens onto one subscription and the app can open the customer portal. Two things
    /// are pinned here: a token that carries `sid` round-trips it, and a token that does not carry it verifies
    /// exactly as it did before, which is what keeps every already-issued Paddle token working.</summary>
    public sealed class LicenseSubscriptionIdTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);

        [Fact]
        public void A_token_minted_with_a_subscription_id_round_trips_it()
        {
            var (pub, priv) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(priv, new LicenseClaims
            {
                Sub = "buyer@example.com",
                Tier = "pro",
                Sid = "sub_1UHwqtP3DpRFv4uU9QkExample",
                Iat = Now.ToUnixTimeSeconds(),
                Exp = Now.AddDays(365).ToUnixTimeSeconds(),
            });

            var claims = LicenseVerifier.Verify(token, pub, Now);
            Assert.NotNull(claims);
            Assert.Equal("sub_1UHwqtP3DpRFv4uU9QkExample", claims!.Sid);
            Assert.Equal("buyer@example.com", claims.Sub);
            Assert.True(LicenseEntitlement.Evaluate(token, pub, devPro: false, Now).IsPro);
        }

        [Fact]
        public void A_token_without_a_subscription_id_verifies_exactly_as_before()
        {
            var (pub, priv) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(priv, new LicenseClaims
            {
                Sub = "buyer@example.com",
                Tier = "pro",
                Iat = Now.ToUnixTimeSeconds(),
                Exp = Now.AddDays(365).ToUnixTimeSeconds(),
            });

            var claims = LicenseVerifier.Verify(token, pub, Now);
            Assert.NotNull(claims);
            Assert.Null(claims!.Sid);
            Assert.True(LicenseEntitlement.Evaluate(token, pub, devPro: false, Now).IsPro);
        }

        /// <summary>A null subscription id is left out of the signed payload altogether, so adding the claim did
        /// not change one byte of the token a pre-Stripe minter produces. Without this the old tokens would still
        /// verify, but every recorded token in a fixture would have moved, which is a silent break.</summary>
        [Fact]
        public void A_null_subscription_id_adds_nothing_to_the_signed_payload()
        {
            var (_, priv) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(priv, new LicenseClaims
            {
                Sub = "buyer@example.com", Tier = "pro", Iat = Now.ToUnixTimeSeconds(), Exp = 0,
            });

            var payload = token.Substring(0, token.IndexOf('.'));
            var json = System.Text.Encoding.UTF8.GetString(FromBase64Url(payload));
            Assert.DoesNotContain("sid", json, StringComparison.Ordinal);
        }

        private static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }
    }
}
