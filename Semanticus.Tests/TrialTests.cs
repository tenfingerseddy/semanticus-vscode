using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The free Pro month (Kane, 11 Oct 2026, option B: "add a try pro for free option that enables pro for a month
    /// and then switches off"). A trial token is the normal signed Pro token with <c>trial: true</c>. It gets no grace: the
    /// moment it ends the session is Free, without a restart, and nothing already running is cut off. The tally the Studio
    /// shows during the month and on the end card is counted on this computer from what really happened.</summary>
    [Collection("restore-root")]
    public sealed class TrialTests : IDisposable
    {
        private const string Challenge = "trial-test-challenge-0123456789abcdef0123";
        private readonly string _root;
        private readonly string _previousRoot;
        private readonly string _pub;
        private readonly string _priv;
        // Whole seconds, from the real clock: an engine arms its timer from the real clock before a test hands it a fake
        // one, so a trial built around the real now cannot end behind the test's back.
        private readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        public TrialTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sem-trial-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            _previousRoot = ConnectionRegistry.RootOverride;
            ConnectionRegistry.RootOverride = _root;
            (_pub, _priv) = LicenseVerifier.GenerateKeyPair();
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _previousRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private DateTimeOffset End => _now.AddDays(30);

        private string TrialToken(DateTimeOffset? iat = null, DateTimeOffset? exp = null, string sid = "trial_0123456789abcdef01234567") =>
            LicenseVerifier.Mint(_priv, new LicenseClaims
            {
                Sub = "trial@contoso.com", Tier = "pro", Sid = sid, Trial = true,
                Iat = (iat ?? _now).ToUnixTimeSeconds(), Exp = (exp ?? End).ToUnixTimeSeconds(),
            });

        private string PaidToken() =>
            LicenseVerifier.Mint(_priv, new LicenseClaims { Sub = "buyer@contoso.com", Tier = "pro", Iat = _now.ToUnixTimeSeconds(), Exp = _now.AddDays(365).ToUnixTimeSeconds() });

        private static string PayloadJson(string token) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(Pad(token.Substring(0, token.IndexOf('.')).Replace('-', '+').Replace('_', '/'))));

        private static string Pad(string s) => (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };

        /// <summary>A trial engine whose clock the test moves.</summary>
        private (LocalEngine Engine, SessionManager Sessions, Func<DateTimeOffset> Read, Action<DateTimeOffset> Set) TrialEngine(string token = null, string workspace = null)
        {
            var at = _now;
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, LicenseEntitlement.Evaluate(token ?? TrialToken(), _pub, devPro: false, _now), workspace);
            engine.UseLicenseEvaluatorForTest(t => LicenseEntitlement.Evaluate(t, _pub, devPro: false, at));
            engine.UseClockForTest(() => at);
            return (engine, sessions, () => at, v => at = v);
        }

        // ---- the token -----------------------------------------------------------------------------------------------

        [Fact]
        public void A_trial_token_signs_the_trial_claim_and_every_other_token_stays_byte_identical()
        {
            Assert.Contains("\"trial\":true", PayloadJson(TrialToken()));
            var paid = PaidToken();
            Assert.DoesNotContain("trial", PayloadJson(paid));
            Assert.True(LicenseVerifier.VerifySignature(TrialToken(), _pub).Trial);
            Assert.False(LicenseVerifier.VerifySignature(paid, _pub).Trial);
        }

        // ---- the policy ----------------------------------------------------------------------------------------------

        [Fact]
        public void A_running_trial_is_Pro_and_says_when_it_ends_with_days_left_rounded_up()
        {
            var e = LicenseEntitlement.Evaluate(TrialToken(), _pub, devPro: false, _now.AddHours(1));
            Assert.True(e.IsPro);
            Assert.Equal("pro", e.Info.Tier);
            Assert.True(e.Info.IsTrial);
            Assert.False(e.Info.TrialEnded);
            Assert.Equal(End.ToUnixTimeSeconds(), e.Info.TrialEndsAt);
            Assert.Equal(30, e.Info.TrialDaysLeft);   // 29 days 23 hours is 30 days, rounded up
            Assert.Equal(1, LicenseEntitlement.Evaluate(TrialToken(), _pub, false, End.AddSeconds(-1)).Info.TrialDaysLeft);
        }

        [Fact]
        public void A_trial_gets_no_grace_one_second_past_its_end()
        {
            var end = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
            var e = LicenseEntitlement.Evaluate(TrialToken(end.AddDays(-30), end), _pub, devPro: false, end.AddSeconds(1));
            Assert.False(e.IsPro);
            Assert.Equal("free", e.Info.Tier);
            Assert.True(e.Info.TrialEnded);
            Assert.False(e.Info.IsTrial);
            Assert.Equal(0, e.Info.TrialDaysLeft);
            Assert.Equal(end.ToUnixTimeSeconds(), e.Info.TrialEndsAt);
            Assert.Equal("Your Pro trial ended on 12 Nov 2026. Your work is untouched.", e.Info.Reason);
            Assert.True(LicenseEntitlement.Evaluate(TrialToken(end.AddDays(-30), end), _pub, false, end).IsPro);   // the end second itself is still Pro
        }

        [Fact]
        public void A_paid_licence_keeps_its_grace_and_carries_no_trial_fields()
        {
            var e = LicenseEntitlement.Evaluate(PaidToken(), _pub, devPro: false, _now.AddDays(366));
            Assert.True(e.IsPro);
            Assert.False(e.Info.IsTrial);
            Assert.False(e.Info.TrialEnded);
            Assert.Equal(0, e.Info.TrialEndsAt);
            Assert.Equal(0, e.Info.TrialDaysLeft);
        }

        // ---- the end, while the window is open -------------------------------------------------------------------------

        [Fact]
        public async Task A_window_left_open_drops_to_free_at_the_end_without_a_restart()
        {
            var (engine, sessions, _, set) = TrialEngine();
            using (sessions)
            using (engine)
            {
                set(End.AddSeconds(-10));
                Assert.Equal("pro", (await engine.GetEntitlementAsync()).Tier);
                Assert.Equal(1, (await engine.GetEntitlementAsync()).TrialDaysLeft);   // counted at the engine's now, not at start
                Assert.Equal(1, (await engine.GetOrientationAsync()).Entitlement.TrialDaysLeft);
                await engine.ListWorkflowsAsync();   // a Pro door, open

                set(End.AddSeconds(1));   // no timer has fired: the gate itself reads the clock
                await Assert.ThrowsAsync<EntitlementException>(() => engine.ListWorkflowsAsync());
                var info = await engine.GetEntitlementAsync();
                Assert.Equal("free", info.Tier);
                Assert.True(info.TrialEnded);
                Assert.Empty(info.Features);
                var summary = (await engine.GetOrientationAsync()).Entitlement;   // get_model_summary's entitlement part
                Assert.True(summary.TrialEnded);
                Assert.Null(summary.TrialDaysLeft);
                Assert.StartsWith("Your Pro trial ended on ", summary.Reason);
            }
        }

        [Fact]
        public void The_timer_is_armed_for_the_end_instant()
        {
            var (engine, sessions, _, set) = TrialEngine();
            using (sessions)
            using (engine)
            {
                set(End.AddSeconds(-10));
                engine.FireEntitlementTimerForTest();   // not due yet: it re-arms for what is left
                var due = engine.EntitlementTimerDueForTest;
                Assert.NotNull(due);
                Assert.InRange(due.Value, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.5));
            }
        }

        [Fact]
        public async Task At_the_end_the_Studio_is_told_and_the_assistant_reads_free_on_its_next_call()
        {
            var (engine, sessions, _, set) = TrialEngine();
            var pipeName = TestPipe.Mint("semanticus-trial-");
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            var heard = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            NamedPipeClientStream pipe = null;
            JsonRpc studio = null;
            RemoteEngine agent = null;
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, Challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                studio = new JsonRpc(RpcServer.CreateHandler(pipe));
                studio.AddLocalRpcMethod("entitlement/didChange", new Action<JObject>(n => heard.TrySetResult(n)));
                studio.StartListening();
                agent = await RemoteEngine.ConnectAsync(pipeName);
                Assert.True((await McpTools.GetEntitlement(agent)).IsTrial);

                set(End.AddSeconds(1));
                engine.FireEntitlementTimerForTest();

                var told = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("free", (string)told["tier"]);
                Assert.True((bool)told["trialEnded"]);
                var next = await McpTools.GetEntitlement(agent);
                Assert.Equal("free", next.Tier);
                Assert.True(next.TrialEnded);
            }
            finally
            {
                agent?.Dispose();
                studio?.Dispose();
                pipe?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                engine.Dispose();
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task The_end_drops_the_hosted_service_for_new_asks_and_cancels_nothing_already_asked()
        {
            var (engine, sessions, _, set) = TrialEngine();
            using (sessions)
            using (engine)
            {
                var running = new Semanticus.Engine.Decisions.DecisionService(Semanticus.Decisions.NullDecisionOracle.Instance);
                engine.UseDecisionsForTest(running);
                set(End.AddSeconds(1));
                Assert.Equal("free", (await engine.GetEntitlementAsync()).Tier);
                Assert.False(running.Retired);   // a request already on its way finishes
                Assert.NotSame(running, engine.Decisions);   // a new ask gets a service built for Free
            }
        }

        // ---- the tally -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_tally_counts_true_changes_and_steps_ignores_undo_and_survives_a_restart()
        {
            var token = TrialToken();
            var (engine, sessions, _, _) = TrialEngine(token);
            TrialTally before;
            using (sessions)
            {
                await engine.CreateModelAsync("Trial", 1604);
                var table = await engine.CreateTableAsync("Sales", "human");
                before = await engine.GetTrialTallyAsync();
                await engine.CreateMeasureAsync(table, "Total Sales", "1", "human");
                await engine.CreateMeasureAsync(table, "Order Count", "2", "agent");
                await engine.UndoAsync("human");   // a walk, not a change

                var during = await engine.GetTrialTallyAsync();
                Assert.Equal(_now.ToUnixTimeSeconds(), during.StartedAt);
                Assert.Equal(before.Steps + 2, during.Steps);
                Assert.Equal(before.ChangesApplied + 2, during.ChangesApplied);
                Assert.True(before.Steps >= 1);   // the table was a step too
                engine.Dispose();   // writes what it holds
            }

            var (again, sessions2, _, _) = TrialEngine(token);
            using (sessions2)
            using (again)
            {
                var after = await again.GetTrialTallyAsync();
                Assert.Equal(before.Steps + 2, after.Steps);
                Assert.Equal(before.ChangesApplied + 2, after.ChangesApplied);
            }
        }

        [Fact]
        public async Task Free_and_paid_Pro_read_an_empty_tally_and_write_nothing()
        {
            foreach (var entitlement in new IEntitlement[] { TestEntitlements.Free, LicenseEntitlement.Evaluate(PaidToken(), _pub, false, _now) })
            {
                var sessions = new SessionManager();
                using (sessions)
                using (var engine = new LocalEngine(sessions, entitlement))
                {
                    await engine.CreateModelAsync("Paid", 1604);
                    await engine.CreateTableAsync("Sales", "human");
                    var tally = await engine.GetTrialTallyAsync();
                    Assert.Equal(0, tally.StartedAt);
                    Assert.Equal(0, tally.Steps);
                    Assert.Equal(0, tally.ChangesApplied);
                    Assert.Equal(0, tally.JevChoices);
                    Assert.Equal(0, tally.WorkflowsRun);
                }
            }
            Assert.Empty(Directory.GetFiles(_root, "trial*"));
        }

        [Fact]
        public async Task After_the_end_the_tally_stays_readable_and_stops_counting()
        {
            var (engine, sessions, _, set) = TrialEngine();
            using (sessions)
            using (engine)
            {
                await engine.CreateModelAsync("Trial", 1604);
                var table = await engine.CreateTableAsync("Sales", "human");
                var during = await engine.GetTrialTallyAsync();
                set(End.AddSeconds(1));
                await engine.CreateMeasureAsync(table, "After", "1", "human");
                var after = await engine.GetTrialTallyAsync();
                Assert.Equal(_now.ToUnixTimeSeconds(), after.StartedAt);
                Assert.Equal(during.Steps, after.Steps);
                Assert.Equal(during.ChangesApplied, after.ChangesApplied);
                Assert.True(after.Steps > 0);
            }
        }

        [Fact]
        public async Task Each_choice_the_hosted_service_makes_counts_once_and_a_cached_answer_does_not()
        {
            using var f = new DecisionFixture(LicenseEntitlement.Evaluate(TrialToken(), _pub, false, _now));
            f.Engine.UseClockForTest(() => _now.AddHours(1));
            var measureRef = await f.BuildModelAsync();
            await f.ConsentAsync();
            var digest = await f.DiscoverDigestAsync(measureRef);   // asked, not decided: no choice made
            Assert.Equal(0, (await f.Engine.GetTrialTallyAsync()).JevChoices);

            f.UseRecorded(DecisionFixture.ProofFixture(digest, "Volume", 0.93, "currency", 0.97));
            await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
            Assert.Equal(2, (await f.Engine.GetTrialTallyAsync()).JevChoices);   // a folder and a format
            await f.Engine.ProposeMeasureDefaultsAsync(measureRef);   // served from the cache
            Assert.Equal(2, (await f.Engine.GetTrialTallyAsync()).JevChoices);
        }

        [Fact]
        public async Task A_finished_workflow_run_counts_once()
        {
            var workspace = Path.Combine(_root, "ws");
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "one-step.md"),
                "---\nname: one-step\ntitle: One step\nversion: 7\nstrictness: hard\n---\n## Step 1: Say it\nSay it.\n");
            var (engine, sessions, _, _) = TrialEngine(workspace: workspace);
            using (sessions)
            using (engine)
            {
                var run = await engine.StartWorkflowAsync("one-step", "human");
                var done = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");
                Assert.Equal("completed", done.Status);
                Assert.Equal(1, (await engine.GetTrialTallyAsync()).WorkflowsRun);
            }
        }

        [Fact]
        public async Task The_tally_reads_the_same_on_both_doors()
        {
            var (engine, sessions, _, _) = TrialEngine();
            var pipeName = TestPipe.Mint("semanticus-tally-");
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            NamedPipeClientStream pipe = null;
            JsonRpc studio = null;
            RemoteEngine agent = null;
            try
            {
                await engine.CreateModelAsync("Trial", 1604);
                await engine.CreateTableAsync("Sales", "human");
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, Challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                studio = new JsonRpc(RpcServer.CreateHandler(pipe));
                studio.StartListening();
                agent = await RemoteEngine.ConnectAsync(pipeName);

                var ui = await studio.InvokeAsync<JObject>("getTrialTally");
                var mcp = await McpTools.GetTrialTally(agent);
                Assert.True(mcp.Steps > 0);
                Assert.Equal(mcp.Steps, (long)ui["steps"]);
                Assert.Equal(mcp.ChangesApplied, (long)ui["changesApplied"]);
                Assert.Equal(mcp.StartedAt, (long)ui["startedAt"]);
                Assert.Equal(0, (long)ui["jevChoices"]);
                Assert.Equal(0, (long)ui["workflowsRun"]);
            }
            finally
            {
                agent?.Dispose();
                studio?.Dispose();
                pipe?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                engine.Dispose();
                sessions.Dispose();
            }
        }
    }
}
