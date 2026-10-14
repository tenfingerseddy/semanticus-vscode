using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero 22 (H1): seconds after an assistant created a new model, the person's Studio engine opened a real sign-in window though the saved
    /// destination used a service principal. The rule these tests hold: NOTHING automatic opens a window. A window may begin only while a person's
    /// press is in force (Connect, Open, a Publish that writes), so every read the page starts by itself uses the saved sign-in and, when that fails,
    /// says to press Connect. InteractiveSignIn.BrowserForTests stands in for the browser: a call that reaches it would have opened a window.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SignInPressTests : IDisposable
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;
        private readonly List<string> _windows = new();

        public SignInPressTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-press-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
            _prevPersistDir = EntraToken.PersistDirOverride;
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");      // an empty sign-in store: nothing is saved for the interactive modes
            EntraToken.TokenCacheNameOverride = "semanticus-press-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            EntraToken.FabricTokenForTests = null;
            InteractiveSignIn.BrowserForTests = what => { lock (_windows) _windows.Add(what); };
        }

        public void Dispose()
        {
            InteractiveSignIn.BrowserForTests = null;
            InteractiveSignIn.AllowedForTests(false);
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static CancellationToken Short() => new CancellationTokenSource(TimeSpan.FromSeconds(25)).Token;

        // The Studio page lists workspaces when it opens, with the person's origin. Nothing was pressed.
        [Fact]
        public async Task A_read_the_page_starts_by_itself_opens_no_window_when_the_sign_in_is_not_saved()
        {
            using var local = new LocalEngine(new SessionManager(), new Pro());
            var person = PersonFence.For(local);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => person.ListWorkspacesAsync("interactive", null, "human", Short()));
            Assert.Equal(0, _windows.Count);
            Assert.Contains("Press Sign in", ex.Message);
        }

        // The plant: a person's press with no saved sign-in may open one window (and only one).
        [Fact]
        public async Task A_persons_press_with_no_saved_sign_in_may_open_one_window()
        {
            using var local = new LocalEngine(new SessionManager(), new Pro());
            var person = PersonFence.For(local);
            await Assert.ThrowsAnyAsync<Exception>(() => person.ConnectXmlaAsync(Endpoint, "Created", "interactive", null));
            Assert.Single(_windows);
        }

        // Hero 22 itself: the assistant created the model and linked it as the destination (a saved service principal). The page then reads that destination
        // with the person's origin. It uses the saved sign-in, and no window is reachable on that path or on a destination nobody saved.
        [Fact]
        public async Task After_an_assistants_new_model_the_pages_own_compare_builds_no_window_capable_sign_in()
        {
            var repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
            var model = Path.Combine(repo, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var local = new LocalEngine(new SessionManager(), new Pro());
            await local.OpenAsync(model);
            var made = ConnectionRegistry.Remember("xmla", Endpoint, "Created", "Created", "tenant-1", "serviceprincipal");
            await local.SetPublishDestinationAsync(made.Id, "agent");   // what publish_new_model does at its end
            var seen = new List<bool>();
            local.SnapshotDisableInteractiveForTests = d => seen.Add(d);
            var person = PersonFence.For(local);
            var session = new ModelRef { Kind = "session" };

            // The saved destination: the first (and only) try cannot open a window.
            await Assert.ThrowsAnyAsync<Exception>(() => person.CompareModelsAsync(session, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "Created" }, false, "human"));
            Assert.All(seen, d => Assert.True(d));
            Assert.Empty(_windows);

            // A destination the page names with a browser sign-in nobody saved: the read is automatic, so it still opens nothing.
            await Assert.ThrowsAnyAsync<Exception>(() => person.CompareModelsAsync(session, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "Other", AuthMode = "interactive" }, false, "human"));
            Assert.Empty(_windows);
        }

        // A credential a press built and cached is silent for the next automatic read: the check runs at every use, not once at build.
        [Fact]
        public async Task A_credential_built_for_a_press_is_silent_for_a_later_automatic_read()
        {
            InteractiveSignIn.BrowserForTests = null;
            InteractiveSignIn.AllowedForTests(true);   // stands in for the engine the VS Code window starts
            TokenCredentialProbe cred;
            using (SignInPress.Press()) cred = new TokenCredentialProbe(EntraToken.BuildCredential("interactive", null));
            Assert.False(SignInPress.Pressed);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EntraToken.GetTokenAsync(cred.Credential, Short()));
            Assert.Equal(SignInPress.NotPressedText, ex.Message);
        }

        private sealed class TokenCredentialProbe
        {
            public Azure.Core.TokenCredential Credential { get; }
            public TokenCredentialProbe(Azure.Core.TokenCredential c) { Credential = c; }
        }

        // What counts as a press is a short, named list: a new call is automatic until it is named.
        [Fact]
        public void Only_the_sign_in_buttons_and_the_calls_that_write_are_presses()
        {
            var deploy = typeof(IEngine).GetMethod("DeployLiveAsync");
            var withCommit = new object[deploy.GetParameters().Length];
            withCommit[Array.FindIndex(deploy.GetParameters(), p => p.Name == "commit")] = true;
            Assert.True(SignInPress.IsPress(deploy, withCommit));                          // Publish that writes
            Assert.False(SignInPress.IsPress(deploy, new object[deploy.GetParameters().Length].Select((o, i) => deploy.GetParameters()[i].ParameterType == typeof(bool) ? (object)false : null).ToArray()));   // its preview
            Assert.True(SignInPress.IsPress(typeof(IEngine).GetMethod("ConnectXmlaAsync"), new object[0]));
            Assert.True(SignInPress.IsPress(typeof(IEngine).GetMethod("OpenLiveAsync"), new object[0]));
            Assert.False(SignInPress.IsPress(typeof(IEngine).GetMethod("ListWorkspacesAsync"), new object[] { "interactive", null, "human", default(CancellationToken) }));
            Assert.False(SignInPress.IsPress(typeof(IEngine).GetMethod("CompareModelsAsync"), new object[] { null, null, false, "human" }));
        }
    }
}
