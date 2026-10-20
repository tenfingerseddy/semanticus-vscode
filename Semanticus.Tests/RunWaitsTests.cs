// Run the plan never does nothing (Kane, 13 Oct 2026: "clicking run plan here does nothing", "stuck on a message about skipped Jev").
// Kane's shape: Jev worked the summarize rows out, the model then changed, and Jev does not answer again (slow or gone).
// One press must run the rows, and no row may stay Waiting with no one working on it.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class RunWaitsTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        private readonly ITestOutputHelper _out;
        private static readonly string[] Tables = { "Sales", "Customer", "Project", "Task", "Capacity", "Setting" };
        public RunWaitsTests(ModelTemplates templates, ITestOutputHelper output) { _templates = templates; _out = output; }

        private async Task<DecisionFixture> ModelAsync(string file, bool tidySales = false)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, tidySales ? "run-waits-tidy" : "run-waits", async b =>
            {
                await b.Engine.CreateModelAsync("Contoso", 1604);
                foreach (var table in Tables)
                {
                    await b.Engine.CreateTableAsync(table, "human");
                    await b.Engine.CreateColumnAsync("table:" + table, "Amount", "Double", null, "human");
                    var filed = await b.Engine.CreateMeasureAsync("table:" + table, table + " Orders", "COUNTROWS ( '" + table + "' )", "human", "Volume");
                    await b.Engine.SetMeasureFormatAsync(filed, "#,0", "human");
                }
                await b.Engine.CreateLinguisticSchemaAsync(null, "human");
                // Already hidden and set not to add up: the Sales row then holds only lines on the new measure, none of them naming the column.
                if (tidySales)
                {
                    await b.Engine.SetObjectPropertyAsync("column:Sales/Amount", "SummarizeBy", "None", "human");
                    await b.Engine.SetObjectPropertyAsync("column:Sales/Amount", "IsHidden", "true", "human");
                    // A table whose only column is hidden and that holds measures reads as a home for measures (IsMeasureTablePlaceholder, 15 Oct), so Sales shows one real column.
                    await b.Engine.CreateColumnAsync("table:Sales", "Region", "String", null, "human");
                }
            }, file);
            f.UseNull();
            return f;
        }

        /// <summary>A Jev that has gone quiet: it never answers until the run is cancelled.</summary>
        private sealed class SilentOracle : IDecisionOracle
        {
            public bool Available => true;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }
        }

        private static string Answers()
        {
            var entries = new System.Collections.Generic.List<object>();
            foreach (var table in Tables)
                entries.Add(new { digest = "column:" + table + "/Amount", point = "column.summarize", answers = new
                { summarize_by = new { type = "choice", choice = "sum", confidence = .99 } } });
            return System.Text.Json.JsonSerializer.Serialize(new { servedModelVersion = "jev-test-1", entries });
        }

        /// <summary>Jev answered once and the plan has its Jev rows; then the model changes and Jev goes quiet.</summary>
        private async Task<ModelPlanView> JevWentQuietAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            foreach (var point in DecisionRegistry.Points.Where(p => p.Name != "column.summarize"))
                await f.Engine.SetDecisionPointEnabledAsync(point.Name, false, "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Answers()), new DecisionBudget()));
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            await f.Engine.GetModelPlanAsync();
            // The model changes (a person's edit), and Jev does not come back.
            await f.Engine.SetDescriptionAsync("table:Customer", "Who buys.", "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new SilentOracle(), new DecisionBudget()));
            return await f.Engine.GetModelPlanAsync();
        }

        private static bool OpenJevRow(ModelPlanItem i) =>
            i.Id.StartsWith("jev.", StringComparison.Ordinal) && (i.StateWord == PlanStateWords.Ready || i.StateWord == PlanStateWords.Waiting);

        // Kane's shape: every measure row of one family, Jev worked them out, the model changed, and Jev does not answer again.
        // One press runs every row. It waits for Jev once, not once per step, and no row is left Waiting or left out with no one on it.
        [Fact]
        public async Task One_press_runs_every_Jev_row_when_Jev_does_not_answer_again()
        {
            var was = LocalEngine.JevLookAgainWait;
            LocalEngine.JevLookAgainWait = TimeSpan.FromSeconds(3);
            try
            {
                using var f = await ModelAsync("Quiet.bim");
                var plan = await JevWentQuietAsync(f);
                var inv = new PlanInvariant(f.Engine, _out);
                var rows = plan.Items.Where(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)).ToArray();
                Assert.Equal(Tables.Length, rows.Length);
                Assert.True(rows.Count(r => r.StateWord == PlanStateWords.Waiting) >= 1, "the rows should wait behind each other, as on Kane's plan");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                _out.WriteLine("press took " + clock.Elapsed + ": " + ran.Sentence);
                foreach (var i in ran.Plan.Items) _out.WriteLine($"after | {i.Id} | {i.StateWord} | {i.Doer} | {i.Title}");

                Assert.True(ran.Performed, ran.Sentence);
                Assert.DoesNotContain("Nothing was changed", ran.Sentence);
                Assert.DoesNotContain("did not finish looking", ran.Sentence);
                Assert.Equal(Tables.Length, ran.ItemIds.Count(id => id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)));
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3 * 2.5), "the press waited for Jev more than once: " + clock.Elapsed);
                Assert.DoesNotContain(ran.Plan.Items, OpenJevRow);
                await inv.CheckAsync("after the press", findings: false);
                var measures = await f.Engine.ListMeasuresAsync();
                foreach (var t in Tables)
                    Assert.Contains(measures, m => m.Table == t && m.Name != t + " Orders");
            }
            finally { LocalEngine.JevLookAgainWait = was; }
        }

        // The press starts with a Jev row already stale (a person edited after Jev looked, and Jev stays quiet): the first press runs it too,
        // and says nothing about a skipped Jev (Kane: "clicking run plan here does nothing").
        [Fact]
        public async Task A_press_that_starts_with_stale_Jev_rows_runs_them_and_does_not_do_nothing()
        {
            var was = LocalEngine.JevLookAgainWait;
            LocalEngine.JevLookAgainWait = TimeSpan.FromSeconds(1);
            try
            {
                using var f = await ModelAsync("QuietStart.bim");
                var plan = await JevWentQuietAsync(f);
                await f.Engine.SetDescriptionAsync("table:Sales", "What we sold.", "human");
                await f.Engine.SetDescriptionAsync("table:Customer", "Who buys.", "human");
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                _out.WriteLine("press: " + ran.Sentence);
                Assert.True(ran.Performed, ran.Sentence);
                Assert.DoesNotContain("Nothing was changed", ran.Sentence);
                Assert.DoesNotContain("did not finish looking", ran.Sentence);
                Assert.Equal(Tables.Length, ran.ItemIds.Count(id => id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)));
                Assert.DoesNotContain(ran.Plan.Items, OpenJevRow);
                await new PlanInvariant(f.Engine, _out).CheckAsync("after the press", findings: false);
            }
            finally { LocalEngine.JevLookAgainWait = was; }
        }

        // A press waits for Jev a short while: 15 seconds at most (Kane, 13 Oct: two minutes is too long for a press).
        [Fact]
        public void The_shipped_wait_for_Jev_is_at_most_fifteen_seconds() =>
            Assert.True(LocalEngine.JevLookAgainWait <= TimeSpan.FromSeconds(15), LocalEngine.JevLookAgainWait.ToString());

        // The write guard: a table whose only column was deleted has no column list left, and a formula over that column was let through.
        [Fact]
        public async Task A_formula_over_the_deleted_only_column_of_a_table_is_refused()
        {
            using var f = await ModelAsync("NoColumns.bim");
            await f.Engine.DeleteObjectAsync("column:Sales/Amount", "human");
            var v = await f.Engine.ValidateDaxAsync("SUM ( 'Sales'[Amount] )", strict: true);
            Assert.False(v.Valid);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Engine.CreateMeasureAsync("table:Sales", "Total Sales Amount", "SUM ( 'Sales'[Amount] )", "human", null));
            Assert.Contains("not valid DAX", refused.Message);
        }

        // The other side of the same guard: a table that never had columns (just created, a calculated table not yet calculated, a live table not yet loaded) is not judged.
        [Fact]
        public async Task A_measure_on_a_just_created_table_with_no_columns_is_accepted()
        {
            using var f = await ModelAsync("FreshTables.bim");
            await f.Engine.CreateCalculatedTableAsync("Plan Lines", "SELECTCOLUMNS ( 'Sales', \"Amount\", 1 )", "human");
            await f.Engine.CreateMeasureAsync("table:Plan Lines", "Plan Total", "SUM ( 'Plan Lines'[Amount] )", "human", null);
            await f.Engine.CreateTableAsync("Fresh", "human");
            await f.Engine.CreateMeasureAsync("table:Fresh", "Fresh Total", "SUM ( Fresh[Amt] )", "human", null);
            var measures = await f.Engine.ListMeasuresAsync();
            Assert.Contains(measures, m => m.Table == "Plan Lines" && m.Name == "Plan Total");
            Assert.Contains(measures, m => m.Table == "Fresh" && m.Name == "Fresh Total");
        }

        // The harm (verifier, 13 Oct): the column was already hidden and not summed, so no line named it, and Jev's earlier measure was written
        // over a column that is gone. The row goes back to Jev, the other rows run, and the press says which row went back, in one true sentence.
        [Fact]
        public async Task A_row_that_no_longer_fits_goes_back_to_Jev_and_the_press_names_it()
        {
            var was = LocalEngine.JevLookAgainWait;
            LocalEngine.JevLookAgainWait = TimeSpan.FromSeconds(3);
            try
            {
                using var f = await ModelAsync("GoesBack.bim", tidySales: true);
                var seen = new System.Collections.Generic.List<PlanRunProgress>();
                f.Sessions.Bus.PlanRunProgressed += seen.Add;
                await JevWentQuietAsync(f);
                await f.Engine.DeleteObjectAsync("column:Sales/Amount", "human");
                var plan = await f.Engine.GetModelPlanAsync();
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                _out.WriteLine(ran.Sentence);
                Assert.True(ran.Performed, ran.Sentence);
                Assert.DoesNotContain("Nothing was changed", ran.Sentence);
                Assert.Contains("Jev is still looking at 1", ran.Sentence);   // said when it is true: one row went back to Jev
                Assert.Contains("numbers in Sales", ran.Sentence);            // and it is named
                Assert.Equal(Tables.Length - 1, ran.ItemIds.Count(id => id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)));
                var measures = await f.Engine.ListMeasuresAsync();
                Assert.DoesNotContain(measures, m => m.Table == "Sales" && m.Name != "Sales Orders");
                await new PlanInvariant(f.Engine, _out).CheckAsync("after the press", findings: false);
                // The page hears the wait, counting up in whole seconds, and hears the row that went back.
                var waits = seen.Where(e => e.Phase == PlanRunPhases.WaitJev).ToList();
                Assert.NotEmpty(waits);
                Assert.Contains(waits, e => e.WaitedSeconds >= 1);
                Assert.Contains(seen, e => e.Phase == PlanRunPhases.Skipped && e.ItemIds.Contains("jev.column-summarize.table:Sales"));
            }
            finally { LocalEngine.JevLookAgainWait = was; }
        }
    }
}
