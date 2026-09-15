using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Astra's point 5: a table row count has ONE home. "New check, Table row count" and "Map source" in the Table
    /// row counts list are the same action on the same record, so the page needs one list that shows every table,
    /// where its count is read from, and how the last count went. There is no saved-test kind for a table count and
    /// there must not be one: the mapping IS the check.
    ///
    /// Astra's point 4: a source is bound by stable id, so a rename keeps every reference, and a refused delete names
    /// what needs repair rather than only counting it.
    /// </summary>
    [Collection("restore-root")]
    public sealed class TableMappingListTests : IDisposable
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _modelDir;

        public TableMappingListTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-maplist-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _modelDir = Path.Combine(_root, "model");
            Directory.CreateDirectory(_modelDir);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private const string DetectedM =
            "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t";
        private const string PartialM =
            "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\") in Source";
        private const string PlainM =
            "let Source = #table({\"n\"}, {{1}}) in Source";

        private static SqlSourceRecord SaveSource(string name = "Contoso warehouse")
            => SqlSourceRegistry.Save(null, name, "contoso-sql.database.windows.net", "Warehouse", "devicecode", "tenant-7");

        private async Task<LocalEngine> ModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.CreateModelAsync("Rows", 1604);
            await engine.CreateImportTableAsync("Sales", DetectedM, "human");
            await engine.CreateImportTableAsync("Returns", PartialM, "human");
            await engine.CreateImportTableAsync("Lookup", PlainM, "human");
            await engine.CreateMeasureAsync("Sales", "Total Rows", "COUNTROWS('Sales')", "human");
            await engine.SaveAsync(_modelDir, "TMDL");
            return engine;
        }

        [Fact]
        public async Task Every_table_appears_once_with_where_its_count_comes_from()
        {
            using var engine = await ModelAsync();

            var list = await engine.ListTableSourceMappingsAsync();
            var rows = list.Rows;

            Assert.Equal(new[] { "Lookup", "Returns", "Sales" }, rows.Select(r => r.ModelTable).ToArray());

            var sales = rows.Single(r => r.ModelTable == "Sales");
            Assert.Equal("detected", sales.Source);
            Assert.Equal("fabric.example.com", sales.Server);
            Assert.Equal("dbo", sales.Schema);
            Assert.Equal("fact_sales", sales.Entity);
            Assert.Null(sales.Reason);
            Assert.True(sales.CanCount);

            var returns = rows.Single(r => r.ModelTable == "Returns");
            Assert.Equal("none", returns.Source);
            Assert.False(returns.CanCount);
            Assert.Contains("not mapped to a SQL source yet", returns.Reason);

            var lookup = rows.Single(r => r.ModelTable == "Lookup");
            Assert.Equal("none", lookup.Source);
            Assert.False(lookup.CanCount);
            Assert.Contains("not mapped to a SQL source yet", lookup.Reason);

            // Every row is editable through the one op, including the ones with no source at all.
            Assert.All(rows, r => Assert.True(r.Editable));
        }

        [Fact]
        public async Task Mapping_a_table_changes_its_row_in_the_same_list()
        {
            var source = SaveSource();
            using var engine = await ModelAsync();

            await engine.SetTableSourceMappingAsync("Returns", source.Id, "sales", "returns");

            var row = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Returns");
            Assert.Equal("mapping", row.Source);
            Assert.Equal(source.Id, row.SqlSourceId);
            Assert.Equal("Contoso warehouse", row.SqlSourceName);
            Assert.Equal("sales", row.Schema);
            Assert.Equal("returns", row.Entity);
            Assert.True(row.CanCount);
            Assert.Null(row.Reason);
        }

        [Fact]
        public async Task The_row_carries_the_latest_count_outcome_and_says_when_there_is_none()
        {
            using var engine = await ModelAsync();

            var before = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
            Assert.Null(before.LastVerdict);
            Assert.Null(before.LastRunUtc);

            await engine.RunTestSuiteAsync();

            var after = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
            // Offline, so the count could not be read. That is "couldn't check", never a pass and never a failure.
            Assert.Equal(Verdict.NotVerifiable.ToString(), after.LastVerdict);
            Assert.False(string.IsNullOrWhiteSpace(after.LastMessage));
            Assert.False(string.IsNullOrWhiteSpace(after.LastRunUtc));
        }

        [Fact]
        public async Task A_table_count_is_never_a_saved_test_kind()
        {
            using var engine = await ModelAsync();

            // There is one home for a table count and it is the mapping. Sending it as a saved test is refused
            // by name, so a UI that tried would learn immediately instead of storing a check that never runs.
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = "tableRowCount", Title = "Sales rows", TargetRef = "Sales",
            }, "human"));
            Assert.Contains("Unknown test kind", ex.Message);
        }

        [Fact]
        public async Task Renaming_a_source_keeps_every_reference_working()
        {
            var source = SaveSource();
            using var engine = await ModelAsync();

            await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders");
            await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "revenue vs source", TargetRef = "Total Rows",
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = "Total Rows", SqlSourceId = source.Id, Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");

            // The name changes; the id does not.
            var renamed = await engine.SaveSqlSourceAsync(source.Id, "Contoso warehouse (renamed)",
                source.Server, source.Database, source.AuthMode, source.TenantId);
            Assert.Equal(source.Id, renamed.Id);

            var row = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
            Assert.Equal(source.Id, row.SqlSourceId);
            Assert.Equal("Contoso warehouse (renamed)", row.SqlSourceName);
            Assert.True(row.CanCount);

            var use = Assert.Single((await engine.ListTestDefinitionsAsync()).SqlSourceUse);
            Assert.Equal(source.Id, use.SqlSourceId);
            Assert.Equal("Contoso warehouse (renamed)", use.SourceName);
            Assert.False(use.Missing);
        }

        [Fact]
        public async Task A_refused_delete_names_the_checks_and_tables_that_need_repair()
        {
            var source = SaveSource();
            using var engine = await ModelAsync();

            await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders");
            await engine.SetTableSourceMappingAsync("Returns", source.Id, "sales", "returns");
            await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "revenue vs source", TargetRef = "Total Rows",
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = "Total Rows", SqlSourceId = source.Id, Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");

            var refused = await engine.DeleteSqlSourceAsync(source.Id);

            Assert.False(refused.Deleted);
            Assert.Equal(new[] { "revenue vs source" }, refused.CheckTitles);
            Assert.Equal(new[] { "Returns", "Sales" }, refused.MappedTables);
            Assert.Contains("revenue vs source", refused.Note);
            Assert.Contains("Sales", refused.Note);
            Assert.Contains("Returns", refused.Note);
            Assert.DoesNotContain("—", refused.Note);
        }
    }
}
