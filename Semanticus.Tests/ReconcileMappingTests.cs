using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ReconcileMappingTests
    {
        private const string SalesM = "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t";

        [Fact]
        public async Task Review_detects_the_measures_source_table_and_keeps_overrides_explicit()
        {
            var engine = new LocalEngine(new SessionManager());
            try
            {
                await engine.CreateModelAsync("Mapping", 1604);
                var table = await engine.CreateImportTableAsync("Sales", SalesM, "human");
                await engine.CreateColumnAsync(table, "Amount", "Decimal", null, "human");
                var measure = await engine.CreateMeasureAsync(table, "Total Sales", "SUM('Sales'[Amount])", "human");

                var detected = await engine.ReviewReconcileMappingAsync(new ReconcileMappingRequest { MeasureRef = measure }, "human");
                Assert.Null(detected.Error);
                Assert.False(detected.Ambiguous);
                Assert.Equal("fabric.example.com", detected.DetectedServer);
                Assert.Equal("LH_Gold", detected.DetectedDatabase);
            var source = Assert.Single(detected.Sources, x => x.Relevant);
                Assert.Equal("dbo", source.Schema);
                Assert.Equal("fact_sales", source.Entity);

                var overridden = await engine.ReviewReconcileMappingAsync(new ReconcileMappingRequest
                {
                    MeasureRef = measure,
                    Server = "override.example.com",
                    Database = "Warehouse",
                }, "human");
                Assert.Equal("fabric.example.com", overridden.DetectedServer);
                Assert.Equal("override.example.com", overridden.EffectiveServer);
                Assert.Equal("Warehouse", overridden.EffectiveDatabase);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Review_refuses_to_guess_when_a_measure_reaches_two_sql_sources()
        {
            var engine = new LocalEngine(new SessionManager());
            try
            {
                await engine.CreateModelAsync("AmbiguousMapping", 1604);
                var sales = await engine.CreateImportTableAsync("Sales", SalesM, "human");
                var returns = await engine.CreateImportTableAsync("Returns",
                    "let Source = Sql.Database(\"other.example.com\", \"ReturnsLake\"), t = Source{[Schema=\"ops\",Item=\"fact_returns\"]}[Data] in t", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", null, "human");
                await engine.CreateColumnAsync(returns, "Amount", "Decimal", null, "human");
                await engine.CreateMeasureAsync(sales, "Sales Total", "SUM('Sales'[Amount])", "human");
                await engine.CreateMeasureAsync(returns, "Returns Total", "SUM('Returns'[Amount])", "human");
                var combined = await engine.CreateMeasureAsync(sales, "Net", "[Sales Total] - [Returns Total]", "human");

                var review = await engine.ReviewReconcileMappingAsync(new ReconcileMappingRequest { MeasureRef = combined }, "human");
                Assert.True(review.Ambiguous);
                Assert.Null(review.DetectedServer);
                Assert.Null(review.DetectedDatabase);
                Assert.Contains("more than one SQL source", review.Note);
                Assert.Contains("no endpoint was guessed", review.Note);
                Assert.Equal(2, review.Sources.Count(x => x.Relevant));
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Connection_test_without_coordinates_teaches_the_missing_input_without_network_io()
        {
            var engine = new LocalEngine(new SessionManager());
            try
            {
                await engine.CreateModelAsync("NoSqlMapping", 1604);
                var table = await engine.CreateImportTableAsync("Inline", "let Source = #table({\"A\"},{{1}}) in Source", "human");
                var measure = await engine.CreateMeasureAsync(table, "Rows", "COUNTROWS('Inline')", "human");

                var review = await engine.ReviewReconcileMappingAsync(new ReconcileMappingRequest
                    { MeasureRef = measure, TestConnection = true }, "human");
                Assert.True(review.Tested);
                Assert.False(review.Connected);
                Assert.Contains("Enter a SQL endpoint and database", review.TestError);
                Assert.Equal("supply server + database, then test again", review.SuggestedNextAction);
            }
            finally { engine.Dispose(); }
        }
    }
}
