using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// si1 (Kane, 29 Oct, on 1.7.0-rc23 and on the store 1.6.0): "I'm not able to sign in another account".
    ///
    /// With one saved browser account, every way the Connections page offers to add a second one ends in the workspace
    /// picker, and the picker reads its list through listWorkspaces(authMode, tenant). That call has no way to say
    /// "another account": EntraToken.AcquireFabricAsync builds BuildCredential(mode, tenant) with skipSavedRecord false,
    /// so LoadPinnedRecord pins the saved record (with no tenant named it falls back to the family's default profile).
    /// The person gets the saved account's workspaces with no prompt, or, when its token has aged out, a browser sign-in
    /// locked to the saved account's own tenant (measured in a UAT lane: the authorize URL named that tenant, not
    /// "organizations"), so an account in a client's tenant can never sign in there.
    ///
    /// The fix gives the list an explicit "another account" choice. That choice must start a NEW sign-in (the capture
    /// in BuildCredentialAsync with forceReauth, which pins no record and so reaches every tenant), never a token for
    /// the saved account. No browser opens here: InteractiveSignIn.BrowserForTests stands in for it.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SignInAnotherAccountTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;
        private readonly string _prevClientId;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _browser = new();

        // A client id that is not a registered application, so nothing could complete even past the stand-in.
        private const string UnregisteredClientId = "00000000-dead-4bee-8000-0000000005a1";

        public SignInAnotherAccountTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-si1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            _prevPersistDir = EntraToken.PersistDirOverride;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.TokenCacheNameOverride = "semanticus-si1-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            EntraToken.FabricTokenForTests = null;   // a real credential must be built
            _prevClientId = Environment.GetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID");
            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", UnregisteredClientId);
            InteractiveSignIn.BrowserForTests = what => _browser.Enqueue(what);
        }

        public void Dispose()
        {
            InteractiveSignIn.BrowserForTests = null;
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", _prevClientId);
            ConnectionRegistry.RootOverride = _safeRoot;
            AgentPolicyStore.RootOverride = _safeRoot;
            ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private const string SavedTenant = "00000000-0000-0000-0000-0000000c0450";
        private const string SavedHome = "00000000-0000-0000-0000-00000000a11e." + SavedTenant;

        // A saved browser account exactly as a workspace pick leaves it: no tenant named, the default of the family.
        private static void SeedSavedAccount() =>
            EntraToken.SeedProfileForTests("interactive", null, SavedHome, "alex@contoso.com",
                "{\"username\":\"alex@contoso.com\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"" + SavedHome +
                "\",\"tenantId\":\"" + SavedTenant + "\",\"clientId\":\"" + UnregisteredClientId + "\",\"version\":\"1.0\"}", makeDefault: true);

        [Fact]
        public void The_workspace_list_pins_the_saved_account_when_no_tenant_is_named()
        {
            // The mechanism, pinned so the fix cannot quietly drop it: an ordinary list keeps using the saved account.
            SeedSavedAccount();
            Assert.Equal("alex@contoso.com", EntraToken.LoadPinnedRecordForTests("interactive", null, forceReauth: false)?.Username);
        }

        [Fact]
        public async Task The_workspace_list_can_sign_in_another_account_beside_a_saved_one()
        {
            SeedSavedAccount();
            var list = typeof(LocalEngine).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == nameof(LocalEngine.ListWorkspacesAsync))
                .FirstOrDefault(m => m.GetParameters().Any(p => p.Name == "anotherAccount" && p.ParameterType == typeof(bool)));
            Assert.True(list != null,
                "listWorkspaces has no way to ask for another account: with a saved account the workspace picker, and so " +
                "'Sign in another account' and 'Workspace model', always read the list as the saved account (si1).");

            using var engine = new LocalEngine(new SessionManager(), new Fake());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var args = list.GetParameters().Select(p => p.Name switch
            {
                "authMode" => "interactive",
                "tenantId" => null,
                "origin" => "human",
                "anotherAccount" => true,
                "cancellationToken" => (object)cts.Token,
                _ => p.HasDefaultValue ? p.DefaultValue : null,
            }).ToArray();
            using (SignInPress.Press())
            {
                var ex = await Record.ExceptionAsync(() => (Task)list.Invoke(engine, args));
                Assert.NotNull(ex);   // the stand-in fails the sign-in; nothing is listed
            }

            // A new sign-in started (the capture that pins no record), not a token request for the saved account.
            Assert.Contains(_browser, w => w.StartsWith("a first sign-in", StringComparison.Ordinal));
            Assert.DoesNotContain(_browser, w => w.StartsWith("a token for", StringComparison.Ordinal));
            // And the saved account is still the default: asking for another account moves nothing by itself.
            Assert.Equal("alex@contoso.com", EntraToken.LoadPinnedRecordForTests("interactive", null, forceReauth: false)?.Username);
        }

        private const string OtherHome = "00000000-0000-0000-0000-0000000005a3.00000000-0000-0000-0000-0000000f0b1c";

        // si2: a second account the person already signed in from the picker, saved beside alex without moving the default.
        private static string SeedOtherAccount() =>
            EntraToken.SeedProfileForTests("interactive", null, OtherHome, "sam@fabrikam.com",
                "{\"username\":\"sam@fabrikam.com\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"" + OtherHome +
                "\",\"tenantId\":\"00000000-0000-0000-0000-0000000f0b1c\",\"clientId\":\"" + UnregisteredClientId + "\",\"version\":\"1.0\"}", makeDefault: false);

        [Fact]
        public async Task The_models_of_a_workspace_are_read_as_the_chosen_saved_account_with_no_new_sign_in()
        {
            SeedSavedAccount();
            var sam = SeedOtherAccount();
            Assert.NotNull(sam);
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            Exception ex;
            using (SignInPress.Press())
            {
                ex = await Record.ExceptionAsync(() => engine.ListWorkspaceModelsAsync("ws", "interactive", null, "human", cts.Token, accountProfileId: sam));
                Assert.NotNull(ex);   // no cached token for the stand-in account; the stand-in fails the token request
            }
            // The saved profile is pinned and asked for a token: no new sign-in, and the default is still alex.
            Assert.True(_browser.Any(w => w.StartsWith("a token for", StringComparison.Ordinal)), ex.GetType().Name + ": " + ex.Message + " | " + ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message);
            Assert.DoesNotContain(_browser, w => w.StartsWith("a first sign-in", StringComparison.Ordinal));
            Assert.Equal("alex@contoso.com", EntraToken.LoadPinnedRecordForTests("interactive", null, forceReauth: false)?.Username);
        }

        [Fact]
        public async Task An_unknown_saved_account_says_so_in_plain_words()
        {
            SeedSavedAccount();
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ListWorkspacesAsync("interactive", null, "human", accountProfileId: "0000000000000000"));
            Assert.Equal("That saved account is signed out on this device. Sign in again to use it.", ex.Message);
            Assert.Empty(_browser);
        }

        [Theory]
        [InlineData(true, null)]
        [InlineData(false, "0000000000000000")]
        public async Task An_assistant_never_chooses_the_account_a_workspace_list_uses(bool anotherAccount, string profileId)
        {
            SeedSavedAccount();
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            using (SignInPress.Press())
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ListWorkspacesAsync("interactive", null, "agent", anotherAccount: anotherAccount, accountProfileId: profileId));
                Assert.Equal(LocalEngine.AnotherAccountForAssistant, ex.Message);
            }
            Assert.Empty(_browser);
        }

        [Fact]
        public async Task Another_account_is_a_browser_sign_in()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ListWorkspacesAsync("azcli", null, "human", anotherAccount: true));
            Assert.Equal(LocalEngine.AnotherAccountNeedsBrowser, ex.Message);
            Assert.Empty(_browser);
        }
    }
}
