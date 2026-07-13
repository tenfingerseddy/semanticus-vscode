using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ReadinessStarShapeTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<LocalEngine> ModelAsync(bool snowflake)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("Star shape", 1604);
            var sales = await engine.CreateTableAsync("Sales", "human");
            var product = await engine.CreateTableAsync("Product", "human");
            var category = await engine.CreateTableAsync("Category", "human");
            await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
            await engine.CreateColumnAsync(sales, "Category Key", "Int64", "Category Key", "human");
            await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
            await engine.CreateColumnAsync(product, "Category Key", "Int64", "Category Key", "human");
            await engine.CreateColumnAsync(category, "Category Key", "Int64", "Category Key", "human");
            await engine.CreateRelationshipAsync("column:Sales/Product Key", "column:Product/Product Key", null, true, "human");
            await engine.CreateRelationshipAsync(
                snowflake ? "column:Product/Category Key" : "column:Sales/Category Key",
                "column:Category/Category Key", null, true, "human");
            return engine;
        }

        [Fact]
        public async Task Snowflake_intermediate_fires_but_two_direct_dimensions_do_not()
        {
            using (var snowflake = await ModelAsync(true))
            {
                var finding = Assert.Single((await snowflake.AiReadinessScanAsync()).Findings
                    .Where(f => f.RuleId == "REL-SNOWFLAKE"));
                Assert.Equal("Product", finding.ObjectName);
                Assert.Contains("Sales", finding.Message);
                Assert.Contains("Category", finding.Message);
            }

            using var star = await ModelAsync(false);
            Assert.DoesNotContain((await star.AiReadinessScanAsync()).Findings, f => f.RuleId == "REL-SNOWFLAKE");
        }
    }
}
