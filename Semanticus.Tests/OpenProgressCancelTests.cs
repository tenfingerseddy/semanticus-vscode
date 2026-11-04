// cx2 (Kane, 3 Nov, 1.7.3): "it keeps freezing up or wont do it". During an open the Connections dialog greyed every Open and said
// nothing, and its Cancel could not stop the open. Kane picked option A of the mock-up (11:44): one bar at the bottom of the dialog that
// shows "Step n of 5 · <step>" while the open runs, and a Cancel that really stops it. The tester's notes on the mock-up (2026-11-03
// connections-connect-mockup-2, notes 2 and 3): the steps must come to the window while the open runs, not only on its result at the
// end, and Cancel must stop the open in the engine and change nothing (no swapped model, no saved-list or history write).
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
    public sealed class OpenProgressCancelTests : IAsyncLifetime
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Finance";
        private readonly string _savedRegistryRoot;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-opc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private string _registry = null!;
        private string _workspace = null!;

        public OpenProgressCancelTests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

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

        /// <summary>A live engine whose read of the published model waits at <paramref name="exportGate"/> (the slow "read the model
        /// definition" of a real open), so a test can look at the window's events, or press Cancel, while the open runs.</summary>
        private LocalEngine NewLiveEngine(SessionManager sessions, ManualResetEventSlim? exportGate = null, Func<string, string, LiveConnection>? bind = null)
        {
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, _workspace, true);
            engine.LiveSnapshotExportForTests = async (ds, db) =>
            {
                if (exportGate != null) await Task.Run(() => exportGate.Wait(TimeSpan.FromSeconds(30)));
                var bim = FixtureCopy(snapshot: true);
                return new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = "Northstar Sales", DatabaseCount = 1, DatabaseNames = new[] { "Northstar Sales" } };
            };
            engine.LiveBindForTests = bind ?? ((kind, ds) => LiveConnection.ForTest(kind, ds, "Northstar Sales"));
            return engine;
        }

        private static async Task<OperationProgress> WaitForStep(List<OperationProgress> seen, string words)
        {
            for (var i = 0; i < 300; i++)
            {
                lock (seen) { var hit = seen.FirstOrDefault(e => e.OpKey == "open" && e.Note == words); if (hit != null) return hit; }
                await Task.Delay(50);
            }
            string[] got; lock (seen) got = seen.Select(e => e.OpKey + ":" + e.Note).ToArray();
            throw new Xunit.Sdk.XunitException($"No open step '{words}' reached the window. Seen: {string.Join(" | ", got)}");
        }

        private Dictionary<string, byte[]?> SavedFiles() =>
            new[] { "connections.json", "connection-history.json" }.ToDictionary(f => f, f => File.Exists(Path.Combine(_registry, f)) ? File.ReadAllBytes(Path.Combine(_registry, f)) : null);

        // Tester note 3: "Step 3 of 5" during the open needs the engine to send each step as it starts. Before cx2 the steps came back only
        // on the open's result, after the open ended, so the bar could only show them when there was nothing left to show.
        [Fact]
        public async Task A_live_open_sends_each_step_to_the_window_while_it_runs()
        {
            var sessions = new SessionManager();
            var seen = new List<OperationProgress>();
            sessions.Bus.Progress += e => { lock (seen) seen.Add(e); };
            using var gate = new ManualResetEventSlim(false);
            using var engine = NewLiveEngine(sessions, gate);

            var open = engine.OpenLiveAsync(Endpoint, "Northstar Sales", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
            var reading = await WaitForStep(seen, "Connecting to the workspace");
            Assert.False(open.IsCompleted, "The step arrived only after the open had ended.");
            Assert.Equal(2, reading.Done);
            Assert.Equal(5, reading.Total);
            Assert.True(reading.Cancellable == true, "The open can still be stopped while it reads the published model.");
            gate.Set();
            await open;

            OperationProgress[] got; lock (seen) got = seen.Where(e => e.OpKey == "open").ToArray();
            Assert.True(got.Length >= 4, "Too few steps reached the window.");
            Assert.All(got, e => Assert.Equal(reading.RunId, e.RunId));
            var named = got.Where(e => e.Note != null).ToArray();
            Assert.Equal(new[] { "Signing in", "Connecting to the workspace", "Loading the model", "Connecting for queries" },
                named.Select(e => e.Note).Distinct().ToArray());
            Assert.True(named.Select(e => e.Done).SequenceEqual(named.Select(e => e.Done).OrderBy(d => d)), "A step went back.");
            // The model is open by "Connecting for queries": a Cancel then can no longer change nothing, so the bar is told it cannot.
            Assert.Contains(named, e => e.Note == "Connecting for queries" && e.Cancellable == false);
            Assert.Equal("human", named[0].Origin);
            // The last event has no words: the open is over and the bar stops showing a step.
            Assert.Null(got[^1].Note);
        }

        [Fact]
        public async Task A_file_open_sends_its_three_steps_to_the_window()
        {
            var sessions = new SessionManager();
            var seen = new List<OperationProgress>();
            sessions.Bus.Progress += e => { lock (seen) seen.Add(e); };
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro, _workspace, true);

            await engine.OpenAsync(FixtureCopy(), discardUnsaved: true);

            OperationProgress[] got; lock (seen) got = seen.Where(e => e.OpKey == "open" && e.Note != null).ToArray();
            Assert.Equal(new[] { "Reading the files", "Loading the model", "Saving it to your list" }, got.Select(e => e.Note).Distinct().ToArray());
            Assert.All(got, e => Assert.Equal(3, e.Total));
        }

        // Tester note 2: "Cancel promises nothing changed, and today's engine cannot keep that yet. The open has no cancel." The window's
        // Cancel now reaches the engine (cancelOpen, on the person's own connection only), the open stops, and the model that was open,
        // the saved-connections list and the history are exactly as they were.
        [Fact]
        public async Task Cancel_stops_a_running_open_and_leaves_the_session_and_the_saved_files_as_they_were()
        {
            var sessions = new SessionManager();
            var seen = new List<OperationProgress>();
            sessions.Bus.Progress += e => { lock (seen) seen.Add(e); };
            using var gate = new ManualResetEventSlim(false);
            using var engine = NewLiveEngine(sessions, gate);
            await engine.OpenAsync(FixtureCopy(), discardUnsaved: true);
            var before = sessions.Current!.Id;
            var files = SavedFiles();
            Assert.NotNull(files["connection-history.json"]);   // the file open wrote its history row (a temp-folder file is not put on the saved list)

            var open = engine.OpenLiveAsync(Endpoint, "Northstar Sales", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
            var step = await WaitForStep(seen, "Connecting to the workspace");
            var answer = await new HumanGovernanceRpcTarget(engine).cancelOpen(step.RunId);
            gate.Set();

            Assert.True(answer.Stopped, "The engine did not stop the open.");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => open);
            Assert.Contains("did not open", ex.Message);
            Assert.Contains("nothing changed", ex.Message);
            Assert.Equal(before, sessions.Current!.Id);
            var after = SavedFiles();
            foreach (var f in files.Keys) Assert.True(files[f] == null ? after[f] == null : after[f] != null && files[f]!.SequenceEqual(after[f]!), f + " changed after a cancelled open.");
            OperationProgress[] got; lock (seen) got = seen.Where(e => e.OpKey == "open" && e.RunId == step.RunId).ToArray();
            Assert.Null(got[^1].Note);   // the bar is told the open is over
        }

        // A Cancel that comes after the model is already open cannot keep "nothing changed". The engine says so instead of pretending, and
        // the open finishes as an open.
        [Fact]
        public async Task A_cancel_after_the_model_is_open_says_so_and_the_open_finishes()
        {
            var sessions = new SessionManager();
            var seen = new List<OperationProgress>();
            sessions.Bus.Progress += e => { lock (seen) seen.Add(e); };
            OpenCancelResult? late = null;
            LocalEngine engine = null!;
            engine = NewLiveEngine(sessions, bind: (kind, ds) =>
            {
                string run; lock (seen) run = seen.Last(e => e.OpKey == "open").RunId;
                late = new HumanGovernanceRpcTarget(engine).cancelOpen(run).GetAwaiter().GetResult();
                return LiveConnection.ForTest(kind, ds, "Northstar Sales");
            });
            using (engine)
            {
                var open = await engine.OpenLiveAsync(Endpoint, "Northstar Sales", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
                Assert.NotNull(late);
                Assert.False(late!.Stopped);
                Assert.Equal("opened", late.State);
                Assert.Equal(open.SessionId, sessions.Current!.Id);
            }
        }

        [Fact]
        public async Task Cancel_with_no_open_running_says_none()
        {
            using var engine = NewLiveEngine(new SessionManager());
            var answer = await new HumanGovernanceRpcTarget(engine).cancelOpen("nope1234");
            Assert.False(answer.Stopped);
            Assert.Equal("none", answer.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Desktop_cancel_ends_the_open_before_the_export_finishes_and_changes_nothing(bool useRunId)
        {
            var sessions = new SessionManager();
            var seen = new List<OperationProgress>();
            sessions.Bus.Progress += e => { lock (seen) seen.Add(e); };
            using var engine = NewLiveEngine(sessions);
            await engine.OpenAsync(FixtureCopy(), discardUnsaved: true);
            var before = sessions.Current!;
            var contextBefore = sessions.CurrentContext;
            var files = SavedFiles();
            lock (seen) seen.Clear();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var export = new TaskCompletionSource<LiveModelExport.Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.LiveSnapshotExportForTests = (ds, db) => { entered.SetResult(); return export.Task; };
            var open = engine.OpenLocalAsync("localhost:51234", "Contoso Desktop", discardUnsaved: true);
            string? abandoned = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                OperationProgress? step; lock (seen) step = seen.LastOrDefault(e => e.OpKey == "open");
                var answer = await new HumanGovernanceRpcTarget(engine).cancelOpen(useRunId ? step?.RunId : null!);
                Assert.True(answer.Stopped, "Desktop Cancel found no registered open.");
                Assert.Equal("stopped", answer.State);
                Assert.NotNull(step);
                Assert.True(step!.Cancellable);
                // The export remains blocked. Cancel must end the open without waiting for it.
                var ex = await Assert.ThrowsAsync<OpenCancelledException>(() => open.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("Contoso Desktop did not open, and nothing changed", ex.Message);
                Assert.Same(before, sessions.Current);
                Assert.Same(contextBefore, sessions.CurrentContext);
                var after = SavedFiles();
                foreach (var f in files.Keys) Assert.Equal(files[f], after[f]);
                lock (seen) Assert.Null(seen.Last(e => e.OpKey == "open").Note);
            }
            finally
            {
                abandoned = FixtureCopy(snapshot: true);
                export.SetResult(new LiveModelExport.Snapshot { BimPath = abandoned, DatabaseName = "Contoso Desktop" });
                try { await open; } catch (OpenCancelledException) { }
            }
            for (var i = 0; i < 100 && File.Exists(abandoned); i++) await Task.Delay(20);
            Assert.False(File.Exists(abandoned), "The cancelled Desktop export left its snapshot behind.");
        }

        [Fact]
        public async Task Desktop_open_sends_steps_and_a_late_cancel_says_the_model_is_open()
        {
            var sessions = new SessionManager();
            var seen = new List<OperationProgress>();
            sessions.Bus.Progress += e => seen.Add(e);
            OpenCancelResult? late = null;
            LocalEngine engine = null!;
            engine = NewLiveEngine(sessions, bind: (kind, ds) =>
            {
                late = new HumanGovernanceRpcTarget(engine).cancelOpen(null!).GetAwaiter().GetResult();
                return LiveConnection.ForTest(kind, ds, "Contoso Desktop");
            });
            var export = engine.LiveSnapshotExportForTests;
            engine.LiveSnapshotExportForTests = (ds, db) =>
            {
                // ToBimLocalAsync uses the shared exporter, which sends phase 3 after connecting.
                OpenTimer.Phase(3, "Reading the model definition");
                return export(ds, db);
            };
            using (engine)
            {
                var result = await engine.OpenLocalAsync("localhost:51234", "Contoso Desktop", discardUnsaved: true);
                Assert.NotNull(late);
                Assert.Equal("opened", late!.State);
                Assert.False(late.Stopped);
                Assert.Equal(result.SessionId, sessions.Current!.Id);
                var steps = seen.Where(e => e.OpKey == "open" && e.Note != null).ToArray();
                Assert.Equal(new[] { "Finding the model", "Connecting to Power BI Desktop", "Reading the model definition", "Loading the model", "Connecting for queries" }, steps.Select(e => e.Note).Distinct());
                Assert.All(steps, e => Assert.Equal(5, e.Total));
                Assert.True(steps.Select(e => e.Done).SequenceEqual(steps.Select(e => e.Done).OrderBy(done => done)));
                Assert.Contains(steps, e => e.Note == "Connecting for queries" && e.Cancellable == false);
                Assert.Null(seen.Last(e => e.OpKey == "open").Note);
            }
        }

        // The cancel is the window's business: an assistant connection cannot stop the person's open (and no MCP tool was added).
        [Fact]
        public void Cancel_is_on_the_persons_connection_only()
        {
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("cancelOpen"));
            Assert.Null(typeof(EngineRpcTarget).GetMethod("cancelOpen"));
        }
    }
}
