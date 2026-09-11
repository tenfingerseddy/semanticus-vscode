using System;
using System.IO;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class LicenseTokenDeliveryTests
    {
        // The owner's stdin is ONE stream carrying TWO lines, in this order: the UI challenge, then the Pro token
        // (ownerStdinPayload builds exactly this). The payload here is the extension's byte for byte, and the read
        // order is the one Serve uses, so the token the UI believes it sent is the token the entitlement sees.
        // Serve() itself cannot run in a test (it binds the owner pipe), so the line ORDER is pinned as a source
        // contract in Semanticus.VSCode/test/license-truth.test.mjs; this test pins the effect end to end.
        [Fact]
        public void An_activated_token_on_the_owner_stdin_pipe_reaches_the_pro_tier()
        {
            var (publicKey, privateKey) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(privateKey, new LicenseClaims
            {
                Sub = "owner@example.invalid",   // invented, never a real address
                Tier = "pro",
                Iat = 1751932800,                // 2026-07-08T00:00:00Z
                Exp = 0,                         // perpetual: the token that must survive the upgrade
            });
            var args = new[] { "serve", "--workspace", "/tmp/ws", "--ui-challenge-stdin", "--license-stdin" };

            using var stdin = new StringReader($"ui-challenge-value\n{token}\n");
            var challenge = args.Length > 0 && Array.IndexOf(args, "--ui-challenge-stdin") >= 0 ? stdin.ReadLine() : null;
            var delivered = LicenseTokenDelivery.FromArgs(args, stdin);

            Assert.Equal("ui-challenge-value", challenge);
            Assert.Equal(token, delivered);
            Assert.False(LicenseTokenDelivery.CommandLineExposesToken(args));

            var e = LicenseEntitlement.Evaluate(delivered, publicKey, devPro: false, new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
            Assert.True(e.IsPro);
            Assert.Equal("pro", e.Info.Tier);
            Assert.Equal("owner@example.invalid", e.Info.LicensedTo);
        }

        [Fact]
        public void A_missing_token_line_still_leaves_the_challenge_intact()
        {
            // The free-tier payload is "challenge\n\n" — the empty token line must NOT be mistaken for one, and it
            // must not swallow the challenge read either.
            var args = new[] { "serve", "--ui-challenge-stdin", "--license-stdin" };
            using var stdin = new StringReader("ui-challenge-value\n\n");
            var challenge = stdin.ReadLine();
            var delivered = LicenseTokenDelivery.FromArgs(args, stdin);

            Assert.Equal("ui-challenge-value", challenge);
            Assert.Null(delivered);
        }

        [Fact]
        public void Stdin_flag_reads_the_token_without_putting_it_on_argv()
        {
            var args = new[] { "serve", "--workspace", "/tmp/ws", "--ui-challenge-stdin", "--license-stdin" };
            using var stdin = new StringReader("pro-token-from-stdin\n");
            var token = LicenseTokenDelivery.FromArgs(args, stdin);
            Assert.Equal("pro-token-from-stdin", token);
            Assert.False(LicenseTokenDelivery.CommandLineExposesToken(args));
        }

        [Fact]
        public void Empty_stdin_line_is_not_a_token()
        {
            var args = new[] { "serve", "--license-stdin" };
            using var stdin = new StringReader("\n");
            Assert.Null(LicenseTokenDelivery.FromArgs(args, stdin));
        }

        [Fact]
        public void Legacy_argv_token_is_still_read_but_is_flagged_as_exposed()
        {
            var args = new[] { "mcp", "--workspace", "/tmp/ws", "--license", "old-argv-token" };
            Assert.Equal("old-argv-token", LicenseTokenDelivery.FromArgs(args, null));
            Assert.True(LicenseTokenDelivery.CommandLineExposesToken(args));
        }

        [Fact]
        public void Mcp_args_without_a_token_are_not_exposed()
        {
            var args = new[] { "mcp", "--workspace", "/tmp/ws" };
            Assert.Null(LicenseTokenDelivery.FromArgs(args, null));
            Assert.False(LicenseTokenDelivery.CommandLineExposesToken(args));
        }
    }
}
