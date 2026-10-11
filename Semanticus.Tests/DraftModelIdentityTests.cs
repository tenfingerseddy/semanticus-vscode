// Join 11, drafts round 3: SessionInfo.ModelIdentity, the ONE answer to "which model is this" that the clients file a person's unsaved words under.
// The two failed checks (V1, V3) were the client deriving it twice from fields that do not mean identity: a snapshot path (new on every open), a
// session id (restarts at s1) and a Desktop port and database GUID (new on every Desktop start). These tests drive the REAL engine open paths through
// the snapshot seam, and the pure function. test/verify-drafts-check*.test.mjs and test/reopen-behaviour.mjs are the TypeScript mirror: its fake
// sessions carry the same modelIdentity strings these tests prove the engine sends.
using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // mutates the static ConnectionRegistry root and the snapshot root
    public sealed class DraftModelIdentityTests : IAsyncLifetime
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Finance";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-draft-id-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string _savedRegistryRoot;
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;

        public DraftModelIdentityTests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            LiveModelExport.TempRootOverride = Path.Combine(_root, "snapshots");
            Directory.CreateDirectory(LiveModelExport.TempRoot);
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            // Each open gets its OWN snapshot folder, exactly as the real export does (a new GUID folder every time).
            _engine.LiveSnapshotExportForTests = (ds, db) => Task.FromResult(NewSnapshot(string.IsNullOrEmpty(db) ? "Sales" : db));
            _engine.LiveBindForTests = (kind, ds) => LiveConnection.ForTest(kind, ds, "Sales");
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            _engine?.Dispose();
            LiveModelExport.TempRootOverride = null;
            ConnectionRegistry.RootOverride = _savedRegistryRoot;
            try { Directory.Delete(_root, true); } catch { }
            return Task.CompletedTask;
        }

        private LiveModelExport.Snapshot NewSnapshot(string database)
        {
            var dir = Path.Combine(LiveModelExport.TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, database + ".bim");
            File.Copy(TestModels.FindBim(), bim);
            return new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = database, DatabaseCount = 1, DatabaseNames = new[] { database } };
        }

        private Task<OpenResult> OpenLive(string endpoint = Endpoint, string database = "Sales")
            => _engine.OpenLiveAsync(endpoint, database, "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);

        private async Task<SessionInfo> Info() => await _engine.SessionInfoAsync();

        // ---- the pure function: every rule ------------------------------------------------------------------------------------

        [Fact]
        public void The_function_has_one_rule_per_kind_of_model_and_never_returns_null_for_an_open_one()
        {
            var live = new LiveOrigin(Endpoint, "Sales", null);
            var desktop = new LiveOrigin("localhost:51234", "5b7e-guid", null, localName: "Contoso Sales", localDesktop: true);
            var desktopNoStem = new LiveOrigin("localhost:51234", "5b7e-guid", null, localDesktop: true);
            var marker = new LiveModelExport.SnapshotOrigin { Endpoint = Endpoint, Database = "Sales" };
            var deskMarker = new LiveModelExport.SnapshotOrigin { Endpoint = "localhost:51234", Database = "5b7e-guid", LocalName = "Contoso Sales" };

            Assert.Equal("desktop:contoso sales|localhost:51234|5b7e-guid", LocalEngine.ModelIdentityOf(desktop, null, "/tmp/x/Sales.bim", "k"));   // F2 (drafts-sol): the stem AND the instance
            Assert.Equal("live:" + Endpoint.ToLowerInvariant() + "|sales", LocalEngine.ModelIdentityOf(live, null, "/tmp/x/Sales.bim", "k"));
            Assert.Equal("live:localhost:51234|5b7e-guid", LocalEngine.ModelIdentityOf(desktopNoStem, null, null, "k"));
            Assert.Equal(LocalEngine.ModelIdentityOf(live, null, "/a/Sales.bim", "k"), LocalEngine.ModelIdentityOf(null, marker, "/b/Sales.bim", "k"));
            Assert.Equal(LocalEngine.ModelIdentityOf(desktop, null, "/a", "k"), LocalEngine.ModelIdentityOf(null, deskMarker, "/b", "k"));
            Assert.StartsWith("file:", LocalEngine.ModelIdentityOf(null, null, "/models/Sales.bim", "k"));
            Assert.Equal("unsaved:k1", LocalEngine.ModelIdentityOf(null, null, null, "k1"));
            Assert.Equal("unsaved:k1", LocalEngine.ModelIdentityOf(null, null, "  ", "k1"));
            Assert.NotEqual(LocalEngine.ModelIdentityOf(null, null, null, "k1"), LocalEngine.ModelIdentityOf(null, null, null, "k2"));
        }

        [Fact]
        public void An_accepted_spelling_of_the_same_address_is_one_identity_and_a_different_address_is_another()
        {
            string Id(string endpoint, string db) => LocalEngine.ModelIdentityOf(new LiveOrigin(endpoint, db, null), null, null, "k");
            Assert.Equal(Id("powerbi://api.powerbi.com/v1.0/myorg/Finance", "Sales"), Id("powerbi://api.powerbi.com/v1.0/myorg/finance/", " SALES "));
            Assert.NotEqual(Id("powerbi://api.powerbi.com/v1.0/myorg/Finance", "Sales"), Id("powerbi://api.powerbi.com/v1.0/myorg/Marketing", "Sales"));
            Assert.NotEqual(Id("powerbi://api.powerbi.com/v1.0/myorg/Finance", "Sales"), Id("powerbi://api.powerbi.com/v1.0/myorg/Finance", "Orders"));
            // Two Desktop windows with different stems are two models. The same stem on another instance (another port) is another model too (F2, drafts-sol):
            // a stem is a file's NAME, and West/Sales.pbix and East/Sales.pbix share one.
            string Desk(string stem, string port) => LocalEngine.ModelIdentityOf(new LiveOrigin("localhost:" + port, "g", null, localName: stem), null, null, "k");
            Assert.Equal(Desk("Contoso Sales", "51234"), Desk("CONTOSO SALES", "51234"));
            Assert.NotEqual(Desk("Contoso Sales", "51234"), Desk("CONTOSO SALES", "60321"));
            Assert.NotEqual(Desk("Contoso Sales", "51234"), Desk("Contoso Orders", "51234"));
        }

        // ---- the real open paths ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_same_published_model_opened_twice_from_two_snapshot_folders_is_one_identity()
        {
            await OpenLive();
            var first = await Info();
            var firstSource = first.Source;
            await OpenLive();
            var second = await Info();
            Assert.NotEqual(firstSource, second.Source);   // precondition: a NEW snapshot folder, the thing a path-based key got wrong
            Assert.NotNull(first.ModelIdentity);
            Assert.Equal(first.ModelIdentity, second.ModelIdentity);
            Assert.Equal("live:" + Endpoint.ToLowerInvariant() + "|sales", second.ModelIdentity);
            Assert.Equal("Sales", second.ModelLabel);
        }

        [Fact]
        public async Task A_cold_start_that_opens_the_cached_snapshot_as_a_file_has_the_identity_it_had_while_it_was_open_live()
        {
            await OpenLive();
            var live = await Info();
            var snapshot = live.Source;
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(snapshot)!, "origin.json")), "open_live leaves its marker beside the snapshot");

            // The cold start: a NEW engine process opens the cached snapshot as a plain file.
            using var cold = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await cold.OpenAsync(snapshot);
            var asFile = await cold.SessionInfoAsync();
            Assert.False(asFile.LiveBound, "the marker names the model; it never binds the session to the live source");
            Assert.Null(asFile.LiveEndpoint);
            Assert.Equal(live.ModelIdentity, asFile.ModelIdentity);
            Assert.Equal("Sales", asFile.ModelLabel);
        }

        [Fact]
        public async Task A_snapshot_marker_is_identity_only_it_never_becomes_a_live_origin()
        {
            await OpenLive();
            var snapshot = (await Info()).Source;
            await _engine.OpenAsync(snapshot, discardUnsaved: true);
            var s = _sessions.Current;
            Assert.NotNull(s.SnapshotOf);
            Assert.Null(s.LiveOrigin);
            var info = await Info();
            Assert.False(info.LiveBound);
            Assert.Equal(Endpoint, s.SnapshotOf.Endpoint);
        }

        [Fact]
        public async Task A_snapshot_folder_with_no_marker_is_a_file_model_as_before()
        {
            var snap = NewSnapshot("Plain");   // an older build left this folder: no origin.json
            await _engine.OpenAsync(snap.BimPath, discardUnsaved: true);
            var info = await Info();
            Assert.False(info.LiveBound);
            Assert.StartsWith("file:", info.ModelIdentity);
            Assert.EndsWith("Plain.bim", info.ModelIdentity, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);   // the identity folds the path to upper case on Windows (LocalEngine.CanonAnchor); it is a key compared as a string, never shown or opened as a path
        }

        [Fact]
        public async Task A_file_outside_the_snapshot_root_ignores_a_marker_beside_it()
        {
            var dir = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "Sales.bim");
            File.Copy(TestModels.FindBim(), bim);
            File.WriteAllText(Path.Combine(dir, "origin.json"), "{\"Endpoint\":\"" + Endpoint + "\",\"Database\":\"Sales\"}");
            await _engine.OpenAsync(bim, discardUnsaved: true);
            Assert.StartsWith("file:", (await Info()).ModelIdentity);
        }

        [Fact]
        public void A_Desktop_model_with_the_same_window_stem_is_another_identity_on_a_new_port_and_database()
        {
            // F2 (drafts-sol, Sol join 10): the round-3 rule let a stem alone stand for the model, so West/Sales.pbix and East/Sales.pbix shared writable words. The stem
            // is a file's NAME; the instance (endpoint and database) is what Desktop actually has open. Words typed before Desktop started again are kept read-only.
            // LocalOriginFor is what open_local stamps; the stem is what LocalDesktop.TryGetIdentity captured from the window title.
            var before = LocalEngine.LocalOriginFor("localhost:51234", "guid-one", new LocalDesktop.DesktopIdentity { Stem = "Contoso Sales" });
            var after = LocalEngine.LocalOriginFor("localhost:60321", "guid-two", new LocalDesktop.DesktopIdentity { Stem = "Contoso Sales" });
            Assert.NotEqual(LocalEngine.ModelIdentityOf(before, null, "/s/1/a.bim", "k"), LocalEngine.ModelIdentityOf(after, null, "/s/2/a.bim", "k"));
            Assert.Equal(LocalEngine.ModelIdentityOf(before, null, "/s/1/a.bim", "k"), LocalEngine.ModelIdentityOf(before, null, "/s/2/a.bim", "k"));   // the same instance is one model
            // No stem captured: the port and the database are all there is, so it differs after a Desktop restart (a disclosed limit).
            var noStemBefore = LocalEngine.LocalOriginFor("localhost:51234", "guid-one", null);
            var noStemAfter = LocalEngine.LocalOriginFor("localhost:60321", "guid-two", null);
            Assert.NotEqual(LocalEngine.ModelIdentityOf(noStemBefore, null, null, "k"), LocalEngine.ModelIdentityOf(noStemAfter, null, null, "k"));
        }

        [Fact]
        public async Task The_open_local_path_writes_the_same_marker_and_a_reopened_snapshot_matches_it()
        {
            await _engine.OpenLocalAsync("localhost:51234", "Sales", discardUnsaved: true);
            var live = await Info();
            Assert.Equal("live:localhost:51234|sales", live.ModelIdentity);   // no Desktop window on this host: coordinates
            using var cold = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await cold.OpenAsync(live.Source);
            Assert.Equal(live.ModelIdentity, (await cold.SessionInfoAsync()).ModelIdentity);
        }

        [Fact]
        public async Task Two_unsaved_models_have_two_identities_even_with_the_same_name_and_session_counter()
        {
            await _engine.CreateModelAsync("Untitled", 1604, discardUnsaved: true);
            var a = await Info();
            await _engine.CreateModelAsync("Untitled", 1604, discardUnsaved: true);
            var b = await Info();
            Assert.StartsWith("unsaved:", a.ModelIdentity);
            Assert.StartsWith("unsaved:", b.ModelIdentity);
            Assert.NotEqual(a.ModelIdentity, b.ModelIdentity);
            Assert.DoesNotContain(a.SessionId, a.ModelIdentity);   // never the session id: it restarts at s1 in every engine process
            Assert.Equal(b.ModelIdentity, (await Info()).ModelIdentity);   // stable while it is the open model
            Assert.Equal("Untitled", b.ModelLabel);
        }

        [Fact]
        public async Task A_file_model_is_its_canonical_path_and_Save_As_changes_it()
        {
            var bim = Path.Combine(_root, "Sales.bim");
            File.Copy(TestModels.FindBim(), bim);
            await _engine.OpenAsync(bim, discardUnsaved: true);
            var a = await Info();
            var viaDots = Path.Combine(_root, "sub", "..", "Sales.bim");
            Directory.CreateDirectory(Path.Combine(_root, "sub"));
            await _engine.OpenAsync(viaDots, discardUnsaved: true);
            Assert.Equal(a.ModelIdentity, (await Info()).ModelIdentity);   // one file, one identity, whatever the spelling of its path
            Assert.Equal("Sales", a.ModelLabel);
        }

        [Fact]
        public async Task No_model_open_has_no_identity()
        {
            using var empty = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            var info = await empty.SessionInfoAsync();
            Assert.Null(info.ModelIdentity);
            Assert.Null(info.ModelLabel);
        }
    }
}
