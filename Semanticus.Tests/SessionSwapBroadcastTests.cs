// Golden rule 2 (the UI door is pushed changes live): a model swap made through the assistant's door must reach the
// VS Code window on the pipe at once, with no later edit needed to wake it. Found by the Ask your data fixer: after the
// assistant called create_model the window kept showing the old model until the new one was edited, because a swap
// publishes nothing a commit would. Each test drives one session-replacing op from the assistant's side and watches
// a person's pipe connection for session/didChange naming the new model.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class SessionSwapBroadcastTests : IAsyncLifetime
    {
        private const string Challenge = "session-swap-test-challenge-0123456789abcdef";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-session-swap-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly string _savedRegistryRoot;
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private RpcServer _server = null!;
        private Task _serving = null!;
        private string _pipe = null!;
        private Watcher _ui = null!;
        private readonly List<string> _snapshotDirs = new List<string>();

        public SessionSwapBroadcastTests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            await _engine.OpenAsync(CopyBim("start"));   // an old model is open, so a swap is a real swap
            _pipe = TestPipe.Mint("semanticus-swap-");
            _server = new RpcServer(_sessions, _engine, _pipe, Challenge);
            _serving = _server.RunAsync(_stop.Token);
            _ui = await Watcher.ConnectAsync(_pipe, RpcConnectionRole.Human, Challenge);
        }

        public async Task DisposeAsync()
        {
            _ui?.Dispose();
            _stop.Cancel();
            try { await _serving; } catch (OperationCanceledException) { }
            _server?.Dispose();
            _engine?.Dispose();
            ConnectionRegistry.RootOverride = _savedRegistryRoot;
            try { Directory.Delete(_root, true); } catch { }
            foreach (var d in _snapshotDirs) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        [Fact]
        public async Task Mcp_create_model_tells_the_ui_door_about_the_new_model_with_no_edit()
        {
            var before = _sessions.Current.Id;
            var waiting = _ui.WaitAsync(n => n.Value<string>("modelName") == "Contoso Fresh");
            OpenResult created = null!;
            await PlanQueueKit.CallAsync(_engine, async () =>
                created = await McpTools.CreateModel(_engine, "Contoso Fresh", 1604, discardUnsaved: true));

            var n = await waiting.WaitAsync(Deadline);
            Assert.NotEqual(before, created.SessionId);
            Assert.Equal(created.SessionId, n.Value<string>("sessionId"));
            Assert.Equal(_sessions.Current.Id, n.Value<string>("sessionId"));
            Assert.Equal("create", n.Value<string>("reason"));
            Assert.Equal(0, n.Value<int>("tables"));
            Assert.Equal(0L, _sessions.Current.Revision);   // nothing was edited: the swap alone woke the window
        }

        [Fact]
        public async Task Attached_assistant_create_model_reaches_the_ui_door_over_the_pipe()
        {
            // The cross-process case: the assistant's MCP process is an agent connection on the same pipe.
            using var assistant = await RemoteEngine.ConnectAsync(_pipe);
            var waiting = _ui.WaitAsync(n => n.Value<string>("modelName") == "Contoso Attached");
            var created = await McpTools.CreateModel(assistant, "Contoso Attached", 1604, discardUnsaved: true);

            var n = await waiting.WaitAsync(Deadline);
            Assert.Equal(created.SessionId, n.Value<string>("sessionId"));
            Assert.Equal("create", n.Value<string>("reason"));
        }

        [Fact]
        public async Task Mcp_open_model_tells_the_ui_door()
        {
            var path = CopyBim("opened");
            var waiting = _ui.WaitAsync(n => n.Value<string>("source") == path);
            OpenResult opened = null!;
            await PlanQueueKit.CallAsync(_engine, async () => opened = await McpTools.OpenModel(_engine, path, discardUnsaved: true));

            var n = await waiting.WaitAsync(Deadline);
            Assert.Equal(opened.SessionId, n.Value<string>("sessionId"));
            Assert.Equal("open", n.Value<string>("reason"));
            Assert.Equal(opened.Tables, n.Value<int>("tables"));
            Assert.True(opened.Tables > 0);
        }

        [Fact]
        public async Task Mcp_open_local_tells_the_ui_door_after_the_query_connection_is_bound()
        {
            var bim = SnapshotBim("Local");
            _engine.LiveSnapshotExportForTests = (ds, db) => Task.FromResult(Snapshot(bim, "Local Model"));
            _engine.LiveBindForTests = (kind, ds) => LiveConnection.ForTest(kind, ds, "Local Model");
            var waiting = _ui.WaitAsync(n => n.Value<string>("reason") == "open_local");
            OpenResult opened = null!;
            await PlanQueueKit.CallAsync(_engine, async () =>
                opened = await McpTools.OpenLocal(_engine, "localhost:51234", "Local Model", discardUnsaved: true));

            var n = await waiting.WaitAsync(Deadline);
            Assert.Equal(opened.SessionId, n.Value<string>("sessionId"));
            Assert.True(opened.LiveConnected);
            // Sent after the rebind, so the window's status read already sees the query connection.
            Assert.True(n.Value<bool>("liveConnected"));
        }

        [Fact]
        public async Task Agent_open_live_tells_the_ui_door_after_the_query_connection_is_bound()
        {
            // open_live's MCP tool signs in with a real identity; the engine op underneath is what both doors run, so
            // it is driven directly as the agent with a caller token (no sign-in, no network).
            var bim = SnapshotBim("Published");
            _engine.LiveSnapshotExportForTests = (ep, db) => Task.FromResult(Snapshot(bim, "Published"));
            _engine.LiveBindForTests = (kind, ds) => LiveConnection.ForTest(kind, ds, "Published");
            var waiting = _ui.WaitAsync(n => n.Value<string>("reason") == "open_live");
            var opened = await _engine.OpenLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/Contoso", "Published", "token", "not-a-jwt",
                null, origin: "agent", discardUnsaved: true);

            var n = await waiting.WaitAsync(Deadline);
            Assert.Equal(opened.SessionId, n.Value<string>("sessionId"));
            Assert.True(n.Value<bool>("liveConnected"));
        }

        [Fact]
        public async Task Mcp_prepare_working_copy_tells_the_ui_door_once_the_copy_is_open()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "azcli");
            var snapshot = CopyBim(Path.Combine("wc-snap", "Published"));
            _engine.WorkspaceTokenExportForTests = _ => Task.FromResult(Snapshot(snapshot, "Published"));
            _engine.WorkingCopyConnectForTests = r =>
            {
                _engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };
            var waiting = _ui.WaitAsync(n => n.Value<string>("reason") == "working_copy");
            WorkingCopyResult result = null!;
            await PlanQueueKit.CallAsync(_engine, async () =>
                result = await McpTools.PrepareWorkingCopy(_engine, record.Id, Path.Combine(_root, "models"), commit: true));

            var n = await waiting.WaitAsync(Deadline);
            Assert.True(result.Opened);
            Assert.True(result.QueryConnected);
            Assert.Equal(_sessions.Current.Id, n.Value<string>("sessionId"));
            Assert.True(n.Value<bool>("liveConnected"));   // sent after the query model attached
        }

        [Fact]
        public async Task Mcp_restore_history_checkpoint_tells_the_ui_door_about_the_reopened_model()
        {
            var dir = Path.Combine(_root, "repo");
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            Git(dir, "init", "-q");
            Git(dir, "config", "user.email", "swap@test.local");
            Git(dir, "config", "user.name", "Swap Test");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "add", "--", "model.bim");
            Git(dir, "commit", "-q", "-m", "initial");
            await _engine.OpenAsync(model, discardUnsaved: true);
            var measure = (await _engine.ListMeasuresAsync()).First().Ref;
            await _engine.SetDaxAsync(measure, "1", "human");
            var first = await _engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
            await _engine.SetDaxAsync(measure, "2", "human");
            await _engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human");
            var before = _sessions.Current.Id;

            var waiting = _ui.WaitAsync(n => n.Value<string>("reason") == "restore");
            HistoryRestoreResult restored = null!;
            await PlanQueueKit.CallAsync(_engine, async () =>
                restored = await McpTools.RestoreHistoryCheckpoint(_engine, first.Checkpoint.Hash, restore: true));

            var n = await waiting.WaitAsync(Deadline);
            Assert.True(restored.Restored, restored.Error);
            Assert.NotEqual(before, n.Value<string>("sessionId"));
            Assert.Equal(_sessions.Current.Id, n.Value<string>("sessionId"));
        }

        [Fact]
        public async Task A_windows_own_swap_is_not_echoed_back_to_it_but_reaches_every_other_client()
        {
            // The window refreshes from its own request's answer. An echo could arrive before that answer and tell
            // Studio the connection was replaced while the request was still in flight, which fails the request.
            using var other = await Watcher.ConnectAsync(_pipe, RpcConnectionRole.Agent);
            var otherSees = other.WaitAsync(n => n.Value<string>("modelName") == "Contoso Own");
            var created = await _ui.InvokeAsync<OpenResult>("createModel", "Contoso Own", 1604, true);

            var n = await otherSees.WaitAsync(Deadline);
            Assert.Equal(created.SessionId, n.Value<string>("sessionId"));
            await Task.Delay(500);   // the other client already has it; the window had the same chance
            Assert.DoesNotContain(_ui.Seen, s => s.Value<string>("sessionId") == created.SessionId);
        }

        // The guard in PublishSessionSwap (skipped when a newer swap already replaced this session). No test above
        // failed when the verifier deleted it: a unified open commits its session, then waits in the query bind, and
        // a newer swap lands meanwhile. When the bind returns, its model is no longer open and must not be announced
        // after the newer one, or the window would show a model that is not the engine's. Adapted from the
        // verifier's probe for this lane.
        [Fact]
        public async Task A_unified_open_replaced_while_it_binds_never_announces_the_replaced_model()
        {
            var bim = SnapshotBim("Local");
            using var binding = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            _engine.LiveSnapshotExportForTests = (ds, db) => Task.FromResult(Snapshot(bim, "Local Model"));
            _engine.LiveBindForTests = (kind, ds) => { binding.Set(); release.Wait(TimeSpan.FromSeconds(20)); return LiveConnection.ForTest(kind, ds, "Local Model"); };
            var local = Task.Run(() => _engine.OpenLocalAsync("localhost:51234", "Local Model", discardUnsaved: true));
            try
            {
                Assert.True(binding.Wait(TimeSpan.FromSeconds(20)), "open_local never reached its query bind");
                var newer = _ui.WaitAsync(n => n.Value<string>("modelName") == "Contoso Newer");
                await McpTools.CreateModel(_engine, "Contoso Newer", 1604, discardUnsaved: true);
                await newer.WaitAsync(Deadline);
            }
            finally { release.Set(); }
            var r = await local;
            // A later swap flushes the channel, so an open_local announcement sent after the bind would be seen by now.
            var after = _ui.WaitAsync(n => n.Value<string>("modelName") == "Contoso Flush");
            await McpTools.CreateModel(_engine, "Contoso Flush", 1604, discardUnsaved: true);
            await after.WaitAsync(Deadline);

            Assert.NotEqual(r.SessionId, _ui.Seen.Single(n => n.Value<string>("modelName") == "Contoso Newer").Value<string>("sessionId"));
            Assert.DoesNotContain(_ui.Seen, n => n.Value<string>("reason") == "open_local");
            Assert.DoesNotContain(_ui.Seen, n => n.Value<string>("sessionId") == r.SessionId);
        }

        [Fact]
        public async Task A_failed_open_tells_nobody_that_the_model_changed()
        {
            var before = _sessions.Current.Id;
            await Assert.ThrowsAnyAsync<Exception>(() =>
                McpTools.OpenModel(_engine, Path.Combine(_root, "missing.bim"), discardUnsaved: true));
            // A later swap proves the channel is live, so its absence above is not a dead pipe.
            var waiting = _ui.WaitAsync(n => n.Value<string>("modelName") == "Contoso After");
            await McpTools.CreateModel(_engine, "Contoso After", 1604, discardUnsaved: true);
            await waiting.WaitAsync(Deadline);
            Assert.Single(_ui.Seen);
            Assert.NotEqual(before, _ui.Seen[0].Value<string>("sessionId"));
        }

        private string CopyBim(string relative)
        {
            var path = Path.Combine(_root, relative + ".bim");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(TestModels.FindBim(), path);
            return path;
        }

        // A unified open adopts its snapshot folder and deletes it later, so it only accepts one under the engine's own
        // snapshot root, exactly where the real export writes.
        private string SnapshotBim(string name)
        {
            var dir = Path.Combine(LiveModelExport.TempRoot, "swap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            _snapshotDirs.Add(dir);
            var path = Path.Combine(dir, name + ".bim");
            File.Copy(TestModels.FindBim(), path);
            return path;
        }

        private static LiveModelExport.Snapshot Snapshot(string bim, string database) => new LiveModelExport.Snapshot
        {
            BimPath = bim, DatabaseName = database, DatabaseCount = 1, DatabaseNames = new[] { database },
        };

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        /// <summary>A pipe client that records every session/didChange it is sent.</summary>
        private sealed class Watcher : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;
            private readonly object _gate = new object();
            private readonly List<JObject> _seen = new List<JObject>();
            private readonly List<(Func<JObject, bool> Match, TaskCompletionSource<JObject> Done)> _waits = new();

            private Watcher(NamedPipeClientStream pipe)
            {
                _pipe = pipe;
                _rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                _rpc.AddLocalRpcMethod("session/didChange", new Action<JToken>(OnSwap));
                _rpc.StartListening();
            }

            internal static async Task<Watcher> ConnectAsync(string pipeName, RpcConnectionRole role, string? challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                return new Watcher(pipe);
            }

            internal List<JObject> Seen { get { lock (_gate) return _seen.ToList(); } }

            internal Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            internal Task<JObject> WaitAsync(Func<JObject, bool> match)
            {
                lock (_gate)
                {
                    var hit = _seen.FirstOrDefault(match);
                    if (hit != null) return Task.FromResult(hit);
                    var done = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waits.Add((match, done));
                    return done.Task;
                }
            }

            private void OnSwap(JToken payload)
            {
                var n = (JObject)payload;
                lock (_gate)
                {
                    _seen.Add(n);
                    foreach (var w in _waits.Where(w => w.Match(n)).ToList())
                    {
                        _waits.Remove(w);
                        w.Done.TrySetResult(n);
                    }
                }
            }

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}
