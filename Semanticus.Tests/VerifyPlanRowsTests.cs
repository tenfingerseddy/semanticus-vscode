// Verifier's failing tests for lane m11/plan-rows at e21aa061 (28 Sep). Each one fails on the lane head and names a
// defect in the verifier's report (lane-reports/v-plan-rows.md). They are left failing on purpose: the fix makes them pass.
//
//   V1  Run the plan writes a whole-number format ("#,0") on a ratio measure that has no format, so 0.35 reads 0.
//   V2  Run the plan writes a static format beside a measure's dynamic format (Analysis Services allows only one).
//   V3  A new empty model opens a row ("Give the words to your assistant: 1 set of synonyms"), and the Studio shows the
//       ways to start only when no row is open, so a person never sees "From a SQL source" or "New blank model".
//   V4  The words queue leaves out words the assistant must write: the AI instructions, once the schema exists.
//   V5  The words row waits, through the person's rows, for the unused-object cleanup.
//   V6  The task the person gives from the words row never tells the assistant about the one call that hands it the queue.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class VerifyPlanRowsTests
    {
        private static async Task<string> PropertyOf(IEngine engine, string objRef, string name) =>
            (await engine.GetObjectPropertiesAsync(objRef)).FirstOrDefault(p => p.Name == name)?.Value?.ToString() ?? "";

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

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V1_run_the_plan_never_writes_a_whole_number_format_on_a_ratio_measure()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("VerifyRatio.bim");
            var ratio = await f.Engine.CreateMeasureAsync("table:Sales", "Utilisation",
                "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )", "human");
            await PressUntilQuietAsync(f.Engine);
            var format = await PropertyOf(f.Engine, ratio, "FormatString");
            Assert.False(format == "#,0" || format == "0" || format == "#,##0",
                "Run the plan wrote the whole-number format '" + format + "' on DIVIDE(...), so a share of 0.73 now reads 1.");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V2_run_the_plan_never_writes_a_static_format_beside_a_dynamic_one()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("VerifyDynamic.bim");
            var dynamic = await f.Engine.CreateMeasureAsync("table:Sales", "Dynamic Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.SetMeasureFormatExpressionAsync(dynamic, "\"#,0.00\"", "human");
            await PressUntilQuietAsync(f.Engine);
            var format = await PropertyOf(f.Engine, dynamic, "FormatString");
            var expression = await PropertyOf(f.Engine, dynamic, "FormatStringExpression");
            Assert.False(format.Length > 0 && expression.Length > 0,
                "Run the plan wrote the static format '" + format + "' beside the dynamic format " + expression + ".");
        }

        [Fact]
        public async Task V3_a_new_empty_model_opens_no_row_so_the_studio_shows_its_starts()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Empty", 1604);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.NotEmpty(plan.Starts);
            // plan.tsx draws PlanStarts only when items.filter(isOpenRow).length === 0.
            var open = plan.Items.Where(i => PlanStateWords.IsOpen(i.StateWord)).Select(i => i.Id + " (" + i.Title + ": " + i.Sub + ")").ToArray();
            Assert.True(open.Length == 0, "an empty model opens " + string.Join(", ", open) + ", so the Studio never shows the ways to start");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V4_the_words_queue_hands_over_the_ai_instructions_when_they_are_open()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("VerifyInstructions.bim");
            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, x => !x.Waived && x.RuleId == "DAC-AI-INSTRUCTIONS");
            var queue = await f.Engine.GetWordsQueueAsync();
            Assert.Contains(queue.Items, i => i.RuleId == "DAC-AI-INSTRUCTIONS");
        }

        private static IEnumerable<string> WaitsThrough(ModelPlanView plan, string id)
        {
            var byId = plan.Items.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(new[] { id });
            while (stack.Count > 0)
                foreach (var w in (byId.TryGetValue(stack.Pop(), out var it) ? it.WaitsFor : null) ?? Array.Empty<PlanWaitRef>())
                    if (seen.Add(w.Id)) stack.Push(w.Id);
            return seen;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V5_the_words_row_never_waits_through_other_rows_for_the_cleanup()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Shape", 1604);
            await f.Engine.CreateTableAsync("fact_invoice", "human");
            foreach (var (c, dt) in new[] { ("INV_NO", "String"), ("CLNT_ID", "String"), ("BILL_CLNT_ID", "String"), ("INV_DT_KEY", "Int64"), ("INV_AMT", "Decimal"), ("TAX_AMT", "Decimal") })
                await f.Engine.CreateColumnAsync("table:fact_invoice", c, dt, null, "human");
            await f.Engine.CreateTableAsync("dim_client", "human");
            foreach (var (c, dt) in new[] { ("CLNT_ID", "String"), ("CLNT_NM", "String"), ("IND_CD", "String"), ("RGN_CD", "String") })
                await f.Engine.CreateColumnAsync("table:dim_client", c, dt, null, "human");
            await f.Engine.CreateTableAsync("dim_date", "human");
            foreach (var (c, dt) in new[] { ("DATE_KEY", "Int64"), ("CAL_DT", "DateTime"), ("FY_NO", "Int64"), ("MTH_NO", "Int64") })
                await f.Engine.CreateColumnAsync("table:dim_date", c, dt, null, "human");
            await f.Engine.CreateTableAsync("ref_codes", "human");
            foreach (var (c, dt) in new[] { ("CODE_TYPE", "String"), ("CODE", "String"), ("CODE_DESC", "String") })
                await f.Engine.CreateColumnAsync("table:ref_codes", c, dt, null, "human");
            await f.Engine.CreateRelationshipAsync("column:fact_invoice/CLNT_ID", "column:dim_client/CLNT_ID", null, true, "human");
            await f.Engine.CreateRelationshipAsync("column:fact_invoice/BILL_CLNT_ID", "column:dim_client/CLNT_ID", null, false, "human");
            await f.Engine.CreateRelationshipAsync("column:fact_invoice/INV_DT_KEY", "column:dim_date/DATE_KEY", null, true, "human");
            await f.Engine.MarkDateTableAsync("table:dim_date", "CAL_DT", "human");
            await f.Engine.CreateMeasureAsync("table:fact_invoice", "INV_AMT_NET", "SUM ( fact_invoice[INV_AMT] )", "human");
            await f.Engine.CreateMeasureAsync("table:fact_invoice", "TAX_PCT", "DIVIDE ( SUM ( fact_invoice[TAX_AMT] ), SUM ( fact_invoice[INV_AMT] ) )", "human");
            await f.SaveAsAsync("VerifyShape.bim");
            f.UseNull();
            await PressUntilQuietAsync(f.Engine);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain("cleanup/model", WaitsThrough(plan, LocalEngine.WordsItemId));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V6_the_task_given_from_the_words_row_names_the_one_call_that_returns_the_queue()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("VerifyGive.bim");
            // Kane's order: press Run the plan first (a task waits for Semanticus's own safe fixes), then give the words.
            await PressUntilQuietAsync(f.Engine);
            var given = await f.Engine.SetPlanItemDoerAsync(LocalEngine.WordsItemId, "assistant");
            var task = Assert.Single(given.Items.Where(i => i.OriginLink?.Ref == LocalEngine.WordsItemId && i.Id.StartsWith("pt-", StringComparison.Ordinal)));
            // Every row is the assistant's task now (doer lane, 13 Oct), and the words task goes after a task that hides the same field: set that one aside so this one is ready.
            foreach (var w in task.WaitsFor ?? Array.Empty<PlanWaitRef>()) await f.Engine.SkipModelPlanItemAsync(w.Id, "Not now", "human");
            var claim = await f.Engine.ClaimAssistantRequestAsync(task.Id);
            Assert.True(claim.Ok, claim.Message);
            var packet = (claim.Request?.Need ?? "") + "\n" + (claim.Request?.Brief ?? "") + "\n" + (claim.Request?.FullText ?? "");
            Assert.Contains("get_words_queue", packet);
        }
    }
}
