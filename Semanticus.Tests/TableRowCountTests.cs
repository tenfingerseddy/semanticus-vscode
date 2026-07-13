using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TableRowCountTests
    {
        [Fact]
        public void Exact_independent_observations_pass_and_keep_timestamps()
        {
            var result = TableRowCountReconciliation.Evaluate(new TableRowCountInput
            {
                ModelTable = "Sales", Server = "fabric.example.com", Database = "LH_Gold",
                Schema = "dbo", Entity = "fact_sales", ModelCount = 42, SourceCount = 42,
                ModelObservedUtc = "2026-07-11T01:00:00Z", SourceObservedUtc = "2026-07-11T01:00:02Z",
            });

            Assert.Equal(Verdict.Pass, result.Check.Verdict);
            Assert.Equal(42, result.ModelCount);
            Assert.Equal("2026-07-11T01:00:02Z", result.SourceObservedUtc);
        }

        [Fact]
        public void Mismatch_without_snapshot_alignment_is_not_verifiable()
        {
            var result = TableRowCountReconciliation.Evaluate(new TableRowCountInput
            {
                ModelTable = "Sales", ModelCount = 42, SourceCount = 41, SnapshotAligned = false,
            });

            Assert.Equal(Verdict.NotVerifiable, result.Check.Verdict);
            Assert.Contains("independent connections", result.Check.Message);
        }

        [Fact]
        public void Mismatch_with_proven_snapshot_alignment_fails_once()
        {
            var result = TableRowCountReconciliation.Evaluate(new TableRowCountInput
            {
                ModelTable = "Sales", ModelCount = 42, SourceCount = 39, SnapshotAligned = true,
            });

            Assert.Equal(Verdict.Fail, result.Check.Verdict);
            Assert.Equal(3, result.Check.Count);
        }

        [Fact]
        public async Task Ambient_suite_discovers_sql_physical_tables_offline_without_calling_them_passed()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.CreateModelAsync("Rows", 1604);
            await engine.CreateImportTableAsync("Sales",
                "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t", "human");

            var run = await engine.RunTestSuiteAsync();

            var table = Assert.Single(run.Relationships.TableRowCounts);
            Assert.Equal("Sales", table.ModelTable);
            Assert.Equal("dbo", table.Schema);
            Assert.Equal("fact_sales", table.Entity);
            Assert.Equal(Verdict.NotVerifiable, table.Check.Verdict);
            Assert.Null(table.ModelCount);
            Assert.Contains(run.Health.Categories, c => c.Category == "Integrity" && c.NotVerifiable == 1);
        }
    }
}
