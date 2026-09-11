using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C2.1 (D-013 / D-015): a remembered sign-in survives a restart on every OS, including Linux.
    /// The store under test is isolated (PersistDirOverride + TokenCacheNameOverride): a fake credential
    /// directory and a renamed MSAL cache, never the user's real sign-in. No live tenant, no browser.
    /// </summary>
    [Collection("restore-root")]
    public sealed class PersistentSignInTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;

        public PersistentSignInTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-persist-signin-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
            _prevPersistDir = EntraToken.PersistDirOverride;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.TokenCacheNameOverride = "semanticus-c21-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string Home(string username, string tenant) => "oid-" + username + "." + tenant;
        private static string RecordJsonFor(string username, string tenant) =>
            "{\"username\":\"" + username + "\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"" +
            Home(username, tenant) + "\",\"tenantId\":\"" + tenant + "\",\"clientId\":\"client\",\"version\":\"1.0\"}";

        private static string SeedSignedIn(string tenant, string username, bool makeDefault = false) =>
            EntraToken.SeedProfileForTests("interactive", tenant, Home(username, tenant), username, RecordJsonFor(username, tenant), makeDefault);

        [Fact]
        public void The_token_cache_is_attached_on_this_platform()
        {
            // D-013: Linux used to skip TokenCachePersistenceOptions, so a Code restart lost the refresh token
            // and every later open popped the Microsoft picker again. The options object is what Azure.Identity
            // consults; asserting it here proves the cache is wired without a live sign-in.
            var interactive = EntraToken.InteractiveOptionsForTests("contoso.com", disableInteractive: false);
            Assert.NotNull(interactive.TokenCachePersistenceOptions);
            Assert.False(string.IsNullOrWhiteSpace(interactive.TokenCachePersistenceOptions.Name));
            Assert.Equal(!OperatingSystem.IsWindows(), interactive.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);

            var device = EntraToken.DeviceCodeOptionsForTests("contoso.com", disableInteractive: false);
            Assert.NotNull(device.TokenCachePersistenceOptions);
            Assert.Equal(interactive.TokenCachePersistenceOptions.Name, device.TokenCachePersistenceOptions.Name);
            Assert.Equal(!OperatingSystem.IsWindows(), device.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        }

        [Fact]
        public async Task A_saved_record_is_read_on_the_second_open()
        {
            // First open: write the identity the way a successful sign-in commits it into the fake store.
            var tenant = Guid.NewGuid().ToString();
            var path = EntraToken.RecordPathForTests("interactive", tenant);
            Assert.NotNull(path);
            Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("alice@contoso.com", tenant)));

            // Second open: a fresh BuildCredentialAsync (a new engine process after a Code restart) must pin
            // Alice from that store and not treat her as missing.
            var prepared = await EntraToken.BuildCredentialAsync("interactive", tenant, System.Threading.CancellationToken.None);
            Assert.Equal("alice@contoso.com", prepared.Account);
            Assert.False(prepared.HasPendingRecord);
            Assert.Equal("alice@contoso.com", EntraToken.ReadSavedAccount("interactive", tenant)?.Username);
            Assert.Equal("alice@contoso.com", EntraToken.LoadPinnedRecordForTests("interactive", tenant, forceReauth: false)?.Username);
        }

        [Fact]
        public async Task Probe_list_and_the_mcp_door_agree_on_the_signed_in_account()
        {
            // ONE account-state fact: Connections (the probe), the chooser (list profiles) and the agent door
            // all name the same saved account. A signed-in account must never read as unknown.
            SeedSignedIn("contoso.com", "alice@contoso.com", makeDefault: true);
            var r = ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/semanticus-test", null, "semanticus-test", "contoso.com", "interactive", lastAccount: null);

            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var probe = (await engine.ProbeConnectionAccountsAsync()).Single(p => p.Id == r.Id);
            Assert.Equal("alice@contoso.com", probe.Account);

            var listed = await engine.ListAccountProfilesAsync();
            Assert.Contains(listed, p => p.Username == "alice@contoso.com" && p.SignedIn && p.IsDefault);

            var mcp = await McpTools.ListAccountProfiles(engine);
            Assert.Contains(mcp, p => p.Username == "alice@contoso.com" && p.SignedIn && p.IsDefault);
            var mcpProbe = await McpTools.ProbeConnectionAccounts(engine);
            Assert.Contains(mcpProbe, p => p.Id == r.Id && p.Account == "alice@contoso.com");
        }

        [Fact]
        public async Task An_endpoint_only_open_with_no_tenant_still_pins_the_signed_in_default()
        {
            // D-015: a remembered workspace address with no dataset and no tenant still reuses the account a
            // named-model open already saved, instead of raising "Who should open" as if nobody is signed in.
            SeedSignedIn("contoso.com", "alice@contoso.com", makeDefault: true);
            var prepared = await EntraToken.BuildCredentialAsync("interactive", tenantId: null, System.Threading.CancellationToken.None);
            Assert.Equal("alice@contoso.com", prepared.Account);
            Assert.False(prepared.HasPendingRecord);
        }
    }
}
