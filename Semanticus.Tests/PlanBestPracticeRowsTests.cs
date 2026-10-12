// The three Plan rows for "build a model from your SQL source, press Run the plan, give the words to your assistant"
// (lane m11/plan-rows, Kane 28 Sep). Each was red before the rows existed.
//
//   bpafix/model  "Fix N best-practice issues": Run the plan takes it, one undo step, an honest count, and no format
//                 it cannot prove is ever overwritten. Pro runs it; Free sees it and is told Run the plan is Pro.
//   words/model   "Write 3 descriptions and ...": one row, one queue call on the agent door in grade order, and it
//                 never waits behind the unused-object cleanup.
//   Starts        an empty model offers "From a SQL source" and "New blank model" instead of an empty plan.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PlanBestPracticeRowsTests
    {
        private static int OpenAutoFixes(Semanticus.Analysis.BpaScorecard card) =>
            card.Violations.Count(v => v.CanAutoFix && !v.Waived);

        [Fact]
        public async Task The_plan_has_one_semanticus_row_for_the_best_practice_fixes_with_an_honest_count()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("BpaRow.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Row(plan, LocalEngine.BpaFixItemId);
            Assert.Equal(ModelPlanOwners.Auto, row.Who);
            Assert.StartsWith("Fix ", row.Title);
            Assert.EndsWith(row.Title.Contains(" findings") ? "Model quality findings" : "Model quality finding", row.Title);

            // The count on the row is the number of lines it works out: nothing more, nothing less.
            var n = int.Parse(row.Title.Split(' ')[1]);
            var worked = await f.Engine.ModelPlanActionAsync(LocalEngine.BpaFixItemId, "propose", "human");
            var set = Row(worked.Plan, LocalEngine.BpaFixItemId).ChangeSet;
            Assert.Equal(n, set.Lines.Length);
            Assert.All(set.Lines, l => Assert.Equal("bpa_fix", l.Op));
        }

        [Fact]
        public async Task The_best_practice_and_safe_fix_rows_name_their_changes_in_plain_words()
        {
            // hero16 section 10: "[Formatting] Do not summarize numeric columns" is the rule list's sorting word in brackets, and
            // "Fix 2 safe things in naming" does not say what changes. Neither reaches the Plan.
            using var f = await ChangeCauseTests.PlanModelAsync("BpaPlain.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Row(plan, LocalEngine.BpaFixItemId);
            Assert.DoesNotMatch(@"\[[A-Za-z ]+\]", row.Sub ?? "");
            Assert.All(row.Diff, d => Assert.DoesNotMatch(@": \[[A-Za-z ]+\] ", d));
            foreach (var safe in plan.Items.Where(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("safe thing", safe.Title, StringComparison.Ordinal);
                Assert.Matches(@"^Fix \d+ [A-Za-z ]+ problems?$", safe.Title);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_the_plan_takes_the_best_practice_row_and_one_undo_puts_it_back()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("BpaRun.bim");
            var before = await f.Engine.BpaScanAsync();
            Assert.True(OpenAutoFixes(before) > 0);

            // The row is the last pass: it waits for the readiness safe fixes on its objects, then runs.
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(plan.Items, i => i.Id == LocalEngine.BpaFixItemId);
            var presses = 0;
            ApplyPlanItemsResult ran = null;
            while (presses++ < 4)
            {
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) break;
                ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                Assert.True(ran.Performed, ran.Sentence);
                if (ran.ItemIds.Contains(LocalEngine.BpaFixItemId)) break;
            }
            Assert.Contains(LocalEngine.BpaFixItemId, ran.ItemIds);
            await f.Engine.UndoAsync("human");   // step back only the press that ran the row
            before = await f.Engine.BpaScanAsync();
            await f.Engine.RedoAsync("human");

            var after = await f.Engine.BpaScanAsync();
            Assert.True(after.ViolationCount < before.ViolationCount, $"{before.ViolationCount} -> {after.ViolationCount}");
            // A press that uncovers new fixes opens the row again as Ready work, never "checking" (next test).
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items,
                i => i.Id == LocalEngine.BpaFixItemId && i.StateWord == PlanStateWords.Checking);

            // One press is one step on the undo timeline.
            await f.Engine.UndoAsync("human");
            Assert.Equal(before.ViolationCount, (await f.Engine.BpaScanAsync()).ViolationCount);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Fixes_a_press_uncovers_open_the_row_again_instead_of_leaving_it_checking()
        {
            // Hiding a key is what makes it qualify for ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS, so the first press
            // uncovers fixes no scan before it could list (journey raw-pro-words2: "Fix 17", stuck on checking).
            using var f = await ChangeCauseTests.PlanModelAsync("BpaAgain.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            for (var press = 0; press < 4; press++)
            {
                plan = await f.Engine.GetModelPlanAsync();
                var row = plan.Items.FirstOrDefault(i => i.Id == LocalEngine.BpaFixItemId && PlanStateWords.IsOpen(i.StateWord));
                if (row == null) break;
                Assert.NotEqual(PlanStateWords.Checking, row.StateWord);
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                Assert.True(ran.Performed, ran.Sentence);
            }
            var left = (await f.Engine.BpaScanAsync()).Violations.Where(v => v.CanAutoFix && !v.Waived).ToArray();
            // Only what the row refuses on purpose: a format already set, or a data type change.
            Assert.All(left, v => Assert.True(v.RuleId.Contains("FORMAT") || v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES",
                v.RuleId + " " + v.ObjectRef));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_best_practice_row_never_overwrites_a_format_the_model_already_has()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("BpaFormat.bim");
            await f.Engine.SetMeasureFormatAsync("measure:Sales/Total Sales", "#,##0.00", "agent");
            Assert.Contains((await f.Engine.BpaScanAsync()).Violations,
                v => v.RuleId == "INTEGER_FORMATTING" && v.ObjectRef == "measure:Sales/Total Sales" && v.Advice && !v.CanAutoFix);   // the rule still raises it (join: with a decimal format, since "#,##0" is no longer raised, m11/rule-clashes); its fix is withheld over an existing format (m11/feedback-rules)

            var worked = await f.Engine.ModelPlanActionAsync(LocalEngine.BpaFixItemId, "propose", "human");
            var set = Row(worked.Plan, LocalEngine.BpaFixItemId).ChangeSet;
            Assert.DoesNotContain(set.Lines, l => l.Target == "measure:Sales/Total Sales" && l.Property == "formatstring");
            Assert.Contains(set.Refused, r => r.Target == "measure:Sales/Total Sales" && r.Reason.Contains("#,##0.00"));

            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            var props = (await f.Engine.GetObjectAsync("measure:Sales/Total Sales")).Properties;
            Assert.Equal("#,##0.00", props.First(p => string.Equals(p.Key, "FormatString", System.StringComparison.OrdinalIgnoreCase)).Value?.ToString());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_sees_the_best_practice_row_and_is_told_running_it_is_pro()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("BpaFree.bim", TestEntitlements.Free);
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Row(plan, LocalEngine.BpaFixItemId);
            Assert.Equal(ModelPlanOwners.Auto, row.Who);
            var worked = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");   // working out is free
            var digest = Row(worked.Plan, row.Id).ChangeSet.Digest;
            var approve = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);
            Assert.False(approve.Performed);
            Assert.StartsWith(LocalEngine.RunIsProLead, approve.Sentence);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_words_row_hands_the_whole_queue_over_and_never_waits_for_the_cleanup()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Words.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var words = Row(plan, LocalEngine.WordsItemId);
            // ONE words row, the descriptions in it (round 1, 8 Oct): the title names what is written, never a bare "give the words".
            Assert.StartsWith("Write ", words.Title);
            Assert.Equal(PlanDoers.Assistant, words.Doer);
            Assert.Contains(LocalEngine.WordsLine, words.Why);
            Assert.DoesNotContain(words.WaitsFor ?? System.Array.Empty<PlanWaitRef>(), w => w.Id == "cleanup/model");
            // There is no describe row to wait for: its descriptions are part of this row.
            Assert.DoesNotContain(plan.Items, i => i.Id == "describe/model");

            // The agent door: the whole queue in one call, the gate first, then by points.
            var queue = await McpTools.GetWordsQueue(f.Engine);
            Assert.Equal(LocalEngine.WordsLine, queue.Line);
            Assert.Equal(queue.Total, queue.Items.Length);
            Assert.NotEmpty(queue.Items);
            // A measure format Semanticus cannot prove is a choice in the queue too (THE FORMAT FILL RULE, review-fix round 2),
            // and so are the AI instructions once the synonym store exists, and a key's or date's column format (set_property FormatString,
            // plan statement check 2 item 3a: no readiness rule asks for it, the best-practice rule does).
            Assert.All(queue.Items, x => Assert.Contains(x.Tool, new[] { "write_words", "edit_objects", "create_linguistic_schema", "set_ai_instructions" }));
            var keys = queue.Items.Select(x => (x.HoldsGate ? 1 : 0, x.Lift)).ToArray();
            Assert.Equal(keys.OrderByDescending(k => k.Item1).ThenByDescending(k => k.Lift).ToArray(), keys);
            Assert.NotNull(queue.Grounding[queue.Items[0].ObjectRef]);
            Assert.Equal(2, (await McpTools.GetWordsQueue(f.Engine, 2)).Items.Length);
        }

        [Fact]
        public async Task An_empty_model_offers_the_ways_to_start_on_both_doors()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Empty", 1604);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(new[] { "From a SQL source", "New blank model" }, plan.Starts.Select(s => s.Label));
            Assert.Equal("spec", plan.Starts[0].Jump.Tool);
            Assert.Equal(new[] { "sql", "blank" }, (await McpTools.GetModelPlan(f.Engine)).Starts.Select(s => s.Id));

            await f.Engine.CreateTableAsync("Sales", "human");
            Assert.Empty((await f.Engine.GetModelPlanAsync()).Starts);
        }
    }
}
