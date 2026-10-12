using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The engine must not outlive the editor that started it, and must not take a reloading editor's
    /// unsaved session with it either.
    ///
    /// Kane, 2026-09-21: closing and reopening VS Code left old engines serving. Measured on that machine the
    /// same day: nine `serve` processes, all reparented to systemd --user, six from extension 1.1.1 spawned
    /// ten days earlier and two from 1.2.0 spawned seven days earlier. Reproduced here first as a failing
    /// test. A short-lived parent spawns `serve`, the parent is killed, and the engine kept running
    /// indefinitely, before <see cref="ParentWatchdog"/> existed.
    ///
    /// A dead parent then starts a GRACE rather than an exit, because a window reload restarts the extension
    /// host and C4.1 says a reload must not throw unsaved work away. A window that comes back inside the
    /// grace calls adoptParent and the session survives; a window that does not come back leaves an engine
    /// that exits by itself.
    ///
    /// The process tests are the ones that actually prove it, so they run the SHIPPED apphost, not an
    /// in-process stand-in: the defect lives in what a real process does when its real parent dies.
    /// </summary>
    /// <summary>A test that needs the built engine apphost beside the test binaries. When none is built there the
    /// runner reports it SKIPPED with the reason, never PASSED: the test used to print a skip line and return, which
    /// counted as a pass although nothing ran (N14, C4). xunit 2 has no skip from inside a test, so the attribute
    /// decides before the test starts, as PlanCommutationTests' FullCommutationFact does.</summary>
    internal sealed class EngineApphostFactAttribute : FactAttribute
    {
        public EngineApphostFactAttribute(bool unixOnly = false)
        {
            if (unixOnly && OperatingSystem.IsWindows())
                Skip = "NOT RUN: this repro uses /bin/sh as the intermediate parent, which Windows does not have. This is not a pass.";
            else if (EngineApphost.Find() == null)
                Skip = "NOT RUN: no built engine apphost beside the tests (build Semanticus.Engine first). This is not a pass.";
        }
    }

    /// <summary>The shipped apphost beside the test binaries: &lt;repo&gt;/Semanticus.Engine/bin/&lt;config&gt;/net8.0/, or
    /// null when nothing is built there. One copy, read by the attribute and by the tests it guards.</summary>
    internal static class EngineApphost
    {
        public static string Find()
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);      // .../Semanticus.Tests/bin/<config>/net8.0/
            var config = here.Parent?.Name;
            var root = here.Parent?.Parent?.Parent?.Parent?.FullName;
            if (config == null || root == null) return null;
            var name = OperatingSystem.IsWindows() ? "Semanticus.Engine.exe" : "Semanticus.Engine";
            var path = Path.Combine(root, "Semanticus.Engine", "bin", config, "net8.0", name);
            return File.Exists(path) ? path : null;
        }
    }

    public sealed class EngineLifetimeTests
    {
        private const string Challenge = "lifetime-ui-challenge-0123456789abcdef01";

        // ---- the argument and watch rules, without spawning anything ----------------------------------

        [Fact]
        public void Parent_pid_is_read_from_the_command_line_and_a_bad_value_degrades_to_absent()
        {
            Assert.Equal(4242, ParentWatchdog.ParsePid(new[] { "serve", "--parent-pid", "4242" }));
            Assert.Null(ParentWatchdog.ParsePid(new[] { "serve", "--workspace", "/tmp/x" }));
            Assert.Null(ParentWatchdog.ParsePid(new[] { "serve", "--parent-pid" }));           // no value
            Assert.Null(ParentWatchdog.ParsePid(new[] { "serve", "--parent-pid", "later" }));  // not a number
            Assert.Null(ParentWatchdog.ParsePid(new[] { "serve", "--parent-pid", "-9" }));     // not a pid
        }

        [Fact]
        public void The_explicit_parent_wins_and_an_orphaned_launch_watches_nothing()
        {
            var explicitArgs = new[] { "serve", "--parent-pid", "4242" };
            Assert.Equal(4242, ParentWatchdog.ResolveWatchTarget(explicitArgs, () => 77));

            var noArgs = new[] { "serve", "--workspace", "/tmp/x" };
            Assert.Equal(77, ParentWatchdog.ResolveWatchTarget(noArgs, () => 77));

            // init is nobody's parent to wait on, and an undiscoverable parent is not a licence to guess.
            Assert.Null(ParentWatchdog.ResolveWatchTarget(noArgs, () => 1));
            Assert.Null(ParentWatchdog.ResolveWatchTarget(noArgs, () => 0));
            Assert.Null(ParentWatchdog.ResolveWatchTarget(noArgs, () => null));
            Assert.Null(ParentWatchdog.ResolveWatchTarget(new[] { "serve", "--parent-pid", "1" }, () => 77));
        }

        [Fact]
        public void Watch_fires_once_when_the_parent_goes_and_stops_when_disposed()
        {
            var alive = true;
            var fired = new ManualResetEventSlim(false);
            var count = 0;
            using (ParentWatchdog.Watch(4242, () => { Interlocked.Increment(ref count); fired.Set(); },
                       poll: TimeSpan.FromMilliseconds(10), isAlive: _ => Volatile.Read(ref alive)))
            {
                Assert.False(fired.Wait(TimeSpan.FromMilliseconds(200)), "a live parent must not trigger the watchdog");
                Volatile.Write(ref alive, false);
                Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "the watchdog never noticed the parent was gone");
            }
            Thread.Sleep(100);
            Assert.Equal(1, Volatile.Read(ref count));
        }

        [Fact]
        public void A_disposed_watch_never_fires()
        {
            var fired = 0;
            ParentWatchdog.Watch(4242, () => Interlocked.Increment(ref fired),
                poll: TimeSpan.FromMilliseconds(10), isAlive: _ => true).Dispose();
            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref fired));
        }

        [Fact]
        public void This_process_is_alive_and_a_pid_that_cannot_exist_is_not()
        {
            Assert.True(ParentWatchdog.IsAlive(Environment.ProcessId));
            Assert.False(ParentWatchdog.IsAlive(0));
            Assert.False(ParentWatchdog.IsAlive(-1));
        }

        // ---- the grace and adoption, without spawning anything ----------------------------------------

        [Fact]
        public void A_dead_parent_starts_the_grace_and_the_engine_leaves_only_when_it_runs_out()
        {
            var alive = true;
            var expired = new ManualResetEventSlim(false);
            using var lifetime = new ParentLifetime(4242, () => expired.Set(),
                grace: TimeSpan.FromMilliseconds(400), poll: TimeSpan.FromMilliseconds(10),
                isAlive: _ => Volatile.Read(ref alive));

            Assert.False(lifetime.InGrace);
            Volatile.Write(ref alive, false);
            Assert.True(SpinFor(() => lifetime.InGrace, TimeSpan.FromSeconds(5)),
                "a dead parent must open the grace, not end the engine outright");
            Assert.False(lifetime.Expired, "the engine must still be serving while the grace is open");
            Assert.True(expired.Wait(TimeSpan.FromSeconds(5)), "the grace never ran out");
            Assert.True(lifetime.Expired);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void Adopting_inside_the_grace_cancels_the_shutdown_and_switches_the_watch()
        {
            var aliveA = true;
            var aliveB = true;
            var expired = 0;
            const int parentA = 4242;
            const int parentB = 4343;
            using var lifetime = new ParentLifetime(parentA, () => Interlocked.Increment(ref expired),
                grace: TimeSpan.FromMilliseconds(500), poll: TimeSpan.FromMilliseconds(10),
                isAlive: pid => pid == parentA ? Volatile.Read(ref aliveA) : Volatile.Read(ref aliveB));

            Volatile.Write(ref aliveA, false);
            Assert.True(SpinFor(() => lifetime.InGrace, TimeSpan.FromSeconds(5)), "the grace never opened");

            Assert.True(lifetime.Adopt(parentB), "a returning window must be able to claim the engine");
            Assert.False(lifetime.InGrace, "adoption must cancel the pending shutdown");
            Assert.Equal(parentB, lifetime.WatchedPid);

            Thread.Sleep(900);   // well past the original grace
            Assert.Equal(0, Volatile.Read(ref expired));
            Assert.False(lifetime.Expired, "the adopted engine must not shut down on the old parent's clock");

            // And it now dies with the NEW parent, on a fresh grace of its own.
            Volatile.Write(ref aliveB, false);
            Assert.True(SpinFor(() => Volatile.Read(ref expired) == 1, TimeSpan.FromSeconds(5)),
                "an adopted engine must still die when the window that adopted it goes");
        }

        [Fact]
        public void Adopt_refuses_a_pid_that_is_not_alive_and_refuses_init()
        {
            using var lifetime = new ParentLifetime(4242, () => { },
                grace: TimeSpan.FromMilliseconds(400), poll: TimeSpan.FromMilliseconds(10),
                isAlive: pid => pid == 4242);

            Assert.False(lifetime.Adopt(9999), "a claim naming a dead process must be refused");
            Assert.False(lifetime.Adopt(1), "adopting init would make the engine immortal");
            Assert.False(lifetime.Adopt(0));
            Assert.Equal(4242, lifetime.WatchedPid);
        }

        [Fact]
        public void Adopt_refuses_once_the_grace_has_already_run_out()
        {
            var expired = new ManualResetEventSlim(false);
            // The original parent is gone; any other pid answers alive, so the refusal below is about the
            // expired grace, not about the pid being dead.
            using var lifetime = new ParentLifetime(4242, () => expired.Set(),
                grace: TimeSpan.FromMilliseconds(200), poll: TimeSpan.FromMilliseconds(10),
                isAlive: pid => pid != 4242);

            Assert.True(expired.Wait(TimeSpan.FromSeconds(5)), "the grace never ran out");
            Assert.True(lifetime.Expired);
            Assert.False(lifetime.Adopt(4343), "a shutdown that is already under way cannot be taken back");
        }

        [Fact]
        public void A_healthy_parent_never_opens_the_grace()
        {
            var expired = 0;
            using var lifetime = new ParentLifetime(4242, () => Interlocked.Increment(ref expired),
                grace: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromMilliseconds(10),
                isAlive: _ => true);
            Thread.Sleep(400);
            Assert.Equal(0, Volatile.Read(ref expired));
            Assert.False(lifetime.InGrace);
            Assert.False(lifetime.Expired);
            Assert.Equal(4242, lifetime.WatchedPid);
        }

        /// <summary>CI on PR 386 aborted with the test HOST crashing, while every test that ran passed:
        /// "Unhandled exception. System.ObjectDisposedException: The CancellationTokenSource has been
        /// disposed. at CancellationTokenSource.get_WaitHandle() at ParentLifetime.ParentGone". Disposing
        /// the lifetime disposed the source the grace thread was about to wait on. It never fired locally
        /// and fired on the runner, because the runner's shortened grace made the window between publishing
        /// the source and waiting on it the whole of the race.
        ///
        /// The rule now: the grace thread OWNS the source and is the only code that disposes it; everyone
        /// else may only cancel. The loop below lands Dispose at a different point in the grace on every
        /// iteration. Before the fix this did not fail an assertion, it took the test host down.</summary>
        [Fact]
        public void Disposing_a_lifetime_while_its_grace_is_running_never_takes_the_host_down()
        {
            var unhandled = 0;
            var expiredAfterDispose = 0;
            UnhandledExceptionEventHandler onUnhandled = (_, __) => Interlocked.Increment(ref unhandled);
            AppDomain.CurrentDomain.UnhandledException += onUnhandled;
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    var alive = true;
                    var lifetime = new ParentLifetime(4242, () => Interlocked.Increment(ref expiredAfterDispose),
                        grace: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromMilliseconds(1),
                        isAlive: _ => Volatile.Read(ref alive));
                    Volatile.Write(ref alive, false);
                    Thread.Sleep(i % 7);   // a different point inside the 100ms grace every time
                    lifetime.Dispose();
                }
                Thread.Sleep(500);   // every grace thread has finished by now
            }
            finally { AppDomain.CurrentDomain.UnhandledException -= onUnhandled; }

            Assert.Equal(0, Volatile.Read(ref unhandled));
            // Every dispose above lands inside the grace, so no lifetime may reach its expiry: a disposed
            // lifetime shutting the engine down would be the same defect wearing a different hat.
            Assert.Equal(0, Volatile.Read(ref expiredAfterDispose));
        }

        /// <summary>The same ownership rule on the watch itself: disposing the watch while its poll is in
        /// flight must not throw out of the watch thread either.</summary>
        [Fact]
        public void Disposing_a_watch_mid_poll_never_takes_the_host_down()
        {
            var unhandled = 0;
            UnhandledExceptionEventHandler onUnhandled = (_, __) => Interlocked.Increment(ref unhandled);
            AppDomain.CurrentDomain.UnhandledException += onUnhandled;
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    var watch = ParentWatchdog.Watch(4242, () => { },
                        poll: TimeSpan.FromMilliseconds(1), isAlive: _ => true);
                    Thread.Sleep(i % 3);
                    watch.Dispose();
                }
                Thread.Sleep(300);
            }
            finally { AppDomain.CurrentDomain.UnhandledException -= onUnhandled; }
            Assert.Equal(0, Volatile.Read(ref unhandled));
        }

        [Fact]
        public void The_grace_defaults_to_twenty_seconds_and_the_test_hook_can_only_shorten_it()
        {
            Assert.Equal(20_000, ParentLifetime.DefaultGraceMs);
            var previous = Environment.GetEnvironmentVariable(ParentLifetime.GraceEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(ParentLifetime.GraceEnvironmentVariable, "1000");
                using (var shortened = new ParentLifetime(null, () => { }))
                    Assert.Equal(TimeSpan.FromMilliseconds(1000), shortened.Grace);

                // Out of range in either direction falls back to the shipped default, so a stray value cannot
                // disable the watchdog by making the grace enormous, nor spin it by making it microscopic.
                foreach (var bad in new[] { "0", "50", "600000", "-1", "soon", "" })
                {
                    Environment.SetEnvironmentVariable(ParentLifetime.GraceEnvironmentVariable, bad);
                    using var fallback = new ParentLifetime(null, () => { });
                    Assert.Equal(TimeSpan.FromMilliseconds(ParentLifetime.DefaultGraceMs), fallback.Grace);
                }
            }
            finally { Environment.SetEnvironmentVariable(ParentLifetime.GraceEnvironmentVariable, previous); }
        }

        // ---- the published record names who the engine dies with --------------------------------------

        [Fact]
        public void The_parent_pid_is_published_and_re_stamped_only_on_our_own_record()
        {
            using var ws = new TempDir("smx-parentpid-");
            EngineBroker.WriteInfo(ws.Path, new EngineInfo
            {
                PipeName = "semanticus-test",
                Pid = Environment.ProcessId,
                StartedUtc = DateTime.UtcNow.ToString("o"),
                Workspace = ws.Path,
                ParentPid = 4242,
            });
            Assert.Equal(4242, EngineBroker.ReadInfo(ws.Path)?.ParentPid);

            Assert.True(EngineBroker.UpdateParentPid(ws.Path, Environment.ProcessId, 4343));
            Assert.Equal(4343, EngineBroker.ReadInfo(ws.Path)?.ParentPid);

            // A record naming a DIFFERENT engine belongs to another owner and must not be rewritten.
            Assert.False(EngineBroker.UpdateParentPid(ws.Path, Environment.ProcessId + 1, 9999));
            Assert.Equal(4343, EngineBroker.ReadInfo(ws.Path)?.ParentPid);
        }

        [Fact]
        public void An_old_record_with_no_parent_pid_reads_as_null_not_zero()
        {
            using var ws = new TempDir("smx-oldrecord-");
            var dir = Path.Combine(ws.Path, ".semanticus");
            Directory.CreateDirectory(dir);
            // Exactly the shape an engine from before the parent watch published. "No parent recorded" must
            // never arrive at a reader as 0, which would look like a pid and read as a dead one.
            File.WriteAllText(Path.Combine(dir, "engine.json"),
                "{\"PipeName\":\"semanticus-old\",\"Pid\":4242,\"Workspace\":\"" + ws.Path.Replace("\\", "\\\\") + "\"}");
            var info = EngineBroker.ReadInfo(ws.Path);
            Assert.NotNull(info);
            Assert.Null(info.ParentPid);
        }

        // ---- a test that ran nothing is Skipped, never Passed (N14, C4) --------------------------------------

        /// <summary>A test that prints "skipped" and returns is counted as PASSED by the runner although it proved
        /// nothing. Every such guard is an attribute that sets Skip before the test starts (the pattern of
        /// PlanCommutationTests' FullCommutationFact), so the run reports it as not run. This reads the test
        /// sources, because the defect is a shape in the source that no single run can see.</summary>
        [Fact]
        public void No_test_prints_a_skip_and_returns_as_a_pass()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            var early = new System.Text.RegularExpressions.Regex(
                @"Console\.WriteLine\([^;]*(?i:skip)[^;]*\);\s*return;");
            var files = Directory.GetFiles(Path.Combine(dir!.FullName, "Semanticus.Tests"), "*.cs");
            Assert.True(files.Length > 100, "the scan read only " + files.Length + " files");
            var found = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in early.Matches(text))
                    found.Add(Path.GetFileName(file) + ":" + (text.Substring(0, m.Index).Split('\n').Length));
            }
            Assert.True(found.Count == 0, "tests that return early and pass: " + string.Join(", ", found));
        }

        // ---- the defect itself, across real processes -------------------------------------------------

        [Fact]
        public async Task Owner_lifetime_ignores_a_Jev_marker_but_keeps_the_persons_edits()
        {
            using var sessions = new SessionManager();
            using var session = await sessions.BuildCreateAsync("Sales", 1604);
            Assert.False(session.HasUnsavedEdits);
            await session.ReadAsync(_ => { session.MarkConsentMarkerDirty(); return true; });
            Assert.True(session.HasUnsavedChanges);
            Assert.False(session.HasUnsavedEdits);
            Assert.False(Program.KeepOwnerAlive(false, 0, session), "a Jev marker is not unsaved work");
            Assert.False(Program.KeepOwnerAlive(true, 0, session));
            Assert.True(Program.KeepOwnerAlive(true, 1, session), "an assistant connection still keeps its owner alive");
            await session.MutateAsync("human", "Add Sales", model => model.AddTable("Sales"));
            Assert.True(session.HasUnsavedEdits);
            Assert.True(Program.KeepOwnerAlive(false, 0, session), "the person's work still survives a reload");
        }

        [EngineApphostFact]
        public async Task An_editor_owner_keeps_dirty_work_after_the_window_closes_without_an_unsaved_tab()
        {
            var apphost = EngineApphost.Find();
            using var workspace = new TempDir("smx-dirty-");
            using var parentDir = new TempDir("smx-dirty-parent-");
            var parent = StartSleeper(apphost, parentDir.Path);
            Process engine = null;
            HumanClient client = null;
            try
            {
                engine = StartEngine(apphost, new[] { "serve", "--workspace", workspace.Path, "--ui-challenge-stdin", ParentWatchdog.Option, parent.Id.ToString() }, graceMs: 300);
                engine.StandardInput.WriteLine(Challenge);
                engine.StandardInput.Flush();
                var info = await WaitForOwnerAsync(workspace.Path, engine);
                client = await HumanClient.ConnectAsync(info);
                await client.InvokeAsync<OpenResult>("createModel", "Sales", 1604, false);
                await client.InvokeAsync<string>("createTable", "Sales");
                Assert.True((await client.InvokeAsync<SessionInfo>("sessionInfo")).HasUnsavedEdits);
                client.Dispose(); client = null;
                parent.Kill(entireProcessTree: true); parent.WaitForExit(10000);
                await Task.Delay(2500);
                Assert.False(engine.HasExited, "unsaved work must stay available after the editor closes");
                client = await HumanClient.ConnectAsync(EngineBroker.ReadInfo(workspace.Path));
                Assert.True(await client.AdoptAsync(Environment.ProcessId));
                Assert.Equal(1, (await client.InvokeAsync<SessionInfo>("sessionInfo")).Tables);
            }
            finally { client?.Dispose(); Kill(engine); Kill(parent); }
        }

        /// <summary>THE REPRODUCTION. An engine whose spawning process dies, and whose window never comes
        /// back, must go. Before the watchdog it was still listening long after its parent was killed, which
        /// is exactly the pile of orphans Kane found.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task Engine_exits_within_thirty_seconds_of_the_process_that_spawned_it_dying()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise

            using var workspace = new TempDir("smx-life-");
            using var parentDir = new TempDir("smx-life-parent-");
            var parent = StartSleeper(apphost, parentDir.Path);
            Process engine = null;
            var err = new StringBuilder();
            try
            {
                engine = StartEngine(apphost, new[]
                {
                    "serve", "--workspace", workspace.Path, "--ui-challenge-stdin",
                    ParentWatchdog.Option, parent.Id.ToString(),
                }, graceMs: 1000, onErr: line => { lock (err) err.AppendLine(line); });
                engine.StandardInput.WriteLine(Challenge);
                engine.StandardInput.Flush();
                await WaitForOwnerAsync(workspace.Path, engine);

                // Kill AND reap: an unreaped child is a zombie, which still answers a pid-existence probe. The
                // real case does not need this (the engine's own parent slot changes when VS Code goes), but a
                // test that leaves a zombie behind would be measuring the test harness, not the engine.
                parent.Kill(entireProcessTree: true);
                parent.WaitForExit(10000);

                Assert.True(engine.WaitForExit(30000),
                    "the engine outlived the process that spawned it: this is the orphan Kane found.");
                lock (err)
                {
                    Assert.Contains("holding this session", err.ToString(), StringComparison.Ordinal);
                    Assert.Contains("no window came back within the grace", err.ToString(), StringComparison.Ordinal);
                }
                // The record goes with it, so the next window never attaches to a pipe that stopped listening.
                Assert.Null(EngineBroker.ReadInfo(workspace.Path));
            }
            finally
            {
                Kill(engine);
                Kill(parent);
            }
        }

        /// <summary>The C4.1 case. The parent dies, a new process adopts inside the grace, and the engine
        /// keeps its session. A window reload is exactly this: the extension host restarts, reconnects to the
        /// same pipe, and calls adoptParent.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task Engine_adopted_inside_the_grace_survives_and_later_dies_with_its_new_parent()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise

            using var workspace = new TempDir("smx-adopt-");
            using var oldDir = new TempDir("smx-adopt-old-");
            using var newDir = new TempDir("smx-adopt-new-");
            var oldParent = StartSleeper(apphost, oldDir.Path);
            var newParent = StartSleeper(apphost, newDir.Path);
            Process engine = null;
            HumanClient client = null;
            try
            {
                engine = StartEngine(apphost, new[]
                {
                    "serve", "--workspace", workspace.Path, "--ui-challenge-stdin",
                    ParentWatchdog.Option, oldParent.Id.ToString(),
                }, graceMs: 8000);
                engine.StandardInput.WriteLine(Challenge);
                engine.StandardInput.Flush();
                var info = await WaitForOwnerAsync(workspace.Path, engine);

                // The published record names who this engine dies with. A later window reads it to tell an
                // engine whose editor is still open from one whose editor is gone; without it, a VS Code
                // update would make the next window stop this one and discard its unsaved work (Codex P1).
                Assert.Equal(oldParent.Id, info.ParentPid);

                // Connect BEFORE the parent dies, the way a window that is still open would be connected.
                client = await HumanClient.ConnectAsync(info);

                oldParent.Kill(entireProcessTree: true);
                oldParent.WaitForExit(10000);

                // Wait past the watchdog's one-second poll FIRST, so the engine has actually noticed and is
                // sitting in its grace. Adopting inside the poll window would pass even with no grace at all,
                // which is how an earlier version of this test proved nothing.
                await Task.Delay(TimeSpan.FromMilliseconds(2500));
                Assert.False(engine.HasExited, "the engine must still be serving inside its grace");

                // The record must STAY published through the grace: a returning window has to find this pipe.
                Assert.Equal(info.Pid, EngineBroker.ReadInfo(workspace.Path)?.Pid);

                Assert.False(await client.AdoptAsync(oldParent.Id),
                    "a claim naming the process that just died must be refused");
                Assert.True(await client.AdoptAsync(newParent.Id),
                    "a returning window must be able to claim the engine inside the grace");

                // ...and the record follows the adoption, or a third window would judge this engine by a
                // window that has already gone and stop a session that is very much alive.
                Assert.True(await WaitForAsync(() => EngineBroker.ReadInfo(workspace.Path)?.ParentPid == newParent.Id,
                    TimeSpan.FromSeconds(5)), "engine.json must be re-stamped with the adopting window's pid");

                // Well past the original grace. The session is still here.
                await Task.Delay(TimeSpan.FromSeconds(12));
                Assert.False(engine.HasExited, "an adopted engine must not shut down on the old parent's clock");
                Assert.Equal(info.Pid, EngineBroker.ReadInfo(workspace.Path)?.Pid);

                // And it still dies with whoever owns it now.
                newParent.Kill(entireProcessTree: true);
                newParent.WaitForExit(10000);
                Assert.True(engine.WaitForExit(30000),
                    "an adopted engine must still die when the window that adopted it goes");
            }
            finally
            {
                client?.Dispose();
                Kill(engine);
                Kill(oldParent);
                Kill(newParent);
            }
        }

        /// <summary>A watchdog that fired on a LIVE parent would close the editor's engine under it, and a
        /// grace that opened on a healthy parent would be the first half of that. The parent here is the test
        /// host, which is not going anywhere.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task Engine_with_a_live_parent_keeps_serving_and_never_opens_the_grace()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise

            using var workspace = new TempDir("smx-live-");
            Process engine = null;
            var err = new StringBuilder();
            try
            {
                engine = StartEngine(apphost, new[]
                {
                    "serve", "--workspace", workspace.Path, "--ui-challenge-stdin",
                    ParentWatchdog.Option, Environment.ProcessId.ToString(),
                }, graceMs: 1000, onErr: line => { lock (err) err.AppendLine(line); });
                engine.StandardInput.WriteLine(Challenge);
                engine.StandardInput.Flush();
                var info = await WaitForOwnerAsync(workspace.Path, engine);

                // Several poll cycles (the probe runs once a second) plus several graces' worth of time.
                await Task.Delay(TimeSpan.FromSeconds(5));
                Assert.False(engine.HasExited, "the watchdog killed an engine whose parent is alive");
                Assert.Equal(info.Pid, EngineBroker.ReadInfo(workspace.Path)?.Pid);
                lock (err)
                    Assert.DoesNotContain("holding this session", err.ToString(), StringComparison.Ordinal);
            }
            finally { Kill(engine); }
        }

        /// <summary>A launch with no --parent-pid (an older extension, or a hand-run engine) follows its OS
        /// parent instead. This is the shell-grandparent shape of the original repro. Unix only: the
        /// intermediate process here is /bin/sh.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact(unixOnly: true)]
        public async Task Engine_without_an_explicit_parent_pid_follows_its_os_parent()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise

            using var workspace = new TempDir("smx-osparent-");
            var challengeFile = Path.Combine(workspace.Path, "challenge.txt");
            File.WriteAllText(challengeFile, Challenge + "\n");
            // sh starts the engine as its own child and then waits. Killing sh reparents the engine, which is
            // precisely what happened when Kane closed VS Code.
            var script = Quote(apphost) + " serve --workspace " + Quote(workspace.Path)
                + " --ui-challenge-stdin < " + Quote(challengeFile) + " & sleep 120";
            Process shell = null;
            try
            {
                var psi = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false, RedirectStandardError = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                psi.Environment["DOTNET_ROOT"] = RuntimeRoot();
                psi.Environment[ParentLifetime.GraceEnvironmentVariable] = "1000";
                shell = Process.Start(psi);
                Assert.NotNull(shell);
                shell.BeginErrorReadLine();   // the engine inherits this pipe; an undrained one would stall it

                var info = await WaitForOwnerAsync(workspace.Path, shell);

                shell.Kill(entireProcessTree: false);   // only the shell: the engine must die of the ORPHANING
                shell.WaitForExit(10000);

                Assert.True(await WaitForAsync(() => ProcessGone(info.Pid), TimeSpan.FromSeconds(30)),
                    "an engine launched without --parent-pid must still exit when its OS parent goes away");
            }
            finally { Kill(shell); }
        }

        /// <summary>Door two is untouched. The MCP process is spawned by the assistant and dies with its
        /// stdio; giving it a second lifetime rule could only end a healthy agent session early. It must
        /// ignore --parent-pid even when one is passed.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [EngineApphostFact]
        public async Task Mcp_door_ignores_parent_pid()
        {
            var apphost = EngineApphost.Find();   // present: EngineApphostFact skips the test otherwise

            using var workspace = new TempDir("smx-mcp-life-");
            using var parentDir = new TempDir("smx-mcp-parent-");
            var parent = StartSleeper(apphost, parentDir.Path);
            Process agent = null;
            var err = new StringBuilder();
            try
            {
                agent = StartEngine(apphost,
                    new[] { "mcp", "--workspace", workspace.Path, ParentWatchdog.Option, parent.Id.ToString() },
                    graceMs: 1000,
                    onErr: line => { lock (err) err.AppendLine(line); });
                var serving = await WaitForAsync(() => { lock (err) return err.ToString().Contains("serving Semanticus tools over stdio", StringComparison.Ordinal); },
                    TimeSpan.FromSeconds(30));
                lock (err) Assert.True(serving, "the MCP door never came up. Its output was:\n" + err);

                parent.Kill(entireProcessTree: true);
                parent.WaitForExit(10000);

                // Well past the shortened grace plus the exit delay. The agent door must not have noticed.
                await Task.Delay(TimeSpan.FromSeconds(6));
                Assert.False(agent.HasExited, "the MCP door honoured --parent-pid; it must die with its stdio only");
            }
            finally
            {
                Kill(agent);
                Kill(parent);
            }
        }

        [EngineApphostFact]
        public async Task Killed_engine_keeps_file_edits_for_restore_on_reopen()
        {
            using var workspace = new TempDir("smx-crash-");
            var state = Path.Combine(workspace.Path, "state");
            var path = Path.Combine(workspace.Path, "Sales.bim");
            File.Copy(TestModels.FindBim(), path);
            var original = File.ReadAllText(path);
            Process engine = null;
            HumanClient client = null;
            try
            {
                engine = StartEngine(EngineApphost.Find(), new[] { "serve", "--workspace", workspace.Path,
                    "--state-dir", state, "--ui-challenge-stdin", ParentWatchdog.Option, Environment.ProcessId.ToString() });
                engine.StandardInput.WriteLine(Challenge);
                engine.StandardInput.Flush();
                client = await HumanClient.ConnectAsync(await WaitForOwnerAsync(workspace.Path, engine));
                await client.InvokeAsync<OpenResult>("open", path);
                await client.InvokeAsync<SetResult>("setDescription", "table:Product", "survives a kill", "human");
                Assert.True(await WaitForAsync(() => Directory.Exists(Path.Combine(state, "restore")) &&
                    Directory.GetFiles(Path.Combine(state, "restore"), "unsaved.json", SearchOption.AllDirectories).Length == 1,
                    TimeSpan.FromSeconds(8)), "the debounce never wrote a recovery copy before the crash");
                var copyPath = Directory.GetFiles(Path.Combine(state, "restore"), "unsaved.json", SearchOption.AllDirectories)[0];
                var kept = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(copyPath));
                engine.Kill(entireProcessTree: true);
                engine.WaitForExit(10000);
                client.Dispose(); client = null;
                engine.Dispose(); engine = null;
                engine = StartEngine(EngineApphost.Find(), new[] { "serve", "--workspace", workspace.Path,
                    "--state-dir", state, "--ui-challenge-stdin", ParentWatchdog.Option, Environment.ProcessId.ToString() });
                engine.StandardInput.WriteLine(Challenge);
                engine.StandardInput.Flush();
                Assert.True(await WaitForAsync(() => EngineBroker.ReadInfo(workspace.Path)?.Pid == engine.Id,
                    TimeSpan.FromSeconds(10)), "the replacement engine never published its own owner record");
                client = await HumanClient.ConnectAsync(await WaitForOwnerAsync(workspace.Path, engine));
                var opened = await client.InvokeAsync<OpenResult>("open", path);
                var info = await client.InvokeAsync<Newtonsoft.Json.Linq.JObject>("sessionInfo");
                Assert.NotNull(info["fileRecovery"]);
                await client.InvokeAsync<bool>("resolveFileRecovery", opened.SessionId, true);
                var obj = await client.InvokeAsync<ObjectInfo>("getObject", "table:Product");
                Assert.Contains("survives a kill", Newtonsoft.Json.JsonConvert.SerializeObject(obj));
                Assert.True((await client.InvokeAsync<SessionInfo>("sessionInfo")).HasUnsavedEdits);
                Assert.Equal(original, File.ReadAllText(path));
                Assert.True(await WaitForAsync(() => Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(copyPath))["capturedUtc"]?.ToString()
                    != kept["capturedUtc"]?.ToString(), TimeSpan.FromSeconds(8)), "the restored working model never reached recovery");
                var restored = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(copyPath));
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.Parse(kept["bimJson"]!.ToString()),
                    Newtonsoft.Json.Linq.JToken.Parse(restored["bimJson"]!.ToString())), "the model after the killed engine was not restored exactly");
                Assert.Equal(0, (await client.InvokeAsync<UndoState>("undo", "human")).Steps);
                Assert.False((await client.InvokeAsync<SessionInfo>("sessionInfo")).HasUnsavedEdits);
            }
            finally { client?.Dispose(); Kill(engine); }
        }

        [EngineApphostFact]
        public async Task Closing_the_editor_flushes_file_recovery_before_the_engine_exits()
        {
            using var workspace = new TempDir("smx-close-");
            using var parentDir = new TempDir("smx-close-parent-");
            var apphost = EngineApphost.Find();
            var parent = StartSleeper(apphost, parentDir.Path);
            var state = Path.Combine(workspace.Path, "state");
            var path = Path.Combine(workspace.Path, "Sales.bim");
            File.Copy(TestModels.FindBim(), path);
            Process engine = null;
            HumanClient client = null;
            try
            {
                engine = StartEngine(apphost, new[] { "serve", "--workspace", workspace.Path, "--state-dir", state,
                    "--ui-challenge-stdin", ParentWatchdog.Option, parent.Id.ToString() }, graceMs: 300);
                engine.StandardInput.WriteLine(Challenge); engine.StandardInput.Flush();
                client = await HumanClient.ConnectAsync(await WaitForOwnerAsync(workspace.Path, engine));
                await client.InvokeAsync<OpenResult>("open", path);
                await client.InvokeAsync<SetResult>("setDescription", "table:Product", "flushed on exit", "human");
                client.Dispose(); client = null;
                parent.Kill(entireProcessTree: true); parent.WaitForExit(10000);
                Assert.True(engine.WaitForExit(6000), "a file owner must flush recovery and exit after the window closes");
                var copy = Assert.Single(Directory.GetFiles(Path.Combine(state, "restore"), "unsaved.json", SearchOption.AllDirectories));
                Assert.Contains("flushed on exit", File.ReadAllText(copy));
            }
            finally { client?.Dispose(); Kill(engine); Kill(parent); }
        }

        // ---- helpers ----------------------------------------------------------------------------------

        private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        private static bool SpinFor(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        /// <summary>A killable stand-in for the editor. The engine blocks forever reading its challenge from a
        /// stdin nobody writes to, which makes the shipped binary a portable, well-behaved sleeper process.</summary>
        private static Process StartSleeper(string apphost, string workspace) =>
            StartEngine(apphost, new[] { "serve", "--workspace", workspace, "--ui-challenge-stdin" });

        /// <summary>The shipped apphost beside the test binaries:
        /// &lt;repo&gt;/Semanticus.Engine/bin/&lt;config&gt;/net8.0/. Null when nothing is built there.</summary>
        private static Process StartEngine(string apphost, string[] args, int? graceMs = null, Action<string> onErr = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = apphost,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // An apphost resolves its runtime from DOTNET_ROOT. Point it at the runtime running THIS test.
            psi.Environment["DOTNET_ROOT"] = RuntimeRoot();
            // The test hook. A real twenty-second grace per case is not a test; it can only shorten, so what
            // is being measured is still the shipped rule.
            if (graceMs.HasValue) psi.Environment[ParentLifetime.GraceEnvironmentVariable] = graceMs.Value.ToString();
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + apphost);
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onErr?.Invoke(e.Data); };
            p.BeginErrorReadLine();   // drained: a full stderr pipe would stall the child
            return p;
        }

        private static string RuntimeRoot()
        {
            var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            return runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;   // <root>/shared/<framework>/<ver>
        }

        private static async Task<EngineInfo> WaitForOwnerAsync(string workspace, Process launcher)
        {
            for (var i = 0; i < 600; i++)
            {
                if (launcher.HasExited) throw new InvalidOperationException("the launching process exited during startup");
                var info = EngineBroker.ReadInfo(workspace);
                if (info != null && info.Pid > 0 && !string.IsNullOrEmpty(info.PipePath)
                    && (OperatingSystem.IsWindows() || File.Exists(info.PipePath)))
                    return info;
                await Task.Delay(50);
            }
            throw new TimeoutException("the engine never published its pipe for " + workspace);
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(50);
            }
            return condition();
        }

        /// <summary>Gone according to the OS, checked WITHOUT the probe under test.</summary>
        private static bool ProcessGone(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return p.HasExited; }
            catch (ArgumentException) { return true; }
        }

        private static void Kill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p?.Dispose(); } catch { }
        }

        /// <summary>The UI's side of the wire, the way the extension speaks it: the one-line role preamble
        /// with the challenge, then JSON-RPC. adoptParent is registered only on a HUMAN connection, so an
        /// agent client could not reach it even if it tried.</summary>
        private sealed class HumanClient : IDisposable
        {
            private readonly Stream _pipe;
            private readonly JsonRpc _rpc;

            private HumanClient(Stream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            public static async Task<HumanClient> ConnectAsync(EngineInfo info)
            {
                var pipe = await ConnectTransportAsync(info);
                await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, Challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new HumanClient(pipe, rpc);
            }

            public Task<bool> AdoptAsync(int pid) => _rpc.InvokeAsync<bool>("adoptParent", pid);
            public Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }

            /// <summary>On Unix the short pipe name is not the address, so the owner's stamped path is opened
            /// directly (D-118). Windows uses the pipe namespace.</summary>
            private static async Task<Stream> ConnectTransportAsync(EngineInfo info)
            {
                if (OperatingSystem.IsWindows())
                {
                    var client = new NamedPipeClientStream(".", info.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await client.ConnectAsync(5000);
                    return client;
                }
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(info.PipePath), cts.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir(string prefix)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path);
            }
            public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
        }
    }
}
