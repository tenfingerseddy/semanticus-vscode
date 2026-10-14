// A crowded relay is not a failure (m15/jev-busy, Kane 13 Oct: a newly opened model's Plan said "Jev has looked at 0 of 168 things.
// Too many advice requests are already running for this licence. Try again in a moment.").
//
// The relay lets one licence have six asks in flight and answers 429 too_many_in_flight past that. These tests use a fake relay that
// keeps that rule, so what is proved is the behaviour a person meets: the engine stays under the cap on its own, a crowded reply makes
// the ask wait its turn (never charged, never a stop), and past the bound the ask comes back for the Plan to ask again by itself.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevBusyTests
    {
        private const string RelayBase = "https://relay.test.invalid";

        private static readonly string Reply =
            "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10},\"answers\":{" +
            "\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + MeasureDefaultsPoint.FolderOptionId("Volume") +
            "\",\"confidence\":0.93}," +
            "\"format_family\":{\"type\":\"choice\",\"choice\":\"currency\",\"confidence\":0.97}}}";

        private const string CrowdedBody =
            "{\"error\":\"too_many_in_flight\",\"reason\":\"Too many advice requests are already running for this licence. Try again in a moment.\"}";

        /// <summary>The relay's rule: at most <see cref="Limit"/> asks in flight for one licence, the next one is refused with 429.
        /// Share one instance between oracles to act as two engines on one licence.</summary>
        private sealed class CapRelay : HttpMessageHandler
        {
            public int Limit = 6;
            public int Hold = 20;
            public int RetryAfterSeconds;
            public int Refused;
            public int Answered;
            public int MaxInFlight;
            private int _inFlight;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var now = Interlocked.Increment(ref _inFlight);
                try
                {
                    lock (this) MaxInFlight = Math.Max(MaxInFlight, now);
                    if (now > Limit)
                    {
                        Interlocked.Increment(ref Refused);
                        var crowded = new HttpResponseMessage((HttpStatusCode)429)
                        {
                            Content = new StringContent(CrowdedBody, Encoding.UTF8, "application/json"),
                        };
                        if (RetryAfterSeconds > 0) crowded.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
                        return crowded;
                    }
                    await Task.Delay(Hold, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref Answered);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
        }

        private sealed class SilentRelay : HttpMessageHandler
        {
            public int Sends, Cancelled;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Sends);
                try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
                return null;
            }
        }

        private sealed class AlwaysCrowded : HttpMessageHandler
        {
            public int Sends;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Sends);
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent(CrowdedBody, Encoding.UTF8, "application/json"),
                });
            }
        }

        private static JevDecisionOracle Oracle(HttpMessageHandler relay, DecisionBudget budget = null) =>
            JevDecisionOracle.ForRelay("not-a-real-licence", RelayBase, budget ?? new DecisionBudget(), relay);

        [Fact]
        public void The_engine_has_one_generous_limit_and_a_bounded_wait()
        {
            // The relay no longer limits asks in flight (Kane, 13 Oct); this limit only keeps the person's machine responsive.
            Assert.Equal(16, JevDecisionOracle.MaxConcurrent);
            Assert.InRange(JevDecisionOracle.BusyBound.TotalSeconds, 60, 180);
        }

        [Fact]
        public async Task A_burst_of_asks_never_sends_more_than_the_engines_cap_at_once()
        {
            using var relay = new CapRelay { Limit = int.MaxValue, Hold = 30 };
            using var oracle = Oracle(relay);
            var all = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None)));

            Assert.True(relay.MaxInFlight <= JevDecisionOracle.MaxConcurrent, "most in flight: " + relay.MaxInFlight);
            Assert.True(relay.MaxInFlight > 6, "the old cap of six is gone: " + relay.MaxInFlight);
            Assert.Equal(0, relay.Refused);
            Assert.All(all, a => Assert.True(a.Decided, a.NotDecidedReason));
        }

        [Fact]
        public async Task An_ask_that_waits_its_turn_for_a_slot_is_not_dropped_at_the_four_second_deadline()
        {
            // The deadline is the time on the wire. Before, it started when the ask joined the queue, so a few hundred asks sent at once
            // dropped everything past the first slots unsent ("Jev has looked at 0 of N"). One slot, a 300 ms deadline, four asks of 200 ms each.
            using var relay = new CapRelay { Limit = int.MaxValue, Hold = 200 };
            using var oracle = new JevDecisionOracle("not-a-real-licence", new DecisionBudget(), relay, TimeSpan.FromMilliseconds(300), maxConcurrent: 1, RelayBase);

            var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None)));

            Assert.All(all, a => Assert.True(a.Decided, a.NotDecidedReason));
        }

        [Fact]
        public async Task An_ask_never_leaves_a_request_open_after_its_deadline_and_never_retries_in_a_tight_loop()
        {
            // A relay that never replies: the ask comes back at its deadline with the calm sentence, the request was cancelled (the handler sees it),
            // the slot is free again, and exactly one request went out (the relay route makes one attempt).
            using var relay = new SilentRelay();
            using var oracle = new JevDecisionOracle("not-a-real-licence", new DecisionBudget(), relay, TimeSpan.FromMilliseconds(300), maxConcurrent: 1, RelayBase);

            var first = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
            var second = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.Equal(JevDecisionOracle.NotAvailableReason, first.NotDecidedReason);
            Assert.Equal(JevDecisionOracle.NotAvailableReason, second.NotDecidedReason);
            Assert.Equal(2, relay.Sends);       // one per ask, no tight retry
            Assert.Equal(2, relay.Cancelled);   // each was cancelled, none left open
        }

        [Fact]
        public async Task Two_engines_on_one_licence_all_get_answered_and_nobody_is_told_to_try_again()
        {
            using var relay = new CapRelay { Limit = 6, Hold = 40 };
            using var one = Oracle(relay);
            using var two = Oracle(relay);
            var pauses = 0;
            foreach (var o in new[] { one, two }) o.BusyDelayForTest = (t, c) => { Interlocked.Increment(ref pauses); return Task.Delay(15, c); };

            var asks = Enumerable.Range(0, 16).Select(i => (i % 2 == 0 ? one : two).AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None));
            var all = await Task.WhenAll(asks);

            Assert.True(relay.Refused > 0, "precondition: eight in flight on a cap of six crowded the relay");
            Assert.True(pauses > 0);
            Assert.All(all, a => Assert.True(a.Decided, a.NotDecidedReason));
            Assert.All(all, a => Assert.DoesNotContain("Try again in a moment", a.NotDecidedReason ?? "", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_crowded_reply_waits_its_turn_is_not_charged_and_the_ask_is_answered()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var relay = new CapRelay { Limit = 0 };
            using var oracle = Oracle(relay, budget);
            var pauses = new List<TimeSpan>();
            DateTime? seenWaiting = null;
            oracle.BusyDelayForTest = (t, c) =>
            {
                pauses.Add(t);
                seenWaiting ??= oracle.WaitingSince;
                if (pauses.Count == 3) relay.Limit = 6;   // the crowd thins out
                return Task.CompletedTask;
            };

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.True(answers.Decided, answers.NotDecidedReason);
            Assert.Equal(3, relay.Refused);
            Assert.Equal(3, pauses.Count);
            Assert.NotNull(seenWaiting);              // the row can say "waiting its turn" while it waits
            Assert.Null(oracle.WaitingSince);         // and stops saying it when it is over
            Assert.Equal(100, budget.UsedInputTokens);  // only the answered ask is charged, never the crowded ones
            Assert.Equal(0, budget.ReservedInputTokens);
            Assert.True(pauses[1] > pauses[0] * 1.2 || pauses[2] > pauses[0], "pauses grow");
            Assert.All(pauses, p => Assert.True(p <= TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public async Task The_relays_Retry_After_is_honoured()
        {
            using var relay = new CapRelay { Limit = 0, RetryAfterSeconds = 7 };
            using var oracle = Oracle(relay);
            var pauses = new List<TimeSpan>();
            oracle.BusyDelayForTest = (t, c) => { pauses.Add(t); relay.Limit = 6; return Task.CompletedTask; };

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.True(answers.Decided);
            var pause = Assert.Single(pauses);
            Assert.InRange(pause.TotalSeconds, 7, 10.6);   // the hint plus a little jitter
        }

        [Fact]
        public async Task A_relay_that_stays_crowded_gives_the_ask_back_after_the_bound_charges_nothing_and_does_not_hold_off_the_next_ask()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var relay = new AlwaysCrowded();
            using var oracle = Oracle(relay, budget);
            oracle.BusyBoundForTest = TimeSpan.FromSeconds(30);
            var waited = TimeSpan.Zero;
            oracle.BusyDelayForTest = (t, c) => { waited += t; return Task.CompletedTask; };

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.False(answers.Decided);
            Assert.Equal(JevDecisionOracle.CrowdedReason, answers.NotDecidedReason);
            Assert.True(JevDecisionOracle.IsCrowded(answers.NotDecidedReason));
            Assert.False(JevDecisionOracle.IsNotAvailable(answers.NotDecidedReason));
            Assert.DoesNotContain("Try again in a moment", answers.NotDecidedReason, StringComparison.Ordinal);
            Assert.Contains("Semanticus will try again by itself", answers.NotDecidedReason, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromSeconds(30), waited);
            Assert.True(relay.Sends > 3);
            Assert.Equal(0, budget.UsedInputTokens);
            Assert.Equal(0, budget.ReservedInputTokens);

            // Nothing is held off: the next ask goes out at once.
            var before = relay.Sends;
            oracle.BusyBoundForTest = TimeSpan.Zero;
            await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
            Assert.True(relay.Sends > before);
        }

        [Fact]
        public async Task A_person_or_plan_that_moves_on_stops_the_wait_at_once()
        {
            using var relay = new AlwaysCrowded();
            using var oracle = Oracle(relay);
            using var cts = new CancellationTokenSource();
            oracle.BusyDelayForTest = (t, c) => { cts.Cancel(); c.ThrowIfCancellationRequested(); return Task.CompletedTask; };

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), cts.Token);

            Assert.False(answers.Decided);
            Assert.Equal(JevDecisionOracle.CancelledReason, answers.NotDecidedReason);
            Assert.Null(oracle.WaitingSince);
        }

        [Fact]
        public void The_waiting_line_is_calm_and_counts_minutes_and_seconds()
        {
            Assert.Equal("Jev is waiting its turn, 0:12.", Semanticus.Engine.AiSchemaFamily.WaitingLine(TimeSpan.FromSeconds(12)));
            Assert.Equal("Jev is waiting its turn, 1:05.", Semanticus.Engine.AiSchemaFamily.WaitingLine(TimeSpan.FromSeconds(65)));
        }

        [Fact]
        public void A_crowded_stop_reads_as_waiting_and_never_asks_the_person_to_retry()
        {
            var line = Semanticus.Engine.AiSchemaFamily.ProgressLine(0, 168, restFollow: true, stopReason: JevDecisionOracle.CrowdedReason);
            Assert.Equal("Jev has looked at 0 of 168 things in this model so far. " + JevDecisionOracle.CrowdedReason, line);
            Assert.DoesNotContain("Try again in a moment", line, StringComparison.Ordinal);
        }

        // ---- how many requests opening a model sends (the coordinator asked for the number, before and after) ----------------

        private sealed class CountingOracle : IDecisionOracle
        {
            public readonly Dictionary<string, int> ByPoint = new Dictionary<string, int>(StringComparer.Ordinal);
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (ByPoint) ByPoint[request.PointName] = ByPoint.TryGetValue(request.PointName, out var n) ? n + 1 : 1;
                return Task.FromResult(DecisionAnswers.NotDecided(request, "counted"));
            }
        }

        /// <summary>Opens a model of <paramref name="tables"/> tables (five columns and two measures each) with Jev on and counts the requests the
        /// Plan's first run sends, by decision point. The counts go to the file named by SEMANTICUS_JEVBUSY_COUNTS (when set), so the same test can
        /// be run on the code before this lane and after it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(3)]
        [InlineData(30)]
        public async Task Opening_a_model_sends_a_bounded_number_of_requests(int tables)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            for (var t = 1; t <= tables; t++)
            {
                var table = "Table" + t.ToString("00", CultureInfo.InvariantCulture);
                await e.CreateTableAsync(table, "human");
                await e.CreateColumnAsync("table:" + table, "Key", "Int64", null, "human");
                await e.CreateColumnAsync("table:" + table, "Name", "String", null, "human");
                await e.CreateColumnAsync("table:" + table, "Region", "String", null, "human");
                await e.CreateColumnAsync("table:" + table, "Amount", "Double", null, "human");
                await e.CreateColumnAsync("table:" + table, "Quantity", "Double", null, "human");
                await e.CreateMeasureAsync("table:" + table, "Total " + t, "SUM ( " + table + "[Amount] )", "human");
                await e.CreateMeasureAsync("table:" + table, "Units " + t, "SUM ( " + table + "[Quantity] )", "human");
            }
            await f.SaveAsAsync("JevBusyCount" + tables + ".bim");
            f.UseNull();
            await f.ConsentAsync();
            var counting = new CountingOracle();
            e.UseDecisionsForTest(new DecisionService(counting, new DecisionBudget()));

            await e.GetModelPlanAsync();
            await e.JevSettledForTest();

            var total = counting.ByPoint.Values.Sum();
            var line = tables + " tables: " + total + " requests (" + string.Join(", ", counting.ByPoint.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)) + ")";
            var file = Environment.GetEnvironmentVariable("SEMANTICUS_JEVBUSY_COUNTS");
            if (!string.IsNullOrEmpty(file)) System.IO.File.AppendAllText(file, line + "\n");
            Assert.True(total > 0, "precondition: Jev was asked. " + line);
        }

        // ---- a job that stopped on a refusal is asked again (Kane, 13 Oct: Refresh plan did nothing after the relay was fixed) ----------------

        private sealed class RefusingOracle : IDecisionOracle
        {
            public string Reason;
            public int Asks;
            /// <summary>Asks the families' job made. The Plan's ranking is asked on every read, whatever the job did, so it is not counted.</summary>
            public int JobAsks;
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref Asks);
                if (request.PointName != "plan.rank") Interlocked.Increment(ref JobAsks);
                return Task.FromResult(DecisionAnswers.NotDecided(request, Reason));
            }
        }

        private const string Crowded429 = "Too many advice requests are already running for this licence. Try again in a moment.";

        private static async Task<(DecisionFixture F, RefusingOracle Oracle)> OpenedModelAsync(string reason)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            foreach (var table in new[] { "Customer", "Sales" })
            {
                await e.CreateTableAsync(table, "human");
                await e.CreateColumnAsync("table:" + table, "Key", "Int64", null, "human");
                await e.CreateColumnAsync("table:" + table, "Name", "String", null, "human");
            }
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "COUNTROWS ( Sales )", "human");
            await f.SaveAsAsync("JevBusyRetry.bim");
            f.UseNull();
            await f.ConsentAsync();
            var oracle = new RefusingOracle { Reason = reason };
            e.AiSchemaJevDecidesForTest = true;
            e.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            return (f, oracle);
        }

        private static string AllRowText(ModelPlanView plan) =>
            string.Join("\n", plan.Items.SelectMany(i => (i.Why ?? Array.Empty<string>()).Concat(new[] { i.Title })));

        [Fact]
        public async Task Refresh_plan_asks_again_for_a_job_that_stopped_on_a_refusal_and_the_old_refusal_text_goes()
        {
            var (f, oracle) = await OpenedModelAsync(Crowded429);
            using (f)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                var first = await f.Engine.GetModelPlanAsync();
                var asksBefore = oracle.Asks;
                Assert.True(asksBefore > 0, "precondition: Jev was asked");
                Assert.Contains(Crowded429, AllRowText(first));   // precondition: the row said it, as on Kane's screen

                // The cause is gone. Refresh plan, with no model change in between.
                oracle.Reason = "counted";
                await f.Engine.RefreshPlanAsync();
                await f.Engine.JevSettledForTest();
                var after = await f.Engine.GetModelPlanAsync();

                Assert.True(oracle.Asks > asksBefore, "Refresh plan did not ask Jev again: " + asksBefore + " then " + oracle.Asks);
                Assert.DoesNotContain(Crowded429, AllRowText(after));
            }
        }

        [Fact]
        public async Task The_next_plain_read_asks_again_for_a_job_that_heard_nothing_once_the_stop_is_old_enough()
        {
            var (f, oracle) = await OpenedModelAsync(JevDecisionOracle.UnreachableReason);
            using (f)
            {
                f.Engine.JevAutoRetryAfter = TimeSpan.Zero;
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                var asksBefore = oracle.JobAsks;
                Assert.True(asksBefore > 0);

                await f.Engine.GetModelPlanAsync();   // the next plain read starts the job again
                await f.Engine.JevSettledForTest();

                Assert.True(oracle.JobAsks > asksBefore, "the next read did not ask Jev again: " + asksBefore + " then " + oracle.JobAsks);
            }
        }

        [Fact]
        public async Task A_plain_read_inside_the_wait_does_not_start_a_stopped_job_again()
        {
            var (f, oracle) = await OpenedModelAsync(JevDecisionOracle.UnreachableReason);
            using (f)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                var asksBefore = oracle.JobAsks;

                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();

                Assert.Equal(asksBefore, oracle.JobAsks);   // sixty seconds have not passed: no loop of runs
            }
        }
    }
}
