using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Secret hygiene: <see cref="FabricRest.Scrub"/> must redact every credential form that can reach a
    /// surfaced error (the cloud-lane catch blocks route exception messages through it before putting them on a
    /// result DTO that crosses a door). A leak here silently exfiltrates a token — high blast radius, so it is
    /// pinned with a battery of the real-world forms.</summary>
    public sealed class SecretScrubTests
    {
        private const string Jwt = "eyJhbGciOiJSUzI1NiIsImtpZCI6IngifQ.eyJhdWQiOiJodHRwczovL2FwaSJ9.S1gnaTuReHere_aBc-123";

        [Theory]
        [InlineData("Authorization: Bearer " + Jwt)]
        [InlineData("bearer " + Jwt + " expired")]
        [InlineData("connect failed: Data Source=x;Password=hunter2;Initial Catalog=db")]
        [InlineData("Pwd=s3cr3t;Encrypt=true")]
        [InlineData("access_token=abc.def.ghi&scope=...")]
        [InlineData("access token = " + Jwt)]
        [InlineData("{\"error\":\"bad\",\"token\":\"" + Jwt + "\"}")]   // bare JWT in a JSON body — the hardened case
        public void Scrub_redacts_every_secret_form(string message)
        {
            var scrubbed = FabricRest.Scrub(message);
            Assert.DoesNotContain("hunter2", scrubbed);
            Assert.DoesNotContain("s3cr3t", scrubbed);
            Assert.DoesNotContain(Jwt, scrubbed);
            Assert.DoesNotContain("abc.def.ghi", scrubbed);
        }

        [Fact]
        public void Scrub_leaves_a_benign_message_untouched()
        {
            const string benign = "Not found — check the workspace id (404).";
            Assert.Equal(benign, FabricRest.Scrub(benign));
        }

        [Fact]
        public void Scrub_is_null_and_empty_safe()
        {
            Assert.Null(FabricRest.Scrub(null));
            Assert.Equal("", FabricRest.Scrub(""));
        }
    }
}
