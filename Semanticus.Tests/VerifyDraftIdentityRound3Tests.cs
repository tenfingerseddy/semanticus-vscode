// Verifier, join 11 drafts round 3: the open kinds DraftModelIdentityTests does not drive (Save As, an unsaved model saved, a cold-started
// snapshot saved elsewhere) and a pin that the snapshot marker is read ONLY to name the model (so deploy can never target a snapshot through it).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifyDraftIdentityRound3Tests : IAsyncLifetime
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Finance";
        // On Windows the file identity folds the path to upper case (LocalEngine.CanonAnchor); it is a key compared as a string, never shown to the person and never opened as a path.
        private static readonly bool IgnoreCaseOnWindows = OperatingSystem.IsWindows();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-vdid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string _savedRegistryRoot;
        private LocalEngine _engine = null!;
        private SessionManager _sessions = null!;

        public VerifyDraftIdentityRound3Tests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            LiveModelExport.TempRootOverride = Path.Combine(_root, "snapshots");
            Directory.CreateDirectory(LiveModelExport.TempRoot);
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            _engine.LiveSnapshotExportForTests = (ds, db) =>
            {
                var dir = Path.Combine(LiveModelExport.TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                var bim = Path.Combine(dir, "Sales.bim");
                File.Copy(TestModels.FindBim(), bim);
                return Task.FromResult(new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = "Sales", DatabaseCount = 1, DatabaseNames = new[] { "Sales" } });
            };
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

        [Fact]
        public async Task Save_As_of_a_file_model_moves_its_identity_to_the_new_path()
        {
            var a = Path.Combine(_root, "A.bim");
            File.Copy(TestModels.FindBim(), a);
            await _engine.OpenAsync(a, discardUnsaved: true);
            var before = await _engine.SessionInfoAsync();
            Assert.Equal("file:" + Path.GetFullPath(a), before.ModelIdentity, ignoreCase: IgnoreCaseOnWindows);
            var b = Path.Combine(_root, "B.bim");
            await _engine.SaveAsync(b, "bim", false, "human");
            var after = await _engine.SessionInfoAsync();
            Assert.Equal("file:" + Path.GetFullPath(b), after.ModelIdentity, ignoreCase: IgnoreCaseOnWindows);   // the per-session canon cache follows the new path
            Assert.Equal("B", after.ModelLabel);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_unsaved_model_saved_to_a_file_becomes_that_file()
        {
            await _engine.CreateModelAsync("Untitled", 1604, discardUnsaved: true);
            Assert.StartsWith("unsaved:", (await _engine.SessionInfoAsync()).ModelIdentity);
            var p = Path.Combine(_root, "Fresh.bim");
            await _engine.SaveAsync(p, "bim", false, "human");
            Assert.Equal("file:" + Path.GetFullPath(p), (await _engine.SessionInfoAsync()).ModelIdentity, ignoreCase: IgnoreCaseOnWindows);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_cold_started_snapshot_saved_elsewhere_keeps_the_published_identity_and_is_never_live_bound()
        {
            await _engine.OpenLiveAsync(Endpoint, "Sales", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            var live = await _engine.SessionInfoAsync();
            using var cold = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await cold.OpenAsync(live.Source);
            var copy = Path.Combine(_root, "Copy.bim");
            await cold.SaveAsync(copy, "bim", false, "human");
            var info = await cold.SessionInfoAsync();
            Assert.Equal(live.ModelIdentity, info.ModelIdentity);   // the design's priority: the marker ranks above the path
            Assert.False(info.LiveBound);
            Assert.Null(info.LiveEndpoint);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_marker_with_a_Windows_line_ending_or_a_broken_body_is_read_or_ignored_never_thrown()
        {
            await _engine.OpenLiveAsync(Endpoint, "Sales", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            var src = (await _engine.SessionInfoAsync()).Source;
            var marker = Path.Combine(Path.GetDirectoryName(src)!, "origin.json");
            File.WriteAllText(marker, "{\r\n\"Endpoint\":\"" + Endpoint + "\",\r\n\"Database\":\"Sales\"\r\n}\r\n");
            using (var cold = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
            {
                await cold.OpenAsync(src);
                Assert.Equal("live:" + Endpoint.ToLowerInvariant() + "|sales", (await cold.SessionInfoAsync()).ModelIdentity);
            }
            File.WriteAllText(marker, "{ not json");
            using (var cold = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
            {
                await cold.OpenAsync(src);
                Assert.StartsWith("file:", (await cold.SessionInfoAsync()).ModelIdentity);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void The_snapshot_marker_is_read_only_by_the_identity_and_label_functions()
        {
            // Same technique as ConnectionContextTests.Live_origin_write_authority_is_compiled_only_into_the_editing_open_paths:
            // every compiled read of Session.SnapshotOf. Deploy, query and bind code never read it, so they cannot target a snapshot through it.
            var getter = typeof(Session).GetProperty(nameof(Session.SnapshotOf))!.GetMethod!;
            var callers = CompiledCallGraph.FindCallers(typeof(LocalEngine).Assembly, getter)
                .Select(m => m.DeclaringType!.FullName + "." + m.Name).Distinct().OrderBy(x => x).ToArray();
            // Since 15923113 (11 Oct, hero15 H15) the recovery copy and the idle-exit check also ask whether a session is a snapshot: a snapshot
            // keeps no recovery copy, and its unsaved edits keep the engine up. Neither one picks a model to write to or query.
            Assert.Equal(new[]
            {
                "Semanticus.Engine.LocalEngine.ModelIdentityOf", "Semanticus.Engine.LocalEngine.ModelLabelOf", "Semanticus.Engine.Program.KeepOwnerAlive",
                "Semanticus.Engine.Session.AttachLiveRecovery", "Semanticus.Engine.Session.get_RecoveryEligible", "Semanticus.Engine.Session.StartFileRecovery",
            }, callers);
            var setter = typeof(Session).GetProperty(nameof(Session.SnapshotOf))!.SetMethod!;
            Assert.All(CompiledCallGraph.FindCallers(typeof(LocalEngine).Assembly, setter), m => Assert.Contains("OpenCoreAsync", m.DeclaringType!.FullName + m.Name));
        }
    }

    // A REAL engine on a real RPC pipe for the verifier's browser drive (~/.cache/vd3, vd3-real-drive.mjs): not a proof on its own, a host.
    // open_live needs a published model, so the snapshot export and the live bind use this assembly's seams (as DraftModelIdentityTests does);
    // everything else is the real engine, the real RpcServer, the real SessionInfo. The snapshot root is the one VD3_SNAP_ROOT names, so a
    // SECOND host process (an engine restart, a cold start) sees the folders and markers the first one wrote.
    // Without VD3_HOST_DIR the test does nothing and passes at once.
    [Collection("restore-root")]
    public sealed class VerifyDrafts3StudioHostTests
    {
        private const string Challenge = "vd3-host-challenge-0123456789abcdef";
        private readonly RestoreRootFixture _fixture;
        public VerifyDrafts3StudioHostTests(RestoreRootFixture fixture) { _fixture = fixture; }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Hosts_a_real_engine_on_a_pipe_for_the_verifier_drive_when_asked()
        {
            var dir = Environment.GetEnvironmentVariable("VD3_HOST_DIR");
            if (string.IsNullOrWhiteSpace(dir)) return;
            var snaps = Environment.GetEnvironmentVariable("VD3_SNAP_ROOT") ?? Path.Combine(dir, "snapshots");
            Directory.CreateDirectory(dir); Directory.CreateDirectory(snaps);
            var origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Directory.CreateDirectory(Path.Combine(dir, "home")); Directory.CreateDirectory(Path.Combine(dir, "policy"));
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(dir, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(dir, "user");
            AgentPolicyStore.RootOverride = Path.Combine(dir, "policy");
            ApprovalLedger.RootOverride = Path.Combine(dir, "policy");
            LiveModelExport.TempRootOverride = snaps;
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(dir, "ws"));
            engine.LiveSnapshotExportForTests = (ds, db) =>
            {
                var d = Path.Combine(LiveModelExport.TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(d);
                var bim = Path.Combine(d, "Sales.bim");
                File.Copy(TestModels.FindBim(), bim);
                return Task.FromResult(new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = "Sales", DatabaseCount = 1, DatabaseNames = new[] { "Sales" } });
            };
            engine.LiveBindForTests = (kind, ds) => LiveConnection.ForTest(kind, ds, "Sales");
            var pipeName = TestPipe.Mint("sem-vd3-");
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            try
            {
                File.WriteAllText(Path.Combine(dir, "pipe.json.tmp"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    pipe = pipeName, socket = TestPipe.SocketPathFor(Path.GetTempPath(), pipeName), challenge = Challenge, pid = Environment.ProcessId,
                    bim = TestModels.FindBim(),
                }));
                File.Move(Path.Combine(dir, "pipe.json.tmp"), Path.Combine(dir, "pipe.json"), true);
                var until = DateTime.UtcNow.AddMinutes(20);
                while (!File.Exists(Path.Combine(dir, "stop")) && DateTime.UtcNow < until) await Task.Delay(200);
                Assert.True(File.Exists(Path.Combine(dir, "stop")), "the drive ended the host with its stop file, not the 20 minute limit");
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                try { engine.Dispose(); } catch { }
                try { sessions.Dispose(); } catch { }
                LiveModelExport.TempRootOverride = null;
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, origHome);
                ConnectionRegistry.RootOverride = _fixture.Root; AgentPolicyStore.RootOverride = _fixture.Root; ApprovalLedger.RootOverride = _fixture.Root;
            }
        }
    }
}
