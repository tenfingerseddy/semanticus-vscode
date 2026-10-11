using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, round 2 (lost work, D3): a deleted field parameter, or any calculated table, comes back whole on
    /// Undo and goes away again on Redo, and the model reads at every step. TE2 recorded the deletion of a column's
    /// group-by list as a "clear" step that undoes by re-creating the grouped column, so Undo threw and left the model
    /// unreadable (a field parameter groups its display column with its Fields column).
    /// </summary>
    public sealed class UatUndoGroupByTests
    {
        private static async Task<(LocalEngine engine, SessionManager sessions)> OpenAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("GroupBy", 1604);
            await engine.CreateImportTableAsync("Sales", "let Source = 1 in Source", "human");
            await engine.CreateColumnAsync("table:Sales", "Region", "String", "Region", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            await engine.CreateFieldParameterAsync("Pick", new[]
            {
                new FieldParameterItem { ObjectRef = "measure:Sales/Total" }, new FieldParameterItem { ObjectRef = "column:Sales/Region" },
            }, "human");
            return (engine, sessions);
        }

        private static async Task<string> TableAsync(SessionManager sessions, string name)
        {
            var json = await sessions.Current.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase,
                new TOM.SerializeOptions { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true }));
            var table = JsonNode.Parse(json)!["model"]!["tables"]!.AsArray().FirstOrDefault(t => (string)t!["name"]! == name);
            return table == null ? null : Normal(table).ToJsonString();
        }

        private static JsonNode Normal(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var sorted = new JsonObject();
                    foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal).ToList()) sorted[kv.Key] = Normal(kv.Value);
                    return sorted;
                case JsonArray a:
                    return new JsonArray(a.Select(Normal).OrderBy(i => (i as JsonObject)?["name"]?.ToString() ?? i?.ToJsonString(), StringComparer.Ordinal).ToArray());
                default:
                    return n == null ? null : JsonNode.Parse(n.ToJsonString());
            }
        }

        private static async Task<int> GroupedColumnsAsync(SessionManager sessions, string table) =>
            await sessions.Current.ReadAsync(m => m.Tables[table].Columns.Sum(c => c.GroupByColumns?.Count ?? 0));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_deleted_field_parameter_comes_back_whole_goes_again_on_redo_and_comes_back_again()
        {
            var (engine, sessions) = await OpenAsync();
            using (engine)
            {
                var before = await TableAsync(sessions, "Pick");
                Assert.NotNull(before);
                Assert.True(await GroupedColumnsAsync(sessions, "Pick") > 0, "the fixture's field parameter groups its columns");
                await engine.DeleteObjectAsync("table:Pick", "human");
                Assert.Null(await TableAsync(sessions, "Pick"));
                for (var round = 0; round < 2; round++)
                {
                    await engine.UndoAsync("human");
                    await engine.GetModelPlanAsync();                       // the model still reads
                    await engine.ListRolesAsync();
                    Assert.Equal(before, await TableAsync(sessions, "Pick"));
                    await engine.RedoAsync("human");
                    Assert.Null(await TableAsync(sessions, "Pick"));
                }
                await engine.UndoAsync("human");
                Assert.Equal(before, await TableAsync(sessions, "Pick"));
            }
        }

        [Fact]
        public async Task A_deleted_calculated_table_comes_back_whole_and_the_model_still_reads()
        {
            var (engine, sessions) = await OpenAsync();
            using (engine)
            {
                await engine.CreateCalculatedTableAsync("Calc", "ROW(\"x\", 1)", "human");
                var before = await TableAsync(sessions, "Calc");
                Assert.NotNull(before);
                await engine.DeleteObjectAsync("table:Calc", "agent");
                await engine.UndoAsync("agent");
                await engine.GetModelPlanAsync();
                Assert.Equal(before, await TableAsync(sessions, "Calc"));
                await engine.RedoAsync("agent");
                Assert.Null(await TableAsync(sessions, "Calc"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_deleted_column_that_groups_another_comes_back_with_its_group_and_the_other_column_stays_the_one_in_the_table()
        {
            var (engine, sessions) = await OpenAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Sales", "Code", "String", "Code", "human");
                await sessions.Current.MutateAsync("human", "group by", m =>
                    m.Tables["Sales"].Columns["Region"].GroupByColumns.Add(m.Tables["Sales"].Columns["Code"]));
                var before = await TableAsync(sessions, "Sales");
                Assert.Contains("groupByColumns", before);

                await engine.DeleteObjectAsync("column:Sales/Region", "human");
                Assert.DoesNotContain("\"Region\"", await TableAsync(sessions, "Sales"));
                for (var round = 0; round < 2; round++)
                {
                    await engine.UndoAsync("human");
                    await engine.GetModelPlanAsync();
                    Assert.Equal(before, await TableAsync(sessions, "Sales"));
                    Assert.Equal(1, await sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["Region"].GroupByColumns.Count));
                    Assert.Same(
                        await sessions.Current.ReadAsync(m => (object)m.Tables["Sales"].Columns["Code"]),
                        await sessions.Current.ReadAsync(m => (object)m.Tables["Sales"].Columns["Region"].GroupByColumns.Single()));
                    await engine.RedoAsync("human");
                    Assert.DoesNotContain("\"Region\"", await TableAsync(sessions, "Sales"));
                }
            }
        }
    }
}
