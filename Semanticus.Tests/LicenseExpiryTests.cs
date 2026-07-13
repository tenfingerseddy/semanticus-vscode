using System;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Pins the annual-subscription expiry POLICY that <see cref="LicenseEntitlement.Evaluate"/> layers on top
    /// of the signature primitive: unexpired ⇒ Pro; lapsed-but-within-<see cref="LicenseEntitlement.GraceDays"/> ⇒ still
    /// Pro with a teaching grace Reason; past grace ⇒ Free with a teaching Reason; a token with NO exp claim ⇒ perpetual
    /// (backward-compat, must never brick old tokens); a tampered exp ⇒ the signature fails. Deterministic: ephemeral
    /// keypair + injected clock (mirrors EntitlementVerifierTests' test-key pattern).</summary>
    public sealed class LicenseExpiryTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);

        private static (string pub, string priv) Keys() => LicenseVerifier.GenerateKeyPair();

        // exp is a unix-seconds instant (0 = perpetual); default = a year out, i.e. a normal valid annual license.
        private static string Token(string priv, long exp, string sub = "buyer@example.com", string tier = "pro")
            => LicenseVerifier.Mint(priv, new LicenseClaims { Sub = sub, Tier = tier, Iat = Now.ToUnixTimeSeconds(), Exp = exp });

        [Fact]
        public void Valid_unexpired_token_is_pro_with_no_grace_reason()
        {
            var (pub, priv) = Keys();
            var e = LicenseEntitlement.Evaluate(Token(priv, Now.AddDays(200).ToUnixTimeSeconds()), pub, devPro: false, Now);
            Assert.True(e.IsPro);
            Assert.Equal("pro", e.Info.Tier);
            Assert.Equal("buyer@example.com", e.Info.LicensedTo);
            Assert.True(string.IsNullOrEmpty(e.Info.Reason));   // a healthy license carries no advisory
        }

        [Fact]
        public void In_grace_token_is_still_pro_and_teaches_the_grace_deadline()
        {
            var (pub, priv) = Keys();
            var exp = Now.AddDays(-3);   // expired 3 days ago — inside the 14-day grace window
            var e = LicenseEntitlement.Evaluate(Token(priv, exp.ToUnixTimeSeconds()), pub, devPro: false, Now);
            Assert.True(e.IsPro);                                 // grace keeps a paying subscriber on Pro
            Assert.Equal("pro", e.Info.Tier);
            Assert.Contains("in grace until", e.Info.Reason);     // teaches the soft deadline...
            Assert.Contains(LicenseEntitlement.RenewUrl, e.Info.Reason);   // ...and where to renew
            Assert.DoesNotContain("—", e.Info.Reason);            // user-facing release copy stays plain
        }

        [Fact]
        public void Grace_boundary_last_day_is_pro_but_just_past_is_free()
        {
            var (pub, priv) = Keys();
            // exp such that `now` sits exactly on the last grace day vs one day past it.
            var lastGraceDayExp = Now.AddDays(-LicenseEntitlement.GraceDays);      // graceEnd == now ⇒ still Pro (<=)
            var pastGraceExp = Now.AddDays(-(LicenseEntitlement.GraceDays + 1));   // graceEnd < now  ⇒ Free
            Assert.True(LicenseEntitlement.Evaluate(Token(priv, lastGraceDayExp.ToUnixTimeSeconds()), pub, false, Now).IsPro);
            Assert.False(LicenseEntitlement.Evaluate(Token(priv, pastGraceExp.ToUnixTimeSeconds()), pub, false, Now).IsPro);
        }

        [Fact]
        public void Expired_past_grace_is_free_with_a_teaching_reason()
        {
            var (pub, priv) = Keys();
            var exp = Now.AddDays(-(LicenseEntitlement.GraceDays + 30));   // long lapsed
            var e = LicenseEntitlement.Evaluate(Token(priv, exp.ToUnixTimeSeconds()), pub, devPro: false, Now);
            Assert.False(e.IsPro);
            Assert.Equal("free", e.Info.Tier);
            Assert.Contains("expired", e.Info.Reason);                    // says WHAT happened
            Assert.Contains(LicenseEntitlement.RenewUrl, e.Info.Reason);  // WHERE to renew
            Assert.Contains("free tier", e.Info.Reason);                  // reassures nothing broke
            Assert.DoesNotContain("—", e.Info.Reason);                    // user-facing release copy stays plain
        }

        [Fact]
        public void No_exp_claim_is_perpetual_pro_backward_compat()
        {
            // A token minted before the exp claim existed (or with --days<=0) deserializes exp to 0 ⇒ perpetual.
            // This must stay Pro even far in the future, or we'd brick every founder/dev token in the field.
            var (pub, priv) = Keys();
            var perpetual = Token(priv, 0);
            Assert.True(LicenseEntitlement.Evaluate(perpetual, pub, false, Now).IsPro);
            Assert.True(LicenseEntitlement.Evaluate(perpetual, pub, false, Now.AddYears(25)).IsPro);
        }

        [Fact]
        public void Tampered_exp_fails_the_signature_and_is_free()
        {
            // Extending your own license by editing exp must break the ECDSA signature ⇒ Free (not silently honored).
            var (pub, priv) = Keys();
            var token = Token(priv, Now.AddDays(-100).ToUnixTimeSeconds());   // a long-expired token...
            var i = token.Length / 5;   // flip a character inside the payload (before the '.')
            var bad = token.Substring(0, i) + (token[i] == 'A' ? 'B' : 'A') + token.Substring(i + 1);
            Assert.Null(LicenseVerifier.VerifySignature(bad, pub));           // signature no longer verifies...
            var e = LicenseEntitlement.Evaluate(bad, pub, false, Now);
            Assert.False(e.IsPro);                                            // ...so it can't buy back Pro
            Assert.Contains("signature", e.Info.Reason);
        }
    }
}
