using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolUiSuiteMeasureNameTests
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

        private static async Task<TestDefinition> SaveCheck(LocalEngine engine, string measureRef)
            => await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile,
                Title = "Check " + measureRef,
                TargetRef = measureRef,
                Enabled = true,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measureRef,
                    Server = "test.datawarehouse.fabric.microsoft.com",
                    Database = "Warehouse",
                    GroupBy = new[] { "column:Sales/Region" },
                    Sql = "SELECT 'West' AS Region, 2 AS v",
                    SqlGrandTotal = "SELECT 2 AS v",
                    BlankPolicy = "zero",
                }),
            }, "human");

        // Join 11 solui, U3: the saved check now carries the resolved measure's full ref, so every by-name consumer of the plan (the cross-measure cascade, the time-intelligence
        // variants, the timing pass) must key on the resolved measure's NAME. Here the base measure is literally named 'measure:Revenue', and the measure that depends on it fails
        // too, so it is a cascade and not a second root: it must be demoted to Suspect, naming [measure:Revenue].
        [Fact]
        public async Task The_cascade_still_finds_a_dependency_whose_name_starts_with_measure_colon()
        {
            var root = Path.Combine(Path.GetTempPath(), "solui-suite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro, root);
                await engine.CreateModelAsync("Name cascade", 1604);
                sessions.Current.SourcePath = Path.Combine(root, "model.bim");
                var table = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(table, "Region", "String", "Region", "human");
                await engine.CreateMeasureAsync(table, "Revenue", "2", "human");
                var baseRef = await engine.CreateMeasureAsync(table, "measure:Revenue", "4", "human");
                var dependentRef = await engine.CreateMeasureAsync(table, "Dependent", "[measure:Revenue] * 2", "human");
                var baseCheck = await SaveCheck(engine, baseRef);
                var dependentCheck = await SaveCheck(engine, dependentRef);
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Model", query =>
                {
                    var value = query.Contains("[Dependent]", StringComparison.Ordinal) ? 8m
                        : query.Contains("[measure:Revenue]", StringComparison.Ordinal) ? 4m : 2m;
                    return query.Contains("SUMMARIZECOLUMNS", StringComparison.Ordinal) ? Member(value) : Value(value);
                }));
                engine.ReconcileSqlForTests = (server, database, sql) => sql.Contains("'West'", StringComparison.Ordinal) ? Member(2m) : Value(2m);

                var run = await engine.RunTestSuiteAsync(false, "human");

                Assert.Equal(Verdict.Fail, Assert.Single(run.Reconciles, x => x.DefId == baseCheck.Id).Verdict);
                var dependent = Assert.Single(run.Reconciles, x => x.DefId == dependentCheck.Id);
                Assert.True(dependent.Verdict == Verdict.Suspect, "The dependent failure is a cascade of [measure:Revenue], but it was reported as " + dependent.Verdict + ": " + dependent.Message);
                Assert.Contains("[measure:Revenue]", dependent.Message, StringComparison.Ordinal);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
