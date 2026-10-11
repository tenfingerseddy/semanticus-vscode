using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolA6KpiBindingTests
    {
        [Fact]
        public async Task Kpi_rename_rewrites_its_helper_but_preserves_a_same_named_physical_column()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("KpiBindings", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("_Revenue Goal", "Goal", null, DataType.Decimal);
                sales.AddDataColumn("_Sales Revenue Goal", "OtherGoal", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("Revenue", "100").AddKPI().TargetExpression = "50";
                metrics.AddMeasure("Host", "[_Revenue Goal] + SUM ( Sales[_Revenue Goal] )");
            });

            await engine.RenameObjectAsync("measure:Metrics/Revenue", "Sales Revenue", "human");
            var actual = await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);
            Assert.Equal("[_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )", actual);
        }
    }
}
