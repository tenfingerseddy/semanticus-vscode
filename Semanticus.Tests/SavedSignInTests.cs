using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero 21 (N1, N2): a live target the app holds a sign-in for (the model it was opened from, or the destination it publishes to, such as
    /// a model the product just created) uses that sign-in on every path. An assistant compare or merge never falls to az ("Not signed in"
    /// while a saved sign-in works), and a cold saved sign-in is never answered with a browser window: the person presses Connect.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SavedSignInTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private const string ColdAuth = "Authentication failed for all authenticators. Technical Details: RootActivityId: 00000000-0000-0000-0000-000000000000";

        public SavedSignInTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-n1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private async Task<LocalEngine> EngineLinkedToAsync(string mode, string tenant)
        {
            var repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
            var model = Path.Combine(repo, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.OpenAsync(model);
            var made = ConnectionRegistry.Remember("xmla", Endpoint, "Created", "Created", tenant, mode);
            await engine.SetPublishDestinationAsync(made.Id, "human");
            return engine;
        }

        [Fact]
        public async Task A_destination_with_a_saved_sign_in_gives_that_mode_and_tenant_when_the_caller_names_none()
        {
            using var engine = await EngineLinkedToAsync("serviceprincipal", "tenant-1");
            var (mode, tenant, saved) = await engine.SavedSignInForAsync(Endpoint, "Created", null, null);
            Assert.Equal(("serviceprincipal", "tenant-1", true), (mode, tenant, saved));
            // Another database on the same workspace is not that destination: az as before.
            var other = await engine.SavedSignInForAsync(Endpoint, "Other", null, null);
            Assert.Equal(("azcli", (string)null, false), (other.Mode, other.Tenant, other.Saved));
            // A mode the caller names is theirs.
            var named = await engine.SavedSignInForAsync(Endpoint, "Created", "azcli", null);
            Assert.Equal("azcli", named.Mode);
        }

        [Fact]
        public async Task An_assistant_compare_with_a_saved_destination_uses_the_saved_sign_in_not_az()
        {
            using var engine = await EngineLinkedToAsync("serviceprincipal", "tenant-1");
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode =>
            {
                modes.Add(mode);
                var dir = Path.Combine(_root, "snap-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                var p = Path.Combine(dir, "model.bim");
                var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
                File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
                return Task.FromResult(new LiveModelExport.Snapshot { BimPath = p, DatabaseName = "Created", DatabaseCount = 1, DatabaseNames = new[] { "Created" } });
            };
            var diff = await engine.CompareModelsAsync(new ModelRef { Kind = "session" }, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "Created" }, origin: "agent");
            Assert.True(string.IsNullOrEmpty(diff.Error), diff.Error);
            Assert.Equal(new[] { "serviceprincipal" }, modes);
        }

        [Fact]
        public async Task A_saved_destination_that_is_cold_is_never_answered_with_a_browser_window()
        {
            using var engine = await EngineLinkedToAsync("interactive", "tenant-1");
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode => { modes.Add(mode); throw new InvalidOperationException(ColdAuth); };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.CompareModelsAsync(new ModelRef { Kind = "session" }, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "Created" }, origin: "human"));
            Assert.Equal(new[] { "interactive" }, modes);        // one try with the saved sign-in, no second interactive try
            Assert.Contains("Press Sign in", ex.Message);
            Assert.DoesNotContain("RootActivityId", ex.Message);
        }

        // The window guard (hero21 N2): the FIRST attempt for a saved destination can never open a window, whoever asks. An unsaved one may for a person.
        [Fact]
        public async Task The_first_attempt_for_a_saved_destination_is_not_prompt_capable_for_a_person()
        {
            using var engine = await EngineLinkedToAsync("interactive", "tenant-1");
            var seen = new List<(string Db, bool NoWindow)>();
            var db = "";
            engine.SnapshotDisableInteractiveForTests = d => seen.Add((db, d));
            engine.WorkspaceTokenExportForTests = mode => throw new InvalidOperationException(ColdAuth);
            db = "Created";
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CompareModelsAsync(new ModelRef { Kind = "session" }, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "Created" }, origin: "human"));
            Assert.Equal(new[] { ("Created", true) }, seen);        // the saved destination: one try, no window
            seen.Clear(); db = "Other";
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CompareModelsAsync(new ModelRef { Kind = "session" }, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "Other", AuthMode = "azcli" }, origin: "human"));
            Assert.False(seen[0].NoWindow);                          // a destination nobody saved: a person's first try may still open one
        }

        // The person's own push on a saved destination uses the saved sign-in first, and a window opens only when that failed.
        [Fact]
        public async Task A_push_on_a_saved_destination_tries_the_saved_sign_in_first_and_opens_a_window_only_for_a_person_after_it_fails()
        {
            var calls = new List<bool>();
            Task<int> Cold(bool noWindow) { calls.Add(noWindow); return noWindow ? Task.FromException<int>(new Azure.Identity.AuthenticationRequiredException("cold", default(Azure.Core.TokenRequestContext))) : Task.FromResult(1); }
            Assert.Equal(1, await LocalEngine.AcquireSavedFirstAsync(Cold, disableInteractive: false, savedDestination: true));
            Assert.Equal(new[] { true, false }, calls);              // saved sign-in first, then one window the person's press allows
            calls.Clear();
            await Assert.ThrowsAsync<Azure.Identity.AuthenticationRequiredException>(() => LocalEngine.AcquireSavedFirstAsync(Cold, disableInteractive: true, savedDestination: true));
            Assert.Equal(new[] { true }, calls);                     // an assistant never gets a window
            calls.Clear();
            await LocalEngine.AcquireSavedFirstAsync(Cold, disableInteractive: false, savedDestination: false);
            Assert.Equal(new[] { false }, calls);                    // unsaved: as before
        }
    }
}
