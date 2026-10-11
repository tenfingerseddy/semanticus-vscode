// The strip's data (Plan at the heart, lane E5; contract 9).
//
// What these tests hold, one line each:
//   the counts per track and state are the rows' own state words, in the order Semanticus, You, Your assistant
//   done counts only what finished today, on its own track
//   latest is the newest edit ledger entry still on the undo stack
//   the "Jev updated this" line only when Jev ran on Pro; free and a Pro plan Jev did not touch read the rules' line
//   the ready digest moves with a hold and comes back when the hold is lifted

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanStripTests
    {
        private static Task<DecisionFixture> WideAsync(string tag) =>
            CommutationFixtures.All.Single(x => x.Name == "Wide").Copy(tag);

        private static async Task<DecisionFixture> SmallAsync(string file, bool pro)
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

        [Fact]
        public async Task The_counts_per_track_and_state_are_the_rows_own_state_words()
        {
            using var f = await WideAsync("StripCounts");
            await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Describe the Sales measures" });
            await f.Engine.HoldPlanItemAsync("safefix/Visibility", true);
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.NotNull(plan.Strip);
            Assert.Equal(new[] { PlanDoers.Semanticus, PlanDoers.You, PlanDoers.Assistant }, plan.Strip.Tracks.Select(t => t.Doer));
            foreach (var track in plan.Strip.Tracks)
            {
                var rows = plan.Items.Where(i => i.Doer == track.Doer).ToArray();
                int Count(params string[] words) => rows.Count(i => words.Contains(i.StateWord));
                Assert.True(Count(PlanStateWords.Ready) == track.Ready, track.Doer + " ready");
                Assert.True(Count(PlanStateWords.NeedsYou) == track.NeedsYou, track.Doer + " needs you");
                Assert.True(Count(PlanStateWords.OnIt) == track.OnIt, track.Doer + " on it");
                Assert.True(Count(PlanStateWords.Waiting, PlanStateWords.Paused) == track.Waiting, track.Doer + " waiting");
            }
            // The held Semanticus row is a dashed segment on its own track.
            Assert.True(plan.Strip.Tracks[0].Waiting >= 1);
            // The person's task is on the assistant's track, ready.
            Assert.True(plan.Strip.Tracks[2].Ready >= 1);
            Assert.Equal(plan.Worth.From, plan.Strip.GradeNow);
            Assert.Equal(plan.Worth.To, plan.Strip.GradeAfter);
        }

        [Fact]
        public void Done_counts_only_today_on_its_own_track()
        {
            var now = DateTimeOffset.UtcNow;
            ModelPlanItem Row(string id, string doer, string word) =>
                new ModelPlanItem { Id = id, Doer = doer, StateWord = word };
            var items = new[]
            {
                Row("a", PlanDoers.Semanticus, PlanStateWords.Done),
                Row("b", PlanDoers.Semanticus, PlanStateWords.Checking),
                Row("c", PlanDoers.You, PlanStateWords.Done),
                Row("d", PlanDoers.Assistant, PlanStateWords.Done),
                Row("e", PlanDoers.Semanticus, PlanStateWords.Done),   // no known time: not claimed as today
            };
            var when = new Dictionary<string, DateTimeOffset>
            {
                ["a"] = now, ["b"] = now, ["c"] = now.AddDays(-2), ["d"] = now,
            };

            var strip = PlanStripBuilder.Build(items, null, new PlanStripInputs { DoneWhen = when, Now = now });

            Assert.Equal(2, strip.Tracks.Single(t => t.Doer == PlanDoers.Semanticus).Done);
            Assert.Equal(0, strip.Tracks.Single(t => t.Doer == PlanDoers.You).Done);
            Assert.Equal(1, strip.Tracks.Single(t => t.Doer == PlanDoers.Assistant).Done);
        }

        [Fact]
        public async Task Latest_is_the_newest_ledger_entry()
        {
            using var f = await SmallAsync("StripLatest.bim", pro: true);
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "First.", "human");
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "Second.", "agent");
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.NotNull(plan.Strip.Latest);
            Assert.Equal("assistant", plan.Strip.Latest.By);
            Assert.Equal(plan.Strip.Latest.When, plan.Strip.Updated.When);
        }

        [Fact]
        public async Task Free_reads_the_rules_line_never_jev()
        {
            using var f = await SmallAsync("StripFree.bim", pro: false);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStripUpdatedBy.Semanticus, plan.Strip.Updated.By);
        }

        [Fact]
        public async Task A_pro_plan_jev_did_not_touch_reads_the_rules_line()
        {
            using var f = await SmallAsync("StripProNoJev.bim", pro: true);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.False(plan.Ranked);
            Assert.Equal(PlanStripUpdatedBy.Semanticus, plan.Strip.Updated.By);
        }

        /// <summary>Jev ranked the plan on Pro: the strip says Jev updated it. Same two-step as ModelPlanCacheTests:
        /// learn the ranking's digest with an empty oracle, then answer it.</summary>
        [Fact]
        public async Task The_jev_line_only_when_jev_ran_on_pro()
        {
            using var f = await SmallAsync("StripJev.bim", pro: true);
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            // The column format, category and summarize families ask too on this model; switched off so the only ask is the ranking.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            // So does what Copilot sees (ai.schema); switched off for the same reason.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");

            var first = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            Assert.Equal(PlanStripUpdatedBy.Semanticus, first.Strip.Updated.By);
            var digest = Assert.Single(probe.AskedDigests);
            var open = first.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();

            f.UseRecorded(RankFixture(digest, open));
            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var ranked = await f.Engine.GetModelPlanAsync();

            Assert.True(ranked.Ranked);
            Assert.Equal(PlanStripUpdatedBy.Jev, ranked.Strip.Updated.By);
        }

        /// <summary>An oracle that is set up and fails every ask, the way the real one does when the service is down:
        /// a plain not-decided answer, never a throw.</summary>
        private sealed class FailingOracle : IDecisionOracle
        {
            public int Asked;
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct)
            {
                System.Threading.Interlocked.Increment(ref Asked);
                return Task.FromResult(DecisionAnswers.NotDecided(request, "the service could not be reached"));
            }
        }

        /// <summary>The measures point is on, consent is given, and every ask fails. Jev was asked and answered
        /// nothing, so the strip must not say Jev updated the plan (honesty lane).</summary>
        [Fact]
        public async Task Every_ask_failing_reads_the_rules_line_not_jev()
        {
            using var f = await SmallAsync("StripJevFailed.bim", pro: true);
            var oracle = new FailingOracle();
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            await f.ConsentAsync();

            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            await f.Engine.PlanRankSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            plan = await f.Engine.GetModelPlanAsync();

            Assert.True(oracle.Asked > 0, "the point was never asked, so this test proves nothing");
            Assert.False(plan.JevPending);
            Assert.False(plan.Ranked);
            Assert.Equal(PlanStripUpdatedBy.Semanticus, plan.Strip.Updated.By);
        }

        [Fact]
        public async Task The_ready_digest_moves_with_a_hold_and_comes_back_when_it_is_lifted()
        {
            using var f = await WideAsync("StripDigest");
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var i in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed))
                await f.Engine.SkipModelPlanItemAsync(i.Id, "out of the way", "human");
            var before = await f.Engine.GetModelPlanAsync();
            Assert.Matches("^[0-9a-f]{64}$", before.ReadyDigest);

            var held = await f.Engine.HoldPlanItemAsync("safefix/Visibility", true);
            Assert.NotEqual(before.ReadyDigest, held.ReadyDigest);
            Assert.Equal(before.Revision, held.Revision);

            var back = await f.Engine.HoldPlanItemAsync("safefix/Visibility", false);
            Assert.Equal(before.ReadyDigest, back.ReadyDigest);
        }

        private static string RankFixture(string digest, IReadOnlyList<ModelPlanItem> open)
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
                sb.Append(":{\"type\":\"score\",\"score\":\"useful\",\"confidence\":0.95}");
            }
            sb.Append("}}]}");
            return sb.ToString();
        }
    }
}
