// Review-fix round 2 of lane m11/plan-rows (28 Sep): the tests the verifier's six (VerifyPlanRowsTests) do not cover.
// Each was red before its fix.
//
//   THE FORMAT FILL RULE  Run the plan fills an empty measure format only where the engine proves a whole number
//                         (a count, or SUM/MIN/MAX of an Int64 column), and every other format becomes a choice in the
//                         words queue, with the rule's advice kept in words on the best-practice row.
//   The words queue       pages, on both doors, so a large model fits an assistant's reading limit (verify M4).
//   L6, L7, L8            start on a done best-practice row, its key fix's footprint, and Free never told to approve.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PlanRowsFormatFillTests : IClassFixture<ModelTemplates>
    {
        // Lean suite (8 Oct): the plan model and the formats model are built once per class and each test opens its own copy.
        private readonly ModelTemplates _templates;
        public PlanRowsFormatFillTests(ModelTemplates templates) => _templates = templates;

        /// <summary>ChangeCauseTests.PlanModelAsync builds its own fixture, so the template build opens its saved file into the scratch one.</summary>
        private static async Task BuildPlanAsync(DecisionFixture scratch)
        {
            using var built = await ChangeCauseTests.PlanModelAsync("PlanBuild.bim");
            await scratch.Engine.OpenAsync(System.IO.Path.Combine(built.Workspace, "PlanBuild.bim"));
        }

        private async Task<DecisionFixture> PlanModelAsync(string file, Semanticus.Engine.Entitlement.IEntitlement tier = null)
        {
            var f = new DecisionFixture(tier ?? TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "plan", BuildPlanAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task<string> FormatOf(IEngine engine, string objRef) =>
            (await engine.GetObjectPropertiesAsync(objRef)).FirstOrDefault(p => p.Name == "FormatString")?.Value?.ToString() ?? "";

        private static async Task PressUntilQuietAsync(IEngine engine)
        {
            for (var press = 0; press < 5; press++)
            {
                var plan = await engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) return;
                var ran = await engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) return;
            }
        }

        private async Task<DecisionFixture> FormatsModelAsync(string file, Semanticus.Engine.Entitlement.IEntitlement tier = null)
        {
            var f = new DecisionFixture(tier ?? TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "formats", BuildFormatsAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildFormatsAsync(DecisionFixture f)
        {
            await BuildPlanAsync(f);
            await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await f.Engine.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            foreach (var (name, dax) in new[]
            {
                ("Order Count", "COUNTROWS ( Sales )"),
                ("Customers", "DISTINCTCOUNT ( Sales[CustomerKey] )"),
                ("Key Total", "SUM ( Sales[CustomerKey] )"),
                ("Units", "SUM ( Sales[Quantity] )"),
                ("Units Max", "MAX ( 'Sales'[Quantity] )"),
                ("Counted Again", "( [Order Count] )"),
                ("Share", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )"),
                ("Amount Total", "SUM ( Sales[Amount] )"),
                ("Filtered Count", "CALCULATE ( COUNTROWS ( Sales ), Sales[Amount] > 0 )"),
                ("Key Max", "MAX ( 'Sales'[CustomerKey] )"),
                ("Filtered Share", "CALCULATE ( DIVIDE ( SUM ( Sales[Amount] ), 2 ), Sales[Amount] > 0 )"),
                ("Filtered Amount", "CALCULATE ( SUM ( Sales[Amount] ), Sales[CustomerKey] > 0 )"),
                ("Half Count", "COUNTROWS ( Sales ) / 2"),
                ("Echo", "[Total Sales]"),
            })
                await f.Engine.CreateMeasureAsync("table:Sales", name, dax, "human");
        }

        private static readonly string[] Choices =
            // A sum of a key is a whole number but not a quantity: the rule never fills a key, an ID, a code or a flag.
            // MIN and MAX are never proved (m11/feedback-rules: a year, a key or a code is what they most often read), so "Units Max" is a choice too.
            { "Share", "Amount Total", "Filtered Share", "Filtered Amount", "Half Count", "Echo", "Total Sales", "Key Total", "Key Max", "Units Max",
              // Run the plan's own best-practice fix sets Sales[Quantity] not to add up in the same run, and a sum of a column that no longer adds
              // up by itself is no longer proved a quantity by structure (m11/feedback-rules; VerifyFormatFillRound13Tests pins the same case), so
              // the format Units would get is left for the words queue, where the assistant chooses it.
              "Units" };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_the_plan_fills_a_whole_number_format_only_where_the_engine_proves_it()
        {
            using var f = await FormatsModelAsync("FillProven.bim");
            await PressUntilQuietAsync(f.Engine);

            foreach (var proven in new[] { "Order Count", "Customers", "Counted Again", "Filtered Count" })
            {
                var got = await FormatOf(f.Engine, "measure:Sales/" + proven);
                var rows = got == "#,0" ? "" : string.Join(" | ", (await f.Engine.GetModelPlanAsync()).Items.Select(i =>
                    i.Id + " " + i.StateWord + "/" + i.Doer + " waits[" + string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)) + "]"));
                Assert.True(got == "#,0", proven + " has the format '" + got + "', not #,0. The plan after five presses: " + rows);
            }
            foreach (var choice in Choices)
                Assert.True((await FormatOf(f.Engine, "measure:Sales/" + choice)).Length == 0,
                    choice + " got a format Semanticus cannot prove: " + await FormatOf(f.Engine, "measure:Sales/" + choice));

            // Every format left unwritten is a choice in the words queue, handed to the assistant with its tool.
            var queue = await f.Engine.GetWordsQueueAsync();
            foreach (var choice in Choices)
                Assert.Contains(queue.Items, i => i.Kind == "format" && i.Tool == "set_measure_format" && i.ObjectRef == "measure:Sales/" + choice);
            Assert.DoesNotContain(queue.Items, i => i.Kind == "format" && i.ObjectRef == "measure:Sales/Order Count");
            Assert.Equal(Choices.Length, queue.Kinds["format"]);
            var words = Row(await f.Engine.GetModelPlanAsync(), LocalEngine.WordsItemId);
            Assert.Contains(words.Diff.Concat(words.Tables.SelectMany(t => t.Diff)), d => d.StartsWith("+ Sales[Share]:") && d.Contains("a format to choose"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_count_with_a_dynamic_format_never_gets_a_static_one_beside_it()
        {
            // A count is provably a whole number, so only the format string expression guard keeps #,0 off it.
            using var f = await PlanModelAsync("FillDynamicCount.bim");
            var count = await f.Engine.CreateMeasureAsync("table:Sales", "Dynamic Count", "COUNTROWS ( Sales )", "human");
            await f.Engine.SetMeasureFormatExpressionAsync(count, "\"#,0\"", "human");
            var plain = await f.Engine.CreateMeasureAsync("table:Sales", "Plain Count", "COUNTROWS ( Sales )", "human");
            await PressUntilQuietAsync(f.Engine);
            Assert.Equal("", await FormatOf(f.Engine, count));
            Assert.Equal("#,0", await FormatOf(f.Engine, plain));
        }

        [Fact]
        public async Task The_best_practice_row_keeps_the_rules_advice_in_words_for_a_format_it_cannot_prove()
        {
            using var f = await FormatsModelAsync("FillAdvice.bim");
            var worked = await f.Engine.ModelPlanActionAsync(LocalEngine.BpaFixItemId, "propose", "human");
            var set = Row(worked.Plan, LocalEngine.BpaFixItemId).ChangeSet;
            Assert.DoesNotContain(set.Lines, l => l.Target == "measure:Sales/Share");
            Assert.Contains(set.Lines, l => l.Target == "measure:Sales/Order Count" && l.After == "#,0");
            var why = Assert.Single(set.Refused.Where(r => r.Target == "measure:Sales/Share")).Reason;
            Assert.Contains("cannot prove", why);
            Assert.Contains("The rule suggests #,0.", why);
            Assert.DoesNotContain("—", why);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_words_queue_pages_on_both_doors_and_the_pages_add_up_to_the_whole_list()
        {
            using var f = await FormatsModelAsync("WordsPages.bim");
            var whole = await f.Engine.GetWordsQueueAsync();
            Assert.True(whole.Total > 6, "the model needs more than six words: " + whole.Total);
            Assert.Null(whole.Next);
            Assert.Equal(whole.Total, whole.Kinds.Values.Sum());

            var seen = new System.Collections.Generic.List<string>();
            int? offset = 0;
            while (offset != null)
            {
                var page = await f.Engine.GetWordsQueueAsync(3, offset.Value);
                Assert.Equal(whole.Total, page.Total);
                Assert.Equal(offset.Value, page.Offset);
                Assert.True(page.Items.Length <= 3);
                Assert.All(page.Items, i => Assert.NotNull(page.Grounding[i.ObjectRef]));
                seen.AddRange(page.Items.Select(i => i.RuleId + "|" + i.ObjectRef));
                offset = page.Next;
            }
            Assert.Equal(whole.Items.Select(i => i.RuleId + "|" + i.ObjectRef), seen);

            // The agent door and the person's pipe both page: never the whole list of a large model in one answer.
            Assert.Equal(40, LocalEngine.WordsPageSize(0));
            Assert.Equal(100, LocalEngine.WordsPageSize(5000));
            var mcp = await McpTools.GetWordsQueue(f.Engine, 2, 1);
            Assert.Equal(2, mcp.Items.Length);
            Assert.Equal(whole.Items.Skip(1).Take(2).Select(i => i.ObjectRef), mcp.Items.Select(i => i.ObjectRef));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Start_on_a_best_practice_row_whose_fixes_are_in_says_so_and_records_no_refusal()
        {
            using var f = await PlanModelAsync("BpaDoneStart.bim");
            await PressUntilQuietAsync(f.Engine);
            var row = (await f.Engine.GetModelPlanAsync()).Items.FirstOrDefault(i => i.Id == LocalEngine.BpaFixItemId);
            Assert.True(row != null && (row.State == ModelPlanItemStates.Done || row.State == ModelPlanItemStates.Applied),
                "the row's fixes are in after the presses: " + row?.State);
            var start = await f.Engine.ModelPlanActionAsync(LocalEngine.BpaFixItemId, "start", "human");
            Assert.False(start.Performed);
            Assert.Equal("Its fixes are already in, so nothing was changed.", start.Sentence);
            Assert.DoesNotContain(start.Plan.History ?? Array.Empty<ModelPlanHistoryEntry>(),
                h => h.ItemId == LocalEngine.BpaFixItemId && h.Kind == ModelPlanHistoryKinds.Refused);
        }

        [Fact]
        public async Task Free_is_never_told_to_approve_a_row_only_pro_can_approve()
        {
            using var f = await PlanModelAsync("BpaFreeWords.bim", TestEntitlements.Free);
            var worked = await f.Engine.ModelPlanActionAsync(LocalEngine.BpaFixItemId, "propose", "human");
            Assert.Contains("Approving them is part of Semanticus Pro.", worked.Sentence);
            Assert.DoesNotContain("then approve them", worked.Sentence);
            Assert.DoesNotContain("one at a time", LocalEngine.BulkRunIsProSentence);
        }

        [Fact]
        public async Task The_best_practice_key_fix_writes_the_key_as_a_date_mark_does()
        {
            // MARK_PRIMARY_KEYS sets Customer[CustomerKey] as the key; setting a key clears every other key on the table,
            // so the row writes the column and is ordered against the date mark and the key status rules.
            using var f = await PlanModelAsync("BpaKeyFootprint.bim");
            var row = Row(await f.Engine.GetModelPlanAsync(), LocalEngine.BpaFixItemId);
            Assert.Contains(row.Diff.Concat(row.Tables.SelectMany(t => t.Diff)), d => d.Contains("Mark primary keys"));
            Assert.Contains("column:Customer/CustomerKey", row.Writes);
            var worked = await f.Engine.ModelPlanActionAsync(LocalEngine.BpaFixItemId, "propose", "human");
            Assert.Contains("column:Customer/CustomerKey", Row(worked.Plan, LocalEngine.BpaFixItemId).Writes);
        }
    }
}
