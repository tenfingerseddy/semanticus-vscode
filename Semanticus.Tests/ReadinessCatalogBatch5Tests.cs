using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Next-release catalog completion batch 3. The catalog mappings are executable: each touched rule proves its
    /// violation, clean and dormant/advisory states without activating an inapplicable population.
    /// </summary>
    public sealed class ReadinessCatalogBatch5Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> BaseModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("CatalogBatch5", 1604);
            var sales = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(sales, "Label", "String", "Label", "human");
            return (engine, sessions);
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id) =>
            ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        private static RuleEvaluation LiveEval(SessionManager sessions, ReadinessLiveStats stats, string id) =>
            ReadinessRuleSet.LiveRules(stats).Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        [Fact]
        public async Task Disconnected_and_key_summarization_rules_cover_violation_clean_and_dormant_states()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "REL-DISCONNECTED").Applicable);
                Assert.Equal(0, Eval(sessions, "FMT-SUMMARIZE").Applicable);

                var salesKey = await engine.CreateColumnAsync("table:Sales", "Product Key", "Int64", "Product Key", "human");
                var product = await engine.CreateTableAsync("Product", "human");
                var productKey = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                var disconnected = Eval(sessions, "REL-DISCONNECTED");
                Assert.Equal(2, disconnected.Applicable);
                Assert.Equal(2, disconnected.Violations.Count);

                await engine.CreateRelationshipAsync(salesKey, productKey, null, true, "human");
                var connected = Eval(sessions, "REL-DISCONNECTED");
                Assert.Equal(2, connected.Applicable);
                Assert.Empty(connected.Violations);

                var aggregatingKeys = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(2, aggregatingKeys.Applicable);
                Assert.Equal(2, aggregatingKeys.Violations.Count);
                await engine.SetColumnMetadataAsync(salesKey, null, "None", null, null, "human");
                await engine.SetColumnMetadataAsync(productKey, null, "None", null, null, "human");
                var cleanKeys = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(2, cleanKeys.Applicable);
                Assert.Empty(cleanKeys.Violations);
            }
        }

        [Fact]
        public async Task Implicit_and_non_additive_numeric_rules_partition_the_visible_numeric_population()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "DAC-IMPLICIT-MEASURE").Applicable);
                Assert.Equal(0, Eval(sessions, "SUMMARIZE-DIMENSION").Applicable);

                var revenue = await engine.CreateColumnAsync("table:Sales", "Revenue", "Decimal", "Revenue", "human");
                var implicitMeasure = Eval(sessions, "DAC-IMPLICIT-MEASURE");
                Assert.Equal(1, implicitMeasure.Applicable);
                Assert.Single(implicitMeasure.Violations);
                await engine.SetColumnMetadataAsync(revenue, null, "None", null, null, "human");
                Assert.Empty(Eval(sessions, "DAC-IMPLICIT-MEASURE").Violations);

                var age = await engine.CreateColumnAsync("table:Sales", "Customer Age", "Int64", "Customer Age", "human");
                var nonAdditive = Eval(sessions, "SUMMARIZE-DIMENSION");
                Assert.Equal(1, nonAdditive.Applicable);
                Assert.Single(nonAdditive.Violations);
                Assert.DoesNotContain(Eval(sessions, "DAC-IMPLICIT-MEASURE").Violations, f => f.ObjectName == "Customer Age");
                await engine.SetColumnMetadataAsync(age, null, "None", null, null, "human");
                Assert.Empty(Eval(sessions, "SUMMARIZE-DIMENSION").Violations);

                Assert.True(ReadinessRuleSet.IsNonAdditiveDimensionName("Sort Order"));
                Assert.True(ReadinessRuleSet.IsNonAdditiveDimensionName("Latitude"));
                Assert.False(ReadinessRuleSet.IsNonAdditiveDimensionName("Order Quantity"));
                Assert.False(ReadinessRuleSet.IsNonAdditiveDimensionName("3 Year Return"));
            }
        }

        [Fact]
        public async Task DirectLake_mode_advisories_route_Qna_teaching_without_affecting_the_score()
        {
            var (import, importSessions) = await BaseModelAsync();
            using (import)
            {
                Assert.Equal(0, Eval(importSessions, "MODE-DIRECTLAKE-QNA").Applicable);
                Assert.Empty(Eval(importSessions, "MODE-DIRECTLAKE-QNA").Violations);
                Assert.Equal(0, Eval(importSessions, "CFG-PREP-MODE").Applicable);
                Assert.Empty(Eval(importSessions, "CFG-PREP-MODE").Violations);
            }

            var sessions = new SessionManager();
            using var directLake = new LocalEngine(sessions, new Fake());
            await directLake.CreateModelAsync("DirectLake", 1604);
            await directLake.CreateDirectLakeTableAsync("Lake Sales", "sales", null, null, "human");
            var qna = Eval(sessions, "MODE-DIRECTLAKE-QNA");
            var prep = Eval(sessions, "CFG-PREP-MODE");
            Assert.Equal(0, qna.Applicable);
            Assert.Single(qna.Violations);
            Assert.Contains("linguistic teaching", qna.Violations[0].Message);
            Assert.Equal(0, prep.Applicable);
            Assert.Single(prep.Violations);
        }

        [Fact]
        public async Task Qna_unique_value_ceiling_is_dormant_without_stats_and_exact_when_measured()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                var absent = LiveEval(sessions, new ReadinessLiveStats(), "SCALE-QNA-INDEX");
                Assert.Equal(0, absent.Applicable);
                Assert.Empty(absent.Violations);

                var measured = new ReadinessLiveStats();
                measured.Set("Sales", "Label", ReadinessLiveStats.QnaUniqueValueCeiling);
                var clean = LiveEval(sessions, measured, "SCALE-QNA-INDEX");
                Assert.Equal(1, clean.Applicable);
                Assert.Empty(clean.Violations);

                measured.Set("Sales", "Label", ReadinessLiveStats.QnaUniqueValueCeiling + 1);
                var over = LiveEval(sessions, measured, "SCALE-QNA-INDEX");
                Assert.Equal(1, over.Applicable);
                Assert.Single(over.Violations);
            }
        }
    }
}
