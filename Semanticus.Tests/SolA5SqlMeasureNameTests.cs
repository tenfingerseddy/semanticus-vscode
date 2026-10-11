using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolA5SqlMeasureNameTests
    {
        private static ResultSet Value(decimal value) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "v" } },
            Rows = new[] { new object[] { value } },
            RowCount = 1,
        };

        private static ResultSet Member(decimal value) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "Region" }, new ColumnDef { Name = "v" } },
            Rows = new[] { new object[] { "West", value } },
            RowCount = 1,
        };

        [Fact]
        public async Task Saved_sql_check_keeps_the_tagged_measure_when_its_name_starts_with_measure_colon()
        {
            var root = Path.Combine(Path.GetTempPath(), "sol-a5-sql-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro, root);
                await engine.CreateModelAsync("Name collision", 1604);
                sessions.Current.SourcePath = Path.Combine(root, "model.bim");
                var table = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(table, "Region", "String", "Region", "human");
                await engine.CreateMeasureAsync(table, "Revenue", "2", "human");
                var selected = await engine.CreateMeasureAsync(table, "measure:Revenue", "4", "human");
                Assert.Equal("measure:Sales/measure:Revenue", selected);
                var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile,
                    Title = "The selected measure against source",
                    TargetRef = selected,
                    Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = selected,
                        Server = "test.datawarehouse.fabric.microsoft.com",
                        Database = "Warehouse",
                        GroupBy = new[] { "column:Sales/Region" },
                        Sql = "SELECT 'West' AS Region, 2 AS v",
                        SqlGrandTotal = "SELECT 2 AS v",
                        BlankPolicy = "zero",
                    }),
                }, "human");
                Assert.False(string.IsNullOrEmpty(stored.TargetTag));
                var queries = new List<string>();
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Model", query =>
                {
                    queries.Add(query);
                    var value = query.Contains("[measure:Revenue]", StringComparison.Ordinal) ? 4m : 2m;
                    if (query.Contains("SUMMARIZECOLUMNS", StringComparison.Ordinal)) return Member(value);
                    if (query.Contains("[measure:Revenue]", StringComparison.Ordinal) || query.Contains("[Revenue]", StringComparison.Ordinal)) return Value(value);
                    return Value(1m);
                }));
                engine.ReconcileSqlForTests = (server, database, sql) => sql.Contains("'West'", StringComparison.Ordinal) ? Member(2m) : Value(2m);

                var run = await engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);

                Assert.True(outcome.Verdict == Verdict.Fail,
                    "The selected measure returns 4 and the source returns 2. "
                    + "Its check must fail, but the engine reported " + outcome.Verdict
                    + " using DAX: " + outcome.Dax);
                Assert.Contains(queries, q => q.Contains("[measure:Revenue]", StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
