using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Mapping a model table to a named SQL source. Astra's journey B found the dead end this closes: the table
    /// row-count check could only ever use what a partition happened to reveal, and when that was not enough there
    /// was no action anywhere that repaired it.
    ///
    /// The honesty of the judge is untouched. Counts that differ without proven snapshot alignment stay
    /// NotVerifiable; a mapping supplies WHERE to count, never a verdict.
    /// </summary>
    [Collection("restore-root")]
    public sealed class TableSourceMappingTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _modelDir;

        public TableSourceMappingTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-tablemap-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _modelDir = Path.Combine(_root, "model");
            Directory.CreateDirectory(_modelDir);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static SqlSourceRecord SaveSource(string name = "Contoso warehouse")
            => SqlSourceRegistry.Save(null, name, "contoso-sql.database.windows.net", "Warehouse", "devicecode", "tenant-7");

        private async Task<LocalEngine> ModelWithSalesAsync(string partitionM)
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("Rows", 1604);
            await engine.CreateImportTableAsync("Sales", partitionM, "human");
            // A mapping needs an on-disk home, the same rule a saved check follows.
            await engine.SaveAsync(_modelDir, "TMDL");
            return engine;
        }

        private const string DetectedM =
            "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t";
        private const string PartialM =
            "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\") in Source";

        [Fact]
        public async Task A_mapping_wins_over_what_the_model_detects()
        {
            var source = SaveSource();
            using var engine = await ModelWithSalesAsync(DetectedM);

            var mapped = await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders");
            Assert.Equal("Sales", mapped.ModelTable);
            Assert.Equal("Contoso warehouse", mapped.SqlSourceName);
            Assert.Contains("mapped to sales.orders in 'Contoso warehouse'", mapped.Note);

            var run = await engine.RunTestSuiteAsync();
            var row = Assert.Single(run.Relationships.TableRowCounts);

            Assert.Equal("contoso-sql.database.windows.net", row.Server);
            Assert.Equal("Warehouse", row.Database);
            Assert.Equal("sales", row.Schema);
            Assert.Equal("orders", row.Entity);
            Assert.Equal(source.Id, row.SqlSourceId);
            Assert.Equal("Contoso warehouse", row.SqlSourceName);
        }

        [Fact]
        public async Task Clearing_the_mapping_returns_the_table_to_what_the_model_detects()
        {
            var source = SaveSource();
            using var engine = await ModelWithSalesAsync(DetectedM);
            await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders");

            Assert.True(await engine.ClearTableSourceMappingAsync("Sales"));
            Assert.False(await engine.ClearTableSourceMappingAsync("Sales"));

            var row = Assert.Single((await engine.RunTestSuiteAsync()).Relationships.TableRowCounts);
            Assert.Equal("fabric.example.com", row.Server);
            Assert.Equal("dbo", row.Schema);
            Assert.Equal("fact_sales", row.Entity);
            Assert.Null(row.SqlSourceId);
        }

        [Fact]
        public async Task A_table_with_neither_a_mapping_nor_a_readable_source_says_it_is_not_mapped()
        {
            using var engine = await ModelWithSalesAsync(PartialM);

            var row = Assert.Single((await engine.RunTestSuiteAsync()).Relationships.TableRowCounts);

            Assert.Equal(Verdict.NotVerifiable, row.Check.Verdict);
            Assert.Contains("not mapped to a SQL source yet", row.Check.Message);
            Assert.DoesNotContain("—", row.Check.Message);
        }

        [Fact]
        public async Task A_mapping_to_a_source_that_has_been_removed_says_so_rather_than_counting_elsewhere()
        {
            var source = SaveSource();
            using var engine = await ModelWithSalesAsync(DetectedM);
            await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders");
            Assert.True(SqlSourceRegistry.Delete(source.Id));

            var row = Assert.Single((await engine.RunTestSuiteAsync()).Relationships.TableRowCounts);

            Assert.Equal(Verdict.NotVerifiable, row.Check.Verdict);
            Assert.Contains("no longer saved", row.Check.Message);
            // It must NOT quietly fall back to the coordinates the model detects.
            Assert.NotEqual("fabric.example.com", row.Server);
        }

        [Fact]
        public async Task A_mapping_without_a_schema_and_table_asks_for_them_instead_of_guessing()
        {
            var source = SaveSource();
            using var engine = await ModelWithSalesAsync(PartialM);

            var mapped = await engine.SetTableSourceMappingAsync("Sales", source.Id, null, null);
            Assert.Contains("Add the schema and the table name", mapped.Note);

            var row = Assert.Single((await engine.RunTestSuiteAsync()).Relationships.TableRowCounts);
            Assert.Equal(Verdict.NotVerifiable, row.Check.Verdict);
            Assert.Contains("does not say which schema and table", row.Check.Message);
        }

        [Fact]
        public async Task Mapping_a_table_that_is_not_in_the_model_is_refused_by_name()
        {
            var source = SaveSource();
            using var engine = await ModelWithSalesAsync(DetectedM);

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => engine.SetTableSourceMappingAsync("NoSuchTable", source.Id, "dbo", "t"));
            Assert.Contains("is not a table in this model", ex.Message);
        }

        [Fact]
        public async Task Mapping_to_a_source_that_was_never_saved_is_refused_by_name()
        {
            using var engine = await ModelWithSalesAsync(DetectedM);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SetTableSourceMappingAsync("Sales", "sql-gone", "dbo", "t"));
            Assert.Contains("Save the source in Connections first", ex.Message);
        }

        [Fact]
        public void A_mapping_supplies_where_to_count_never_a_verdict()
        {
            // The one thing a mapping must not change: counts that differ without proven snapshot alignment.
            var result = TableRowCountReconciliation.Evaluate(new TableRowCountInput
            {
                ModelTable = "Sales", Server = "contoso-sql.database.windows.net", Database = "Warehouse",
                Schema = "sales", Entity = "orders", SqlSourceId = "sql-1", SqlSourceName = "Contoso warehouse",
                ModelCount = 88410, SourceCount = 88405, SnapshotAligned = false,
            });

            Assert.Equal(Verdict.NotVerifiable, result.Check.Verdict);
            Assert.Contains("Matching snapshots were not confirmed", result.Check.Message);
            Assert.Equal("Contoso warehouse", result.SqlSourceName);
        }
    }
}
