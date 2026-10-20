// m16/plan-bugs (1.5.1): three bugs the 15 Oct live Jev runs found in the Plan.
//   1. a measure table's hidden placeholder column got a base measure ("Activity Event Value" = SUM('Measure Table'[Value]));
//   2. after a column was renamed, its base measure was refused by the DAX check;
//   3. a plan.rank call a newer Plan read cancelled was still charged by the relay (see the third test class below).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Analysis;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanBugsTests
    {
        private static async Task<(DecisionFixture F, CountingRelay Relay)> OpenAsync(int tables)
        {
            var dir = Path.Combine(Path.GetTempPath(), "plan-bugs-" + Guid.NewGuid().ToString("N"));
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

        private static async Task<ModelPlanView> ReadUntilQuietAsync(DecisionFixture f, CountingRelay relay)
        {
            var seen = -1;
            for (var read = 0; read < 12; read++)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                if (relay.Calls.Count == seen) break;
                seen = relay.Calls.Count;
            }
            return await f.Engine.GetModelPlanAsync();
        }

        private static string[] MeasureLines(ModelPlanView plan) =>
            plan.Items.SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => string.Equals(l.Op, "create_measure", StringComparison.Ordinal))
                .Select(l => l.Target + " = " + l.After).ToArray();

        [Fact]
        public async Task A_measure_tables_hidden_placeholder_column_never_gets_a_base_measure()
        {
            var (f, relay) = await OpenAsync(30);
            using (f)
            {
                relay.Choose = q => q.Options.Any(o => o.Id == "sum") ? "sum" : q.Options[0].Id;
                var plan = await ReadUntilQuietAsync(f, relay);

                var lines = MeasureLines(plan);
                Assert.Contains(lines, l => l.Contains("'Activity Event'["));   // the happy path: the fact tables still get theirs
                Assert.DoesNotContain(lines, l => l.Contains("'Measure Table'["));
            }
        }

        // Bug 2. The cause is the DAX check, not the Plan: it read the "(id)" inside [Item Key (id)] as a call to a function named Key.
        [Theory]
        [InlineData("Item Number", "SUM ( 'Sales'[Item Number] )")]
        [InlineData("Item Key (id)", "SUM ( 'Sales'[Item Key (id)] )")]
        [InlineData("Rate (%", "AVERAGE ( 'Sales'[Rate (%] )")]
        [InlineData("Owner's Total", "MAX ( 'Sales'[Owner's Total] )")]
        [InlineData("Net (USD)", "DISTINCTCOUNT ( 'Sales'[Net (USD)] )")]
        public async Task A_column_name_with_brackets_or_a_quote_in_it_passes_the_dax_check_as_a_plain_name_does(string columnName, string dax)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();
            await f.Engine.CreateColumnAsync("table:Sales", columnName, "Double", null, "human");

            var v = await f.Sessions.Require().ReadAsync(m => DaxValidator.Validate(m, dax));

            Assert.True(v.Diagnostics.Length == 0, dax + " was refused: " + string.Join("; ", v.Diagnostics.Select(d => d.Message)));
        }

        [Fact]
        public async Task A_function_a_name_looks_like_is_still_found_outside_the_brackets()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync();

            var v = await f.Sessions.Require().ReadAsync(m => DaxValidator.Validate(m, "Ghost ( [Amount] ) + ( 1"));

            Assert.Contains(v.Diagnostics, d => d.Message.Contains("Ghost"));
            Assert.Contains(v.Diagnostics, d => d.Message.Contains("Unclosed"));
        }

        [Fact]
        public async Task A_plan_rank_ask_in_flight_is_not_cancelled_by_a_read_that_asks_about_a_changed_item_list()
        {
            var (f, relay) = await OpenAsync(12);
            using (f)
            {
                // Settle the Jev rows first, then keep plan.rank in flight long enough to read again.
                relay.ExtraLatencyMs = point => point == "plan.rank" ? 700 : 0;
                for (var i = 0; i < 12; i++)
                {
                    await f.Engine.GetModelPlanAsync();
                    await f.Engine.JevSettledForTest();
                    if (f.Engine.PlanRankRunsStartedForTest > 0) break;
                }
                Assert.True(f.Engine.PlanRankRunsStartedForTest > 0, "no plan.rank run started");

                // A row leaves the list while the ask is out: the next read would ask about a different list.
                var plan = await f.Engine.GetModelPlanAsync();
                var row = plan.Items.First(i => i.Doer == PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done && i.Status != ModelPlanStatuses.Skipped);
                await f.Engine.SkipModelPlanItemAsync(row.Id, "Not needed.", "human");
                await f.Engine.GetModelPlanAsync();
                await f.Engine.PlanRankSettledForTest();
                await f.Engine.GetModelPlanAsync();
                await f.Engine.PlanRankSettledForTest();

                Assert.Equal(0, relay.CancelledOf("plan.rank"));
                // And the answer still lands: the newest list is ranked once the ask in flight is done.
                var final = await f.Engine.GetModelPlanAsync();
                Assert.False(final.JevPending, "the ranking never landed");
                Assert.True(final.Ranked, "the newest list was never ranked");
            }
        }
    }
}
