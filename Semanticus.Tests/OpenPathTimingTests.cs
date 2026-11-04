// cx1 (Kane, 3 Nov, 1.7.3): "I tried to open a previous model ... it keeps freezing up or wont do it ... It did finally connect but took a
// very long time". His open took about two minutes and the output log did not say where. Timed on our own models, the open waited on two
// disk flushes after the model was already loaded: the saved-connections write and the history write. On a busy disk each flush took
// 0.04 to 6 s (btrfs on the Inspiron), so a 1 s model load became an 8 s open. These tests hold the open off those flushes, and hold the
// step timings in the log and in the result, so the next slow open names its slow step.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class OpenPathTimingTests : IAsyncLifetime
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Finance";
        private readonly string _savedRegistryRoot;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-opt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private string _registry = null!;
        private string _workspace = null!;

        public OpenPathTimingTests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            _workspace = Path.Combine(_root, "connect", "Model");
            Directory.CreateDirectory(_workspace);
            _registry = Path.Combine(_root, "registry");
            ConnectionRegistry.RootOverride = _registry;
            LiveModelExport.TempRootOverride = Path.Combine(_root, "snapshots");
            Directory.CreateDirectory(LiveModelExport.TempRoot);
            LocalEngine.LastingHomeForTests = Path.Combine(_root, "home");
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            ConnectionRegistry.FlushHookForTests = null;
            OpenTimer.LogForTests = null;
            LiveModelExport.TempRootOverride = null;
            LocalEngine.LastingHomeForTests = null;
            ConnectionRegistry.RootOverride = _savedRegistryRoot;
            try { Directory.Delete(_root, true); } catch { }
            return Task.CompletedTask;
        }

        private string FixtureCopy(bool snapshot = false)
        {
            var dir = Path.Combine(snapshot ? LiveModelExport.TempRoot : Path.Combine(_root, "models"), Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "Northstar Sales.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        private LocalEngine NewLiveEngine(Func<string, string, LiveConnection>? bind = null)
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _workspace, true);
            engine.LiveSnapshotExportForTests = (ds, db) =>
            {
                var bim = FixtureCopy(snapshot: true);
                return Task.FromResult(new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = "Northstar Sales", DatabaseCount = 1, DatabaseNames = new[] { "Northstar Sales" } });
            };
            engine.LiveBindForTests = bind ?? ((kind, ds) => LiveConnection.ForTest(kind, ds, "Northstar Sales"));
            return engine;
        }

        private bool IsMine(string path, string file) =>
            path.StartsWith(_registry, StringComparison.Ordinal) && Path.GetFileName(path).StartsWith(file, StringComparison.Ordinal);

        // Cause 1. The history is a convenience log ("losing one timeline entry is harmless"); an open must not wait for its disk flush.
        // Before the fix the open held until the history file reached the disk, so a slow flush was a slow (or frozen-looking) open.
        [Fact]
        public async Task An_open_does_not_wait_for_the_history_write_to_reach_the_disk()
        {
            using var release = new ManualResetEventSlim(false);
            ConnectionRegistry.FlushHookForTests = path => { if (IsMine(path, "connection-history.json")) release.Wait(TimeSpan.FromSeconds(30)); };
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _workspace, true);
            var bim = FixtureCopy();

            var open = engine.OpenAsync(bim, discardUnsaved: true);
            var finished = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(15))) == open;
            release.Set();
            await open;

            Assert.True(finished, "The open waited for the history file to reach the disk.");
            // The history still has the open, written in place before the open answered.
            var history = ConnectionHistory.List();
            Assert.Contains(history, e => e.Kind == "open" && string.Equals(e.Endpoint, Path.GetFullPath(bim), StringComparison.Ordinal));
        }

        // Cause 2. A live open writes the saved-connections list (labels are governance data, so that write still reaches the disk). It
        // ran after the model loaded and before the query connection, one after the other. It now runs while the query connection opens.
        [Fact]
        public async Task A_live_open_saves_the_connection_while_the_query_connection_opens()
        {
            using var bindStarted = new ManualResetEventSlim(false);
            var overlapped = false;
            ConnectionRegistry.FlushHookForTests = path => { if (IsMine(path, "connections.json")) overlapped |= bindStarted.Wait(TimeSpan.FromSeconds(10)); };
            using var engine = NewLiveEngine((kind, ds) => { bindStarted.Set(); return LiveConnection.ForTest(kind, ds, "Northstar Sales"); });

            var open = await engine.OpenLiveAsync(Endpoint, "Northstar Sales", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);

            Assert.True(overlapped, "The connection save ran before the query connection started, one after the other.");
            Assert.True(open.LiveConnected);
            // The row is on disk when the open answers: the next list read (the Connections page) shows it.
            Assert.Contains(ConnectionRegistry.List(), r => r.Kind == "xmla" && r.Database == "Northstar Sales");
        }

        // Step timings in the output log: one line per step and one total line naming the slowest steps.
        [Fact]
        public async Task A_live_open_writes_each_step_and_its_time_to_the_log()
        {
            var lines = new List<string>();
            OpenTimer.LogForTests = line => { lock (lines) lines.Add(line); };
            using var engine = NewLiveEngine();

            var open = await engine.OpenLiveAsync(Endpoint, "Northstar Sales", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);

            string[] got; lock (lines) got = lines.ToArray();
            foreach (var step in new[] { "sign-in token", "read the published model", "load the model", "save connection", "connect for queries" })
                Assert.Contains(got, l => l.StartsWith("[open] step=" + step, StringComparison.Ordinal) && l.Contains(" ms=", StringComparison.Ordinal));
            Assert.Contains(got, l => l.StartsWith("[open] done published model total ms=", StringComparison.Ordinal) && l.Contains("slowest:", StringComparison.Ordinal));
            // The result carries the same steps, so the extension can log them even when another process owns the engine.
            Assert.NotNull(open.Steps);
            Assert.Contains(open.Steps!, s => s.Step == "load the model");
            Assert.Contains(open.Steps!, s => s.Step == "save connection and history" && s.Alongside);
            Assert.True(open.TotalMs >= open.Steps!.Where(s => !s.Alongside).Sum(s => s.Ms) - 5);
        }

        [Fact]
        public async Task A_file_open_writes_its_steps_to_the_log_and_the_result()
        {
            var lines = new List<string>();
            OpenTimer.LogForTests = line => { lock (lines) lines.Add(line); };
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _workspace, true);

            var open = await engine.OpenAsync(FixtureCopy(), discardUnsaved: true);

            string[] got; lock (lines) got = lines.ToArray();
            Assert.Contains(got, l => l.StartsWith("[open] step=load the model ms=", StringComparison.Ordinal));
            Assert.Contains(got, l => l.StartsWith("[open] done file total ms=", StringComparison.Ordinal));
            Assert.Contains(open.Steps!, s => s.Step == "save connection and history");
        }

        // cx3, tester note 4 (1.7.4-rc1): the start-up open saved the local model as ".../ContosoMini.SemanticModel/definition", and a row open saved it
        // without "/definition". The list then showed two rows with the same name: the "some twice" Kane saw. The window reopens the path the engine
        // reports as the session's source, which for a Power BI folder model is its inner definition folder.
        [Fact]
        public async Task A_folder_model_opened_by_its_definition_folder_keeps_one_saved_row()
        {
            var folder = Path.Combine(_root, "models", "ContosoMini.SemanticModel");
            var definition = Path.Combine(folder, "definition");
            using (var writer = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _workspace, true))
            {
                await writer.OpenAsync(FixtureCopy(), discardUnsaved: true);
                await writer.SaveAsync(definition, "TMDL", false, "human");
            }
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _workspace, true);
            var roots = ThrowawayPaths.RootsOverride;
            ThrowawayPaths.RootsOverride = new[] { Path.Combine(_root, "throwaway") };   // a test writes under temp; this folder stands for a lasting one
            try
            {
            await engine.OpenAsync(folder, discardUnsaved: true);                       // a row open: the folder the person picked
            var source = (await engine.SessionInfoAsync()).Source;                      // what the window remembers for the next start
            await engine.OpenAsync(source, discardUnsaved: true);                       // the start-up open of that path
            }
            finally { ThrowawayPaths.RootsOverride = roots; }

            var rows = ConnectionRegistry.List().Where(r => r.Kind == "file" && r.Endpoint.StartsWith(folder, StringComparison.Ordinal)).ToArray();
            Assert.Single(rows);
            Assert.Equal(folder, rows[0].Endpoint);
            Assert.Equal(2, rows[0].UseCount);
        }
    }
}
