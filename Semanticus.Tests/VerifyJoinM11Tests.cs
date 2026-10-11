// The M11 join verifier (1 Oct), full check of m11/join at d141ce01. Each test attacks the nearest sibling of a JOIN MUST FIX item
// (join-2.md section 2) that the join closed on one path only. Left failing on purpose: the verifier fixes no product code.
//
//   item 1 (a stored acceptance declines a change on EVERY path that writes or queues one; the join taught Jev's column-format
//          family to ask DeclinedChanges): Jev's column-summarize family, its sibling, still asks nothing, and its "Stop adding
//          up by itself" line is ticked, so Run the plan writes the summarize setting the person accepted leaving alone.
//   item 6 (starter measures never total a key, on every path that offers one): with Jev live in Pro the starter rows stand aside
//          for column.summarize (StarterMeasuresFamily.cs:12-13), and that family's Choose takes a sure "sum" on a key column,
//          so Run the plan adds "Total CustomerKey", a sum of keys. StarterMeasuresFamily fences the same pick (IsKeyShaped).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyJoinM11Tests
    {
        private const string Point = "column.summarize";

        private static async Task<DecisionFixture> ModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Jev answers sure for every number column; <paramref name="keyPick"/> is its pick for Sales[CustomerKey].</summary>
        private static async Task AnswerAsync(DecisionFixture f, string keyPick)
        {
            await f.ConsentAsync();
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var d = prepared.Asks.ToDictionary(a => a.TableName + "/" + a.ColumnName, a => a.Ref, StringComparer.Ordinal);
            var rows = new List<(string, string, double)>
            {
                (d["Sales/Amount"], "sum", 0.95),
                (d["Sales/Quantity"], "sum", 0.95),
                (d["Sales/CustomerKey"], keyPick, 0.97),
                (d["Customer/CustomerKey"], "none", 0.97),
            };
            var recorded = new ByObjectRecordedOracle(Fixture(rows), "jev-test-1");
            f.Engine.UseDecisionsForTest(new DecisionService(recorded, new DecisionBudget()));
        }

        /// <summary>Press Run the plan until nothing Semanticus does is ready, setting aside the assistant's rows a person with no
        /// assistant must set aside (join-2 section 7), so the run reaches the Jev rows.</summary>
        private static async Task RunThePlanAsync(DecisionFixture f)
        {
            var e = f.Engine;
            Trail.Clear();
            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            for (var press = 0; press < 10; press++)
            {
                var plan = await e.GetModelPlanAsync();
                foreach (var asst in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done
                                                           && i.Status != ModelPlanStatuses.Skipped && (i.Id.StartsWith("describe/", StringComparison.Ordinal)
                                                           || i.Id.StartsWith("words/", StringComparison.Ordinal))))
                    await e.SkipModelPlanItemAsync(asst.Id, "No assistant here.", "human");
                plan = await e.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                Trail.Add("press " + press + ": " + ran.Performed + " " + ran.Sentence);
                // A row whose changes were worked out before the model last changed is worked out again, as the sentence asks.
                if (!ran.Performed)
                    foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held))
                    {
                        var c1 = await e.ModelPlanActionAsync(r.Id, "cancel", "human", null);
                        var p1 = await e.ModelPlanActionAsync(r.Id, "propose", "human", null);
                        var now = p1.Plan?.Items.FirstOrDefault(i => i.Id == r.Id);
                        Trail.Add("  rework " + r.Id + ": cancel=" + c1.Performed + " '" + c1.Sentence + "' propose=" + p1.Performed + " '" + p1.Sentence
                                  + "' state=" + now?.ChangeSet?.State + " stale=" + now?.ChangeSet?.Stale + " rev=" + f.Sessions.Require().Revision);
                        // Found here: a row worked out again still reads stale, so Run the plan can never take it. A person sets it aside.
                        if (now?.ChangeSet?.Stale == true) { await e.SkipModelPlanItemAsync(r.Id, "Stuck stale.", "human"); Trail.Add("  set aside " + r.Id); }
                    }
            }
            var last = await e.GetModelPlanAsync();
            foreach (var i in last.Items)
                Trail.Add(i.Id + " doer=" + i.Doer + " state=" + i.StateWord + " status=" + i.Status + " waits=" + string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)));
        }

        private static readonly List<string> Trail = new List<string>();
        private static string Dump() => string.Join("\n", Trail);

        private static async Task<string> SummarizeOf(DecisionFixture f, string columnRef) =>
            (await f.Engine.GetObjectAsync(columnRef)).Properties["summarizeBy"]?.ToString() ?? "";

        /// <summary>Sibling of item 1. The person accepted "Keys should not add up" (FMT-SUMMARIZE) on Sales[CustomerKey]: a decision
        /// to leave its summarize setting as it is, which declines the summarizeBy change on every path. Jev's column-summarize row
        /// must not carry a summarizeBy line for that column, and Run the plan must leave it as it was.</summary>
        [Fact]
        public async Task Verify_an_accepted_summarize_finding_stops_Jevs_summarize_line_and_Run_the_plan_leaves_the_column()
        {
            using var f = await ModelAsync("VjSumAccepted.bim");
            await AnswerAsync(f, "none");
            var before = await SummarizeOf(f, "column:Sales/CustomerKey");
            var kept = await f.Engine.WaiveFindingAsync("air", "FMT-SUMMARIZE", "column:Sales/CustomerKey", "It adds up on purpose here.", "human");
            Assert.True(kept.Changed, "the acceptance did not take");

            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();
            var sales = plan.Items.SingleOrDefault(i => i.Id == "jev.column-summarize.table:Sales");
            Assert.NotNull(sales);   // not vacuous: Jev's row is there for the other columns
            var lines = sales.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>();
            Assert.Contains(lines, l => l.Target == "column:Sales/Amount" && l.Op == "set_summarize_by");
            var lineOnRow = lines.Any(l => l.Target == "column:Sales/CustomerKey" && l.Op == "set_summarize_by");

            await RunThePlanAsync(f);
            Assert.True("None" == await SummarizeOf(f, "column:Sales/Amount"), "run did not reach Jev's row:\n" + Dump());
            var after = await SummarizeOf(f, "column:Sales/CustomerKey");
            Assert.True(before == after, "Run the plan wrote the accepted column's summarize setting: " + before + " -> " + after + " (line on Jev's row: " + lineOnRow + ")");
            Assert.False(lineOnRow, "Jev's row carries a summarizeBy line for the accepted column");
        }

        /// <summary>Sibling of item 6. Jev is sure Sales[CustomerKey] (the many side of a relationship) should be summed. No path may
        /// total a key: the base measure family must refuse the pick as StarterMeasuresFamily does, and Run the plan adds no
        /// "Total CustomerKey".</summary>
        [Fact]
        public async Task Verify_Jevs_sure_sum_on_a_key_never_becomes_a_total_of_the_key()
        {
            using var f = await ModelAsync("VjSumKey.bim");
            await AnswerAsync(f, "sum");

            await RunThePlanAsync(f);
            var measures = await f.Sessions.Require().ReadAsync(m => m.AllMeasures.Select(x => x.Name + " = " + x.Expression).ToArray());
            Assert.True(measures.Any(x => x.StartsWith("Total Amount", StringComparison.Ordinal)), "no totals made:\n" + string.Join("\n", measures) + "\n" + Dump());
            Assert.DoesNotContain(measures, x => x.Contains("CustomerKey", StringComparison.Ordinal));
        }

        /// <summary>The same, on the family's pure map: a sure "sum" for a key column gives no measure line.</summary>
        [Fact]
        public void Verify_the_base_measure_map_never_totals_a_key_column_even_when_Jev_is_sure()
        {
            var ask = new ColumnSummarizeFamily.Ask
            {
                Ref = "column:Sales/CustomerKey", ColumnName = "CustomerKey", TableRef = "table:Sales", TableName = "Sales", Current = "Default",
                IsKey = false, Role = "many",
                NameOf = StarterMeasureTemplates.MeasureOptions.ToDictionary(o => o, o => StarterMeasureTemplates.Name(o, "CustomerKey")),
            };
            var lines = new List<ChangeItem>();
            ColumnSummarizeFamily.Map(ask, "sum", 0.97, 0.85, lines, new List<ModelPlanChangeRefusal>());
            Assert.DoesNotContain(lines, l => l.Kind == "create_measure");

            var key = new ColumnSummarizeFamily.Ask
            {
                Ref = "column:Customer/CustomerKey", ColumnName = "CustomerKey", TableRef = "table:Customer", TableName = "Customer", Current = "Default",
                IsKey = true,
                NameOf = StarterMeasureTemplates.MeasureOptions.ToDictionary(o => o, o => StarterMeasureTemplates.Name(o, "CustomerKey")),
            };
            var keyLines = new List<ChangeItem>();
            ColumnSummarizeFamily.Map(key, "sum", 0.97, 0.85, keyLines, new List<ModelPlanChangeRefusal>());
            Assert.DoesNotContain(keyLines, l => l.Kind == "create_measure");
        }
    }
}
