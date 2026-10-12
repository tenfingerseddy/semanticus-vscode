// What the plan REMEMBERS between calls, and what it must never remember (Jev M2).
//
// The plan used to be composed from scratch on every single call: a readiness scan, a cleanup sweep, a model
// read, a report reading, and then a SECOND readiness scan inside the grade rehearsal. get_model_plan paid
// that, get_next_actions paid it again for the Overview card, and so did every skip, every unskip and every
// action. On a published model that is a long wait for an answer that had not changed.
//
// So the expensive readings are now kept for one (session, revision, report basis). These tests hold the two
// halves of that bargain:
//
//   the work is really skipped   - proved by COUNTING the gathers, never by timing them. A timing test on a
//                                  shared machine is a coin toss, and a green coin toss is worse than no test.
//   the answer is never stale    - proved by moving the model and demanding the next plan be read at the new
//                                  revision, and by making a skip show up at once.
//
// ONE CASE IS DELIBERATELY NOT TESTED HERE, and it is not an oversight. The brief asked for a test that a
// change to a live-bound session's SOURCE VERSION invalidates the cache the way a model edit does. There is
// no such thing to test yet: Session exposes Revision (Session.cs:164) and nothing else that versions the
// source. SourceVersion exists only as a contract on IChangeTarget (Jev/ChangeContract.cs:261) whose one
// implementation is the in-memory reference target used by the change-set tests, and no live session is
// wired to it. When a session really carries a source version, it belongs in the cache key beside the
// revision and that test belongs here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ModelPlanCacheTests
    {
        /// <summary>The same shape ModelPlanTests uses: real gaps, including a safe fix, so there is something
        /// for the grade rehearsal to rehearse.</summary>
        private static async Task<DecisionFixture> GappyModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Old Total", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        // ---- a: the second read of an unchanged model costs nothing -------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Two_plans_for_an_unchanged_model_gather_once_and_answer_the_same()
        {
            using var f = await GappyModelAsync("PlanCacheTwice.bim");

            var first = await f.Engine.GetModelPlanAsync();
            var again = await f.Engine.GetModelPlanAsync();

            Assert.Equal(1, f.Engine.PlanGatherCountForTest);
            Assert.Equal(first.Items.Select(i => i.Id), again.Items.Select(i => i.Id));
            Assert.Equal(first.Revision, again.Revision);
            Assert.Equal(f.Sessions.Current.Revision, again.Revision);
        }

        // ---- b: the Overview card rides on the plan the page already paid for ---------------------------

        [Fact]
        public async Task The_top_three_after_a_plan_gathers_nothing_more()
        {
            using var f = await GappyModelAsync("PlanCacheNextActions.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var gathersAfterThePlan = f.Engine.PlanGatherCountForTest;
            var next = await f.Engine.GetNextActionsAsync();

            Assert.Equal(1, gathersAfterThePlan);
            Assert.Equal(gathersAfterThePlan, f.Engine.PlanGatherCountForTest);

            // And it is the same list, so the card and the page cannot disagree.
            Assert.Equal(
                plan.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed)
                          .Take(3).Select(i => "itemId: " + i.Id),
                next.Select(n => n.Args));
        }

        // ---- c: a model that moved is never answered from what was remembered ---------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_change_makes_the_next_plan_gather_again_at_the_new_revision()
        {
            using var f = await GappyModelAsync("PlanCacheInvalidate.bim");
            var before = await f.Engine.GetModelPlanAsync();
            Assert.Equal(1, f.Engine.PlanGatherCountForTest);

            await f.Engine.CreateMeasureAsync("table:Sales", "Margin", "SUM ( Sales[Amount] )", "human");

            var after = await f.Engine.GetModelPlanAsync();

            Assert.Equal(2, f.Engine.PlanGatherCountForTest);
            Assert.True(after.Revision > before.Revision, "the plan must be read at the revision the model is at now");
            Assert.Equal(f.Sessions.Current.Revision, after.Revision);
        }

        // ---- d: a decision shows at once, and costs nothing ---------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Setting_aside_and_putting_back_show_at_once_without_gathering_again()
        {
            using var f = await GappyModelAsync("PlanCacheSkip.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);
            var gathers = f.Engine.PlanGatherCountForTest;

            var skipped = await f.Engine.SkipModelPlanItemAsync(target.Id, "not until the quarter closes", "human");
            Assert.Equal(ModelPlanStatuses.Skipped, skipped.Items.Single(i => i.Id == target.Id).Status);
            Assert.Equal("not until the quarter closes", skipped.Items.Single(i => i.Id == target.Id).SkipReason);

            var back = await f.Engine.UnskipModelPlanItemAsync(target.Id, "human");
            Assert.NotEqual(ModelPlanStatuses.Skipped, back.Items.Single(i => i.Id == target.Id).Status);

            Assert.Equal(gathers, f.Engine.PlanGatherCountForTest);
        }

        // ---- two callers arriving together pay once -----------------------------------------------------

        [Fact]
        public async Task Two_first_calls_at_the_same_time_gather_once()
        {
            using var f = await GappyModelAsync("PlanCacheConcurrent.bim");

            var both = await Task.WhenAll(f.Engine.GetModelPlanAsync(), f.Engine.GetModelPlanAsync());

            Assert.Equal(1, f.Engine.PlanGatherCountForTest);
            Assert.Equal(both[0].Items.Select(i => i.Id), both[1].Items.Select(i => i.Id));
        }

        // ---- the ranking is not re-bought for evidence that did not change ------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Two_plans_for_the_same_evidence_ask_the_ranking_service_once()
        {
            using var f = await GappyModelAsync("PlanCacheRank.bim");
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            // This test is about the RANKING. Since M4 the measures family also asks on a Pro plan (its two
            // measures have no folder or format), so that point is switched off here to keep one ask per
            // plan and the digest below the ranking's own.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            // The column format, category and summarize families ask too on this model; switched off so the only ask is the ranking.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            // So does what Copilot sees (ai.schema); switched off for the same reason.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");

            // Phase one, the fixture's own two-step: ask with an empty oracle to learn the digest the engine
            // really produces, rather than writing a copy of the model's shape into a string literal.
            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();   // the ranking is asked in the background (T268 wiring)
            var digest = Assert.Single(probe.AskedDigests);
            var open = plan.Items
                .Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed)
                .ToArray();
            Assert.NotEmpty(open);

            // Phase two needs an oracle that really DECIDES. A not-decided answer is deliberately never kept
            // (DecisionCache.Put: "a timeout or a shrug is about the moment, not about the evidence"), so an
            // empty fixture would show two asks and prove nothing about the cache.
            var oracle = f.UseRecorded(RankFixture(digest, open));

            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();

            // The plan is recomposed on the second call, so the ranking really is asked for again locally;
            // what must not happen is a second trip to the SERVICE for evidence that did not change. That is
            // the DecisionCache's job, keyed on the request digest, and this is the test of it rather than
            // the assumption.
            Assert.Single(oracle.AskedDigests);
        }

        // ---- the plan never waits for the ranking either (T268 wiring) -----------------------------------

        /// <summary>A slow ranking service: every plan.rank ask waits until the test lets it go, then answers
        /// from the recorded fixture. Anything else passes straight through.</summary>
        private sealed class GatedRankOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _waiting;
            public GatedRankOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public int Waiting => System.Threading.Volatile.Read(ref _waiting);
            public void Release() => _release.TrySetResult(true);
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct)
            {
                if (!string.Equals(request.PointName, DecisionRegistry.PlanRank, StringComparison.Ordinal)) return await _inner.AskAsync(request, ct);
                System.Threading.Interlocked.Increment(ref _waiting);
                try { await _release.Task.WaitAsync(ct); }
                finally { System.Threading.Interlocked.Decrement(ref _waiting); }
                return await _inner.AskAsync(request, ct);
            }
        }

        private static async Task<T> Within<T>(Task<T> task, string what)
        {
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(ReferenceEquals(done, task), what);
            return await task;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_plan_returns_before_the_ranking_answers_and_the_next_read_after_it_lands_is_in_value_order()
        {
            using var f = await GappyModelAsync("PlanRankBackground.bim");
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            // The column format, category and summarize families ask too on this model; switched off so the only ask is the ranking.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            // So does what Copilot sees (ai.schema); switched off for the same reason.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");

            // Learn the ranking's real digest and the open items, in rule order.
            var ruleOrder = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var digest = Assert.Single(probe.AskedDigests);
            var open = ruleOrder.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            Assert.True(open.Length >= 2, "the test needs two open items to tell value order from rule order");
            // The optional cleanup row always sits last and no ranking lifts it (round 5 lists it on more models), so the row the value
            // order puts first is the last one that is not optional.
            var last = open.Last(i => !i.Optional).Id;

            // The value order puts the LAST rule-order item first, so the two orders cannot be confused.
            var recorded = new RecordedDecisionOracle(RankFixture(digest, open, top: last), "jev-test-1");
            var gated = new GatedRankOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(gated, new DecisionBudget()));
            var announced = 0;
            f.Sessions.Bus.PlanChanged += _ => System.Threading.Interlocked.Increment(ref announced);

            // The service has not answered and will not until it is let go, so a plan that came back at all
            // came back without waiting for the ranking.
            var first = await Within(f.Engine.GetModelPlanAsync(), "get_model_plan waited for the ranking");
            Assert.False(first.Ranked);
            Assert.True(first.JevPending);
            Assert.Equal(open.Select(i => i.Id), first.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).Select(i => i.Id));
            Assert.Contains("order Semanticus found", first.OrderedBy, StringComparison.Ordinal);

            // A second read while the ask is out does not ask again.
            for (var i = 0; i < 400 && gated.Waiting == 0; i++) await Task.Delay(25);
            await Within(f.Engine.GetModelPlanAsync(), "a second read waited for the ranking");
            Assert.Equal(1, gated.Waiting);

            gated.Release();
            await f.Engine.PlanRankSettledForTest();
            Assert.True(System.Threading.Volatile.Read(ref announced) > 0, "the page was never told the ranking landed");

            var after = await f.Engine.GetModelPlanAsync();
            Assert.True(after.Ranked);
            Assert.False(after.JevPending);
            Assert.Equal(last, after.Items.First().Id);
            Assert.Single(recorded.AskedDigests);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_plan_starts_no_ranking_run_and_says_the_order_is_Pro()
        {
            using var f = await GappyModelAsync("PlanRankFree.bim", pro: false);
            var oracle = f.UseEmptyRecorded();
            await f.ConsentAsync();

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.False(plan.Ranked);
            Assert.False(plan.JevPending);
            Assert.Contains("Pro", plan.OrderedBy, StringComparison.Ordinal);
            Assert.Equal(0, f.Engine.PlanRankRunsStartedForTest);
            Assert.Equal(0, oracle.AskCount);
        }

        /// <summary>A recorded answer for every open item on the plan, at one rung, so the ask is DECIDED and
        /// therefore cacheable. With <paramref name="top"/> that one item is scored above every other.</summary>
        private static string RankFixture(string digest, IReadOnlyList<ModelPlanItem> open, string top = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
            sb.Append(JsonSerializer.Serialize(digest));
            sb.Append(",\"point\":\"").Append(DecisionRegistry.PlanRank).Append('"');
            sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{");
            for (var i = 0; i < open.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(PlanRankPoint.QuestionFor(open[i].Id)));
                sb.Append(":{\"type\":\"score\",\"score\":\"").Append(open[i].Id == top ? "urgent" : "useful").Append("\",\"confidence\":0.95}");
            }
            sb.Append("}}]}");
            return sb.ToString();
        }

        // ---- the first look does not wait for the second scan -------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_first_look_does_not_wait_for_the_rehearsed_grade_and_says_so()
        {
            using var f = await GappyModelAsync("PlanCacheProjection.bim");

            var first = await f.Engine.GetModelPlanAsync();

            Assert.True(first.Worth.Pending);
            Assert.Null(first.Worth.To);
            Assert.Contains(first.Worth.Lines, l => l.Contains("still being worked out", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(first.Worth.From));   // the grade NOW is known immediately

            await f.Engine.PlanProjectionForTest;
            var settled = await f.Engine.GetModelPlanAsync();

            Assert.False(settled.Worth.Pending);
            Assert.False(string.IsNullOrWhiteSpace(settled.Worth.To));
            Assert.DoesNotContain(settled.Worth.Lines, l => l.Contains("still being worked out", StringComparison.Ordinal));

            // The rehearsal is not a gather, and it must not have cost one.
            Assert.Equal(1, f.Engine.PlanGatherCountForTest);
        }

        // ---- verify (m11/m4-relationships round 2, 26 Sep): the value order plan.rank now drives live ----------

        /// <summary>verify: plan.rank answers live since the 422 fix, so its value order reaches every Pro Plan.
        /// PlanRankPoint.RungValue says an answer below the bar "scores zero and keeps the deterministic order", but
        /// Order gives an item with no score -1, below the lowest rung ("none" is 0). So an item Jev is SURE is worth
        /// nothing ("doing this would change nothing anyone would notice") jumps above every item it could not score and
        /// leads the plan, under "In value order where it was sure enough". The reply is the vendor's own shape
        /// (a fractional score counted from zero, legend and probabilities keyed by position).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_an_item_Jev_is_sure_is_worth_nothing_never_leads_the_items_it_could_not_score()
        {
            using var f = await GappyModelAsync("PlanRankSureNone.bim");
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            var ruleOrder = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var digest = Assert.Single(probe.AskedDigests);
            var open = ruleOrder.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            Assert.True(open.Length >= 2, "the test needs two open items");
            var worthless = open[^1].Id;

            // The last rule-order item: "none" (0.1 lands on rung 0) at 0.95. Every other item: below the bar.
            var criteria = PlanRankPoint.Questions(open)[0].Options.Select(o => o.Criterion).ToArray();
            var legend = string.Join(",", criteria.Select((c, j) => "\"" + j + "\":" + JsonSerializer.Serialize(c)));
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
            sb.Append(JsonSerializer.Serialize(digest));
            sb.Append(",\"point\":\"").Append(DecisionRegistry.PlanRank).Append('"');
            sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{");
            for (var i = 0; i < open.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var sure = open[i].Id == worthless;
                sb.Append(JsonSerializer.Serialize(PlanRankPoint.QuestionFor(open[i].Id)))
                  .Append(":{\"type\":\"score\",\"score\":").Append(sure ? "0.1" : "3.8")
                  .Append(",\"confidence\":").Append(sure ? "0.95" : "0.5")
                  .Append(",\"legend\":{").Append(legend).Append("},\"probabilities\":")
                  .Append(sure ? "{\"0\":0.9,\"1\":0.1,\"2\":0,\"3\":0,\"4\":0}" : "{\"0\":0,\"1\":0,\"2\":0.1,\"3\":0,\"4\":0.9}")
                  .Append('}');
            }
            sb.Append("}}]}");
            f.Engine.UseDecisionsForTest(new DecisionService(new RecordedDecisionOracle(sb.ToString(), "jev-test-1"), new DecisionBudget()));

            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var ranked = await f.Engine.GetModelPlanAsync();

            Assert.True(ranked.Ranked, ranked.OrderedBy);
            var first = ranked.Items.First(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed);
            Assert.True(first.Id != worthless,
                "the item Jev is sure is worth nothing leads the plan, above every item it could not score: " + ranked.OrderedBy);
        }

        /// <summary>The value order and the rule order agree about an item Jev could not score: it stays where the rules
        /// put it, and an item Jev is sure is worth nothing ("none") sinks below those. Sure scores above "none" still
        /// lead. (The rungs are counted from zero, as the vendor's legend counts them.)</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Unscored_items_keep_rule_order_and_a_sure_none_sinks_below_them()
        {
            using var f = await GappyModelAsync("PlanRankOrder.bim");
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            var ruleOrder = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var digest = Assert.Single(probe.AskedDigests);
            // An OPTIONAL row (the cleanup, m11/plan-harm) sorts after every other row whatever Jev says, so it takes no part in this
            // order; it is still asked (a reply must answer every question), and it gets the default answer below.
            var asked = ruleOrder.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            var open = asked.Where(i => !i.Optional).ToArray();
            Assert.True(open.Length >= 3, "the test needs three open items");
            var worthless = open[0].Id;           // first in rule order, sure it is worth nothing
            var urgent = open[^1].Id;             // last in rule order, sure it is urgent

            var reply = new Dictionary<string, (string Score, string Confidence)>
            {
                [worthless] = ("0", "0.95"),
                [urgent] = ("4", "0.95"),
            };
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
            sb.Append(JsonSerializer.Serialize(digest)).Append(",\"point\":\"").Append(DecisionRegistry.PlanRank).Append('"');
            sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{");
            for (var i = 0; i < asked.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var (score, confidence) = reply.TryGetValue(asked[i].Id, out var r) ? r : ("2", "0.4");
                sb.Append(JsonSerializer.Serialize(PlanRankPoint.QuestionFor(asked[i].Id)))
                  .Append(":{\"type\":\"score\",\"score\":").Append(score).Append(",\"confidence\":").Append(confidence).Append('}');
            }
            sb.Append("}}]}");
            f.Engine.UseDecisionsForTest(new DecisionService(new RecordedDecisionOracle(sb.ToString(), "jev-test-1"), new DecisionBudget()));

            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var ranked = await f.Engine.GetModelPlanAsync();

            Assert.True(ranked.Ranked, ranked.OrderedBy);
            var order = ranked.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed && !i.Optional).Select(i => i.Id).ToArray();
            var unscored = open.Select(i => i.Id).Where(id => id != worthless && id != urgent).ToArray();
            Assert.Equal(new[] { urgent }.Concat(unscored).Concat(new[] { worthless }).ToArray(), order);
        }

        /// <summary>A ranking the reader refused leaves the rule order, and the Plan says so in plain words. It never
        /// shows the reader's own sentence (question names, "legend", "rung") to the person.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refused_ranking_says_so_in_plain_words()
        {
            using var f = await GappyModelAsync("PlanRankRefused.bim");
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            var ruleOrder = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var digest = Assert.Single(probe.AskedDigests);
            var open = ruleOrder.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();

            // Every answer carries a legend naming criteria that were never sent.
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
            sb.Append(JsonSerializer.Serialize(digest)).Append(",\"point\":\"").Append(DecisionRegistry.PlanRank).Append('"');
            sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{");
            for (var i = 0; i < open.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(PlanRankPoint.QuestionFor(open[i].Id)))
                  .Append(":{\"type\":\"score\",\"score\":3,\"confidence\":0.9,\"legend\":{\"0\":\"something else\"}}");
            }
            sb.Append("}}]}");
            f.Engine.UseDecisionsForTest(new DecisionService(new RecordedDecisionOracle(sb.ToString(), "jev-test-1"), new DecisionBudget()));

            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.False(plan.Ranked);
            Assert.Contains("order Semanticus found", plan.OrderedBy, StringComparison.Ordinal);
            foreach (var jargon in new[] { "legend", "rung", "value:", "rejected", "'" })
                Assert.DoesNotContain(jargon, plan.OrderedBy, StringComparison.Ordinal);
        }

        /// <summary>A ranking the service would not give says why in its own words: whole sentences are kept as they are, with
        /// one full stop, and Jev keeps its capital. The line used to put a second full stop after the relay's sentence, and
        /// wrote "jev was asked but did not answer" for the development route's reason (ai.schema final verify, low item 6).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Cloud advice is paused right now. Nothing else has changed.",
                    "Jev did not put the tasks in order this time because cloud advice is paused right now, so they are in the order Semanticus found them.")]
        [InlineData(Semanticus.Engine.Decisions.JevDecisionOracle.AskedThenStoppedReason,
                    "Jev did not answer this time and this session has used its suggestion budget, so the tasks are in the order Semanticus found them.")]
        [InlineData("the advice service did not answer in time",
                    "Jev did not put the tasks in order this time because the advice service did not answer in time, so they are in the order Semanticus found them.")]
        [InlineData("This licence has used its cloud advice for the month. It resets on 2026-10-01.",
                    "Jev did not put the tasks in order this time because this licence has used its cloud advice for the month, so they are in the order Semanticus found them.")]
        [InlineData(Semanticus.Engine.Decisions.JevDecisionOracle.SubscriptionInactiveReason,
                    "Jev did not put these in order because your Pro plan is not active. Open billing to fix it.")]
        [InlineData(Semanticus.Engine.Decisions.JevDecisionOracle.NotAvailableReason,
                    "Jev could not be reached this time, so the tasks are in the order Semanticus found them.")]
        public async Task A_ranking_the_service_would_not_give_says_why_in_one_sentence_with_one_full_stop(string reason, string expected)
        {
            using var f = await GappyModelAsync("PlanRankNotDecided.bim");
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new NotDecidingOracle(reason), new DecisionBudget()));

            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.False(plan.Ranked);
            Assert.Equal(expected, plan.OrderedBy);
            // One sentence: the only full stop is the last character (the old line joined the relay's two sentences with "because").
            var billing = reason == Semanticus.Engine.Decisions.JevDecisionOracle.SubscriptionInactiveReason;
            Assert.Equal(billing ? 2 : 1, plan.OrderedBy.Count(c => c == '.'));
            Assert.Equal(billing ? Semanticus.Engine.Entitlement.LicenseEntitlement.ManageUrl : null, plan.BillingUrl);
        }

        /// <summary>The relay says the subscription is not active: the header's entitlement says so too, with the billing address, so the
        /// screen never shows Pro and refuses Pro at once. A session that was never refused says nothing.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_inactive_subscription_reaches_the_entitlement_for_the_header()
        {
            using var f = await GappyModelAsync("PlanRankInactive.bim");
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            Assert.False((await f.Engine.GetEntitlementAsync()).PlanInactive);

            using var relay = new AlwaysRefuses();
            using var oracle = JevDecisionOracle.ForRelay("licence-token", "https://relay.example", new DecisionBudget(capInputTokens: 100000), relay);
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.Equal("Jev did not put these in order because your Pro plan is not active. Open billing to fix it.", plan.OrderedBy);
            var entitlement = await f.Engine.GetEntitlementAsync();
            Assert.True(entitlement.PlanInactive);
            Assert.Equal("pro", entitlement.Tier);
            Assert.Equal(Semanticus.Engine.Entitlement.LicenseEntitlement.ManageUrl, entitlement.ManageUrl);
        }

        private sealed class AlwaysRefuses : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct) =>
                Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"error\":\"subscription_inactive\",\"reason\":\"This subscription is not active. Check your billing details to use cloud advice again.\"}", Encoding.UTF8, "application/json"),
                });
        }

        /// <summary>A service that is asked and gives the same reason every time instead of an answer.</summary>
        private sealed class NotDecidingOracle : IDecisionOracle
        {
            private readonly string _reason;
            public NotDecidingOracle(string reason) { _reason = reason; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct) =>
                Task.FromResult(DecisionAnswers.NotDecided(request, _reason));
        }

        /// <summary>Verifier round 3: "Save the changes you have made" leaves the plan when the changes are saved AND
        /// when they are thrown away (a restart's Discard re-opens the files). Only the first is done: a discard
        /// saves nothing, so it is neither recorded as done nor counted as done in History.</summary>
        [Fact]
        public async Task Discarding_unsaved_changes_is_not_saving_them()
        {
            using var f = await GappyModelAsync("PlanDiscard.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");

            await f.Engine.OpenAsync(System.IO.Path.Combine(f.Workspace, "PlanDiscard.bim"), true);
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Id == "ship/unsaved");
            var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn || h.Kind == ModelPlanHistoryKinds.Drawn);
            Assert.DoesNotContain(" done", redraw.Sentence ?? "", StringComparison.Ordinal);
            Assert.Empty(redraw.Completed);

            // Saving is done.
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");
            await f.Engine.SaveAsync(null, null, false, "human");
            var saved = await f.Engine.GetModelPlanAsync();
            Assert.Contains(saved.Items, i => i.Id == "ship/unsaved" && i.Status == ModelPlanStatuses.Done);
        }

        /// <summary>D14 (seen live in UAT lane o): a redraw that read the model BEFORE a save and finished AFTER it
        /// used to note the row with the save count of the moment it finished. The next redraw then found no save
        /// since the row was seen, and filed "Save the changes you have made" off the plan instead of done. The save
        /// count is read with the facts, so the row is noted at the count the model was dirty at.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_save_that_lands_while_a_redraw_is_reading_is_still_done()
        {
            using var f = await GappyModelAsync("PlanSaveMidRedraw.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");

            // A second edit, like the save's own stamp, so the next read is a real redraw of a dirty model.
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch Two", "2", "human");
            var parked = await ParkOneRedrawAsync(f, () => f.Engine.SaveAsync(null, null, false, "human"));
            Assert.Contains(parked.Items, i => i.Id == "ship/unsaved" && i.Status != ModelPlanStatuses.Done);

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(plan.Items, i => i.Id == "ship/unsaved" && i.Status == ModelPlanStatuses.Done);
            var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            Assert.Contains("Save the changes you have made", redraw.Completed);
        }

        /// <summary>The other half of the same read order: a redraw that read a model made clean by Undo (nothing was
        /// saved) and finished after a later edit and save must not count that later save. The row left because of
        /// the Undo, so it is off the plan and not done.</summary>
        [Fact]
        public async Task A_save_that_lands_after_a_redraw_read_an_undone_model_is_not_counted_for_it()
        {
            using var f = await GappyModelAsync("PlanUndoMidRedraw.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");

            await f.Engine.UndoAsync("human");
            var parked = await ParkOneRedrawAsync(f, async () =>
            {
                await f.Engine.CreateMeasureAsync("table:Sales", "Scratch Two", "2", "human");
                await f.Engine.SaveAsync(null, null, false, "human");
            });

            Assert.DoesNotContain(parked.Items, i => i.Id == "ship/unsaved");
            var redraw = parked.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            Assert.Contains("Save the changes you have made", redraw.Gone);
            Assert.DoesNotContain("Save the changes you have made", redraw.Completed);
        }

        /// <summary>Starts one plan read, holds it just after it has read the model, runs <paramref name="between"/>
        /// while it is held, then lets it finish and returns the plan it composed.</summary>
        private static async Task<ModelPlanView> ParkOneRedrawAsync(DecisionFixture f, Func<Task> between)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fired = 0;
            f.Engine.PlanComposeFactsReadForTest = async () =>
            {
                if (System.Threading.Interlocked.Exchange(ref fired, 1) != 0) return;
                reached.SetResult();
                await release.Task.ConfigureAwait(false);
            };
            try
            {
                var redraw = f.Engine.GetModelPlanAsync();
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
                try { await between().WaitAsync(TimeSpan.FromSeconds(30)); }
                finally { release.TrySetResult(); }
                return await redraw.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { f.Engine.PlanComposeFactsReadForTest = null; }
        }

        // ---- verify round 5 (m11/m4-relationships at b0fa003b, 27 Sep): defects ---------------------------------

        /// <summary>verify: "Save the changes you have made" is done only when the changes are SAVED. Here the change was
        /// undone (the model is back to what its files hold) and a save followed with nothing new to write, with no plan
        /// read between the undo and the save. SaveCount counts every save, so the next read finds a save since the row
        /// was seen and files the row done: History says "1 item done" for a change that was thrown away. Read once more
        /// between the undo and the save and the same row reads "off the plan"
        /// (A_save_that_lands_after_a_redraw_read_an_undone_model_is_not_counted_for_it says why: the row left because of
        /// the Undo). Which of the two a person is told must not depend on whether a read happened to land in between: an
        /// assistant that calls undo_change then save_model, or a person who presses Undo then Save before the Plan
        /// redraws, gets the false one.</summary>
        [Fact]
        public async Task Verify_an_undo_then_a_save_with_nothing_to_write_is_not_saving_the_changes()
        {
            using var f = await GappyModelAsync("PlanUndoThenSaveNothing.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");

            await f.Engine.UndoAsync("human");
            await f.Engine.SaveAsync(null, null, false, "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            var filedDone = redraw.Completed.Contains("Save the changes you have made")
                || plan.Items.Any(i => i.Id == "ship/unsaved" && i.Status == ModelPlanStatuses.Done);
            Assert.False(filedDone,
                "History says '" + redraw.Sentence + "' and files 'Save the changes you have made' as done, although the change was undone and the save had nothing new to write");
        }

        /// <summary>verify: a real save followed by an engine restart before the next plan read is filed "off the plan",
        /// not done. SavedSinceUnsavedRowSeen trusts only the session that saw the row and its comment says "Another
        /// session (a re-open, a restart) never did", but here the session that saw the row DID save before it ended.
        /// The same symptom as D14 (seen live in UAT lane o), by another order: edit, read, save, restart. An assistant
        /// that saves and a person who then restarts the engine, or reopens the model, before the Plan redraws, is told
        /// the save did not count.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_real_save_then_a_restart_before_the_next_read_is_still_done()
        {
            using var f = await GappyModelAsync("PlanSaveThenRestart.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");
            await f.Engine.SaveAsync(null, null, false, "human");

            // A restart: a second engine over the same files and the same workspace, so the same plan memory.
            using (var sessions = new SessionManager())
            using (var restarted = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace))
            {
                await restarted.OpenAsync(System.IO.Path.Combine(f.Workspace, "PlanSaveThenRestart.bim"));
                restarted.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance));
                var plan = await restarted.GetModelPlanAsync();
                var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
                Assert.True(redraw.Completed.Contains("Save the changes you have made"),
                    "the change was saved before the restart, but History says '" + redraw.Sentence + "' (gone: "
                    + string.Join("; ", redraw.Gone) + ")");
            }
        }

        // ---- review-fix round 5 (D15, D16): the save row is decided by what each save wrote ----------------------

        /// <summary>D16's guard the other way: the mark a save leaves for the next engine counts only saves AFTER the row
        /// was last seen. Save, edit again, read (the row is back), then a restart that discards: those later changes
        /// were thrown away, so the row is off the plan, although the files hold an earlier save.</summary>
        [Fact]
        public async Task Changes_made_after_a_save_and_discarded_by_a_restart_are_off_the_plan()
        {
            using var f = await GappyModelAsync("PlanSaveEditRestart.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");
            await f.Engine.SaveAsync(null, null, false, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch Two", "2", "human");
            var dirty = await f.Engine.GetModelPlanAsync();
            Assert.Contains(dirty.Items, i => i.Id == "ship/unsaved" && i.Status != ModelPlanStatuses.Done);

            using (var sessions = new SessionManager())
            using (var restarted = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace))
            {
                await restarted.OpenAsync(System.IO.Path.Combine(f.Workspace, "PlanSaveEditRestart.bim"));
                restarted.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance));
                var plan = await restarted.GetModelPlanAsync();
                var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
                Assert.Contains("Save the changes you have made", redraw.Gone);
                Assert.DoesNotContain("Save the changes you have made", redraw.Completed);
            }
        }

        // ---- the save row's supporting guards (L3 of the joint verifier's relationships check, its probes adopted) ------

        private static string PlanMemoryPath(DecisionFixture f) => System.IO.Path.Combine(f.Workspace, ".semanticus", "plan-memory.json");

        /// <summary>A second engine over the same files and workspace: a restart, which throws unsaved changes away.</summary>
        private static async Task<(SessionManager Sessions, LocalEngine Engine)> RestartAsync(DecisionFixture f, string file)
        {
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await e.OpenAsync(System.IO.Path.Combine(f.Workspace, file));
            e.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance));
            return (sessions, e);
        }

        /// <summary>A save writes its mark only once its session's Plan has read the model unsaved. Without that gate a later
        /// session's save the Plan never saw overwrote the mark of the save that did answer the row, and a real save then
        /// read "off the plan" two restarts on (the verifier's X07: edit, read, save, restart, edit, save, restart, read).
        /// The same gate keeps such a save from touching the memory file at all.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_save_the_plan_never_saw_unsaved_leaves_the_earlier_saves_mark_and_the_file_alone()
        {
            using var f = await GappyModelAsync("PlanSaveGate.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");
            await f.Engine.SaveAsync(null, null, false, "human");

            var (s2, e2) = await RestartAsync(f, "PlanSaveGate.bim");
            using (s2)
            using (e2)
            {
                var before = System.IO.File.ReadAllBytes(PlanMemoryPath(f));
                await e2.CreateMeasureAsync("table:Sales", "Scratch Two", "2", "human");
                await e2.SaveAsync(null, null, false, "human");
                Assert.True(before.SequenceEqual(System.IO.File.ReadAllBytes(PlanMemoryPath(f))),
                    "a save the Plan never read unsaved changed the plan memory: " + System.IO.File.ReadAllText(PlanMemoryPath(f)));
            }

            var (s3, e3) = await RestartAsync(f, "PlanSaveGate.bim");
            using (s3)
            using (e3)
            {
                var plan = await e3.GetModelPlanAsync();
                var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
                Assert.True(redraw.Completed.Contains("Save the changes you have made"),
                    "the change was saved in the first engine, but two restarts on History says '" + redraw.Sentence + "'");
            }
        }

        /// <summary>The row's mark (where the unsaved row was last seen) is cleared once the row is off the plan, so the file
        /// carries it only while the row is there to be answered, and a read after that writes nothing new.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_unsaved_rows_mark_leaves_the_plan_memory_with_the_row()
        {
            using var f = await GappyModelAsync("PlanSeenClears.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");
            Assert.Contains("\"unsavedSeen\"", System.IO.File.ReadAllText(PlanMemoryPath(f)), StringComparison.Ordinal);
            await f.Engine.SaveAsync(null, null, false, "human");
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved" && i.Status != ModelPlanStatuses.Done);
            Assert.DoesNotContain("\"unsavedSeen\"", System.IO.File.ReadAllText(PlanMemoryPath(f)), StringComparison.Ordinal);
        }

        /// <summary>Across sessions only a save made by the session that SAW the row unsaved answers it. A save mark from any
        /// other session (a memory file two engines wrote, or one carried in with the project) says nothing about the changes
        /// that session saw, so the row is off the plan, never done.</summary>
        [Fact]
        public async Task Another_sessions_save_mark_never_answers_the_row_a_session_saw()
        {
            using var f = await GappyModelAsync("PlanSaveOtherSession.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");

            var (s2, e2) = await RestartAsync(f, "PlanSaveOtherSession.bim");
            using (s2)
            using (e2)
            {
                Assert.True(e2.SeedPlanMemoryForTest(state =>
                {
                    state.UnsavedSeen = new PlanSaveMark { Session = "session-that-saw-it", Saves = 0 };
                    state.LastSave = new PlanSaveMark { Session = "some-other-session", Saves = 3 };
                }));
                var plan = await e2.GetModelPlanAsync();
                var redraw = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
                Assert.Contains("Save the changes you have made", redraw.Gone);
                Assert.DoesNotContain("Save the changes you have made", redraw.Completed);
            }
        }

        // ---- review-fix round 5: plan.rank waits for the families (ai.schema verifier, round 4) ------------------

        /// <summary>A slow family service: every ask of a Jev family waits until the test lets it go. The plan's value
        /// ranking passes straight through, and its asks are counted, with those cancelled before they answered.</summary>
        private sealed class GatedFamilyOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _familyWaiting, _rankAsks, _rankCancelled;
            public GatedFamilyOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public int FamilyWaiting => System.Threading.Volatile.Read(ref _familyWaiting);
            public int RankAsks => System.Threading.Volatile.Read(ref _rankAsks);
            public int RankCancelled => System.Threading.Volatile.Read(ref _rankCancelled);
            public void Release() => _release.TrySetResult(true);
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct)
            {
                if (string.Equals(request.PointName, DecisionRegistry.PlanRank, StringComparison.Ordinal))
                {
                    System.Threading.Interlocked.Increment(ref _rankAsks);
                    try { return await _inner.AskAsync(request, ct); }
                    catch (OperationCanceledException) { System.Threading.Interlocked.Increment(ref _rankCancelled); throw; }
                }
                System.Threading.Interlocked.Increment(ref _familyWaiting);
                try { await _release.Task.WaitAsync(ct); }
                finally { System.Threading.Interlocked.Decrement(ref _familyWaiting); }
                return await _inner.AskAsync(request, ct);
            }
        }

        /// <summary>The ai.schema verifier (round 4) saw one plan.rank ask fail on every open after a restart. The first
        /// read of a revision starts the families and, in the same compose, asked the ranking for the rows without Jev's.
        /// When the families landed, the page's next read ranked a different list and cancelled the first ask, which was
        /// charged and logged "outcome=failed". While the families are still working the plan out, no ranking is asked;
        /// the read after they land asks it once.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_ranking_is_not_asked_while_the_families_are_still_working_out_the_plan()
        {
            using var f = await GappyModelAsync("PlanRankAfterFamilies.bim");
            var recorded = f.UseEmptyRecorded();
            await f.ConsentAsync();
            var gated = new GatedFamilyOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(gated, new DecisionBudget()));

            var first = await Within(f.Engine.GetModelPlanAsync(), "get_model_plan waited for the families");
            Assert.True(first.JevPending);
            for (var i = 0; i < 400 && gated.FamilyWaiting == 0; i++) await Task.Delay(25);
            Assert.True(gated.FamilyWaiting > 0, "the test needs a family ask held open");
            await f.Engine.PlanRankSettledForTest();
            await Within(f.Engine.GetModelPlanAsync(), "a second read waited for the families");
            await f.Engine.PlanRankSettledForTest();
            Assert.True(gated.RankAsks == 0,
                "the ranking was asked " + gated.RankAsks + " time(s) while the families were still working out the plan");

            gated.Release();
            await f.Engine.JevSettledForTest();
            await f.Engine.GetModelPlanAsync();          // the read the families' landing asks the page for
            await f.Engine.PlanRankSettledForTest();
            var settled = await f.Engine.GetModelPlanAsync();
            Assert.Equal(1, gated.RankAsks);
            Assert.Equal(0, gated.RankCancelled);
            Assert.False(settled.JevPending);
        }
    }
}
