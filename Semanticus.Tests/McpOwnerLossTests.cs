using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The assistant's connection must outlive the engine it is attached to. Measured on 2026-09-23 against the
    /// shipped 1.2.x door: when the owner goes away (VS Code closed, reloaded onto an update, or restarted) the
    /// attached MCP process stays up but every later call fails until the whole server is reconnected, and when
    /// the assistant started first the MCP process WAS the owner, so opening Studio (which reclaims ownership by
    /// stopping the owner's pid) stopped the assistant's server outright. These pin the fixed behaviour: the MCP
    /// process never hosts the owner itself, it starts or rejoins one on its own, and every call in a gap gets a
    /// plain sentence the assistant can act on, quickly, never a hang.
    /// </summary>
    public sealed class McpOwnerLossTests
    {
        private sealed class Tier : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Tier(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        /// <summary>One in-process owner on its own pipe, published in the workspace like a real one.</summary>
        private sealed class FakeOwner : IDisposable
        {
            public readonly string Pipe = TestPipe.Mint("semanticus-loss-");
            private readonly SessionManager _sessions = new SessionManager();
            private readonly LocalEngine _engine;
            private readonly RpcServer _server;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _run;

            public FakeOwner(string ws, bool pro, string exePath = null, string buildId = null, string home = null)
            {
                _engine = new LocalEngine(_sessions, new Tier(pro), ws);
                _server = new RpcServer(_sessions, _engine, Pipe);
                _run = _server.RunAsync(_cts.Token);
                EngineBroker.WriteInfo(ws, new EngineInfo
                {
                    PipeName = Pipe, Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow.ToString("o"), Workspace = ws,
                    ExePath = exePath,
                    BuildId = buildId,   // null: stamped with THIS build by WriteInfo, like a real same-build owner
                    Home = home,         // null: stamped with THIS process's state folder, like an owner it started
                });
            }

            /// <summary>The owner exits the way a real one does: stops serving and takes its record with it.</summary>
            public void Exit(string ws)
            {
                Dispose();
                EngineBroker.ClearInfo(ws, Environment.ProcessId);
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _server.Dispose(); } catch { }
                try { _run.Wait(5000); } catch { }
                try { _engine.Dispose(); } catch { }
                try { _sessions.Dispose(); } catch { }
            }
        }

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-loss-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return ws;
        }

        private static async Task<string> FailureOf(Func<Task> call)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(call);
            return ex.Message;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_call_with_no_owner_and_nobody_to_start_one_gets_a_plain_sentence_quickly()
        {
            var ws = NewWorkspace();
            var owner = new FakeOwner(ws, pro: false);
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(owner.Pipe, ws, timeoutMs: 1000);
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                owner.Exit(ws);
                var clock = Stopwatch.StartNew();
                var message = await FailureOf(() => remote.GetEntitlementAsync());
                clock.Stop();

                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the gap answer took {clock.Elapsed.TotalSeconds:0.0}s");
                Assert.Contains("Semanticus is not running for this folder", message);
                Assert.Contains("reconnect with /mcp", message);
                Assert.DoesNotContain("owner engine", message);   // engine jargon the assistant would repeat
            }
            finally
            {
                remote?.Dispose();
                owner.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_proxy_whose_owner_exits_starts_a_new_owner_and_keeps_serving()
        {
            var ws = NewWorkspace();
            var first = new FakeOwner(ws, pro: false);
            FakeOwner second = null;
            var starts = 0;
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(first.Pipe, ws, timeoutMs: 1000, startOwner: () =>
                {
                    Interlocked.Increment(ref starts);
                    second = new FakeOwner(ws, pro: true);   // pro, so landing on it is observable
                    return Task.FromResult(true);
                });
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                first.Exit(ws);   // VS Code closed and nobody came back

                Assert.Equal("pro", (await remote.GetEntitlementAsync()).Tier);
                Assert.Equal(1, starts);
                Assert.Equal("pro", (await remote.GetEntitlementAsync()).Tier);
                Assert.Equal(1, starts);
            }
            finally
            {
                remote?.Dispose();
                first.Dispose();
                second?.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Fact]
        public async Task An_owner_that_comes_back_during_the_gap_is_rejoined_without_starting_another()
        {
            var ws = NewWorkspace();
            var first = new FakeOwner(ws, pro: false);
            FakeOwner second = null;
            var starts = 0;
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(first.Pipe, ws, timeoutMs: 1000, startOwner: () =>
                {
                    Interlocked.Increment(ref starts);
                    return Task.FromResult(false);
                });
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                // A window reload: the old owner leaves, the new one publishes a moment later.
                first.Exit(ws);
                var comeBack = Task.Run(async () => { await Task.Delay(600); second = new FakeOwner(ws, pro: true); });

                Assert.Equal("pro", (await remote.GetEntitlementAsync()).Tier);
                await comeBack;
                Assert.Equal(0, starts);
            }
            finally
            {
                remote?.Dispose();
                first.Dispose();
                second?.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Fact]
        public async Task An_owner_from_a_different_install_gets_the_reconnect_sentence()
        {
            var ws = NewWorkspace();
            var first = new FakeOwner(ws, pro: false);
            FakeOwner updated = null;
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(first.Pipe, ws, timeoutMs: 1000, requireMatchingExecutable: true);
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                // The extension updated and the window reloaded: the new owner runs from the new install folder.
                first.Exit(ws);
                // Same file name as this process (the liveness check compares names), different install folder.
                var elsewhere = Path.Combine(Path.GetTempPath(), "contoso-ext-1.2.1", "engine",
                    Path.GetFileName(EngineBroker.CurrentExecutablePath()));
                updated = new FakeOwner(ws, pro: true, exePath: elsewhere);

                var message = await FailureOf(() => remote.GetEntitlementAsync());
                Assert.Contains("Semanticus was updated", message);
                Assert.Contains("Connect AI Assistant", message);
                Assert.Contains("reconnect with /mcp", message);
            }
            finally
            {
                remote?.Dispose();
                first.Dispose();
                updated?.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        /// <summary>A run given its own state folder (every test run is: SEMANTICUS_HOME is set for the assembly) refuses
        /// to rejoin an owner that came back keeping its state elsewhere, such as a window on the person's own home.
        /// The first join refuses the same owner in Program.Mcp; without this, the rejoin would take it quietly.</summary>
        [Fact]
        public async Task A_rejoin_refuses_an_owner_that_keeps_its_state_in_another_folder()
        {
            var ws = NewWorkspace();
            var first = new FakeOwner(ws, pro: false);
            FakeOwner elsewhere = null;
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(first.Pipe, ws, timeoutMs: 1000, requireMatchingExecutable: true);
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                first.Exit(ws);
                elsewhere = new FakeOwner(ws, pro: true, home: Path.Combine(Path.GetTempPath(), "someone-elses-home"));

                var message = await FailureOf(() => remote.GetEntitlementAsync());
                Assert.Contains("different state folder", message);
                Assert.Contains("nothing was changed", message);
            }
            finally
            {
                remote?.Dispose();
                first.Dispose();
                elsewhere?.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        // ---- F-241, the assistant side: join only this build ------------------------------------------------------

        /// <summary>The join rule the election and the rejoin share. The path check stays first (a different install
        /// folder is the "was updated" case); a same-path owner of another build, or one too old to say which build
        /// it is, is not joined. A process that cannot read its own build cannot judge, and joins as before.</summary>
        [Fact]
        public void The_join_rule_accepts_only_this_build_at_this_path()
        {
            var here = EngineBroker.CurrentExecutablePath();
            EngineInfo Owner(string exe, string build) => new EngineInfo { PipeName = "p", Pid = 1, ExePath = exe, BuildId = build };
            var elsewhere = Path.Combine(Path.GetTempPath(), "contoso-ext-1.2.1", "engine", Path.GetFileName(here));

            Assert.Equal(McpJoin.Join, EngineBroker.McpJoinVerdict(Owner(here, BuildIdentity.Current)));
            Assert.Equal(McpJoin.DifferentInstall, EngineBroker.McpJoinVerdict(Owner(elsewhere, BuildIdentity.Current)));
            Assert.Equal(McpJoin.DifferentBuild, EngineBroker.McpJoinVerdict(Owner(here, "an-older-build")));
            Assert.Equal(McpJoin.DifferentBuild, EngineBroker.McpJoinVerdict(Owner(here, null)));   // older than BuildId
            Assert.Equal(McpJoin.Legacy, EngineBroker.McpJoinVerdict(Owner(null, null)));           // older than ExePath
            Assert.Equal(McpJoin.Join, EngineBroker.McpJoinVerdict(Owner(here, "whatever"), expectedBuild: null));
        }

        /// <summary>Kane's in-place reinstall, seen from the assistant: two owners of the SAME path and different
        /// builds. The connection does not join the older build, never stops it (Studio's Save offer owns unsaved
        /// work there), and starts nothing over the top of it. Once that owner has gone and this build's owner is
        /// published, the next call joins it.</summary>
        [Fact]
        public async Task A_same_path_owner_of_another_build_is_not_joined_and_not_stopped()
        {
            var ws = NewWorkspace();
            var first = new FakeOwner(ws, pro: false);
            FakeOwner older = null, mine = null;
            var starts = 0;
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(first.Pipe, ws, timeoutMs: 1000, requireMatchingExecutable: true,
                    startOwner: () => { Interlocked.Increment(ref starts); return Task.FromResult(false); });
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                first.Exit(ws);
                // Same executable path as this process, different build: the in-place reinstall.
                older = new FakeOwner(ws, pro: true, exePath: EngineBroker.CurrentExecutablePath(), buildId: "an-older-build");

                var message = await FailureOf(() => remote.GetEntitlementAsync());
                Assert.Equal(RemoteEngine.DifferentBuildText, message);
                Assert.Contains("Call this tool again", message);
                Assert.Contains("reconnect with /mcp", message);
                Assert.Equal(0, starts);   // nothing started over the top of it

                // Left alone: still published and still answering its own clients.
                Assert.Equal("an-older-build", EngineBroker.ReadInfo(ws)?.BuildId);
                using (var direct = await RemoteEngine.ConnectAsync(older.Pipe))
                    Assert.Equal("pro", (await direct.GetEntitlementAsync()).Tier);

                // Studio replaced it with this build: the next call joins that owner.
                older.Exit(ws);
                mine = new FakeOwner(ws, pro: true);
                Assert.Equal("pro", (await remote.GetEntitlementAsync()).Tier);
                Assert.Equal(0, starts);
            }
            finally
            {
                remote?.Dispose();
                first.Dispose();
                older?.Dispose();
                mine?.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Fact]
        public async Task An_owner_that_is_published_but_not_answering_gets_a_plain_retry_sentence_not_a_hang()
        {
            var ws = NewWorkspace();
            var first = new FakeOwner(ws, pro: false);
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(first.Pipe, ws, timeoutMs: 1000);
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);

                // Mid-restart: a live process has published a pipe that is not listening yet.
                first.Exit(ws);
                EngineBroker.WriteInfo(ws, new EngineInfo
                {
                    PipeName = TestPipe.Mint("semanticus-silent-"),
                    Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow.ToString("o"), Workspace = ws,
                });

                var clock = Stopwatch.StartNew();
                var message = await FailureOf(() => remote.GetEntitlementAsync());
                clock.Stop();
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the answer took {clock.Elapsed.TotalSeconds:0.0}s");
                Assert.Contains("Semanticus is restarting for this folder", message);
                Assert.Contains("Call this tool again", message);
            }
            finally
            {
                remote?.Dispose();
                first.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        /// <summary>When the door cannot come up at all, Claude Code shows the server's last stderr line. It must
        /// name the fix a person can do, in the words the VS Code command palette uses.</summary>
        [Fact]
        public void Every_startup_failure_names_the_reconnect()
        {
            foreach (var message in new[]
            {
                EngineBroker.McpAttachFailureMessage(TimeSpan.FromSeconds(5), ownerAlive: true),
                EngineBroker.McpAttachFailureMessage(TimeSpan.FromSeconds(5), ownerAlive: false),
                EngineBroker.McpDifferentBuildMessage,
                RemoteEngine.DifferentBuildText,
            })
            {
                Assert.Contains("Semanticus: Connect AI Assistant", message);
                Assert.Contains("reconnect with /mcp", message);
                Assert.DoesNotContain("\u2014", message);
            }
        }

        // ---- an assistant-started owner never leaves with work in it ---------------------------------------------

        /// <summary>The grace is for a clean owner nobody is using. While the hold says "unsaved changes or a
        /// client attached", a dead parent must not end the owner, however long it takes.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_held_owner_outlives_its_grace_and_leaves_one_grace_after_the_hold_clears()
        {
            var hold = true;
            var expired = new ManualResetEventSlim(false);
            using var lifetime = new ParentLifetime(4242, () => expired.Set(),
                grace: TimeSpan.FromMilliseconds(300), poll: TimeSpan.FromMilliseconds(10),
                isAlive: _ => false,
                holdOpen: () => Volatile.Read(ref hold));

            Assert.False(expired.Wait(TimeSpan.FromMilliseconds(1500)),
                "an owner holding unsaved work or an attached client must not leave when its grace runs out");
            Assert.False(lifetime.Expired);
            var released = Stopwatch.StartNew();
            Volatile.Write(ref hold, false);
            Assert.True(expired.Wait(TimeSpan.FromSeconds(5)), "a clean owner with no clients must still leave");
            Assert.True(released.Elapsed >= TimeSpan.FromMilliseconds(250),
                $"the full grace must run again after the hold clears (left after {released.ElapsedMilliseconds} ms)");
        }

        /// <summary>The reconnect-inside-the-grace case: the assistant's connection restarts (a /mcp reconnect)
        /// after it made an unsaved edit. The owner it started must keep the edit through the grace and beyond,
        /// the new connection must land on the same owner, and the edit must still be undoable.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task A_reconnect_inside_the_grace_keeps_the_owner_and_its_unsaved_edit()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise
            var ws = NewWorkspace();
            McpClient first = null, second = null;
            EngineInfo owner = null;
            try
            {
                first = McpClient.Start(apphost, ws);   // grace shortened to 1 s for the owner it starts
                await first.InitializeAsync();
                Assert.False((await first.CallToolAsync("create_model", new JObject { ["name"] = "Contoso Sales", ["compatibilityLevel"] = 1604 })).IsError);
                var made = await first.CallToolAsync("create_table", new JObject { ["name"] = "Orders" });
                Assert.False(made.IsError, made.Text + "\n" + first.Errors);
                owner = EngineBroker.ReadInfo(ws);
                Assert.NotNull(owner);

                first.KillServerOnly();                // the connection goes away (the assistant stops it, not its tree)
                await Task.Delay(3000);                // three graces with nobody attached
                Assert.True(EngineBroker.IsAlive(owner), "the owner left while it held an unsaved edit");

                second = McpClient.Start(apphost, ws); // the reconnect
                await second.InitializeAsync();
                await Task.Delay(3000);                // and three more graces with a client attached
                Assert.Equal(owner.Pid, EngineBroker.ReadInfo(ws)?.Pid);
                var summary = await second.CallToolAsync("get_model_summary");
                Assert.False(summary.IsError, summary.Text + "\n" + second.Errors);
                Assert.Contains("\"tables\":1", summary.Text);
                var undone = await second.CallToolAsync("undo_change");
                Assert.False(undone.IsError, undone.Text);
                Assert.Contains("\"tables\":0", (await second.CallToolAsync("get_model_summary")).Text);
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                KillPid(owner?.Pid);
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        // ---- the real door, across processes ---------------------------------------------------------------------

        /// <summary>The assistant started first. The published owner must be a SEPARATE process: VS Code reclaims a
        /// workspace by stopping the published pid, and when that pid was the MCP server itself, opening Studio
        /// stopped the assistant's connection.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task The_mcp_process_never_publishes_itself_as_the_owner()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise
            var ws = NewWorkspace();
            McpClient mcp = null;
            try
            {
                mcp = McpClient.Start(apphost, ws);
                await mcp.InitializeAsync();
                var info = await WaitForAsync(() =>
                {
                    var i = EngineBroker.ReadInfo(ws);
                    return i != null && EngineBroker.IsAlive(i) ? i : null;
                }, TimeSpan.FromSeconds(30));
                Assert.NotNull(info);
                Assert.NotEqual(mcp.Pid, info.Pid);
                // Marked, so an updated VS Code can tell it from another open window and is not locked out by it.
                Assert.Equal(EngineInfo.StartedByAssistant, info.StartedBy);
                var reply = await mcp.CallToolAsync("get_entitlement");
                Assert.False(reply.IsError, "get_entitlement failed through the MCP door: " + reply.Text + "\n" + mcp.Errors);
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                mcp?.Dispose();
                KillPid(owner?.Pid);
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        /// <summary>A real owner process exits mid-session (its editor closed) and nothing replaces it. The
        /// assistant's next call must still work, through an owner the MCP process started itself.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task The_mcp_process_keeps_serving_after_its_owner_process_exits()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise
            var ws = NewWorkspace();
            Process owner = null;
            McpClient mcp = null;
            try
            {
                owner = StartOwner(apphost, ws);
                var first = await WaitForAsync(() =>
                {
                    var i = EngineBroker.ReadInfo(ws);
                    return i != null && i.Pid == owner.Id && EngineBroker.IsAlive(i) ? i : null;
                }, TimeSpan.FromSeconds(30));
                Assert.NotNull(first);

                mcp = McpClient.Start(apphost, ws);
                await mcp.InitializeAsync();
                var before = await mcp.CallToolAsync("get_entitlement");
                Assert.False(before.IsError, before.Text + "\n" + mcp.Errors);

                owner.Kill(entireProcessTree: true);
                owner.WaitForExit(10000);

                var after = await mcp.CallToolAsync("get_entitlement");
                Assert.False(after.IsError, "the call after the owner left failed: " + after.Text + "\n" + mcp.Errors);
                Assert.False(mcp.HasExited, "the MCP server died with its owner");
                var replacement = EngineBroker.ReadInfo(ws);
                Assert.NotNull(replacement);
                Assert.NotEqual(first.Pid, replacement.Pid);
                Assert.NotEqual(mcp.Pid, replacement.Pid);
            }
            finally
            {
                var current = EngineBroker.ReadInfo(ws);
                mcp?.Dispose();
                Kill(owner);
                KillPid(current?.Pid);
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        /// <summary>A minimal MCP stdio client: newline-delimited JSON-RPC, replies matched by id.</summary>
        private sealed class McpClient : IDisposable
        {
            private readonly Process _p;
            private readonly ConcurrentDictionary<int, JObject> _replies = new ConcurrentDictionary<int, JObject>();
            private readonly StringBuilder _err = new StringBuilder();
            private int _id;

            private McpClient(Process p) { _p = p; }
            public int Pid => _p.Id;
            public bool HasExited => _p.HasExited;
            public string Errors { get { lock (_err) return _err.ToString(); } }

            public static McpClient Start(string apphost, string ws)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = apphost,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in new[] { "mcp", "--workspace", ws }) psi.ArgumentList.Add(a);
                psi.Environment["DOTNET_ROOT"] = RuntimeRoot();
                psi.Environment[ParentLifetime.GraceEnvironmentVariable] = "1000";
                var client = new McpClient(Process.Start(psi));
                client._p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (client._err) client._err.AppendLine(e.Data); };
                client._p.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    try
                    {
                        var o = JObject.Parse(e.Data);
                        if (o["id"] != null && o["id"].Type == JTokenType.Integer) client._replies[o["id"].Value<int>()] = o;
                    }
                    catch { }
                };
                client._p.BeginErrorReadLine();
                client._p.BeginOutputReadLine();
                return client;
            }

            private async Task<JObject> RequestAsync(string method, JObject @params)
            {
                var id = Interlocked.Increment(ref _id);
                var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params };
                await _p.StandardInput.WriteLineAsync(msg.ToString(Newtonsoft.Json.Formatting.None));
                await _p.StandardInput.FlushAsync();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    if (_replies.TryRemove(id, out var reply)) return reply;
                    if (_p.HasExited) throw new InvalidOperationException("the MCP server exited:\n" + Errors);
                    await Task.Delay(20);
                }
                throw new TimeoutException($"no reply to {method} within 60s:\n" + Errors);
            }

            public async Task InitializeAsync()
            {
                await RequestAsync("initialize", new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "owner-loss-test", ["version"] = "0" },
                });
                var note = new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
                await _p.StandardInput.WriteLineAsync(note.ToString(Newtonsoft.Json.Formatting.None));
                await _p.StandardInput.FlushAsync();
            }

            public async Task<(bool IsError, string Text)> CallToolAsync(string name, JObject arguments = null)
            {
                var reply = await RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() });
                if (reply["error"] != null) return (true, reply["error"].ToString());
                var result = reply["result"];
                var text = string.Join("\n", result?["content"]?.Select(c => (string)c["text"]) ?? Array.Empty<string>());
                return (result?["isError"]?.Value<bool>() == true, text);
            }

            /// <summary>What Claude Code does to a server it drops: stop that process, not the owner it started.</summary>
            public void KillServerOnly()
            {
                try { if (!_p.HasExited) _p.Kill(entireProcessTree: false); } catch { }
                try { _p.WaitForExit(5000); } catch { }
            }

            public void Dispose()
            {
                try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
                try { _p.WaitForExit(5000); } catch { }
                try { _p.Dispose(); } catch { }
            }
        }

        private static Process StartOwner(string apphost, string ws)
        {
            var psi = new ProcessStartInfo
            {
                FileName = apphost,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "serve", "--workspace", ws, "--ui-challenge-stdin" }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_ROOT"] = RuntimeRoot();
            var p = Process.Start(psi);
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            p.StandardInput.WriteLine("owner-loss-ui-challenge-0123456789abcdef0123");
            p.StandardInput.Flush();
            return p;
        }

        private static string RuntimeRoot()
        {
            var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            return runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;
        }

        private static async Task<T> WaitForAsync<T>(Func<T> probe, TimeSpan timeout) where T : class
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var v = probe();
                if (v != null) return v;
                await Task.Delay(50);
            }
            return probe();
        }

        private static void Kill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p?.Dispose(); } catch { }
        }

        /// <summary>Only ever called with a pid this test's own processes published.</summary>
        private static void KillPid(int? pid)
        {
            if (pid == null || pid == Environment.ProcessId) return;
            try { using var p = Process.GetProcessById(pid.Value); p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
