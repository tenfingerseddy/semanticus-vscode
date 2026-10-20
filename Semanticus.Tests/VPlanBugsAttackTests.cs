// Verifier attack tests for m16/plan-bugs (not for commit).
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Analysis;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VPlanBugsAttackTests
    {
        private readonly ITestOutputHelper _o;
        public VPlanBugsAttackTests(ITestOutputHelper o) { _o = o; }

        // ---- 1. measure table placeholder ----
        [Fact]
        public async Task V1_real_one_column_data_table_still_gets_base_measure_placeholder_does_not()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.CreateTableAsync("Targets", "human");
            await f.Engine.CreateColumnAsync("table:Targets", "Target", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Targets", "Target Rows", "COUNTROWS ( Targets )", "human");
            await f.Engine.CreateTableAsync("KPIs", "human");
            await f.Engine.CreateColumnAsync("table:KPIs", "Value", "Int64", null, "human");
            await f.Engine.SetColumnMetadataAsync("column:KPIs/Value", true, null, null, null, "human");
            await f.Engine.CreateMeasureAsync("table:KPIs", "Margin", "1", "human");
            var r = await f.Sessions.Require().ReadAsync(m => (
                real: StarterMeasureTemplates.IsMeasureTablePlaceholder(m.Tables["Targets"].Columns["Target"]),
                realWants: ColumnSummarizeFamily.Wants(m.Tables["Targets"].Columns["Target"]),
                ph: StarterMeasureTemplates.IsMeasureTablePlaceholder(m.Tables["KPIs"].Columns["Value"]),
                phWants: ColumnSummarizeFamily.Wants(m.Tables["KPIs"].Columns["Value"]),
                sales: StarterMeasureTemplates.IsMeasureTablePlaceholder(m.Tables["Sales"].Columns["Amount"])));
            _o.WriteLine(r.ToString());
            Assert.False(r.real); Assert.True(r.realWants);
            Assert.True(r.ph); Assert.False(r.phWants);
            Assert.False(r.sales);
        }

        [Fact]
        public async Task V1b_hidden_disconnected_data_table_with_a_measure_is_read_as_a_placeholder_NOTE()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.SetColumnMetadataAsync("column:Sales/Amount", true, null, null, null, "human");
            Assert.True(await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Amount"].IsHidden), "the setup hid the column");
            var ph = await f.Sessions.Require().ReadAsync(m => StarterMeasureTemplates.IsMeasureTablePlaceholder(m.Tables["Sales"].Columns["Amount"]));
            _o.WriteLine("single hidden fact table read as placeholder: " + ph);   // a NOTE: the reading is recorded, not judged
        }

        // ---- 2. DAX check ----
        private static async Task<DaxValidation> V(DecisionFixture f, string dax) => await f.Sessions.Require().ReadAsync(m => DaxValidator.Validate(m, dax));

        [Theory]
        [InlineData("SUM ( 'Sales'[Amount )")]                                  // unclosed bracket
        [InlineData("SUM ( 'Sales'[Amount] ) )")]                               // stray paren
        [InlineData("SUM ( 'Sales'[Amount] ")]                                  // unclosed paren
        [InlineData("GHOSTFN ( 'Sales'[Amount] )")]                             // unknown function
        [InlineData("SUM ( 'Sales'[Amount]] )")]                                // ]] escape eats the close: unclosed
        [InlineData("SUM ( 'Sales'[Amount] ) -- total [x")]                     // comment with a bracket: valid? (should PASS - see below)
        [InlineData("IF ( 'Sales'[Amount) > 1, 'Sales'[Amount], 0 )")]         // mis-closed bracket swallows a paren
        [InlineData("IF ( [Total Sales) > 1, [Total Sales], 0 )")]              // same, bare
        [InlineData("SUM ( 'Sales'[Amount] ) +")]                               // trailing operator
        [InlineData("SUM ( 'Sales'[Amount (x] ) + SUM ( 'Sales'[Amount] ) )")]  // name with paren, then stray paren
        public async Task V2_broken_dax_still_has_a_diagnostic(string dax)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            var v = await V(f, dax);
            _o.WriteLine(dax + " => valid=" + v.Valid + " :: " + string.Join("; ", v.Diagnostics.Select(d => d.Severity + ":" + d.Message)));
            if (dax.Contains("-- total")) { Assert.Empty(v.Diagnostics); return; }
            Assert.NotEmpty(v.Diagnostics);
        }

        [Theory]
        [InlineData("Odd]Name", "SUM ( 'Sales'[Odd]]Name] )")]
        [InlineData("Say \"hi\"", "SUM ( 'Sales'[Say \"hi\"] )")]
        [InlineData("A (b) % 'c'", "SUM ( 'Sales'[A (b) % 'c'] ) /* [x ( */")]
        [InlineData("Paren(", "CALCULATE ( SUM ( 'Sales'[Paren(] ), 'Sales'[Paren(] > 0 )")]
        public async Task V2_odd_names_pass(string col, string dax)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.CreateColumnAsync("table:Sales", col, "Double", null, "human");
            var v = await V(f, dax);
            _o.WriteLine(dax + " => " + string.Join("; ", v.Diagnostics.Select(d => d.Severity + ":" + d.Message)));
            Assert.Empty(v.Diagnostics);
        }

        [Fact]
        public async Task V2_odd_table_names_pass()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.CreateTableAsync("Sales (EU) % 'x' [y]", "human");
            await f.Engine.CreateColumnAsync("table:Sales (EU) % 'x' [y]", "Net (id)", "Double", null, "human");
            var v = await V(f, "SUM ( 'Sales (EU) % ''x'' [y]'[Net (id)] )");
            _o.WriteLine(string.Join("; ", v.Diagnostics.Select(d => d.Severity + ":" + d.Message)));
            Assert.Empty(v.Diagnostics);
        }

        [Fact]
        public async Task V2_calculated_table_swallowed_paren_is_still_caught()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.CreateCalculatedTableAsync("Calc", "SELECTCOLUMNS ( Sales, \"A\", Sales[Amount] )", "human");
            var dax = "IF ( 'Calc'[A) > 1, 'Calc'[A], 0 )";
            var v = await V(f, dax);
            _o.WriteLine(dax + " => valid=" + v.Valid + " :: " + string.Join("; ", v.Diagnostics.Select(d => d.Severity + ":" + d.Message)));
            Assert.NotEmpty(v.Diagnostics);
        }

        [Fact]
        public async Task V2_rename_repro_base_measure_text_passes()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Item Key", "Int64", null, "human");
            await f.Engine.RenameObjectAsync("column:Sales/Item Key", "Item Key (id)", "human");
            var v = await V(f, "SUM ( 'Sales'[Item Key (id)] )");
            Assert.Empty(v.Diagnostics);
            var stale = await V(f, "SUM ( 'Sales'[Item Key] )");
            Assert.NotEmpty(stale.Diagnostics);
        }
    
        // ---- 3. plan.rank ----
        private static async Task<(DecisionFixture F, CountingRelay Relay)> OpenManyAsync(int tables)
        {
            var dir = Path.Combine(Path.GetTempPath(), "vpb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "many.bim");
            ManyTablesModel.Write(bim, tables);
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.OpenAsync(bim);
            await f.SaveAsAsync("many.bim");
            await f.ConsentAsync();
            var relay = new CountingRelay();
            f.Engine.UseDecisionsForTest(new DecisionService(relay, new DecisionBudget(), answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => true;
            return (f, relay);
        }

        private static async Task StartRankInFlight(DecisionFixture f, CountingRelay relay, int latencyMs = 900)
        {
            relay.ExtraLatencyMs = point => point == "plan.rank" ? latencyMs : 0;
            for (var i = 0; i < 12; i++)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                if (f.Engine.PlanRankRunsStartedForTest > 0) break;
            }
            Assert.True(f.Engine.PlanRankRunsStartedForTest > 0);
        }

        private static ModelPlanItem OpenRow(ModelPlanView plan, int skip = 0) =>
            plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done && i.Status != ModelPlanStatuses.Skipped).Skip(skip).First();

        // 4 s of latency: on a loaded host three reads and three skips can take over a second, and the ask must still be out for all of them.
        [Fact]
        public async Task V3_many_reads_of_changing_lists_ask_once_after_the_first_lands_and_are_pending_meanwhile()
        {
            var (f, relay) = await OpenManyAsync(12);
            using (f)
            {
                await StartRankInFlight(f, relay, 4000);
                var started = f.Engine.PlanRankRunsStartedForTest;
                for (var k = 0; k < 3; k++)
                {
                    var plan = await f.Engine.GetModelPlanAsync();
                    await f.Engine.SkipModelPlanItemAsync(OpenRow(plan).Id, "Not needed.", "human");
                    var mid = await f.Engine.GetModelPlanAsync();
                    Assert.True(mid.JevPending, "a read while the ask is out showed an order as current");
                }
                Assert.Equal(started, f.Engine.PlanRankRunsStartedForTest);   // nothing new started while one is out
                await f.Engine.PlanRankSettledForTest();
                await f.Engine.PlanRankSettledForTest();
                var final = await f.Engine.GetModelPlanAsync();
                _o.WriteLine($"started {started} -> {f.Engine.PlanRankRunsStartedForTest}, cancelled {relay.CancelledOf("plan.rank")}, pending {final.JevPending}, ranked {final.Ranked}");
                Assert.Equal(started + 1, f.Engine.PlanRankRunsStartedForTest);   // one ask for the newest
                Assert.Equal(0, relay.CancelledOf("plan.rank"));
                Assert.False(final.JevPending); Assert.True(final.Ranked);
            }
        }

        [Fact]
        public async Task V3_a_model_change_still_cancels_and_drops_the_wanted_ask()
        {
            var (f, relay) = await OpenManyAsync(12);
            using (f)
            {
                await StartRankInFlight(f, relay);
                var plan = await f.Engine.GetModelPlanAsync();
                await f.Engine.SkipModelPlanItemAsync(OpenRow(plan).Id, "Not needed.", "human");
                await f.Engine.GetModelPlanAsync();   // wanted is set
                var startedBefore = f.Engine.PlanRankRunsStartedForTest;
                await f.Engine.CreateMeasureAsync("table:Measure Table", "Probe", "1", "human");   // revision moves
                await f.Engine.PlanRankSettledForTest();
                await Task.Delay(300);
                _o.WriteLine($"cancelled {relay.CancelledOf("plan.rank")}, started {startedBefore} -> {f.Engine.PlanRankRunsStartedForTest}");
                Assert.Equal(1, relay.CancelledOf("plan.rank"));
                Assert.Equal(startedBefore, f.Engine.PlanRankRunsStartedForTest);   // the old-revision wanted ask never left
                for (var i = 0; i < 6; i++) { await f.Engine.GetModelPlanAsync(); await f.Engine.JevSettledForTest(); await f.Engine.PlanRankSettledForTest(); }
                var final = await f.Engine.GetModelPlanAsync();
                Assert.False(final.JevPending); Assert.True(final.Ranked);
            }
        }
    }
}
