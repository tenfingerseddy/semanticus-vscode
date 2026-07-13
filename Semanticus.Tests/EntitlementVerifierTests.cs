using System;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The offline license primitive (mint with the private key ↔ verify with the public key) and the
    /// LicenseEntitlement policy: a missing / expired / tampered / wrong-key / wrong-tier license ⇒ FREE, a valid
    /// Pro token ⇒ Pro, the dev escape ⇒ Pro. Pure + deterministic (an injected clock, ephemeral keypairs).</summary>
    public sealed class EntitlementVerifierTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);

        private static (string pub, string priv) Keys() => LicenseVerifier.GenerateKeyPair();

        private static string Token(string priv, long exp, string sub = "buyer@example.com", string tier = "pro")
            => LicenseVerifier.Mint(priv, new LicenseClaims { Sub = sub, Tier = tier, Iat = Now.ToUnixTimeSeconds(), Exp = exp });

        [Fact]
        public void Valid_pro_token_verifies_and_unlocks_pro()
        {
            var (pub, priv) = Keys();
            var ent = LicenseEntitlement.Evaluate(Token(priv, Now.AddDays(365).ToUnixTimeSeconds()), pub, devPro: false, Now);
            Assert.True(ent.IsPro);
            Assert.Equal("pro", ent.Info.Tier);
            Assert.Equal("buyer@example.com", ent.Info.LicensedTo);
        }

        [Fact]
        public void Verify_returns_claims_for_a_perpetual_token()
        {
            var (pub, priv) = Keys();
            var claims = LicenseVerifier.Verify(Token(priv, 0), pub, Now);   // exp=0 ⇒ perpetual
            Assert.NotNull(claims);
            Assert.Equal("pro", claims!.Tier);
            Assert.NotNull(LicenseVerifier.Verify(Token(priv, 0), pub, Now.AddYears(50)));
        }

        [Fact]
        public void Expired_token_is_free()
        {
            // Expired well BEYOND the grace window (see GraceDays) so both the raw hard-expiry Verify AND the
            // grace-aware Evaluate agree it's lapsed. (In-grace and just-past-grace boundaries are pinned in LicenseExpiryTests.)
            var (pub, priv) = Keys();
            var token = Token(priv, Now.AddDays(-(LicenseEntitlement.GraceDays + 10)).ToUnixTimeSeconds());
            Assert.Null(LicenseVerifier.Verify(token, pub, Now));   // hard expiry: no grace at the primitive level
            Assert.False(LicenseEntitlement.Evaluate(token, pub, false, Now).IsPro);
        }

        [Fact]
        public void Tampered_payload_is_rejected()
        {
            var (pub, priv) = Keys();
            var token = Token(priv, 0);
            var i = token.Length / 4;   // a character inside the payload (before the '.')
            var bad = token.Substring(0, i) + (token[i] == 'A' ? 'B' : 'A') + token.Substring(i + 1);
            Assert.Null(LicenseVerifier.Verify(bad, pub, Now));
        }

        [Fact]
        public void Wrong_public_key_is_rejected()
        {
            var (_, priv) = Keys();
            var (otherPub, _) = Keys();
            Assert.Null(LicenseVerifier.Verify(Token(priv, 0), otherPub, Now));
        }

        [Fact]
        public void Non_pro_tier_is_free()
        {
            var (pub, priv) = Keys();
            Assert.False(LicenseEntitlement.Evaluate(Token(priv, 0, tier: "trial"), pub, false, Now).IsPro);
        }

        [Fact]
        public void No_token_or_garbage_is_free_never_throws()
        {
            var (pub, _) = Keys();
            Assert.False(LicenseEntitlement.Evaluate(null, pub, false, Now).IsPro);
            Assert.Null(LicenseVerifier.Verify("not-a-token", pub, Now));
            Assert.Null(LicenseVerifier.Verify("a.b.c", pub, Now));
            Assert.False(LicenseEntitlement.Evaluate("garbage", pub, false, Now).IsPro);
        }

        [Fact]
        public void Dev_flag_unlocks_pro_without_a_token()
        {
            Assert.True(LicenseEntitlement.Evaluate(null, null, devPro: true, Now).IsPro);
        }

        [Fact]
        public void Embedded_public_key_is_a_real_p256_spki()
        {
            // The shipped constant must be a parseable P-256 SPKI (not the placeholder), so production tokens verify.
            Assert.NotEqual("REPLACE_AT_ISSUANCE", LicenseEntitlement.EmbeddedPublicKey);
            using var ec = System.Security.Cryptography.ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicenseEntitlement.EmbeddedPublicKey), out _);  // throws if malformed
        }
    }
}
