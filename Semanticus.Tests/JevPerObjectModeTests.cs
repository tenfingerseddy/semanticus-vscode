// The shipped mode of the four batched families, and the proof that asking a table at a time changes nothing but the requests.
//
// m11/jev-batch built the per-table requests behind a switch per family (MeasuresFamily.Batched and its three siblings) and the
// comparison run of 30 September decided which points ship that way (docs/redesign/jev-batch-comparison.md). The family tests
// run in the batched mode (DecisionFixture sets it), so this class holds the other half: the SAME recorded answers, played once one
// object at a time and once a table at a time, give the SAME plan lines, and the requests are what each mode says they are.

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
    public sealed class JevPerObjectModeTests
    {
        private static async Task<DecisionFixture> ModelAsync(string file)
        {
            var f = new DecisionFixture();
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Note", "String", null, "human");
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            var revenue = await e.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human", "Money");
            await e.SetMeasureFormatAsync(revenue, "$#,##0.00", "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Returns", "Return Count", "COUNTROWS ( Returns )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static string Choice(string id, double confidence) =>
            "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(id) + ",\"confidence\":" + confidence.ToString(CultureInfo.InvariantCulture) + "}";

        /// <summary>One entry per object ref, answering every question any of the four points asks about it.</summary>
        private static string Answers()
        {
            var entries = new List<string>();
            void Add(string objectRef, params (string Question, string Choice, double Confidence)[] answers) =>
                entries.Add("{\"digest\":" + JsonSerializer.Serialize(objectRef) + ",\"usage\":{\"input_tokens\":500,\"output_tokens\":20},\"answers\":{"
                    + string.Join(",", answers.Select(a => JsonSerializer.Serialize(a.Question) + ":" + Choice(a.Choice, a.Confidence))) + "}}");
            var money = MeasureDefaultsPoint.FolderOptionId("Money");
            Add("measure:Sales/Total Sales", ("display_folder", money, 0.93), ("format_family", "currency", 0.95));
            Add("measure:Sales/Order Count", ("display_folder", "none", 0.9), ("format_family", "whole_number", 0.92));
            Add("measure:Returns/Return Count", ("display_folder", money, 0.6), ("format_family", "whole_number", 0.9));
            Add("column:Sales/Amount", ("format_family", "currency", 0.95), ("summarize_by", "sum", 0.95), ("ai_schema", "include", 0.9));
            Add("column:Sales/Quantity", ("format_family", "whole_number", 0.93), ("summarize_by", "sum", 0.9), ("ai_schema", "include", 0.9));
            Add("column:Sales/CustomerKey", ("summarize_by", "none", 0.95), ("ai_schema", "exclude", 0.95));
            Add("column:Customer/CustomerKey", ("summarize_by", "none", 0.9), ("ai_schema", "exclude", 0.9));
            Add("column:Returns/Qty", ("format_family", "whole_number", 0.4), ("summarize_by", "sum", 0.7), ("ai_schema", "include", 0.9));
            return "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[" + string.Join(",", entries) + "]}";
        }

        private sealed class Run
        {
            public string[] Lines;
            public Dictionary<string, int> Requests;
        }

        private static async Task<Run> PlayAsync(bool batched)
        {
            using var f = await ModelAsync(batched ? "PerTable.bim" : "PerObject.bim");
            f.Engine.JevBatchedForTest = _ => batched;
            f.Engine.AiSchemaJevDecidesForTest = true;
            await f.ConsentAsync();
            foreach (var other in new[] { DecisionRegistry.ColumnCategory, DecisionRegistry.RelationshipJoin, DecisionRegistry.DateTable, DecisionRegistry.MeasureStarter })
                await f.Engine.SetDecisionPointEnabledAsync(other, false, "human");
            var oracle = f.UseRecordedByObject(Answers());

            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();

            var lines = plan.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => (i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l =>
                    string.Join("|", i.Id, l.Op, l.Target, l.After, l.Kept, l.Provenance?.Point, l.Provenance?.ConfidenceWord)))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var requests = oracle.Seen.GroupBy(r => r.PointName).ToDictionary(g => g.Key, g => g.Count());
            return new Run { Lines = lines, Requests = requests };
        }

        [Fact]
        public async Task The_same_answers_give_the_same_plan_lines_asked_one_object_at_a_time_or_a_table_at_a_time()
        {
            var one = await PlayAsync(batched: false);
            var table = await PlayAsync(batched: true);

            Assert.True(one.Lines.Length >= 8, "not vacuous: " + one.Lines.Length + " Jev lines");
            Assert.Equal(one.Lines, table.Lines);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_object_at_a_time_sends_a_request_for_each_object_and_a_table_at_a_time_one_for_each_table()
        {
            var one = await PlayAsync(batched: false);
            var table = await PlayAsync(batched: true);

            // One request per object: 3 measures to tidy, 3 columns to format, 4 number columns to summarize (the fifth already has a base measure, so the Plan does not ask about it again), and every eligible object (3 tables,
            // 6 columns, 4 measures) for what Copilot sees. Batched, a request holds the whole model up to each point's per-request bound (JevTableBatch.*ObjectsPerRequest), so this small model needs one request for each point.
            Assert.Equal((3, 3, 4, 13), (one.Requests["measure.folder_and_format"], one.Requests["column.format"], one.Requests["column.summarize"], one.Requests["ai.schema"]));
            static int Requests(int objects, int perRequest) => (objects + perRequest - 1) / perRequest;
            Assert.Equal(
                (Requests(3, JevTableBatch.MeasureObjectsPerRequest), Requests(3, JevTableBatch.ColumnObjectsPerRequest), Requests(4, JevTableBatch.ColumnObjectsPerRequest), Requests(13, JevTableBatch.AiSchemaObjectsPerRequest)),
                (table.Requests["measure.folder_and_format"], table.Requests["column.format"], table.Requests["column.summarize"], table.Requests["ai.schema"]));
            Assert.Equal((1, 1, 1, 1), (table.Requests["measure.folder_and_format"], table.Requests["column.format"], table.Requests["column.summarize"], table.Requests["ai.schema"]));
        }
    }
}
