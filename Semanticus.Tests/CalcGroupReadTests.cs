using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// list_calculation_groups — the focused read behind the Advanced-Modelling calc-group editor. Proves it
    /// returns each group's precedence and its ordered items with DAX + dynamic format-string, reflecting the
    /// existing write ops (create group/item, set precedence, set format).
    /// </summary>
    public sealed class CalcGroupReadTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        [Fact]
        public async Task Lists_groups_with_precedence_and_ordered_items()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(false));
            using (engine)
            {
                await engine.CreateModelAsync("CG", 1604);
                var gRef = await engine.CreateCalculationGroupAsync("Time Intelligence", "human");
                await engine.SetCalcGroupPrecedenceAsync(gRef, 10, "human");
                var ytd = await engine.CreateCalculationItemAsync(gRef, "YTD", "CALCULATE(SELECTEDMEASURE(), DATESYTD('Date'[Date]))", "human");
                await engine.CreateCalculationItemAsync(gRef, "PY", "CALCULATE(SELECTEDMEASURE(), SAMEPERIODLASTYEAR('Date'[Date]))", "human");
                await engine.SetCalcItemFormatStringAsync(ytd, "\"$\"#,##0", "human");

                var groups = await engine.ListCalculationGroupsAsync();
                var g = Assert.Single(groups);
                Assert.Equal("table:Time Intelligence", g.Ref);
                Assert.Equal(10, g.Precedence);
                Assert.Equal(2, g.Items.Length);

                var i0 = g.Items[0];
                Assert.Equal("YTD", i0.Name);
                Assert.Equal("calcitem:Time Intelligence/YTD", i0.Ref);
                Assert.Contains("DATESYTD", i0.Expression);
                Assert.Equal("\"$\"#,##0", i0.FormatStringExpression);

                Assert.Equal("PY", g.Items[1].Name);                      // ordinal order preserved
                Assert.True(string.IsNullOrEmpty(g.Items[1].FormatStringExpression));   // inherits base format
            }
        }

        [Fact]
        public async Task Empty_when_no_calc_groups()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(false));
            using (engine)
            {
                await engine.CreateModelAsync("Plain", 1604);
                Assert.Empty(await engine.ListCalculationGroupsAsync());
            }
        }
    }
}
