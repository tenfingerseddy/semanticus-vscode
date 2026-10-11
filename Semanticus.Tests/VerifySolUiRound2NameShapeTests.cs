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
    // Verifier on m11/solui round 2 (V2). A saved SQL check (and an expected-total check) runs the measure it is bound to, for every name shape: a '/' in the table or the
    // measure name, a name that starts with 'measure:', quotes and brackets. Each case saves the check on a plain measure, renames the table and the measure into the shape,
    // then adds an impostor whose ref reads like the bound one (or whose bracketed name is a prefix of it). The bound measure answers 4 and the impostor 2, and the source
    // says 2: a run of the right measure FAILS, a run of the impostor would show Pass. Tagged and tagless models (the identity index binds the check) both run.
    // A check whose measure is gone says so and never runs another measure with the same name.
    public sealed class VerifySolUiRound2NameShapeTests
    {
        private static ResultSet Value(decimal value) => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { value } }, RowCount = 1 };
        private static ResultSet Member(decimal value) => new ResultSet { Columns = new[] { new ColumnDef { Name = "Region" }, new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { "West", value } }, RowCount = 1 };
        private static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

        private static async Task<(LocalEngine Engine, SessionManager Sessions, string Root)> Open()
        {
            var root = Path.Combine(Path.GetTempPath(), "vr2-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(root);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, root);
            await engine.CreateModelAsync("Shapes", 1604);
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

        private static Task<TestDefinition> SaveReconcile(LocalEngine engine, string measureRef, decimal source) => engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureReconcile,
            Title = "Check",
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

        private static Task<TestDefinition> SaveValue(LocalEngine engine, string measureRef, string expected) => engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue,
            Title = "Total",
            TargetRef = measureRef,
            Enabled = true,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = measureRef, ExpectedValue = expected }),
        }, "human");

        /// <summary>The live model answers 4 for a query that names the bound measure, 2 for any other measure; the SQL source says 2.</summary>
        private static void Live(LocalEngine engine, List<string> queries, string boundName)
        {
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Model", query =>
            {
                queries.Add(query);
                var v = query.Contains(Bracket(boundName), StringComparison.Ordinal) ? 4m : 2m;
                return query.Contains("SUMMARIZECOLUMNS", StringComparison.Ordinal) ? Member(v) : Value(v);
            }));
            engine.ReconcileSqlForTests = (server, database, sql) => sql.Contains("'West'", StringComparison.Ordinal) ? Member(2m) : Value(2m);
        }

        /// <summary>A plain measure 'Start/Base' style setup: table T0 holding M0 = 4, optionally without lineage tags. Returns the measure's ref.</summary>
        private static async Task<string> Bound(LocalEngine engine, SessionManager sessions, bool tagless)
        {
            var t0 = await engine.CreateTableAsync("T0", "human");
            var m0 = await engine.CreateMeasureAsync(t0, "M0", "4", "human");
            if (tagless) await sessions.Current.MutateAsync("human", "fixture tagless measure", m => ((Measure)ObjectRefs.Resolve(m, m0)).LineageTag = null);
            return m0;
        }

        /// <summary>Rename T0/M0 into the shape (found by the table object, never by a ref), then add the impostor.</summary>
        private static async Task Shape(LocalEngine engine, SessionManager sessions, string table, string measure, string impTable, string impMeasure)
        {
            await engine.RenameObjectAsync("measure:T0/M0", measure, "human");
            await engine.RenameObjectAsync("table:T0", table, "human");
            var exists = await sessions.Current.ReadAsync(m => m.Tables.Contains(impTable));
            var imp = exists ? "table:" + impTable : await engine.CreateTableAsync(impTable, "human");
            await engine.CreateMeasureAsync(imp, impMeasure, "2", "human");
            var names = await sessions.Current.ReadAsync(m => m.Tables.SelectMany(t => t.Measures).Select(x => x.Table.Name + "|" + x.Name).ToArray());
            Assert.Contains(table + "|" + measure, names);
            Assert.Contains(impTable + "|" + impMeasure, names);
        }

        public static IEnumerable<object[]> Shapes()
        {
            var shapes = new[]
            {
                new[] { "Sales/EU", "Revenue", "Sales", "EU/Revenue" },
                new[] { "Sales", "EU/Revenue", "Sales/EU", "Revenue" },
                new[] { "A/B", "C/D", "A", "B/C/D" },
                new[] { "Sales", "measure:Revenue", "Sales", "Revenue" },
                new[] { "Sales", "Sales[Amount]", "Sales", "Amount" },
                new[] { "O'Brien", "Rev]enue", "O'Brien", "Rev" },
                new[] { "Sales", "'Revenue'", "Sales", "Revenue" },
                new[] { "[Sales]", "Total \"x\"", "Sales", "Total" },
            };
            foreach (var s in shapes)
                foreach (var tagless in new[] { false, true })
                    yield return new object[] { s[0], s[1], s[2], s[3], tagless };
        }

        // Lean suite (8 Oct): the default run keeps two of the 16 name shapes for each check (a slash in the table, then a quote and a
        // bracket with no tag); all 16 are the sweep.
        public static IEnumerable<object[]> ShapesSample() => Shapes().Where(r =>
            ((string)r[0] == "Sales/EU" && !(bool)r[4]) || ((string)r[0] == "O'Brien" && (bool)r[4]));

        [Theory]
        [MemberData(nameof(ShapesSample))]
        public Task A_saved_SQL_check_runs_its_own_measure_for_every_name_shape_sample(string table, string measure, string impTable, string impMeasure, bool tagless) =>
            A_saved_SQL_check_runs_its_own_measure_for_every_name_shape(table, measure, impTable, impMeasure, tagless);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(ShapesSample))]
        public Task A_saved_expected_total_check_asks_its_own_measure_for_every_name_shape_sample(string table, string measure, string impTable, string impMeasure, bool tagless) =>
            A_saved_expected_total_check_asks_its_own_measure_for_every_name_shape(table, measure, impTable, impMeasure, tagless);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task A_saved_SQL_check_runs_its_own_measure_for_every_name_shape(string table, string measure, string impTable, string impMeasure, bool tagless)
        {
            var o = await Open();
            try
            {
                var m0 = await Bound(o.Engine, o.Sessions, tagless);
                var stored = await SaveReconcile(o.Engine, m0, 2m);
                Assert.True(tagless ? stored.TargetIdentity?.StartsWith("sid:") == true : !string.IsNullOrEmpty(stored.TargetTag), "the check is bound by identity");
                await Shape(o.Engine, o.Sessions, table, measure, impTable, impMeasure);

                var queries = new List<string>();
                Live(o.Engine, queries, measure);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.True(outcome.Verdict == Verdict.Fail,
                    $"the bound measure [{table}].[{measure}] returns 4 and the source 2, so the check must fail; it reported {outcome.Verdict} ({outcome.Message}) with DAX: {outcome.Dax}");
                Assert.Contains(queries, q => q.Contains(Bracket(measure), StringComparison.Ordinal));
            }
            finally { Close(o); }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task A_saved_expected_total_check_asks_its_own_measure_for_every_name_shape(string table, string measure, string impTable, string impMeasure, bool tagless)
        {
            var o = await Open();
            try
            {
                var m0 = await Bound(o.Engine, o.Sessions, tagless);
                var stored = await SaveValue(o.Engine, m0, "2");
                await Shape(o.Engine, o.Sessions, table, measure, impTable, impMeasure);

                var queries = new List<string>();
                Live(o.Engine, queries, measure);
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.True(outcome.Verdict == Verdict.Fail,
                    $"the bound measure [{table}].[{measure}] returns 4 and the expected total is 2, so the check must fail; it reported {outcome.Verdict} ({outcome.Message}) with DAX: {outcome.Dax}");
            }
            finally { Close(o); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_check_whose_measure_is_gone_says_so_and_never_runs_a_same_named_measure_in_another_table(bool tagless)
        {
            var o = await Open();
            try
            {
                var m0 = await Bound(o.Engine, o.Sessions, tagless);
                var stored = await SaveReconcile(o.Engine, m0, 2m);
                await o.Engine.DeleteObjectAsync(m0, "human");
                var other = await o.Engine.CreateTableAsync("Other", "human");
                await o.Engine.CreateMeasureAsync(other, "M0", "2", "human");   // the same name, another table: a name fallback would run it and show Pass

                var queries = new List<string>();
                Live(o.Engine, queries, "no-such-measure");
                var run = await o.Engine.RunTestSuiteAsync(false, "human");
                var outcome = Assert.Single(run.Reconciles, x => x.DefId == stored.Id);
                Assert.NotEqual(Verdict.Pass, outcome.Verdict);
                Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
                Assert.True(outcome.Missing, "the outcome says the measure is gone: " + outcome.Message);
                Assert.DoesNotContain(queries, q => q.Contains("[M0]", StringComparison.Ordinal));
            }
            finally { Close(o); }
        }
    }
}
