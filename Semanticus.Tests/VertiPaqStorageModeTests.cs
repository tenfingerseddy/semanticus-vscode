using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VertiPaqStorageModeTests
    {
        [Fact]
        public async Task Query_only_attach_reports_unknown_instead_of_import()
        {
            using var engine = new LocalEngine(new SessionManager());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Published", ExecuteDmv));

            var report = await engine.VertiPaqScanAsync(25);

            Assert.Equal("powerbi://example/workspace|Published", report.QueryIdentity);
            Assert.Equal(VpaqStorageMode.Unknown, report.StorageMode);
            Assert.Null(report.Tables[0].Rows);
            Assert.Contains("Storage mode could not be confirmed", report.Caveat);
        }

        [Fact]
        public void Unknown_mode_never_falls_through_to_import_totals()
        {
            var report = VertiPaq.Compute(Segments(), Columns(), Tables(), 25);

            Assert.Equal(VpaqStorageMode.Unknown, report.StorageMode);
            Assert.Null(report.Tables[0].Rows);
            Assert.Contains("Storage mode could not be confirmed", report.Caveat);
            Assert.DoesNotContain("Microsoft", report.Caveat);
            Assert.DoesNotContain("Power BI", report.Caveat);
            Assert.DoesNotContain("\u2014", report.Caveat);
        }

        [Theory]
        [InlineData(VpaqStorageMode.Import, 42L)]
        [InlineData(VpaqStorageMode.DirectLake, null)]
        [InlineData("future-mode", null)]
        public void Row_counts_require_proven_import_mode(string requestedMode, long? expectedRows)
        {
            var report = VertiPaq.Compute(Segments(), Columns(), Tables(), 25, requestedMode);

            Assert.Equal(requestedMode == VpaqStorageMode.Import || requestedMode == VpaqStorageMode.DirectLake
                ? requestedMode : VpaqStorageMode.Unknown, report.StorageMode);
            Assert.Equal(expectedRows, report.Tables[0].Rows);
        }

        private static ResultSet Segments() => new ResultSet
        {
            Columns = new[]
            {
                new ColumnDef { Name = "DIMENSION_NAME" },
                new ColumnDef { Name = "COLUMN_ID" },
                new ColumnDef { Name = "USED_SIZE" },
            },
            Rows = new[] { new object[] { "Sales", "1", 100L } },
        };

        private static ResultSet Columns() => new ResultSet
        {
            Columns = new[]
            {
                new ColumnDef { Name = "DIMENSION_NAME" },
                new ColumnDef { Name = "ATTRIBUTE_NAME" },
                new ColumnDef { Name = "COLUMN_ID" },
                new ColumnDef { Name = "COLUMN_ENCODING" },
                new ColumnDef { Name = "DICTIONARY_SIZE" },
                new ColumnDef { Name = "STRING_INDEX_SIZE" },
            },
            Rows = new[] { new object[] { "Sales", "Amount", "1", "2", 20L, 5L } },
        };

        private static ResultSet Tables() => new ResultSet
        {
            Columns = new[]
            {
                new ColumnDef { Name = "DIMENSION_NAME" },
                new ColumnDef { Name = "ROWS_COUNT" },
            },
            Rows = new[] { new object[] { "Sales", 42L } },
        };

        private static ResultSet ExecuteDmv(string query)
        {
            if (query.IndexOf("COLUMN_SEGMENTS", StringComparison.OrdinalIgnoreCase) >= 0) return Segments();
            if (query.IndexOf("STORAGE_TABLE_COLUMNS", StringComparison.OrdinalIgnoreCase) >= 0) return Columns();
            if (query.IndexOf("STORAGE_TABLES", StringComparison.OrdinalIgnoreCase) >= 0) return Tables();
            return ResultSet.FromError("Unexpected query in storage-mode test.");
        }
    }
}
