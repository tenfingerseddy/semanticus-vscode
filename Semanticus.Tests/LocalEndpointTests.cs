using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Loopback classification must fail closed on every accepted host form, while adjacent and spoof hosts stay remote.
    /// Case ids are stable. Unlabelled remotes stay prod (the strictest label).
    /// </summary>
    [Collection("restore-root")]   // ResolveTargetLabel reads the static ConnectionRegistry root — serialize it, or a
                                   // concurrent collection's temp root (or the developer's real connections.json) can
                                   // supply an explicit label for one of these endpoints and flip the expected answer.
    public sealed class LocalEndpointTests
    {
        public LocalEndpointTests(RestoreRootFixture fixture)
        {
            // Pin the registry at the collection's own clean root, so the label half of each case is decided by the
            // loopback inference and never by a record somebody else left on disk.
            ConnectionRegistry.RootOverride = fixture.Root;
        }

        // L-ids are loopback (must classify local). R-ids must stay remote. Do not renumber.
        [Theory]
        [InlineData("L01", "localhost:51234", true)]
        [InlineData("L02", "127.0.0.1:51234", true)]
        [InlineData("L03", "127.0.0.2:51234", true)]
        [InlineData("L04", "127.0.0.0:51234", true)]
        [InlineData("L05", "127.255.255.255:51234", true)]
        [InlineData("L06", "::1", true)]
        [InlineData("L07", "[::1]:51234", true)]
        [InlineData("L08", ".", true)]
        [InlineData("L09", "0:0:0:0:0:0:0:1", true)]
        [InlineData("L10", "::ffff:127.0.0.1", true)]
        [InlineData("L11", "[::ffff:127.0.0.1]:51234", true)]
        [InlineData("L12", "http://localhost:51234", true)]
        [InlineData("L13", "https://127.0.0.1", true)]
        [InlineData("L14", "msolap://localhost", true)]
        [InlineData("L15", "Data Source=\"localhost:51234\"", true)]
        [InlineData("L16", "Data Source='127.0.0.1'", true)]
        [InlineData("L17", "localhost.:51234", true)]
        [InlineData("L18", "localhost.", true)]
        [InlineData("L19", "Server=127.0.0.2", true)]
        [InlineData("L20", "http://localhost./", true)]
        [InlineData("L21", "::ffff:127.0.0.2", true)]
        [InlineData("L22", "http://[::1]/model", true)]
        [InlineData("R01", "128.0.0.1:51234", false)]
        [InlineData("R02", "126.255.255.255:51234", false)]
        [InlineData("R03", "localhost.evil.com", false)]
        [InlineData("R04", "127.0.0.1.evil.com", false)]
        [InlineData("R05", "powerbi://api.powerbi.com/v1.0/myorg/prod-localhost-mirror", false)]
        [InlineData("R06", "asazure://westus.asazure.windows.net/srv", false)]
        [InlineData("R07", "https://example/xmla", false)]
        [InlineData("R08", "", false)]
        [InlineData("R09", "http://localhost.evil.com", false)]
        [InlineData("R10", "0.0.0.0:51234", false)]
        [InlineData("R11", "[::1].evil.com", false)]
        [InlineData("R12", "[::1]evil.com", false)]
        [InlineData("R13", "[::1]@evil.com", false)]
        [InlineData("R14", "[::1]:bad", false)]
        [InlineData("R15", "[::1]/evil", false)]
        [InlineData("R16", "http://[::1].evil.com", false)]
        [InlineData("R17", "http://[::1]:bad", false)]
        [InlineData("R18", "X=http://localhost;Password=marker", false)]
        [InlineData("R19", "localhost:１２３", false)]
        [InlineData("R20", "[::1]:１２３", false)]
        [InlineData("R21", "http://localhost;@evil.example/x", false)]
        [InlineData("R22", "http://localhost:80;@evil.example/x", false)]
        [InlineData("R23", "http://[::1];@evil.example/x", false)]
        [InlineData("R24", "http://[::1]:80;@evil.example/x", false)]
        [InlineData("R25", "http://localhost;@evil.example/x?q=1", false)]
        public void Classifies_loopback_fail_closed_and_preserves_adjacent_spoof(string id, string endpoint, bool local)
        {
            Assert.Equal(local, LiveDeploy.IsLocalEndpoint(endpoint));
            if (string.IsNullOrEmpty(endpoint)) return;
            var expectedLabel = local ? "local" : "prod";
            Assert.Equal(expectedLabel, LocalEngine.ResolveTargetLabel(endpoint, "DS"));
            _ = id;
        }

        [Fact]
        public void Parse_strips_quotes_from_connection_string_hosts()
        {
            var c = ConnectionInput.Parse("Data Source=\"localhost:51234\";Initial Catalog=\"Sales\"", null);
            Assert.Equal("localhost:51234", c.Endpoint);
            Assert.Equal("Sales", c.Database);
            Assert.True(c.Safe);
        }
    }

    [Collection("restore-root")]
    public sealed class DeployLiveInputScrubTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public DeployLiveInputScrubTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-deploy-scrub-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot;
            ApprovalLedger.RootOverride = _safeRoot;
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string PwdKey() => "Pass" + "word=";

        [Fact]
        public async Task Deploy_live_scrubs_input_before_policy_approvals_or_records()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("ScrubDeploy", 1604);
            var secret = "DEP" + Guid.NewGuid().ToString("N");
            var raw = "Data Source=powerbi://api.powerbi.com/v1.0/myorg/WS;Initial Catalog=Sales;" + PwdKey() + secret;

            var token = await ReviewFenceTest.TokenAsync(engine, "powerbi://api.powerbi.com/v1.0/myorg/WS", "Sales", "agent");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync(raw, "Sales", "azcli", null, null, commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            Assert.DoesNotContain(secret, ex.Message);
            Assert.Contains("approval", ex.Message, StringComparison.OrdinalIgnoreCase);
            foreach (var pending in ApprovalLedger.List())
            {
                Assert.DoesNotContain(secret, pending.Summary ?? "");
                Assert.DoesNotContain(secret, pending.Target ?? "");
                Assert.Contains("powerbi://api.powerbi.com/v1.0/myorg/WS", pending.Target ?? "");
                Assert.Equal("prod", pending.Label);
            }
            var chain = await engine.ListVerifiedEditsAsync();
            Assert.DoesNotContain(chain.Records, r => (r.Summary ?? "").Contains(secret) || (r.Evidence ?? "").Contains(secret));
        }

        [Fact]
        public async Task Quoted_loopback_is_local_before_the_policy_gate()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("QuotedLocal", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");

            var token = await ReviewFenceTest.TokenAsync(engine, "Data Source=\"localhost:2\"", "QuotedLocal", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync("Data Source=\"localhost:2\"", "QuotedLocal", null, null, null,
                    commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            Assert.Empty(ApprovalLedger.List());
            Assert.DoesNotContain("approval", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
