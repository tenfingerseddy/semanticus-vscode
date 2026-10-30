// UAT 1.7 Free (D3, lost work): Claude saved a live model's new measures to its file in the connected folder, the person opened the same
// published model again from Connections, and got the service copy with no word that a saved copy existed; the next Ctrl+S wrote over
// the saved file. The open now offers the saved copy, and a save never writes over a saved copy that holds more than the open model.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class SavedCopyOfferTests : IAsyncLifetime
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Finance";
        private readonly string _savedRegistryRoot;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-sco-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private string _workspace = null!;

        public SavedCopyOfferTests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            _workspace = Path.Combine(_root, "connect", "Model");
            Directory.CreateDirectory(_workspace);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            LiveModelExport.TempRootOverride = Path.Combine(_root, "snapshots");
            Directory.CreateDirectory(LiveModelExport.TempRoot);
            LocalEngine.LastingHomeForTests = Path.Combine(_root, "home");
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            LiveModelExport.TempRootOverride = null;
            LocalEngine.LastingHomeForTests = null;
            ConnectionRegistry.RootOverride = _savedRegistryRoot;
            try { Directory.Delete(_root, true); } catch { }
            return Task.CompletedTask;
        }

        private LocalEngine NewLiveEngine()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _workspace, true);
            engine.LiveSnapshotExportForTests = (ds, db) =>
            {
                var dir = Path.Combine(LiveModelExport.TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                var bim = Path.Combine(dir, "Northstar Sales Working.bim");
                File.Copy(TestModels.FindBim(), bim);
                return Task.FromResult(new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = "Northstar Sales Working", DatabaseCount = 1, DatabaseNames = new[] { "Northstar Sales Working" } });
            };
            engine.LiveBindForTests = (kind, ds) => LiveConnection.ForTest(kind, ds, "Northstar Sales Working");
            return engine;
        }

        // The exact steps of the UAT: Claude adds three measures and saves the live model; later the person opens the published model again.
        private async Task<string> ClaudeSavesThreeMeasuresAsync()
        {
            using var claude = NewLiveEngine();
            await claude.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            foreach (var n in new[] { "Saved Probe A", "Saved Probe B", "Saved Probe C" })
                await claude.CreateMeasureAsync("table:Product", n, "1", "agent");
            return (await claude.SaveAsync(null, "bim", false, "agent")).Path;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Opening_the_published_model_again_offers_the_saved_copy_that_holds_the_measures()
        {
            var saved = await ClaudeSavesThreeMeasuresAsync();

            using var person = NewLiveEngine();
            var open = await person.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);

            Assert.NotNull(open.SavedCopy);
            Assert.Equal(saved, open.SavedCopy!.Path);
            Assert.Equal(3, open.SavedCopy.Extra);
            Assert.Contains("Saved Probe A", open.SavedCopy.Names);
            Assert.Contains("saved copy", open.Note);   // the agent door reads the same sentence
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_save_after_that_reopen_never_writes_over_the_saved_copy()
        {
            var saved = await ClaudeSavesThreeMeasuresAsync();
            using var person = NewLiveEngine();
            await person.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
            await person.CreateMeasureAsync("table:Product", "Person Probe", "2", "human");

            var r = await person.SaveAsync(null, "bim", false, "human");   // Ctrl+S

            Assert.NotEqual(saved, r.Path);
            var kept = File.ReadAllText(saved);
            Assert.Contains("Saved Probe A", kept);
            Assert.Contains("Saved Probe C", kept);
            Assert.Contains("Person Probe", File.ReadAllText(r.Path));
            Assert.Contains(saved, r.Note);   // the save says where the saved copy is
            Assert.False((await person.SessionInfoAsync()).HasUnsavedChanges);
        }

        // The happy path: take the offer, the measures are there, and Ctrl+S writes that same file with nothing lost.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Taking_the_offer_opens_the_saved_copy_and_saves_back_to_it()
        {
            var saved = await ClaudeSavesThreeMeasuresAsync();
            using var person = NewLiveEngine();
            var open = await person.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);

            var again = await person.OpenAsync(open.SavedCopy!.Path, discardUnsaved: true);
            await person.CreateMeasureAsync("table:Product", "After Offer Probe", "3", "human");
            var r = await person.SaveAsync(null, "bim", false, "human");

            Assert.Equal(saved, r.Path);
            var text = File.ReadAllText(saved);
            Assert.Contains("Saved Probe B", text);
            Assert.Contains("After Offer Probe", text);
            Assert.Equal(1, Directory.GetFiles(_workspace, "*.bim").Length);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_saved_copy_with_nothing_the_open_model_lacks_makes_no_offer_and_is_saved_in_place()
        {
            string first;
            using (var e = NewLiveEngine())
            {
                await e.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
                first = (await e.SaveAsync(null, "bim", false, "human")).Path;
            }

            using var again = NewLiveEngine();
            var open = await again.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
            await again.CreateMeasureAsync("table:Product", "Plain Probe", "1", "human");
            var r = await again.SaveAsync(null, "bim", false, "human");

            Assert.Null(open.SavedCopy);
            Assert.Equal(first, r.Path);
        }
    }
}
