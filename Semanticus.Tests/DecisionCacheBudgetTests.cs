// The cache and the session budget.

using System;
using System.Collections.Generic;
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
    public sealed class DecisionCacheBudgetTests
    {
        private static DecisionAnswers Decided(DecisionRequest request, string servedVersion, int inputTokens = 100)
        {
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97, inputTokens), servedVersion);
            return oracle.AskAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }

        [Fact]
        public void The_same_evidence_under_a_DIFFERENT_served_version_is_a_miss()
        {
            var request = DecisionFixture.SampleRequest();
            var cache = new DecisionCache();
            cache.Put(Decided(request, "jev-1.13.0"));

            DecisionAnswers hit;
            Assert.True(cache.TryGet(request.Digest, "jev-1.13.0", out hit));
            Assert.False(cache.TryGet(request.Digest, "jev-1.14.0", out hit));
        }

        [Fact]
        public void A_cached_answer_reports_no_spend_so_a_replay_is_not_charged_twice()
        {
            var request = DecisionFixture.SampleRequest();
            var cache = new DecisionCache();
            var fresh = Decided(request, "jev-1.13.0", inputTokens: 900);
            Assert.Equal(900, fresh.InputTokens);

            cache.Put(fresh);
            DecisionAnswers hit;
            Assert.True(cache.TryGet(request.Digest, "jev-1.13.0", out hit));
            Assert.Equal(0, hit.InputTokens);
            Assert.True(hit.Decided);
        }

        /// <summary>Codex on PR 384 (F-175): the served version only ever changed when another call succeeded,
        /// so once an answer was cached nothing could discover that the moving alias had rolled. An entry is
        /// now served only inside the revalidation window, and the call that follows re-resolves the alias.</summary>
        [Fact]
        public void A_cached_answer_is_not_served_once_the_alias_revalidation_window_has_passed()
        {
            var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
            var cache = new DecisionCache(revalidateAfter: TimeSpan.FromMinutes(10), clock: () => now);
            var request = DecisionFixture.SampleRequest();
            cache.Put(Decided(request, "jev-1.13.0"));

            DecisionAnswers hit;
            Assert.True(cache.TryGet(request.Digest, "jev-1.13.0", out hit));

            now = now.AddMinutes(9);
            Assert.True(cache.TryGet(request.Digest, "jev-1.13.0", out hit));

            now = now.AddMinutes(2);
            Assert.False(cache.TryGet(request.Digest, "jev-1.13.0", out hit));
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public async Task The_same_evidence_reaches_the_service_again_once_the_alias_window_has_passed()
        {
            var request = DecisionFixture.SampleRequest();
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-1.13.0");
            var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
            var cache = new DecisionCache(revalidateAfter: TimeSpan.FromMinutes(10), clock: () => now);

            using (var service = new DecisionService(oracle, cache: cache, readConsent: _ =>
                DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow))
            { ReadScope = () => Task.FromResult("disk:test") })
            {
                await service.AskAsync(request.PointName, request.Snapshot, request.Questions,
                    isPro: true, enabled: true, fence: null, ct: CancellationToken.None);
                await service.AskAsync(request.PointName, request.Snapshot, request.Questions,
                    isPro: true, enabled: true, fence: null, ct: CancellationToken.None);
                Assert.Equal(1, oracle.AskCount);

                now = now.AddMinutes(11);
                await service.AskAsync(request.PointName, request.Snapshot, request.Questions,
                    isPro: true, enabled: true, fence: null, ct: CancellationToken.None);
                Assert.Equal(2, oracle.AskCount);
            }
        }

        /// <summary>UAT lane p, 26 Sep: an edit made while asks were in flight threw away answers already paid for, so
        /// the next run bought them again (82 requests became 139 for one table's edit). The reply is still dropped for
        /// the model that moved, but it is an answer about its EVIDENCE (the digest), so it is kept for the next ask of
        /// the same evidence.</summary>
        [Fact]
        public async Task A_reply_that_lands_after_the_model_moved_is_dropped_but_kept_for_the_same_evidence()
        {
            var request = DecisionFixture.SampleRequest();
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-1.13.0");
            var revision = 1L;
            var fence = new DecisionSessionFence("s1", 1, () => ("s1", revision));
            using (var service = new DecisionService(oracle, readConsent: _ =>
                DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow))
            { ReadScope = () => Task.FromResult("disk:test") })
            {
                service.BeforeReturnForTest = () => revision = 2;
                var late = await service.AskAsync(request.PointName, request.Snapshot, request.Questions,
                    isPro: true, enabled: true, fence: fence, ct: CancellationToken.None);
                Assert.False(late.Asked);
                Assert.Contains("model changed", late.SkippedReason, StringComparison.Ordinal);

                service.BeforeReturnForTest = null;
                var again = await service.AskAsync(request.PointName, request.Snapshot, request.Questions,
                    isPro: true, enabled: true, fence: new DecisionSessionFence("s1", 2, () => ("s1", revision)), ct: CancellationToken.None);
                Assert.True(again.Asked);
                Assert.Equal(1, oracle.AskCount);
            }
        }

        [Fact]
        public void A_shrug_is_never_cached()
        {
            var request = DecisionFixture.SampleRequest();
            var cache = new DecisionCache();
            cache.Put(DecisionAnswers.NotDecided(request, "the service did not answer in time"));

            DecisionAnswers hit;
            Assert.False(cache.TryGet(request.Digest, "jev-1.13.0", out hit));
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void The_cache_is_bounded_and_drops_the_least_recently_used()
        {
            var cache = new DecisionCache(capacity: 3);
            var requests = new List<DecisionRequest>();
            for (var i = 0; i < 4; i++)
            {
                var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
                var snapshot = DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("measure_name", "Measure " + i),
                });
                var request = new DecisionRequest(point.Name, point.SchemaVersion, snapshot,
                    MeasureDefaultsPoint.Questions(new[] { "Volume" }, true, true));
                requests.Add(request);
            }

            foreach (var r in requests.Take(3)) cache.Put(Decided(r, "jev-1.13.0"));

            // Touch the first so it is no longer the oldest, then overflow.
            DecisionAnswers hit;
            Assert.True(cache.TryGet(requests[0].Digest, "jev-1.13.0", out hit));
            cache.Put(Decided(requests[3], "jev-1.13.0"));

            Assert.Equal(3, cache.Count);
            Assert.True(cache.TryGet(requests[0].Digest, "jev-1.13.0", out hit));
            Assert.False(cache.TryGet(requests[1].Digest, "jev-1.13.0", out hit));
            Assert.True(cache.TryGet(requests[3].Digest, "jev-1.13.0", out hit));
        }

        [Fact]
        public async Task A_second_ask_for_the_same_evidence_does_not_reach_the_service()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var digest = await f.DiscoverDigestAsync(measureRef);
                var oracle = f.UseRecorded(DecisionFixture.ProofFixture(digest, "Volume", 0.93, "currency", 0.97));

                var first = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                var second = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);

                Assert.Equal("Volume", first.ProposedDisplayFolder);
                Assert.Equal("Volume", second.ProposedDisplayFolder);
                Assert.Equal(1, oracle.AskCount);
            }
        }

        [Fact]
        public async Task Switching_a_point_off_drops_what_was_cached_under_it()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var digest = await f.DiscoverDigestAsync(measureRef);
                var oracle = f.UseRecorded(DecisionFixture.ProofFixture(digest, "Volume", 0.93, "currency", 0.97));

                await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
                await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, true, "human");

                await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.Equal(2, oracle.AskCount);
            }
        }

        [Fact]
        public async Task The_budget_counts_input_tokens_and_stops_asking_when_it_runs_out()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var digest = await f.DiscoverDigestAsync(measureRef);
                var budget = new DecisionBudget(capInputTokens: 500);
                var oracle = f.UseRecorded(
                    DecisionFixture.ProofFixture(digest, "Volume", 0.93, "currency", 0.97, inputTokens: 900), budget);

                // The recorded oracle does not spend on its own; the service records what an answer reports.
                budget.Record(900, 40);

                var proposal = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.False(proposal.Asked);
                Assert.Contains("budget", proposal.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);

                var seen = await f.Engine.GetDecisionBudgetAsync();
                Assert.Equal(500, seen.CapInputTokens);
                Assert.Equal(900, seen.UsedInputTokens);
                Assert.Equal(0, seen.RemainingInputTokens);
                Assert.True(seen.Exhausted);
                Assert.True(seen.ServiceAvailable);
            }
        }

        // ---- the budget bounds failures and queued work too (B11) --------------------------------------

        private static readonly string DecidedReply =
            "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10},\"answers\":{" +
            "\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + MeasureDefaultsPoint.FolderOptionId("Volume") +
            "\",\"confidence\":0.93}," +
            "\"format_family\":{\"type\":\"choice\",\"choice\":\"currency\",\"confidence\":0.97}}}";

        /// <summary>Codex on PR 384 (F-173) at the other end of the same hole: the reply is read inside the
        /// oracle, whose parse boundary caught JsonException only. A number no double or int can hold is
        /// syntactically valid JSON, so it escaped as an exception out of a model operation.</summary>
        [Fact]
        public async Task A_reply_carrying_a_number_no_double_can_hold_comes_back_not_decided()
        {
            foreach (var reply in new[]
            {
                "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10},\"answers\":{" +
                "\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + MeasureDefaultsPoint.FolderOptionId("Volume") +
                "\",\"confidence\":1e400}," +
                "\"format_family\":{\"type\":\"choice\",\"choice\":\"currency\",\"confidence\":0.97}}}",
                "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":1e400,\"output_tokens\":10},\"answers\":{" +
                "\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + MeasureDefaultsPoint.FolderOptionId("Volume") +
                "\",\"confidence\":0.93}," +
                "\"format_family\":{\"type\":\"choice\",\"choice\":\"currency\",\"confidence\":0.97}}}",
            })
            {
                using (var handler = new ScriptedHandler())
                using (var oracle = new JevDecisionOracle("not-a-real-key", new DecisionBudget(), handler,
                                                          TimeSpan.FromSeconds(10), maxConcurrent: 1))
                {
                    handler.Replies.Enqueue((HttpStatusCode.OK, reply));

                    var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                    Assert.False(answers.Decided, reply);
                    Assert.False(string.IsNullOrWhiteSpace(answers.NotDecidedReason));
                }
            }
        }

        /// <summary>A request that never comes back still may have been charged, so it has to count. It used to
        /// escape the counter entirely: nothing recorded a timeout.</summary>
        [Fact]
        public async Task A_request_that_times_out_still_counts_against_the_budget()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new ScriptedHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromMilliseconds(250), maxConcurrent: 1))
            {
                handler.Park = true;

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.False(answers.Decided);
                Assert.True(budget.UsedInputTokens > 0,
                    "a timed-out attempt was not counted as spend that may still have been charged");
                Assert.True(budget.Requests >= 1);
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>A retry is a second thing that may have been charged, so it counts twice. It used to record
        /// zero tokens, which made a retried ask look as cheap as a single one.</summary>
        [Fact]
        public async Task A_retried_request_counts_twice()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new ScriptedHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(10), maxConcurrent: 1))
            {
                handler.Replies.Enqueue((HttpStatusCode.TooManyRequests, "{}"));
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.True(answers.Decided, answers.NotDecidedReason);
                Assert.Equal(2, handler.Sends);
                Assert.Equal(2, budget.Requests);
                Assert.True(budget.UsedInputTokens > answers.InputTokens,
                    "the refused first attempt was recorded as free");
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>A request that is waiting behind others must be refused when the allowance runs out while it
        /// waits. The budget used to be read only before the queue, so anything already queued kept spending.</summary>
        [Fact]
        public async Task A_request_already_queued_when_the_budget_runs_out_is_refused()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new ScriptedHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(10), maxConcurrent: 1))
            {
                handler.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply));
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply));

                // Task.Run because an uncontended queue lets AskAsync run straight into the handler on the
                // calling thread; the first ask has to be somewhere else for the second one to queue behind it.
                var first = Task.Run(() => oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None));
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "the first request never reached the handler");

                // The second request is about to join the queue behind the first. Spend the whole allowance
                // while it is in there, then let the first one through.
                oracle.EnteringQueueForTest = () =>
                {
                    oracle.EnteringQueueForTest = null;
                    budget.Record(100000, 0);
                    handler.Gate.TrySetResult(true);
                };

                var second = await Task.Run(() => oracle.AskAsync(DecisionFixture.SampleRequest("measure.folder_and_format"), CancellationToken.None));

                var a = await first;
                var b = second;

                Assert.True(a.Decided, a.NotDecidedReason);
                Assert.False(b.Decided);
                Assert.Contains("budget", b.NotDecidedReason, StringComparison.Ordinal);
                // And it says so STRUCTURALLY, not only in its words. OverBudget is the one signal every caller
                // reads for "the budget stopped this" (DecisionService turns it into its own spent-budget skip), so
                // the check after the queue has to mark it, not only word it (verify round 4, mutation M-R3-1c).
                Assert.True(b.OverBudget, "the check after the queue did not mark it as the budget: " + b.NotDecidedReason);
                Assert.Equal(1, handler.Sends);
            }
        }

        /// <summary>The check BEFORE the queue, marked the same way. An allowance that is already gone when an ask
        /// arrives is the budget stopping it, and nothing is sent (verify round 4, mutation M-R3-1c).</summary>
        [Fact]
        public async Task An_ask_made_with_the_allowance_already_gone_says_the_budget_stopped_it()
        {
            var budget = new DecisionBudget(capInputTokens: 0);
            using (var handler = new ScriptedHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(10), maxConcurrent: 1))
            {
                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.False(answers.Decided);
                Assert.True(answers.OverBudget, "the check before the queue did not mark it as the budget: " + answers.NotDecidedReason);
                Assert.Equal(0, handler.Sends);
            }
        }

        /// <summary>The direct vendor route makes two attempts. When the FIRST was sent and failed and the budget
        /// cannot fit the retry, Jev WAS asked, once, and may have been charged for it. OverBudget is what every
        /// caller turns into "Jev was not asked" (DraftCopy.JevNotAsked), so an ask that was sent never carries it
        /// (verify round 4, item 4).</summary>
        [Fact]
        public async Task An_ask_that_was_sent_once_is_never_reported_as_one_Jev_was_not_asked()
        {
            var request = DecisionFixture.SampleRequest();
            var estimate = JevDecisionOracle.EstimateInputTokens(
                JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot)));
            // Room for the first attempt and not for the retry.
            var budget = new DecisionBudget(capInputTokens: estimate + 1);
            using (var handler = new ScriptedHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(10), maxConcurrent: 1))
            {
                handler.Replies.Enqueue((HttpStatusCode.InternalServerError, "{}"));   // retryable: a second attempt is tried

                var answers = await oracle.AskAsync(request, CancellationToken.None);

                Assert.Equal(1, handler.Sends);   // precondition: one attempt went out, and the retry did not
                Assert.False(answers.Decided);
                Assert.False(answers.OverBudget,
                    "Jev was asked once, so this must not read as an ask nobody made: " + answers.NotDecidedReason);
                Assert.Equal(JevDecisionOracle.AskedThenStoppedReason, answers.NotDecidedReason);
            }
        }

        /// <summary>A caller that stops an ask while it is out (the ranking cancels one when the plan's items change)
        /// is not a service that failed to answer in time. The client used to say "did not answer in time" and log
        /// it as a failure, because the caller's token and the deadline are one linked token (found by the
        /// relationships builder, 27 Sep). The attempt DID go out, so it may have been charged: the relay keeps
        /// waiting for the real cost after the engine stops listening (relay.md, "A client timeout never refunds a
        /// possibly charged call"), and the estimate stands, exactly as it does for a timeout.</summary>
        [Fact]
        public async Task A_callers_cancel_while_the_ask_is_out_is_a_cancel_and_not_a_timeout()
        {
            var request = DecisionFixture.SampleRequest();
            var estimate = JevDecisionOracle.EstimateInputTokens(
                JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot)));
            var budget = new DecisionBudget(capInputTokens: 100000);
            var log = new List<string>();
            using (var handler = new ScriptedHandler { Park = true })
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(30), maxConcurrent: 1))
            using (var caller = new CancellationTokenSource())
            {
                oracle.LogLineForTest = line => { lock (log) log.Add(line); };
                var pending = oracle.AskAsync(request, caller.Token);
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the ask went out");
                caller.Cancel();

                var answers = await pending;

                Assert.False(answers.Decided);
                Assert.Equal(JevDecisionOracle.CancelledReason, answers.NotDecidedReason);
                Assert.Contains(log, l => l.Contains("outcome=cancelled", StringComparison.Ordinal));
                Assert.Equal(1, handler.Sends);   // one attempt, and no retry after a cancel
                Assert.Equal(estimate, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>A caller that stops an ask BEFORE it goes out (while it waits its turn behind others, or already
        /// cancelled when it arrives) sent nothing, so nothing is charged and nothing is counted as an attempt. It used
        /// to be logged as "queue-deadline" and told as "waited too long behind others", which is a different thing.</summary>
        [Fact]
        public async Task A_callers_cancel_before_the_ask_goes_out_costs_nothing_and_says_so()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            var log = new List<string>();
            using (var handler = new ScriptedHandler { Park = true })
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(30), maxConcurrent: 1))
            using (var first = new CancellationTokenSource())
            using (var second = new CancellationTokenSource())
            {
                oracle.LogLineForTest = line => { lock (log) log.Add(line); };
                var holding = oracle.AskAsync(DecisionFixture.SampleRequest(), first.Token);
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the first ask holds the only slot");
                var queued = oracle.AskAsync(DecisionFixture.SampleRequest(), second.Token);
                second.Cancel();

                var answers = await queued;

                Assert.Equal(JevDecisionOracle.CancelledReason, answers.NotDecidedReason);
                Assert.Equal(1, handler.Sends);
                Assert.Equal(1, budget.Requests);   // only the first ask's attempt
                lock (log)
                {
                    Assert.Contains(log, l => l.Contains("outcome=cancelled", StringComparison.Ordinal));
                    Assert.DoesNotContain(log, l => l.Contains("queue-deadline", StringComparison.Ordinal));
                }

                first.Cancel();
                await holding;
                var used = budget.UsedInputTokens;

                // Already stopped when it arrives: nothing goes out and nothing is held.
                var early = await oracle.AskAsync(DecisionFixture.SampleRequest(), second.Token);
                Assert.Equal(JevDecisionOracle.CancelledReason, early.NotDecidedReason);
                Assert.Equal(1, handler.Sends);
                Assert.Equal(1, budget.Requests);
                Assert.Equal(used, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>A caller that stops an ask while it waits to try again (the direct vendor route retries a 5xx) is a
        /// cancel too, not the first attempt's refusal. The first attempt went out and keeps its estimate; the retry never
        /// went out and costs nothing.</summary>
        [Fact]
        public async Task A_callers_cancel_in_the_wait_before_a_retry_is_a_cancel_and_the_retry_costs_nothing()
        {
            var request = DecisionFixture.SampleRequest();
            var estimate = JevDecisionOracle.EstimateInputTokens(
                JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot)));
            var budget = new DecisionBudget(capInputTokens: 100000);
            var log = new List<string>();
            using (var handler = new ScriptedHandler { RetryAfter = TimeSpan.FromSeconds(20) })
            using (var oracle = new JevDecisionOracle("not-a-real-key", budget, handler,
                                                      TimeSpan.FromSeconds(30), maxConcurrent: 1))
            using (var caller = new CancellationTokenSource())
            {
                oracle.LogLineForTest = line => { lock (log) log.Add(line); };
                handler.Replies.Enqueue((HttpStatusCode.ServiceUnavailable,
                    "{\"error\":\"paused\",\"reason\":\"Cloud advice is paused right now. Nothing else has changed.\"}"));
                var pending = oracle.AskAsync(request, caller.Token);
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the first attempt went out");
                await Task.Delay(200);   // the first attempt's refusal is back and the client is waiting to retry
                caller.Cancel();

                var answers = await pending;

                Assert.Equal(1, handler.Sends);   // precondition: the retry never went out
                Assert.Equal(JevDecisionOracle.CancelledReason, answers.NotDecidedReason);
                lock (log) Assert.Contains(log, l => l.Contains("outcome=cancelled", StringComparison.Ordinal));
                Assert.Equal(estimate, budget.UsedInputTokens);   // the first attempt only
                Assert.Equal(1, budget.Requests);
            }
        }

        /// <summary>The log line names how the ask ENDED, not how its first attempt ended. An ask whose first attempt
        /// was refused "paused" and whose retry then lost its connection returns "could not be reached", and used to
        /// log outcome=paused, so an operator reading the log saw a pause where a person saw a dropped connection
        /// (ai.schema verify round 5, L4). Only the direct vendor route retries.</summary>
        [Fact]
        public async Task The_log_names_how_the_ask_ended_and_not_how_its_first_attempt_did()
        {
            var log = new List<string>();
            using (var handler = new ScriptedHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-key", new DecisionBudget(capInputTokens: 100000), handler,
                                                      TimeSpan.FromSeconds(10), maxConcurrent: 1))
            {
                oracle.LogLineForTest = line => { lock (log) log.Add(line); };
                handler.Replies.Enqueue((HttpStatusCode.ServiceUnavailable,
                    "{\"error\":\"paused\",\"reason\":\"Cloud advice is paused right now. Nothing else has changed.\"}"));
                handler.Replies.Enqueue((ScriptedHandler.Unreachable, null));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.Equal(2, handler.Sends);   // precondition: the retry went out and lost its connection
                Assert.Equal(JevDecisionOracle.UnreachableReason, answers.NotDecidedReason);
                Assert.Equal("Jev is not available right now.", answers.NotDecidedReason);
                var line = Assert.Single(log);
                Assert.DoesNotContain("outcome=paused", line, StringComparison.Ordinal);
                Assert.Contains("outcome=unreachable", line, StringComparison.Ordinal);
            }
        }

        /// <summary>An ask that went out and whose deadline ended it before a reply is logged as a timeout, one attempt, in the
        /// same words a person reads ("did not answer in time"). Nothing pinned the log's code for it, so losing it left the
        /// operator a bare "failed" (ai.schema final verify, low item 1: the timeout log code).</summary>
        [Fact]
        public async Task The_log_says_timeout_when_the_deadline_ends_an_ask_that_went_out()
        {
            var log = new List<string>();
            using (var handler = new ScriptedHandler { Park = true })
            using (var oracle = new JevDecisionOracle("not-a-real-licence", new DecisionBudget(capInputTokens: 100000), handler,
                                                      TimeSpan.FromMilliseconds(250), maxConcurrent: 1, "https://relay.test.invalid"))
            {
                oracle.LogLineForTest = line => { lock (log) log.Add(line); };

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.Equal(1, handler.Sends);   // precondition: the ask went out and was parked past its deadline
                Assert.Equal(JevDecisionOracle.TimedOutReason, answers.NotDecidedReason);
                var line = Assert.Single(log);
                Assert.Contains("attempts=1 ", line, StringComparison.Ordinal);
                Assert.Contains("outcome=timeout", line, StringComparison.Ordinal);
            }
        }

        /// <summary>A handler that never touches a network: it parks, or hands back the replies the test queued.
        /// A send with no reply queued is a test bug and says so rather than inventing one.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            public readonly Queue<(HttpStatusCode Status, string Body)> Replies = new Queue<(HttpStatusCode, string)>();
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public TaskCompletionSource<bool> Gate;
            public bool Park;

            /// <summary>When set, every reply asks the client to wait this long before it tries again.</summary>
            public TimeSpan? RetryAfter;
            private int _sends;

            /// <summary>Queue this as a reply's status to have that send lose its connection instead of answering.</summary>
            public const HttpStatusCode Unreachable = 0;

            public int Sends { get { return Volatile.Read(ref _sends); } }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref _sends);
                Entered.Set();

                if (Park)
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => cancelled.TrySetResult(true)))
                        await cancelled.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                if (Gate != null) await Gate.Task.WaitAsync(ct).ConfigureAwait(false);

                (HttpStatusCode Status, string Body) reply;
                lock (Replies)
                {
                    if (Replies.Count == 0) throw new InvalidOperationException("the test queued no reply for this send");
                    reply = Replies.Dequeue();
                }
                if (reply.Status == Unreachable)
                    throw new HttpRequestException("the connection was reset before any reply (a test handler dropping one send)");

                var response = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
                if (RetryAfter.HasValue) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter.Value);
                return response;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Entered.Dispose();
                base.Dispose(disposing);
            }
        }

        [Fact]
        public async Task The_budget_reads_the_same_on_both_doors()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                f.UseNull();

                var viaMcp = await McpTools.GetDecisionBudget(f.Engine);
                var viaRpc = await new EngineRpcTarget(f.Engine).getDecisionBudget();

                Assert.Equal(viaMcp.CapInputTokens, viaRpc.CapInputTokens);
                Assert.Equal(viaMcp.UsedInputTokens, viaRpc.UsedInputTokens);
                Assert.Equal(viaMcp.Requests, viaRpc.Requests);
                Assert.False(viaMcp.ServiceAvailable);
                Assert.False(viaRpc.ServiceAvailable);
            }
        }
    }
}
