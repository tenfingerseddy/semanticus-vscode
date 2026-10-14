// Verifier attacks on run-waits (13 Oct 2026). Semanticus stands in for a quiet Jev with Jev's earlier lines; these tests plant a model
// change that makes an earlier line wrong and check that the line is not written, that the row goes back to Jev, and how long a press takes.

using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class VerifyRunWaitsTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        private readonly ITestOutputHelper _out;
        private static readonly string[] Tables = { "Sales", "Customer", "Project", "Task", "Capacity", "Setting" };
        public VerifyRunWaitsTests(ModelTemplates templates, ITestOutputHelper output) { _templates = templates; _out = output; }

        private async Task<DecisionFixture> ModelAsync(string file, bool tidySales = false)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, tidySales ? "v-run-waits-tidy" : "v-run-waits", async b =>
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
                if (tidySales)
                {
                    // Already hidden and set not to add up by itself: the row then holds only lines on the new measure.
                    await b.Engine.SetObjectPropertyAsync("column:Sales/Amount", "SummarizeBy", "None", "human");
                    await b.Engine.SetObjectPropertyAsync("column:Sales/Amount", "IsHidden", "true", "human");
                }
            }, file);
            f.UseNull();
            return f;
        }

        /// <summary>A Jev that has gone quiet: no answer, then (when given a time) a failure, as a relay call that times out.</summary>
        private sealed class SilentOracle : IDecisionOracle
        {
            private readonly TimeSpan? _quiet;
            public SilentOracle(TimeSpan? quiet = null) { _quiet = quiet; }
            public bool Available => true;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (_quiet == null) { await Task.Delay(Timeout.Infinite, ct); return null; }
                await Task.Delay(_quiet.Value, ct);
                throw new TimeoutException("Jev did not answer in time.");
            }
        }

        private sealed class SlowOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner; private readonly TimeSpan _delay;
            public SlowOracle(IDecisionOracle inner, TimeSpan delay) { _inner = inner; _delay = delay; }
            public bool Available => true;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                await Task.Delay(_delay, ct);
                return await _inner.AskAsync(request, ct);
            }
        }

        private static string Answers(params string[] columns)
        {
            var entries = new List<object>();
            foreach (var c in columns)
                entries.Add(new { digest = c, point = "column.summarize", answers = new
                { summarize_by = new { type = "choice", choice = "sum", confidence = .99 } } });
            return System.Text.Json.JsonSerializer.Serialize(new { servedModelVersion = "jev-test-1", entries });
        }

        private static string[] AllAmounts(params string[] extra) => Tables.Select(t => "column:" + t + "/Amount").Concat(extra).ToArray();

        private async Task<ModelPlanView> JevAnsweredOnceAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            foreach (var point in DecisionRegistry.Points.Where(p => p.Name != "column.summarize"))
                await f.Engine.SetDecisionPointEnabledAsync(point.Name, false, "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Answers(AllAmounts())), new DecisionBudget()));
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private void Dump(string label, ModelPlanView plan)
        {
            _out.WriteLine($"==== {label}: strip {plan.Strip?.TasksDone}/{plan.Strip?.TasksAll}");
            foreach (var i in plan.Items)
                _out.WriteLine($"  {i.Id} | {i.StateWord} | {i.Doer} | {i.Title} | waitsFor={string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id))}");
            var dir = Environment.GetEnvironmentVariable("VRW_OUT");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, label.Replace(' ', '-') + ".json"), System.Text.Json.JsonSerializer.Serialize(plan,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
            }
        }

        /// <summary>No row reads Waiting unless it names an open row it waits for.</summary>
        private static void NoOrphanWait(ModelPlanView plan)
        {
            var open = plan.Items.Where(i => i.StateWord != PlanStateWords.Done && i.StateWord != PlanStateWords.SetAside).Select(i => i.Id).ToHashSet();
            foreach (var i in plan.Items.Where(i => i.StateWord == PlanStateWords.Waiting))
                Assert.True((i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Any(w => open.Contains(w.Id)), "row waits with no one on it: " + i.Id + " " + i.Title);
        }

        private async Task<ApplyPlanItemsResult> PressAsync(DecisionFixture f, ModelPlanView plan, string label)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            _out.WriteLine($"PRESS {label}: {clock.Elapsed.TotalSeconds:0.0} s, performed={ran.Performed}, ran={ran.ItemIds?.Count()}");
            _out.WriteLine("HEADER " + label + ": " + ran.Sentence);
            _out.WriteLine($"RESULT {label}: applied={ran.Applied} items={string.Join(",", ran.ItemIds ?? Array.Empty<string>())} note={ran.Note} failed={ran.FailedItemId}");
            Dump(label + " after", ran.Plan);
            var dir = Environment.GetEnvironmentVariable("VRW_OUT");
            if (!string.IsNullOrEmpty(dir))
                File.WriteAllText(Path.Combine(dir, label.Replace(' ', '-') + "-result.json"), System.Text.Json.JsonSerializer.Serialize(
                    new { performed = ran.Performed, applied = ran.Applied, itemIds = ran.ItemIds, sentence = ran.Sentence, note = ran.Note, revision = ran.Revision, entryId = ran.EntryId },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return ran;
        }

        private static IEnumerable<MeasureRow> Over(IEnumerable<MeasureRow> measures, string table, string column) =>
            measures.Where(m => (m.Expression ?? "").Contains("[" + column + "]", StringComparison.Ordinal) && m.Table == table);

        private async Task PlantedChangeAsync(string file, Func<DecisionFixture, Task> plant, string newColumn, bool tidySales = false)
        {
            var was = LocalEngine.JevLookAgainWait;
            LocalEngine.JevLookAgainWait = TimeSpan.FromSeconds(2);
            try
            {
                using var f = await ModelAsync(file, tidySales);
                var first = await JevAnsweredOnceAsync(f);
                Dump(file + " Jev answered", first);
                Assert.Contains(first.Items, i => i.Id == "jev.column-summarize.table:Sales");
                await plant(f);
                await f.Engine.SetDescriptionAsync("table:Customer", "Who buys.", "human");
                f.Engine.UseDecisionsForTest(new DecisionService(new SilentOracle(TimeSpan.FromSeconds(6)), new DecisionBudget()));
                var plan = await f.Engine.GetModelPlanAsync();
                Dump(file + " before", plan);
                var ran = await PressAsync(f, plan, file);
                Assert.True(ran.Performed, ran.Sentence);
                Assert.DoesNotContain("Nothing was changed", ran.Sentence);
                var measures = await f.Engine.ListMeasuresAsync();
                foreach (var m in measures.Where(m => m.Table == "Sales")) _out.WriteLine($"  Sales measure {m.Name} = {m.Expression}");
                Assert.Empty(Over(measures, "Sales", "Amount"));   // no measure over a column that is gone or is now text
                Assert.Equal(Tables.Length - 1, ran.ItemIds.Count(id => id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)));
                NoOrphanWait(ran.Plan);
                await new PlanInvariant(f.Engine, _out).CheckAsync("after the press", findings: false);

                if (newColumn == null) return;
                await f.Engine.JevSettledForTest();   // the quiet ask times out
                // Jev comes back: the row returns, worked out against the model as it is now, and a press runs it.
                f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Answers(AllAmounts(newColumn))), new DecisionBudget()));
                var noEdit = await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                noEdit = await f.Engine.GetModelPlanAsync();
                Dump(file + " Jev back no edit", noEdit);
                // Jev looks again after the next change to the model (PlanTasks.JevWaitNote).
                await f.Engine.SetDescriptionAsync("table:Task", "Work items.", "human");
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                var back = await f.Engine.GetModelPlanAsync();
                Dump(file + " Jev back", back);
                var row = back.Items.SingleOrDefault(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal) && i.StateWord == PlanStateWords.Ready);
                Assert.NotNull(row);
                var again = await PressAsync(f, back, file + " second");
                Assert.True(again.Performed, again.Sentence);
                measures = await f.Engine.ListMeasuresAsync();
                foreach (var m in measures.Where(m => m.Table == "Sales")) _out.WriteLine($"  Sales measure {m.Name} = {m.Expression}");
                Assert.NotEmpty(Over(measures, "Sales", newColumn.Split('/')[1]));
                Assert.Empty(Over(measures, "Sales", "Amount"));
                NoOrphanWait(again.Plan);
                await new PlanInvariant(f.Engine, _out).CheckAsync("after the second press", findings: false);
            }
            finally { LocalEngine.JevLookAgainWait = was; }
        }

        [Fact]
        public Task Renamed_column_line_is_not_written_and_the_row_goes_back_to_Jev() =>
            PlantedChangeAsync("VRename.bim", f => f.Engine.RenameObjectAsync("column:Sales/Amount", "Net Amount", "human"), "column:Sales/Net Amount");

        // The row on a column already hidden and set not to add up holds only lines on the new measure, so nothing in it names the column.
        [Fact]
        public Task Renamed_tidy_column_line_is_not_written() =>
            PlantedChangeAsync("VRenameTidy.bim", f => f.Engine.RenameObjectAsync("column:Sales/Amount", "Net Amount", "human"), null, tidySales: true);

        [Fact]
        public Task Deleted_tidy_column_line_is_not_written() =>
            PlantedChangeAsync("VDeleteTidy.bim", f => f.Engine.DeleteObjectAsync("column:Sales/Amount", "human"), null, tidySales: true);

        [Fact]
        public Task Deleted_column_line_is_not_written() =>
            PlantedChangeAsync("VDelete.bim", f => f.Engine.DeleteObjectAsync("column:Sales/Amount", "human"), null);

        [Fact]
        public Task Column_changed_to_text_gets_no_sum() =>
            PlantedChangeAsync("VType.bim", f => f.Engine.SetColumnDataTypeAsync("column:Sales/Amount", "String", "human"), null);

        [Fact]
        public Task Tidy_column_changed_to_text_gets_no_sum() =>
            PlantedChangeAsync("VTypeTidy.bim", f => f.Engine.SetColumnDataTypeAsync("column:Sales/Amount", "String", "human"), null, tidySales: true);

        // The page presses again while a wave ran (runAllWaves). After a renamed tidy column, does any later press run the rows behind Sales?
        [Fact]
        public async Task Renamed_tidy_column_second_and_third_press()
        {
            var was = LocalEngine.JevLookAgainWait;
            LocalEngine.JevLookAgainWait = TimeSpan.FromSeconds(2);
            try
            {
                using var f = await ModelAsync("VRenameTidy2.bim", tidySales: true);
                await JevAnsweredOnceAsync(f);
                await f.Engine.RenameObjectAsync("column:Sales/Amount", "Net Amount", "human");
                await f.Engine.SetDescriptionAsync("table:Customer", "Who buys.", "human");
                f.Engine.UseDecisionsForTest(new DecisionService(new SilentOracle(TimeSpan.FromSeconds(6)), new DecisionBudget()));
                var plan = await f.Engine.GetModelPlanAsync();
                Dump("rt2 before", plan);
                var one = await PressAsync(f, plan, "rt2 one");
                var two = await PressAsync(f, await f.Engine.GetModelPlanAsync(), "rt2 two");
                await f.Engine.JevSettledForTest();
                var three = await PressAsync(f, await f.Engine.GetModelPlanAsync(), "rt2 three");
                foreach (var m in (await f.Engine.ListMeasuresAsync()).Where(m => m.Table == "Sales")) _out.WriteLine($"  Sales measure {m.Name} = {m.Expression}");
                var left = three.Plan.Items.Where(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal) && i.StateWord != PlanStateWords.Done).ToList();
                _out.WriteLine("open summarize rows after three presses: " + string.Join("; ", left.Select(i => i.Id + "=" + i.StateWord)));
                Assert.Empty(left.Where(i => !i.Id.EndsWith("table:Sales", StringComparison.Ordinal)));
            }
            finally { LocalEngine.JevLookAgainWait = was; }
        }

        // Jev slow but inside the wait: the press runs Jev's fresh answers, every row, and says so.
        [Fact]
        public async Task Slow_Jev_inside_the_wait_runs_every_row()
        {
            var was = LocalEngine.JevLookAgainWait;
            LocalEngine.JevLookAgainWait = TimeSpan.FromSeconds(8);
            try
            {
                using var f = await ModelAsync("VSlow.bim");
                await JevAnsweredOnceAsync(f);
                await f.Engine.SetDescriptionAsync("table:Customer", "Who buys.", "human");
                f.Engine.UseDecisionsForTest(new DecisionService(new SlowOracle(new ByObjectRecordedOracle(Answers(AllAmounts())), TimeSpan.FromSeconds(1.5)), new DecisionBudget()));
                var plan = await f.Engine.GetModelPlanAsync();
                Dump("slow before", plan);
                var ran = await PressAsync(f, plan, "slow");
                Assert.True(ran.Performed, ran.Sentence);
                Assert.DoesNotContain("Nothing was changed", ran.Sentence);
                Assert.Equal(Tables.Length, ran.ItemIds.Count(id => id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)));
                NoOrphanWait(ran.Plan);
                await new PlanInvariant(f.Engine, _out).CheckAsync("after the press", findings: false);
            }
            finally { LocalEngine.JevLookAgainWait = was; }
        }

        // The shipped wait (120 s), Jev silent, Kane's shape: how long the press takes, and the strip before and after.
        [Fact]
        public async Task Press_time_with_the_shipped_wait_and_a_silent_Jev()
        {
            using var f = await ModelAsync("VTime.bim");
            await JevAnsweredOnceAsync(f);
            await f.Engine.SetDescriptionAsync("table:Customer", "Who buys.", "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new SilentOracle(), new DecisionBudget()));
            var plan = await f.Engine.GetModelPlanAsync();
            Dump("kane-shape before", plan);
            _out.WriteLine("shipped wait " + LocalEngine.JevLookAgainWait);
            var ran = await PressAsync(f, plan, "kane-shape");
            Assert.True(ran.Performed, ran.Sentence);
            Assert.True(ran.Plan.Strip.TasksDone > plan.Strip.TasksDone, "Plan done did not rise");
            Assert.Equal(Tables.Length, ran.ItemIds.Count(id => id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)));
            NoOrphanWait(ran.Plan);
            await new PlanInvariant(f.Engine, _out).CheckAsync("after the press", findings: false);
        }
    }
}
