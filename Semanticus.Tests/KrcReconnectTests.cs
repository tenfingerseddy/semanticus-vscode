using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class KrcReconnectTests : IDisposable
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso";
        private const string Database = "Contoso Sales";
        private const string Tenant = "contoso.com";
        private readonly string _root;
        private readonly string _previousRoot;
        private readonly string _previousPersistDir;
        private readonly string _previousCacheName;

        public KrcReconnectTests(RestoreRootFixture fixture)
        {
            _previousRoot = fixture.Root;
            _previousPersistDir = EntraToken.PersistDirOverride;
            _previousCacheName = EntraToken.TokenCacheNameOverride;
            _root = Path.Combine(Path.GetTempPath(), "krc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");
            EntraToken.TokenCacheNameOverride = "semanticus-krc-" + Guid.NewGuid().ToString("N");
        }

        public void Dispose()
        {
            EntraToken.CredentialBuiltForTests = null;
            EntraToken.PersistDirOverride = _previousPersistDir;
            EntraToken.TokenCacheNameOverride = _previousCacheName;
            ConnectionRegistry.RootOverride = _previousRoot;
            Directory.Delete(_root, recursive: true);
        }

        private static string SeedAccount(string family = "interactive", string tenant = Tenant, bool makeDefault = true)
        {
            const string username = "modeler@contoso.com";
            var home = "contoso-account." + tenant;
            var record = JsonSerializer.Serialize(new
            {
                username, authority = "login.microsoftonline.com", homeAccountId = home,
                tenantId = tenant, clientId = "contoso-client", version = "1.0",
            });
            return EntraToken.SeedProfileForTests(family, tenant, home, username, record, makeDefault);
        }

        // Stop at the existing credential-construction hook. No token, browser, CLI or endpoint is used.
        private sealed class CredentialReached : Exception
        {
            public string Mode { get; }
            public CredentialReached(string mode) { Mode = mode; }
        }

        private static Task<ConnectionStatus> Connect(LocalEngine engine, string door, string? profile = null, string? mode = null)
        {
            return door switch
            {
                "mcp" => McpTools.ConnectXmla(engine, Endpoint, Database, mode, accountProfileId: profile),
                "rpc-omitted" => new EngineRpcTarget(engine).connectXmla(Endpoint, Database, accountProfileId: profile),
                "rpc-null" => new EngineRpcTarget(engine).connectXmla(Endpoint, Database, authMode: mode, accountProfileId: profile),
                _ => engine.ConnectXmlaAsync(Endpoint, Database, mode, null, accountProfileId: profile, origin: "agent"),
            };
        }

        [Theory]
        [InlineData("mcp")]
        [InlineData("rpc-omitted")]
        [InlineData("rpc-null")]
        [InlineData("engine")]
        public async Task Reconnect_without_a_way_uses_the_saved_connection_way(string door)
        {
            SeedAccount();
            ConnectionRegistry.Remember("xmla", Endpoint, Database, Database, Tenant, "interactive", "modeler@contoso.com");
            // A different saved default must not replace this source's sign-in method.
            SeedAccount("devicecode", "other.contoso.com");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", Endpoint, Database));
            Assert.False((await engine.DisconnectAsync()).Connected);
            EntraToken.CredentialBuiltForTests = mode => throw new CredentialReached(mode);

            var reached = await Assert.ThrowsAsync<CredentialReached>(() => Connect(engine, door));

            Assert.Equal("interactive", reached.Mode);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Mcp_omitted_way_reaches_the_saved_credential_after_disconnect(bool selectProfile)
        {
            var profile = SeedAccount();
            ConnectionRegistry.Remember("xmla", Endpoint, Database, Database, Tenant, "interactive");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", Endpoint, Database));
            await engine.DisconnectAsync();
            var modes = new List<string>();
            EntraToken.CredentialBuiltForTests = mode => { modes.Add(mode); throw new CredentialReached(mode); };
            var (host, client) = await ToolMergeTests.StartDoorAsync(engine, gated: true);
            using (host)
            await using (client)
            {
                var arguments = new Dictionary<string, object?> { ["endpoint"] = Endpoint, ["database"] = Database };
                if (selectProfile) arguments["accountProfileId"] = profile;

                var result = await client.CallToolAsync("connect_xmla", arguments);

                Assert.True(result.IsError == true); // The hook stops the request before authentication.
                Assert.Equal(new[] { "interactive" }, modes);
            }
        }

        [Theory]
        [InlineData("mcp")]
        [InlineData("rpc-omitted")]
        [InlineData("rpc-null")]
        [InlineData("engine")]
        public async Task A_selected_saved_account_with_no_way_uses_its_own_way(string door)
        {
            var profile = SeedAccount(makeDefault: false);
            ConnectionRegistry.Remember("xmla", Endpoint, Database, Database, Tenant, "devicecode");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            EntraToken.CredentialBuiltForTests = mode => throw new CredentialReached(mode);

            var reached = await Assert.ThrowsAsync<CredentialReached>(() => Connect(engine, door, profile));

            Assert.Equal("interactive", reached.Mode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void An_empty_way_is_not_a_saved_account_mismatch(string? mode)
        {
            var profile = SeedAccount();
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal(profile, mode, null));
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal(profile, mode, Tenant));
        }

        [Theory]
        [InlineData("mcp")]
        [InlineData("rpc-null")]
        [InlineData("engine")]
        public async Task Two_different_named_ways_keep_a_plain_mismatch_message(string door)
        {
            var profile = SeedAccount();
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            EntraToken.CredentialBuiltForTests = _ => throw new CredentialReached("unexpected");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(engine, door, profile, "devicecode"));

            Assert.Contains("different way", error.Message);
            Assert.Contains("browser", error.Message);
            Assert.Contains("device code", error.Message);
            Assert.DoesNotContain("()", error.Message);
        }

        [Fact]
        public async Task A_named_way_stays_the_callers_choice()
        {
            SeedAccount();
            ConnectionRegistry.Remember("xmla", Endpoint, Database, Database, Tenant, "interactive");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            EntraToken.CredentialBuiltForTests = mode => throw new CredentialReached(mode);

            var reached = await Assert.ThrowsAsync<CredentialReached>(() => Connect(engine, "mcp", mode: "azcli"));

            Assert.Equal("azcli", reached.Mode);
        }

        [Fact]
        public void An_omitted_way_still_reports_a_real_tenant_mismatch()
        {
            var profile = SeedAccount();
            Assert.Contains("different tenant", LocalEngine.AccountProfileMismatchRefusal(profile, null, "other.contoso.com"));
        }
    }
}
