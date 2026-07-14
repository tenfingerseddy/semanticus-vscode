using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Next-release catalog completion batch 6. Pins the live-cardinality and DAX-performance evidence used to
    /// reconcile the broad performance/complexity rows; other mapped signals retain their dedicated rule tests.
    /// </summary>
    public sealed class ReadinessCatalogCompletionBatch6Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id) =>
            ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        private static RuleEvaluation LiveEval(SessionManager sessions, ReadinessLiveStats stats, string id) =>
            ReadinessRuleSet.LiveRules(stats).Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        [Fact]
        public async Task High_cardinality_rule_covers_violation_clean_and_dormant_live_populations()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("CatalogCompletionBatch6Cardinality", 1604);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Customer Label", "String", "Customer Label", "human");

            var absent = LiveEval(sessions, new ReadinessLiveStats(), "SCALE-HICARD-COLUMN");
            Assert.Equal(0, absent.Applicable);
            Assert.Empty(absent.Violations);

            var stats = new ReadinessLiveStats();
            stats.Set("Sales", "Customer Label", ReadinessLiveStats.HighCardinalityColumn);
            var clean = LiveEval(sessions, stats, "SCALE-HICARD-COLUMN");
            Assert.Equal(1, clean.Applicable);
            Assert.Empty(clean.Violations);

            stats.Set("Sales", "Customer Label", ReadinessLiveStats.HighCardinalityColumn + 1);
            var violation = LiveEval(sessions, stats, "SCALE-HICARD-COLUMN");
            Assert.Equal(1, violation.Applicable);
            Assert.Single(violation.Violations);
        }

        [Fact]
        public async Task Dax_performance_rule_fires_and_clears_without_creating_a_pass_population()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("CatalogCompletionBatch6Dax", 1604);
            var table = await engine.CreateTableAsync("Sales", "human");

            Assert.Equal(0, Eval(sessions, "BP-DAX-IFERROR").Applicable);
            var measure = await engine.CreateMeasureAsync(table, "Risky Ratio", "IFERROR ( DIVIDE ( 1, 0 ), 0 )", "human");
            var violation = Eval(sessions, "BP-DAX-IFERROR");
            Assert.Equal(1, violation.Applicable);
            Assert.Single(violation.Violations);

            await engine.SetDaxAsync(measure, "DIVIDE ( 1, 2 )", "human");
            var cleared = Eval(sessions, "BP-DAX-IFERROR");
            Assert.Equal(0, cleared.Applicable); // presence design: no artificial clean pass
            Assert.Empty(cleared.Violations);
        }
    }
}
