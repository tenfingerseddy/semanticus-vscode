using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // Verifier on m11/solui (U3). The lane hands a saved SQL check's tagged measure on as ObjectRefs.For(measure), a 'measure:Table/Name' string, and the runner reads it back
    // with ObjectRefs.Resolve, which splits at the FIRST '/'. A measure whose TABLE name holds a '/' then does not come back as itself: the check errors where the base ran it,
    // and when another table's measure matches the split, the check runs THAT measure and reports its verdict as the tagged one's (the same class as Sol's A5 finding 1).
    // The first two facts fail on the lane and pass on its base (1eae0287); the third is a guard for the name shapes the brief names, and passes on both.
    public sealed class VerifySolUiMeasureRefTests
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

        private static Task<TestDefinition> SaveCheck(LocalEngine engine, string measureRef, decimal source) => engine.SaveTestDefinitionAsync(new TestDefinition
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
                GroupBy = new[] { "column:Geo/Region" },
                Sql = $"SELECT 'West' AS Region, {source} AS v",
                SqlGrandTotal = $"SELECT {source} AS v",
                BlankPolicy = "zero",
            }),
        }, "human");

        /// <summary>The live model answers by the measure the DAX names: `values` maps a bracketed measure to its value; anything else is 1.</summary>
        private static void Live(LocalEngine engine, List<string> queries, Dictionary<string, decimal> values, decimal source)
        {
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Model", query =>
            {
                queries.Add(query);
                decimal v = 1m;
                foreach (var kv in values) if (query.Contains(kv.Key, StringComparison.Ordinal)) { v = kv.Value; break; }
                return query.Contains("SUMMARIZECOLUMNS", StringComparison.Ordinal) ? Member(v) : Value(v);
            }));
            engine.ReconcileSqlForTests = (server, database, sql) => sql.Contains("'West'", StringComparison.Ordinal) ? Member(source) : Value(source);
        }

        private static async Task<(LocalEngine Engine, SessionManager Sessions, string Root)> Open()
        {
            var root = Path.Combine(Path.GetTempPath(), "v-solui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, root);
            await engine.CreateModelAsync("Slash names", 1604);
            sessions.Current.SourcePath = Path.Combine(root, "model.bim");
            var geo = await engine.CreateTableAsync("Geo", "human");
            await engine.CreateColumnAsync(geo, "Region", "String", "Region", "human");
            return (engine, sessions, root);
        }

        private static void Close((LocalEngine Engine, SessionManager Sessions, string Root) o)
        {
            o.Engine.Dispose(); o.Sessions.Dispose();
            if (Directory.Exists(o.Root)) Directory.Delete(o.Root, true);
        }

        [Fact]
        public async Task A_tagged_check_still_runs_its_measure_after_its_table_is_renamed_to_a_name_with_a_slash()
        {
            var o = await Open();
            try
            {
                var sales = await o.Engine.CreateTableAsync("Sales", "human");
                var revenue = await o.Engine.CreateMeasureAsync(sales, "Revenue", "4", "human");
                var stored = await SaveCheck(o.Engine, revenue, 4m);
                Assert.False(string.IsNullOrEmpty(stored.TargetTag));
                await o.Engine.RenameObjectAsync(sales, "Sales/EU", "human");   // the binding is by tag: a rename must not lose it

                var queries = new List<string>();
                Live(o.Engine, queries, new Dictionary<string, decimal> { ["[Revenue]"] = 4m }, 4m);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.True(outcome.Verdict == Verdict.Pass,
                    "The tagged measure returns 4 and the source returns 4, so the check passes; the engine reported " + outcome.Verdict
                    + " (" + outcome.Message + ") using DAX: " + outcome.Dax);
            }
            finally { Close(o); }
        }

        [Fact]
        public async Task A_tagged_check_never_runs_another_tables_measure_whose_ref_reads_the_same()
        {
            var o = await Open();
            try
            {
                var sales = await o.Engine.CreateTableAsync("Sales", "human");
                var revenue = await o.Engine.CreateMeasureAsync(sales, "Revenue", "4", "human");
                var stored = await SaveCheck(o.Engine, revenue, 2m);
                Assert.False(string.IsNullOrEmpty(stored.TargetTag));
                await o.Engine.RenameObjectAsync(sales, "Sales/EU", "human");
                // A second table now named Sales holds a measure 'EU/Revenue': its ref, measure:Sales/EU/Revenue, reads exactly like the tagged measure's.
                var other = await o.Engine.CreateTableAsync("Sales", "human");
                await o.Engine.CreateMeasureAsync(other, "EU/Revenue", "2", "human");

                var queries = new List<string>();
                Live(o.Engine, queries, new Dictionary<string, decimal> { ["[EU/Revenue]"] = 2m, ["[Revenue]"] = 4m }, 2m);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.True(outcome.Verdict == Verdict.Fail,
                    "The tagged measure (Sales/EU[Revenue]) returns 4 and the source returns 2, so its check must fail; the engine reported " + outcome.Verdict
                    + " using DAX: " + outcome.Dax);
            }
            finally { Close(o); }
        }

        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("Rev]enue")]
        public Task A_tagged_check_runs_its_measure_for_the_name_shapes_the_brief_names_sample(string name) => A_tagged_check_runs_its_measure_for_the_name_shapes_the_brief_names(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Rev]enue")]          // a bracket: escaped as ]] in the DAX
        [InlineData("O'Brien Revenue")]   // a quote
        [InlineData("Amount")]            // a name equal to another table's column (Geo[Amount])
        [InlineData("measure:Revenue")]   // Sol's shape
        public async Task A_tagged_check_runs_its_measure_for_the_name_shapes_the_brief_names(string name)
        {
            var o = await Open();
            try
            {
                await o.Engine.CreateColumnAsync("table:Geo", "Amount", "Decimal", "Amount", "human");
                var sales = await o.Engine.CreateTableAsync("Sales", "human");
                await o.Engine.CreateMeasureAsync(sales, "Revenue", "2", "human");
                var target = await o.Engine.CreateMeasureAsync(sales, name, "4", "human");
                var stored = await SaveCheck(o.Engine, target, 2m);
                Assert.False(string.IsNullOrEmpty(stored.TargetTag));

                var queries = new List<string>();
                var bracketed = "[" + name.Replace("]", "]]") + "]";
                Live(o.Engine, queries, new Dictionary<string, decimal> { [bracketed] = 4m, ["[Revenue]"] = 2m }, 2m);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.True(outcome.Verdict == Verdict.Fail, $"{name}: 4 against 2 must fail; the engine reported {outcome.Verdict} using DAX: {outcome.Dax}");
                Assert.Contains(queries, q => q.Contains(bracketed, StringComparison.Ordinal));
            }
            finally { Close(o); }
        }
    }
}
