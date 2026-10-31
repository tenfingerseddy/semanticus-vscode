using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// fx171 (Kane, 30 Oct; tester cx1 blocker 1 and gap 6): a model in another tenant, opened as an account of that
    /// tenant. The open signed in with no tenant named, so the saved connection kept no tenant. The next open, the
    /// account line ("as home (was guest)") and the SQL endpoint sign-in then all fell back to the HOME account and
    /// tenant. These tests prove: an open remembers the tenant its sign-in used; a named tenant reuses the saved sign-in
    /// of THAT tenant (no browser, no home fallback); a saved account says which tenant it is in; and the account choice
    /// for a connection that knows its tenant is not refused. No sign-in runs: identity-only records with fake tenants.
    /// </summary>
    [Collection("restore-root")]
    public sealed class GuestTenantSignInTests : IDisposable
    {
        private const string Home = "11111111-aaaa-4aaa-8aaa-111111111111";    // the person's own tenant
        private const string Guest = "22222222-bbbb-4bbb-8bbb-222222222222";   // a client's tenant
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevCacheName;

        public GuestTenantSignInTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-guest-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.TokenCacheNameOverride = "semanticus-guest-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            EntraToken.PersistDirOverride = null;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string HomeId(string username, string tenant) => "oid-" + username + "." + tenant;
        private static string RecordJsonFor(string username, string tenant) =>
            "{\"username\":\"" + username + "\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"" +
            HomeId(username, tenant) + "\",\"tenantId\":\"" + tenant + "\",\"clientId\":\"client\",\"version\":\"1.0\"}";
        private static AuthenticationRecord Record(string username, string tenant)
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(RecordJsonFor(username, tenant)));
            return AuthenticationRecord.Deserialize(ms);
        }

        // The person's state after Kane's morning: their own account is the default of the tenantless browser sign-in, and
        // "Sign in another account" (no tenant named) added the client account as a saved profile without moving the default.
        private void SeedHomeDefaultAndGuestProfile()
        {
            var path = EntraToken.RecordPathForTests("interactive", null);
            Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("kane@home.example", Home)));
            EntraToken.SeedProfileForTests("interactive", null, HomeId("kane@home.example", Home), "kane@home.example", RecordJsonFor("kane@home.example", Home), makeDefault: false);
            EntraToken.SeedProfileForTests("interactive", null, HomeId("alex@contoso-a.example", Guest), "alex@contoso-a.example", RecordJsonFor("alex@contoso-a.example", Guest), makeDefault: false);
        }

        [Fact]
        public void An_open_with_no_tenant_remembers_the_tenant_its_sign_in_used()
        {
            var prepared = new EntraToken.PreparedCredential { ResolvedRecord = Record("alex@contoso-a.example", Guest) };
            Assert.Equal(Guest, LocalEngine.RecordTenantForOpen(null, "interactive", prepared));
            // A tenant the person named stays exactly as named; a sign-in way with no saved record adds nothing.
            Assert.Equal("contoso.com", LocalEngine.RecordTenantForOpen("contoso.com", "interactive", prepared));
            Assert.Null(LocalEngine.RecordTenantForOpen(null, "serviceprincipal", null));
            Assert.Null(LocalEngine.RecordTenantForOpen(null, "interactive", null));
        }

        [Fact]
        public async Task A_named_tenant_reuses_the_saved_sign_in_of_that_tenant_not_the_home_default()
        {
            SeedHomeDefaultAndGuestProfile();
            // The client tenant has no default slot of its own. It must still find the saved client account, silently.
            Assert.Equal("alex@contoso-a.example", EntraToken.ReadSavedAccount("interactive", Guest)?.Username);
            var prepared = await EntraToken.BuildCredentialAsync("interactive", Guest, System.Threading.CancellationToken.None);
            Assert.Equal("alex@contoso-a.example", prepared.Account);
            Assert.False(prepared.HasPendingRecord);   // pinned: no browser sign-in was needed
            // The home tenant, now named, still finds the person's own default.
            Assert.Equal("kane@home.example", EntraToken.ReadSavedAccount("interactive", Home)?.Username);
            // A tenant nobody signed in to is still unknown (no guess from another tenant).
            Assert.Null(EntraToken.ReadSavedAccount("interactive", "33333333-cccc-4ccc-8ccc-333333333333"));
        }

        [Fact]
        public async Task The_account_line_for_a_model_that_knows_its_tenant_names_the_account_of_that_tenant()
        {
            SeedHomeDefaultAndGuestProfile();
            var r = ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Workspace%20A", "Contoso Sales", "Contoso Sales", Guest, "interactive", "alex@contoso-a.example");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var p = (await engine.ProbeConnectionAccountsAsync()).Single(x => x.Id == r.Id);
            Assert.Equal("alex@contoso-a.example", p.Account);
            Assert.Null(p.PreviousAccount);   // no "(was ...)" line: the account in use IS the one that opened it
            Assert.Equal(Guest, p.TenantId);
        }

        [Fact]
        public void A_saved_account_says_which_tenant_it_signed_in_to_and_may_open_a_model_in_that_tenant()
        {
            SeedHomeDefaultAndGuestProfile();
            var alex = EntraToken.ListProfiles().Single(x => x.Username == "alex@contoso-a.example");
            Assert.Equal(Guest, alex.AccountTenantId);
            // The model knows its tenant now; picking that saved account for it is not a mismatch.
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal(alex.Id, "interactive", Guest));
            // A different named tenant is still refused.
            Assert.NotNull(LocalEngine.AccountProfileMismatchRefusal(alex.Id, "interactive", Home));
        }

        [Fact]
        public async Task Use_another_account_in_the_workspace_picker_signs_in_at_the_tenant_the_person_gave()
        {
            // cx1 blocker 1: "Use another account" read the list at "organizations" whatever tenant was typed, so a client's
            // tenant could not be reached. No sign-in runs: the browser step is refused here, after the credential is built.
            var seen = new System.Collections.Generic.List<string>();
            EntraToken.CredentialTenantForTests = (mode, tenant) => seen.Add(tenant);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                await Assert.ThrowsAnyAsync<Exception>(() => engine.ListWorkspacesAsync("interactive", Guest, "human", default, anotherAccount: true));
            }
            finally { EntraToken.CredentialTenantForTests = null; }
            Assert.Contains(Guest, seen);
            Assert.DoesNotContain(null, seen);
        }

        [Fact]
        public void Connection_status_names_the_tenant_of_the_connection()
        {
            Assert.NotNull(typeof(ConnectionStatus).GetProperty("TenantId"));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public async Task Fx174_workspace_sign_in_reuses_the_last_connection_or_saved_account_tenant(bool connection, bool another)
        {
            SeedHomeDefaultAndGuestProfile();
            if (connection) ConnectionRegistry.Remember("xmla", "powerbi://x/client", "Client", "Client", Guest, "interactive", "alex@contoso-a.example");
            string seen = null;
            EntraToken.CredentialTenantForTests = (mode, tenant) => { seen = tenant; throw new InvalidOperationException("offline credential probe"); };
            try
            {
                using var engine = new LocalEngine(new SessionManager());
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ListWorkspacesAsync("interactive", null, "human", default, anotherAccount: another));
            }
            finally { EntraToken.CredentialTenantForTests = null; }
            Assert.Equal(connection ? Guest : Home, seen);
        }
    }
}
