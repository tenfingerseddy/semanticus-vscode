using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    // Join 11 solui, fix round 2, U3: a saved SQL check runs the measure it is bound to, found by identity. The run no longer turns the measure back into a ref string, because
    // ObjectRefs.Resolve splits at the FIRST '/', so a table 'Sales/EU' and a measure 'EU/Revenue' on a table 'Sales' read alike. VerifySolUiMeasureRefTests holds the tagged cases;
    // these hold what it does not: a model without lineage tags (the identity index binds the check), the binding itself, and a tag that no longer resolves.
    public sealed class SolUiRound2BindingTests
    {
        private static ResultSet Value(decimal value) => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { value } }, RowCount = 1 };
        private static ResultSet Member(decimal value) => new ResultSet { Columns = new[] { new ColumnDef { Name = "Region" }, new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { "West", value } }, RowCount = 1 };

        private static async Task<(LocalEngine Engine, SessionManager Sessions, string Root)> Open()
        {
            var root = Path.Combine(Path.GetTempPath(), "r2-bind-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, root);
            await engine.CreateModelAsync("Bindings", 1604);
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

        [Fact]
        public async Task A_check_on_a_model_without_lineage_tags_runs_its_own_measure_after_a_rename_to_a_slash_name()
        {
            var o = await Open();
            try
            {
                var sales = await o.Engine.CreateTableAsync("Sales", "human");
                var revenue = await o.Engine.CreateMeasureAsync(sales, "Revenue", "4", "human");
                await o.Sessions.Current.MutateAsync("human", "fixture tagless measure", m => ((Measure)ObjectRefs.Resolve(m, revenue)).LineageTag = null);
                var stored = await SaveCheck(o.Engine, revenue, 2m);
                Assert.Null(stored.TargetTag);
                Assert.StartsWith("sid:", stored.TargetIdentity);
                await o.Engine.RenameObjectAsync(sales, "Sales/EU", "human");
                var other = await o.Engine.CreateTableAsync("Sales", "human");   // a table 'Sales' with a measure 'EU/Revenue': its ref reads like the bound measure's
                await o.Engine.CreateMeasureAsync(other, "EU/Revenue", "2", "human");

                var queries = new List<string>();
                Live(o.Engine, queries, new Dictionary<string, decimal> { ["[EU/Revenue]"] = 2m, ["[Revenue]"] = 4m }, 2m);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.True(outcome.Verdict == Verdict.Fail, "the bound measure returns 4 and the source 2, so it must fail; the engine reported " + outcome.Verdict + " (" + outcome.Message + ") using DAX: " + outcome.Dax);
                Assert.DoesNotContain(queries, q => q.Contains("[EU/Revenue]", StringComparison.Ordinal));
            }
            finally { Close(o); }
        }

        [Fact]
        public async Task A_tag_that_no_longer_resolves_is_reported_and_never_falls_back_to_a_measure_with_the_same_name()
        {
            var o = await Open();
            try
            {
                var sales = await o.Engine.CreateTableAsync("Sales", "human");
                var revenue = await o.Engine.CreateMeasureAsync(sales, "Revenue", "4", "human");
                var stored = await SaveCheck(o.Engine, revenue, 4m);
                Assert.False(string.IsNullOrEmpty(stored.TargetTag));
                await o.Engine.DeleteObjectAsync(revenue, "human");
                await o.Engine.CreateMeasureAsync(sales, "Revenue", "4", "human");   // the same name, a new identity

                var queries = new List<string>();
                Live(o.Engine, queries, new Dictionary<string, decimal> { ["[Revenue]"] = 4m }, 4m);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
                Assert.True(outcome.Missing);
                Assert.Empty(queries.Where(q => q.Contains("[Revenue]", StringComparison.Ordinal)));
            }
            finally { Close(o); }
        }

        [Fact]
        public async Task A_binding_finds_the_measure_by_its_tag_or_by_table_and_measure_name_and_never_by_a_split_ref()
        {
            var o = await Open();
            try
            {
                var a = await o.Engine.CreateTableAsync("Sales/EU", "human");
                var aRev = await o.Engine.CreateMeasureAsync(a, "Revenue", "4", "human");
                var b = await o.Engine.CreateTableAsync("Sales", "human");
                var bRev = await o.Engine.CreateMeasureAsync(b, "EU/Revenue", "2", "human");
                // The refs of the two measures read alike (measure:Sales/EU/Revenue), so the tags are read from the tables, never through a ref.
                var (tagA, tagB) = await o.Sessions.Current.ReadAsync(m => (m.Tables.First(t => t.Name == "Sales/EU").Measures.Single().LineageTag, m.Tables.First(t => t.Name == "Sales").Measures.Single().LineageTag));
                Assert.NotEqual(tagA, tagB);
                await o.Sessions.Current.ReadAsync(m =>
                {
                    var byTag = LocalEngine.ReconcileBinding.ForTag(tagA).Find(m);
                    Assert.Equal("Sales/EU", byTag.Table.Name); Assert.Equal("Revenue", byTag.Name);
                    Assert.Equal("EU/Revenue", LocalEngine.ReconcileBinding.ForTag(tagB).Find(m).Name);
                    Assert.Null(LocalEngine.ReconcileBinding.ForTag("no-such-tag").Find(m));
                    var byNames = LocalEngine.ReconcileBinding.ForNames("Sales/EU", "Revenue").Find(m);
                    Assert.Equal("Revenue", byNames.Name); Assert.Equal("Sales/EU", byNames.Table.Name);
                    Assert.Equal("Sales", LocalEngine.ReconcileBinding.ForNames("Sales", "EU/Revenue").Find(m).Table.Name);
                    Assert.Null(LocalEngine.ReconcileBinding.ForNames("Sales", "Revenue").Find(m));
                    return 0;
                });
            }
            finally { Close(o); }
        }
    }
}
